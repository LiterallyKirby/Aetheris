using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OpenTK.Mathematics;
using System.Net;

namespace Aetheris
{
    public class Client
    {
private readonly ConcurrentQueue<(TcpPacketType type, byte[] data)> tcpPacketQueue = new();
private readonly Dictionary<int, TaskCompletionSource<byte[]>> pendingChunkRequests = new();
private int nextRequestId = 0;
        private Game? game;
        private TcpClient? tcp;
        private UdpClient? udp;
        private IPEndPoint? serverUdpEndpoint;
        private NetworkStream? stream;
        
        private readonly ConcurrentDictionary<(int, int, int), Aetheris.Chunk> loadedChunks = new();
        private readonly ConcurrentQueue<(int cx, int cy, int cz, float priority)> requestQueue = new();
        private readonly ConcurrentDictionary<(int, int, int), byte> requestedChunks = new();
        
        private Vector3 lastPlayerChunk = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        private CancellationTokenSource? cts;
        private Task? loaderTask;
        private Task? updateTask;
        private Task? tcpListenerTask;
        
        private readonly SemaphoreSlim networkSemaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim connectionSemaphore = new SemaphoreSlim(1, 1);
        private int currentRenderDistance;
        private readonly int udpPort = ClientConfig.SERVER_PORT + 1;

        // TCP Packet Types
        private enum TcpPacketType : byte
        {
            ChunkRequest = 0,
            BlockBreak = 1
        }

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
            //tcpListenerTask = Task.Run(() => TcpBroadcastListenerAsync(cts.Token));
            _ = Task.Run(() => ListenForUdpAsync(cts.Token));
            
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
            udp = new UdpClient();

            await tcp.ConnectAsync(host, port);

            serverUdpEndpoint = new IPEndPoint(IPAddress.Parse(host), udpPort);
            udp.Connect(serverUdpEndpoint);

            stream = tcp.GetStream();
            tcp.NoDelay = true;
            tcp.SendTimeout = 5000;
            tcp.ReceiveTimeout = 5000;
            Console.WriteLine("[Client] Connected to server.");
        }

        // TCP Broadcast Listener - listens for server-initiated messages
        private async Task TcpBroadcastListenerAsync(CancellationToken token)
        {
            Console.WriteLine("[Client] TCP broadcast listener started");
            
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (stream == null || tcp == null || !tcp.Connected)
                    {
                        await Task.Delay(100, token);
                        continue;
                    }

                    try
                    {
                        // Peek to see if data is available without blocking chunk requests
                        if (tcp.Available > 0)
                        {
                            // Read packet type (1 byte)
                            var packetTypeBuf = new byte[1];
                            int bytesRead = await stream.ReadAsync(packetTypeBuf, 0, 1, token);
                            
                            if (bytesRead == 0)
                            {
                                Console.WriteLine("[Client] Server closed connection");
                                break;
                            }

                            TcpPacketType packetType = (TcpPacketType)packetTypeBuf[0];

                            switch (packetType)
                            {
                                case TcpPacketType.BlockBreak:
                                    await HandleBlockBreakBroadcastAsync(token);
                                    break;

                                default:
                                    Console.WriteLine($"[Client] Unknown TCP broadcast packet type: {packetType}");
                                    break;
                            }
                        }
                        else
                        {
                            // No data available, small delay
                            await Task.Delay(10, token);
                        }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        Console.WriteLine($"[Client] TCP listener error: {ex.Message}");
                        await Task.Delay(1000, token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[Client] TCP broadcast listener cancelled");
            }
        }

        private async Task HandleBlockBreakBroadcastAsync(CancellationToken token)
        {
            // Read block coordinates (12 bytes)
            var buf = new byte[12];
            await ReadFullAsync(stream!, buf, 0, 12, token);

            int x = BitConverter.ToInt32(buf, 0);
            int y = BitConverter.ToInt32(buf, 4);
            int z = BitConverter.ToInt32(buf, 8);

            Console.WriteLine($"[Client] Received block break broadcast at ({x}, {y}, {z})");

            // Update local density field with smooth removal
            WorldGen.RemoveBlock(x, y, z, radius: 1.5f, strength: 3.0f);

            // Queue regeneration on the game's main thread
            var blockPos = new Vector3(x, y, z);
            _ = Task.Run(() =>
            {
                Thread.Sleep(10);
                game?.RegenerateMeshForBlock(blockPos);
            });
        }

        // Send block break to server via TCP
        public async Task SendBlockBreakAsync(int x, int y, int z)
        {
            if (stream == null || tcp == null || !tcp.Connected)
            {
                Console.WriteLine("[Client] Cannot send block break - not connected");
                return;
            }

            await networkSemaphore.WaitAsync();
            try
            {
                byte[] packet = new byte[13];
                packet[0] = (byte)TcpPacketType.BlockBreak;

                BitConverter.TryWriteBytes(packet.AsSpan(1, 4), x);
                BitConverter.TryWriteBytes(packet.AsSpan(5, 4), y);
                BitConverter.TryWriteBytes(packet.AsSpan(9, 4), z);

                await stream.WriteAsync(packet, 0, packet.Length);
                await stream.FlushAsync();

                Console.WriteLine($"[Client] Sent block break at ({x}, {y}, {z}) via TCP");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Client] Error sending block break: {ex.Message}");
            }
            finally
            {
                networkSemaphore.Release();
            }
        }

