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
        /// Generate mesh with block type data
        /// Returns interleaved: position(3), normal(3), blockType(1) = 7 floats per vertex
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

                            // Calculate edges for degenerate check
                            var edge1 = v1 - v0;
                            var edge2 = v2 - v0;

                            // Skip if edges are too short (degenerate triangle)
                            if (edge1.LengthSquared < 0.001f || edge2.LengthSquared < 0.001f)
                                continue;

                            // Calculate normal
                            var normal = Vector3.Cross(edge1, edge2);
                            float normalLenSq = normal.LengthSquared;

                            // Skip if triangle area is too small
                            if (normalLenSq < 0.0001f)
                                continue;

                            normal = Vector3.Normalize(normal);

                            // Use majority vote for block type
                            BlockType triBlockType = GetMostCommonBlockType(
                                vertBlockTypes[a],
                                vertBlockTypes[b],
                                vertBlockTypes[c]
                            );

                            // Add small offset to prevent z-fighting
                            float offset = 0.001f;
                            v0 += normal * offset;
                            v1 += normal * offset;
                            v2 += normal * offset;

                            AddVertexWithBlockType(verts, v0, normal, triBlockType);
                            AddVertexWithBlockType(verts, v1, normal, triBlockType);
                            AddVertexWithBlockType(verts, v2, normal, triBlockType);
                        }
                    }

            return verts.ToArray();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
private static BlockType ChooseBlockType(BlockType b1, BlockType b2, float v1, float v2, float iso)
{
    // First priority: choose the solid block (above iso threshold)
    bool b1Solid = v1 > iso;
    bool b2Solid = v2 > iso;
    
    // If only one is solid, use that one
    if (b1Solid && !b2Solid) return b1;
    if (b2Solid && !b1Solid) return b2;
    
    // If both solid or both air, choose based on which is closer to surface
    float dist1 = Math.Abs(v1 - iso);
    float dist2 = Math.Abs(v2 - iso);
    
    BlockType chosen = dist1 < dist2 ? b1 : b2;
    
    // Never return Air for surface vertices - fallback to Stone
    if (chosen == BlockType.Air)
    {
        if (b1 != BlockType.Air) return b1;
        if (b2 != BlockType.Air) return b2;
        return BlockType.Stone;
    }
    
    return chosen;
}
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsSurfaceBlock(BlockType bt)
        {
            return bt == BlockType.Grass ||
                   bt == BlockType.Sand ||
                   bt == BlockType.Snow;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
private static BlockType GetMostCommonBlockType(BlockType a, BlockType b, BlockType c)
{
    // Remove Air from consideration - these are surface triangles
    if (a == BlockType.Air) a = BlockType.Stone;
    if (b == BlockType.Air) b = BlockType.Stone;
    if (c == BlockType.Air) c = BlockType.Stone;

    // Simple majority vote
    if (a == b) return a;  // At least 2 votes for a
    if (a == c) return a;  // At least 2 votes for a
    if (b == c) return b;  // At least 2 votes for b
    
    // All different - use priority system
    // Priority: Surface blocks > Subsurface > Stone
    
    // Check for surface blocks first
    if (a == BlockType.Grass || a == BlockType.Sand || a == BlockType.Snow) return a;
    if (b == BlockType.Grass || b == BlockType.Sand || b == BlockType.Snow) return b;
    if (c == BlockType.Grass || c == BlockType.Sand || c == BlockType.Snow) return c;
    
    // Check for subsurface blocks
    if (a == BlockType.Dirt || a == BlockType.Gravel) return a;
    if (b == BlockType.Dirt || b == BlockType.Gravel) return b;
    if (c == BlockType.Dirt || c == BlockType.Gravel) return c;
    
    // Default to first non-air block
    return a;
}

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddVertexWithBlockType(List<float> verts, Vector3 pos, Vector3 n, BlockType blockType)
        {
            // Position
            verts.Add(pos.X);
            verts.Add(pos.Y);
            verts.Add(pos.Z);

            // Normal
            verts.Add(n.X);
            verts.Add(n.Y);
            verts.Add(n.Z);

            // BlockType stored as float
            verts.Add((float)blockType);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 Lerp(float iso, Vector3 p1, Vector3 p2, float v1, float v2)
        {
            const float epsilon = 1e-5f;
            if (Math.Abs(iso - v1) < epsilon) return p1;
            if (Math.Abs(iso - v2) < epsilon) return p2;
            if (Math.Abs(v1 - v2) < epsilon) return p1;

            float t = (iso - v1) / (v2 - v1);
            t = Math.Clamp(t, 0f, 1f);
            return p1 + t * (p2 - p1);
        }
    }
}
