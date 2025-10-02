// File: AetherisClient/Rendering/AtlasManager.cs
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OpenTK.Graphics.OpenGL4;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AetherisClient.Rendering
{
    // Keep BlockType in your shared code; copy/adjust if needed on client side.
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
        Leaves = 8,
        // Add new block types as necessary.
    }

    /// <summary>
    /// Client-only atlas loader + UV helper.
    /// Loads an atlas PNG, auto-detects tile size/rows, uploads GL texture,
    /// and provides block -> uv mapping that adapts to atlas resolution/pack layout.
    /// </summary>
    public static class AtlasManager
    {
        public static int AtlasTextureId { get; private set; } = 0;
        public static int AtlasWidth { get; private set; } = 0;
        public static int AtlasHeight { get; private set; } = 0;
        public static int TileSize { get; private set; } = 64;
        public static int TilesPerRow { get; private set; } = 0;
        public static int TilesPerCol { get; private set; } = 0;
        public static bool IsLoaded => AtlasTextureId != 0;

        /// <summary>
        /// If true, GetAtlasUV will flip the V coordinates (1 - v).
        /// Toggle this to match your shader/image origin convention.
        /// </summary>
        public static bool FlipV { get; set; } = true;

        // Default mapping (BlockType -> tile index row-major). Packs can override via atlas.png.json
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

        /// <summary>
        /// Load an atlas PNG on the client and upload to GL.
        /// If a JSON mapping (atlasPath + ".json") exists, it will remap block indices.
        /// </summary>
        /// <param name="atlasPath">Path to atlas PNG (e.g. "textures/atlas.png")</param>
        /// <param name="preferredTileSize">Optional preferred tile size to try first (e.g. 64)</param>
        public static void LoadAtlas(string atlasPath, int preferredTileSize = 64)
        {
            if (!File.Exists(atlasPath))
            {
                Console.WriteLine($"[AtlasManager] Atlas file not found: {atlasPath}");
                return;
            }

            using (var image = Image.Load<Rgba32>(atlasPath))
            {
                AtlasWidth = image.Width;
                AtlasHeight = image.Height;
                Console.WriteLine($"[AtlasManager] Loaded atlas: {atlasPath} ({AtlasWidth}x{AtlasHeight})");

                // Detect tile size heuristically.
                TileSize = DetectTileSize(AtlasWidth, AtlasHeight, DefaultBlockToTile.Count, preferredTileSize);
                TilesPerRow = Math.Max(1, AtlasWidth / TileSize);
                TilesPerCol = Math.Max(1, AtlasHeight / TileSize);

                Console.WriteLine($"[AtlasManager] tileSize={TileSize}, tilesPerRow={TilesPerRow}, tilesPerCol={TilesPerCol}");

                // Copy pixel data into byte[] (RGBA)
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


                // Upload to GL (replace existing texture if present)
                if (AtlasTextureId != 0)
                {
                    GL.DeleteTexture(AtlasTextureId);
                    AtlasTextureId = 0;
                }

                AtlasTextureId = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, AtlasTextureId);

                // Ensure byte alignment won't screw us if width isn't multiple of 4
                GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                    AtlasWidth, AtlasHeight, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

                // Common settings for pixel-perfect block textures
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

                // restore default alignment
                GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);

                GL.BindTexture(TextureTarget.Texture2D, 0);

                Console.WriteLine($"[AtlasManager] Uploaded atlas as GL texture {AtlasTextureId}");
            }

            // Attempt to load a mapping JSON next to atlas (atlas.png.json)
            TryLoadOptionalMapping(atlasPath + ".json");
        }

        /// <summary>
        /// Unload the currently loaded atlas (GL resource freed).
        /// </summary>
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

        /// <summary>
        /// Get UV coordinates for the given BlockType in the currently loaded atlas.
        /// Throws if atlas not loaded.
        /// </summary>
        public static (float uMin, float vMin, float uMax, float vMax) GetAtlasUV(BlockType type)
        {
            if (AtlasWidth == 0 || AtlasHeight == 0 || AtlasTextureId == 0)
                throw new InvalidOperationException("Atlas not loaded. Call AtlasManager.LoadAtlas(...) before requesting UVs.");

            int tileIndex = blockToTile.TryGetValue(type, out var t) ? t : 0;

            int tx = tileIndex % TilesPerRow;
            int ty = tileIndex / TilesPerRow;

            // half-texel bias to reduce bleeding
            float halfTexelU = 0.5f / AtlasWidth;
            float halfTexelV = 0.5f / AtlasHeight;

            float uMin = (tx * TileSize + halfTexelU) / (float)AtlasWidth;
            float vMin = (ty * TileSize + halfTexelV) / (float)AtlasHeight;
            float uMax = ((tx + 1) * TileSize - halfTexelU) / (float)AtlasWidth;
            float vMax = ((ty + 1) * TileSize - halfTexelV) / (float)AtlasHeight;

            if (FlipV)
            {
                // flip V to match OpenGL bottom-left convention if needed
                return (uMin, 1f - vMax, uMax, 1f - vMin);
            }

            return (uMin, vMin, uMax, vMax);
        }

        /// <summary>
        /// Optional: allow runtime override of block->tile mapping (e.g. pack metadata).
        /// </summary>
        public static void SetBlockToTileMap(Dictionary<BlockType, int> map)
        {
            if (map == null) return;
            blockToTile = new Dictionary<BlockType, int>(map);
        }

        // ----------------- Internal helpers -----------------

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
                    else if (int.TryParse(kv.Key, out var numeric) && Enum.IsDefined(typeof(BlockType), numeric))
                    {
                        map[(BlockType)numeric] = kv.Value;
                    }
                }

                if (map.Count > 0)
                {
                    SetBlockToTileMap(map);
                    Console.WriteLine($"[AtlasManager] Loaded tile mapping from {jsonPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AtlasManager] Failed to read mapping file: {ex.Message}");
            }
        }

        /// <summary>
        /// Heuristic detection for tile size. Tries preferred size, common POW2 sizes
        /// and divisors; ensures enough tiles exist for known tile count.
        /// </summary>
        private static int DetectTileSize(int width, int height, int knownTileCount, int preferredTileSize)
        {
            if (preferredTileSize > 0 && width % preferredTileSize == 0 && height % preferredTileSize == 0)
                return preferredTileSize;

            int[] candidates = new[] { 1024, 512, 256, 128, 64, 32, 16, 8 };
            foreach (var c in candidates)
            {
                if (c > 0 && width % c == 0 && height % c == 0)
                {
                    int tiles = (width / c) * (height / c);
                    if (tiles >= knownTileCount) return c;
                }
            }

            int maxCheck = Math.Min(width, height);
            for (int d = Math.Min(maxCheck, 512); d >= 8; d--)
            {
                if (width % d == 0 && height % d == 0)
                {
                    int tiles = (width / d) * (height / d);
                    if (tiles >= knownTileCount) return d;
                }
            }

            int gcd = Gcd(width, height);
            if (gcd >= 8) return gcd;
            return 64; // last resort
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
