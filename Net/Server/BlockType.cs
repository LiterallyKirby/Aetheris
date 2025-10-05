using System;

namespace Aetheris
{
    /// <summary>
    /// Block types with texture atlas coordinates
    /// </summary>
    public enum BlockType : byte
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

    public static class BlockTypeExtensions
    {
        // Texture atlas is 4x4 grid (16 textures max for this example)
        // Each texture is at position (u, v) in the atlas
        private const int ATLAS_SIZE = 4;
        private const float TILE_SIZE = 1.0f / ATLAS_SIZE;

        /// <summary>
        /// Get UV coordinates for a block type in the texture atlas
        /// Returns (uMin, vMin, uMax, vMax)
        /// </summary>
        public static (float uMin, float vMin, float uMax, float vMax) GetAtlasUV(this BlockType block)
        {
            // Map block types to atlas positions
            int atlasIndex = block switch
            {
                BlockType.Stone => 0,   // Top-left (0,0)
                BlockType.Dirt => 1,    // (1,0)
                BlockType.Grass => 2,   // (2,0)
                BlockType.Sand => 3,    // (3,0)
                BlockType.Snow => 4,    // (0,1)
                BlockType.Gravel => 5,  // (1,1)
                BlockType.Wood => 6,    // (2,1)
                BlockType.Leaves => 7,  // (3,1)
                _ => 0
            };

            // Calculate UV from atlas index
            int x = atlasIndex % ATLAS_SIZE;
            int y = atlasIndex / ATLAS_SIZE;

            float uMin = x * TILE_SIZE;
            float vMin = y * TILE_SIZE;
            float uMax = uMin + TILE_SIZE;
            float vMax = vMin + TILE_SIZE;

            return (uMin, vMin, uMax, vMax);
        }

        /// <summary>
        /// Get center UV for triplanar mapping
        /// </summary>
        public static (float u, float v) GetAtlasCenter(this BlockType block)
        {
            var (uMin, vMin, uMax, vMax) = block.GetAtlasUV();
            return ((uMin + uMax) * 0.5f, (vMin + vMax) * 0.5f);
        }
    }
}
