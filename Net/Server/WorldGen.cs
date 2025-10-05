using System;
using System.Runtime.CompilerServices;
using static FastNoiseLite;

namespace Aetheris
{
    public static class WorldGen
    {
        private static FastNoiseLite terrainNoise;
        private static FastNoiseLite biomeNoise;
        private static FastNoiseLite caveNoise1;
        private static FastNoiseLite caveNoise2;
        private static FastNoiseLite caveLarge;

        private const float ISO = 0.5f;
        private static bool initialized = false;

        public struct ColumnData
        {
            public Biome Biome;           // dominant biome (for legacy usage)
            public float BaseHeight;      // blended base height
            public float Amplitude;       // blended amplitude
            public float SurfaceY;        // computed surface Y
            public int DirtDepth;         // blended dirt depth (integer)
            public float CaveIntensity;   // blended cave intensity
        }

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

            biomeNoise = new FastNoiseLite(seed);
            biomeNoise.SetNoiseType(NoiseType.Cellular);
            biomeNoise.SetFrequency(0.001f);
            biomeNoise.SetCellularReturnType(CellularReturnType.CellValue);

            terrainNoise = new FastNoiseLite(seed + 1);
            terrainNoise.SetNoiseType(NoiseType.OpenSimplex2);
            terrainNoise.SetFrequency(0.008f);
            terrainNoise.SetFractalOctaves(4);

            caveNoise1 = new FastNoiseLite(seed + 2);
            caveNoise1.SetNoiseType(NoiseType.OpenSimplex2);
            caveNoise1.SetFrequency(0.018f);

            caveNoise2 = new FastNoiseLite(seed + 3);
            caveNoise2.SetNoiseType(NoiseType.OpenSimplex2);
            caveNoise2.SetFrequency(0.025f);

            caveLarge = new FastNoiseLite(seed + 4);
            caveLarge.SetNoiseType(NoiseType.Cellular);
            caveLarge.SetFrequency(0.01f);
            caveLarge.SetCellularReturnType(CellularReturnType.Distance);

            initialized = true;
        }

