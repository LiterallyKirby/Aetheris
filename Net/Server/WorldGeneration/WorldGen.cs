using System;
using System.Runtime.CompilerServices;
using static FastNoiseLite;

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using System.Collections.Generic;


namespace Aetheris
{
    public static class WorldGen
    {
        private static FastNoiseLite terrainNoise;
        private static FastNoiseLite biomeNoise;
        private static FastNoiseLite caveNoise1;
        private static FastNoiseLite caveNoise2;
        private static FastNoiseLite caveLarge;
        private static FastNoiseLite biomeBlendNoise;
        private static FastNoiseLite terrainDetailNoise;
        private static readonly object modificationLock = new object();
        private const float ISO = 0.5f;
        private const float EPSILON = 0.0001f;  // For numerical stability
        private static bool initialized = false;

        public struct ColumnData
        {
            public Biome Biome;
            public float BaseHeight;
            public float Amplitude;
            public float SurfaceY;
            public int DirtDepth;
            public float CaveIntensity;
            public float Moisture;
        }
        private static ConcurrentDictionary<(int, int, int), BlockType> modifiedBlocks = new();
        private static bool enableModifications = false;

        public static void SetModificationsEnabled(bool enabled)
        {
            enableModifications = enabled;
        }
        public static void SetBlock(int x, int y, int z, BlockType blockType)
        {
            modifiedBlocks[(x, y, z)] = blockType;
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

            int seed = ServerConfig.WORLD_SEED;

            biomeNoise = new FastNoiseLite(seed);
            biomeNoise.SetNoiseType(NoiseType.Cellular);
            biomeNoise.SetFrequency(0.0006f);
            biomeNoise.SetCellularReturnType(CellularReturnType.CellValue);

            biomeBlendNoise = new FastNoiseLite(seed + 10);
            biomeBlendNoise.SetNoiseType(NoiseType.OpenSimplex2);
            biomeBlendNoise.SetFrequency(0.002f);

            terrainNoise = new FastNoiseLite(seed + 1);
            terrainNoise.SetNoiseType(NoiseType.OpenSimplex2);
            terrainNoise.SetFrequency(0.005f);
            terrainNoise.SetFractalType(FractalType.FBm);
            terrainNoise.SetFractalOctaves(5);
            terrainNoise.SetFractalLacunarity(2.0f);
            terrainNoise.SetFractalGain(0.5f);

            terrainDetailNoise = new FastNoiseLite(seed + 11);
            terrainDetailNoise.SetNoiseType(NoiseType.OpenSimplex2);
            terrainDetailNoise.SetFrequency(0.02f);

            caveNoise1 = new FastNoiseLite(seed + 2);
            caveNoise1.SetNoiseType(NoiseType.OpenSimplex2);
            caveNoise1.SetFrequency(0.015f);
            caveNoise1.SetFractalType(FractalType.FBm);
            caveNoise1.SetFractalOctaves(2);

            caveNoise2 = new FastNoiseLite(seed + 3);
            caveNoise2.SetNoiseType(NoiseType.OpenSimplex2);
            caveNoise2.SetFrequency(0.02f);
            caveNoise2.SetFractalType(FractalType.FBm);
            caveNoise2.SetFractalOctaves(2);

            caveLarge = new FastNoiseLite(seed + 4);
            caveLarge.SetNoiseType(NoiseType.Cellular);
            caveLarge.SetFrequency(0.008f);
            caveLarge.SetCellularReturnType(CellularReturnType.Distance);

            initialized = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GetBiomeWeights(float biomeValue, float blendNoise, out float wPlains, out float wForest, out float wDesert, out float wMountains)
        {
            biomeValue += blendNoise * 0.15f;

            const float cPlains = -0.8f;
            const float cForest = -0.2f;
            const float cDesert = 0.3f;
            const float cMountains = 0.85f;
            const float width = 0.5f;

            float HermiteBlend(float dist, float w)
            {
                if (dist >= w) return 0f;
                float t = 1f - (dist / w);
                return t * t * (3f - 2f * t);
            }

            float rawPlains = HermiteBlend(MathF.Abs(biomeValue - cPlains), width);
            float rawForest = HermiteBlend(MathF.Abs(biomeValue - cForest), width);
            float rawDesert = HermiteBlend(MathF.Abs(biomeValue - cDesert), width);
            float rawMountains = HermiteBlend(MathF.Abs(biomeValue - cMountains), width);

            float sum = rawPlains + rawForest + rawDesert + rawMountains;
            if (sum <= EPSILON)
            {
                float dP = MathF.Abs(biomeValue - cPlains);
                float dF = MathF.Abs(biomeValue - cForest);
                float dD = MathF.Abs(biomeValue - cDesert);
                float dM = MathF.Abs(biomeValue - cMountains);
                float minDist = MathF.Min(MathF.Min(dP, dF), MathF.Min(dD, dM));

                wPlains = (dP == minDist) ? 1f : 0f;
                wForest = (dF == minDist) ? 1f : 0f;
                wDesert = (dD == minDist) ? 1f : 0f;
                wMountains = (dM == minDist) ? 1f : 0f;
                return;
            }

            wPlains = rawPlains / sum;
            wForest = rawForest / sum;
            wDesert = rawDesert / sum;
            wMountains = rawMountains / sum;
        }
        private static ConcurrentDictionary<(int, int, int), float> densityModifications = new();
        public static void RemoveBlock(int x, int y, int z, float radius = 1.5f, float strength = 3f)
        {
            lock (modificationLock)
            {
                int iRadius = (int)Math.Ceiling(radius);

                for (int dx = -iRadius; dx <= iRadius; dx++)
                {
                    for (int dy = -iRadius; dy <= iRadius; dy++)
                    {
                        for (int dz = -iRadius; dz <= iRadius; dz++)
                        {
                            int px = x + dx;
                            int py = y + dy;
                            int pz = z + dz;

                            float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

                            if (dist <= radius)
                            {
                                float falloff = 1f - (dist / radius);
                                falloff = falloff * falloff;

                                float reduction = strength * falloff;

                                var key = (px, py, pz);
                                densityModifications.AddOrUpdate(
                                    key,
                                    -reduction,
                                    (k, existing) => existing - reduction
                                );
                            }
                        }
                    }
                }
            }
        }

        public static void AddBlock(int x, int y, int z, float radius = 1.5f, float strength = 3f)
        {
            lock (modificationLock)
            {
                int iRadius = (int)Math.Ceiling(radius);

                for (int dx = -iRadius; dx <= iRadius; dx++)
                {
                    for (int dy = -iRadius; dy <= iRadius; dy++)
                    {
                        for (int dz = -iRadius; dz <= iRadius; dz++)
                        {
                            int px = x + dx;
                            int py = y + dy;
                            int pz = z + dz;

                            float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

                            if (dist <= radius)
                            {
                                float falloff = 1f - (dist / radius);
                                falloff = falloff * falloff;

                                float increase = strength * falloff;

                                var key = (px, py, pz);
                                densityModifications.AddOrUpdate(
                                    key,
                                    increase,
                                    (k, existing) => existing + increase
                                );
                            }
                        }
                    }
                }
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ColumnData GetColumnData(int x, int z)
        {
            float biomeValue = biomeNoise.GetNoise(x, z);
            float blendNoise = biomeBlendNoise.GetNoise(x, z);

            GetBiomeWeights(biomeValue, blendNoise, out float wPlains, out float wForest, out float wDesert, out float wMountains);

            float basePlains = 32f; float ampPlains = 5f;
            float baseForest = 36f; float ampForest = 8f;
            float baseDesert = 30f; float ampDesert = 4f;
            float baseMount = 55f; float ampMount = 28f;

            float baseHeight = basePlains * wPlains + baseForest * wForest + baseDesert * wDesert + baseMount * wMountains;
            float amplitude = ampPlains * wPlains + ampForest * wForest + ampDesert * wDesert + ampMount * wMountains;

            float surfaceNoise = terrainNoise.GetNoise(x, z);
            float detailNoise = terrainDetailNoise.GetNoise(x, z) * 0.15f;

            float surfaceY = baseHeight + (surfaceNoise * amplitude) + (detailNoise * amplitude * 0.3f);

            float ddP = 6f, ddF = 7f, ddD = 4f, ddM = 3f;
            float dirtDepthF = ddP * wPlains + ddF * wForest + ddD * wDesert + ddM * wMountains;
            int dirtDepth = Math.Max(2, (int)MathF.Round(dirtDepthF));

            float ciP = 0.85f, ciF = 0.75f, ciD = 0.6f, ciM = 0.4f;
            float caveIntensity = ciP * wPlains + ciF * wForest + ciD * wDesert + ciM * wMountains;

            float moisture = wPlains * 0.6f + wForest * 0.9f + wDesert * 0.1f + wMountains * 0.5f;

            Biome dominant = Biome.Plains;
            float maxW = wPlains;
            if (wForest > maxW) { dominant = Biome.Forest; maxW = wForest; }
            if (wDesert > maxW) { dominant = Biome.Desert; maxW = wDesert; }
            if (wMountains > maxW) { dominant = Biome.Mountains; }

            return new ColumnData
            {
                Biome = dominant,
                BaseHeight = baseHeight,
                Amplitude = amplitude,
                SurfaceY = surfaceY,
                DirtDepth = dirtDepth,
                CaveIntensity = caveIntensity,
                Moisture = moisture
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsExposedToAir(int x, int y, int z, in ColumnData columnData)
        {
            if (y >= columnData.SurfaceY - 1)
                return true;

            for (int dy = 1; dy <= 2; dy++)
            {
                if (SampleDensityFast(x, y + dy, z, columnData) <= ISO)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Improved cave generation with continuous density field for seamless chunk boundaries
        /// </summary>
        // In WorldGen.cs - Replace the SampleDensityFast method with this version that includes modifications:

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float GetDensityModification(int x, int y, int z)
        {
            lock (modificationLock)
            {
                if (densityModifications.TryGetValue((x, y, z), out float mod))
                    return mod;
                return 0f;
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SampleDensityFast(int x, int y, int z, in ColumnData columnData)
        {
            // Base terrain density with smooth gradient
            float density;
            if (y > columnData.SurfaceY)
            {
                float heightAbove = y - columnData.SurfaceY;
                density = ISO - heightAbove * 0.12f;
            }
            else
            {
                float depthBelowSurface = columnData.SurfaceY - y;
                density = ISO + 1.2f + (depthBelowSurface * 0.012f);
            }

            // Early return for air blocks (with modifications)
            if (y > columnData.SurfaceY + 2)
            {
                return density + GetDensityModification(x, y, z);
            }

            if (columnData.CaveIntensity < 0.05f)
            {
                return density + GetDensityModification(x, y, z);
            }

            // Cave generation code here...
            if (y > -150 && y < columnData.SurfaceY)
            {
                float r1 = caveNoise1.GetNoise(x, y, z);
                float r2 = caveNoise2.GetNoise(x, y, z);
                float rL = caveLarge.GetNoise(x, y, z);

                float n1 = (r1 + 1f) * 0.5f;
                float n2 = (r2 + 1f) * 0.5f;
                float nL = Clamp(MathF.Abs(rL) * 1.2f, 0f, 1f);

                float worm = MathF.Abs(r1) * MathF.Abs(r2);
                float cavern = n1 * n2;

                float depthFactor = 1f;
                if (y > 0)
                {
                    float distFromSurface = columnData.SurfaceY - y;
                    depthFactor = Clamp(distFromSurface / 20f, 0.15f, 1f);
                }

                float elevationAtten = 1f;
                if (columnData.SurfaceY > 50f)
                {
                    float excess = columnData.SurfaceY - 50f;
                    elevationAtten = 1f - Clamp(excess / 35f, 0f, 0.82f);
                }

                float caveFactor = columnData.CaveIntensity * depthFactor * elevationAtten;

                if (y > columnData.SurfaceY - 15)
                {
                    float wormIntensity = SmoothThreshold(worm, 0.08f, 0.02f);
                    density -= wormIntensity * 22f * caveFactor * 0.5f;
                }
                else if (y > 0)
                {
                    float wormIntensity = SmoothThreshold(worm, 0.12f, 0.03f);
                    density -= wormIntensity * 16f * caveFactor;

                    if (cavern > 0.4f && nL > 0.55f)
                    {
                        float chamberIntensity = SmoothThreshold(cavern, 0.5f, 0.05f);
                        density -= chamberIntensity * 32f * caveFactor;
                    }
                }
                else if (y > -80)
                {
                    float wormIntensity = SmoothThreshold(worm, 0.15f, 0.04f);
                    density -= wormIntensity * 22f * caveFactor;

                    float cavernIntensity = SmoothThreshold(cavern, 0.45f, 0.05f);
                    density -= cavernIntensity * 45f * caveFactor;

                    float largeIntensity = SmoothThreshold(nL, 0.55f, 0.05f);
                    density -= largeIntensity * 38f * caveFactor;
                }
                else
                {
                    float wormIntensity = SmoothThreshold(worm, 0.18f, 0.05f);
                    density -= wormIntensity * 28f * caveFactor;

                    float cavernIntensity = SmoothThreshold(cavern, 0.4f, 0.06f);
                    density -= cavernIntensity * 75f * caveFactor;

                    float largeIntensity = SmoothThreshold(nL, 0.5f, 0.06f);
                    density -= largeIntensity * 55f * caveFactor;

                    if (y < -100)
                    {
                        float abyss = (cavern + nL) * 0.5f;
                        float abyssIntensity = SmoothThreshold(abyss, 0.62f, 0.08f);
                        density -= abyssIntensity * 95f * caveFactor;
                    }
                }
            }

            density = MathF.Max(density, ISO - 20f);

            if (y <= -150)
            {
                float bedrockDepth = -150 - y;
                density += bedrockDepth * bedrockDepth * 5f;
            }
            else if (y < -140)
            {
                float approachDepth = -140 - y;
                density += approachDepth * approachDepth * 0.5f;
            }

            // Apply modifications ONCE at the end
            return density + GetDensityModification(x, y, z);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SampleDensity(int x, int y, int z)
        {
            if (!initialized) Initialize();

            var columnData = GetColumnData(x, z);
            // SampleDensityFast already includes modifications
            return SampleDensityFast(x, y, z, columnData);
        }

        /// <summary>
        /// Smooth threshold function that creates continuous transitions
        /// Prevents hard boundaries that cause gaps in marching cubes
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float SmoothThreshold(float value, float threshold, float smoothness)
        {
            if (value < threshold - smoothness) return 0f;
            if (value > threshold + smoothness) return value - threshold;

            // Smooth hermite interpolation in the transition zone
            float t = (value - (threshold - smoothness)) / (2f * smoothness);
            t = Clamp(t, 0f, 1f);
            float smooth = t * t * (3f - 2f * t);

            return smooth * (value - (threshold - smoothness));
        }

        public static BlockType GetBlockType(int x, int y, int z, float density, in ColumnData columnData)
        {
            if (density <= ISO)
                return BlockType.Air;

            if (y <= 1)
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
                    Biome.Mountains => (columnData.SurfaceY > 50) ? BlockType.Snow :
                                       (columnData.SurfaceY > 42) ? BlockType.Stone : BlockType.Grass,
                    _ => BlockType.Grass
                };
            }

            // Subsurface layers
            if (depthBelowSurface > 0 && depthBelowSurface <= columnData.DirtDepth)
            {
                return columnData.Biome switch
                {
                    Biome.Plains => BlockType.Dirt,
                    Biome.Forest => BlockType.Dirt,
                    Biome.Desert => BlockType.Sand,
                    Biome.Mountains => (depthBelowSurface <= 1) ? BlockType.Dirt : BlockType.Stone,
                    _ => BlockType.Dirt
                };
            }

            // Underground variation
            if (y < 20 && ((x * 73 + y * 31 + z * 17) % 19) == 0)
                return BlockType.Gravel;

            if (y < -40 && ((x * 53 + y * 47 + z * 29) % 23) == 0)
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
            // Check modified blocks first
            if (modifiedBlocks.TryGetValue((x, y, z), out var modifiedBlock))
                return modifiedBlock;

            // Fall back to procedural generation
            var columnData = GetColumnData(x, z);
            float density = SampleDensityFast(x, y, z, columnData);
            return GetBlockType(x, y, z, density, columnData);
        }



        public static bool IsSolid(int x, int y, int z)
        {
            return SampleDensity(x, y, z) > ISO;
        }

        public static void PrintBiomeAt(int x, int z)
        {
            var columnData = GetColumnData(x, z);

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Clamp(float value, float min, float max)
             => value < min ? min : (value > max ? max : value);
    }
}
