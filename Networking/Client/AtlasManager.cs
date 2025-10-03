// File: AetherisClient/Rendering/AtlasManager.cs
using System;
using System.IO;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Text.Json;

namespace AetherisClient.Rendering
{
    public enum BlockType
    {
        Air = 0,
        Stone = 1,
        Dirt = 2,
        Grass = 3,
        Sand = 4,
        Snow = 5,
        Gravel = 6,
        Wood = 7,
        Leaves = 8
    }

    public static class AtlasManager
    {
        public static int AtlasTextureId { get; private set; } = 0;
        public static int AtlasWidth { get; private set; } = 0;
        public static int AtlasHeight { get; private set; } = 0;
        public static int TileSize { get; private set; } = 64;
        public static int TilesPerRow { get; private set; } = 0;
        public static int TilesPerCol { get; private set; } = 0;
        public static bool IsLoaded => AtlasTextureId != 0 && AtlasWidth > 0;

        // CRITICAL FIX: Add the missing Padding constant
        private const float Padding = 0.002f; // 0.2% padding to prevent bleeding

        public static bool FlipV { get; set; } = false;

        private static readonly Dictionary<BlockType, int> DefaultBlockToTile = new()
        {
            [BlockType.Stone] = 0,
            [BlockType.Dirt] = 1,
            [BlockType.Grass] = 2,
            [BlockType.Sand] = 3,
            [BlockType.Snow] = 4,
            [BlockType.Gravel] = 5,
            [BlockType.Wood] = 6,
            [BlockType.Leaves] = 7
        };

        private static Dictionary<BlockType, int> blockToTile = new(DefaultBlockToTile);

        public static void LoadAtlas(string atlasPath, int preferredTileSize = 64)
        {
            if (!File.Exists(atlasPath))
            {
                Console.WriteLine($"[AtlasManager] Atlas file not found: {atlasPath}");
                return;
            }

            try
            {
                using (var image = Image.Load<Rgba32>(atlasPath))
                {
                    AtlasWidth = image.Width;
                    AtlasHeight = image.Height;
                    Console.WriteLine($"[AtlasManager] Loaded image: {atlasPath} ({AtlasWidth}x{AtlasHeight})");

                    TileSize = DetectTileSize(AtlasWidth, AtlasHeight, DefaultBlockToTile.Count, preferredTileSize);
                    TilesPerRow = Math.Max(1, AtlasWidth / TileSize);
                    TilesPerCol = Math.Max(1, AtlasHeight / TileSize);

                    Console.WriteLine($"[AtlasManager] Detected: tileSize={TileSize}, tilesPerRow={TilesPerRow}, tilesPerCol={TilesPerCol}");

                    var pixels = new byte[AtlasWidth * AtlasHeight * 4];

                    image.ProcessPixelRows(accessor =>
                    {
                        for (int y = 0; y < AtlasHeight; y++)
                        {
                            Span<Rgba32> row = accessor.GetRowSpan(y);
                            for (int x = 0; x < AtlasWidth; x++)
                            {
                                Rgba32 px = row[x];
                                int idx = (y * AtlasWidth + x) * 4;
                                pixels[idx + 0] = px.R;
                                pixels[idx + 1] = px.G;
                                pixels[idx + 2] = px.B;
                                pixels[idx + 3] = px.A;
                            }
                        }
                    });

                    if (AtlasTextureId != 0)
                    {
                        GL.DeleteTexture(AtlasTextureId);
                    }

                    AtlasTextureId = GL.GenTexture();
                    GL.BindTexture(TextureTarget.Texture2D, AtlasTextureId);
                    GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

                    GL.TexImage2D(
                        TextureTarget.Texture2D,
                        0,
                        PixelInternalFormat.Rgba,
                        AtlasWidth,
                        AtlasHeight,
                        0,
                        PixelFormat.Rgba,
                        PixelType.UnsignedByte,
                        pixels
                    );

                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

                    GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
                    GL.BindTexture(TextureTarget.Texture2D, 0);

                    Console.WriteLine($"[AtlasManager] Successfully uploaded to GL texture {AtlasTextureId}");

                    GL.BindTexture(TextureTarget.Texture2D, AtlasTextureId);
                    GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureWidth, out int w);
                    GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureHeight, out int h);
                    Console.WriteLine($"[AtlasManager] Verified texture dimensions: {w}x{h}");
                    GL.BindTexture(TextureTarget.Texture2D, 0);
                }

