// Add these methods to your MarchingCubes class:

using OpenTK.Mathematics;
using System.Collections.Generic;

namespace Aetheris
{
    public static partial class MarchingCubes
    {
        /// <summary>
        /// Generate both render mesh and collision mesh from a chunk
        /// </summary>
        public static (float[] renderMesh, CollisionMesh collisionMesh) GenerateMeshes(
            Chunk chunk, ChunkCoord coord, ChunkManager chunkManager, float isoLevel = 0.5f)
        {
            // Generate the render mesh
            float[] renderMesh = GenerateMesh(chunk, coord, chunkManager, isoLevel);
            
            // Convert render mesh to collision mesh
            CollisionMesh collisionMesh = ConvertToCollisionMesh(renderMesh, chunk);
            
            return (renderMesh, collisionMesh);
        }
        
        /// <summary>
        /// Convert render mesh data to collision mesh format
        /// </summary>
        private static CollisionMesh ConvertToCollisionMesh(float[] meshData, Chunk chunk)
        {
            if (meshData == null || meshData.Length == 0)
            {
                return new CollisionMesh(new List<Vector3>(), new List<int>());
            }
            
            var vertices = new List<Vector3>();
            var indices = new List<int>();
            
            // Mesh format: 7 floats per vertex (x, y, z, nx, ny, nz, blockType)
            int vertexStride = 7;
            int vertexCount = meshData.Length / vertexStride;
            
            for (int i = 0; i < vertexCount; i++)
            {
                int offset = i * vertexStride;
                
                // Extract position (relative to chunk origin)
                float x = meshData[offset + 0] - chunk.PositionX;
                float y = meshData[offset + 1] - chunk.PositionY;
                float z = meshData[offset + 2] - chunk.PositionZ;
                
                vertices.Add(new Vector3(x, y, z));
                indices.Add(i);
            }
            
            return new CollisionMesh(vertices, indices);
        }
        
        /// <summary>
        /// Generate simplified collision mesh (optional - for better performance)
        /// </summary>
        public static CollisionMesh GenerateSimplifiedCollisionMesh(
            float[] meshData, Chunk chunk, int simplificationFactor = 2)
        {
            if (meshData == null || meshData.Length == 0)
            {
                return new CollisionMesh(new List<Vector3>(), new List<int>());
            }
            
            var vertices = new List<Vector3>();
            var indices = new List<int>();
            
            int vertexStride = 7;
            int triangleCount = meshData.Length / (vertexStride * 3);
            
            // Simple decimation: keep every Nth triangle
            for (int triIdx = 0; triIdx < triangleCount; triIdx += simplificationFactor)
            {
                for (int vertIdx = 0; vertIdx < 3; vertIdx++)
                {
                    int offset = (triIdx * 3 + vertIdx) * vertexStride;
                    
                    if (offset + 2 >= meshData.Length)
                        break;
                    
                    float x = meshData[offset + 0] - chunk.PositionX;
                    float y = meshData[offset + 1] - chunk.PositionY;
                    float z = meshData[offset + 2] - chunk.PositionZ;
                    
                    vertices.Add(new Vector3(x, y, z));
                    indices.Add(vertices.Count - 1);
                }
            }
            
            return new CollisionMesh(vertices, indices);
        }
    }
}
