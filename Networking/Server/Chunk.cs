using System;

namespace Aetheris
{
    // Shared chunk format used by server and client.
    public class Chunk
    {
        public static readonly int SizeX = Config.CHUNK_SIZE;
        public static readonly int SizeY = Config.CHUNK_SIZE_Y;
        public static readonly int SizeZ = Config.CHUNK_SIZE;

        public static readonly int TotalSize = SizeX * SizeY * SizeZ;

        public byte[,,] Blocks;

        public Chunk()
        {
            Blocks = new byte[SizeX, SizeY, SizeZ];
        }

        public byte[] ToBytes()
        {
            var flat = new byte[TotalSize];
            int i = 0;
            for (int x = 0; x < SizeX; x++)
                for (int y = 0; y < SizeY; y++)
                    for (int z = 0; z < SizeZ; z++)
                        flat[i++] = Blocks[x, y, z];
            return flat;
        }

        public static Chunk FromBytes(byte[] data)
        {
            if (data.Length != TotalSize) throw new ArgumentException($"Chunk.FromBytes: bad length {data.Length}, expected {TotalSize}");
            var c = new Chunk();
            int i = 0;
            for (int x = 0; x < SizeX; x++)
                for (int y = 0; y < SizeY; y++)
                    for (int z = 0; z < SizeZ; z++)
                        c.Blocks[x, y, z] = data[i++];
            return c;
        }
    }
}
