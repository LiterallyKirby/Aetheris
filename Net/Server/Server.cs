using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using System.Collections.Generic;

namespace Aetheris
{
    public class Server
    {
        private TcpListener? listener;
        private CancellationTokenSource? cts;
        private readonly ChunkManager chunkManager = new();

        // Mesh cache with LRU eviction
        private readonly ConcurrentDictionary<ChunkCoord, float[]> meshCache = new();
        private readonly ConcurrentDictionary<ChunkCoord, SemaphoreSlim> generationLocks = new();

        private const int MaxCachedMeshes = 20000;
        private int cacheSize = 0;

        // 60 TPS timing
        private const double TickRate = 60.0;
        private const double TickDuration = 1000.0 / TickRate;
        private long tickCount = 0;

        private readonly ConcurrentDictionary<string, PlayerState> playerStates = new();

        private class PlayerState
        {
            public Vector3 Position { get; set; }
            public Vector3 Velocity { get; set; }
            public Vector2 Rotation { get; set; }
            public long LastUpdate { get; set; }
            public IPEndPoint? EndPoint { get; set; }
        }

        private enum UdpPacketType : byte
        {
            PlayerPosition = 1,
            PlayerInput = 2,
            EntityUpdate = 3,
            KeepAlive = 4
        }

        // Performance tracking
        private long totalRequests = 0;
        private double totalChunkGenTime = 0;
        private double totalMeshGenTime = 0;
        private double totalSendTime = 0;
        private readonly object perfLock = new();

        private UdpClient? udpServer;
        private readonly int UDP_PORT = ServerConfig.SERVER_PORT + 1;

        // File logging
        private StreamWriter? logWriter;
        private readonly object logLock = new();

        private void Log(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string logMessage = $"[{timestamp}] {message}";

            lock (logLock)
            {
                try
                {
                    logWriter?.WriteLine(logMessage);
                    logWriter?.Flush();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Failed to write to log file: {ex.Message}");
                }
            }
        }

        public async Task RunServerAsync()
        {
            string logPath = $"server_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            try
            {
                logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
                Log($"[[Server]] Log file created: {logPath}");
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Failed to create log file: {ex.Message}");
            }

            Log("[[Server]] Initializing world generation...");
            WorldGen.Initialize();

            listener = new TcpListener(IPAddress.Any, ServerConfig.SERVER_PORT);
            listener.Start();
            listener.Server.NoDelay = true;
            cts = new CancellationTokenSource();

            udpServer = new UdpClient(UDP_PORT);
            _ = Task.Run(() => HandleUdpLoop(cts.Token));
            Log($"[[Server]] UDP listening on port {UDP_PORT}");
            Log($"[[Server]] Listening on port {ServerConfig.SERVER_PORT} @ {TickRate} TPS");
            Log("[[Server]] Performance timing enabled - waiting for connections...");

            _ = Task.Run(() => ServerTickLoop(cts.Token));
            _ = Task.Run(() => CacheCleanupLoop(cts.Token));

            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync();
                    client.NoDelay = true;
                    _ = Task.Run(() => HandleClientAsync(client, cts.Token), cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                Log("[[Server]] Shutting down...");
            }
            finally
            {
                logWriter?.Close();
                logWriter?.Dispose();
            }
        }

