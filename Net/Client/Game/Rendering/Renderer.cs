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

        public PSXVisualEffects psxEffects;
        private readonly Dictionary<(int cx, int cy, int cz), MeshData> meshes = new();
        private readonly ConcurrentQueue<Action> uploadQueue = new();
        private readonly List<MeshData> visibleMeshes = new(1024);
        private readonly Plane[] frustumPlanes = new Plane[6];

        // Original shader still kept as fallback

        private int shaderProgram;
        private int locProjection, locView, locModel, locFogDecay, locAtlasTexture;

        // New uniforms for PS2-ish shader:
        private int locNormalMatrix;
        private int locCameraPos;
        private int locSpecPower;
        private int locSpecStrength;
        private int locFogColor;
        private int locSkyColor;
        private int locGroundColor;


        public delegate void ChunkMeshLoadedCallback(int cx, int cy, int cz, float[] meshData);
        public ChunkMeshLoadedCallback? OnChunkMeshLoaded;

        private int localAtlasTextureId = 0;
        private int frameCount = 0;
        private int lastVisibleCount = 0;

        public int RenderDistanceChunks { get; set; } = ClientConfig.RENDER_DISTANCE;
        public float FogDecay = 0.003f;


        // Track screen resolution for PSX effects
        public int ScreenWidth { get; set; } = 1280;
        public int ScreenHeight { get; set; } = 720;

        // Toggle PSX effects on/off
        public bool UsePSXEffects { get; set; } = true;

        private readonly HashSet<int> physicsRegisteredChunks = new();
        private readonly Dictionary<(int, int, int), float[]> meshCache = new();

        #region Shaders
        private const string VertexShaderSrc = @"

#version 330 core

layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;

out vec3 vNormal;
out vec3 vWorldPos;
out vec3 vViewPos;
out vec2 vUV;

uniform mat4 uProjection;
uniform mat4 uView;
uniform mat4 uModel;
uniform mat3 uNormalMatrix; // inverse-transpose of model's upper-left 3x3

void main()
{
    vec4 worldPos = uModel * vec4(aPos, 1.0);
    vWorldPos = worldPos.xyz;
    vec4 viewPos4 = uView * worldPos;
    vViewPos = viewPos4.xyz;                  // view-space position (useful for fog)
    vNormal = normalize(uNormalMatrix * aNormal);
    vUV = aUV;
    gl_Position = uProjection * viewPos4;
}

";


        private const string FragmentShaderSrc = @"
#version 330 core

in vec3 vNormal;
in vec3 vWorldPos;
in vec3 vViewPos;
in vec2 vUV;

out vec4 FragColor;

uniform sampler2D uAtlasTexture;
uniform float uFogDecay;
uniform vec3 uCameraPos;
uniform float uSpecPower;
uniform float uSpecStrength;
uniform vec3 uFogColor;
uniform vec3 uSkyColor;
uniform vec3 uGroundColor;

