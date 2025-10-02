using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OpenTK.Mathematics;

namespace Aetheris
{
    public class Client
    {
        private Game? game;
        private TcpClient? tcp;
        private NetworkStream? stream;
        private readonly ConcurrentDictionary<(int, int, int), Aetheris.Chunk> loadedChunks = new();
        private readonly ConcurrentQueue<(int cx, int cy, int cz, float priority)> requestQueue = new();
        private readonly ConcurrentDictionary<(int, int, int), byte> requestedChunks = new(); // byte = dummy value
        private Vector3 lastPlayerChunk = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        private CancellationTokenSource? cts;
        private Task? loaderTask;
        private Task? updateTask;
        private readonly SemaphoreSlim networkSemaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim connectionSemaphore = new SemaphoreSlim(1, 1);
        private int currentRenderDistance;
        // Configurable loading parameters
        public int MaxConcurrentLoads { get; set; } = 16; // How many chunks to load at once
        public int ChunksPerUpdateBatch { get; set; } = 128; // How many to queue per update cycle
        public int UpdatesPerSecond { get; set; } = 20; // How often to check for new chunks
        public int MaxPendingUploads { get; set; } = 64; // Max chunks waiting in upload queue

        private int UpdateInterval => 1000 / UpdatesPerSecond;

        public void Run()
        {
            cts = new CancellationTokenSource();

            // Auto-tune settings based on render distance
            AutoTuneSettings();

            // Connect to server asynchronously
            Task.Run(async () => await ConnectToServerAsync("127.0.0.1", Config.SERVER_PORT)).Wait();

            // Create game with empty chunks initially
            game = new Game(new Dictionary<(int, int, int), Aetheris.Chunk>(loadedChunks), this);

            // Start background tasks AFTER game is created
            loaderTask = Task.Run(() => ChunkLoaderLoopAsync(cts.Token));
            updateTask = Task.Run(() => ChunkUpdateLoopAsync(cts.Token));

            // Don't trigger initial load here - let the game do it with the real player position

            // Run the game (blocks until window closes)
            game.RunGame();

            // Cleanup
            Cleanup();
        }

        /// <summary>
        /// Auto-tune client settings based on render distance
        /// </summary>
        private void AutoTuneSettings()
        {
            int rd = Config.RENDER_DISTANCE;

            if (rd <= 8)
            {
                // Small render distance - conservative settings
                MaxConcurrentLoads = 8;
                ChunksPerUpdateBatch = 64;
                UpdatesPerSecond = 10;
                MaxPendingUploads = 32;
            }
            else if (rd <= 32)
            {
                // Medium render distance - balanced
                MaxConcurrentLoads = 16;
                ChunksPerUpdateBatch = 128;
                UpdatesPerSecond = 20;
                MaxPendingUploads = 64;
            }
            else if (rd <= 64)
            {
                // Large render distance - aggressive
                MaxConcurrentLoads = 32;
                ChunksPerUpdateBatch = 256;
                UpdatesPerSecond = 30;
                MaxPendingUploads = 128;
            }
            else
            {
                // Extreme render distance - maximum throughput
                MaxConcurrentLoads = 64;
                ChunksPerUpdateBatch = 512;
                UpdatesPerSecond = 40;
                MaxPendingUploads = 256;
            }

            Console.WriteLine($"[Client] Auto-tuned for render distance {rd}: " +
                            $"{MaxConcurrentLoads} concurrent loads, " +
                            $"{ChunksPerUpdateBatch} per batch, " +
                            $"{UpdatesPerSecond} updates/sec");
        }

        private async Task ConnectToServerAsync(string host, int port)
        {
            Console.WriteLine($"[Client] Connecting to {host}:{port}...");

            tcp = new TcpClient();
            await tcp.ConnectAsync(host, port);
            stream = tcp.GetStream();
            tcp.NoDelay = true;

            Console.WriteLine("[Client] Connected to server.");
        }

