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
        private readonly ConcurrentDictionary<(int, int, int), byte> requestedChunks = new();
        private Vector3 lastPlayerChunk = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        private CancellationTokenSource? cts;
        private Task? loaderTask;
        private Task? updateTask;
        private readonly SemaphoreSlim networkSemaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim connectionSemaphore = new SemaphoreSlim(1, 1);
        private int currentRenderDistance;

        private readonly ConcurrentQueue<(int cx, int cy, int cz, float[] colliderMesh)> colliderQueue = new();
        private readonly ConcurrentQueue<(int cx, int cy, int cz, float[] mesh)> physicsQueue = new();
        // Auto-tuned parameters
        public int MaxConcurrentLoads { get; set; } = 16;
        public int ChunksPerUpdateBatch { get; set; } = 128;
        public int UpdatesPerSecond { get; set; } = 20;
        public int MaxPendingUploads { get; set; } = 64;

        private int UpdateInterval => 1000 / UpdatesPerSecond;



        private Task? physicsTask;

        public void Run()
        {
            cts = new CancellationTokenSource();
            AutoTuneSettings();

            Task.Run(async () => await ConnectToServerAsync("127.0.0.1", ClientConfig.SERVER_PORT)).Wait();

            game = new Game(new Dictionary<(int, int, int), Aetheris.Chunk>(loadedChunks), this);

            loaderTask = Task.Run(() => ChunkLoaderLoopAsync(cts.Token));
            updateTask = Task.Run(() => ChunkUpdateLoopAsync(cts.Token));
            physicsTask = Task.Run(() => PhysicsLoopAsync(cts.Token)); // <--- new

            game.RunGame();
            Cleanup();
        }



        private void AutoTuneSettings()
        {
            int rd = ClientConfig.RENDER_DISTANCE;

            if (rd <= 4)
            {
                MaxConcurrentLoads = 4;
                ChunksPerUpdateBatch = 32;
                UpdatesPerSecond = 10;
                MaxPendingUploads = 16;
            }
            else if (rd <= 8)
            {
                MaxConcurrentLoads = 8;
                ChunksPerUpdateBatch = 64;
                UpdatesPerSecond = 15;
                MaxPendingUploads = 32;
            }
            else if (rd <= 16)
            {
                MaxConcurrentLoads = 16;
                ChunksPerUpdateBatch = 128;
                UpdatesPerSecond = 20;
                MaxPendingUploads = 64;
            }
            else
            {
                MaxConcurrentLoads = 32;
                ChunksPerUpdateBatch = 256;
                UpdatesPerSecond = 30;
                MaxPendingUploads = 128;
            }

            Console.WriteLine($"[Client] Auto-tuned for {rd} chunk render distance: " +
                            $"{MaxConcurrentLoads} concurrent, {ChunksPerUpdateBatch} batch size");
        }

        private async Task ConnectToServerAsync(string host, int port)
        {
            Console.WriteLine($"[Client] Connecting to {host}:{port}...");
            tcp = new TcpClient();
            await tcp.ConnectAsync(host, port);
            stream = tcp.GetStream();
            tcp.NoDelay = true;
            tcp.SendTimeout = 5000;
            tcp.ReceiveTimeout = 5000;
            Console.WriteLine("[Client] Connected to server.");
        }

        private async Task ChunkUpdateLoopAsync(CancellationToken token)
        {
            await Task.Delay(500, token); // Wait for game init
            Console.WriteLine("[Client] Chunk update loop starting...");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(UpdateInterval, token);
                    CheckAndUpdateChunks();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Client] Update loop error: {ex.Message}");
                }
            }
        }


        private async Task<(float[] renderMesh, float[] colliderMesh)> ReceiveChunkPayloadAsync(CancellationToken token)
        {
            var lenBuf = new byte[4];
            await ReadFullAsync(stream!, lenBuf, 0, 4, token);
            int renderPayloadLen = BitConverter.ToInt32(lenBuf, 0);

            var renderPayload = new byte[renderPayloadLen];
            await ReadFullAsync(stream!, renderPayload, 0, renderPayloadLen, token);

            int colliderPayloadLen = BitConverter.ToInt32(renderPayload, renderPayloadLen); // next payload length
            var colliderPayload = new byte[colliderPayloadLen];
            await ReadFullAsync(stream!, colliderPayload, 0, colliderPayloadLen, token);

            int vertexCount = BitConverter.ToInt32(renderPayload, 0);
            const int floatsPerVertex = 7;
            int floatsCount = vertexCount * floatsPerVertex;
            var renderFloats = new float[floatsCount];
            Buffer.BlockCopy(renderPayload, 4, renderFloats, 0, floatsCount * sizeof(float));

            int colliderVertexCount = colliderPayloadLen / (sizeof(float) * 3);
            var colliderFloats = new float[colliderVertexCount * 3];
            Buffer.BlockCopy(colliderPayload, 0, colliderFloats, 0, colliderVertexCount * 3 * sizeof(float));

            return (renderFloats, colliderFloats);
        }

        public void UpdateLoadedChunks(Vector3 playerChunk, int renderDistance)
        {
            currentRenderDistance = renderDistance;
            lastPlayerChunk = playerChunk;
        }


        private async Task PhysicsLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                while (physicsQueue.TryDequeue(out var item))
                {
                    try
                    {
                        game?.RegisterChunkPhysicsImmediate(item.cx, item.cy, item.cz, item.mesh);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Physics] Error registering chunk ({item.cx},{item.cy},{item.cz}): {ex.Message}");
                    }
                }

                await Task.Delay(10, token); // small delay so loop isn't a CPU hog
            }
        }

        private void CheckAndUpdateChunks()
        {
            if (lastPlayerChunk.X == float.MinValue || game == null)
                return;

            int playerCx = (int)lastPlayerChunk.X;
            int playerCy = (int)lastPlayerChunk.Y;
            int playerCz = (int)lastPlayerChunk.Z;

            // Get actual player block Y position for vertical filtering
            int playerBlockY = (int)(game.GetPlayerPosition().Y); // Add this method to Game class

            var toRequest = new List<(int cx, int cy, int cz, float priority)>();

            int rd = currentRenderDistance;
            for (int dx = -rd; dx <= rd; dx++)
            {
                for (int dz = -rd; dz <= rd; dz++)
                {
                    float horizontalDist = MathF.Sqrt(dx * dx + dz * dz);
                    if (horizontalDist > rd)
                        continue;

                    for (int dy = -2; dy <= 2; dy++) // Expanded from -1,1 to -2,2 for deeper caves
                    {
                        int cx = playerCx + dx;
                        int cy = playerCy + dy;
                        int cz = playerCz + dz;

                        // CRITICAL OPTIMIZATION: Skip chunks far from player's actual Y position
                        int chunkCenterY = cy * ClientConfig.CHUNK_SIZE_Y + ClientConfig.CHUNK_SIZE_Y / 2;
                        int yDistance = Math.Abs(chunkCenterY - playerBlockY);

                        // Don't load chunks more than 150 blocks above/below player
                        if (yDistance > 150)
                            continue;

                        var key = (cx, cy, cz);

                        if (!loadedChunks.ContainsKey(key) && !requestedChunks.ContainsKey(key))
                        {
                            float distance = MathF.Sqrt(dx * dx + dy * dy * 4 + dz * dz);
                            toRequest.Add((cx, cy, cz, distance));
                        }
                    }
                }
            }

            if (toRequest.Count > 0 && loadedChunks.Count < 10)
            {
                Console.WriteLine($"[Client] At ({playerCx},{playerCy},{playerCz}), need {toRequest.Count} chunks");
            }

            if (toRequest.Count > 0)
            {
                toRequest.Sort((a, b) => a.priority.CompareTo(b.priority));
                int toEnqueue = Math.Min(toRequest.Count, ChunksPerUpdateBatch);

                if (requestQueue.Count > MaxPendingUploads)
                    return;

                for (int i = 0; i < toEnqueue; i++)
                {
                    var chunk = toRequest[i];
                    requestedChunks[(chunk.cx, chunk.cy, chunk.cz)] = 0;
                    requestQueue.Enqueue(chunk);
                }
            }

            if (Random.Shared.NextDouble() < 0.1)
            {
                UnloadDistantChunks(playerCx, playerCy, playerCz);
            }
        }

        private void UnloadDistantChunks(int playerCx, int playerCy, int playerCz)
        {
            var toUnload = new List<(int, int, int)>();
            int unloadDist = currentRenderDistance + 2; // Buffer zone

            foreach (var coord in loadedChunks.Keys)
            {
                int dx = coord.Item1 - playerCx;
                int dy = coord.Item2 - playerCy;
                int dz = coord.Item3 - playerCz;

                // Use circular distance for unloading too
                float dist = MathF.Sqrt(dx * dx + dz * dz);

                if (dist > unloadDist || Math.Abs(dy) > 3)
                {
                    toUnload.Add(coord);
                }
            }

            // Unload in small batches
            int unloadLimit = Math.Min(toUnload.Count, 4);
            for (int i = 0; i < unloadLimit; i++)
            {
                var coord = toUnload[i];
                if (loadedChunks.TryRemove(coord, out _))
                {
                    game?.Renderer.RemoveChunk(coord.Item1, coord.Item2, coord.Item3);
                    requestedChunks.TryRemove(coord, out _);
                }
            }

            if (unloadLimit > 0)
            {

            }
        }

        private async Task ChunkLoaderLoopAsync(CancellationToken token)
        {
            var activeTasks = new List<Task>();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    activeTasks.RemoveAll(t => t.IsCompleted);

                    while (activeTasks.Count < MaxConcurrentLoads && requestQueue.TryDequeue(out var chunk))
                    {
                        activeTasks.Add(LoadChunkAsync(chunk, token));
                    }

                    if (activeTasks.Count == 0)
                    {
                        await Task.Delay(10, token);
                    }
                    else
                    {
                        await Task.WhenAny(activeTasks);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Client] Loader error: {ex.Message}");
                    await Task.Delay(100, token);
                }
            }

            await Task.WhenAll(activeTasks);
        }




        private async Task LoadChunkAsync((int cx, int cy, int cz, float priority) chunk, CancellationToken token)
        {
            try
            {
                var (renderMesh, colliderMesh) = await RequestChunkMeshAsync(chunk.cx, chunk.cy, chunk.cz, token);

                loadedChunks[(chunk.cx, chunk.cy, chunk.cz)] = new Aetheris.Chunk();

                physicsQueue.Enqueue((chunk.cx, chunk.cy, chunk.cz, colliderMesh));

                game?.Renderer.EnqueueMeshForChunk(chunk.cx, chunk.cy, chunk.cz, renderMesh);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Client] Error loading ({chunk.cx},{chunk.cy},{chunk.cz}): {ex.Message}");
                requestedChunks.TryRemove((chunk.cx, chunk.cy, chunk.cz), out _);
            }
        }



 private async Task<(float[] renderMesh, float[] colliderMesh)> RequestChunkMeshAsync(int cx, int cy, int cz, CancellationToken token)
{
    // Ensure connection
    if (stream == null || tcp == null || !tcp.Connected)
    {
        await connectionSemaphore.WaitAsync(token);
        try
        {
            if (stream == null || tcp == null || !tcp.Connected)
            {
                await ConnectToServerAsync("127.0.0.1", ClientConfig.SERVER_PORT);
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

        // First: render mesh (has vertex count header)
        float[] renderMesh = await ReceiveMeshPayloadAsync(token);

        // Second: collider mesh (raw floats, no header)
        float[] colliderMesh = await ReceiveColliderMeshAsync(token);

        return (renderMesh, colliderMesh);
    }
    finally
    {
        networkSemaphore.Release();
    }
}
        private async Task SendChunkRequestAsync(int cx, int cy, int cz, CancellationToken token)
        {
            var req = new byte[12];
            BitConverter.TryWriteBytes(req.AsSpan(0, 4), cx);
            BitConverter.TryWriteBytes(req.AsSpan(4, 4), cy);
            BitConverter.TryWriteBytes(req.AsSpan(8, 4), cz);

            await stream!.WriteAsync(req, token);
            await stream.FlushAsync(token);
        }

        private async Task<float[]> ReceiveMeshPayloadAsync(CancellationToken token)
        {
            var lenBuf = new byte[4];
            await ReadFullAsync(stream!, lenBuf, 0, 4, token);
            int payloadLen = BitConverter.ToInt32(lenBuf, 0);

            // Sanity check
            if (payloadLen < 0 || payloadLen > 100_000_000)
                throw new Exception($"Invalid payload length: {payloadLen}");

            var payload = new byte[payloadLen];
            await ReadFullAsync(stream!, payload, 0, payloadLen, token);

            // Render mesh has vertex count header
            int vertexCount = BitConverter.ToInt32(payload, 0);
            const int floatsPerVertex = 7;
            int floatsCount = vertexCount * floatsPerVertex;

            var floats = new float[floatsCount];
            Buffer.BlockCopy(payload, 4, floats, 0, floatsCount * sizeof(float));

            return floats;
        }

private async Task<float[]> ReceiveColliderMeshAsync(CancellationToken token)
{
    var lenBuf = new byte[4];
    await ReadFullAsync(stream!, lenBuf, 0, 4, token);
    int payloadLen = BitConverter.ToInt32(lenBuf, 0);

    // Sanity check
    if (payloadLen < 0 || payloadLen > 100_000_000)
        throw new Exception($"Invalid collider payload length: {payloadLen}");

    if (payloadLen == 0)
    {
        Console.WriteLine("[Client] Empty collider mesh received");
        return Array.Empty<float>();
    }

    var payload = new byte[payloadLen];
    await ReadFullAsync(stream!, payload, 0, payloadLen, token);

    // Collider mesh is just raw floats (xyz per vertex, 9 floats per triangle)
    int floatCount = payloadLen / sizeof(float);
    var floats = new float[floatCount];
    Buffer.BlockCopy(payload, 0, floats, 0, payloadLen);

    Console.WriteLine($"[Client] Received collider mesh: {floatCount} floats ({floatCount / 9} triangles)");
    return floats;
}

        private static async Task ReadFullAsync(NetworkStream stream, byte[] buf, int off, int count, CancellationToken token)
        {
            int read = 0;
            while (read < count)
            {
                int r = await stream.ReadAsync(buf.AsMemory(off + read, count - read), token);
                if (r <= 0)
                    throw new Exception("Stream closed unexpectedly");
                read += r;
            }
        }

        private void Cleanup()
        {
            Console.WriteLine("[Client] Shutting down...");
            cts?.Cancel();

            physicsTask?.Wait(TimeSpan.FromSeconds(2));
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
