using System;

namespace Aetheris
{
    public readonly struct ChunkCoord : IEquatable<ChunkCoord>
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;

        public ChunkCoord(int x, int y, int z) { X = x; Y = y; Z = z; }

        public bool Equals(ChunkCoord other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object? obj) => obj is ChunkCoord c && Equals(c);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public override string ToString() => $"({X},{Y},{Z})";
    }
}