void main()
{
    vec3 N = normalize(vNormal);
    vec3 baseTex = texture(uAtlasTexture, vUV).rgb;

    // Two directional lights
    vec3 light1Dir = normalize(vec3(0.5, 1.0, 0.3));
    vec3 light2Dir = normalize(vec3(-0.3, 0.5, -0.8));

    float diff1 = max(dot(N, light1Dir), 0.0);
    float diff2 = max(dot(N, light2Dir), 0.0) * 0.45;

    // Hemispheric ambient
    float hemi = N.y * 0.5 + 0.5;
    vec3 hemiAmbient = mix(uGroundColor, uSkyColor, hemi) * 0.28;

    vec3 diffuse = (diff1 + diff2) * baseTex;

    // Blinn-Phong specular
    vec3 viewDir = normalize(uCameraPos - vWorldPos);
    vec3 half1 = normalize(light1Dir + viewDir);
    vec3 half2 = normalize(light2Dir + viewDir);
    float spec1 = pow(max(dot(N, half1), 0.0), uSpecPower);
    float spec2 = pow(max(dot(N, half2), 0.0), uSpecPower);
    float spec = (spec1 + spec2) * 0.5 * uSpecStrength;

    vec3 lit = hemiAmbient + diffuse + vec3(spec);

    // Reinhard tonemapping + mild desaturation
    vec3 tone = lit / (lit + vec3(1.0));
    float gray = dot(tone, vec3(0.299, 0.587, 0.114));
    tone = mix(vec3(gray), tone, 0.88);

    // Gamma correction
    tone = pow(tone, vec3(1.0 / 2.2));

    // Fog using view-space distance (exp^2)
    float fogDist = length(vViewPos);
    float f = exp(- (fogDist * uFogDecay) * (fogDist * uFogDecay));
    f = clamp(f, 0.0, 1.0);

    vec3 final = mix(uFogColor, tone * baseTex, f);

    FragColor = vec4(final, 1.0);
}
";

        #endregion

        public Renderer()
        {
            psxEffects = PSXVisualEffects.CreateMinimal();

        }

        public void LoadTextureAtlas(string path)
        {
            try
            {
                AtlasManager.LoadAtlas(path, preferredTileSize: 64);
                if (AtlasManager.IsLoaded && AtlasManager.AtlasTextureId != 0)
                {
                    Console.WriteLine($"[Renderer] AtlasManager loaded atlas: {path} (GL id {AtlasManager.AtlasTextureId})");
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Renderer] AtlasManager failed to load atlas: {ex.Message}");
            }

            if (System.IO.File.Exists(path))
            {
                try
                {
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

            Console.WriteLine("[Renderer] Using procedural atlas as fallback");
            CreateProceduralAtlas();
        }

        public void CreateProceduralAtlas()
        {
            const int atlasSize = 256;
            const int tileSize = 64;
            byte[] pixels = new byte[atlasSize * atlasSize * 4];

            var blockColors = new Dictionary<int, (byte, byte, byte)>
            {
                [0] = (128, 128, 128),
                [1] = (139, 69, 19),
                [2] = (34, 139, 34),
                [3] = (194, 178, 128),
                [4] = (255, 250, 250),
                [5] = (255, 255, 105),
                [6] = (101, 67, 33),
                [7] = (46, 125, 50)
            };

            var rand = new Random(12345);

            for (int ty = 0; ty < 4; ty++)
            {
                for (int tx = 0; tx < 4; tx++)
                {
                    int tileIndex = ty * 4 + tx;
                    var color = blockColors.ContainsKey(tileIndex)
                        ? blockColors[tileIndex]
                        : ((byte)255, (byte)0, (byte)255);

                    for (int py = 0; py < tileSize; py++)
                    {
                        for (int px = 0; px < tileSize; px++)
                        {
                            int x = tx * tileSize + px;
                            int y = ty * tileSize + py;
                            int idx = (y * atlasSize + x) * 4;

                            int noise = (int)((rand.NextDouble() - 0.5) * 30);
                            float gradient = 0.8f + 0.2f * ((float)py / tileSize);

                            pixels[idx + 0] = (byte)Math.Clamp((color.Item1 + noise) * gradient, 0, 255);
                            pixels[idx + 1] = (byte)Math.Clamp((color.Item2 + noise) * gradient, 0, 255);
                            pixels[idx + 2] = (byte)Math.Clamp((color.Item3 + noise) * gradient, 0, 255);
                            pixels[idx + 3] = 255;
                        }
                    }
                }
            }

            if (localAtlasTextureId != 0)
            {
                try { GL.DeleteTexture(localAtlasTextureId); } catch { }
                localAtlasTextureId = 0;
            }

            localAtlasTextureId = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, localAtlasTextureId);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                atlasSize, atlasSize, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            GL.BindTexture(TextureTarget.Texture2D, 0);
            Console.WriteLine("[Renderer] Created new procedural texture atlas (renderer-local).");
        }

        public float[]? GetMeshData(int cx, int cy, int cz)
        {
            return meshCache.TryGetValue((cx, cy, cz), out var data) ? data : null;
        }

        private float[] ConvertMeshWithBlockTypeToUVs(float[] src)
        {
            const int srcStride = 7;
            const int dstStride = 8;

            if (src == null || src.Length == 0) return Array.Empty<float>();

            int vertexCount = src.Length / srcStride;
            var dst = new float[vertexCount * dstStride];

            bool atlasLoaded = AtlasManager.IsLoaded;

            for (int triIdx = 0; triIdx < vertexCount; triIdx += 3)
            {
                if (triIdx + 2 >= vertexCount) break;

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

                AetherisClient.Rendering.BlockType clientType;
                if (Enum.IsDefined(typeof(AetherisClient.Rendering.BlockType), blockTypeInt))
                    clientType = (AetherisClient.Rendering.BlockType)blockTypeInt;
                else
                    clientType = AetherisClient.Rendering.BlockType.Stone;

                Vector3 triNormal = (normal[0] + normal[1] + normal[2]) / 3.0f;
                Vector3 absNormal = new Vector3(
                    MathF.Abs(triNormal.X),
                    MathF.Abs(triNormal.Y),
                    MathF.Abs(triNormal.Z)
                );

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

                int axis;
                if (absNormal.Y > absNormal.X && absNormal.Y > absNormal.Z)
                    axis = 1;
                else if (absNormal.X > absNormal.Z)
                    axis = 0;
                else
                    axis = 2;

                Vector3 avgPos = (pos[0] + pos[1] + pos[2]) / 3.0f;
                int blockX = (int)MathF.Floor(avgPos.X);
                int blockY = (int)MathF.Floor(avgPos.Y);
                int blockZ = (int)MathF.Floor(avgPos.Z);
                int seed = blockX * 73856093 ^ blockY * 19349663 ^ blockZ * 83492791;
                float random = (float)((seed & 0x7FFFFFFF) / (float)0x7FFFFFFF);
                int rotation = (int)(random * 4);

                const float texScale = 1.0f / 8.0f;

                for (int i = 0; i < 3; i++)
                {
                    int vi = triIdx + i;
                    int si = vi * srcStride;
                    int di = vi * dstStride;

                    dst[di + 0] = src[si + 0];
                    dst[di + 1] = src[si + 1];
                    dst[di + 2] = src[si + 2];
                    dst[di + 3] = src[si + 3];
                    dst[di + 4] = src[si + 4];
                    dst[di + 5] = src[si + 5];

                    float u2d, v2d;
                    switch (axis)
                    {
                        case 0:
                            u2d = pos[i].Z * texScale;
                            v2d = pos[i].Y * texScale;
                            break;
                        case 1:
                            u2d = pos[i].X * texScale;
                            v2d = pos[i].Z * texScale;
                            break;
                        default:
                            u2d = pos[i].X * texScale;
                            v2d = pos[i].Y * texScale;
                            break;
                    }

                    u2d = u2d - MathF.Floor(u2d);
                    v2d = v2d - MathF.Floor(v2d);

                    float u2dRot = u2d, v2dRot = v2d;
                    switch (rotation)
                    {
                        case 1: u2dRot = 1 - v2d; v2dRot = u2d; break;
                        case 2: u2dRot = 1 - u2d; v2dRot = 1 - v2d; break;
                        case 3: u2dRot = v2d; v2dRot = 1 - u2d; break;
                    }

                    float u = uMin + u2dRot * (uMax - uMin);
                    float v = vMin + v2dRot * (vMax - vMin);

                    dst[di + 6] = Math.Clamp(u, uMin, uMax);
                    dst[di + 7] = Math.Clamp(v, vMin, vMax);
                }
            }

            return dst;
        }

        private int LoadTextureFromFile(string path)
        {
            StbImage.stbi_set_flip_vertically_on_load(1);

            using (var stream = System.IO.File.OpenRead(path))
            {
                ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

                if (image == null)
                    throw new Exception("Failed to load image");

                int textureId = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, textureId);

                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                    image.Width, image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, image.Data);

                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.NearestMipmapLinear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

                if (GL.GetString(StringName.Extensions)?.Contains("GL_EXT_texture_filter_anisotropic") == true)
                {
                    GL.GetFloat((GetPName)0x84FF, out float maxAniso);
                    GL.TexParameter(TextureTarget.Texture2D, (TextureParameterName)0x84FE, Math.Min(4.0f, maxAniso));
                }

                GL.BindTexture(TextureTarget.Texture2D, 0);
                Console.WriteLine($"[Renderer] Loaded {image.Width}x{image.Height} texture with {image.Comp} components");
                return textureId;
            }
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
                frameCount++;
                return;
            }

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
                Console.WriteLine($"Using PSX Effects: {UsePSXEffects}");
                Console.WriteLine($"Texture to bind: {textureToBind}");
                Console.WriteLine($"Total meshes: {meshes.Count}");
                Console.WriteLine($"Camera position: {cameraPos}");
                Console.WriteLine($"Screen resolution: {ScreenWidth}x{ScreenHeight}");

                GL.BindTexture(TextureTarget.Texture2D, textureToBind);
                GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureWidth, out int w);
                GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureHeight, out int h);
                Console.WriteLine($"Texture dimensions: {w}x{h}");
                GL.BindTexture(TextureTarget.Texture2D, 0);

                if (meshes.Count > 0)
                {
                    var firstMesh = meshes.Values.First();
                    Console.WriteLine($"First mesh: VAO={firstMesh.Vao}, VBO={firstMesh.Vbo}, vertices={firstMesh.VertexCount}");
                }

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
                if (distSq > maxDistSq) continue;

                if (IsInFrustum(md.Center, md.Radius))
                {
                    visibleMeshes.Add(md);
                }
            }

            lastVisibleCount = visibleMeshes.Count;

            if (visibleMeshes.Count == 0)
            {
                frameCount++;
                return;
            }

            // Sort by distance (front to back)
            visibleMeshes.Sort((a, b) =>
            {
                float da = (a.Center - cameraPos).LengthSquared;
                float db = (b.Center - cameraPos).LengthSquared;
                return da.CompareTo(db);
            });

            // === PSX SHADER PATH ===
            if (UsePSXEffects)
            {
                psxEffects.BeginPSXRender(
       projection,
       view,
       FogDecay,
       textureToBind,
       new Vector2(ScreenWidth, ScreenHeight),
       cameraPos  // Add this parameter
   );

                // Draw all visible meshes with PSX shader
                foreach (var md in visibleMeshes)
                {
                    if (md.VertexCount == 0) continue;

                    var model = md.Model;
                    psxEffects.SetModelMatrix(model);

                    // Set normal matrix
                    Matrix3 normalMat = new Matrix3(model);
                    normalMat = Matrix3.Transpose(normalMat.Inverted());
                    GL.UniformMatrix3(locNormalMatrix, false, ref normalMat);

                    GL.BindVertexArray(md.Vao);
                    GL.DrawArrays(PrimitiveType.Triangles, 0, md.VertexCount);
                }


            }
            // === ORIGINAL SHADER PATH (Fallback) ===
            else
            {
                EnsureShader();

                GL.UseProgram(shaderProgram);

                // Projection & view (same as before)
                GL.UniformMatrix4(locProjection, false, ref projection);
                GL.UniformMatrix4(locView, false, ref view);
                GL.Uniform1(locFogDecay, FogDecay);

                // Texture
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, textureToBind);
                GL.Uniform1(locAtlasTexture, 0);

                // Per-frame uniforms (camera + material/fog presets)
                GL.Uniform3(locCameraPos, cameraPos);
                GL.Uniform1(locSpecPower, 32.0f);     // tweak: shininess
                GL.Uniform1(locSpecStrength, 0.6f);   // tweak: specular intensity
                GL.Uniform3(locFogColor, 0.5f, 0.6f, 0.7f);
                GL.Uniform3(locSkyColor, 0.6f, 0.7f, 0.95f);
                GL.Uniform3(locGroundColor, 0.28f, 0.22f, 0.16f);

                foreach (var md in visibleMeshes)
                {
                    if (md.VertexCount == 0) continue;

                    var model = md.Model;

                    // send model matrix
                    GL.UniformMatrix4(locModel, false, ref model);

                    // compute normal matrix = inverse-transpose of model's 3x3
                    // build 3x3 from model:
                    Matrix3 normalMat = new Matrix3(model);
                    // invert + transpose:
                    // Depending on your OpenTK version you might have Inverted()/Transposed() helpers:
                    // normalMat = normalMat.Inverted();
                    // normalMat = normalMat.Transposed();
                    // Fallback (works in most versions):
                    normalMat = Matrix3.Transpose(normalMat.Inverted());

                    GL.UniformMatrix3(locNormalMatrix, false, ref normalMat);

                    GL.BindVertexArray(md.Vao);
                    GL.DrawArrays(PrimitiveType.Triangles, 0, md.VertexCount);
                }



            }

            frameCount++;
        }


        public void Clear()
        {
            // Remove physics colliders we registered

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


            float[] uploadData = interleavedData;
            int detectedStride = 0;
            if (uploadData.Length >= 8 && meshes.Count < 3)
            {
                float expectedX = cx * ClientConfig.CHUNK_SIZE;
                float expectedY = cy * ClientConfig.CHUNK_SIZE_Y;
                float expectedZ = cz * ClientConfig.CHUNK_SIZE;

                Console.WriteLine($"[Renderer] Chunk ({cx},{cy},{cz})");
                Console.WriteLine($"[Renderer]   Expected world range: ({expectedX}-{expectedX + ClientConfig.CHUNK_SIZE}, {expectedY}-{expectedY + ClientConfig.CHUNK_SIZE_Y}, {expectedZ}-{expectedZ + ClientConfig.CHUNK_SIZE})");
                Console.WriteLine($"[Renderer]   First vertex at: ({uploadData[0]:F1}, {uploadData[1]:F1}, {uploadData[2]:F1})");

                bool inExpectedRange =
                    uploadData[0] >= expectedX && uploadData[0] <= expectedX + ClientConfig.CHUNK_SIZE &&
                    uploadData[1] >= expectedY && uploadData[1] <= expectedY + ClientConfig.CHUNK_SIZE_Y &&
                    uploadData[2] >= expectedZ && uploadData[2] <= expectedZ + ClientConfig.CHUNK_SIZE;

                Console.WriteLine($"[Renderer]   Vertex in expected range: {inExpectedRange}");
            }
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

            var model = Matrix4.Identity;

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


            if (meshes.Count < 3)
            {
                Console.WriteLine($"[Renderer] Uploaded chunk ({cx},{cy},{cz}): VAO={vao}, VBO={vbo}, {vertexCount} vertices");
            }
            if (uploadData.Length % 8 == 0 && uploadData.Length > 0)
            {
                meshCache[(cx, cy, cz)] = uploadData;

                // TRIGGER THE CALLBACK HERE

            }

            OnChunkMeshLoaded?.Invoke(cx, cy, cz, uploadData);

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

            // New uniforms for PS2-like lighting & fog
            locNormalMatrix = GL.GetUniformLocation(shaderProgram, "uNormalMatrix");
            locCameraPos = GL.GetUniformLocation(shaderProgram, "uCameraPos");
            locSpecPower = GL.GetUniformLocation(shaderProgram, "uSpecPower");
            locSpecStrength = GL.GetUniformLocation(shaderProgram, "uSpecStrength");
            locFogColor = GL.GetUniformLocation(shaderProgram, "uFogColor");
            locSkyColor = GL.GetUniformLocation(shaderProgram, "uSkyColor");
            locGroundColor = GL.GetUniformLocation(shaderProgram, "uGroundColor");
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
