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

        private int renderDistance = ClientConfig.RENDER_DISTANCE;
        private float chunkUpdateTimer = 0f;
        private const float CHUNK_UPDATE_INTERVAL = 0.5f;
        private bool physicsReady = false;
        private HashSet<(int, int, int)> registeredChunks = new HashSet<(int, int, int)>();
        private object physicsLock = new object();
        private bool forcePhysicsStep = false;
        // --- Logging fields ---
        private const string LogFileName = "physics_debug.log";
        private StreamWriter? logWriter;
        private TextWriter? originalConsoleOut;
        private TextWriter? originalConsoleError;
        private TeeTextWriter? teeWriter;

        public Game(Dictionary<(int, int, int), Aetheris.Chunk> loadedChunks, Client? client = null)
            : base(GameWindowSettings.Default, new NativeWindowSettings()
            {
                Size = new Vector2i(1920, 1080),
                Title = "Aetheris Client"
            })
        {
            this.loadedChunks = loadedChunks ?? new Dictionary<(int, int, int), Aetheris.Chunk>();
            this.client = client;

            // Initialize logging very early so all Console writes are captured.
            SetupLogging();

            // Initialize physics FIRST

            Console.WriteLine("[Game] PhysicsManager initialized");

            Renderer = new Renderer();


            // Create player with physics manager
            player = new Player(new Vector3(16, 50, 16), this);
            Console.WriteLine("[Game] Player initialized with physics");
        }

        private void SetupLogging()
        {
            try
            {
                // Delete existing file so we always start fresh
                if (File.Exists(LogFileName))
                {
                    File.Delete(LogFileName);
                }

                // Open file for writing with read sharing (so you can tail/read while program runs)
                var fs = new FileStream(LogFileName, FileMode.Create, FileAccess.Write, FileShare.Read);
                logWriter = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };

                // Keep original console writers so we can still output to console
                originalConsoleOut = Console.Out;
                originalConsoleError = Console.Error;

                // Create a tee writer that writes to both original console and file, with timestamps
                teeWriter = new TeeTextWriter(originalConsoleOut, logWriter);

                // Redirect console output & error to the tee writer
                Console.SetOut(teeWriter);
                Console.SetError(teeWriter);

                Console.WriteLine($"[Logging] Started logging to '{LogFileName}' (overwritten).");
            }
            catch (Exception ex)
            {
                // If logging setup fails, fall back to console and avoid throwing here.
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

            // CRITICAL: Wait for minimum physics chunks before allowing full updates


            // Still process pending mesh uploads while waiting
            Renderer.ProcessPendingUploads();

            // Check if player is falling too fast (no ground loaded yet)


            // Trigger chunk loading even while waiting
            if (chunkUpdateTimer == 0f)
            {
                Vector3 playerChunk = player.GetPlayersChunk();
                client?.UpdateLoadedChunks(playerChunk, renderDistance);
            }

            chunkUpdateTimer += (float)e.Time;
            if (chunkUpdateTimer >= CHUNK_UPDATE_INTERVAL)
            {
                chunkUpdateTimer = 0f;
                Vector3 playerChunk = player.GetPlayersChunk();
                client?.UpdateLoadedChunks(playerChunk, renderDistance);
            }




            // Normal update logic once physics is ready

            Renderer.ProcessPendingUploads();

            if (IsKeyDown(Keys.Escape))
                Close();

            // Update player
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

            if (KeyboardState.IsKeyPressed(Keys.D1))
            {
                player.TeleportTo(new Vector3(
                    player.Position.X,
                    player.Position.Y + 10,
                    player.Position.Z
                ));
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



        public bool IsPhysicsReady()
        {
            lock (physicsLock)
            {
                return physicsReady;
            }
        }
        private static int ChunkKey(int cx, int cy, int cz)
        {
            unchecked
            {
                return (cx * 73856093) ^ (cy * 19349663) ^ (cz * 83492791);
            }
        }
        protected override void OnUnload()
        {
            base.OnUnload();
            player.Cleanup();

            Renderer.Dispose();

            // Restore console and close log file cleanly
            try
            {
                if (originalConsoleOut != null)
                {
                    Console.SetOut(originalConsoleOut);
                }
                if (originalConsoleError != null)
                {
                    Console.SetError(originalConsoleError);
                }
                logWriter?.Flush();
                logWriter?.Dispose();
                Console.WriteLine("[Game] Cleanup complete (logging stopped)");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Game] Error closing log: " + ex.Message);
            }
        }

        public Vector3 GetPlayerPosition()
        {
            return player.Position;
        }

        public void RunGame() => Run();

        // --- Helper tee writer --- (writes to both original console writer and the log file with timestamps)
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

            // Prefer overriding WriteLine(string) and Write(string) & Write(char)
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
                    // For intermediate Writes (no newline), write without timestamp.
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
