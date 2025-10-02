using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Aetheris
{
    public class Renderer : IDisposable
    {
        private record MeshData(int Vao, int Vbo, int VertexCount, Matrix4 Model, Vector3 Center, float Radius);

        // All loaded chunk meshes (key: chunk coords)
        private readonly Dictionary<(int cx, int cy, int cz), MeshData> meshes = new();

        // Queue for thread-safe mesh uploads
        private readonly ConcurrentQueue<Action> uploadQueue = new();

        // Visible meshes collected per-frame
        private readonly List<MeshData> visibleMeshes = new(1024);

        // Frustum planes for sphere-frustum tests
        private readonly Plane[] frustumPlanes = new Plane[6];

        // Shader program + uniform locations
        private int shaderProgram;
        private int locProjection, locView, locModel, locFogDecay;

        // Stats
        private int frameCount = 0;
        private int lastVisibleCount = 0;

        // How many chunks to render around player (in chunks). Default read from Config.
        public int RenderDistanceChunks { get; set; } = Config.RENDER_DISTANCE;

        // You can tweak this to reduce fog aggression; smaller = less fog.
        public float FogDecay = 0.003f;

        #region Shaders
        private const string VertexShaderSrc = @"
#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;

out vec3 vNormal;
out vec3 vFragPos;

uniform mat4 uProjection;
uniform mat4 uView;
uniform mat4 uModel;

void main()
{
    vec4 worldPos = uModel * vec4(aPos, 1.0);
    vFragPos = worldPos.xyz;
    vNormal = mat3(uModel) * aNormal;
    gl_Position = uProjection * uView * worldPos;
}
";

        private const string FragmentShaderSrc = @"
#version 330 core
in vec3 vNormal;
in vec3 vFragPos;

out vec4 FragColor;

uniform float uFogDecay;

void main()
{
    vec3 n = normalize(vNormal);
    
    // Lighting
    vec3 light1Dir = normalize(vec3(0.5, 1.0, 0.3));
    vec3 light2Dir = normalize(vec3(-0.3, 0.5, -0.8));
    float diffuse1 = max(dot(n, light1Dir), 0.0);
    float diffuse2 = max(dot(n, light2Dir), 0.0) * 0.4;
    float ambient = 0.3;
    float light = ambient + diffuse1 + diffuse2;

    // Fog (exponential)
    float fogDistance = length(vFragPos);
    float fogFactor = exp(-fogDistance * uFogDecay);
    fogFactor = clamp(fogFactor, 0.0, 1.0);

    vec3 color = vec3(0.7, 0.72, 0.75) * light;
    vec3 fogColor = vec3(0.5, 0.6, 0.7);
    vec3 finalColor = mix(fogColor, color, fogFactor);
    
    FragColor = vec4(finalColor, 1.0);
}
";
        #endregion

        public Renderer() { }

        // ---------------- Public API ----------------

        /// <summary>
        /// Enqueue mesh data for an async upload (thread-safe).
        /// </summary>
        public void EnqueueMeshForChunk(int cx, int cy, int cz, float[] interleavedPosNormal)
        {
            uploadQueue.Enqueue(() => UploadMesh(cx, cy, cz, interleavedPosNormal));
        }

        /// <summary>
        /// Immediately upload mesh (useful when on main thread or pre-loading).
        /// </summary>
        public void LoadMeshForChunk(int cx, int cy, int cz, float[] interleavedPosNormal)
        {
            UploadMesh(cx, cy, cz, interleavedPosNormal);
        }

        /// <summary>
        /// Process a limited number of pending uploads per-frame to avoid stutters.
        /// Call from main thread each frame.
        /// </summary>
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

        /// <summary>
        /// Main render call. projection and view should match the camera; cameraPos is world-space.
        /// </summary>
        public void Render(Matrix4 projection, Matrix4 view, Vector3 cameraPos)
        {
            if (meshes.Count == 0) return;

            EnsureShader();

            // Build frustum planes from projection*view every frame
            Matrix4 viewProj = view * projection;
            ExtractFrustumPlanes(viewProj);

            visibleMeshes.Clear();

            // Distance-based culling distance (in world units)
            float maxRenderDistance = RenderDistanceChunks * Config.CHUNK_SIZE * 1.5f;
            float maxDistSq = maxRenderDistance * maxRenderDistance;

            foreach (var md in meshes.Values)
            {
                // First check: distance culling
                float distSq = (md.Center - cameraPos).LengthSquared;
                if (distSq > maxDistSq)
                    continue;

                // Second check: frustum culling
                if (IsInFrustum(md.Center, md.Radius))
                {
                    visibleMeshes.Add(md);
                }
            }

            lastVisibleCount = visibleMeshes.Count;

            // Sort front-to-back for performance
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
                catch { /* ignore cleanup errors */ }
            }
            meshes.Clear();

            if (shaderProgram != 0)
            {
                try { GL.DeleteProgram(shaderProgram); } catch { }
                shaderProgram = 0;
            }
        }

        public void Dispose()
        {
            Clear();
        }

        public int GetVisibleChunkCount() => lastVisibleCount;
        public int GetTotalChunkCount() => meshes.Count;

        // ---------------- Internal Helpers ----------------

        private void UploadMesh(int cx, int cy, int cz, float[] interleavedPosNormal)
        {
            if (interleavedPosNormal == null || interleavedPosNormal.Length == 0) return;

            EnsureShader();
            RemoveChunk(cx, cy, cz);

            int vao = GL.GenVertexArray();
            int vbo = GL.GenBuffer();

            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer,
                interleavedPosNormal.Length * sizeof(float),
                interleavedPosNormal,
                BufferUsageHint.StaticDraw);

            int stride = 6 * sizeof(float); // 3 pos + 3 normal
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));

            GL.BindVertexArray(0);

            // Model matrix: translate chunk to world position
            var model = Matrix4.CreateTranslation(cx * Config.CHUNK_SIZE, cy * Config.CHUNK_SIZE_Y, cz * Config.CHUNK_SIZE);

            // Bounding sphere center and radius in world-space
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

            int vertexCount = interleavedPosNormal.Length / 6;
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

        // ---------------- Frustum Culling ----------------

        private void ExtractFrustumPlanes(Matrix4 viewProj)
        {
            // Extract frustum planes from view-projection matrix
            // Left plane
            frustumPlanes[0] = NormalizePlane(new Plane(
                viewProj.M14 + viewProj.M11,
                viewProj.M24 + viewProj.M21,
                viewProj.M34 + viewProj.M31,
                viewProj.M44 + viewProj.M41));
            
            // Right plane
            frustumPlanes[1] = NormalizePlane(new Plane(
                viewProj.M14 - viewProj.M11,
                viewProj.M24 - viewProj.M21,
                viewProj.M34 - viewProj.M31,
                viewProj.M44 - viewProj.M41));
            
            // Bottom plane
            frustumPlanes[2] = NormalizePlane(new Plane(
                viewProj.M14 + viewProj.M12,
                viewProj.M24 + viewProj.M22,
                viewProj.M34 + viewProj.M32,
                viewProj.M44 + viewProj.M42));
            
            // Top plane
            frustumPlanes[3] = NormalizePlane(new Plane(
                viewProj.M14 - viewProj.M12,
                viewProj.M24 - viewProj.M22,
                viewProj.M34 - viewProj.M32,
                viewProj.M44 - viewProj.M42));
            
            // Near plane
            frustumPlanes[4] = NormalizePlane(new Plane(
                viewProj.M14 + viewProj.M13,
                viewProj.M24 + viewProj.M23,
                viewProj.M34 + viewProj.M33,
                viewProj.M44 + viewProj.M43));
            
            // Far plane
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
            // Check if sphere intersects all 6 frustum planes
            for (int i = 0; i < 6; i++)
            {
                float distance = frustumPlanes[i].A * center.X +
                                 frustumPlanes[i].B * center.Y +
                                 frustumPlanes[i].C * center.Z +
                                 frustumPlanes[i].D;
                
                // If sphere is completely outside this plane, it's not visible
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
