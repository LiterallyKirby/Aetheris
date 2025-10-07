using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Aetheris
{
    /// <summary>
    /// ULTRA OPTIMIZED: Lightweight chunk manager with parallel column generation and collision mesh support
    /// </summary>
    public class ChunkManager
    {
        private readonly ConcurrentDictionary<ChunkCoord, Chunk> chunks = new();
        private readonly ConcurrentDictionary<ChunkCoord, WorldGen.ColumnData[,]> columnCache = new();
        private const int MaxCachedColumns = 20000;
        
        // Track cache access times for LRU eviction
        private readonly ConcurrentDictionary<ChunkCoord, long> cacheAccessTimes = new();
        private long accessCounter = 0;
        
        // Option to enable/disable collision mesh generation
        public bool GenerateCollisionMeshes { get; set; } = true;
        
        // Option to simplify collision meshes (improves performance)
        public float CollisionSimplification { get; set; } = 1f; // 1 = no simplification, 2 = half triangles, etc.

        public Chunk GetOrGenerateChunk(ChunkCoord coord)
        {
            return chunks.GetOrAdd(coord, c => GenerateChunk(c));
        }

        public bool TryGetChunk(ChunkCoord coord, out Chunk? chunk)
        {
            return chunks.TryGetValue(coord, out chunk);
        }

        public void UnloadChunk(ChunkCoord coord)
        {
            chunks.TryRemove(coord, out _);
            columnCache.TryRemove(coord, out _);
            cacheAccessTimes.TryRemove(coord, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public WorldGen.ColumnData[,] GetColumnDataForChunk(ChunkCoord coord)
        {
            // Update access time for LRU
            cacheAccessTimes[coord] = System.Threading.Interlocked.Increment(ref accessCounter);
            
            return columnCache.GetOrAdd(coord, c => GenerateColumnData(c));
        }

        private WorldGen.ColumnData[,] GenerateColumnData(ChunkCoord coord)
        {
            var columns = new WorldGen.ColumnData[ServerConfig.CHUNK_SIZE, ServerConfig.CHUNK_SIZE];
            
            int baseX = coord.X * ServerConfig.CHUNK_SIZE;
            int baseZ = coord.Z * ServerConfig.CHUNK_SIZE;

            // OPTIMIZATION: Parallel column generation for better CPU utilization
            if (ServerConfig.CHUNK_SIZE >= 64)
            {
                Parallel.For(0, ServerConfig.CHUNK_SIZE, lx =>
                {
                    for (int lz = 0; lz < ServerConfig.CHUNK_SIZE; lz++)
                    {
                        int wx = baseX + lx;
                        int wz = baseZ + lz;
                        columns[lx, lz] = WorldGen.GetColumnData(wx, wz);
                    }
                });
            }
            else
            {
                for (int lx = 0; lx < ServerConfig.CHUNK_SIZE; lx++)
                {
                    for (int lz = 0; lz < ServerConfig.CHUNK_SIZE; lz++)
                    {
                        int wx = baseX + lx;
                        int wz = baseZ + lz;
                        columns[lx, lz] = WorldGen.GetColumnData(wx, wz);
                    }
                }
            }

            // LRU cache eviction when over limit
            if (columnCache.Count > MaxCachedColumns)
            {
                EvictOldestCacheEntries();
            }

            return columns;
        }

        private void EvictOldestCacheEntries()
        {
            int toRemove = MaxCachedColumns / 5;
            var sorted = new System.Collections.Generic.List<(ChunkCoord coord, long time)>();
            
            foreach (var kvp in cacheAccessTimes)
            {
                sorted.Add((kvp.Key, kvp.Value));
            }
            
            sorted.Sort((a, b) => a.time.CompareTo(b.time));
            
            for (int i = 0; i < Math.Min(toRemove, sorted.Count); i++)
            {
                var coord = sorted[i].coord;
                columnCache.TryRemove(coord, out _);
                cacheAccessTimes.TryRemove(coord, out _);
            }
        }

        private Chunk GenerateChunk(ChunkCoord coord)
        {
            int worldX = coord.X * ServerConfig.CHUNK_SIZE;
            int worldY = coord.Y * ServerConfig.CHUNK_SIZE_Y;
            int worldZ = coord.Z * ServerConfig.CHUNK_SIZE;

            // Pre-cache column data
            _ = GetColumnDataForChunk(coord);

            var chunk = new Chunk(worldX, worldY, worldZ);
            
            // Generate collision mesh if enabled
            if (GenerateCollisionMeshes)
            {
                GenerateChunkCollisionMesh(chunk, coord);
            }
            
            return chunk;
        }
        
        /// <summary>
        /// Generate collision mesh for a chunk using marching cubes
        /// </summary>
        private void GenerateChunkCollisionMesh(Chunk chunk, ChunkCoord coord)
        {
            try
            {
                // Generate mesh using marching cubes
                float[] meshData = MarchingCubes.GenerateMesh(chunk, coord, this);
                
                if (meshData != null && meshData.Length > 0)
                {
                    // Generate collision mesh (simplified if requested)
                    if (CollisionSimplification > 1f)
                    {
                        chunk.GenerateSimplifiedCollisionMesh(meshData, CollisionSimplification);
                    }
                    else
                    {
                        chunk.GenerateCollisionMesh(meshData);
                    }
                }
                else
                {
                    chunk.ClearCollisionMesh();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ChunkManager] Error generating collision mesh for {coord}: {ex.Message}");
                chunk.ClearCollisionMesh();
            }
        }
        
        /// <summary>
        /// Regenerate collision mesh for an existing chunk (useful after terrain modifications)
        /// </summary>
        public void RegenerateCollisionMesh(ChunkCoord coord)
        {
            if (TryGetChunk(coord, out var chunk) && chunk != null)
            {
                GenerateChunkCollisionMesh(chunk, coord);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float SampleDensityFast(int worldX, int worldY, int worldZ, ChunkCoord coord)
        {
            var columns = GetColumnDataForChunk(coord);
            
            int lx = worldX - (coord.X * ServerConfig.CHUNK_SIZE);
            int lz = worldZ - (coord.Z * ServerConfig.CHUNK_SIZE);

            lx = Math.Clamp(lx, 0, ServerConfig.CHUNK_SIZE - 1);
            lz = Math.Clamp(lz, 0, ServerConfig.CHUNK_SIZE - 1);

            return WorldGen.SampleDensityFast(worldX, worldY, worldZ, columns[lx, lz]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BlockType GetBlockTypeFast(int worldX, int worldY, int worldZ, float density, ChunkCoord coord)
        {
            var columns = GetColumnDataForChunk(coord);
            
            int lx = worldX - (coord.X * ServerConfig.CHUNK_SIZE);
            int lz = worldZ - (coord.Z * ServerConfig.CHUNK_SIZE);

            lx = Math.Clamp(lx, 0, ServerConfig.CHUNK_SIZE - 1);
            lz = Math.Clamp(lz, 0, ServerConfig.CHUNK_SIZE - 1);

            return WorldGen.GetBlockType(worldX, worldY, worldZ, density, columns[lx, lz]);
        }

        public int GetChunkCount() => chunks.Count;
        public int GetCachedColumnCount() => columnCache.Count;
        
        /// <summary>
        /// Clear all caches (useful for memory management)
        /// </summary>
        public void ClearCaches()
        {
            columnCache.Clear();
            cacheAccessTimes.Clear();
        }
        
        /// <summary>
        /// Clear all collision meshes to save memory
        /// </summary>
        public void ClearAllCollisionMeshes()
        {
            foreach (var chunk in chunks.Values)
            {
                chunk.ClearCollisionMesh();
            }
        }
    }
}
