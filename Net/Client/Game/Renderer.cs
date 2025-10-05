using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Linq;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using StbImageSharp;

// <-- IMPORTANT: add this so we can call the client-side AtlasManager
using AetherisClient.Rendering;

namespace Aetheris
{
    public class Renderer : IDisposable
    {
        private record MeshData(int Vao, int Vbo, int VertexCount, Matrix4 Model, Vector3 Center, float Radius);

        private readonly Dictionary<(int cx, int cy, int cz), MeshData> meshes = new();
        private readonly ConcurrentQueue<Action> uploadQueue = new();
        private readonly List<MeshData> visibleMeshes = new(1024);
        private readonly Plane[] frustumPlanes = new Plane[6];

        private int shaderProgram;
        private int locProjection, locView, locModel, locFogDecay, locAtlasTexture;
        public delegate void ChunkMeshLoadedCallback(int cx, int cy, int cz, float[] meshData);
        public ChunkMeshLoadedCallback? OnChunkMeshLoaded;
        // renderer-local atlas id is only used for the procedural atlas fallback or when using StbImageSharp loader.
        // If AtlasManager is loaded, we use AtlasManager.AtlasTextureId instead.
        private int localAtlasTextureId = 0;

        private int frameCount = 0;
        private int lastVisibleCount = 0;

        public int RenderDistanceChunks { get; set; } = ClientConfig.RENDER_DISTANCE;
        public float FogDecay = 0.003f;

        // --- PHYSICS INTEGRATION ---
        // Set this from Game.OnLoad after creating your PhysicsManager:
        // physics = new PhysicsManager();
        // renderer.Physics = physics;
        public PhysicsManager? Physics { get; set; }

        // Track chunk colliders registered by this renderer instance
        private readonly HashSet<int> physicsRegisteredChunks = new();

        #region Shaders
        private const string VertexShaderSrc = @"
#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;

out vec3 vNormal;
out vec3 vFragPos;
out vec2 vUV;

uniform mat4 uProjection;
uniform mat4 uView;
uniform mat4 uModel;

void main()
{
    vec4 worldPos = uModel * vec4(aPos, 1.0);
    vFragPos = worldPos.xyz;
    vNormal = mat3(uModel) * aNormal;
    vUV = aUV;
    gl_Position = uProjection * uView * worldPos;
}
";

        private const string FragmentShaderSrc = @"
#version 330 core
in vec3 vNormal;
in vec3 vFragPos;
in vec2 vUV;

out vec4 FragColor;

uniform float uFogDecay;
uniform sampler2D uAtlasTexture;

void main()
{
    vec3 n = normalize(vNormal);
    
    // Sample texture
    vec4 texColor = texture(uAtlasTexture, vUV);
    
    // Lighting
    vec3 light1Dir = normalize(vec3(0.5, 1.0, 0.3));
    vec3 light2Dir = normalize(vec3(-0.3, 0.5, -0.8));
    float diffuse1 = max(dot(n, light1Dir), 0.0);
    float diffuse2 = max(dot(n, light2Dir), 0.0) * 0.4;
    float ambient = 0.3;
    float light = ambient + diffuse1 + diffuse2;

    // Fog
    float fogDistance = length(vFragPos);
    float fogFactor = exp(-fogDistance * uFogDecay);
    fogFactor = clamp(fogFactor, 0.0, 1.0);

    vec3 color = texColor.rgb * light;
    vec3 fogColor = vec3(0.5, 0.6, 0.7);
    vec3 finalColor = mix(fogColor, color, fogFactor);
    
    FragColor = vec4(finalColor, 1.0);
}
";
        #endregion

        public Renderer() { }

        /// <summary>
        /// Load texture atlas from file. Prefers client-side AtlasManager (ImageSharp).
        /// Falls back to StbImageSharp loader or procedural atlas if needed.
        /// </summary>
        public void LoadTextureAtlas(string path)
        {
            // Try AtlasManager first (client-side image loader + metadata/tile detection)
            try
            {
                AtlasManager.LoadAtlas(path, preferredTileSize: 64);
                if (AtlasManager.IsLoaded && AtlasManager.AtlasTextureId != 0)
                {
                    Console.WriteLine($"[Renderer] AtlasManager loaded atlas: {path} (GL id {AtlasManager.AtlasTextureId})");
                    // don't set localAtlasTextureId — AtlasManager owns that texture
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Renderer] AtlasManager failed to load atlas: {ex.Message}");
            }

            // If AtlasManager didn't load it, try the old StbImageSharp loader as a fallback.
            if (System.IO.File.Exists(path))
            {
                try
                {
                    // This returns a local GL texture id (renderer owns this one)
                    localAtlasTextureId = LoadTextureFromFile(path);
                    Console.WriteLine($"[Renderer] StbImageSharp loaded texture atlas: {path} (GL id {localAtlasTextureId})");
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Renderer] Failed to load texture '{path}' via StbImageSharp: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"[Renderer] Texture file not found: {path}");
            }

