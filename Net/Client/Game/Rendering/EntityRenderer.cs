using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Aetheris
{
    /// <summary>
    /// Handles rendering of remote players with PSX-style visuals
    /// </summary>
    public class EntityRenderer : IDisposable
    {
        // Player mesh GPU data
        private int playerVao;
        private int playerVbo;
        private int playerVertexCount;
        
        // Player dimensions (matching Player.cs)
        private const float PLAYER_WIDTH = 1.2f;
        private const float PLAYER_HEIGHT = 3.6f;
        private const float HEAD_SIZE = 0.8f;
        
        // Visual style - use block types from atlas for colors
        private const AetherisClient.Rendering.BlockType BODY_BLOCK = AetherisClient.Rendering.BlockType.Dirt;  // Brown body
        private const AetherisClient.Rendering.BlockType HEAD_BLOCK = AetherisClient.Rendering.BlockType.Sand;  // Skin tone (sand color)
        
        // Render settings
        public float MaxRenderDistance { get; set; } = 100f; // blocks
        
        public EntityRenderer()
        {
            GeneratePlayerMesh();
        }
        
        /// <summary>
        /// Render all remote players - call this during your main render loop
        /// Should be called AFTER terrain but BEFORE UI
        /// </summary>
        public void RenderPlayers(
            Dictionary<string, RemotePlayer> players,
            PSXVisualEffects psxEffects,
            Vector3 localPlayerPos,
            bool usePSXShader)
        {
            if (players == null || players.Count == 0 || playerVertexCount == 0) 
            {
                return;
            }
            
            // Remove stale players
            long currentTicks = DateTime.UtcNow.Ticks;
            var toRemove = new List<string>();
            
            foreach (var kvp in players)
            {
                if (kvp.Value.IsStale(currentTicks))
                {
                    toRemove.Add(kvp.Key);
                }
            }
            
            foreach (var id in toRemove)
            {
                players.Remove(id);
                Console.WriteLine($"[EntityRenderer] Removed stale player {id}");
            }
            
            if (players.Count == 0) return;
            
            // DETAILED LOGGING FOR DEBUGGING
            Console.WriteLine($"[EntityRenderer] === RENDER FRAME ===");
            Console.WriteLine($"[EntityRenderer] Total players in dict: {players.Count}");
            Console.WriteLine($"[EntityRenderer] Local player position: {localPlayerPos}");
            Console.WriteLine($"[EntityRenderer] usePSXShader flag: {usePSXShader}");
            
            // Check what shader is actually bound
            GL.GetInteger(GetPName.CurrentProgram, out int currentShader);
            Console.WriteLine($"[EntityRenderer] Currently bound shader program: {currentShader}");
            
            foreach (var kvp in players)
            {
                Vector3 pos = kvp.Value.GetDisplayPosition();
                float dist = (pos - localPlayerPos).Length;
                Console.WriteLine($"[EntityRenderer]   Player {kvp.Key}: pos={pos}, dist={dist:F1}");
            }
            
            // Ensure shader is active - check the actual OpenGL state
            if (currentShader == 0)
            {
                Console.WriteLine("[EntityRenderer] ERROR: No shader program bound! Cannot render players.");
                return;
            }
            
            // Get uniform locations from the currently active shader
            int locModel = GL.GetUniformLocation(currentShader, "uModel");
            int locNormalMatrix = GL.GetUniformLocation(currentShader, "uNormalMatrix");
            
            Console.WriteLine($"[EntityRenderer] Shader uniform locations - uModel: {locModel}, uNormalMatrix: {locNormalMatrix}");
            
            if (locModel < 0)
            {
                Console.WriteLine("[EntityRenderer] ERROR: uModel uniform not found in shader!");
                return;
            }
            
            GL.BindVertexArray(playerVao);
            Console.WriteLine($"[EntityRenderer] Bound player VAO: {playerVao}");
            
            float maxDistSq = MaxRenderDistance * MaxRenderDistance;
            int rendered = 0;
            
            foreach (var player in players.Values)
            {
                Vector3 playerPos = player.GetDisplayPosition();
                float distSq = (playerPos - localPlayerPos).LengthSquared;
                
                // Distance culling
                if (distSq > maxDistSq) 
                {
                    Console.WriteLine($"[EntityRenderer] Player at {playerPos} too far (dist={MathF.Sqrt(distSq):F1}), skipping");
                    continue;
                }
                
                // Get model matrix for this player
                Matrix4 model = player.GetModelMatrix();
                
                // EXTRACT POSITION FROM MODEL MATRIX FOR DEBUGGING
                Vector3 extractedPos = new Vector3(model.M41, model.M42, model.M43);
                Console.WriteLine($"[EntityRenderer] Player display pos: {playerPos}, Extracted from matrix: {extractedPos}");
                Console.WriteLine($"[EntityRenderer] Model matrix M41-M43: ({model.M41:F2}, {model.M42:F2}, {model.M43:F2})");
                
                // Set model matrix in active shader
                GL.UniformMatrix4(locModel, false, ref model);
                
                // Set normal matrix if available
                if (locNormalMatrix >= 0)
                {
                    Matrix3 normalMat = new Matrix3(model);
                    normalMat = Matrix3.Transpose(normalMat.Inverted());
                    GL.UniformMatrix3(locNormalMatrix, false, ref normalMat);
                }
                
                // Draw the player
                GL.DrawArrays(PrimitiveType.Triangles, 0, playerVertexCount);
                rendered++;
                
                Console.WriteLine($"[EntityRenderer] ✓ Drew player at {playerPos} with {playerVertexCount} vertices");
            }
            
            GL.BindVertexArray(0);
            
            Console.WriteLine($"[EntityRenderer] Successfully rendered {rendered}/{players.Count} players");
        }
        
        /// <summary>
        /// Generate capsule mesh: cylinder body + sphere head
        /// Format: Position(3) + Normal(3) + UV(2) = 8 floats per vertex
        /// </summary>
        private void GeneratePlayerMesh()
        {
            List<float> vertices = new List<float>();
            
            const int segments = 8; // Low-poly for PSX aesthetic
            const int bodyRings = 4;
            const int headRings = 6;
            
            float bodyRadius = PLAYER_WIDTH * 0.35f;
            float bodyHeight = PLAYER_HEIGHT - HEAD_SIZE;
            float headRadius = HEAD_SIZE * 0.5f;
            
            // === BODY (Cylinder) ===
            GenerateCylinder(vertices, bodyRadius, bodyHeight, segments, bodyRings, BODY_BLOCK);
            
            // === HEAD (Sphere on top) ===
            Vector3 headOffset = new Vector3(0, bodyHeight + headRadius, 0);
            GenerateSphere(vertices, headRadius, segments, headRings, HEAD_BLOCK, headOffset);
            
            // Upload to GPU
            playerVertexCount = vertices.Count / 8;
            
            playerVao = GL.GenVertexArray();
            playerVbo = GL.GenBuffer();
            
            GL.BindVertexArray(playerVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, playerVbo);
            
            GL.BufferData(BufferTarget.ArrayBuffer,
                vertices.Count * sizeof(float),
                vertices.ToArray(),
                BufferUsageHint.StaticDraw);
            
            int stride = 8 * sizeof(float);
            
            // Position (location 0)
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            
            // Normal (location 1)
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            
            // UV (location 2)
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
            
            GL.BindVertexArray(0);
            
            Console.WriteLine($"[EntityRenderer] Generated player mesh: {playerVertexCount} vertices, VAO={playerVao}, VBO={playerVbo}");
        }
        
        private void GenerateCylinder(List<float> verts, float radius, float height,
            int segments, int rings, AetherisClient.Rendering.BlockType blockType)
        {
            for (int r = 0; r < rings; r++)
            {
                float y0 = (float)r / rings * height;
                float y1 = (float)(r + 1) / rings * height;
                
                for (int s = 0; s < segments; s++)
                {
                    float a0 = (float)s / segments * MathF.PI * 2f;
                    float a1 = (float)(s + 1) / segments * MathF.PI * 2f;
                    
                    float c0 = MathF.Cos(a0), s0 = MathF.Sin(a0);
                    float c1 = MathF.Cos(a1), s1 = MathF.Sin(a1);
                    
                    Vector3 n0 = new Vector3(c0, 0, s0);
                    Vector3 n1 = new Vector3(c1, 0, s1);
                    
                    Vector3 p0 = new Vector3(c0 * radius, y0, s0 * radius);
                    Vector3 p1 = new Vector3(c1 * radius, y0, s1 * radius);
                    Vector3 p2 = new Vector3(c0 * radius, y1, s0 * radius);
                    Vector3 p3 = new Vector3(c1 * radius, y1, s1 * radius);
                    
                    // Tri 1
                    AddVertex(verts, p0, n0, blockType);
                    AddVertex(verts, p1, n1, blockType);
                    AddVertex(verts, p2, n0, blockType);
                    
                    // Tri 2
                    AddVertex(verts, p1, n1, blockType);
                    AddVertex(verts, p3, n1, blockType);
                    AddVertex(verts, p2, n0, blockType);
                }
            }
        }
        
        private void GenerateSphere(List<float> verts, float radius, int segments, int rings,
            AetherisClient.Rendering.BlockType blockType, Vector3 offset)
        {
            for (int r = 0; r < rings; r++)
            {
                float theta0 = (float)r / rings * MathF.PI;
                float theta1 = (float)(r + 1) / rings * MathF.PI;
                
                for (int s = 0; s < segments; s++)
                {
                    float phi0 = (float)s / segments * MathF.PI * 2f;
                    float phi1 = (float)(s + 1) / segments * MathF.PI * 2f;
                    
                    Vector3 p0 = SphericalToCartesian(radius, theta0, phi0) + offset;
                    Vector3 p1 = SphericalToCartesian(radius, theta0, phi1) + offset;
                    Vector3 p2 = SphericalToCartesian(radius, theta1, phi0) + offset;
                    Vector3 p3 = SphericalToCartesian(radius, theta1, phi1) + offset;
                    
                    Vector3 n0 = Vector3.Normalize(p0 - offset);
                    Vector3 n1 = Vector3.Normalize(p1 - offset);
                    Vector3 n2 = Vector3.Normalize(p2 - offset);
                    Vector3 n3 = Vector3.Normalize(p3 - offset);
                    
                    // Tri 1
                    AddVertex(verts, p0, n0, blockType);
                    AddVertex(verts, p1, n1, blockType);
                    AddVertex(verts, p2, n2, blockType);
                    
                    // Tri 2
                    AddVertex(verts, p1, n1, blockType);
                    AddVertex(verts, p3, n3, blockType);
                    AddVertex(verts, p2, n2, blockType);
                }
            }
        }
        
        private Vector3 SphericalToCartesian(float r, float theta, float phi)
        {
            return new Vector3(
                r * MathF.Sin(theta) * MathF.Cos(phi),
                r * MathF.Cos(theta),
                r * MathF.Sin(theta) * MathF.Sin(phi)
            );
        }
        
        private void AddVertex(List<float> verts, Vector3 pos, Vector3 normal, AetherisClient.Rendering.BlockType blockType)
        {
            // Position
            verts.Add(pos.X);
            verts.Add(pos.Y);
            verts.Add(pos.Z);
            
            // Normal
            verts.Add(normal.X);
            verts.Add(normal.Y);
            verts.Add(normal.Z);
            
            // UV - Use AtlasManager to get proper UVs
            float u, v;
            
            if (AetherisClient.Rendering.AtlasManager.IsLoaded)
            {
                // Use AtlasManager to get proper UV coordinates
                var (uMin, vMin, uMax, vMax) = AetherisClient.Rendering.AtlasManager.GetAtlasUV(
                    blockType, 
                    AetherisClient.Rendering.BlockFace.Side
                );
                
                // Use center of tile to avoid edge bleeding
                u = (uMin + uMax) * 0.5f;
                v = (vMin + vMax) * 0.5f;
            }
            else
            {
                // Fallback: Use your 5-tile atlas layout (stone, dirt, grass, sand, snow)
                // Each tile is 1/5 = 0.2 width
                int tileIndex;
                switch (blockType)
                {
                    case AetherisClient.Rendering.BlockType.Stone:
                        tileIndex = 0;
                        break;
                    case AetherisClient.Rendering.BlockType.Dirt:
                        tileIndex = 1;
                        break;
                    case AetherisClient.Rendering.BlockType.Grass:
                        tileIndex = 2;
                        break;
                    case AetherisClient.Rendering.BlockType.Sand:
                        tileIndex = 3;
                        break;
                    case AetherisClient.Rendering.BlockType.Snow:
                        tileIndex = 4;
                        break;
                    default:
                        tileIndex = 0; // Default to stone
                        break;
                }
                
                // Calculate UV for 5-tile horizontal atlas
                const float tileWidth = 1.0f / 5.0f;  // 0.2
                const float padding = 0.002f;
                
                float uMin = tileIndex * tileWidth + padding;
                float uMax = (tileIndex + 1) * tileWidth - padding;
                
                // Center of tile
                u = (uMin + uMax) * 0.5f;
                v = 0.5f;  // Center vertically (single row atlas)
            }
            
            verts.Add(u);
            verts.Add(v);
        }
        
        public void Dispose()
        {
            if (playerVbo != 0)
            {
                GL.DeleteBuffer(playerVbo);
                playerVbo = 0;
            }
            
            if (playerVao != 0)
            {
                GL.DeleteVertexArray(playerVao);
                playerVao = 0;
            }
        }
    }
    
    /// <summary>
    /// Represents a remote player with interpolation
    /// </summary>
    public class RemotePlayer
    {
        public string PlayerId { get; set; } = "";
        public string? PlayerName { get; set; }
        
        // Server state (target values)
        private Vector3 targetPosition;
        private Vector3 targetVelocity;
        private float targetYaw;
        private float targetPitch;
        private bool isGrounded;
        
        // Smoothed display state
        private Vector3 displayPosition;
        private float displayYaw;
        
        // Interpolation speeds
        private const float POSITION_LERP_SPEED = 12f;
        private const float ROTATION_LERP_SPEED = 10f;
        
        public long LastUpdateTime { get; private set; }
        
        public RemotePlayer(string playerId, Vector3 initialPosition)
        {
            PlayerId = playerId;
            targetPosition = initialPosition;
            displayPosition = initialPosition;
            LastUpdateTime = DateTime.UtcNow.Ticks;
        }
        
        public void UpdateFromServer(Vector3 position, Vector3 velocity, float yaw, float pitch, bool grounded)
        {
            targetPosition = position;
            targetVelocity = velocity;
            targetYaw = yaw;
            targetPitch = pitch;
            isGrounded = grounded;
            LastUpdateTime = DateTime.UtcNow.Ticks;
        }
        
        public void Update(float deltaTime)
        {
            // Smooth position interpolation
            displayPosition = Vector3.Lerp(displayPosition, targetPosition, 
                POSITION_LERP_SPEED * deltaTime);
            
            // Smooth rotation with angle wrapping
            float yawDiff = targetYaw - displayYaw;
            while (yawDiff > 180f) yawDiff -= 360f;
            while (yawDiff < -180f) yawDiff += 360f;
            displayYaw += yawDiff * ROTATION_LERP_SPEED * deltaTime;
        }
        
        public Vector3 GetDisplayPosition() => displayPosition;
        
        public Matrix4 GetModelMatrix()
        {
            // Manually build the model matrix to avoid any confusion
            // We want: Translate to world position, then rotate around Y axis at that position
            
            // Start with identity
            Matrix4 model = Matrix4.Identity;
            
            // Apply rotation around Y axis
            float yawRad = MathHelper.DegreesToRadians(displayYaw);
            float cos = MathF.Cos(yawRad);
            float sin = MathF.Sin(yawRad);
            
            // Rotation matrix (column-major for OpenGL)
            model.M11 = cos;
            model.M13 = sin;
            model.M31 = -sin;
            model.M33 = cos;
            
            // Translation (last column)
            model.M41 = displayPosition.X;
            model.M42 = displayPosition.Y;
            model.M43 = displayPosition.Z;
            
            return model;
        }
        
        public bool IsStale(long currentTicks, long maxAgeMs = 5000)
        {
            long ageMs = (currentTicks - LastUpdateTime) / TimeSpan.TicksPerMillisecond;
            return ageMs > maxAgeMs;
        }
    }
}
