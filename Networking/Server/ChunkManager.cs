using System;
using System.Collections.Concurrent;

namespace Aetheris
{
    public class ChunkManager
    {
        // Use ConcurrentDictionary for thread-safe access
        private readonly ConcurrentDictionary<ChunkCoord, Chunk> chunks = new();
        
        // Cache for expensive sin/cos calculations
        private readonly ConcurrentDictionary<int, double> sinCache = new();
        private readonly ConcurrentDictionary<int, double> cosCache = new();

        public Chunk GetOrGenerateChunk(ChunkCoord coord)
        {
            return chunks.GetOrAdd(coord, GenerateChunk);
        }

        public bool TryGetChunk(ChunkCoord coord, out Chunk? chunk) 
        {
            return chunks.TryGetValue(coord, out chunk);
        }

        public void UnloadChunk(ChunkCoord coord) => chunks.TryRemove(coord, out _);

        private Chunk GenerateChunk(ChunkCoord coord)
        {
            var chunk = new Chunk();
            
            // Pre-calculate world offsets
            int worldXBase = coord.X * Config.CHUNK_SIZE;
            int worldYBase = coord.Y * Config.CHUNK_SIZE_Y;
            int worldZBase = coord.Z * Config.CHUNK_SIZE;

            // Generate terrain in parallel for better performance
            System.Threading.Tasks.Parallel.For(0, Config.CHUNK_SIZE, localX =>
            {
                int worldX = worldXBase + localX;
                
                for (int localZ = 0; localZ < Config.CHUNK_SIZE; localZ++)
                {
                    int worldZ = worldZBase + localZ;
                    
                    // Cache expensive trig calculations
                    double sinX = GetCachedSin(worldX);
                    double cosZ = GetCachedCos(worldZ);
                    
                    // Height calculation
                    double height = 20.0 + sinX * 6.0 + cosZ * 6.0;
                    int maxY = (int)Math.Clamp(Math.Round(height), 0, Config.CHUNK_SIZE_Y - 1);
                    
                    // Fill vertical column
                    for (int localY = 0; localY < Config.CHUNK_SIZE_Y; localY++)
                    {
                        int worldY = worldYBase + localY;
                        chunk.Blocks[localX, localY, localZ] = (byte)(worldY <= maxY ? 1 : 0);
                    }
                    
                    // Add decorative pillars
                    if ((worldX + worldZ) % 11 == 0)
                    {
                        int pillarHeight = Math.Min(4, Config.CHUNK_SIZE_Y - 6);
                        for (int p = 0; p < pillarHeight; p++)
                        {
                            int y = Math.Min(Config.CHUNK_SIZE_Y - 1, p + 6);
                            if (y >= 0 && y < Config.CHUNK_SIZE_Y)
                            {
                                chunk.Blocks[localX, y, localZ] = 2;
                            }
                        }
                    }
                }
            });

            return chunk;
        }

        private double GetCachedSin(int x)
        {
            return sinCache.GetOrAdd(x, key => Math.Sin(key * 0.08));
        }

        private double GetCachedCos(int z)
        {
            return cosCache.GetOrAdd(z, key => Math.Cos(key * 0.08));
        }

        // Optional: Clear old cache entries if memory becomes an issue
        public void ClearCaches()
        {
            if (sinCache.Count > 10000)
            {
                sinCache.Clear();
            }
            if (cosCache.Count > 10000)
            {
                cosCache.Clear();
            }
        }
    }
}