            // Final fallback: create procedural atlas (renderer-local)
            Console.WriteLine("[Renderer] Using procedural atlas as fallback");
            CreateProceduralAtlas();
        }

        /// <summary>
        /// Create a procedural texture atlas if no image file is available
        /// (renderer-local; kept for dev fallback).
        /// </summary>
        public void CreateProceduralAtlas()
        {
            const int atlasSize = 256; // 4x4 grid, 64px per tile
            const int tileSize = 64;

            byte[] pixels = new byte[atlasSize * atlasSize * 4];

            // Block colors for atlas tiles
            var blockColors = new Dictionary<int, (byte, byte, byte)>
            {
                [0] = (128, 128, 128), // Stone - gray
                [1] = (139, 69, 19),   // Dirt - brown
                [2] = (34, 139, 34),   // Grass - green
                [3] = (194, 178, 128), // Sand - tan
                [4] = (255, 250, 250), // Snow - white
                [5] = (255, 255, 105), // Gravel - light yellow-ish (visible)
                [6] = (101, 67, 33),   // Wood - dark brown
                [7] = (46, 125, 50)    // Leaves - dark green
            };

            var rand = new Random(12345); // Consistent seed for noise

            for (int ty = 0; ty < 4; ty++)
            {
                for (int tx = 0; tx < 4; tx++)
                {
                    int tileIndex = ty * 4 + tx;
                    var color = blockColors.ContainsKey(tileIndex)
                        ? blockColors[tileIndex]
                        : ((byte)255, (byte)0, (byte)255); // Magenta fallback

                    for (int py = 0; py < tileSize; py++)
                    {
                        for (int px = 0; px < tileSize; px++)
                        {
                            int x = tx * tileSize + px;
                            int y = ty * tileSize + py;
                            int idx = (y * atlasSize + x) * 4;

                            // Simple per-pixel noise for texture variation
                            int noise = (int)((rand.NextDouble() - 0.5) * 30); // ±15 variation

                            // Gradient shading to give texture depth
                            float gradient = 0.8f + 0.2f * ((float)py / tileSize);

                            pixels[idx + 0] = (byte)Math.Clamp((color.Item1 + noise) * gradient, 0, 255);
                            pixels[idx + 1] = (byte)Math.Clamp((color.Item2 + noise) * gradient, 0, 255);
                            pixels[idx + 2] = (byte)Math.Clamp((color.Item3 + noise) * gradient, 0, 255);
                            pixels[idx + 3] = 255;
                        }
                    }
                }
            }

            // If there was an existing renderer-local atlas, delete it
            if (localAtlasTextureId != 0)
            {
                try { GL.DeleteTexture(localAtlasTextureId); } catch { }
                localAtlasTextureId = 0;
            }

            localAtlasTextureId = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, localAtlasTextureId);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                atlasSize, atlasSize, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

            // Ensure crisp block textures
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            GL.BindTexture(TextureTarget.Texture2D, 0);

