using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using StbImageSharp;

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
        private int atlasTextureId = 0;

        private int frameCount = 0;
        private int lastVisibleCount = 0;

        public int RenderDistanceChunks { get; set; } = Config.RENDER_DISTANCE;
        public float FogDecay = 0.003f;

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
        /// Load texture atlas from file. Falls back to procedural if file not found.
        /// Call this once during initialization.
        /// </summary>
        public void LoadTextureAtlas(string path)
        {
            if (System.IO.File.Exists(path))
            {
                try
                {
                    atlasTextureId = LoadTextureFromFile(path);
                    Console.WriteLine($"[Renderer] Loaded texture atlas: {path}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Renderer] Failed to load texture '{path}': {ex.Message}");
                    Console.WriteLine("[Renderer] Falling back to procedural atlas");
                    CreateProceduralAtlas();
                }
            }
            else
            {
                Console.WriteLine($"[Renderer] Texture file not found: {path}");
                Console.WriteLine("[Renderer] Using procedural atlas as fallback");
                CreateProceduralAtlas();
            }
        }

        /// <summary>
        /// Create a procedural texture atlas if no image file is available
        /// </summary>
        public void CreateProceduralAtlas()
        {
            const int atlasSize = 256; // 4x4 grid, 64px per tile
            const int tileSize = 64;
            
            byte[] pixels = new byte[atlasSize * atlasSize * 4];
            
            // Define colors for each block type
            var blockColors = new Dictionary<int, (byte, byte, byte)>
            {
                [0] = (128, 128, 128), // Stone - gray
                [1] = (139, 69, 19),   // Dirt - brown
                [2] = (34, 139, 34),   // Grass - green
                [3] = (194, 178, 128), // Sand - tan
                [4] = (255, 250, 250), // Snow - white
                [5] = (105, 105, 105), // Gravel - dark gray
                [6] = (101, 67, 33),   // Wood - dark brown
                [7] = (46, 125, 50)    // Leaves - dark green
            };
            
            for (int ty = 0; ty < 4; ty++)
            {
                for (int tx = 0; tx < 4; tx++)
                {
                    int tileIndex = ty * 4 + tx;
                    var color = blockColors.ContainsKey(tileIndex) 
                        ? blockColors[tileIndex] 
                        : ((byte)255, (byte)0, (byte)255); // Magenta fallback
                    
                    Console.WriteLine($"[Renderer] Creating tile {tileIndex} at ({tx},{ty}) with color RGB({color.Item1},{color.Item2},{color.Item3})");
                    
                    // Fill tile with color and some noise
                    for (int py = 0; py < tileSize; py++)
                    {
                        for (int px = 0; px < tileSize; px++)
                        {
                            int x = tx * tileSize + px;
                            int y = ty * tileSize + py;
                            int idx = (y * atlasSize + x) * 4;
                            
                            // Add deterministic noise variation using position
                            int seed = (tileIndex * 10000) + (px * 100) + py;
                            seed = (seed * 1103515245 + 12345) & 0x7fffffff; // LCG
                            int noise = (seed % 41) - 20; // Range: -20 to 20
                            
                            pixels[idx + 0] = (byte)Math.Clamp(color.Item1 + noise, 0, 255);
                            pixels[idx + 1] = (byte)Math.Clamp(color.Item2 + noise, 0, 255);
                            pixels[idx + 2] = (byte)Math.Clamp(color.Item3 + noise, 0, 255);
                            pixels[idx + 3] = 255;
                        }
                    }
                }
            }
            
            atlasTextureId = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, atlasTextureId);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                atlasSize, atlasSize, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            
            // CRITICAL: Use ClampToEdge to prevent atlas bleeding at tile boundaries
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            
            Console.WriteLine("[Renderer] Created procedural texture atlas");
        }

        /// <summary>
        /// Load texture from image file using StbImageSharp
        /// Add NuGet package: StbImageSharp
        /// </summary>
        private int LoadTextureFromFile(string path)
        {
            // Using StbImageSharp for cross-platform image loading
            // Install via: dotnet add package StbImageSharp
            
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
                if (GL.GetString(StringName.Extensions).Contains("GL_EXT_texture_filter_anisotropic"))
                {
                    float maxAniso;
                    GL.GetFloat((GetPName)0x84FF, out maxAniso); // MAX_TEXTURE_MAX_ANISOTROPY_EXT
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
            if (meshes.Count == 0) return;

            EnsureShader();
            if (atlasTextureId == 0)
            {
                CreateProceduralAtlas();
            }

            Matrix4 viewProj = view * projection;
            ExtractFrustumPlanes(viewProj);

            visibleMeshes.Clear();

            float maxRenderDistance = RenderDistanceChunks * Config.CHUNK_SIZE * 1.5f;
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

            visibleMeshes.Sort((a, b) =>
            {
                float da = (a.Center - cameraPos).LengthSquared;
                float db = (b.Center - cameraPos).LengthSquared;
                return da.CompareTo(db);
            });

            GL.UseProgram(shaderProgram);
            GL.UniformMatrix4(locProjection, false, ref projection);
            GL.UniformMatrix4(locView, false, ref view);
            GL.Uniform1(locFogDecay, FogDecay);
            
            // Bind texture atlas
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, atlasTextureId);
            GL.Uniform1(locAtlasTexture, 0);

            foreach (var md in visibleMeshes)
            {
                var model = md.Model;
                GL.UniformMatrix4(locModel, false, ref model);
                GL.BindVertexArray(md.Vao);
                GL.DrawArrays(PrimitiveType.Triangles, 0, md.VertexCount);
            }

            GL.BindVertexArray(0);
            GL.UseProgram(0);

            frameCount++;
            if (frameCount % 300 == 0)
                Console.WriteLine($"[Renderer] Visible: {lastVisibleCount}/{meshes.Count}");
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
        }

        public void Clear()
        {
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
            
            if (atlasTextureId != 0)
            {
                try { GL.DeleteTexture(atlasTextureId); } catch { }
                atlasTextureId = 0;
            }
        }

        public void Dispose()
        {
            Clear();
        }

        public int GetVisibleChunkCount() => lastVisibleCount;
        public int GetTotalChunkCount() => meshes.Count;

        private void UploadMesh(int cx, int cy, int cz, float[] interleavedData)
        {
            if (interleavedData == null || interleavedData.Length == 0) return;

            EnsureShader();
            RemoveChunk(cx, cy, cz);

            int vao = GL.GenVertexArray();
            int vbo = GL.GenBuffer();

            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer,
                interleavedData.Length * sizeof(float),
                interleavedData,
                BufferUsageHint.StaticDraw);

            int stride = 8 * sizeof(float); // 3 pos + 3 normal + 2 uv
            
            // Position attribute
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            
            // Normal attribute
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            
            // UV attribute
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));

            GL.BindVertexArray(0);

            var model = Matrix4.CreateTranslation(cx * Config.CHUNK_SIZE, cy * Config.CHUNK_SIZE_Y, cz * Config.CHUNK_SIZE);

            Vector3 chunkCenter = new Vector3(
                cx * Config.CHUNK_SIZE + Config.CHUNK_SIZE * 0.5f,
                cy * Config.CHUNK_SIZE_Y + Config.CHUNK_SIZE_Y * 0.5f,
                cz * Config.CHUNK_SIZE + Config.CHUNK_SIZE * 0.5f
            );
            float radius = MathF.Sqrt(
                Config.CHUNK_SIZE * Config.CHUNK_SIZE +
                Config.CHUNK_SIZE_Y * Config.CHUNK_SIZE_Y +
                Config.CHUNK_SIZE * Config.CHUNK_SIZE
            ) * 0.5f;

            int vertexCount = interleavedData.Length / 8;
            meshes[(cx, cy, cz)] = new MeshData(vao, vbo, vertexCount, model, chunkCenter, radius);
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
    }
}
