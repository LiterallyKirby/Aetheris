using System;
using System.Collections.Concurrent;

namespace Aetheris
{
    /// <summary>
    /// Lightweight chunk manager - chunks are just coordinate metadata.
    /// Actual terrain data comes from WorldGen.SampleDensity() on-demand.
    /// </summary>
    public class ChunkManager
    {
        private readonly ConcurrentDictionary<ChunkCoord, Chunk> chunks = new();

        public Chunk GetOrGenerateChunk(ChunkCoord coord)
        {
            return chunks.GetOrAdd(coord, c => GenerateChunk(c));
        }

        public bool TryGetChunk(ChunkCoord coord, out Chunk? chunk)
        {
            return chunks.TryGetValue(coord, out chunk);
        }

        public void UnloadChunk(ChunkCoord coord)
        {
            chunks.TryRemove(coord, out _);
        }

        private Chunk GenerateChunk(ChunkCoord coord)
        {
            // Calculate world position
            int worldX = coord.X * Config.CHUNK_SIZE;
            int worldY = coord.Y * Config.CHUNK_SIZE_Y;
            int worldZ = coord.Z * Config.CHUNK_SIZE;

            // Create chunk with position metadata
            // We don't need to fill the Blocks array anymore!
            // MarchingCubes reads directly from WorldGen.SampleDensity()
            var chunk = new Chunk(worldX, worldY, worldZ);
            
            return chunk;
        }
    }
}
