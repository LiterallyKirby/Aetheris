using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        private readonly ChunkManager chunkManager;
        public PlayerNetworkController? NetworkController { get; private set; }
        private int renderDistance = ClientConfig.RENDER_DISTANCE;
        private float chunkUpdateTimer = 0f;
        private const float CHUNK_UPDATE_INTERVAL = 0.5f;

        private MiningSystem? miningSystem;
        // Logging
        private const string LogFileName = "physics_debug.log";
        private StreamWriter? logWriter;
        private TextWriter? originalConsoleOut;
        private TextWriter? originalConsoleError;
        private TeeTextWriter? teeWriter;
        private EntityRenderer? entityRenderer;
        private PlayerNetworkController? networkController;
        public Game(Dictionary<(int, int, int), Aetheris.Chunk> loadedChunks, Client? client = null)
       : base(GameWindowSettings.Default, new NativeWindowSettings()
       {
           ClientSize = new Vector2i(1920, 1080),
           Title = "Aetheris Client"
       })
        {
            this.loadedChunks = loadedChunks ?? new Dictionary<(int, int, int), Aetheris.Chunk>();
            this.client = client;

            SetupLogging();

            // Initialize WorldGen FIRST (needed for player collision)
            WorldGen.Initialize();
            Console.WriteLine("[Game] WorldGen initialized");

            // Create ChunkManager for collision detection
            chunkManager = new ChunkManager();
            chunkManager.GenerateCollisionMeshes = true;
            Console.WriteLine("[Game] ChunkManager initialized with collision support");

            // Create Renderer
            Renderer = new Renderer();
            Console.WriteLine("[Game] Renderer initialized");

            // Create EntityRenderer
            entityRenderer = new EntityRenderer();
            Console.WriteLine("[Game] EntityRenderer initialized");

            // Create player (MUST be after WorldGen.Initialize())
            player = new Player(new Vector3(16, 50, 16));
            Console.WriteLine("[Game] Player initialized at position: {0}", player.Position);

            // Create network controller (MUST be after player is created)
            if (client != null)
            {
                NetworkController = new PlayerNetworkController(player, client);
                networkController = NetworkController; // Keep both references in sync
                Console.WriteLine("[Game] Network controller initialized");
            }
            else
            {
                Console.WriteLine("[Game] Running in single-player mode (no network)");
            }
        }

        private void SetupLogging()
        {
            try
            {
                if (File.Exists(LogFileName))
                    File.Delete(LogFileName);

                var fs = new FileStream(LogFileName, FileMode.Create, FileAccess.Write, FileShare.Read);
                logWriter = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };

                originalConsoleOut = Console.Out;
                originalConsoleError = Console.Error;

                teeWriter = new TeeTextWriter(originalConsoleOut, logWriter);

                Console.SetOut(teeWriter);
                Console.SetError(teeWriter);

                Console.WriteLine($"[Logging] Started logging to '{LogFileName}'");
            }
            catch (Exception ex)
            {
                try
                {
                    Console.SetOut(Console.Out);
                    Console.WriteLine("[Logging] Failed to initialize file logging: " + ex.Message);
                }
                catch { }
            }
        }

        protected override void OnLoad()
        {
            base.OnLoad();
            GL.ClearColor(0.15f, 0.18f, 0.2f, 1.0f);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(TriangleFace.Back);
            GL.FrontFace(FrontFaceDirection.Ccw);
            CursorState = CursorState.Grabbed;
            miningSystem = new MiningSystem(player, this, OnBlockMined);
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

            // Load pre-fetched chunks into both renderer and chunk manager
            foreach (var kv in loadedChunks)
            {
                var coord = kv.Key;
                var chunk = kv.Value;

                var chunkCoord = new ChunkCoord(coord.Item1, coord.Item2, coord.Item3);

                // Generate mesh for rendering
                var meshFloats = MarchingCubes.GenerateMesh(chunk, chunkCoord, chunkManager, 0.5f);

                // Generate collision mesh for physics
                chunk.GenerateCollisionMesh(meshFloats);

                // Store chunk in manager for collision queries
                // Note: You may need to add a method to add pre-generated chunks
                // For now, the chunk will be generated on-demand when player collides

                Console.WriteLine($"[Game] Loading chunk {coord} with {meshFloats.Length / 7} vertices");
                Renderer.LoadMeshForChunk(coord.Item1, coord.Item2, coord.Item3, meshFloats);
            }

            Console.WriteLine($"[Game] Loaded {loadedChunks.Count} chunks");
        }

        private readonly Queue<Action> pendingMainThreadActions = new Queue<Action>();
        private readonly object mainThreadLock = new object();

        private void OnBlockMined(Vector3 blockPos, BlockType blockType)
        {
            Console.WriteLine($"[Client] Mined {blockType} at {blockPos}");

            int x = (int)blockPos.X;
            int y = (int)blockPos.Y;
            int z = (int)blockPos.Z;

            // Send to server via TCP (reliable)
            if (client != null)
            {
                _ = client.SendBlockBreakAsync(x, y, z);
            }

            // Client-side prediction: reduce density in the area (SMOOTH REMOVAL)
            WorldGen.RemoveBlock(x, y, z, radius: 1.5f, strength: 3.0f);

            // Queue regeneration for next frame to ensure modifications complete
            lock (mainThreadLock)
            {
                pendingMainThreadActions.Enqueue(() => RegenerateMeshForBlock(blockPos));
            }
        }

        public void RegenerateMeshForBlock(Vector3 blockPos)
        {
            int blockX = (int)blockPos.X;
            int blockY = (int)blockPos.Y;
            int blockZ = (int)blockPos.Z;

            // Calculate affected area based on mining radius
            float miningRadius = 1.5f;
            int affectRadius = (int)Math.Ceiling(miningRadius);

            // Determine all chunks that need regeneration
            HashSet<(int, int, int)> chunksToUpdate = new HashSet<(int, int, int)>();

            for (int dx = -affectRadius; dx <= affectRadius; dx++)
            {
                for (int dy = -affectRadius; dy <= affectRadius; dy++)
                {
                    for (int dz = -affectRadius; dz <= affectRadius; dz++)
                    {
                        int wx = blockX + dx;
                        int wy = blockY + dy;
                        int wz = blockZ + dz;

                        int cx = (int)Math.Floor((float)wx / ClientConfig.CHUNK_SIZE);
                        int cy = (int)Math.Floor((float)wy / ClientConfig.CHUNK_SIZE_Y);
                        int cz = (int)Math.Floor((float)wz / ClientConfig.CHUNK_SIZE);

                        chunksToUpdate.Add((cx, cy, cz));
                    }
                }
            }

            Console.WriteLine($"[Client] Regenerating {chunksToUpdate.Count} chunks affected by mining at ({blockX}, {blockY}, {blockZ})");

            // Regenerate all affected chunks
            foreach (var (cx, cy, cz) in chunksToUpdate)
            {
                RegenerateChunkMesh(cx, cy, cz);
            }
        }

        private void RegenerateChunkMesh(int cx, int cy, int cz)
        {
            var coord = new ChunkCoord(cx, cy, cz);

            // Get existing chunk or generate new one
            var chunk = chunkManager.GetOrGenerateChunk(coord);

            // Generate new mesh with updated density field (WorldGen.SampleDensity now applies modifications)
            var meshFloats = MarchingCubes.GenerateMesh(chunk, coord, chunkManager, 0.5f);

            // Update renderer immediately
            Renderer.LoadMeshForChunk(cx, cy, cz, meshFloats);

            Console.WriteLine($"[Client] Regenerated chunk ({cx}, {cy}, {cz}) - {meshFloats.Length / 7} vertices");
        }

        protected override void OnUpdateFrame(FrameEventArgs e)
        {
            base.OnUpdateFrame(e);
            lock (mainThreadLock)
            {
                while (pendingMainThreadActions.Count > 0)
                {
                    var action = pendingMainThreadActions.Dequeue();
                    try
                    {
                        action?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Game] Error executing pending action: {ex.Message}");
                    }
                }
            }
            float delta = (float)e.Time;

            // Process pending mesh uploads
            Renderer.ProcessPendingUploads();

            if (IsKeyDown(Keys.Escape))
                Close();

            // Update player
            if (NetworkController != null)
            {
                NetworkController.Update(e, KeyboardState, MouseState);
            }
            else
            {
                player.Update(e, KeyboardState, MouseState);
            }
            if (networkController != null)
            {
                networkController.Update(e, KeyboardState, MouseState);
            }
            else
            {
                player.Update(e, KeyboardState, MouseState);
            }
            // Chunk loading
            if (chunkUpdateTimer == 0f)
            {
                Vector3 playerChunk = player.GetPlayersChunk();
                client?.UpdateLoadedChunks(playerChunk, renderDistance);
            }

            chunkUpdateTimer += delta;
            if (chunkUpdateTimer >= CHUNK_UPDATE_INTERVAL)
            {
                chunkUpdateTimer = 0f;
                Vector3 playerChunk = player.GetPlayersChunk();
                client?.UpdateLoadedChunks(playerChunk, renderDistance);
            }
            if (miningSystem != null)
            {
                miningSystem.Update((float)e.Time, MouseState, IsFocused);
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
        }

        protected override void OnRenderFrame(FrameEventArgs e)
        {
            base.OnRenderFrame(e);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // Debug: Raycast
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

            // Debug: Biome info
            if (KeyboardState.IsKeyPressed(Keys.B))
            {
                int px = (int)player.Position.X;
                int pz = (int)player.Position.Z;
                WorldGen.PrintBiomeAt(px, pz);
                Console.WriteLine($"Player at: {player.Position}");
            }
            var projection = Matrix4.CreatePerspectiveFieldOfView(
                OpenTK.Mathematics.MathHelper.DegreesToRadians(60f),
                Size.X / (float)Size.Y,
                0.1f,
                1000f);
            var view = player.GetViewMatrix();

            // === RENDER TERRAIN (sets up shader and keeps it active) ===
            Renderer.Render(projection, view, player.Position);

            // === RENDER OTHER PLAYERS (shader still active) ===
            if (entityRenderer != null && networkController != null)
            {
                var remotePlayers = networkController.RemotePlayers;
                if (remotePlayers != null && remotePlayers.Count > 0)
                {
                    entityRenderer.RenderPlayers(
                        remotePlayers as Dictionary<string, RemotePlayer>,
                        Renderer.psxEffects,
                        player.Position,
                        Renderer.UsePSXEffects
                    );
                }
            }

            // === CLEANUP ===
            GL.BindVertexArray(0);
            GL.UseProgram(0);

            SwapBuffers();
        }

        protected override void OnUnload()
        {
            base.OnUnload();

            Renderer.Dispose();

            // Restore console
            try
            {
                if (originalConsoleOut != null)
                    Console.SetOut(originalConsoleOut);
                if (originalConsoleError != null)
                    Console.SetError(originalConsoleError);
                logWriter?.Flush();
                logWriter?.Dispose();
                Console.WriteLine("[Game] Cleanup complete");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Game] Error closing log: " + ex.Message);
            }
        }

        public Vector3 GetPlayerPosition() => player.Position;

        public void RunGame() => Run();

        private class TeeTextWriter : TextWriter
        {
            private readonly TextWriter consoleWriter;
            private readonly StreamWriter fileWriter;
            private readonly object writeLock = new object();

            public TeeTextWriter(TextWriter consoleWriter, StreamWriter fileWriter)
            {
                this.consoleWriter = consoleWriter ?? throw new ArgumentNullException(nameof(consoleWriter));
                this.fileWriter = fileWriter ?? throw new ArgumentNullException(nameof(fileWriter));
            }

            public override Encoding Encoding => Encoding.UTF8;

            public override void WriteLine(string? value)
            {
                lock (writeLock)
                {
                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {value}";
                    try { consoleWriter.WriteLine(line); } catch { }
                    try { fileWriter.WriteLine(line); } catch { }
                }
            }

            public override void Write(string? value)
            {
                lock (writeLock)
                {
                    try { consoleWriter.Write(value); } catch { }
                    try { fileWriter.Write(value); } catch { }
                }
            }

            public override void Write(char value)
            {
                lock (writeLock)
                {
                    try { consoleWriter.Write(value); } catch { }
                    try { fileWriter.Write(value); } catch { }
                }
            }

            public override void Flush()
            {
                lock (writeLock)
                {
                    try { consoleWriter.Flush(); } catch { }
                    try { fileWriter.Flush(); } catch { }
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { fileWriter.Flush(); } catch { }
                }
                base.Dispose(disposing);
            }
        }
    }
}