        // --- Helper: smooth biome blending (returns normalized weights for each biome) ---
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GetBiomeWeights(float biomeValue, out float wPlains, out float wForest, out float wDesert, out float wMountains)
        {
            // centers chosen to match your previous thresholds (-0.5, 0.0, 0.5)
            const float cPlains = -0.75f;
            const float cForest = -0.25f;
            const float cDesert = 0.25f;
            const float cMountains = 0.75f;
            const float width = 0.35f; // how wide the blending is (bigger => smoother transitions)

            float rawPlains = MathF.Max(0f, 1f - MathF.Abs(biomeValue - cPlains) / width);
            float rawForest = MathF.Max(0f, 1f - MathF.Abs(biomeValue - cForest) / width);
            float rawDesert = MathF.Max(0f, 1f - MathF.Abs(biomeValue - cDesert) / width);
            float rawMountains = MathF.Max(0f, 1f - MathF.Abs(biomeValue - cMountains) / width);

            float sum = rawPlains + rawForest + rawDesert + rawMountains;
            if (sum <= 0f)
            {
                // fallback: pick nearest center
                float dP = MathF.Abs(biomeValue - cPlains);
                float dF = MathF.Abs(biomeValue - cForest);
                float dD = MathF.Abs(biomeValue - cDesert);
                float dM = MathF.Abs(biomeValue - cMountains);
                if (dP <= dF && dP <= dD && dP <= dM) { wPlains = 1; wForest = wDesert = wMountains = 0; return; }
                if (dF <= dP && dF <= dD && dF <= dM) { wForest = 1; wPlains = wDesert = wMountains = 0; return; }
                if (dD <= dP && dD <= dF && dD <= dM) { wDesert = 1; wPlains = wForest = wMountains = 0; return; }
                wMountains = 1; wPlains = wForest = wDesert = 0; return;
            }

            wPlains = rawPlains / sum;
            wForest = rawForest / sum;
            wDesert = rawDesert / sum;
            wMountains = rawMountains / sum;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GetBiomeParams(Biome biome, out float baseHeight, out float amplitude)
        {
            switch (biome)
            {
                case Biome.Plains:
                    baseHeight = 30f; amplitude = 6f; break;
                case Biome.Forest:
                    baseHeight = 35f; amplitude = 10f; break;
                case Biome.Desert:
                    baseHeight = 28f; amplitude = 5f; break;
                case Biome.Mountains:
                    baseHeight = 50f; amplitude = 35f; break;
                default:
                    baseHeight = 30f; amplitude = 10f; break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float GetCaveIntensity(Biome biome)
        {
            return biome switch
            {
                Biome.Plains => 1.2f,
                Biome.Forest => 1.0f,
                Biome.Desert => 0.8f,
                Biome.Mountains => 0.6f,
                _ => 1.0f
            };
        }

        // --- Blended ColumnData instead of sharp enum switching ---
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ColumnData GetColumnData(int x, int z)
        {
            float biomeValue = biomeNoise.GetNoise(x, z);

            // get blending weights for each biome
            GetBiomeWeights(biomeValue, out float wPlains, out float wForest, out float wDesert, out float wMountains);

            // blended baseHeight & amplitude
            float basePlains = 30f; float ampPlains = 6f;
            float baseForest = 35f; float ampForest = 10f;
            float baseDesert = 28f; float ampDesert = 5f;
            float baseMount = 50f; float ampMount = 35f;

            float baseHeight = basePlains * wPlains + baseForest * wForest + baseDesert * wDesert + baseMount * wMountains;
            float amplitude = ampPlains * wPlains + ampForest * wForest + ampDesert * wDesert + ampMount * wMountains;

            // get surface noise and compute surface Y (this naturally blends because biome params are blended)
            float surfaceNoise = terrainNoise.GetNoise(x, z);
            float surfaceY = baseHeight + surfaceNoise * amplitude;

            // blended integer dirt depth & cave intensity
            float ddP = GetDirtDepth(Biome.Plains);
            float ddF = GetDirtDepth(Biome.Forest);
            float ddD = GetDirtDepth(Biome.Desert);
            float ddM = GetDirtDepth(Biome.Mountains);
            float dirtDepthF = ddP * wPlains + ddF * wForest + ddD * wDesert + ddM * wMountains;
            int dirtDepth = Math.Max(1, (int)MathF.Round(dirtDepthF));

            float ciP = GetCaveIntensity(Biome.Plains);
            float ciF = GetCaveIntensity(Biome.Forest);
            float ciD = GetCaveIntensity(Biome.Desert);
            float ciM = GetCaveIntensity(Biome.Mountains);
            float caveIntensity = ciP * wPlains + ciF * wForest + ciD * wDesert + ciM * wMountains;

            // pick dominant biome for the legacy Biome field
            Biome dominant = Biome.Plains;
            float maxW = wPlains;
            if (wForest > maxW) { dominant = Biome.Forest; maxW = wForest; }
            if (wDesert > maxW) { dominant = Biome.Desert; maxW = wDesert; }
            if (wMountains > maxW) { dominant = Biome.Mountains; maxW = wMountains; }

            return new ColumnData
            {
                Biome = dominant,
                BaseHeight = baseHeight,
                Amplitude = amplitude,
                SurfaceY = surfaceY,
                DirtDepth = dirtDepth,
                CaveIntensity = caveIntensity
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsExposedToAir(int x, int y, int z, in ColumnData columnData)
        {
            if (y >= columnData.SurfaceY - 1)
                return true;

            for (int dy = 1; dy <= 3; dy++)
            {
                if (SampleDensityFast(x, y + dy, z, columnData) <= ISO)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Improved cave generation with normalization, attenuation on peaks, smaller multipliers and blending-based biome params.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SampleDensityFast(int x, int y, int z, in ColumnData columnData)
        {
            float density;
            if (y > columnData.SurfaceY)
            {
                density = ISO - (y - columnData.SurfaceY) * 0.2f;
            }
            else
            {
                float depthBelowSurface = columnData.SurfaceY - y;
                density = ISO + 2.0f + (depthBelowSurface * 0.02f);
            }

            // skip caves well above the surface
            if (y > columnData.SurfaceY + 5)
                return density;

            // If biome says caves should be disabled (intensity near zero), skip
            if (columnData.CaveIntensity <= 0.01f)
                return density;

            // Cave carving region
            if (y > -150 && y < columnData.SurfaceY + 3)
            {
                // get noise values
                float r1 = caveNoise1.GetNoise(x, y, z); // approx [-1,1]
                float r2 = caveNoise2.GetNoise(x, y, z); // approx [-1,1]
                float rL = caveLarge.GetNoise(x, y, z);  // cellular distance or similar

                // normalize to 0..1 in a conservative way
                float n1 = (r1 + 1f) * 0.5f; // 0..1
                float n2 = (r2 + 1f) * 0.5f; // 0..1

                // caveLarge can be cellular-distance; clamp and scale it into 0..1 range
                // Using a soft mapping: small distances -> small; large distances -> approach 1
                float nL = MathF.Abs(rL);
                nL = nL / (nL + 1f); // maps [0,inf) -> [0,1)
                nL = Clamp(nL, 0f, 1f);

                float worm = (n1 + n2) * 0.5f;
                float megaCavern = n1 * n2;

                // Attenuate cave intensity for high surface columns (shallow mountain peaks should have far fewer caves)
                // SurfaceY around 45 is typical; above that -> attenuate more.
                float elevationAtten = 1f;
                if (columnData.SurfaceY > 45f)
                {
                    // linear attenuation from 45..75 surface Y -> 1.0..0.15
                    elevationAtten = 1f - Clamp((columnData.SurfaceY - 45f) / 30f, 0f, 1f) * 0.85f;
                }
                float caveFactor = MathF.Max(0.05f, columnData.CaveIntensity * elevationAtten); // never fully zero unless intensity is 0

                // Conservative caps so no single branch can subtract enormous values
                const float maxSubtract = 40f; // absolute clamp of how much density can be removed by caves at one sample

                // Surface entrances: rarer and smaller
                if (y > columnData.SurfaceY - 10)
                {
                    if (worm > 0.22f)
                    {
                        float sub = (worm - 0.22f) * 12.0f * caveFactor;
                        density -= MathF.Min(sub, maxSubtract * 0.25f);
                    }
                }
                // Upper caves (0 .. surface-10)
                else if (y > 0)
                {
                    if (worm > 0.12f)
                    {
                        float sub = (worm - 0.12f) * 28.0f * caveFactor;
                        density -= MathF.Min(sub, maxSubtract * 0.5f);
                    }
                    // Large chambers
                    if (nL > 0.45f)
                    {
                        float sub = (nL - 0.45f) * 60.0f * caveFactor;
                        density -= MathF.Min(sub, maxSubtract * 0.6f);
                    }
                }
                // Mid-depth caves (0 .. -75)
                else if (y > -75)
                {
                    if (worm > 0.08f)
                    {
                        float sub = (worm - 0.08f) * 60.0f * caveFactor;
                        density -= MathF.Min(sub, maxSubtract);
                    }

                    // Mega caverns (rarer but not catastrophically scaled)
                    if (megaCavern > 0.18f)
                    {
                        float sub = (megaCavern - 0.18f) * 400.0f * caveFactor;
                        density -= MathF.Min(sub, maxSubtract * 2f); // allow slightly larger mid-depth caves
                    }

                    if (nL > 0.35f)
                    {
                        float sub = (nL - 0.35f) * 80.0f * caveFactor;
                        density -= MathF.Min(sub, maxSubtract);
                    }
                }
                // Deep caves (-75 .. -150)
                else
                {
                    if (worm > 0.04f)
                    {
                        float sub = (worm - 0.04f) * 120.0f * caveFactor;
                        density -= MathF.Min(sub, maxSubtract * 1.25f);
                    }

                    if (megaCavern > 0.08f)
                    {
                        float sub = (megaCavern - 0.08f) * 1200.0f * caveFactor;
                        density -= MathF.Min(sub, maxSubtract * 3f);
                    }

                    if (nL > 0.28f)
                    {
                        float sub = (nL - 0.28f) * 200.0f * caveFactor;
                        density -= MathF.Min(sub, maxSubtract * 1.5f);
                    }

                    // Deep abyss formations but made rarer and capped
                    if (y < -100)
                    {
                        float abyss = (n1 + nL) * 0.5f;
                        if (abyss > 0.18f)
                        {
                            float sub = (abyss - 0.18f) * 400.0f * caveFactor;
                            density -= MathF.Min(sub, maxSubtract * 2f);
                        }
                    }
                }
            }

            // Limit how low density can go due to cave carving so we don't wipe out entire columns
            density = MathF.Max(density, ISO - 45f);

            // Bedrock clamp
            if (y <= -150)
                density += (-149 - y) * 100f;

            return density;
        }

        public static BlockType GetBlockType(int x, int y, int z, float density, in ColumnData columnData)
        {
            if (density <= ISO)
                return BlockType.Air;

            if (y <= 2)
                return BlockType.Stone;

            int depthBelowSurface = (int)columnData.SurfaceY - y;
            bool isExposed = IsExposedToAir(x, y, z, columnData);

            // Surface layer
            if (isExposed)
            {
                return columnData.Biome switch
                {
                    Biome.Plains => BlockType.Grass,
                    Biome.Forest => BlockType.Grass,
                    Biome.Desert => BlockType.Sand,
                    Biome.Mountains => (columnData.SurfaceY > 45) ? BlockType.Snow : BlockType.Stone,
                    _ => BlockType.Grass
                };
            }

            // Subsurface layer
            if (depthBelowSurface > 0 && depthBelowSurface <= columnData.DirtDepth)
            {
                return columnData.Biome switch
                {
                    Biome.Plains => BlockType.Dirt,
                    Biome.Forest => BlockType.Dirt,
                    Biome.Desert => BlockType.Sand,
                    Biome.Mountains => BlockType.Stone,
                    _ => BlockType.Dirt
                };
            }

            // Underground features - more gravel in deep areas
            if (y < 15 && ((x + y + z) % 7) == 0)
                return BlockType.Gravel;

            if (y < -50 && ((x + y + z) % 12) == 0)
                return BlockType.Gravel;

            return BlockType.Stone;
        }

        // Legacy overloads
        public static BlockType GetBlockType(int x, int y, int z, float density)
        {
            var columnData = GetColumnData(x, z);
            return GetBlockType(x, y, z, density, columnData);
        }

        public static BlockType GetBlockType(int x, int y, int z)
        {
            var columnData = GetColumnData(x, z);
            float density = SampleDensityFast(x, y, z, columnData);
            return GetBlockType(x, y, z, density, columnData);
        }

        public static float SampleDensity(int x, int y, int z)
        {
            if (!initialized) Initialize();
            var columnData = GetColumnData(x, z);
            return SampleDensityFast(x, y, z, columnData);
        }

        public static bool IsSolid(int x, int y, int z)
        {
            return SampleDensity(x, y, z) > ISO;
        }

        public static void PrintBiomeAt(int x, int z)
        {
            var columnData = GetColumnData(x, z);
            Console.WriteLine($"Biome at ({x}, {z}) is {columnData.Biome} (surfaceY={columnData.SurfaceY:0.0})");
        }

        public static float Clamp(float value, float min, float max)
             => value < min ? min : (value > max ? max : value);
    }
}
