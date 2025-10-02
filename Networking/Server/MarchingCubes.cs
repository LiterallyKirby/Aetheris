using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenTK.Mathematics;

namespace Aetheris
{
    public static class MarchingCubes
    {
        [ThreadStatic]
        private static List<float>? threadLocalVerts;

        public static float[] GenerateMesh(Chunk chunk, float isoLevel = 0.5f)
        {
            if (threadLocalVerts == null)
                threadLocalVerts = new List<float>(10000);
            else
                threadLocalVerts.Clear();

            var verts = threadLocalVerts!;
            int step = Math.Max(1, Config.STEP);

            Span<Vector3> pos = stackalloc Vector3[8];
            Span<float> val = stackalloc float[8];
            Span<Vector3> vertList = stackalloc Vector3[12];

            // CRITICAL FIX: Process cubes up to and INCLUDING the boundary
            // This ensures chunks connect properly at edges
            int maxX = Chunk.SizeX;
            int maxY = Chunk.SizeY;
            int maxZ = Chunk.SizeZ;

            for (int x = 0; x < maxX; x += step)
            for (int y = 0; y < maxY; y += step)
            for (int z = 0; z < maxZ; z += step)
            {
                // Allow cubes to extend one step beyond chunk boundary
                // This is OK because we're sampling WorldGen directly
                int nextX = x + step;
                int nextY = y + step;
                int nextZ = z + step;

                // 8 corners of the cube
                pos[0] = new Vector3(x, y, z);
                pos[1] = new Vector3(nextX, y, z);
                pos[2] = new Vector3(nextX, y, nextZ);
                pos[3] = new Vector3(x, y, nextZ);
                pos[4] = new Vector3(x, nextY, z);
                pos[5] = new Vector3(nextX, nextY, z);
                pos[6] = new Vector3(nextX, nextY, nextZ);
                pos[7] = new Vector3(x, nextY, nextZ);

                // Sample density at corners (can go beyond chunk bounds)
                for (int i = 0; i < 8; i++)
                {
                    int worldX = chunk.PositionX + (int)pos[i].X;
                    int worldY = chunk.PositionY + (int)pos[i].Y;
                    int worldZ = chunk.PositionZ + (int)pos[i].Z;
                    val[i] = WorldGen.SampleDensity(worldX, worldY, worldZ);
                }

                // Determine cube configuration
                int cubeIndex = 0;
                if (val[0] > isoLevel) cubeIndex |= 1;
                if (val[1] > isoLevel) cubeIndex |= 2;
                if (val[2] > isoLevel) cubeIndex |= 4;
                if (val[3] > isoLevel) cubeIndex |= 8;
                if (val[4] > isoLevel) cubeIndex |= 16;
                if (val[5] > isoLevel) cubeIndex |= 32;
                if (val[6] > isoLevel) cubeIndex |= 64;
                if (val[7] > isoLevel) cubeIndex |= 128;

                int edges = Tables.EdgeTable[cubeIndex];
                if (edges == 0) continue;

                // Interpolate edge vertices
                if ((edges & 1) != 0) vertList[0] = Lerp(isoLevel, pos[0], pos[1], val[0], val[1]);
                if ((edges & 2) != 0) vertList[1] = Lerp(isoLevel, pos[1], pos[2], val[1], val[2]);
                if ((edges & 4) != 0) vertList[2] = Lerp(isoLevel, pos[2], pos[3], val[2], val[3]);
                if ((edges & 8) != 0) vertList[3] = Lerp(isoLevel, pos[3], pos[0], val[3], val[0]);
                if ((edges & 16) != 0) vertList[4] = Lerp(isoLevel, pos[4], pos[5], val[4], val[5]);
                if ((edges & 32) != 0) vertList[5] = Lerp(isoLevel, pos[5], pos[6], val[5], val[6]);
                if ((edges & 64) != 0) vertList[6] = Lerp(isoLevel, pos[6], pos[7], val[6], val[7]);
                if ((edges & 128) != 0) vertList[7] = Lerp(isoLevel, pos[7], pos[4], val[7], val[4]);
                if ((edges & 256) != 0) vertList[8] = Lerp(isoLevel, pos[0], pos[4], val[0], val[4]);
                if ((edges & 512) != 0) vertList[9] = Lerp(isoLevel, pos[1], pos[5], val[1], val[5]);
                if ((edges & 1024) != 0) vertList[10] = Lerp(isoLevel, pos[2], pos[6], val[2], val[6]);
                if ((edges & 2048) != 0) vertList[11] = Lerp(isoLevel, pos[3], pos[7], val[3], val[7]);

                // Generate triangles
                for (int i = 0; i < Tables.TriTable.GetLength(1); i += 3)
                {
                    int a = Tables.TriTable[cubeIndex, i];
                    if (a == -1) break;

                    int b = Tables.TriTable[cubeIndex, i + 1];
                    int c = Tables.TriTable[cubeIndex, i + 2];

                    var v0 = vertList[a];
                    var v1 = vertList[b];
                    var v2 = vertList[c];

                    // Calculate normal
                    var normal = Vector3.Cross(v1 - v0, v2 - v0).Normalized();

                    // Add triangle
                    AddVertex(verts, v0, normal);
                    AddVertex(verts, v1, normal);
                    AddVertex(verts, v2, normal);
                }
            }

            return verts.ToArray();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddVertex(List<float> verts, Vector3 v, Vector3 n)
        {
            verts.Add(v.X);
            verts.Add(v.Y);
            verts.Add(v.Z);
            verts.Add(n.X);
            verts.Add(n.Y);
            verts.Add(n.Z);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 Lerp(float iso, Vector3 p1, Vector3 p2, float v1, float v2)
        {
            const float epsilon = 1e-5f;
            if (Math.Abs(iso - v1) < epsilon) return p1;
            if (Math.Abs(iso - v2) < epsilon) return p2;
            if (Math.Abs(v1 - v2) < epsilon) return p1;

            float t = (iso - v1) / (v2 - v1);
            return p1 + t * (p2 - p1);
        }
    }
}
