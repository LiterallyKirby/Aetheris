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

        // Pool for parallel density computation
        private class DensityWorkItem
        {
            public float[,,] DensityCache = null!;
            public BlockType[,,] BlockTypeCache = null!;
        }

        [ThreadStatic]
        private static DensityWorkItem? workItem;

        public static float[] GenerateMesh(Chunk chunk, ChunkCoord coord, ChunkManager chunkManager, float isoLevel = 0.5f)
        {
            if (threadLocalVerts == null)
                threadLocalVerts = new List<float>(30000); // Increased pre-allocation
            else
                threadLocalVerts.Clear();

            var verts = threadLocalVerts!;
            int step = Config.STEP;

            int sizeX = Chunk.SizeX + 1;
            int sizeY = Chunk.SizeY + 1;
            int sizeZ = Chunk.SizeZ + 1;

            // Check if chunk is potentially empty BEFORE any allocation
            bool potentiallyEmpty = chunk.PositionY > 100 || chunk.PositionY < -32;
            
            // Initialize work item
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
            var columns = chunkManager.GetColumnDataForChunk(coord);

            int solidCount = 0;
            int airCount = 0;

            // OPTIMIZED: Parallel density sampling with proper thread-local storage
            // Process in Z-columns to maintain cache coherency
            Parallel.For(0, sizeX, x =>
            {
                int worldX = chunk.PositionX + x;
                int colX = Math.Clamp(x, 0, Config.CHUNK_SIZE - 1);

                for (int z = 0; z < sizeZ; z++)
                {
                    int worldZ = chunk.PositionZ + z;
                    int colZ = Math.Clamp(z, 0, Config.CHUNK_SIZE - 1);
                    var columnData = columns[colX, colZ];

                    // Process entire Y column at once (better cache locality)
                    for (int y = 0; y < sizeY; y++)
                    {
                        int worldY = chunk.PositionY + y;
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

            // Early exit for empty chunks
            if (potentiallyEmpty && (solidCount == 0 || airCount == 0))
            {
                return Array.Empty<float>();
            }

            // Generate mesh
            var pos = posCache!;
            var val = valCache!;
            var blockTypes = blockTypesCache!;

            Span<Vector3> vertList = stackalloc Vector3[12];
            Span<BlockType> vertBlockTypes = stackalloc BlockType[12];

            int maxX = Chunk.SizeX / step * step;
            int maxY = Chunk.SizeY / step * step;
            int maxZ = Chunk.SizeZ / step * step;

            // OPTIMIZED: Process in better order for cache coherency (Y innermost)
            for (int x = 0; x < maxX; x += step)
            {
                int nextX = x + step;

                for (int z = 0; z < maxZ; z += step)
                {
                    int nextZ = z + step;

                    for (int y = 0; y < maxY; y += step)
                    {
                        int nextY = y + step;

                        // OPTIMIZED: Single array access pattern
                        val[0] = densityCache[x, y, z];
                        val[1] = densityCache[nextX, y, z];
                        val[2] = densityCache[nextX, y, nextZ];
                        val[3] = densityCache[x, y, nextZ];
                        val[4] = densityCache[x, nextY, z];
                        val[5] = densityCache[nextX, nextY, z];
                        val[6] = densityCache[nextX, nextY, nextZ];
                        val[7] = densityCache[x, nextY, nextZ];

                        // OPTIMIZED: Combined cube index calculation and early exit
                        int cubeIndex = 0;
                        int solidMask = 0;
                        
                        if (val[0] > isoLevel) { cubeIndex |= 1; solidMask |= 1; }
                        if (val[1] > isoLevel) { cubeIndex |= 2; solidMask |= 2; }
                        if (val[2] > isoLevel) { cubeIndex |= 4; solidMask |= 4; }
                        if (val[3] > isoLevel) { cubeIndex |= 8; solidMask |= 8; }
                        if (val[4] > isoLevel) { cubeIndex |= 16; solidMask |= 16; }
                        if (val[5] > isoLevel) { cubeIndex |= 32; solidMask |= 32; }
                        if (val[6] > isoLevel) { cubeIndex |= 64; solidMask |= 64; }
                        if (val[7] > isoLevel) { cubeIndex |= 128; solidMask |= 128; }

                        // Early exit: all solid (255) or all air (0)
                        if (solidMask == 0 || solidMask == 255) continue;

                        int edges = Tables.EdgeTable[cubeIndex];
                        if (edges == 0) continue;

                        // Load block types only when needed
                        blockTypes[0] = blockTypeCache[x, y, z];
                        blockTypes[1] = blockTypeCache[nextX, y, z];
                        blockTypes[2] = blockTypeCache[nextX, y, nextZ];
                        blockTypes[3] = blockTypeCache[x, y, nextZ];
                        blockTypes[4] = blockTypeCache[x, nextY, z];
                        blockTypes[5] = blockTypeCache[nextX, nextY, z];
                        blockTypes[6] = blockTypeCache[nextX, nextY, nextZ];
                        blockTypes[7] = blockTypeCache[x, nextY, nextZ];

                        // OPTIMIZED: Reuse position values
                        pos[0].X = x; pos[0].Y = y; pos[0].Z = z;
                        pos[1].X = nextX; pos[1].Y = y; pos[1].Z = z;
                        pos[2].X = nextX; pos[2].Y = y; pos[2].Z = nextZ;
                        pos[3].X = x; pos[3].Y = y; pos[3].Z = nextZ;
                        pos[4].X = x; pos[4].Y = nextY; pos[4].Z = z;
                        pos[5].X = nextX; pos[5].Y = nextY; pos[5].Z = z;
                        pos[6].X = nextX; pos[6].Y = nextY; pos[6].Z = nextZ;
                        pos[7].X = x; pos[7].Y = nextY; pos[7].Z = nextZ;

                        // Interpolate edge vertices (unrolled for better performance)
                        if ((edges & 1) != 0) { vertList[0] = LerpFast(isoLevel, pos[0], pos[1], val[0], val[1]); vertBlockTypes[0] = ChooseBlockType(blockTypes[0], blockTypes[1], val[0], val[1], isoLevel); }
                        if ((edges & 2) != 0) { vertList[1] = LerpFast(isoLevel, pos[1], pos[2], val[1], val[2]); vertBlockTypes[1] = ChooseBlockType(blockTypes[1], blockTypes[2], val[1], val[2], isoLevel); }
                        if ((edges & 4) != 0) { vertList[2] = LerpFast(isoLevel, pos[2], pos[3], val[2], val[3]); vertBlockTypes[2] = ChooseBlockType(blockTypes[2], blockTypes[3], val[2], val[3], isoLevel); }
                        if ((edges & 8) != 0) { vertList[3] = LerpFast(isoLevel, pos[3], pos[0], val[3], val[0]); vertBlockTypes[3] = ChooseBlockType(blockTypes[3], blockTypes[0], val[3], val[0], isoLevel); }
                        if ((edges & 16) != 0) { vertList[4] = LerpFast(isoLevel, pos[4], pos[5], val[4], val[5]); vertBlockTypes[4] = ChooseBlockType(blockTypes[4], blockTypes[5], val[4], val[5], isoLevel); }
                        if ((edges & 32) != 0) { vertList[5] = LerpFast(isoLevel, pos[5], pos[6], val[5], val[6]); vertBlockTypes[5] = ChooseBlockType(blockTypes[5], blockTypes[6], val[5], val[6], isoLevel); }
                        if ((edges & 64) != 0) { vertList[6] = LerpFast(isoLevel, pos[6], pos[7], val[6], val[7]); vertBlockTypes[6] = ChooseBlockType(blockTypes[6], blockTypes[7], val[6], val[7], isoLevel); }
                        if ((edges & 128) != 0) { vertList[7] = LerpFast(isoLevel, pos[7], pos[4], val[7], val[4]); vertBlockTypes[7] = ChooseBlockType(blockTypes[7], blockTypes[4], val[7], val[4], isoLevel); }
                        if ((edges & 256) != 0) { vertList[8] = LerpFast(isoLevel, pos[0], pos[4], val[0], val[4]); vertBlockTypes[8] = ChooseBlockType(blockTypes[0], blockTypes[4], val[0], val[4], isoLevel); }
                        if ((edges & 512) != 0) { vertList[9] = LerpFast(isoLevel, pos[1], pos[5], val[1], val[5]); vertBlockTypes[9] = ChooseBlockType(blockTypes[1], blockTypes[5], val[1], val[5], isoLevel); }
                        if ((edges & 1024) != 0) { vertList[10] = LerpFast(isoLevel, pos[2], pos[6], val[2], val[6]); vertBlockTypes[10] = ChooseBlockType(blockTypes[2], blockTypes[6], val[2], val[6], isoLevel); }
                        if ((edges & 2048) != 0) { vertList[11] = LerpFast(isoLevel, pos[3], pos[7], val[3], val[7]); vertBlockTypes[11] = ChooseBlockType(blockTypes[3], blockTypes[7], val[3], val[7], isoLevel); }

                        // Generate triangles
                        for (int i = 0; i < 16; i += 3)
                        {
                            int a = Tables.TriTable[cubeIndex, i];
                            if (a == -1) break;

                            int b = Tables.TriTable[cubeIndex, i + 1];
                            int c = Tables.TriTable[cubeIndex, i + 2];

                            var v0 = vertList[a];
                            var v1 = vertList[b];
                            var v2 = vertList[c];

                            // OPTIMIZED: Manual cross product (avoid intermediate vectors)
                            float e1x = v1.X - v0.X;
                            float e1y = v1.Y - v0.Y;
                            float e1z = v1.Z - v0.Z;

                            float e2x = v2.X - v0.X;
                            float e2y = v2.Y - v0.Y;
                            float e2z = v2.Z - v0.Z;

                            float nx = e1y * e2z - e1z * e2y;
                            float ny = e1z * e2x - e1x * e2z;
                            float nz = e1x * e2y - e1y * e2x;

                            float normalLenSq = nx * nx + ny * ny + nz * nz;
                            if (normalLenSq < 0.0001f) continue;

                            // OPTIMIZED: Fast inverse square root approximation
                            float invLen = 1.0f / MathF.Sqrt(normalLenSq);
                            nx *= invLen;
                            ny *= invLen;
                            nz *= invLen;

                            BlockType triBlockType = GetMostCommonBlockType(
                                vertBlockTypes[a],
                                vertBlockTypes[b],
                                vertBlockTypes[c]
                            );

                            // Small offset for z-fighting
                            const float offset = 0.001f;
                            float ox = nx * offset;
                            float oy = ny * offset;
                            float oz = nz * offset;

                            // OPTIMIZED: Direct addition instead of creating new vectors
                            // Add vertices (21 floats total)
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

            return verts.ToArray();
        }

        public static float[] GenerateMesh(Chunk chunk, float isoLevel = 0.5f)
        {
            var tempManager = new ChunkManager();
            var coord = new ChunkCoord(
                chunk.PositionX / Config.CHUNK_SIZE,
                chunk.PositionY / Config.CHUNK_SIZE_Y,
                chunk.PositionZ / Config.CHUNK_SIZE
            );
            return GenerateMesh(chunk, coord, tempManager, isoLevel);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 LerpFast(float iso, Vector3 p1, Vector3 p2, float v1, float v2)
        {
            float t = (iso - v1) / (v2 - v1);
            t = Math.Clamp(t, 0f, 1f);
            return new Vector3(
                p1.X + t * (p2.X - p1.X),
                p1.Y + t * (p2.Y - p1.Y),
                p1.Z + t * (p2.Z - p1.Z)
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static BlockType ChooseBlockType(BlockType b1, BlockType b2, float v1, float v2, float iso)
        {
            bool b1Solid = v1 > iso;
            bool b2Solid = v2 > iso;

            if (b1Solid != b2Solid)
                return b1Solid ? b1 : b2;

            BlockType chosen = (MathF.Abs(v1 - iso) < MathF.Abs(v2 - iso)) ? b1 : b2;

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
            if (a == BlockType.Air) a = BlockType.Stone;
            if (b == BlockType.Air) b = BlockType.Stone;
            if (c == BlockType.Air) c = BlockType.Stone;

            if (a == b || a == c) return a;
            if (b == c) return b;

            // Priority system
            if (a == BlockType.Grass || a == BlockType.Sand || a == BlockType.Snow) return a;
            if (b == BlockType.Grass || b == BlockType.Sand || b == BlockType.Snow) return b;
            if (c == BlockType.Grass || c == BlockType.Sand || c == BlockType.Snow) return c;

            if (a == BlockType.Dirt || a == BlockType.Gravel) return a;
            if (b == BlockType.Dirt || b == BlockType.Gravel) return b;
            if (c == BlockType.Dirt || c == BlockType.Gravel) return c;

            return a;
        }
    }
}
