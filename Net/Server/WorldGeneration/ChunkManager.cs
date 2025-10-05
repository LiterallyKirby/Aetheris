using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Aetheris
{
    /// <summary>
    /// ULTRA OPTIMIZED: Lightweight chunk manager with parallel column generation
    /// </summary>
    public class ChunkManager
    {
        private readonly ConcurrentDictionary<ChunkCoord, Chunk> chunks = new();
        private readonly ConcurrentDictionary<ChunkCoord, WorldGen.ColumnData[,]> columnCache = new();
        private const int MaxCachedColumns = 20000; // Increased from 10000
        
        // Track cache access times for LRU eviction
        private readonly ConcurrentDictionary<ChunkCoord, long> cacheAccessTimes = new();
        private long accessCounter = 0;

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
            // Only parallelize for larger chunks (64x64+)
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
                // Sequential for small chunks (less parallel overhead)
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
            // Find oldest 20% of entries
            int toRemove = MaxCachedColumns / 5;
            var sorted = new System.Collections.Generic.List<(ChunkCoord coord, long time)>();
            
            foreach (var kvp in cacheAccessTimes)
            {
                sorted.Add((kvp.Key, kvp.Value));
            }
            
            // Sort by access time (oldest first)
            sorted.Sort((a, b) => a.time.CompareTo(b.time));
            
            // Remove oldest entries
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
            return chunk;
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
    }
}