        private async Task ServerTickLoop(CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            double accumulator = 0;

            while (!token.IsCancellationRequested)
            {
                double frameTime = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                accumulator += frameTime;

                while (accumulator >= TickDuration)
                {
                    tickCount++;
                    accumulator -= TickDuration;
                }

                double sleepTime = TickDuration - sw.Elapsed.TotalMilliseconds;
                if (sleepTime > 1)
                {
                    await Task.Delay((int)sleepTime, token);
                }

                if (tickCount % (int)(TickRate * 5) == 0)
                {
                    lock (perfLock)
                    {
                        if (totalRequests > 0)
                        {
                            double avgChunk = totalChunkGenTime / totalRequests;
                            double avgMesh = totalMeshGenTime / totalRequests;
                            double avgSend = totalSendTime / totalRequests;
                            double avgTotal = avgChunk + avgMesh + avgSend;

                            Log($"[[Server]] Tick {tickCount} | Cache: {cacheSize}/{MaxCachedMeshes}");
                            Log($"  Requests: {totalRequests} | Avg Times: Chunk={avgChunk:F2}ms Mesh={avgMesh:F2}ms Send={avgSend:F2}ms Total={avgTotal:F2}ms");
                        }
                        else
                        {
                            Log($"[[Server]] Tick {tickCount} | Cache: {cacheSize}/{MaxCachedMeshes}");
                        }
                    }
                }
            }
        }

        private async Task HandleUdpLoop(CancellationToken token)
        {
            if (udpServer == null) return;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    UdpReceiveResult result = await udpServer.ReceiveAsync(token);
                    HandleUdpPacket(result.Buffer, result.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException)
            {
                Log("[[UDP]] Loop cancelled");
            }
            catch (Exception ex)
            {
                Log($"[[UDP]] Error: {ex.Message}");
            }
        }

