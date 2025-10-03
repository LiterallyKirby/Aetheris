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
        public enum Biome
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
            caveNoise1.SetFrequency(0.02f);

            caveNoise2 = new FastNoiseLite(seed + 3);
            caveNoise2.SetNoiseType(NoiseType.OpenSimplex2);
            caveNoise2.SetFrequency(0.04f);

            caveNoise3 = new FastNoiseLite(seed + 4);
            caveNoise3.SetNoiseType(NoiseType.Perlin);
            caveNoise3.SetFrequency(0.08f);

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

        private static (float baseHeight, float amplitude) GetBiomeParams(Biome biome)
        {
            return biome switch
            {
                Biome.Plains => (30f, 6f),
                Biome.Forest => (35f, 10f),
                Biome.Desert => (28f, 5f), // slightly raised and smoothed deserts
                Biome.Mountains => (50f, 35f),
                _ => (30f, 10f)
            };
        }

        // thickness of surface/subsurface per biome
        private static int GetDirtDepth(Biome biome)
        {
            return biome switch
            {
                Biome.Plains => 6,
                Biome.Forest => 7,
                Biome.Desert => 4,
                Biome.Mountains => 2,
                _ => 5
            };
        }

        /// <summary>
        /// Check if a position is exposed to air (has air above it)
        /// slightly relaxed: check a couple blocks above so slopes count as exposed
        /// </summary>
        private static bool IsExposedToAir(int x, int y, int z)
        {
            // Check multiple blocks above for better detection
            for (int dy = 1; dy <= 3; dy++)
            {
                if (SampleDensity(x, y + dy, z) <= ISO)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Get the (rounded) surface Y coordinate for a column (x,z)
        /// </summary>
        private static int GetSurfaceY(int x, int z)
        {
            var biome = GetBiome(x, z);
            var (baseHeight, amplitude) = GetBiomeParams(biome);
            float surfaceNoise = terrainNoise.GetNoise(x, z);
            float surfaceYf = baseHeight + surfaceNoise * amplitude;
            return (int)MathF.Round(surfaceYf);
        }

        /// <summary>
        /// Get block type at world position (with known density)
        /// </summary>
        public static BlockType GetBlockType(int x, int y, int z, float density)
        {
            if (!initialized) Initialize();

            if (density <= ISO)
                return BlockType.Air;

            if (y <= 2)
                return BlockType.Stone;

            Biome biome = GetBiome(x, z);
            int surfaceY = GetSurfaceY(x, z);

            // CRITICAL DEBUG: Log suspicious assignments
            BlockType result;

            int depthBelowSurface = surfaceY - y;
            bool isAtSurface = (y == surfaceY);
            bool isExposed = IsExposedToAir(x, y, z);

            if (isAtSurface || isExposed)
            {
                result = biome switch
                {
                    Biome.Plains => BlockType.Grass,
                    Biome.Forest => BlockType.Grass,
                    Biome.Desert => BlockType.Sand,
                    Biome.Mountains => (y >= surfaceY && surfaceY > 45) ? BlockType.Snow : BlockType.Stone,
                    _ => BlockType.Grass
                };
            }
            else if (depthBelowSurface > 0 && depthBelowSurface <= GetDirtDepth(biome))
            {
                result = biome switch
                {
                    Biome.Plains => BlockType.Dirt,
                    Biome.Forest => BlockType.Dirt,
                    Biome.Desert => BlockType.Sand,
                    Biome.Mountains => BlockType.Stone,
                    _ => BlockType.Dirt
                };
            }
            else if (y < 15 && ((x + y + z) % 7) == 0)
            {
                result = BlockType.Gravel;
            }
            else
            {
                result = BlockType.Stone;
            }

            // DEBUG: Log anomalies
            if (result == BlockType.Sand && y < 20)
            {


            }
            if (result == BlockType.Snow && y < 35)
            {
                Console.WriteLine($"[WorldGen] WARNING: Snow at y={y}, surfaceY={surfaceY}, " +
                                 $"biome={biome}, isExposed={isExposed}");
            }

            return result;
        }
        /// <summary>
        /// Get block type at world position (convenience overload)
        /// </summary>
        public static BlockType GetBlockType(int x, int y, int z)
        {
            float density = SampleDensity(x, y, z);
            return GetBlockType(x, y, z, density);
        }

        public static float SampleDensity(int x, int y, int z)
        {
            if (!initialized) Initialize();

            // Determine biome and params for column
            Biome biome = GetBiome(x, z);
            var (baseHeight, amplitude) = GetBiomeParams(biome);

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

            // Get cave intensity for this biome
            float caveIntensity = GetCaveIntensity(biome);

            // Multi-layer cave system with depth-based parameters
            if (y > 3 && y < surfaceY - 3 && caveIntensity > 0)
            {
                float c1 = caveNoise1.GetNoise(x, y, z);
                float c2 = caveNoise2.GetNoise(x, y, z);
                float c3 = caveNoise3.GetNoise(x, y, z);

                // Calculate depth factor (0 = near surface, 1 = deep underground)
                float depthFactor = Math.Clamp(1.0f - (y / surfaceY), 0f, 1f);

                // Shallow caves (near surface) - tight tunnels
                if (y > surfaceY - 15)
                {
                    float worm = (c1 + c2) * 0.5f;
                    if (worm > 0.2f)
                    {
                        density -= (worm - 0.2f) * 60.0f * caveIntensity;
                    }
                }
                // Mid-depth caves - larger tunnels and chambers
                else if (y > 20)
                {
                    float worm = (c1 + c2) * 0.5f;
                    if (worm > 0.15f)
                    {
                        float strength = 80.0f + (depthFactor * 70.0f);
                        density -= (worm - 0.15f) * strength * caveIntensity;
                    }

                    // Medium caverns
                    if (c3 > 0.3f)
                    {
                        float strength = 15.0f + (depthFactor * 15.0f);
                        density -= (c3 - 0.3f) * strength * caveIntensity;
                    }
                }
                // Deep caves (y <= 20) - massive caverns and networks
                else
                {
                    // Worm tunnels - more aggressive
                    float worm = (c1 + c2) * 0.5f;
                    if (worm > 0.1f)
                    {
                        density -= (worm - 0.1f) * 200.0f * caveIntensity;
                    }

                    // Large caverns
                    if (c3 > 0.25f)
                    {
                        density -= (c3 - 0.25f) * 40.0f * caveIntensity;
                    }

                    // Massive deep caverns using noise multiplication
                    float cavern = c1 * c2;
                    if (cavern > 0.2f)
                    {
                        density -= (cavern - 0.2f) * 25000.0f * caveIntensity;
                    }

                    // Extra deep pockets (y < 10) - enormous underground chambers
                    if (y < 10)
                    {
                        float deepCavern = (c1 + c3) * 0.5f;
                        if (deepCavern > 0.15f)
                        {
                            density -= (deepCavern - 0.15f) * 300.0f * caveIntensity;
                        }
                    }
                }
            }

            // Bedrock layer (unbreakable bottom)
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

        public static void PrintBiomeAt(int x, int z)
        {
            Biome biome = GetBiome(x, z);
            Console.WriteLine($"Biome at ({x}, {z}) is {biome}");
        }

        public static bool IsSolid(int x, int y, int z)
        {
            return SampleDensity(x, y, z) > ISO;
        }
    }
}
