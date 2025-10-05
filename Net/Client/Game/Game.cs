using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using AetherisClient.Rendering;

namespace Aetheris
{
    public class Game : GameWindow
    {
        public Renderer Renderer { get; private set; }
        private readonly Dictionary<(int, int, int), Aetheris.Chunk> loadedChunks;
        private readonly Player player;
        private readonly Client? client;
        private readonly PhysicsManager physics;
        private int renderDistance = ClientConfig.RENDER_DISTANCE;
        private float chunkUpdateTimer = 0f;
        private const float CHUNK_UPDATE_INTERVAL = 0.5f;

        public Game(Dictionary<(int, int, int), Aetheris.Chunk> loadedChunks, Client? client = null)
            : base(GameWindowSettings.Default, new NativeWindowSettings()
            {
                Size = new Vector2i(1920, 1080),
                Title = "Aetheris Client"
            })
        {
            this.loadedChunks = loadedChunks ?? new Dictionary<(int, int, int), Aetheris.Chunk>();
            this.client = client;
            
            // Initialize physics FIRST
            physics = new PhysicsManager();
            Console.WriteLine("[Game] PhysicsManager initialized");
            
            Renderer = new Renderer();
            Renderer.Physics = physics; // Connect renderer to physics
            
            // Create player with physics manager
            player = new Player(new Vector3(16, 50, 16), physics);
            Console.WriteLine("[Game] Player initialized with physics");
        }

        protected override void OnLoad()
        {
            base.OnLoad();
            GL.ClearColor(0.15f, 0.18f, 0.2f, 1.0f);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Back);
            GL.FrontFace(FrontFaceDirection.Ccw);
            CursorState = CursorState.Grabbed;

            // Load atlas
            string[] atlasPaths = new[]
            {
                "textures/atlas.png",
                "../textures/atlas.png",
                "../../textures/atlas.png",
                "atlas.png"
            };

            bool atlasLoaded = false;
            foreach (var path in atlasPaths)
            {
                if (System.IO.File.Exists(path))
                {
                    Console.WriteLine($"[Game] Found atlas at: {path}");
                    Renderer.LoadTextureAtlas(path);
                    atlasLoaded = true;
                    break;
                }
            }

            if (!atlasLoaded)
            {
                Console.WriteLine("[Game] No atlas.png found - using procedural fallback");
                Renderer.CreateProceduralAtlas();
            }

            // Verify atlas
            if (AtlasManager.IsLoaded)
            {
                Console.WriteLine($"[Game] AtlasManager loaded: {AtlasManager.AtlasWidth}x{AtlasManager.AtlasHeight}, " +
                                 $"tileSize={AtlasManager.TileSize}, texture ID={AtlasManager.AtlasTextureId}");

                GL.BindTexture(TextureTarget.Texture2D, AtlasManager.AtlasTextureId);
                GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureWidth, out int w);
                GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureHeight, out int h);
                GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureInternalFormat, out int fmt);
                Console.WriteLine($"[Game] Texture verified: {w}x{h}, format={fmt}");
                GL.BindTexture(TextureTarget.Texture2D, 0);
            }

            // Load all pre-fetched chunks
            foreach (var kv in loadedChunks)
            {
                var coord = kv.Key;
                var chunk = kv.Value;
                var meshFloats = MarchingCubes.GenerateMesh(chunk, 0.5f);
                Console.WriteLine($"[Game] Loading chunk {coord} with {meshFloats.Length / 7} vertices");
                Renderer.LoadMeshForChunk(coord.Item1, coord.Item2, coord.Item3, meshFloats);
            }

            Console.WriteLine($"[Game] Loaded {loadedChunks.Count} chunks");
            Console.WriteLine("[Game] Physics colliders registered with renderer");
        }

        protected override void OnUpdateFrame(FrameEventArgs e)
        {
            base.OnUpdateFrame(e);

            // Update physics simulation
            physics.Update((float)e.Time);

            // Process pending mesh uploads
            Renderer.ProcessPendingUploads();

            if (IsKeyDown(Keys.Escape))
                Close();

            // Update player (now includes physics)
            player.Update(e, KeyboardState, MouseState);

            // Trigger immediate load on first frame
            if (chunkUpdateTimer == 0f)
            {
                Vector3 playerChunk = player.GetPlayersChunk();
                client?.UpdateLoadedChunks(playerChunk, renderDistance);
            }

            // Periodically update chunk loading
            chunkUpdateTimer += (float)e.Time;
            if (chunkUpdateTimer >= CHUNK_UPDATE_INTERVAL)
            {
                chunkUpdateTimer = 0f;
                Vector3 playerChunk = player.GetPlayersChunk();
                client?.UpdateLoadedChunks(playerChunk, renderDistance);
            }

            // Render distance controls
            if (KeyboardState.IsKeyPressed(Keys.Equal) || KeyboardState.IsKeyPressed(Keys.KeyPadAdd))
            {
                renderDistance = Math.Min(renderDistance + 1, 999);
                Console.WriteLine($"[Game] Render distance: {renderDistance}");
            }
            if (KeyboardState.IsKeyPressed(Keys.Minus) || KeyboardState.IsKeyPressed(Keys.KeyPadSubtract))
            {
                renderDistance = Math.Max(renderDistance - 1, 1);
                Console.WriteLine($"[Game] Render distance: {renderDistance}");
            }

            // Debug: Show physics stats
            if (KeyboardState.IsKeyPressed(Keys.P))
            {
                Console.WriteLine($"[Physics] Bodies: {physics.Simulation.Bodies.ActiveSet.Count}, " +
                                $"Statics: {physics.Simulation.Statics.Count}");
            }
        }

        protected override void OnRenderFrame(FrameEventArgs e)
        {
            base.OnRenderFrame(e);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // Debug: Raycast to see what player is looking at
            if (KeyboardState.IsKeyPressed(Keys.R))
            {
                Vector3 forward = player.GetForward();
                for (float dist = 0; dist < 50; dist += 0.5f)
                {
                    Vector3 pos = player.Position + forward * dist;
                    int x = (int)pos.X, y = (int)pos.Y, z = (int)pos.Z;
                    var blockType = WorldGen.GetBlockType(x, y, z);
                    if (blockType != BlockType.Air)
                    {
                        Console.WriteLine($"Looking at: {blockType} at ({x},{y},{z}), distance={dist:F1}");
                        break;
                    }
                }
            }

            // Debug: Show biome info
            if (KeyboardState.IsKeyPressed(Keys.B))
            {
                int px = (int)player.Position.X;
                int pz = (int)player.Position.Z;
                WorldGen.PrintBiomeAt(px, pz);
                Console.WriteLine($"Player at: {player.Position}");
            }

            var projection = Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(60f),
                Size.X / (float)Size.Y,
                0.1f,
                1000f);

            var view = player.GetViewMatrix();
            Renderer.Render(projection, view, player.Position);

            SwapBuffers();
        }

        protected override void OnUnload()
        {
            base.OnUnload();
            player.Cleanup();
            physics.Dispose();
            Renderer.Dispose();
            Console.WriteLine("[Game] Cleanup complete");
        }

        public Vector3 GetPlayerPosition()
        {
            return player.Position;
        }

        public void RunGame() => Run();
    }
}