                TryLoadOptionalMapping(atlasPath + ".json");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AtlasManager] Failed to load atlas: {ex.Message}");
                Console.WriteLine($"[AtlasManager] Stack trace: {ex.StackTrace}");
                AtlasTextureId = 0;
                AtlasWidth = 0;
                AtlasHeight = 0;
            }
        }

        public static void UnloadAtlas()
        {
            if (AtlasTextureId != 0)
            {
                GL.DeleteTexture(AtlasTextureId);
                AtlasTextureId = 0;
            }
            AtlasWidth = AtlasHeight = TilesPerRow = TilesPerCol = 0;
            TileSize = 64;
            blockToTile = new Dictionary<BlockType, int>(DefaultBlockToTile);
        }

        public static (float uMin, float vMin, float uMax, float vMax) GetAtlasUV(BlockType blockType)
        {
            if (!IsLoaded) return (0f, 0f, 0.25f, 0.25f);

            // Get tile index from block type mapping
            if (!blockToTile.TryGetValue(blockType, out int tileIndex))
            {
                // Fallback for unmapped types
                tileIndex = blockType == BlockType.Air ? 0 : (int)blockType - 1;
            }

            int tx = tileIndex % TilesPerRow;
            int ty = tileIndex / TilesPerRow;

            float tileU = (float)TileSize / AtlasWidth;
            float tileV = (float)TileSize / AtlasHeight;

            float uMin = tx * tileU + Padding;
            float vMin = ty * tileV + Padding;
            float uMax = (tx + 1) * tileU - Padding;
            float vMax = (ty + 1) * tileV - Padding;

            return (uMin, vMin, uMax, vMax);
        }

        public static void SetBlockToTileMap(Dictionary<BlockType, int> map)
        {
            if (map == null) return;
            blockToTile = new Dictionary<BlockType, int>(map);
        }

        private static void TryLoadOptionalMapping(string jsonPath)
        {
            try
            {
                if (!File.Exists(jsonPath)) return;

                string json = File.ReadAllText(jsonPath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                if (dict == null) return;

                var map = new Dictionary<BlockType, int>();
                foreach (var kv in dict)
                {
                    if (Enum.TryParse<BlockType>(kv.Key, true, out var bt))
                    {
                        map[bt] = kv.Value;
                    }
                }

                if (map.Count > 0)
                {
                    SetBlockToTileMap(map);
                    Console.WriteLine($"[AtlasManager] Loaded {map.Count} tile mappings from {jsonPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AtlasManager] Failed to read mapping: {ex.Message}");
            }
        }

        private static int DetectTileSize(int width, int height, int knownTileCount, int preferredTileSize)
        {
            if (preferredTileSize > 0 &&
                width % preferredTileSize == 0 &&
                height % preferredTileSize == 0)
            {
                int tiles = (width / preferredTileSize) * (height / preferredTileSize);
                if (tiles >= knownTileCount)
                {
                    Console.WriteLine($"[AtlasManager] Using preferred tile size: {preferredTileSize}");
                    return preferredTileSize;
                }
            }

            int[] candidates = { 256, 128, 64, 32, 16 };
            foreach (var size in candidates)
            {
                if (width % size == 0 && height % size == 0)
                {
                    int tiles = (width / size) * (height / size);
                    if (tiles >= knownTileCount)
                    {
                        Console.WriteLine($"[AtlasManager] Auto-detected tile size: {size}");
                        return size;
                    }
                }
            }

            int gcd = Gcd(width, height);
            Console.WriteLine($"[AtlasManager] Using GCD tile size: {gcd}");
            return Math.Max(gcd, 16);
        }

        private static int Gcd(int a, int b)
        {
            while (b != 0)
            {
                int t = b;
                b = a % b;
                a = t;
            }
            return Math.Abs(a);
        }
    }
}
