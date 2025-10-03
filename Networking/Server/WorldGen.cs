using System;
using static FastNoiseLite;

namespace Aetheris
{
    /// <summary>
    /// Biome-based world generation with deep cave systems and feature placement framework.
    /// 
    /// ARCHITECTURE:
    /// 1. Density Generation: SampleDensity() creates the terrain shape (ISO = 0.5 threshold)
    ///    - Values > 0.5 = solid blocks
    ///    - Values <= 0.5 = air
    /// 
    /// 2. Block Type Assignment: GetBlockType() assigns materials based on:
    ///    - Biome type
    ///    - Position relative to surface
    ///    - Special feature placement (ores, etc.)
    /// 
    /// 3. Feature Placement: AddFeatures() generates structures like trees, ores
    ///    - Called during chunk generation
    ///    - Uses noise/randomness for natural distribution
    /// 
    /// HOW TO ADD FEATURES:
    /// 
    /// A) Adding New Ore Types:
    ///    1. Add to BlockType enum (e.g., BlockType.IronOre = 9)
    ///    2. In GetBlockType(), add ore placement logic in the underground section:
    ///       if (y < 40 && oreNoise.GetNoise(x, y, z) > 0.6f)
    ///           return BlockType.IronOre;
    ///    3. Control rarity with threshold (0.6 = rare, 0.3 = common)
    ///    4. Control depth range with y-coordinate checks
    /// 
    /// B) Adding Trees/Surface Structures:
    ///    1. Create placement noise generator in Initialize()
    ///    2. In GetBlockType(), check if position is a tree location:
    ///       if (IsTreeTrunk(x, y, z)) return BlockType.Wood;
    ///       if (IsTreeLeaves(x, y, z)) return BlockType.Leaves;
    ///    3. Use helper methods to define tree shape
    /// 
    /// C) Adding New Biomes:
    ///    1. Add to Biome enum
    ///    2. Update GetBiome() threshold ranges
    ///    3. Add case to GetBiomeParams() for height/amplitude
    ///    4. Add case to GetDirtDepth() for subsurface depth
    ///    5. Add case to GetBlockType() surface material logic
    /// 
    /// CAVE SYSTEM:
    /// - Shallow caves (near surface): Tight winding tunnels
    /// - Mid-depth (y=20-surface): Large tunnel networks with chambers
    /// - Deep caves (y=5-20): Massive caverns and underground lakes
    /// - Bedrock (y<=2): Unbreakable layer
    /// </summary>
    public static class WorldGen
    {
        // Noise generators (initialized with seed)
        private static FastNoiseLite terrainNoise;
        private static FastNoiseLite biomeNoise;
        private static FastNoiseLite caveNoise1;
        private static FastNoiseLite caveNoise2;
        private static FastNoiseLite caveNoise3;
        private static FastNoiseLite caveLarge;  // For huge caverns
        
        // Feature placement noise (add more as needed)
        // private static FastNoiseLite oreNoise;
        // private static FastNoiseLite treeNoise;

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

            // Cave systems - multiple layers for complexity
            caveNoise1 = new FastNoiseLite(seed + 2);
            caveNoise1.SetNoiseType(NoiseType.OpenSimplex2);
            caveNoise1.SetFrequency(0.015f);  // Increased for more tunnels

            caveNoise2 = new FastNoiseLite(seed + 3);
            caveNoise2.SetNoiseType(NoiseType.OpenSimplex2);
            caveNoise2.SetFrequency(0.03f);   // Increased for finer detail

            caveNoise3 = new FastNoiseLite(seed + 4);
            caveNoise3.SetNoiseType(NoiseType.Perlin);
            caveNoise3.SetFrequency(0.05f);   // Increased for varied chambers

            caveLarge = new FastNoiseLite(seed + 5);
            caveLarge.SetNoiseType(NoiseType.Cellular);
            caveLarge.SetFrequency(0.008f);   // Very large caverns
            caveLarge.SetCellularReturnType(CellularReturnType.Distance);

