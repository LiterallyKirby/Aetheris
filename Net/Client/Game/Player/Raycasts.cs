using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Aetheris
{
    /// <summary>
    /// Helper class for performing raycasts against marching cubes terrain
    /// PlayCanvas style: Raycast(start, end, raycastAll)
    /// Returns an array: single hit (raycastAll = false) or all hits sorted by distance (raycastAll = true)
    /// </summary>
    public class RaycastHelper
    {
        private readonly Game? game;

        public struct RaycastHit
        {
            public bool Hit;
            public Vector3 Point;
            public Vector3 Normal;
            public float Distance;
            public BlockType BlockType;

            public static RaycastHit Miss => new RaycastHit { Hit = false };
        }

        public RaycastHelper(Game? gameRef)
        {
            game = gameRef;
        }

        /// <summary>
        /// PlayCanvas-style API:
        /// Raycast(start, end, raycastAll)
        /// If raycastAll == false -> returns array with 0 or 1 RaycastHit (closest)
        /// If raycastAll == true -> returns all RaycastHit results sorted by distance
        /// </summary>
        public RaycastHit[] Raycast(Vector3 start, Vector3 end, bool raycastAll = false)
        {
            Vector3 rayVec = end - start;
            float maxDistance = rayVec.Length;
            if (maxDistance <= 0.0001f)
                return Array.Empty<RaycastHit>();

            Vector3 dir = Vector3.Normalize(rayVec);

            // DDA setup (voxel grid with unit cubes)
            int ix = (int)MathF.Floor(start.X);
            int iy = (int)MathF.Floor(start.Y);
            int iz = (int)MathF.Floor(start.Z);

            int stepX = dir.X > 0 ? 1 : -1;
            int stepY = dir.Y > 0 ? 1 : -1;
            int stepZ = dir.Z > 0 ? 1 : -1;

            float tDeltaX = dir.X == 0 ? float.MaxValue : MathF.Abs(1.0f / dir.X);
            float tDeltaY = dir.Y == 0 ? float.MaxValue : MathF.Abs(1.0f / dir.Y);
            float tDeltaZ = dir.Z == 0 ? float.MaxValue : MathF.Abs(1.0f / dir.Z);

            float tMaxX = dir.X > 0
                ? ((ix + 1.0f) - start.X) * tDeltaX
                : (start.X - ix) * tDeltaX;
            float tMaxY = dir.Y > 0
                ? ((iy + 1.0f) - start.Y) * tDeltaY
                : (start.Y - iy) * tDeltaY;
            float tMaxZ = dir.Z > 0
                ? ((iz + 1.0f) - start.Z) * tDeltaZ
                : (start.Z - iz) * tDeltaZ;

            // Limit the number of voxel steps to avoid pathological loops
            int maxSteps = (int)(maxDistance / 0.5f) + 8; // half-voxel step safety
            var hits = new List<RaycastHit>();

            // Walk voxels along ray
            for (int step = 0; step < maxSteps; step++)
            {
                // Query the chunk that contains voxel (ix,iy,iz)
                try
                {
                    int chunkX = (int)MathF.Floor((float)ix / ClientConfig.CHUNK_SIZE);
                    int chunkY = (int)MathF.Floor((float)iy / ClientConfig.CHUNK_SIZE_Y);
                    int chunkZ = (int)MathF.Floor((float)iz / ClientConfig.CHUNK_SIZE);

                    var meshData = game?.Renderer.GetMeshData(chunkX, chunkY, chunkZ);
                    if (meshData != null && meshData.Length >= 21)
                    {
                        // Mesh data format assumed: 7 floats per vertex (x,y,z,nx,ny,nz,blockType) * 3 = 21 per triangle
                        for (int i = 0; i + 20 < meshData.Length; i += 21)
                        {
                            Vector3 v0 = new Vector3(meshData[i + 0], meshData[i + 1], meshData[i + 2]);
                            Vector3 v1 = new Vector3(meshData[i + 7], meshData[i + 8], meshData[i + 9]);
                            Vector3 v2 = new Vector3(meshData[i + 14], meshData[i + 15], meshData[i + 16]);

                            if (RayTriangleIntersect(start, dir, v0, v1, v2, out float t, out Vector3 triNormal))
                            {
                                if (t > 0.0005f && t <= maxDistance)
                                {
                                    var hit = new RaycastHit
                                    {
                                        Hit = true,
                                        Point = start + dir * t,
                                        Normal = triNormal,
                                        Distance = t,
                                        BlockType = (BlockType)(int)meshData[i + 6]
                                    };

                                    hits.Add(hit);
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // ignore chunk access errors and continue stepping
                }

                // Advance DDA to next voxel
                if (tMaxX < tMaxY)
                {
                    if (tMaxX < tMaxZ)
                    {
                        // X is smallest
                        if (tMaxX * 1.0001f > maxDistance) break; // next boundary past maxDistance
                        ix += stepX;
                        tMaxX += tDeltaX;
                    }
                    else
                    {
                        // Z is smallest
                        if (tMaxZ * 1.0001f > maxDistance) break;
                        iz += stepZ;
                        tMaxZ += tDeltaZ;
                    }
                }
                else
                {
                    if (tMaxY < tMaxZ)
                    {
                        // Y is smallest
                        if (tMaxY * 1.0001f > maxDistance) break;
                        iy += stepY;
                        tMaxY += tDeltaY;
                    }
                    else
                    {
                        // Z is smallest (or tie)
                        if (tMaxZ * 1.0001f > maxDistance) break;
                        iz += stepZ;
                        tMaxZ += tDeltaZ;
                    }
                }
            }

            if (hits.Count == 0)
                return Array.Empty<RaycastHit>();

            // Deduplicate hits that are very close (same triangle found multiple times across voxels)
            hits = hits
                .OrderBy(h => h.Distance)
                .Where((h, idx) =>
                {
                    // keep if it's the first, or far enough from the previous kept hit
                    if (idx == 0) return true;
                    return h.Distance - hits[idx - 1].Distance > 0.001f;
                })
                .ToList();

            if (!raycastAll)
            {
                return new[] { hits[0] };
            }

            return hits.ToArray();
        }

        /// <summary>
        /// Estimate surface normal at a point using finite differences
        /// </summary>
        private Vector3 EstimateNormal(Vector3 position)
        {
            const float epsilon = 0.1f;

            float centerDensity = SampleDensityAt(position);

            float dx = SampleDensityAt(position + new Vector3(epsilon, 0, 0)) - centerDensity;
            float dy = SampleDensityAt(position + new Vector3(0, epsilon, 0)) - centerDensity;
            float dz = SampleDensityAt(position + new Vector3(0, 0, epsilon)) - centerDensity;

            Vector3 normal = new Vector3(dx, dy, dz);

            if (normal.LengthSquared < 0.0001f)
                return Vector3.UnitY;

            return Vector3.Normalize(normal);
        }

        /// <summary>
        /// Möller–Trumbore ray-triangle intersection algorithm
        /// </summary>
        private bool RayTriangleIntersect(Vector3 origin, Vector3 direction, Vector3 v0, Vector3 v1, Vector3 v2,
                                         out float t, out Vector3 normal)
        {
            t = 0;
            normal = Vector3.UnitY;

            const float EPSILON = 1e-7f;

            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;

            Vector3 h = Vector3.Cross(direction, edge2);
            float a = Vector3.Dot(edge1, h);

            if (a > -EPSILON && a < EPSILON)
                return false; // Ray parallel to triangle

            float f = 1.0f / a;
            Vector3 s = origin - v0;
            float u = f * Vector3.Dot(s, h);
            if (u < 0.0f || u > 1.0f) return false;

            Vector3 q = Vector3.Cross(s, edge1);
            float v = f * Vector3.Dot(direction, q);
            if (v < 0.0f || u + v > 1.0f) return false;

            t = f * Vector3.Dot(edge2, q);
            if (t > EPSILON)
            {
                normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));
                return true;
            }

            return false;
        }

        private float SampleDensityAt(Vector3 position)
        {
            float fx = position.X;
            float fy = position.Y;
            float fz = position.Z;

            int x0 = (int)MathF.Floor(fx);
            int y0 = (int)MathF.Floor(fy);
            int z0 = (int)MathF.Floor(fz);

            int x1 = x0 + 1;
            int y1 = y0 + 1;
            int z1 = z0 + 1;

            float tx = fx - x0;
            float ty = fy - y0;
            float tz = fz - z0;

            float d000 = WorldGen.SampleDensity(x0, y0, z0);
            float d100 = WorldGen.SampleDensity(x1, y0, z0);
            float d010 = WorldGen.SampleDensity(x0, y1, z0);
            float d110 = WorldGen.SampleDensity(x1, y1, z0);
            float d001 = WorldGen.SampleDensity(x0, y0, z1);
            float d101 = WorldGen.SampleDensity(x1, y0, z1);
            float d011 = WorldGen.SampleDensity(x0, y1, z1);
            float d111 = WorldGen.SampleDensity(x1, y1, z1);

            float d00 = d000 * (1 - tx) + d100 * tx;
            float d01 = d001 * (1 - tx) + d101 * tx;
            float d10 = d010 * (1 - tx) + d110 * tx;
            float d11 = d011 * (1 - tx) + d111 * tx;

            float d0 = d00 * (1 - ty) + d10 * ty;
            float d1 = d01 * (1 - ty) + d11 * ty;

            return d0 * (1 - tz) + d1 * tz;
        }
    }
}
