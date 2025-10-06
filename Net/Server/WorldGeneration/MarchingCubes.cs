using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using OpenTK.Mathematics;

namespace Aetheris
{
    public static class MarchingCubes
    {
        [ThreadStatic]
        private static List<float>? threadLocalVerts;

        [ThreadStatic]
        private static Vector3[]? posCache;

        [ThreadStatic]
        private static float[]? valCache;

        [ThreadStatic]
        private static BlockType[]? blockTypesCache;

        private class DensityWorkItem
        {
            public float[,,] DensityCache = null!;
            public BlockType[,,] BlockTypeCache = null!;
        }

        [ThreadStatic]
        private static DensityWorkItem? workItem;

        private const float EPSILON = 0.0001f;
        private const float MIN_TRIANGLE_AREA = 0.00001f;

        public static float[] GenerateMesh(Chunk chunk, ChunkCoord coord, ChunkManager chunkManager, float isoLevel = 0.5f)
        {
            if (threadLocalVerts == null)
                threadLocalVerts = new List<float>(30000);
            else
                threadLocalVerts.Clear();

            var verts = threadLocalVerts!;
            int step = ServerConfig.STEP;

            int sizeX = Chunk.SizeX + 1;
            int sizeY = Chunk.SizeY + 1;
            int sizeZ = Chunk.SizeZ + 1;

            bool potentiallyEmpty = chunk.PositionY > 100 || chunk.PositionY < -32;

            if (workItem == null)
            {
                workItem = new DensityWorkItem
                {
                    DensityCache = new float[sizeX, sizeY, sizeZ],
                    BlockTypeCache = new BlockType[sizeX, sizeY, sizeZ]
                };
                posCache = new Vector3[8];
                valCache = new float[8];
                blockTypesCache = new BlockType[8];
            }

            var densityCache = workItem.DensityCache;
            var blockTypeCache = workItem.BlockTypeCache;

            int solidCount = 0;
            int airCount = 0;

            // Parallel density sampling with consistent boundaries
            Parallel.For(0, sizeX, x =>
            {
                int worldX = chunk.PositionX + x;

                for (int z = 0; z < sizeZ; z++)
                {
                    int worldZ = chunk.PositionZ + z;

                    // IMPORTANT: lookup column data by world coordinates (not clamped local index).
                    // This guarantees chunk A and chunk B get the exact same ColumnData at shared world coords.
                    var columnData = WorldGen.GetColumnData(worldX, worldZ);

                    for (int y = 0; y < sizeY; y++)
                    {
                        int worldY = chunk.PositionY + y;

                        // CRITICAL: Ensure consistent density sampling using world coordinates
                        float density = WorldGen.SampleDensityFast(worldX, worldY, worldZ, columnData);
                        densityCache[x, y, z] = density;
                        blockTypeCache[x, y, z] = WorldGen.GetBlockType(worldX, worldY, worldZ, density, columnData);

                        if (potentiallyEmpty)
                        {
                            if (density > isoLevel)
                                System.Threading.Interlocked.Increment(ref solidCount);
                            else
                                System.Threading.Interlocked.Increment(ref airCount);
                        }
                    }
                }
            });

            if (potentiallyEmpty && (solidCount == 0 || airCount == 0))
            {
                return Array.Empty<float>();
            }

            var pos = posCache!;
            var val = valCache!;
            var blockTypes = blockTypesCache!;

            Span<Vector3> vertList = stackalloc Vector3[12];
            Span<BlockType> vertBlockTypes = stackalloc BlockType[12];

            int maxX = Chunk.SizeX / step * step;
            int maxY = Chunk.SizeY / step * step;
            int maxZ = Chunk.SizeZ / step * step;

            for (int x = 0; x < maxX; x += step)
            {
                int nextX = x + step;

                for (int z = 0; z < maxZ; z += step)
                {
                    int nextZ = z + step;

                    for (int y = 0; y < maxY; y += step)
                    {
                        int nextY = y + step;

                        // Load density values
                        val[0] = densityCache[x, y, z];
                        val[1] = densityCache[nextX, y, z];
                        val[2] = densityCache[nextX, y, nextZ];
                        val[3] = densityCache[x, y, nextZ];
                        val[4] = densityCache[x, nextY, z];
                        val[5] = densityCache[nextX, nextY, z];
                        val[6] = densityCache[nextX, nextY, nextZ];
                        val[7] = densityCache[x, nextY, nextZ];

                        // Calculate cube index with epsilon tolerance
                        int cubeIndex = 0;
                        int solidMask = 0;

                        // Use epsilon for boundary cases to ensure consistency
                        if (val[0] > isoLevel + EPSILON) { cubeIndex |= 1; solidMask |= 1; }
                        if (val[1] > isoLevel + EPSILON) { cubeIndex |= 2; solidMask |= 2; }
                        if (val[2] > isoLevel + EPSILON) { cubeIndex |= 4; solidMask |= 4; }
                        if (val[3] > isoLevel + EPSILON) { cubeIndex |= 8; solidMask |= 8; }
                        if (val[4] > isoLevel + EPSILON) { cubeIndex |= 16; solidMask |= 16; }
                        if (val[5] > isoLevel + EPSILON) { cubeIndex |= 32; solidMask |= 32; }
                        if (val[6] > isoLevel + EPSILON) { cubeIndex |= 64; solidMask |= 64; }
                        if (val[7] > isoLevel + EPSILON) { cubeIndex |= 128; solidMask |= 128; }

                        if (solidMask == 0 || solidMask == 255) continue;

                        int edges = Tables.EdgeTable[cubeIndex];
                        if (edges == 0) continue;

                        blockTypes[0] = blockTypeCache[x, y, z];
                        blockTypes[1] = blockTypeCache[nextX, y, z];
                        blockTypes[2] = blockTypeCache[nextX, y, nextZ];
                        blockTypes[3] = blockTypeCache[x, y, nextZ];
                        blockTypes[4] = blockTypeCache[x, nextY, z];
                        blockTypes[5] = blockTypeCache[nextX, nextY, z];
                        blockTypes[6] = blockTypeCache[nextX, nextY, nextZ];
                        blockTypes[7] = blockTypeCache[x, nextY, nextZ];

                        // Position cache
                        // Position cache - MUST use world coordinates
                        pos[0].X = chunk.PositionX + x;
                        pos[0].Y = chunk.PositionY + y;
                        pos[0].Z = chunk.PositionZ + z;

                        pos[1].X = chunk.PositionX + nextX;
                        pos[1].Y = chunk.PositionY + y;
                        pos[1].Z = chunk.PositionZ + z;

                        pos[2].X = chunk.PositionX + nextX;
                        pos[2].Y = chunk.PositionY + y;
                        pos[2].Z = chunk.PositionZ + nextZ;

                        pos[3].X = chunk.PositionX + x;
                        pos[3].Y = chunk.PositionY + y;
                        pos[3].Z = chunk.PositionZ + nextZ;

                        pos[4].X = chunk.PositionX + x;
                        pos[4].Y = chunk.PositionY + nextY;
                        pos[4].Z = chunk.PositionZ + z;

                        pos[5].X = chunk.PositionX + nextX;
                        pos[5].Y = chunk.PositionY + nextY;
                        pos[5].Z = chunk.PositionZ + z;

                        pos[6].X = chunk.PositionX + nextX;
                        pos[6].Y = chunk.PositionY + nextY;
                        pos[6].Z = chunk.PositionZ + nextZ;

                        pos[7].X = chunk.PositionX + x;
                        pos[7].Y = chunk.PositionY + nextY;
                        pos[7].Z = chunk.PositionZ + nextZ;

                        // Interpolate edge vertices with improved precision
                        if ((edges & 1) != 0) { vertList[0] = LerpImproved(isoLevel, pos[0], pos[1], val[0], val[1]); vertBlockTypes[0] = ChooseBlockType(blockTypes[0], blockTypes[1], val[0], val[1], isoLevel); }
                        if ((edges & 2) != 0) { vertList[1] = LerpImproved(isoLevel, pos[1], pos[2], val[1], val[2]); vertBlockTypes[1] = ChooseBlockType(blockTypes[1], blockTypes[2], val[1], val[2], isoLevel); }
                        if ((edges & 4) != 0) { vertList[2] = LerpImproved(isoLevel, pos[2], pos[3], val[2], val[3]); vertBlockTypes[2] = ChooseBlockType(blockTypes[2], blockTypes[3], val[2], val[3], isoLevel); }
                        if ((edges & 8) != 0) { vertList[3] = LerpImproved(isoLevel, pos[3], pos[0], val[3], val[0]); vertBlockTypes[3] = ChooseBlockType(blockTypes[3], blockTypes[0], val[3], val[0], isoLevel); }
                        if ((edges & 16) != 0) { vertList[4] = LerpImproved(isoLevel, pos[4], pos[5], val[4], val[5]); vertBlockTypes[4] = ChooseBlockType(blockTypes[4], blockTypes[5], val[4], val[5], isoLevel); }
                        if ((edges & 32) != 0) { vertList[5] = LerpImproved(isoLevel, pos[5], pos[6], val[5], val[6]); vertBlockTypes[5] = ChooseBlockType(blockTypes[5], blockTypes[6], val[5], val[6], isoLevel); }
                        if ((edges & 64) != 0) { vertList[6] = LerpImproved(isoLevel, pos[6], pos[7], val[6], val[7]); vertBlockTypes[6] = ChooseBlockType(blockTypes[6], blockTypes[7], val[6], val[7], isoLevel); }
                        if ((edges & 128) != 0) { vertList[7] = LerpImproved(isoLevel, pos[7], pos[4], val[7], val[4]); vertBlockTypes[7] = ChooseBlockType(blockTypes[7], blockTypes[4], val[7], val[4], isoLevel); }
                        if ((edges & 256) != 0) { vertList[8] = LerpImproved(isoLevel, pos[0], pos[4], val[0], val[4]); vertBlockTypes[8] = ChooseBlockType(blockTypes[0], blockTypes[4], val[0], val[4], isoLevel); }
                        if ((edges & 512) != 0) { vertList[9] = LerpImproved(isoLevel, pos[1], pos[5], val[1], val[5]); vertBlockTypes[9] = ChooseBlockType(blockTypes[1], blockTypes[5], val[1], val[5], isoLevel); }
                        if ((edges & 1024) != 0) { vertList[10] = LerpImproved(isoLevel, pos[2], pos[6], val[2], val[6]); vertBlockTypes[10] = ChooseBlockType(blockTypes[2], blockTypes[6], val[2], val[6], isoLevel); }
                        if ((edges & 2048) != 0) { vertList[11] = LerpImproved(isoLevel, pos[3], pos[7], val[3], val[7]); vertBlockTypes[11] = ChooseBlockType(blockTypes[3], blockTypes[7], val[3], val[7], isoLevel); }

                        // Generate triangles with validation
                        for (int i = 0; i < 16; i += 3)
                        {
                            int a = Tables.TriTable[cubeIndex, i];
                            if (a == -1) break;

                            int b = Tables.TriTable[cubeIndex, i + 1];
                            int c = Tables.TriTable[cubeIndex, i + 2];

                            var v0 = vertList[a];
                            var v1 = vertList[b];
                            var v2 = vertList[c];

                            // Validate triangle - skip degenerate triangles
                            float dx01 = v1.X - v0.X;
                            float dy01 = v1.Y - v0.Y;
                            float dz01 = v1.Z - v0.Z;
                            float dist01Sq = dx01 * dx01 + dy01 * dy01 + dz01 * dz01;

                            float dx02 = v2.X - v0.X;
                            float dy02 = v2.Y - v0.Y;
                            float dz02 = v2.Z - v0.Z;
                            float dist02Sq = dx02 * dx02 + dy02 * dy02 + dz02 * dz02;

                            float dx12 = v2.X - v1.X;
                            float dy12 = v2.Y - v1.Y;
                            float dz12 = v2.Z - v1.Z;
                            float dist12Sq = dx12 * dx12 + dy12 * dy12 + dz12 * dz12;

                            // Skip if any edge is too short (degenerate triangle)
                            if (dist01Sq < EPSILON || dist02Sq < EPSILON || dist12Sq < EPSILON)
                                continue;

                            // Calculate normal using cross product
                            float nx = dy01 * dz02 - dz01 * dy02;
                            float ny = dz01 * dx02 - dx01 * dz02;
                            float nz = dx01 * dy02 - dy01 * dx02;

                            float normalLenSq = nx * nx + ny * ny + nz * nz;

                            // Skip triangles with zero or near-zero area
                            if (normalLenSq < MIN_TRIANGLE_AREA)
                                continue;

                            // Normalize
                            float invLen = 1.0f / MathF.Sqrt(normalLenSq);
                            nx *= invLen;
                            ny *= invLen;
                            nz *= invLen;

                            // Validate normal
                            if (float.IsNaN(nx) || float.IsNaN(ny) || float.IsNaN(nz))
                                continue;

                            BlockType triBlockType = GetMostCommonBlockType(
                                vertBlockTypes[a],
                                vertBlockTypes[b],
                                vertBlockTypes[c]
                            );

                            // Small offset to prevent z-fighting
                            const float offset = 0.001f;
                            float ox = nx * offset;
                            float oy = ny * offset;
                            float oz = nz * offset;

                            // Add triangle vertices
                            verts.Add(v0.X + ox); verts.Add(v0.Y + oy); verts.Add(v0.Z + oz);
                            verts.Add(nx); verts.Add(ny); verts.Add(nz);
                            verts.Add((float)triBlockType);

                            verts.Add(v1.X + ox); verts.Add(v1.Y + oy); verts.Add(v1.Z + oz);
                            verts.Add(nx); verts.Add(ny); verts.Add(nz);
                            verts.Add((float)triBlockType);

                            verts.Add(v2.X + ox); verts.Add(v2.Y + oy); verts.Add(v2.Z + oz);
                            verts.Add(nx); verts.Add(ny); verts.Add(nz);
                            verts.Add((float)triBlockType);
                        }
                    }
                }
            }
for (int i = 0; i < Math.Min(21, verts.Count); i += 7) // 7 floats per vertex (pos + normal + blocktype)
{
    float x = verts[i + 0];
    float y = verts[i + 1];
    float z = verts[i + 2];

}
            return verts.ToArray();
        }

