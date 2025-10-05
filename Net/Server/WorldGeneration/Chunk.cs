using System;

namespace Aetheris
{
    // Shared chunk format used by server and client.
    public class Chunk
    {
        public static readonly int SizeX = ServerConfig.CHUNK_SIZE;
        public static readonly int SizeY = ServerConfig.CHUNK_SIZE_Y;
        public static readonly int SizeZ = ServerConfig.CHUNK_SIZE;

        public static readonly int TotalSize = SizeX * SizeY * SizeZ;

        public byte[,,] Blocks;

        // World position of chunk (in blocks)
        public int PositionX { get; }
        public int PositionY { get; }
        public int PositionZ { get; }

        public Chunk(int posX, int posY, int posZ)
        {
            PositionX = posX;
            PositionY = posY;
            PositionZ = posZ;
            Blocks = new byte[SizeX, SizeY, SizeZ];
        }

        // Default constructor places chunk at (0,0,0)
        public Chunk() : this(0, 0, 0) { }

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

        public static Chunk FromBytes(byte[] data, int posX, int posY, int posZ)
        {
            if (data.Length != TotalSize)
                throw new ArgumentException($"Chunk.FromBytes: bad length {data.Length}, expected {TotalSize}");
            var c = new Chunk(posX, posY, posZ);
            int i = 0;
            for (int x = 0; x < SizeX; x++)
                for (int y = 0; y < SizeY; y++)
                    for (int z = 0; z < SizeZ; z++)
                        c.Blocks[x, y, z] = data[i++];
            return c;
        }
    }
}