        private void HandleUdpPacket(byte[] data)
        {
            if (data.Length < 1) return;

            byte packetType = data[0];

            switch (packetType)
            {
                case 3: // EntityUpdate - other player positions
                    HandleRemotePlayerUpdate(data);
                    break;

                case 4: // KeepAlive
                    _ = udp?.SendAsync(data, data.Length, serverUdpEndpoint);
                    break;

                case 5: // PositionAck - server correction
                    HandleServerPositionUpdate(data);
                    break;

                default:
                    Console.WriteLine($"[Client] Unknown UDP packet type: {packetType}");
                    break;
            }
        }

        private void HandleServerPositionUpdate(byte[] data)
        {
            if (data.Length < 37) return;

            var update = new ServerPlayerUpdate
            {
                AcknowledgedSequence = BitConverter.ToUInt32(data, 1),
                Position = new Vector3(
                    BitConverter.ToSingle(data, 5),
                    BitConverter.ToSingle(data, 9),
                    BitConverter.ToSingle(data, 13)
                ),
                Velocity = new Vector3(
                    BitConverter.ToSingle(data, 17),
                    BitConverter.ToSingle(data, 21),
                    BitConverter.ToSingle(data, 25)
                ),
                Yaw = BitConverter.ToSingle(data, 29),
                Pitch = BitConverter.ToSingle(data, 33),
                Timestamp = DateTime.UtcNow.Ticks
            };

            game?.NetworkController?.OnServerUpdate(update);
        }

        private void HandleRemotePlayerUpdate(byte[] data)
        {
            if (data.Length < 38) return;

            uint playerIdHash = BitConverter.ToUInt32(data, 1);
            string playerId = playerIdHash.ToString("X8");

            var update = new ServerPlayerUpdate
            {
                Position = new Vector3(
                    BitConverter.ToSingle(data, 5),
                    BitConverter.ToSingle(data, 9),
                    BitConverter.ToSingle(data, 13)
                ),
                Velocity = new Vector3(
                    BitConverter.ToSingle(data, 17),
                    BitConverter.ToSingle(data, 21),
                    BitConverter.ToSingle(data, 25)
                ),
                Yaw = BitConverter.ToSingle(data, 29),
                Pitch = BitConverter.ToSingle(data, 33),
                Timestamp = DateTime.UtcNow.Ticks
            };

            game?.NetworkController?.OnRemotePlayerUpdate(playerId, update);
        }

        public async Task SendUdpAsync(byte[] packet)
        {
            if (udp != null && serverUdpEndpoint != null)
            {
                try
                {
                    await udp.SendAsync(packet, packet.Length);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Client] UDP send error: {ex.Message}");
                }
            }
        }

        private async Task ListenForUdpAsync(CancellationToken token)
        {
            if (udp == null) return;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await udp.ReceiveAsync(token);
                    HandleUdpPacket(result.Buffer);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Client] UDP recv error: {ex}");
                }
            }
        }

        private async Task ChunkUpdateLoopAsync(CancellationToken token)
        {
            await Task.Delay(500, token);
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
                var (renderMesh, collisionMesh) = await RequestChunkMeshAsync(chunk.cx, chunk.cy, chunk.cz, token);

                loadedChunks[(chunk.cx, chunk.cy, chunk.cz)] = new Aetheris.Chunk();
                game?.Renderer.EnqueueMeshForChunk(chunk.cx, chunk.cy, chunk.cz, renderMesh);
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

                float[] renderMesh = await ReceiveRenderMeshAsync(token);
                CollisionMesh collisionMesh = await ReceiveCollisionMeshAsync(token);

                return (renderMesh, collisionMesh);
            }
            finally
            {
                networkSemaphore.Release();
            }
        }

        private async Task SendChunkRequestAsync(int cx, int cy, int cz, CancellationToken token)
        {
            var req = new byte[13];
            req[0] = (byte)TcpPacketType.ChunkRequest;
            BitConverter.TryWriteBytes(req.AsSpan(1, 4), cx);
            BitConverter.TryWriteBytes(req.AsSpan(5, 4), cy);
            BitConverter.TryWriteBytes(req.AsSpan(9, 4), cz);

            await stream!.WriteAsync(req, token);
            await stream.FlushAsync(token);
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
            tcpListenerTask?.Wait(TimeSpan.FromSeconds(1));

            stream?.Dispose();
            tcp?.Close();
            udp?.Close();
            networkSemaphore?.Dispose();
            connectionSemaphore?.Dispose();
            cts?.Dispose();
        }
    }
}