            Console.WriteLine("[Renderer] Created new procedural texture atlas (renderer-local).");
        }
        private readonly Dictionary<(int, int, int), float[]> meshCache = new();
        public float[]? GetMeshData(int cx, int cy, int cz)
        {
            // This is called frequently, so we need stored mesh data
            // We'll add a cache when meshes are uploaded
            return meshCache.TryGetValue((cx, cy, cz), out var data) ? data : null;
        }

        private float[] ConvertMeshWithBlockTypeToUVs(float[] src)
        {
            const int srcStride = 7; // pos3 + normal3 + blockType1
            const int dstStride = 8; // pos3 + normal3 + uv2

            if (src == null || src.Length == 0) return Array.Empty<float>();

            int vertexCount = src.Length / srcStride;
            var dst = new float[vertexCount * dstStride];

            bool atlasLoaded = AtlasManager.IsLoaded;

            // Process each triangle
            for (int triIdx = 0; triIdx < vertexCount; triIdx += 3)
            {
                if (triIdx + 2 >= vertexCount) break;

                // Read triangle data
                Vector3[] pos = new Vector3[3];
                Vector3[] normal = new Vector3[3];
                int blockTypeInt = 0;

                for (int i = 0; i < 3; i++)
                {
                    int si = (triIdx + i) * srcStride;
                    pos[i] = new Vector3(src[si + 0], src[si + 1], src[si + 2]);
                    normal[i] = new Vector3(src[si + 3], src[si + 4], src[si + 5]);
                    if (i == 0) blockTypeInt = (int)Math.Round(src[si + 6]);
                }

                // Get block type
                AetherisClient.Rendering.BlockType clientType;
                if (Enum.IsDefined(typeof(AetherisClient.Rendering.BlockType), blockTypeInt))
                    clientType = (AetherisClient.Rendering.BlockType)blockTypeInt;
                else
                    clientType = AetherisClient.Rendering.BlockType.Stone;

                // Calculate triangle's average normal
                Vector3 triNormal = (normal[0] + normal[1] + normal[2]) / 3.0f;
                Vector3 absNormal = new Vector3(
                    MathF.Abs(triNormal.X),
                    MathF.Abs(triNormal.Y),
                    MathF.Abs(triNormal.Z)
                );

                // Determine which face this triangle belongs to
                AetherisClient.Rendering.BlockFace face;
                if (absNormal.Y > absNormal.X && absNormal.Y > absNormal.Z)
                {
                    face = triNormal.Y > 0 ? AetherisClient.Rendering.BlockFace.Top
                                            : AetherisClient.Rendering.BlockFace.Bottom;
                }
                else
                {
                    face = AetherisClient.Rendering.BlockFace.Side;
                }

                // Get face-specific UVs
                float uMin, vMin, uMax, vMax;
                if (atlasLoaded)
                {
                    (uMin, vMin, uMax, vMax) = AtlasManager.GetAtlasUV(clientType, face);
                }
                else
                {
                    int tx = blockTypeInt % 4;
                    int ty = blockTypeInt / 4;
                    const float pad = 0.002f;
                    uMin = tx * 0.25f + pad;
                    vMin = ty * 0.25f + pad;
                    uMax = (tx + 1) * 0.25f - pad;
                    vMax = (ty + 1) * 0.25f - pad;
                }

                // Determine projection axis for texture mapping
                int axis;
                if (absNormal.Y > absNormal.X && absNormal.Y > absNormal.Z)
                    axis = 1; // Top/Bottom
                else if (absNormal.X > absNormal.Z)
                    axis = 0; // Left/Right
                else
                    axis = 2; // Front/Back

                // Calculate per-triangle rotation based on average position
                Vector3 avgPos = (pos[0] + pos[1] + pos[2]) / 3.0f;
                int blockX = (int)MathF.Floor(avgPos.X);
                int blockY = (int)MathF.Floor(avgPos.Y);
                int blockZ = (int)MathF.Floor(avgPos.Z);
                int seed = blockX * 73856093 ^ blockY * 19349663 ^ blockZ * 83492791;
                float random = (float)((seed & 0x7FFFFFFF) / (float)0x7FFFFFFF);
                int rotation = (int)(random * 4);

                const float texScale = 1.0f / 8.0f;

                // Map each vertex
                for (int i = 0; i < 3; i++)
                {
                    int vi = triIdx + i;
                    int si = vi * srcStride;
                    int di = vi * dstStride;

                    // Copy position and normal
                    dst[di + 0] = src[si + 0];
                    dst[di + 1] = src[si + 1];
                    dst[di + 2] = src[si + 2];
                    dst[di + 3] = src[si + 3];
                    dst[di + 4] = src[si + 4];
                    dst[di + 5] = src[si + 5];

                    // Project position to 2D based on dominant axis
                    float u2d, v2d;
                    switch (axis)
                    {
                        case 0: // X-facing (use YZ)
                            u2d = pos[i].Z * texScale;
                            v2d = pos[i].Y * texScale;
                            break;
                        case 1: // Y-facing (use XZ)
                            u2d = pos[i].X * texScale;
                            v2d = pos[i].Z * texScale;
                            break;
                        default: // Z-facing (use XY)
                            u2d = pos[i].X * texScale;
                            v2d = pos[i].Y * texScale;
                            break;
                    }

                    // Get fractional part (0-1 range for texture tiling)
                    u2d = u2d - MathF.Floor(u2d);
                    v2d = v2d - MathF.Floor(v2d);

                    // Apply rotation
                    float u2dRot = u2d, v2dRot = v2d;
                    switch (rotation)
                    {
                        case 1: u2dRot = 1 - v2d; v2dRot = u2d; break;      // 90°
                        case 2: u2dRot = 1 - u2d; v2dRot = 1 - v2d; break;  // 180°
                        case 3: u2dRot = v2d; v2dRot = 1 - u2d; break;      // 270°
                    }

                    // Map to atlas tile (linear interpolation)
                    float u = uMin + u2dRot * (uMax - uMin);
                    float v = vMin + v2dRot * (vMax - vMin);

                    // Store UVs
                    dst[di + 6] = Math.Clamp(u, uMin, uMax);
                    dst[di + 7] = Math.Clamp(v, vMin, vMax);
                }
            }

            return dst;
        }
        private static float Fract(float x)
        {
            float f = x - MathF.Floor(x);
            return Math.Clamp(f, 0f, 1f);
        }        /// <summary>
                 /// Load texture from image file using StbImageSharp
                 /// Add NuGet package: StbImageSharp
                 /// </summary>
        private int LoadTextureFromFile(string path)
        {
            StbImage.stbi_set_flip_vertically_on_load(1); // OpenGL expects bottom-left origin

            using (var stream = System.IO.File.OpenRead(path))
            {
                ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

                if (image == null)
                    throw new Exception("Failed to load image");

                int textureId = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, textureId);

                GL.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    PixelInternalFormat.Rgba,
                    image.Width,
                    image.Height,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    image.Data
                );

                // Generate mipmaps for better quality at distance
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

                // CRITICAL: Set proper texture parameters to prevent atlas bleeding
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.NearestMipmapLinear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

                // Optional: Anisotropic filtering for better quality
                if (GL.GetString(StringName.Extensions)?.Contains("GL_EXT_texture_filter_anisotropic") == true)
                {
                    GL.GetFloat((GetPName)0x84FF, out float maxAniso); // MAX_TEXTURE_MAX_ANISOTROPY_EXT
                    GL.TexParameter(TextureTarget.Texture2D, (TextureParameterName)0x84FE, Math.Min(4.0f, maxAniso));
                }

                GL.BindTexture(TextureTarget.Texture2D, 0);

                Console.WriteLine($"[Renderer] Loaded {image.Width}x{image.Height} texture with {image.Comp} components");
                return textureId;
            }
        }

        private int LoadTexture(string path)
        {
            // Deprecated - use LoadTextureAtlas() instead
            return LoadTextureFromFile(path);
        }

        public void EnqueueMeshForChunk(int cx, int cy, int cz, float[] interleavedData)
        {
            uploadQueue.Enqueue(() => UploadMesh(cx, cy, cz, interleavedData));
        }

        public void LoadMeshForChunk(int cx, int cy, int cz, float[] interleavedData)
        {
            OnChunkMeshLoaded?.Invoke(cx, cy, cz, interleavedData);
            UploadMesh(cx, cy, cz, interleavedData);
        }

        public void ProcessPendingUploads()
        {
            const int maxPerFrame = 8;
            int processed = 0;

            while (processed < maxPerFrame && uploadQueue.TryDequeue(out var act))
            {
                try { act(); }
                catch (Exception ex) { Console.WriteLine("[Renderer] Upload error: " + ex); }
                processed++;
            }
        }

        public void Render(Matrix4 projection, Matrix4 view, Vector3 cameraPos)
        {
            if (meshes.Count == 0)
            {
                if (frameCount % 60 == 0) // Log every 60 frames

                    return;
            }

            EnsureShader();

            // Determine which texture to use
            int textureToBind = 0;
            if (AtlasManager.IsLoaded && AtlasManager.AtlasTextureId != 0)
            {
                textureToBind = AtlasManager.AtlasTextureId;
            }
            else if (localAtlasTextureId != 0)
            {
                textureToBind = localAtlasTextureId;
            }
            else
            {
                CreateProceduralAtlas();
                textureToBind = localAtlasTextureId;
            }

            // FIRST FRAME DIAGNOSTICS
            if (frameCount == 0)
            {
                Console.WriteLine("=== RENDERER FIRST FRAME DIAGNOSTICS ===");
                Console.WriteLine($"Shader program: {shaderProgram}");
                Console.WriteLine($"Texture to bind: {textureToBind}");
                Console.WriteLine($"Total meshes: {meshes.Count}");
                Console.WriteLine($"Camera position: {cameraPos}");

                // Check uniform locations
                Console.WriteLine($"Uniform locations: proj={locProjection}, view={locView}, model={locModel}, fog={locFogDecay}, atlas={locAtlasTexture}");

                // Verify texture
                GL.BindTexture(TextureTarget.Texture2D, textureToBind);
                GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureWidth, out int w);
                GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureHeight, out int h);
                Console.WriteLine($"Texture dimensions: {w}x{h}");
                GL.BindTexture(TextureTarget.Texture2D, 0);

                // Check first mesh
                var firstMesh = meshes.Values.First();
                Console.WriteLine($"First mesh: VAO={firstMesh.Vao}, VBO={firstMesh.Vbo}, vertices={firstMesh.VertexCount}, center={firstMesh.Center}");

                Console.WriteLine("=== END DIAGNOSTICS ===");
            }

            // Frustum culling
            Matrix4 viewProj = view * projection;
            ExtractFrustumPlanes(viewProj);
            visibleMeshes.Clear();

            float maxRenderDistance = RenderDistanceChunks * ClientConfig.CHUNK_SIZE * 1.5f;
            float maxDistSq = maxRenderDistance * maxRenderDistance;

            foreach (var md in meshes.Values)
            {
                float distSq = (md.Center - cameraPos).LengthSquared;
                if (distSq > maxDistSq)
                    continue;

                if (IsInFrustum(md.Center, md.Radius))
                {
                    visibleMeshes.Add(md);
                }
            }

            lastVisibleCount = visibleMeshes.Count;

            // Log visibility info periodically
            if (frameCount % 60 == 0)
            {

            }

            if (visibleMeshes.Count == 0)
            {
                if (frameCount < 10) // Log for first few frames
                    Console.WriteLine("[Renderer] WARNING: No visible meshes after frustum culling!");
                frameCount++;
                return;
            }

            // Sort by distance (front to back for early Z rejection)
            visibleMeshes.Sort((a, b) =>
            {
                float da = (a.Center - cameraPos).LengthSquared;
                float db = (b.Center - cameraPos).LengthSquared;
                return da.CompareTo(db);
            });

            // Set up OpenGL state
            GL.UseProgram(shaderProgram);
            GL.UniformMatrix4(locProjection, false, ref projection);
            GL.UniformMatrix4(locView, false, ref view);
            GL.Uniform1(locFogDecay, FogDecay);

            // Bind texture
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, textureToBind);
            GL.Uniform1(locAtlasTexture, 0);

            // Draw all visible meshes
            int drawn = 0;
            foreach (var md in visibleMeshes)
            {
                if (md.VertexCount == 0) continue;

                var model = md.Model;
                GL.UniformMatrix4(locModel, false, ref model);
                GL.BindVertexArray(md.Vao);
                GL.DrawArrays(PrimitiveType.Triangles, 0, md.VertexCount);
                drawn++;
            }

            GL.BindVertexArray(0);
            GL.UseProgram(0);
            if (frameCount == 0 && AtlasManager.IsLoaded)
            {
                Console.WriteLine("=== ATLAS UV DIAGNOSTICS ===");
                Console.WriteLine($"Atlas: {AtlasManager.AtlasWidth}x{AtlasManager.AtlasHeight}");
                Console.WriteLine($"Tile: {AtlasManager.TileSize}px, Grid: {AtlasManager.TilesPerRow}x{AtlasManager.TilesPerCol}");

                // Test each block type and face
                foreach (var bt in Enum.GetValues(typeof(AetherisClient.Rendering.BlockType)).Cast<AetherisClient.Rendering.BlockType>())
                {
                    if (bt == AetherisClient.Rendering.BlockType.Air) continue;

                    var (u0, v0, u1, v1) = AtlasManager.GetAtlasUV(bt, AetherisClient.Rendering.BlockFace.Top);
                    int tileX = (int)(u0 * AtlasManager.TilesPerRow);
                    int tileY = (int)(v0 * AtlasManager.TilesPerCol);
                    Console.WriteLine($"{bt} Top -> Tile({tileX},{tileY}) UV=({u0:F4},{v0:F4})");
                }
                Console.WriteLine("=== END ATLAS DIAGNOSTICS ===");
            }
            // Log draw count on first frame
            if (frameCount == 0)
            {
                Console.WriteLine($"[Renderer] Drew {drawn} chunks in first frame");
            }
            // In Renderer.Render(), add after texture diagnostics:
            if (frameCount == 0 && AtlasManager.IsLoaded)
            {
                Console.WriteLine("=== ATLAS UV DIAGNOSTICS ===");
                Console.WriteLine($"Atlas: {AtlasManager.AtlasWidth}x{AtlasManager.AtlasHeight}");
                Console.WriteLine($"Tile: {AtlasManager.TileSize}px, Grid: {AtlasManager.TilesPerRow}x{AtlasManager.TilesPerCol}");
                Console.WriteLine($"Padding: 0.002 (0.2%)");
                Console.WriteLine();

                for (int i = 0; i <= 8; i++)
                {
                    if (!Enum.IsDefined(typeof(AetherisClient.Rendering.BlockType), i)) continue;

                    var bt = (AetherisClient.Rendering.BlockType)i;
                    var (u0, v0, u1, v1) = AtlasManager.GetAtlasUV(bt);

                    // Calculate which tile this maps to
                    int tileX = (int)(u0 / 0.25f);
                    int tileY = (int)(v0 / 0.25f);

                    Console.WriteLine($"{bt,-8} (ID:{i}) -> Tile({tileX},{tileY}) UV=({u0:F4},{v0:F4}) to ({u1:F4},{v1:F4})");
                }
                Console.WriteLine("=== END ATLAS DIAGNOSTICS ===");
            }
            frameCount++;
        }

        public void RemoveChunk(int cx, int cy, int cz)
        {
            var key = (cx, cy, cz);
            if (meshes.TryGetValue(key, out var md))
            {
                try
                {
                    if (md.Vbo != 0) GL.DeleteBuffer(md.Vbo);
                    if (md.Vao != 0) GL.DeleteVertexArray(md.Vao);

                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Renderer] GL delete error: " + ex);
                }
                meshes.Remove(key);
            }

            // Remove physics collider if we registered it
            int chunkId = ChunkKey(cx, cy, cz);
            if (physicsRegisteredChunks.Contains(chunkId))
            {
                try
                {
                    Physics?.RemoveChunkCollider(chunkId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Renderer] Error removing chunk collider from physics: " + ex);
                }
                physicsRegisteredChunks.Remove(chunkId);
            }
        }

        public void Clear()
        {
            // Remove physics colliders we registered
            if (Physics != null)
            {
                foreach (var id in physicsRegisteredChunks.ToArray())
                {
                    try
                    {
                        Physics.RemoveChunkCollider(id);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[Renderer] Error removing chunk collider during Clear(): " + ex);
                    }
                }
            }
            physicsRegisteredChunks.Clear();

            foreach (var md in meshes.Values)
            {
                try
                {
                    if (md.Vbo != 0) GL.DeleteBuffer(md.Vbo);
                    if (md.Vao != 0) GL.DeleteVertexArray(md.Vao);
                }
                catch { }
            }
            meshes.Clear();

            if (shaderProgram != 0)
            {
                try { GL.DeleteProgram(shaderProgram); } catch { }
                shaderProgram = 0;
            }

            // Only delete renderer-local atlas texture. AtlasManager's texture is managed by AtlasManager.
            if (localAtlasTextureId != 0)
            {
                try { GL.DeleteTexture(localAtlasTextureId); } catch { }
                localAtlasTextureId = 0;
            }
        }

        public void Dispose()
        {
            Clear();
        }

        public int GetVisibleChunkCount() => lastVisibleCount;
        public int GetTotalChunkCount() => meshes.Count;



        // Replace your UploadMesh method with this version:

        private void UploadMesh(int cx, int cy, int cz, float[] interleavedData)
        {
            if (interleavedData == null || interleavedData.Length == 0)
            {

                return;
            }

            EnsureShader();
            RemoveChunk(cx, cy, cz);

            float[] uploadData = interleavedData;
            int detectedStride = 0;

            // More robust stride detection
            // Check if data has blockType (stride 7) by examining the 7th value
            if (interleavedData.Length >= 7)
            {
                // Sample a few vertices to check if 7th value looks like a blockType (0-8)
                bool looksLikeBlockType = true;
                int samplesToCheck = Math.Min(5, interleavedData.Length / 7);

                for (int i = 0; i < samplesToCheck; i++)
                {
                    int idx = i * 7 + 6;
                    if (idx >= interleavedData.Length) break;

                    float val = interleavedData[idx];
                    // BlockType should be integer in range [0, 8]
                    if (val < -0.5f || val > 8.5f || Math.Abs(val - Math.Round(val)) > 0.1f)
                    {
                        looksLikeBlockType = false;
                        break;
                    }
                }

                if (looksLikeBlockType && interleavedData.Length % 7 == 0)
                {
                    detectedStride = 7;
                }
                else if (interleavedData.Length % 8 == 0)
                {
                    detectedStride = 8;
                }
                else
                {
                    Console.WriteLine($"[Renderer] Chunk ({cx},{cy},{cz}): Cannot determine stride from {interleavedData.Length} floats");
                    return;
                }
            }
            else if (interleavedData.Length % 8 == 0)
            {
                detectedStride = 8;
            }
            else
            {
                Console.WriteLine($"[Renderer] Chunk ({cx},{cy},{cz}): Data too short or invalid stride");
                return;
            }

            // Convert if needed
            if (detectedStride == 7)
            {
                if (meshes.Count < 3)
                {
                    Console.WriteLine($"[Renderer] Chunk ({cx},{cy},{cz}) BEFORE conversion:");
                    Console.WriteLine($"  Length: {interleavedData.Length}, vertices: {interleavedData.Length / 7}");
                    Console.WriteLine($"  First vertex: pos=({interleavedData[0]:F2},{interleavedData[1]:F2},{interleavedData[2]:F2}), " +
                                    $"normal=({interleavedData[3]:F2},{interleavedData[4]:F2},{interleavedData[5]:F2}), " +
                                    $"blockType={interleavedData[6]:F0}");
                }

                try
                {
                    uploadData = ConvertMeshWithBlockTypeToUVs(interleavedData);

                    if (uploadData == null || uploadData.Length == 0)
                    {
                        Console.WriteLine($"[Renderer] Chunk ({cx},{cy},{cz}): Conversion produced empty mesh");
                        return;
                    }

                    if (meshes.Count < 3)
                    {
                        Console.WriteLine($"[Renderer] Chunk ({cx},{cy},{cz}) AFTER conversion:");
                        Console.WriteLine($"  Length: {uploadData.Length}, vertices: {uploadData.Length / 8}");
                        Console.WriteLine($"  First vertex: pos=({uploadData[0]:F2},{uploadData[1]:F2},{uploadData[2]:F2}), " +
                                    $"normal=({uploadData[3]:F2},{uploadData[4]:F2},{uploadData[5]:F2}), " +
                                    $"uv=({uploadData[6]:F4},{uploadData[7]:F4})");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Renderer] Chunk ({cx},{cy},{cz}) conversion error: {ex.Message}");
                    Console.WriteLine($"[Renderer] Stack trace: {ex.StackTrace}");
                    return;
                }
            }
            if (uploadData.Length % 8 == 0 && uploadData.Length > 0)
            {
                meshCache[(cx, cy, cz)] = uploadData;
            }
            // Final validation
            if (uploadData.Length % 8 != 0)
            {
                Console.WriteLine($"[Renderer] Chunk ({cx},{cy},{cz}): Final data has invalid stride ({uploadData.Length} floats)");
                return;
            }

            int vertexCount = uploadData.Length / 8;
            if (vertexCount == 0)
            {
                Console.WriteLine($"[Renderer] Chunk ({cx},{cy},{cz}): Zero vertices after conversion");
                return;
            }

            // Create OpenGL objects
            int vao = GL.GenVertexArray();
            int vbo = GL.GenBuffer();

            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);

            GL.BufferData(BufferTarget.ArrayBuffer,
                uploadData.Length * sizeof(float),
                uploadData,
                BufferUsageHint.StaticDraw);

            int stride = 8 * sizeof(float);

            // Position attribute (location = 0)
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);

            // Normal attribute (location = 1)
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));

            // UV attribute (location = 2)
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));

            GL.BindVertexArray(0);

            var model = Matrix4.CreateTranslation(cx * ClientConfig.CHUNK_SIZE, cy * ClientConfig.CHUNK_SIZE_Y, cz * ClientConfig.CHUNK_SIZE);

            Vector3 chunkCenter = new Vector3(
                cx * ClientConfig.CHUNK_SIZE + ClientConfig.CHUNK_SIZE * 0.5f,
                cy * ClientConfig.CHUNK_SIZE_Y + ClientConfig.CHUNK_SIZE_Y * 0.5f,
                cz * ClientConfig.CHUNK_SIZE + ClientConfig.CHUNK_SIZE * 0.5f
            );

            float radius = MathF.Sqrt(
                ClientConfig.CHUNK_SIZE * ClientConfig.CHUNK_SIZE +
                ClientConfig.CHUNK_SIZE_Y * ClientConfig.CHUNK_SIZE_Y +
                ClientConfig.CHUNK_SIZE * ClientConfig.CHUNK_SIZE
            ) * 0.5f;

            meshes[(cx, cy, cz)] = new MeshData(vao, vbo, vertexCount, model, chunkCenter, radius);

            // Register physics collider (if Physics manager assigned)
            try
            {
                var physMesh = GetPhysicsMesh(cx, cy, cz);
                if (physMesh != null && physMesh.Value.Vertices != null && physMesh.Value.Vertices.Length >= 3)
                {
                    int id = ChunkKey(cx, cy, cz);
                    if (Physics != null && !physicsRegisteredChunks.Contains(id))
                    {
                        Physics.AddChunkCollider(id, physMesh.Value.Vertices);
                        physicsRegisteredChunks.Add(id);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Renderer] Error registering chunk collider with physics: " + ex);
            }

            if (meshes.Count < 3)
            {
                Console.WriteLine($"[Renderer] Uploaded chunk ({cx},{cy},{cz}): VAO={vao}, VBO={vbo}, {vertexCount} vertices");
            }
            if (uploadData.Length % 8 == 0 && uploadData.Length > 0)
            {
                meshCache[(cx, cy, cz)] = uploadData;

                // TRIGGER THE CALLBACK HERE
                OnChunkMeshLoaded?.Invoke(cx, cy, cz, uploadData);
            }

        }
        private void EnsureShader()
        {
            if (shaderProgram != 0) return;

            int vs = CompileShader(ShaderType.VertexShader, VertexShaderSrc);
            int fs = CompileShader(ShaderType.FragmentShader, FragmentShaderSrc);

            shaderProgram = GL.CreateProgram();
            GL.AttachShader(shaderProgram, vs);
            GL.AttachShader(shaderProgram, fs);
            GL.LinkProgram(shaderProgram);
            GL.GetProgram(shaderProgram, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0)
            {
                string info = GL.GetProgramInfoLog(shaderProgram);
                GL.DeleteProgram(shaderProgram);
                shaderProgram = 0;
                throw new Exception("Shader link error: " + info);
            }

            GL.DetachShader(shaderProgram, vs);
            GL.DetachShader(shaderProgram, fs);
            GL.DeleteShader(vs);
            GL.DeleteShader(fs);

            locProjection = GL.GetUniformLocation(shaderProgram, "uProjection");
            locView = GL.GetUniformLocation(shaderProgram, "uView");
            locModel = GL.GetUniformLocation(shaderProgram, "uModel");
            locFogDecay = GL.GetUniformLocation(shaderProgram, "uFogDecay");
            locAtlasTexture = GL.GetUniformLocation(shaderProgram, "uAtlasTexture");
        }

        public struct PhysicsMeshData
        {
            public Vector3[] Vertices;
            public int[] Indices;
        }

        public PhysicsMeshData? GetPhysicsMesh(int cx, int cy, int cz)
        {
            var meshData = GetMeshData(cx, cy, cz);
            if (meshData == null || meshData.Length == 0) return null;

            // Extract just positions from stride-8 data
            int vertexCount = meshData.Length / 8;
            if (vertexCount < 3) return null;

            var vertices = new Vector3[vertexCount];

            Vector3 chunkOffset = new Vector3(
                cx * ClientConfig.CHUNK_SIZE,
                cy * ClientConfig.CHUNK_SIZE_Y,
                cz * ClientConfig.CHUNK_SIZE
            );

            // Extract vertices in world space
            for (int i = 0; i < vertexCount; i++)
            {
                int idx = i * 8;
                vertices[i] = new Vector3(
                    meshData[idx + 0],
                    meshData[idx + 1],
                    meshData[idx + 2]
                ) + chunkOffset;
            }

            // Validate mesh has reasonable bounds
            float minY = float.MaxValue;
            float maxY = float.MinValue;
            foreach (var v in vertices)
            {
                if (v.Y < minY) minY = v.Y;
                if (v.Y > maxY) maxY = v.Y;
            }



            var indices = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                indices[i] = i;

            return new PhysicsMeshData { Vertices = vertices, Indices = indices };
        }

        private int CompileShader(ShaderType type, string src)
        {
            int s = GL.CreateShader(type);
            GL.ShaderSource(s, src);
            GL.CompileShader(s);
            GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0)
            {
                string info = GL.GetShaderInfoLog(s);
                GL.DeleteShader(s);
                throw new Exception($"Shader compile error ({type}): {info}");
            }
            return s;
        }

        private void ExtractFrustumPlanes(Matrix4 viewProj)
        {
            frustumPlanes[0] = NormalizePlane(new Plane(
                viewProj.M14 + viewProj.M11,
                viewProj.M24 + viewProj.M21,
                viewProj.M34 + viewProj.M31,
                viewProj.M44 + viewProj.M41));

            frustumPlanes[1] = NormalizePlane(new Plane(
                viewProj.M14 - viewProj.M11,
                viewProj.M24 - viewProj.M21,
                viewProj.M34 - viewProj.M31,
                viewProj.M44 - viewProj.M41));

            frustumPlanes[2] = NormalizePlane(new Plane(
                viewProj.M14 + viewProj.M12,
                viewProj.M24 + viewProj.M22,
                viewProj.M34 + viewProj.M32,
                viewProj.M44 + viewProj.M42));

            frustumPlanes[3] = NormalizePlane(new Plane(
                viewProj.M14 - viewProj.M12,
                viewProj.M24 - viewProj.M22,
                viewProj.M34 - viewProj.M32,
                viewProj.M44 - viewProj.M42));

            frustumPlanes[4] = NormalizePlane(new Plane(
                viewProj.M14 + viewProj.M13,
                viewProj.M24 + viewProj.M23,
                viewProj.M34 + viewProj.M33,
                viewProj.M44 + viewProj.M43));

            frustumPlanes[5] = NormalizePlane(new Plane(
                viewProj.M14 - viewProj.M13,
                viewProj.M24 - viewProj.M23,
                viewProj.M34 - viewProj.M33,
                viewProj.M44 - viewProj.M43));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Plane NormalizePlane(Plane plane)
        {
            float len = MathF.Sqrt(plane.A * plane.A + plane.B * plane.B + plane.C * plane.C);
            if (len < 0.00001f) return plane;
            return new Plane(plane.A / len, plane.B / len, plane.C / len, plane.D / len);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsInFrustum(Vector3 center, float radius)
        {
            for (int i = 0; i < 6; i++)
            {
                float distance = frustumPlanes[i].A * center.X +
                                 frustumPlanes[i].B * center.Y +
                                 frustumPlanes[i].C * center.Z +
                                 frustumPlanes[i].D;

                if (distance < -radius)
                    return false;
            }
            return true;
        }

        private struct Plane
        {
            public float A, B, C, D;
            public Plane(float a, float b, float c, float d) { A = a; B = b; C = c; D = d; }
        }

        // Helper: consistent chunk-to-int key (same as Game.ChunkKey)
        private static int ChunkKey(int cx, int cy, int cz)
        {
            unchecked
            {
                return (cx * 73856093) ^ (cy * 19349663) ^ (cz * 83492791);
            }
        }
    }
}
