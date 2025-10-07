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

        // Auto-tuned parameters
        public int MaxConcurrentLoads { get; set; } = 16;
        public int ChunksPerUpdateBatch { get; set; } = 128;
        public int UpdatesPerSecond { get; set; } = 20;
        public int MaxPendingUploads { get; set; } = 64;

        private int UpdateInterval => 1000 / UpdatesPerSecond;

        public void Run()
        {
            cts = new CancellationTokenSource();
            AutoTuneSettings();

            Task.Run(async () => await ConnectToServerAsync("127.0.0.1", ClientConfig.SERVER_PORT)).Wait();

            game = new Game(new Dictionary<(int, int, int), Aetheris.Chunk>(loadedChunks), this);

            loaderTask = Task.Run(() => ChunkLoaderLoopAsync(cts.Token));
            updateTask = Task.Run(() => ChunkUpdateLoopAsync(cts.Token));

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

        public void UpdateLoadedChunks(Vector3 playerChunk, int renderDistance)
        {
            currentRenderDistance = renderDistance;
            lastPlayerChunk = playerChunk;
        }

        private void CheckAndUpdateChunks()
        {
            if (lastPlayerChunk.X == float.MinValue || game == null)
                return;

            int playerCx = (int)lastPlayerChunk.X;
            int playerCy = (int)lastPlayerChunk.Y;
            int playerCz = (int)lastPlayerChunk.Z;
            int playerBlockY = (int)(game.GetPlayerPosition().Y);

            var toRequest = new List<(int cx, int cy, int cz, float priority)>();

            int rd = currentRenderDistance;
            for (int dx = -rd; dx <= rd; dx++)
            {
                for (int dz = -rd; dz <= rd; dz++)
                {
                    float horizontalDist = MathF.Sqrt(dx * dx + dz * dz);
                    if (horizontalDist > rd)
                        continue;

                    for (int dy = -2; dy <= 2; dy++)
                    {
                        int cx = playerCx + dx;
                        int cy = playerCy + dy;
                        int cz = playerCz + dz;

                        int chunkCenterY = cy * ClientConfig.CHUNK_SIZE_Y + ClientConfig.CHUNK_SIZE_Y / 2;
                        int yDistance = Math.Abs(chunkCenterY - playerBlockY);

                        if (yDistance > 150)
                            continue;

                        var key = (cx, cy, cz);

                        if (!loadedChunks.ContainsKey(key) && !requestedChunks.ContainsKey(key))
                        {
                            float distance = MathF.Sqrt(dx * dx + dy * dy * 4 + dz * dz);

                            // Give priority to chunks directly under/around player
                            if (Math.Abs(dx) <= 1 && Math.Abs(dz) <= 1 && dy <= 0)
                            {
                                distance *= 0.01f;
                            }

                            toRequest.Add((cx, cy, cz, distance));
                        }
                    }
                }
            }

            if (toRequest.Count > 0)
            {
                toRequest.Sort((a, b) => a.priority.CompareTo(b.priority));

                int batchSize = ChunksPerUpdateBatch;
                int toEnqueue = Math.Min(toRequest.Count, batchSize);

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
            int unloadDist = currentRenderDistance + 2;

            foreach (var coord in loadedChunks.Keys)
            {
                int dx = coord.Item1 - playerCx;
                int dy = coord.Item2 - playerCy;
                int dz = coord.Item3 - playerCz;

                float dist = MathF.Sqrt(dx * dx + dz * dz);

                if (dist > unloadDist || Math.Abs(dy) > 3)
                {
                    toUnload.Add(coord);
                }
            }

            int unloadLimit = Math.Min(toUnload.Count, 4);
            for (int i = 0; i < unloadLimit; i++)
            {
                var coord = toUnload[i];
                if (loadedChunks.TryRemove(coord, out _))
                {
                    requestedChunks.TryRemove(coord, out _);
                }
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
                Console.WriteLine($"[Client] Requesting chunk ({chunk.cx},{chunk.cy},{chunk.cz})");

                var (renderMesh, collisionMesh) = await RequestChunkMeshAsync(chunk.cx, chunk.cy, chunk.cz, token);

                Console.WriteLine($"[Client] Received chunk ({chunk.cx},{chunk.cy},{chunk.cz}): " +
                                 $"{renderMesh.Length} render floats, {collisionMesh.Vertices.Count} collision verts");

                // Mark chunk as loaded
                loadedChunks[(chunk.cx, chunk.cy, chunk.cz)] = new Aetheris.Chunk();

                // Enqueue render mesh for GPU
                game?.Renderer.EnqueueMeshForChunk(chunk.cx, chunk.cy, chunk.cz, renderMesh);

                // TODO: Add collision mesh to physics world here
                // game?.PhysicsWorld.AddChunkCollider(chunk.cx, chunk.cy, chunk.cz, collisionMesh);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Client] Error loading ({chunk.cx},{chunk.cy},{chunk.cz}): {ex.Message}");
                requestedChunks.TryRemove((chunk.cx, chunk.cy, chunk.cz), out _);
            }
        }


        private async Task<(float[] renderMesh, CollisionMesh collisionMesh)> RequestChunkMeshAsync(
         int cx, int cy, int cz, CancellationToken token)
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

                // Receive both meshes
                float[] renderMesh = await ReceiveRenderMeshAsync(token);
                CollisionMesh collisionMesh = await ReceiveCollisionMeshAsync(token);

                return (renderMesh, collisionMesh);
            }
            finally
            {
                networkSemaphore.Release();
            }
        }

        private async Task<float[]> ReceiveRenderMeshAsync(CancellationToken token)
        {
            var lenBuf = new byte[4];
            await ReadFullAsync(stream!, lenBuf, 0, 4, token);
            int payloadLen = BitConverter.ToInt32(lenBuf, 0);

            if (payloadLen < 0 || payloadLen > 100_000_000)
                throw new Exception($"Invalid payload length: {payloadLen}");

            if (payloadLen == 0)
            {
                Console.WriteLine("[Client] Received empty render mesh");
                return Array.Empty<float>();
            }

            var payload = new byte[payloadLen];
            await ReadFullAsync(stream!, payload, 0, payloadLen, token);

            int vertexCount = BitConverter.ToInt32(payload, 0);
            const int floatsPerVertex = 7;
            int floatsCount = vertexCount * floatsPerVertex;

            var floats = new float[floatsCount];
            Buffer.BlockCopy(payload, 4, floats, 0, floatsCount * sizeof(float));

            return floats;
        }



        private async Task<CollisionMesh> ReceiveCollisionMeshAsync(CancellationToken token)
        {
            var lenBuf = new byte[4];
            await ReadFullAsync(stream!, lenBuf, 0, 4, token);
            int payloadLen = BitConverter.ToInt32(lenBuf, 0);

            if (payloadLen < 0 || payloadLen > 100_000_000)
                throw new Exception($"Invalid collision payload length: {payloadLen}");

            if (payloadLen == 0)
            {
                Console.WriteLine("[Client] Received empty collision mesh");
                return new CollisionMesh
                {
                    Vertices = new List<Vector3>(),
                    Indices = new List<int>()
                };
            }

            var payload = new byte[payloadLen];
            await ReadFullAsync(stream!, payload, 0, payloadLen, token);

            int offset = 0;
            int vertexCount = BitConverter.ToInt32(payload, offset);
            offset += sizeof(int);
            int indexCount = BitConverter.ToInt32(payload, offset);
            offset += sizeof(int);

            var vertices = new List<Vector3>(vertexCount);
            for (int i = 0; i < vertexCount; i++)
            {
                float x = BitConverter.ToSingle(payload, offset);
                offset += sizeof(float);
                float y = BitConverter.ToSingle(payload, offset);
                offset += sizeof(float);
                float z = BitConverter.ToSingle(payload, offset);
                offset += sizeof(float);

                vertices.Add(new Vector3(x, y, z));
            }

            var indices = new List<int>(indexCount);
            for (int i = 0; i < indexCount; i++)
            {
                indices.Add(BitConverter.ToInt32(payload, offset));
                offset += sizeof(int);
            }

            return new CollisionMesh { Vertices = vertices, Indices = indices };
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

            if (payloadLen < 0 || payloadLen > 100_000_000)
                throw new Exception($"Invalid payload length: {payloadLen}");

            if (payloadLen == 0)
            {
                Console.WriteLine("[Client] Received empty mesh");
                return Array.Empty<float>();
            }

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
