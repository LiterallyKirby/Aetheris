using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenTK.Mathematics;

namespace Aetheris
{
    public static class MarchingCubes
    {
        // Pre-allocate to reduce GC pressure
        [ThreadStatic]
        private static List<float>? threadLocalVerts;

        public static float[] GenerateMesh(Aetheris.Chunk chunk, float isoLevel = 0.5f)
        {
            // Use thread-local list to avoid allocations
            if (threadLocalVerts == null)
            {
                threadLocalVerts = new List<float>(10000);
            }
            else
            {
                threadLocalVerts.Clear();
            }

            var verts = threadLocalVerts;
            int step = Math.Max(1, Config.STEP);

            // Pre-calculate bounds
            int maxX = Aetheris.Chunk.SizeX - 1;
            int maxY = Aetheris.Chunk.SizeY - 1;
            int maxZ = Aetheris.Chunk.SizeZ - 1;

            // Stack-allocate small arrays for better performance
            Span<(float x, float y, float z)> pos = stackalloc (float, float, float)[8];
            Span<float> val = stackalloc float[8];
            Span<(float x, float y, float z)> vertList = stackalloc (float, float, float)[12];

            for (int x = 0; x < maxX; x += step)
            for (int y = 0; y < maxY; y += step)
            for (int z = 0; z < maxZ; z += step)
            {
                // Cube corners
                pos[0] = (x, y, z);
                pos[1] = (x + step, y, z);
                pos[2] = (x + step, y, z + step);
                pos[3] = (x, y, z + step);
                pos[4] = (x, y + step, z);
                pos[5] = (x + step, y + step, z);
                pos[6] = (x + step, y + step, z + step);
                pos[7] = (x, y + step, z + step);

                // Sample density values
                for (int i = 0; i < 8; i++)
                {
                    val[i] = SampleDensityFast(chunk, (int)pos[i].x, (int)pos[i].y, (int)pos[i].z);
                }

                // Calculate cube index
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

                // Calculate vertex positions on edges
                if ((edges & 1) != 0) vertList[0] = VertexInterpFast(isoLevel, pos[0], pos[1], val[0], val[1]);
                if ((edges & 2) != 0) vertList[1] = VertexInterpFast(isoLevel, pos[1], pos[2], val[1], val[2]);
                if ((edges & 4) != 0) vertList[2] = VertexInterpFast(isoLevel, pos[2], pos[3], val[2], val[3]);
                if ((edges & 8) != 0) vertList[3] = VertexInterpFast(isoLevel, pos[3], pos[0], val[3], val[0]);
                if ((edges & 16) != 0) vertList[4] = VertexInterpFast(isoLevel, pos[4], pos[5], val[4], val[5]);
                if ((edges & 32) != 0) vertList[5] = VertexInterpFast(isoLevel, pos[5], pos[6], val[5], val[6]);
                if ((edges & 64) != 0) vertList[6] = VertexInterpFast(isoLevel, pos[6], pos[7], val[6], val[7]);
                if ((edges & 128) != 0) vertList[7] = VertexInterpFast(isoLevel, pos[7], pos[4], val[7], val[4]);
                if ((edges & 256) != 0) vertList[8] = VertexInterpFast(isoLevel, pos[0], pos[4], val[0], val[4]);
                if ((edges & 512) != 0) vertList[9] = VertexInterpFast(isoLevel, pos[1], pos[5], val[1], val[5]);
                if ((edges & 1024) != 0) vertList[10] = VertexInterpFast(isoLevel, pos[2], pos[6], val[2], val[6]);
                if ((edges & 2048) != 0) vertList[11] = VertexInterpFast(isoLevel, pos[3], pos[7], val[3], val[7]);

                // Generate triangles
                for (int i = 0; i < Tables.TriTable.GetLength(1); i += 3)
                {
                    int a = Tables.TriTable[cubeIndex, i];
                    if (a == -1) break;
                    
                    int b = Tables.TriTable[cubeIndex, i + 1];
                    int c = Tables.TriTable[cubeIndex, i + 2];

                    var pa = new Vector3(vertList[a].x, vertList[a].y, vertList[a].z);
                    var pb = new Vector3(vertList[b].x, vertList[b].y, vertList[b].z);
                    var pc = new Vector3(vertList[c].x, vertList[c].y, vertList[c].z);

                    var normal = Vector3.Cross(pb - pa, pc - pa).Normalized();

                    // Add vertices and normals
                    AddVertex(verts, pa, normal);
                    AddVertex(verts, pb, normal);
                    AddVertex(verts, pc, normal);
                }
            }

            return verts.ToArray();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddVertex(List<float> verts, Vector3 v, Vector3 normal)
        {
            verts.Add(v.X);
            verts.Add(v.Y);
            verts.Add(v.Z);
            verts.Add(normal.X);
            verts.Add(normal.Y);
            verts.Add(normal.Z);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float SampleDensityFast(Aetheris.Chunk chunk, int x, int y, int z)
        {
            // Bounds check with early return
            if ((uint)x >= Aetheris.Chunk.SizeX ||
                (uint)y >= Aetheris.Chunk.SizeY ||
                (uint)z >= Aetheris.Chunk.SizeZ)
                return 0.0f;

            return chunk.Blocks[x, y, z] > 0 ? 1.0f : 0.0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (float x, float y, float z) VertexInterpFast(float isoLevel,
            (float x, float y, float z) p1, (float x, float y, float z) p2,
            float val1, float val2)
        {
            const float epsilon = 1e-6f;
            
            if (Math.Abs(isoLevel - val1) < epsilon) return p1;
            if (Math.Abs(isoLevel - val2) < epsilon) return p2;
            if (Math.Abs(val1 - val2) < epsilon) return p1;
            
            float mu = (isoLevel - val1) / (val2 - val1);
            return (
                p1.x + mu * (p2.x - p1.x),
                p1.y + mu * (p2.y - p1.y),
                p1.z + mu * (p2.z - p1.z)
            );
        }
    }
}
