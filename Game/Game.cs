using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Aetheris
{
    public class Game : GameWindow
    {
        public Renderer Renderer { get; private set; }
        private readonly Dictionary<(int, int, int), Aetheris.Chunk> loadedChunks;
        private readonly Player player;
        private readonly Client? client;
        private int renderDistance = Config.RENDER_DISTANCE; // How many chunks to load around player
        private float chunkUpdateTimer = 0f;
        private const float CHUNK_UPDATE_INTERVAL = 0.5f; // Update chunk loading every 0.5 seconds

        public Game(Dictionary<(int, int, int), Aetheris.Chunk> loadedChunks, Client? client = null)
            : base(GameWindowSettings.Default, new NativeWindowSettings()
            {
                Size = new Vector2i(800, 600),
                Title = "Aetheris Client"
            })
        {
            this.loadedChunks = loadedChunks ?? new Dictionary<(int, int, int), Aetheris.Chunk>();
            this.client = client;
            Renderer = new Renderer();
            // Start player at a better viewing position
            player = new Player(new Vector3(16, 30, 16));
        }

        protected override void OnLoad()
        {
            base.OnLoad();
            GL.ClearColor(0.15f, 0.18f, 0.2f, 1.0f);
            GL.Enable(EnableCap.DepthTest);
            CursorState = CursorState.Grabbed;

            // Load all pre-fetched chunks into renderer
            foreach (var kv in loadedChunks)
            {
                var coord = kv.Key;
                var chunk = kv.Value;
                var meshFloats = MarchingCubes.GenerateMesh(chunk, 0.5f);
                Console.WriteLine($"[Game] Loading chunk {coord} with {meshFloats.Length / 6} vertices");
                Renderer.LoadMeshForChunk(coord.Item1, coord.Item2, coord.Item3, meshFloats);
            }

            Console.WriteLine($"[Game] Loaded {loadedChunks.Count} chunks");
        }

       protected override void OnUpdateFrame(FrameEventArgs e)
{
    base.OnUpdateFrame(e);
    
    // CRITICAL: Process any pending mesh uploads from the client thread
    Renderer.ProcessPendingUploads();
    
    if (IsKeyDown(Keys.Escape))
        Close();
    
    player.Update(e, KeyboardState, MouseState);
    
    // Trigger immediate load on FIRST frame only
    if (chunkUpdateTimer == 0f)
    {
        Vector3 playerChunk = player.GetPlayersChunk();
        Console.WriteLine($"[Game] Initial chunk request at {playerChunk}");
        client?.UpdateLoadedChunks(playerChunk, renderDistance);
    }
    
    // Periodically update chunk loading based on player position
    chunkUpdateTimer += (float)e.Time;
    if (chunkUpdateTimer >= CHUNK_UPDATE_INTERVAL)
    {
        chunkUpdateTimer = 0f;
        // Use player's existing GetPlayersChunk method
        Vector3 playerChunk = player.GetPlayersChunk();
        client?.UpdateLoadedChunks(playerChunk, renderDistance);
    }
    
    // Allow changing render distance with +/- keys
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
            Renderer.Dispose();
        }

        public void RunGame() => Run();
    }
}