        public static float[] GenerateMesh(Chunk chunk, float isoLevel = 0.5f)
        {
            var tempManager = new ChunkManager();
            var coord = new ChunkCoord(
                chunk.PositionX / ServerConfig.CHUNK_SIZE,
                chunk.PositionY / ServerConfig.CHUNK_SIZE_Y,
                chunk.PositionZ / ServerConfig.CHUNK_SIZE
            );
            return GenerateMesh(chunk, coord, tempManager, isoLevel);
        }

        /// <summary>
        /// Improved linear interpolation with better numerical stability
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 LerpImproved(float iso, Vector3 p1, Vector3 p2, float v1, float v2)
        {
            // Handle edge cases
            float diff = v2 - v1;
            if (MathF.Abs(diff) < EPSILON)
            {
                // Values are essentially equal, return midpoint
                return new Vector3(
                    (p1.X + p2.X) * 0.5f,
                    (p1.Y + p2.Y) * 0.5f,
                    (p1.Z + p2.Z) * 0.5f
                );
            }

            float t = (iso - v1) / diff;

            // Clamp to prevent extrapolation
            t = Math.Clamp(t, 0f, 1f);

            // Use more precise interpolation
            float oneMinusT = 1f - t;

            return new Vector3(
                p1.X * oneMinusT + p2.X * t,
                p1.Y * oneMinusT + p2.Y * t,
                p1.Z * oneMinusT + p2.Z * t
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static BlockType ChooseBlockType(BlockType b1, BlockType b2, float v1, float v2, float iso)
        {
            // Determine which side of the surface each value is on
            bool b1Solid = v1 > iso + EPSILON;
            bool b2Solid = v2 > iso + EPSILON;

            // If they're on opposite sides, choose the solid one
            if (b1Solid != b2Solid)
                return b1Solid ? b1 : b2;

            // Both on same side, choose based on distance to isosurface
            float dist1 = MathF.Abs(v1 - iso);
            float dist2 = MathF.Abs(v2 - iso);

            BlockType chosen = (dist1 < dist2) ? b1 : b2;

            // Fallback to non-air block
            if (chosen == BlockType.Air)
            {
                if (b1 != BlockType.Air) return b1;
                if (b2 != BlockType.Air) return b2;
                return BlockType.Stone;
            }

            return chosen;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static BlockType GetMostCommonBlockType(BlockType a, BlockType b, BlockType c)
        {
            // Replace air with stone for voting
            if (a == BlockType.Air) a = BlockType.Stone;
            if (b == BlockType.Air) b = BlockType.Stone;
            if (c == BlockType.Air) c = BlockType.Stone;

            // Majority vote
            if (a == b || a == c) return a;
            if (b == c) return b;

            // Priority system for tie-breaking
            int Priority(BlockType t)
            {
                return t switch
                {
                    BlockType.Grass => 5,
                    BlockType.Sand => 5,
                    BlockType.Snow => 5,
                    BlockType.Dirt => 3,
                    BlockType.Gravel => 3,
                    BlockType.Stone => 1,
                    _ => 0
                };
            }

            int priorityA = Priority(a);
            int priorityB = Priority(b);
            int priorityC = Priority(c);

            if (priorityA >= priorityB && priorityA >= priorityC) return a;
            if (priorityB >= priorityC) return b;
            return c;
        }
    }
}
