using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenTK.Mathematics;

namespace Aetheris
{
    public static class MarchingCubes
    {
        [ThreadStatic]
        private static List<float>? threadLocalVerts;

        /// <summary>
        /// Generate mesh with texture coordinates
        /// Returns interleaved: position(3), normal(3), uv(2) = 8 floats per vertex
        /// </summary>
        public static float[] GenerateMesh(Chunk chunk, float isoLevel = 0.5f)
        {
            if (threadLocalVerts == null)
                threadLocalVerts = new List<float>(15000);
            else
                threadLocalVerts.Clear();

            var verts = threadLocalVerts!;
            int step = Math.Max(1, Config.STEP);

            Span<Vector3> pos = stackalloc Vector3[8];
            Span<float> val = stackalloc float[8];
            Span<BlockType> blockTypes = stackalloc BlockType[8];
            Span<Vector3> vertList = stackalloc Vector3[12];
            Span<BlockType> vertBlockTypes = stackalloc BlockType[12];

            int maxX = Chunk.SizeX;
            int maxY = Chunk.SizeY;
            int maxZ = Chunk.SizeZ;

            for (int x = 0; x < maxX; x += step)
                for (int y = 0; y < maxY; y += step)
                    for (int z = 0; z < maxZ; z += step)
                    {
                        int nextX = x + step;
                        int nextY = y + step;
                        int nextZ = z + step;

                        // 8 corners of the cube
                        pos[0] = new Vector3(x, y, z);
                        pos[1] = new Vector3(nextX, y, z);
                        pos[2] = new Vector3(nextX, y, nextZ);
                        pos[3] = new Vector3(x, y, nextZ);
                        pos[4] = new Vector3(x, nextY, z);
                        pos[5] = new Vector3(nextX, nextY, z);
                        pos[6] = new Vector3(nextX, nextY, nextZ);
                        pos[7] = new Vector3(x, nextY, nextZ);

                        // Sample density and block types at corners
                        for (int i = 0; i < 8; i++)
                        {
                            int worldX = chunk.PositionX + (int)pos[i].X;
                            int worldY = chunk.PositionY + (int)pos[i].Y;
                            int worldZ = chunk.PositionZ + (int)pos[i].Z;
                            val[i] = WorldGen.SampleDensity(worldX, worldY, worldZ);
                            blockTypes[i] = WorldGen.GetBlockType(worldX, worldY, worldZ, val[i]);
                        }

                        // Determine cube configuration
                        int cubeIndex = 0;
                        if (val[0] > isoLevel) cubeIndex |= 1;
                        if (val[1] > isoLevel) cubeIndex |= 2;
                        if (val[2] > isoLevel) cubeIndex |= 4;
                        if (val[3] > isoLevel) cubeIndex |= 8;
                        if (val[4] > isoLevel) cubeIndex |= 16;
                        if (val[5] > isoLevel) cubeIndex |= 32;
                        if (val[6] > isoLevel) cubeIndex |= 64;
                        if (val[7] > isoLevel) cubeIndex |= 128;

                        int edges = Tables.EdgeTable[cubeIndex];
                        if (edges == 0) continue;

                        // Interpolate edge vertices and block types
                        if ((edges & 1) != 0) { vertList[0] = Lerp(isoLevel, pos[0], pos[1], val[0], val[1]); vertBlockTypes[0] = ChooseBlockType(blockTypes[0], blockTypes[1], val[0], val[1], isoLevel); }
                        if ((edges & 2) != 0) { vertList[1] = Lerp(isoLevel, pos[1], pos[2], val[1], val[2]); vertBlockTypes[1] = ChooseBlockType(blockTypes[1], blockTypes[2], val[1], val[2], isoLevel); }
                        if ((edges & 4) != 0) { vertList[2] = Lerp(isoLevel, pos[2], pos[3], val[2], val[3]); vertBlockTypes[2] = ChooseBlockType(blockTypes[2], blockTypes[3], val[2], val[3], isoLevel); }
                        if ((edges & 8) != 0) { vertList[3] = Lerp(isoLevel, pos[3], pos[0], val[3], val[0]); vertBlockTypes[3] = ChooseBlockType(blockTypes[3], blockTypes[0], val[3], val[0], isoLevel); }
                        if ((edges & 16) != 0) { vertList[4] = Lerp(isoLevel, pos[4], pos[5], val[4], val[5]); vertBlockTypes[4] = ChooseBlockType(blockTypes[4], blockTypes[5], val[4], val[5], isoLevel); }
                        if ((edges & 32) != 0) { vertList[5] = Lerp(isoLevel, pos[5], pos[6], val[5], val[6]); vertBlockTypes[5] = ChooseBlockType(blockTypes[5], blockTypes[6], val[5], val[6], isoLevel); }
                        if ((edges & 64) != 0) { vertList[6] = Lerp(isoLevel, pos[6], pos[7], val[6], val[7]); vertBlockTypes[6] = ChooseBlockType(blockTypes[6], blockTypes[7], val[6], val[7], isoLevel); }
                        if ((edges & 128) != 0) { vertList[7] = Lerp(isoLevel, pos[7], pos[4], val[7], val[4]); vertBlockTypes[7] = ChooseBlockType(blockTypes[7], blockTypes[4], val[7], val[4], isoLevel); }
                        if ((edges & 256) != 0) { vertList[8] = Lerp(isoLevel, pos[0], pos[4], val[0], val[4]); vertBlockTypes[8] = ChooseBlockType(blockTypes[0], blockTypes[4], val[0], val[4], isoLevel); }
                        if ((edges & 512) != 0) { vertList[9] = Lerp(isoLevel, pos[1], pos[5], val[1], val[5]); vertBlockTypes[9] = ChooseBlockType(blockTypes[1], blockTypes[5], val[1], val[5], isoLevel); }
                        if ((edges & 1024) != 0) { vertList[10] = Lerp(isoLevel, pos[2], pos[6], val[2], val[6]); vertBlockTypes[10] = ChooseBlockType(blockTypes[2], blockTypes[6], val[2], val[6], isoLevel); }
                        if ((edges & 2048) != 0) { vertList[11] = Lerp(isoLevel, pos[3], pos[7], val[3], val[7]); vertBlockTypes[11] = ChooseBlockType(blockTypes[3], blockTypes[7], val[3], val[7], isoLevel); }

                        // Generate triangles
                        for (int i = 0; i < Tables.TriTable.GetLength(1); i += 3)
                        {
                            int a = Tables.TriTable[cubeIndex, i];
                            if (a == -1) break;

                            int b = Tables.TriTable[cubeIndex, i + 1];
                            int c = Tables.TriTable[cubeIndex, i + 2];

                            var v0 = vertList[a];
                            var v1 = vertList[b];
                            var v2 = vertList[c];

                            // CRITICAL: More aggressive degenerate triangle detection
                            var edge1 = v1 - v0;
                            var edge2 = v2 - v0;

                            // Skip if edges are too short
                            if (edge1.LengthSquared < 0.001f || edge2.LengthSquared < 0.001f)
                                continue;

                            // Calculate normal with better degenerate check
                            var normal = Vector3.Cross(edge1, edge2);
                            float normalLenSq = normal.LengthSquared;

                            // Skip degenerate triangles (area too small)
                            if (normalLenSq < 0.0001f)
                                continue;

                            normal = normal.Normalized();

                            // Use the most common block type for this triangle
                            BlockType triBlockType = GetMostCommonBlockType(
                                vertBlockTypes[a],
                                vertBlockTypes[b],
                                vertBlockTypes[c]
                            );

                            // Add small offset along normal to prevent z-fighting
                            float offset = 0.001f;
                            v0 += normal * offset;
                            v1 += normal * offset;
                            v2 += normal * offset;

                            // Generate triplanar UVs based on normal
                            AddVertexWithUV(verts, v0, normal, triBlockType);
                            AddVertexWithUV(verts, v1, normal, triBlockType);
                            AddVertexWithUV(verts, v2, normal, triBlockType);
                        }
                    }

            return verts.ToArray();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static BlockType ChooseBlockType(BlockType b1, BlockType b2, float v1, float v2, float iso)
        {
            float diff1 = Math.Abs(v1 - iso);
            float diff2 = Math.Abs(v2 - iso);
            return diff1 < diff2 ? b1 : b2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static BlockType GetMostCommonBlockType(BlockType a, BlockType b, BlockType c)
        {
            if (a == b || a == c) return a;
            if (b == c) return b;
            return a;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Fract(float x)
        {
            // Improved fract with better precision handling
            float f = x - MathF.Floor(x);
            // Clamp to avoid edge cases
            return Math.Clamp(f, 0f, 0.9999f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]

        private static void AddVertexWithUV(List<float> verts, Vector3 pos, Vector3 n, BlockType blockType)
        {
            // Position
            verts.Add(pos.X);
            verts.Add(pos.Y);
            verts.Add(pos.Z);

            // Normal
            verts.Add(n.X);
            verts.Add(n.Y);
            verts.Add(n.Z);

            // Triplanar UV mapping
            float uRaw, vRaw;
            float absX = MathF.Abs(n.X);
            float absY = MathF.Abs(n.Y);
            float absZ = MathF.Abs(n.Z);

            float scale = 0.25f;
            if (absY > absX && absY > absZ)
            {
                uRaw = pos.X * scale;
                vRaw = pos.Z * scale;
            }
            else if (absX > absZ)
            {
                uRaw = pos.Z * scale;
                vRaw = pos.Y * scale;
            }
            else
            {
                uRaw = pos.X * scale;
                vRaw = pos.Y * scale;
            }

            uRaw = Fract(uRaw);
            vRaw = Fract(vRaw);

            var (uMin, vMin, uMax, vMax) = GetAtlasUV(blockType);

            // DEBUG: Log for Sand blocks
            if (blockType == BlockType.Sand && verts.Count < 240) // Only log first few
            {

            }

            float uvEpsilon = 0.001f;
            float uRange = (uMax - uMin) * (1f - uvEpsilon * 2);
            float vRange = (vMax - vMin) * (1f - uvEpsilon * 2);

            float u = uMin + uvEpsilon + uRaw * uRange;
            float v = vMin + uvEpsilon + vRaw * vRange;

            u = Math.Clamp(u, uMin + uvEpsilon, uMax - uvEpsilon);
            v = Math.Clamp(v, vMin + uvEpsilon, vMax - uvEpsilon);

            // DEBUG: Log final UV
            if (blockType == BlockType.Sand && verts.Count < 240)
            {

            }

            verts.Add(u);
            verts.Add(v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 Lerp(float iso, Vector3 p1, Vector3 p2, float v1, float v2)
        {
            const float epsilon = 1e-5f;
            if (Math.Abs(iso - v1) < epsilon) return p1;
            if (Math.Abs(iso - v2) < epsilon) return p2;
            if (Math.Abs(v1 - v2) < epsilon) return p1;

            float t = (iso - v1) / (v2 - v1);
            t = Math.Clamp(t, 0f, 1f); // Ensure interpolation stays in bounds
            return p1 + t * (p2 - p1);
        }

        /// <summary>
        /// Return normalized atlas bounds (uMin, vMin, uMax, vMax) for the given block type.
        /// Matches the procedural atlas in Renderer.CreateProceduralAtlas:
        /// 0=Stone,1=Dirt,2=Grass,3=Sand,4=Snow,5=Gravel,6=Wood,7=Leaves
        /// Atlas assumed 256x256 with 4x4 tiles (64px each).
        /// </summary>
        private static (float uMin, float vMin, float uMax, float vMax) GetAtlasUV(BlockType type)
        {
            const int atlasSize = 256;
            const int tileSize = 64;
            const int tilesPerRow = atlasSize / tileSize; // =4
            const float halfTexel = 0.5f / atlasSize;

            // FIXED: Account for Air = 0 offset in enum
            int tileIndex = type switch
            {
                BlockType.Stone => 0,
                BlockType.Dirt => 1,
                BlockType.Grass => 2,
                BlockType.Sand => 3,
                BlockType.Snow => 4,
                BlockType.Gravel => 5,
                BlockType.Wood => 6,
                BlockType.Leaves => 7,
                _ => 0  // Air and unknown fallback to Stone
            };

            int tx = tileIndex % tilesPerRow;
            int ty = tileIndex / tilesPerRow;

            float uMin = (tx * tileSize + halfTexel) / (float)atlasSize;
            float vMin = (ty * tileSize + halfTexel) / (float)atlasSize;
            float uMax = ((tx + 1) * tileSize - halfTexel) / (float)atlasSize;
            float vMax = ((ty + 1) * tileSize - halfTexel) / (float)atlasSize;

            return (uMin, vMin, uMax, vMax);
        }
    }
}