        private void HandleUdpPacket(byte[] data, IPEndPoint remoteEndPoint)
        {
            if (data.Length < 1) return;

            UdpPacketType packetType = (UdpPacketType)data[0];

            try
            {
                switch (packetType)
                {
                    case UdpPacketType.PlayerPosition:
                        HandlePlayerPositionPacket(data, remoteEndPoint);
                        break;
                    case UdpPacketType.PlayerInput:
                        HandlePlayerInputPacket(data, remoteEndPoint);
                        break;
                    case UdpPacketType.KeepAlive:
                        HandleKeepAlivePacket(data, remoteEndPoint);
                        break;
                    default:
                        Log($"[[UDP]] Unknown packet type: {packetType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"[[UDP]] Error handling packet: {ex.Message}");
            }
        }

        private void HandlePlayerPositionPacket(byte[] data, IPEndPoint remoteEndPoint)
        {
            if (data.Length < 33) return;

            string playerKey = remoteEndPoint.ToString();

            float x = BitConverter.ToSingle(data, 1);
            float y = BitConverter.ToSingle(data, 5);
            float z = BitConverter.ToSingle(data, 9);
            float velX = BitConverter.ToSingle(data, 13);
            float velY = BitConverter.ToSingle(data, 17);
            float velZ = BitConverter.ToSingle(data, 21);
            float yaw = BitConverter.ToSingle(data, 25);
            float pitch = BitConverter.ToSingle(data, 29);

            var state = playerStates.GetOrAdd(playerKey, _ => new PlayerState { EndPoint = remoteEndPoint });
            state.Position = new Vector3(x, y, z);
            state.Velocity = new Vector3(velX, velY, velZ);
            state.Rotation = new Vector2(yaw, pitch);
            state.LastUpdate = DateTime.UtcNow.Ticks;
            state.EndPoint = remoteEndPoint;

            _ = BroadcastPlayerState(playerKey, state);
        }

        private void HandlePlayerInputPacket(byte[] data, IPEndPoint remoteEndPoint)
        {
            if (data.Length < 2) return;
            // Process input if needed
        }

        private void HandleKeepAlivePacket(byte[] data, IPEndPoint remoteEndPoint)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await udpServer!.SendAsync(data, data.Length, remoteEndPoint);
                }
                catch (Exception ex)
                {
                    Log($"[[UDP]] Error sending keep-alive: {ex.Message}");
                }
            });
        }

        private async Task BroadcastPlayerState(string excludePlayer, PlayerState state)
        {
            byte[] packet = new byte[33];
            packet[0] = (byte)UdpPacketType.EntityUpdate;
            BitConverter.TryWriteBytes(packet.AsSpan(1, 4), state.Position.X);
            BitConverter.TryWriteBytes(packet.AsSpan(5, 4), state.Position.Y);
            BitConverter.TryWriteBytes(packet.AsSpan(9, 4), state.Position.Z);
            BitConverter.TryWriteBytes(packet.AsSpan(13, 4), state.Velocity.X);
            BitConverter.TryWriteBytes(packet.AsSpan(17, 4), state.Velocity.Y);
            BitConverter.TryWriteBytes(packet.AsSpan(21, 4), state.Velocity.Z);
            BitConverter.TryWriteBytes(packet.AsSpan(25, 4), state.Rotation.X);
            BitConverter.TryWriteBytes(packet.AsSpan(29, 4), state.Rotation.Y);

            foreach (var player in playerStates)
            {
                if (player.Key != excludePlayer && player.Value.EndPoint != null)
                {
                    try
                    {
                        await udpServer!.SendAsync(packet, packet.Length, player.Value.EndPoint);
                    }
                    catch (Exception ex)
                    {
                        Log($"[[UDP]] Error broadcasting to {player.Key}: {ex.Message}");
                    }
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            {
                var stream = client.GetStream();

                try
                {
                    while (!token.IsCancellationRequested && client.Connected)
                    {
                        var coord = await ReadChunkRequestAsync(stream, token);
                        if (!coord.HasValue)
                            break;

                        _ = Task.Run(async () =>
                        {
                            var requestSw = Stopwatch.StartNew();
                            double chunkTime = 0, meshTime = 0, sendTime = 0;

                            try
                            {
                                var result = await GetOrGenerateMeshAsync(coord.Value, token);
                                chunkTime = result.chunkGenTime;
                                meshTime = result.meshGenTime;

                                var sendSw = Stopwatch.StartNew();
                                await SendMeshAsync(stream, result.renderMesh, coord.Value, token);
                                sendTime = sendSw.Elapsed.TotalMilliseconds;

                                lock (perfLock)
                                {
                                    totalRequests++;
                                    totalChunkGenTime += chunkTime;
                                    totalMeshGenTime += meshTime;
                                    totalSendTime += sendTime;
                                }

                                double totalTime = requestSw.Elapsed.TotalMilliseconds;
                                Log($"[[Timing]] Chunk {coord.Value}: Chunk={chunkTime:F2}ms Mesh={meshTime:F2}ms Send={sendTime:F2}ms Total={totalTime:F2}ms");
                            }
                            catch (Exception ex)
                            {
                                Log($"[[Server]] Error handling chunk {coord.Value}: {ex.Message}");
                            }
                        }, token);
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    Log($"[[Server]] Client error: {ex.Message}");
                }
            }
        }

        private async Task<ChunkCoord?> ReadChunkRequestAsync(NetworkStream stream, CancellationToken token)
        {
            var buf = ArrayPool<byte>.Shared.Rent(12);
            try
            {
                int totalRead = 0;
                while (totalRead < 12)
                {
                    int bytesRead = await stream.ReadAsync(buf, totalRead, 12 - totalRead, token);
                    if (bytesRead == 0)
                        return null;
                    totalRead += bytesRead;
                }

                int cx = BitConverter.ToInt32(buf, 0);
                int cy = BitConverter.ToInt32(buf, 4);
                int cz = BitConverter.ToInt32(buf, 8);

                return new ChunkCoord(cx, cy, cz);
            }
            catch
            {
                return null;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        private async Task<(float[] renderMesh, double chunkGenTime, double meshGenTime)> GetOrGenerateMeshAsync(ChunkCoord coord, CancellationToken token)
        {
            // Check cache first
            if (meshCache.TryGetValue(coord, out var cached))
            {
                Log($"[[Cache]] Hit for {coord}");
                return (cached, 0, 0);
            }

            var lockObj = generationLocks.GetOrAdd(coord, _ => new SemaphoreSlim(1, 1));

            await lockObj.WaitAsync(token);
            try
            {
                if (meshCache.TryGetValue(coord, out cached))
                {
                    Log($"[[Cache]] Hit after lock for {coord}");
                    return (cached, 0, 0);
                }

                // Generate chunk
                var chunkSw = Stopwatch.StartNew();
                var chunk = await Task.Run(() => chunkManager.GetOrGenerateChunk(coord), token);
                double chunkGenTime = chunkSw.Elapsed.TotalMilliseconds;

                // Generate render mesh
                var meshSw = Stopwatch.StartNew();
                var renderMesh = await Task.Run(() => MarchingCubes.GenerateMesh(chunk, coord, chunkManager, isoLevel: 0.5f), token);
                double meshGenTime = meshSw.Elapsed.TotalMilliseconds;

                // Cache mesh
                meshCache[coord] = renderMesh;
                Interlocked.Increment(ref cacheSize);
if (renderMesh.Length >= 7)
{
    Console.WriteLine($"[[Generation]] Chunk coord ({coord.X},{coord.Y},{coord.Z})");
    Console.WriteLine($"[[Generation]] Chunk world pos: ({coord.X * ServerConfig.CHUNK_SIZE}, {coord.Y * ServerConfig.CHUNK_SIZE_Y}, {coord.Z * ServerConfig.CHUNK_SIZE})");
    Console.WriteLine($"[[Generation]] First vertex: ({renderMesh[0]:F1}, {renderMesh[1]:F1}, {renderMesh[2]:F1})");
}

                Log($"[[Generation]] {coord}: Render vertices={renderMesh.Length / 7}");

                return (renderMesh, chunkGenTime, meshGenTime);
            }
            finally
            {
                lockObj.Release();
            }
        }

        private readonly SemaphoreSlim sendSemaphore = new SemaphoreSlim(1, 1);

        private async Task SendMeshAsync(NetworkStream stream, float[] renderMesh, ChunkCoord coord, CancellationToken token)
        {
            int renderVertexCount = renderMesh.Length / 7;
            int renderPayloadSize = sizeof(int) + renderMesh.Length * sizeof(float);
            var renderPayload = ArrayPool<byte>.Shared.Rent(renderPayloadSize);

            try
            {
                Array.Copy(BitConverter.GetBytes(renderVertexCount), 0, renderPayload, 0, sizeof(int));
                Buffer.BlockCopy(renderMesh, 0, renderPayload, sizeof(int), renderMesh.Length * sizeof(float));

                await sendSemaphore.WaitAsync(token);
                try
                {
                    var renderLenBytes = BitConverter.GetBytes(renderPayloadSize);
                    await stream.WriteAsync(renderLenBytes, 0, renderLenBytes.Length, token);
                    await stream.WriteAsync(renderPayload, 0, renderPayloadSize, token);
                    await stream.FlushAsync(token);
                }
                finally
                {
                    sendSemaphore.Release();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(renderPayload);
            }
        }

        private async Task CacheCleanupLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(60000, token);

                    if (cacheSize > MaxCachedMeshes)
                    {
                        var cleanupSw = Stopwatch.StartNew();
                        
                        // Simple cleanup - remove 25% of cache
                        int toRemove = cacheSize / 4;
                        int removed = 0;

                        foreach (var coord in meshCache.Keys)
                        {
                            if (removed >= toRemove) break;
                            
                            if (meshCache.TryRemove(coord, out _))
                            {
                                removed++;
                                Interlocked.Decrement(ref cacheSize);
                            }
                        }

                        Log($"[[Cache Cleanup]] Removed {removed} meshes in {cleanupSw.Elapsed.TotalMilliseconds:F2}ms, {cacheSize} remaining");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"[[Server]] Cache cleanup error: {ex.Message}");
                }
            }
        }

        public void Stop()
        {
            cts?.Cancel();
            listener?.Stop();
            sendSemaphore?.Dispose();

            foreach (var lockObj in generationLocks.Values)
            {
                lockObj.Dispose();
            }
            generationLocks.Clear();
        }
    }
}