        // Background task that periodically checks player position
        private async Task ChunkUpdateLoopAsync(CancellationToken token)
        {
            // Wait for game to fully initialize
            await Task.Delay(1000, token);
            Console.WriteLine("[Client] Chunk update loop starting...");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(UpdateInterval, token);

                    // Run update logic asynchronously to avoid blocking
                    _ = Task.Run(() => CheckAndUpdateChunks(), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Client] Update loop error: {ex.Message}");
                }
            }
        }

        public void UpdateLoadedChunks(Vector3 playerChunk, int renderDistance)
        {
            currentRenderDistance = renderDistance;
            lastPlayerChunk = playerChunk;
        }

        private void CheckAndUpdateChunks()
        {
            // Wait until we have a valid player position
            if (lastPlayerChunk.X == float.MinValue || game == null)
                return;

            int playerCx = (int)lastPlayerChunk.X;
            int playerCy = (int)lastPlayerChunk.Y;
            int playerCz = (int)lastPlayerChunk.Z;

            // Collect chunks to request - prioritize by distance
            var toRequest = new List<(int cx, int cy, int cz, float priority)>();

            // Check ALL chunks in range, then sort by priority
            for (int dx = -currentRenderDistance; dx <= currentRenderDistance; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -currentRenderDistance; dz <= currentRenderDistance; dz++)
                    {
                        int cx = playerCx + dx;
                        int cy = playerCy + dy;
                        int cz = playerCz + dz;

                        var key = (cx, cy, cz);

                        if (!loadedChunks.ContainsKey(key) && !requestedChunks.ContainsKey(key))
                        {
                            float distance = MathF.Sqrt(dx * dx + dy * dy * 2 + dz * dz);
                            toRequest.Add((cx, cy, cz, distance));
                        }
                    }

            // Sort by priority (closest first) and enqueue
            if (toRequest.Count > 0)
            {
                toRequest.Sort((a, b) => a.priority.CompareTo(b.priority));

                // Queue chunks up to the batch size
                int toEnqueue = Math.Min(toRequest.Count, ChunksPerUpdateBatch);

                // Throttle if upload queue is getting too long
                int queueSize = requestQueue.Count;
                if (queueSize > MaxPendingUploads)
                {

                    return;
                }



                for (int i = 0; i < toEnqueue; i++)
                {
                    var chunk = toRequest[i];
                    requestedChunks[(chunk.cx, chunk.cy, chunk.cz)] = 0;
                    requestQueue.Enqueue(chunk);
                }
            }
            else if (loadedChunks.Count > 0) // Only log if we've actually loaded something
            {

            }

            // Unload distant chunks (less frequently)
            if (Random.Shared.NextDouble() < 0.1) // 10% chance per update
            {
                UnloadDistantChunksAsync(playerCx, playerCy, playerCz, currentRenderDistance);
            }
        }

        private void UnloadDistantChunksAsync(int playerCx, int playerCy, int playerCz, int renderDistance)
        {
            var toUnload = new List<(int, int, int)>();

            foreach (var coord in loadedChunks.Keys)
            {
                int dx = Math.Abs(coord.Item1 - playerCx);
                int dy = Math.Abs(coord.Item2 - playerCy);
                int dz = Math.Abs(coord.Item3 - playerCz);

                if (dx > renderDistance + 3 || dy > 4 || dz > renderDistance + 3)
                {
                    toUnload.Add(coord);
                }
            }

            // Limit unloads per frame to avoid stutters
            int unloadLimit = Math.Min(toUnload.Count, 8);
            for (int i = 0; i < unloadLimit; i++)
            {
                var coord = toUnload[i];
                if (loadedChunks.TryRemove(coord, out _))
                {
                    game?.Renderer.RemoveChunk(coord.Item1, coord.Item2, coord.Item3);
                    requestedChunks.TryRemove(coord, out _);
                }
            }
        }

        private async Task ChunkLoaderLoopAsync(CancellationToken token)
        {
            // Process chunks concurrently based on config
            var activeTasks = new List<Task>();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Clean up completed tasks
                    activeTasks.RemoveAll(t => t.IsCompleted);

                    // Start new tasks if we have capacity
                    while (activeTasks.Count < MaxConcurrentLoads && requestQueue.TryDequeue(out var chunk))
                    {
                        var task = LoadChunkAsync(chunk, token);
                        activeTasks.Add(task);
                    }

                    if (activeTasks.Count == 0)
                    {
                        // Shorter delay when idle for more responsive loading
                        await Task.Delay(5, token);
                    }
                    else
                    {
                        // Wait for any task to complete
                        await Task.WhenAny(activeTasks);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Client] Loader error: {ex.Message}");
                    await Task.Delay(100, token);
                }
            }

            // Wait for remaining tasks
            await Task.WhenAll(activeTasks);
        }

        private async Task LoadChunkAsync((int cx, int cy, int cz, float priority) chunk, CancellationToken token)
        {
            try
            {
                // Quick relevance check
                if (lastPlayerChunk.X != float.MinValue)
                {
                    int playerCx = (int)lastPlayerChunk.X;
                    int playerCy = (int)lastPlayerChunk.Y;
                    int playerCz = (int)lastPlayerChunk.Z;

                    int dx = Math.Abs(chunk.cx - playerCx);
                    int dy = Math.Abs(chunk.cy - playerCy);
                    int dz = Math.Abs(chunk.cz - playerCz);

                    if (dx > currentRenderDistance + 2 || dy > 3 || dz > currentRenderDistance + 2)
                    {
                        requestedChunks.TryRemove((chunk.cx, chunk.cy, chunk.cz), out _);
                        return;
                    }
                }

                float[] meshData = await RequestChunkMeshAsync(chunk.cx, chunk.cy, chunk.cz, token);

                var placeholderChunk = new Aetheris.Chunk();
                loadedChunks[(chunk.cx, chunk.cy, chunk.cz)] = placeholderChunk;

                //Console.WriteLine($"[Client] Loaded chunk ({chunk.cx},{chunk.cy},{chunk.cz}): {meshData.Length / 7} vertices");
                // Enqueue mesh for upload on render thread
                game?.Renderer.EnqueueMeshForChunk(chunk.cx, chunk.cy, chunk.cz, meshData);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Client] Error loading chunk ({chunk.cx},{chunk.cy},{chunk.cz}): {ex.Message}");
                requestedChunks.TryRemove((chunk.cx, chunk.cy, chunk.cz), out _);
            }
        }

        private async Task<float[]> RequestChunkMeshAsync(int cx, int cy, int cz, CancellationToken token)
        {
            // Ensure connection
            if (stream == null || tcp == null || !tcp.Connected)
            {
                await connectionSemaphore.WaitAsync(token);
                try
                {
                    if (stream == null || tcp == null || !tcp.Connected)
                    {
                        await ConnectToServerAsync("127.0.0.1", Config.SERVER_PORT);
                    }
                }
                finally
                {
                    connectionSemaphore.Release();
                }
            }

            await networkSemaphore.WaitAsync(token);
            try
            {
                await SendChunkRequestAsync(cx, cy, cz, token);
                return await ReceiveMeshPayloadAsync(token);
            }
            finally
            {
                networkSemaphore.Release();
            }
        }

        private async Task SendChunkRequestAsync(int cx, int cy, int cz, CancellationToken token)
        {
            var req = new byte[12];
            Array.Copy(BitConverter.GetBytes(cx), 0, req, 0, 4);
            Array.Copy(BitConverter.GetBytes(cy), 0, req, 4, 4);
            Array.Copy(BitConverter.GetBytes(cz), 0, req, 8, 4);

            await stream!.WriteAsync(req, 0, req.Length, token);
            await stream.FlushAsync(token);
        }



        private async Task<float[]> ReceiveMeshPayloadAsync(CancellationToken token)
        {
            var lenBuf = new byte[4];
            await ReadFullAsync(stream!, lenBuf, 0, 4, token);
            int payloadLen = BitConverter.ToInt32(lenBuf, 0);

            var payload = new byte[payloadLen];
            await ReadFullAsync(stream!, payload, 0, payloadLen, token);

            int vertexCount = BitConverter.ToInt32(payload, 0);

            const int floatsPerVertex = 7; // pos(3) + normal(3) + blockType(1)
            int floatsCount = vertexCount * floatsPerVertex;

            var floats = new float[floatsCount];
            Buffer.BlockCopy(payload, 4, floats, 0, floatsCount * sizeof(float));

            return floats;
        }



        private static async Task ReadFullAsync(NetworkStream stream, byte[] buf, int off, int count, CancellationToken token)
        {
            int read = 0;
            while (read < count)
            {
                int r = await stream.ReadAsync(buf, off + read, count - read, token);
                if (r <= 0)
                    throw new Exception("Stream closed unexpectedly");
                read += r;
            }
        }

        private void Cleanup()
        {
            Console.WriteLine("[Client] Shutting down...");
            cts?.Cancel();

            loaderTask?.Wait(TimeSpan.FromSeconds(2));
            updateTask?.Wait(TimeSpan.FromSeconds(1));

            stream?.Dispose();
            tcp?.Close();
            networkSemaphore?.Dispose();
            connectionSemaphore?.Dispose();
            cts?.Dispose();
        }
    }
}
