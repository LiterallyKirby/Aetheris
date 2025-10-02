using System;
using static FastNoiseLite;

namespace Aetheris
{
    /// <summary>
    /// Biome-based world generation with configurable seed and block types
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
            biomeNoise.SetFrequency(0.001f);
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
            
            if (biomeValue < -0.5f) return Biome.Plains;
            if (biomeValue < 0.0f) return Biome.Forest;
            if (biomeValue < 0.5f) return Biome.Desert;
            return Biome.Mountains;
        }

        /// <summary>
        /// Get biome blend weights for smooth transitions
        /// Returns the primary biome and a blend factor (0-1)
        /// </summary>
        private static (Biome primary, Biome secondary, float blend) GetBiomeBlend(int x, int z)
        {
            float biomeValue = biomeNoise.GetNoise(x, z);
            const float blendWidth = 0.15f; // Transition zone width
            
            Biome primary, secondary;
            float blend;
            
            // Check each biome boundary
            if (biomeValue < -0.5f + blendWidth && biomeValue >= -0.5f - blendWidth)
            {
                // Transition between Plains and previous
                primary = Biome.Plains;
                secondary = Biome.Mountains; // Wraps around
                blend = (biomeValue - (-0.5f - blendWidth)) / (blendWidth * 2);
            }
            else if (biomeValue < 0.0f + blendWidth && biomeValue >= 0.0f - blendWidth)
            {
                // Transition between Forest and Plains
                primary = Biome.Forest;
                secondary = Biome.Plains;
                blend = (biomeValue - (0.0f - blendWidth)) / (blendWidth * 2);
            }
            else if (biomeValue < 0.5f + blendWidth && biomeValue >= 0.5f - blendWidth)
            {
                // Transition between Desert and Forest
                primary = Biome.Desert;
                secondary = Biome.Forest;
                blend = (biomeValue - (0.5f - blendWidth)) / (blendWidth * 2);
            }
            else if (biomeValue >= 0.5f + blendWidth || biomeValue < -0.5f - blendWidth)
            {
                // Transition between Mountains and Desert
                primary = Biome.Mountains;
                secondary = Biome.Desert;
                if (biomeValue >= 0.5f + blendWidth)
                    blend = (biomeValue - (0.5f + blendWidth)) / (blendWidth * 2);
                else
                    blend = 1.0f - ((biomeValue - (-0.5f - blendWidth * 2)) / (blendWidth * 2));
            }
            else
            {
                // Pure biome (no blending)
                primary = GetBiome(x, z);
                secondary = primary;
                blend = 0.0f;
            }
            
            blend = Math.Clamp(blend, 0.0f, 1.0f);
            return (primary, secondary, blend);
        }

        private static (float baseHeight, float amplitude) GetBiomeParams(Biome biome)
        {
            return biome switch
            {
                Biome.Plains => (25f, 5f),
                Biome.Forest => (30f, 12f),
                Biome.Desert => (20f, 8f),
                Biome.Mountains => (40f, 35f),
                _ => (30f, 10f)
            };
        }

        /// <summary>
        /// Get block type at world position (with known density)
        /// Uses biome blending for smooth transitions
        /// </summary>
        public static BlockType GetBlockType(int x, int y, int z, float density)
        {
            if (!initialized) Initialize();
            
            // Air check - use provided density
            if (density <= ISO)
                return BlockType.Air;
            
            // Bedrock layer
            if (y <= 2)
                return BlockType.Stone;
            
            // Get blended biome info
            var (primaryBiome, secondaryBiome, blendFactor) = GetBiomeBlend(x, z);
            
            // Get terrain params for both biomes
            var (baseHeight1, amplitude1) = GetBiomeParams(primaryBiome);
            var (baseHeight2, amplitude2) = GetBiomeParams(secondaryBiome);
            
            // Blend terrain height
            float surfaceNoise = terrainNoise.GetNoise(x, z);
            float surfaceY1 = baseHeight1 + surfaceNoise * amplitude1;
            float surfaceY2 = baseHeight2 + surfaceNoise * amplitude2;
            float surfaceY = surfaceY1 * (1.0f - blendFactor) + surfaceY2 * blendFactor;
            
            int depthBelowSurface = (int)(surfaceY - y);
            
            // Determine which biome's block to use based on blend
            // Use a noise-based selection for natural mixing
            float selectionNoise = terrainNoise.GetNoise(x * 0.5f, z * 0.5f);
            bool usePrimary = (selectionNoise + 1.0f) * 0.5f > blendFactor;
            Biome selectedBiome = usePrimary ? primaryBiome : secondaryBiome;
            
            // Surface layer (top block)
            if (depthBelowSurface <= 1)
            {
                return selectedBiome switch
                {
                    Biome.Plains => BlockType.Grass,
                    Biome.Forest => BlockType.Grass,
                    Biome.Desert => BlockType.Sand,
                    Biome.Mountains => y > 45 ? BlockType.Snow : BlockType.Stone,
                    _ => BlockType.Grass
                };
            }
            
            // Subsurface layers
            if (depthBelowSurface <= 4)
            {
                return selectedBiome switch
                {
                    Biome.Desert => BlockType.Sand,
                    Biome.Mountains => BlockType.Stone,
                    _ => BlockType.Dirt
                };
            }
            
            // Deep underground - mix of stone and gravel
            if (y < 15 && (x + y + z) % 7 == 0)
                return BlockType.Gravel;
            
            return BlockType.Stone;
        }

        /// <summary>
        /// Get block type at world position (convenience overload)
        /// </summary>
        public static BlockType GetBlockType(int x, int y, int z)
        {
            if (!initialized) Initialize();
            
            // Air check first - using density
            float density = SampleDensity(x, y, z);
            if (density <= ISO)
                return BlockType.Air;
            
            // Bedrock layer
            if (y <= 2)
                return BlockType.Stone;
            
            // Determine biome
            Biome biome = GetBiome(x, z);
            var (baseHeight, amplitude) = GetBiomeParams(biome);
            
            float surfaceNoise = terrainNoise.GetNoise(x, z);
            float surfaceY = baseHeight + surfaceNoise * amplitude;
            
            int depthBelowSurface = (int)(surfaceY - y);
            
            // Surface layer (top block)
            if (depthBelowSurface <= 1)
            {
                return biome switch
                {
                    Biome.Plains => BlockType.Grass,
                    Biome.Forest => BlockType.Grass,
                    Biome.Desert => BlockType.Sand,
                    Biome.Mountains => y > 45 ? BlockType.Snow : BlockType.Stone,
                    _ => BlockType.Grass
                };
            }
            
            // Subsurface layers
            if (depthBelowSurface <= 4)
            {
                return biome switch
                {
                    Biome.Desert => BlockType.Sand,
                    Biome.Mountains => BlockType.Stone,
                    _ => BlockType.Dirt
                };
            }
            
            // Deep underground - mix of stone and gravel
            if (y < 15 && (x + y + z) % 7 == 0)
                return BlockType.Gravel;
            
            return BlockType.Stone;
        }

        public static float SampleDensity(int x, int y, int z)
        {
            if (!initialized) Initialize();
            
            // Get blended biome parameters
            var (primaryBiome, secondaryBiome, blendFactor) = GetBiomeBlend(x, z);
            var (baseHeight1, amplitude1) = GetBiomeParams(primaryBiome);
            var (baseHeight2, amplitude2) = GetBiomeParams(secondaryBiome);
            
            // Blend the terrain parameters
            float baseHeight = baseHeight1 * (1.0f - blendFactor) + baseHeight2 * blendFactor;
            float amplitude = amplitude1 * (1.0f - blendFactor) + amplitude2 * blendFactor;
            
            float surfaceNoise = terrainNoise.GetNoise(x, z);
            float surfaceY = baseHeight + surfaceNoise * amplitude;
            
            float density;
            if (y > surfaceY)
            {
                density = ISO - (y - surfaceY) * 0.2f;
            }
            else
            {
                float depthBelowSurface = surfaceY - y;
                density = ISO + 2.0f + (depthBelowSurface * 0.02f);
            }
            
            // Blend cave intensity
            float caveIntensity1 = GetCaveIntensity(primaryBiome);
            float caveIntensity2 = GetCaveIntensity(secondaryBiome);
            float caveIntensity = caveIntensity1 * (1.0f - blendFactor) + caveIntensity2 * blendFactor;
            
            // Carve caves
            if (y < surfaceY - 5 && y > 5 && caveIntensity > 0)
            {
                float c1 = caveNoise1.GetNoise(x, y, z);
                float c2 = caveNoise2.GetNoise(x, y, z);
                float c3 = caveNoise3.GetNoise(x, y, z);
                
                float worm = (c1 + c2) * 0.5f;
                if (worm > 0.3f)
                {
                    density -= (worm - 0.3f) * 50.0f * caveIntensity;
                }
                
                if (c3 > 0.5f && y < surfaceY * 0.7f)
                {
                    density -= (c3 - 0.5f) * 6.0f * caveIntensity;
                }
                
                if (y < 20)
                {
                    float cavern = c1 * c2;
                    if (cavern > 0.4f)
                    {
                        density -= (cavern - 0.4f) * 10000.0f * caveIntensity;
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
            return biome switch
            {
                Biome.Plains => 1.0f,
                Biome.Forest => 0.8f,
                Biome.Desert => 0.6f,
                Biome.Mountains => 1.5f,
                _ => 1.0f
            };
        }

        public static bool IsSolid(int x, int y, int z)
        {
            return SampleDensity(x, y, z) > ISO;
        }
    }
}
