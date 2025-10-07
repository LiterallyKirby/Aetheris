using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace Aetheris
{
    /// <summary>
    /// Collision mesh data for physics
    /// </summary>
    public struct CollisionMesh
    {
        public List<Vector3> Vertices;
        public List<int> Indices;
        
        public CollisionMesh(List<Vector3> vertices, List<int> indices)
        {
            Vertices = vertices;
            Indices = indices;
        }
    }

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
        
        // Collision mesh for physics (optional, generated on demand)
        public CollisionMesh? CollisionMesh { get; private set; }
        
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
        
        /// <summary>
        /// Generate collision mesh from marching cubes mesh data
        /// </summary>
        public void GenerateCollisionMesh(float[] meshData)
        {
            if (meshData == null || meshData.Length == 0)
            {
                CollisionMesh = null;
                return;
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
                float x = meshData[offset + 0] - PositionX;
                float y = meshData[offset + 1] - PositionY;
                float z = meshData[offset + 2] - PositionZ;
                
                vertices.Add(new Vector3(x, y, z));
                indices.Add(i);
            }
            
            CollisionMesh = new CollisionMesh(vertices, indices);
        }
        
        /// <summary>
        /// Generate simplified collision mesh (optional - for performance)
        /// This creates a lower-resolution collision mesh from the visual mesh
        /// </summary>
        public void GenerateSimplifiedCollisionMesh(float[] meshData, float simplificationFactor = 2f)
        {
            if (meshData == null || meshData.Length == 0)
            {
                CollisionMesh = null;
                return;
            }
            
            var vertices = new List<Vector3>();
            var indices = new List<int>();
            
            int vertexStride = 7;
            int triangleCount = meshData.Length / (vertexStride * 3);
            
            // Simple decimation: keep every Nth triangle
            int keepEveryN = Math.Max(1, (int)simplificationFactor);
            
            for (int triIdx = 0; triIdx < triangleCount; triIdx += keepEveryN)
            {
                for (int vertIdx = 0; vertIdx < 3; vertIdx++)
                {
                    int offset = (triIdx * 3 + vertIdx) * vertexStride;
                    
                    if (offset + 2 >= meshData.Length)
                        break;
                    
                    float x = meshData[offset + 0] - PositionX;
                    float y = meshData[offset + 1] - PositionY;
                    float z = meshData[offset + 2] - PositionZ;
                    
                    vertices.Add(new Vector3(x, y, z));
                    indices.Add(vertices.Count - 1);
                }
            }
            
            if (vertices.Count > 0)
            {
                CollisionMesh = new CollisionMesh(vertices, indices);
            }
            else
            {
                CollisionMesh = null;
            }
        }
        
        /// <summary>
        /// Clear collision mesh to save memory
        /// </summary>
        public void ClearCollisionMesh()
        {
            CollisionMesh = null;
        }
    }
}