            // FEATURE PLACEMENT EXAMPLE:
            // oreNoise = new FastNoiseLite(seed + 10);
            // oreNoise.SetNoiseType(NoiseType.OpenSimplex2);
            // oreNoise.SetFrequency(0.04f);
            //
            // treeNoise = new FastNoiseLite(seed + 11);
            // treeNoise.SetNoiseType(NoiseType.Cellular);
            // treeNoise.SetFrequency(0.02f);

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
                Biome.Desert => (28f, 5f),
                Biome.Mountains => (50f, 35f),
                _ => (30f, 10f)
            };
        }

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
        /// </summary>
        private static bool IsExposedToAir(int x, int y, int z)
        {
            for (int dy = 1; dy <= 3; dy++)
            {
                if (SampleDensity(x, y + dy, z) <= ISO)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Get the surface Y coordinate for a column (x,z)
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
        /// This is where you add feature placement logic
        /// </summary>
        public static BlockType GetBlockType(int x, int y, int z, float density)
        {
            if (!initialized) Initialize();

            // Air blocks
            if (density <= ISO)
                return BlockType.Air;

            // Bedrock layer
            if (y <= 2)
                return BlockType.Stone;

            Biome biome = GetBiome(x, z);
            int surfaceY = GetSurfaceY(x, z);
            int depthBelowSurface = surfaceY - y;
            bool isExposed = IsExposedToAir(x, y, z);

            // SURFACE LAYER (exposed to air)
            if (isExposed)
            {
                return biome switch
                {
                    Biome.Plains => BlockType.Grass,
                    Biome.Forest => BlockType.Grass,
                    Biome.Desert => BlockType.Sand,
                    Biome.Mountains => (surfaceY > 45) ? BlockType.Snow : BlockType.Stone,
                    _ => BlockType.Grass
                };
            }

            // SUBSURFACE LAYER (dirt/sand layer below surface)
            if (depthBelowSurface > 0 && depthBelowSurface <= GetDirtDepth(biome))
            {
                return biome switch
                {
                    Biome.Plains => BlockType.Dirt,
                    Biome.Forest => BlockType.Dirt,
                    Biome.Desert => BlockType.Sand,
                    Biome.Mountains => BlockType.Stone,
                    _ => BlockType.Dirt
                };
            }

            // UNDERGROUND LAYER (this is where you add ores and underground features)
            
            // Example: Gravel pockets
            if (y < 15 && ((x + y + z) % 7) == 0)
            {
                return BlockType.Gravel;
            }

            // ORE PLACEMENT EXAMPLE (uncomment and modify):
            /*
            // Iron ore - common, mid-depth
            if (y >= 10 && y <= 50)
            {
                float ironNoise = oreNoise.GetNoise(x, y, z);
                if (ironNoise > 0.65f) // Adjust threshold for rarity
                    return BlockType.IronOre;
            }

            // Gold ore - rare, deep
            if (y >= 5 && y <= 25)
            {
                float goldNoise = oreNoise.GetNoise(x * 1.3f, y * 1.3f, z * 1.3f);
                if (goldNoise > 0.75f) // Higher threshold = rarer
                    return BlockType.GoldOre;
            }

            // Diamond ore - very rare, very deep
            if (y >= 5 && y <= 15)
            {
                float diamondNoise = oreNoise.GetNoise(x * 0.7f, y * 0.7f, z * 0.7f);
                if (diamondNoise > 0.82f)
                    return BlockType.DiamondOre;
            }
            */

            // Default underground material
            return BlockType.Stone;
        }

        /// <summary>
        /// Get block type at world position (convenience overload)
        /// </summary>
        public static BlockType GetBlockType(int x, int y, int z)
        {
            float density = SampleDensity(x, y, z);
            return GetBlockType(x, y, z, density);
        }

        /// <summary>
        /// Sample density at world position - this creates the terrain shape
        /// Higher values = solid, lower values = air
        /// Threshold (ISO) = 0.5
        /// SUPPORTS NEGATIVE Y: Caves extend down to y=-64 (bedrock)
        /// </summary>
        public static float SampleDensity(int x, int y, int z)
        {
            if (!initialized) Initialize();

            // Base terrain shape
            Biome biome = GetBiome(x, z);
            var (baseHeight, amplitude) = GetBiomeParams(biome);
            float surfaceNoise = terrainNoise.GetNoise(x, z);
            float surfaceY = baseHeight + surfaceNoise * amplitude;

            // Basic density: solid below surface, air above
            float density;
            if (y > surfaceY)
            {
                // Above surface - air
                density = ISO - (y - surfaceY) * 0.2f;
            }
            else
            {
                // Below surface - solid (density increases with depth)
                float depthBelowSurface = surfaceY - y;
                density = ISO + 2.0f + (depthBelowSurface * 0.02f);
            }

            // CAVE GENERATION - carve out caves by reducing density
            // Now extends from y=-64 (near bedrock) to surface
            float caveIntensity = GetCaveIntensity(biome);
            
            if (y > -64 && y < surfaceY - 2 && caveIntensity > 0)
            {
                float c1 = caveNoise1.GetNoise(x, y, z);
                float c2 = caveNoise2.GetNoise(x, y, z);
                float c3 = caveNoise3.GetNoise(x, y, z);
                float cL = caveLarge.GetNoise(x, y, z);

                // SHALLOW CAVES (upper 20 blocks) - Surface-level tunnels
                if (y > surfaceY - 20)
                {
                    float worm = (c1 + c2) * 0.5f;
                    if (worm > 0.12f)
                    {
                        density -= (worm - 0.12f) * 100.0f * caveIntensity;
                    }
                }
                // MID-DEPTH CAVES (y=30 to upper caves) - Major tunnel systems
                else if (y > 30)
                {
                    float worm = (c1 + c2) * 0.5f;
                    if (worm > 0.08f)
                    {
                        density -= (worm - 0.08f) * 150.0f * caveIntensity;
                    }

                    if (c3 > 0.2f)
                    {
                        density -= (c3 - 0.2f) * 40.0f * caveIntensity;
                    }

                    if (cL > 0.35f)
                    {
                        density -= (cL - 0.35f) * 80.0f * caveIntensity;
                    }
                }
                // DEEP CAVES (y=0 to 30) - Large interconnected systems
                else if (y > 0)
                {
                    float worm = (c1 + c2) * 0.5f;
                    if (worm > 0.05f)
                    {
                        density -= (worm - 0.05f) * 400.0f * caveIntensity;
                    }

                    if (c3 > 0.18f)
                    {
                        density -= (c3 - 0.18f) * 80.0f * caveIntensity;
                    }

                    float megaCavern = c1 * c2;
                    if (megaCavern > 0.12f)
                    {
                        density -= (megaCavern - 0.12f) * 50000.0f * caveIntensity;
                    }

                    if (cL > 0.28f)
                    {
                        density -= (cL - 0.28f) * 180.0f * caveIntensity;
                    }
                }
                // ULTRA-DEEP CAVES (y=-64 to 0) - MASSIVE ancient chambers
                else
                {
                    // Very aggressive tunnels
                    float worm = (c1 + c2) * 0.5f;
                    if (worm > 0.02f)
                    {
                        density -= (worm - 0.02f) * 800.0f * caveIntensity;
                    }

                    // Enormous caverns
                    if (c3 > 0.12f)
                    {
                        density -= (c3 - 0.12f) * 150.0f * caveIntensity;
                    }

                    // COLOSSAL chambers
                    float megaCavern = c1 * c2;
                    if (megaCavern > 0.06f)
                    {
                        density -= (megaCavern - 0.06f) * 100000.0f * caveIntensity;
                    }

                    // Massive cellular caverns
                    if (cL > 0.2f)
                    {
                        density -= (cL - 0.2f) * 400.0f * caveIntensity;
                    }

                    // Deepest abyss (y < -32) - Void-like spaces
                    if (y < -32)
                    {
                        float abyss = (c1 + c3) * 0.5f;
                        if (abyss > 0.08f)
                        {
                            density -= (abyss - 0.08f) * 1200.0f * caveIntensity;
                        }
                    }
                }
            }

            // Bedrock - ensure it's always solid at the bottom
            if (y <= -64)
            {
                density += (-63 - y) * 100f;
            }

            return density;
        }

        private static float GetCaveIntensity(Biome biome)
        {
            return biome switch
            {
                Biome.Plains => 1.2f,    // Increased
                Biome.Forest => 1.0f,    // Increased
                Biome.Desert => 0.8f,    // Increased
                Biome.Mountains => 0.6f, // Increased - mountains are VERY cavernous
                _ => 1.0f
            };
        }

        // FEATURE PLACEMENT HELPERS (examples for trees, structures, etc.)
        
        /*
        /// <summary>
        /// Check if position should be a tree trunk
        /// </summary>
        private static bool IsTreeTrunk(int x, int y, int z)
        {
            // Only in forest biome
            if (GetBiome(x, z) != Biome.Forest)
                return false;

            // Check if this column has a tree
            float treeValue = treeNoise.GetNoise(x, z);
            if (treeValue < 0.7f) // Tree placement threshold
                return false;

            // Check if we're in the trunk height range
            int surfaceY = GetSurfaceY(x, z);
            int treeHeight = 5 + ((int)(treeValue * 10) % 4); // 5-8 blocks tall
            
            return y > surfaceY && y <= surfaceY + treeHeight;
        }

        /// <summary>
        /// Check if position should be tree leaves
        /// </summary>
        private static bool IsTreeLeaves(int x, int y, int z)
        {
            // Find nearest tree trunk and check distance
            // Implementation depends on your tree shape design
            // This is a simplified example
            
            return false; // Implement based on your tree design
        }
        */

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
