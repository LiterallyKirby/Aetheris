using System;
using static FastNoiseLite;

namespace Aetheris
{
    /// <summary>
    /// Biome-based world generation with configurable seed
    /// </summary>
    public static class WorldGen
    {
        // Noise generators (initialized with seed)
        private static FastNoiseLite terrainNoise;
        private static FastNoiseLite biomeNoise;
        private static FastNoiseLite caveNoise1;
        private static FastNoiseLite caveNoise2;
        private static FastNoiseLite caveNoise3;
        
        private const float ISO = 0.5f;
        private static bool initialized = false;

        // Biome definitions
        private enum Biome
        {
            Plains,
            Mountains,
            Desert,
            Forest
        }

        public static void Initialize()
        {
            if (initialized) return;
            
            int seed = Config.WORLD_SEED;
            
            // Biome selection noise
            biomeNoise = new FastNoiseLite(seed);
            biomeNoise.SetNoiseType(NoiseType.Cellular);
            biomeNoise.SetFrequency(0.001f); // Very large biomes
            biomeNoise.SetCellularReturnType(CellularReturnType.CellValue);
            
            // Terrain height noise
            terrainNoise = new FastNoiseLite(seed + 1);
            terrainNoise.SetNoiseType(NoiseType.OpenSimplex2);
            terrainNoise.SetFrequency(0.008f);
            terrainNoise.SetFractalOctaves(4);
            
            // Cave systems
            caveNoise1 = new FastNoiseLite(seed + 2);
            caveNoise1.SetNoiseType(NoiseType.OpenSimplex2);
            caveNoise1.SetFrequency(0.015f);
            
            caveNoise2 = new FastNoiseLite(seed + 3);
            caveNoise2.SetNoiseType(NoiseType.OpenSimplex2);
            caveNoise2.SetFrequency(0.03f);
            
            caveNoise3 = new FastNoiseLite(seed + 4);
            caveNoise3.SetNoiseType(NoiseType.Perlin);
            caveNoise3.SetFrequency(0.05f);
            
            initialized = true;
        }

        private static Biome GetBiome(int x, int z)
        {
            float biomeValue = biomeNoise.GetNoise(x, z);
            
            // Map noise value (-1 to 1) to biomes
            if (biomeValue < -0.5f) return Biome.Plains;
            if (biomeValue < 0.0f) return Biome.Forest;
            if (biomeValue < 0.5f) return Biome.Desert;
            return Biome.Mountains;
        }

        private static (float baseHeight, float amplitude) GetBiomeParams(Biome biome)
        {
            return biome switch
            {
                Biome.Plains => (25f, 5f),      // Flat, low terrain
                Biome.Forest => (30f, 12f),     // Rolling hills
                Biome.Desert => (20f, 8f),      // Sandy, moderate variation
                Biome.Mountains => (40f, 35f),  // Tall, dramatic peaks
                _ => (30f, 10f)
            };
        }

        public static float SampleDensity(int x, int y, int z)
        {
            if (!initialized) Initialize();
            
            // Determine biome at this XZ position
            Biome biome = GetBiome(x, z);
            var (baseHeight, amplitude) = GetBiomeParams(biome);
            
            // Calculate surface height based on biome
            float surfaceNoise = terrainNoise.GetNoise(x, z);
            float surfaceY = baseHeight + surfaceNoise * amplitude;
            
            // Base density calculation (solid pillars)
            float density;
            if (y > surfaceY)
            {
                // Above surface = air
                density = ISO - (y - surfaceY) * 0.2f;
            }
            else
            {
                // Below surface = solid
                float depthBelowSurface = surfaceY - y;
                density = ISO + 2.0f + (depthBelowSurface * 0.02f);
            }
            
            // Carve caves (biome-specific cave density)
            float caveIntensity = GetCaveIntensity(biome);
            if (y < surfaceY - 5 && y > 5 && caveIntensity > 0)
            {
                float c1 = caveNoise1.GetNoise(x, y, z);
                float c2 = caveNoise2.GetNoise(x, y, z);
                float c3 = caveNoise3.GetNoise(x, y, z);
                
                // Worm caves
                float worm = (c1 + c2) * 0.5f;
                if (worm > 0.3f)
                {
                    density -= (worm - 0.3f) * 8.0f * caveIntensity;
                }
                
                // Cheese caves
                if (c3 > 0.5f && y < surfaceY * 0.7f)
                {
                    density -= (c3 - 0.5f) * 6.0f * caveIntensity;
                }
                
                // Large caverns (deep only)
                if (y < 20)
                {
                    float cavern = c1 * c2;
                    if (cavern > 0.4f)
                    {
                        density -= (cavern - 0.4f) * 10.0f * caveIntensity;
                    }
                }
            }
            
            // Bedrock layer
            if (y <= 2)
            {
                density += (3 - y) * 100f;
            }
            
            return density;
        }

        private static float GetCaveIntensity(Biome biome)
        {
            // Different biomes have different cave densities
            return biome switch
            {
                Biome.Plains => 1.0f,      // Normal caves
                Biome.Forest => 0.8f,      // Fewer caves
                Biome.Desert => 0.6f,      // Sparse caves
                Biome.Mountains => 1.5f,   // Lots of caves in mountains
                _ => 1.0f
            };
        }

        public static bool IsSolid(int x, int y, int z)
        {
            return SampleDensity(x, y, z) > ISO;
        }
    }
}
