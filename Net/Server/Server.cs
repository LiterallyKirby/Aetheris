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
        private readonly ConcurrentDictionary<ChunkCoord, CachedMeshPair> meshCache = new();
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
        private double totalColliderGenTime = 0;
        private double totalSendTime = 0;
        private readonly object perfLock = new();

        private UdpClient? udpServer;
        private readonly int UDP_PORT = ServerConfig.SERVER_PORT + 1;

        // File logging
        private StreamWriter? logWriter;
        private readonly object logLock = new();

        private class CachedMeshPair
        {
            public float[] RenderMesh { get; }
            public float[] ColliderMesh { get; }
            public long LastAccessed { get; set; }

            public CachedMeshPair(float[] renderMesh, float[] colliderMesh)
            {
                RenderMesh = renderMesh;
                ColliderMesh = colliderMesh;
                LastAccessed = DateTime.UtcNow.Ticks;
            }
        }

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
                            double avgCollider = totalColliderGenTime / totalRequests;
                            double avgSend = totalSendTime / totalRequests;
                            double avgTotal = avgChunk + avgMesh + avgCollider + avgSend;

                            Log($"[[Server]] Tick {tickCount} | Cache: {cacheSize}/{MaxCachedMeshes}");
                            Log($"  Requests: {totalRequests} | Avg Times: Chunk={avgChunk:F2}ms Mesh={avgMesh:F2}ms Collider={avgCollider:F2}ms Send={avgSend:F2}ms Total={avgTotal:F2}ms");
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
                            double chunkTime = 0, meshTime = 0, colliderTime = 0, sendTime = 0;

                            try
                            {
                                var result = await GetOrGenerateMeshPairAsync(coord.Value, token);
                                chunkTime = result.chunkGenTime;
                                meshTime = result.meshGenTime;
                                colliderTime = result.colliderGenTime;

                                var sendSw = Stopwatch.StartNew();
                                await SendMeshPairAsync(stream, result.renderMesh, result.colliderMesh, coord.Value, token);
                                sendTime = sendSw.Elapsed.TotalMilliseconds;

                                lock (perfLock)
                                {
                                    totalRequests++;
                                    totalChunkGenTime += chunkTime;
                                    totalMeshGenTime += meshTime;
                                    totalColliderGenTime += colliderTime;
                                    totalSendTime += sendTime;
                                }

                                double totalTime = requestSw.Elapsed.TotalMilliseconds;
                                Log($"[[Timing]] Chunk {coord.Value}: Chunk={chunkTime:F2}ms Mesh={meshTime:F2}ms Collider={colliderTime:F2}ms Send={sendTime:F2}ms Total={totalTime:F2}ms");
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

        // Replace GetOrGenerateMeshPairAsync with this version:

        private async Task<(float[] renderMesh, float[] colliderMesh, double chunkGenTime, double meshGenTime, double colliderGenTime)> GetOrGenerateMeshPairAsync(ChunkCoord coord, CancellationToken token)
        {
            // Check cache first
            if (meshCache.TryGetValue(coord, out var cached))
            {
                cached.LastAccessed = DateTime.UtcNow.Ticks;
                Log($"[[Cache]] Hit for {coord}");
                return (cached.RenderMesh, cached.ColliderMesh, 0, 0, 0);
            }

            var lockObj = generationLocks.GetOrAdd(coord, _ => new SemaphoreSlim(1, 1));

            await lockObj.WaitAsync(token);
            try
            {
                if (meshCache.TryGetValue(coord, out cached))
                {
                    cached.LastAccessed = DateTime.UtcNow.Ticks;
                    Log($"[[Cache]] Hit after lock for {coord}");
                    return (cached.RenderMesh, cached.ColliderMesh, 0, 0, 0);
                }

                // Generate chunk
                var chunkSw = Stopwatch.StartNew();
                var chunk = await Task.Run(() => chunkManager.GetOrGenerateChunk(coord), token);
                double chunkGenTime = chunkSw.Elapsed.TotalMilliseconds;

                // Generate render mesh (high detail, step=2)
                var meshSw = Stopwatch.StartNew();
                var renderMesh = await Task.Run(() => MarchingCubes.GenerateMesh(chunk, coord, chunkManager, isoLevel: 0.5f), token);
                double meshGenTime = meshSw.Elapsed.TotalMilliseconds;

                // Convert render mesh to collision mesh (just extract xyz positions)
                var colliderSw = Stopwatch.StartNew();
                var colliderMesh = ExtractCollisionMeshFromRenderMesh(renderMesh);
                double colliderGenTime = colliderSw.Elapsed.TotalMilliseconds;

                // Cache both meshes
                var cachedPair = new CachedMeshPair(renderMesh, colliderMesh);
                meshCache[coord] = cachedPair;
                Interlocked.Increment(ref cacheSize);

                Log($"[[Generation]] {coord}: Render vertices={renderMesh.Length / 7}, Collider triangles={colliderMesh.Length / 9}");

                return (renderMesh, colliderMesh, chunkGenTime, meshGenTime, colliderGenTime);
            }
            finally
            {
                lockObj.Release();
            }
        }

        // Extract just the vertex positions from render mesh for collision

        private float[] ExtractCollisionMeshFromRenderMesh(float[] renderMesh)
        {
            // Render mesh format: [x,y,z, nx,ny,nz, blockType] * N vertices
            // Collision mesh format: [x,y,z] * N vertices
            // Each triangle is 3 vertices, so we process in groups of 3

            int vertexCount = renderMesh.Length / 7;
            var collisionMesh = new float[vertexCount * 3];

            // Process triangles (3 vertices at a time) and reverse winding
            for (int tri = 0; tri < vertexCount / 3; tri++)
            {
                int baseIdx = tri * 3;

                // Original order: v0, v1, v2
                // Reversed order: v0, v2, v1 (swap v1 and v2)

                // v0 (stays same)
                int v0_render = (baseIdx + 0) * 7;
                int v0_collision = (baseIdx + 0) * 3;
                collisionMesh[v0_collision + 0] = renderMesh[v0_render + 0];
                collisionMesh[v0_collision + 1] = renderMesh[v0_render + 1];
                collisionMesh[v0_collision + 2] = renderMesh[v0_render + 2];

                // v2 (goes to position 1)
                int v2_render = (baseIdx + 2) * 7;
                int v1_collision = (baseIdx + 1) * 3;
                collisionMesh[v1_collision + 0] = renderMesh[v2_render + 0];
                collisionMesh[v1_collision + 1] = renderMesh[v2_render + 1];
                collisionMesh[v1_collision + 2] = renderMesh[v2_render + 2];

                // v1 (goes to position 2)
                int v1_render = (baseIdx + 1) * 7;
                int v2_collision = (baseIdx + 2) * 3;
                collisionMesh[v2_collision + 0] = renderMesh[v1_render + 0];
                collisionMesh[v2_collision + 1] = renderMesh[v1_render + 1];
                collisionMesh[v2_collision + 2] = renderMesh[v1_render + 2];
            }

            return collisionMesh;
        }


        // NEW: Generate collision mesh using simplified marching cubes
        private float[] GenerateColliderMeshFromMarchingCubes(Chunk chunk, ChunkCoord coord)
        {
            int step = ServerConfig.STEP; // Use larger step for collision (less detail)
            var vertices = new List<float>();

            int sizeX = Chunk.SizeX;
            int sizeY = Chunk.SizeY;
            int sizeZ = Chunk.SizeZ;

            const float iso = 0.5f;

            for (int x = 0; x < sizeX; x += step)
            {
                for (int y = 0; y < sizeY; y += step)
                {
                    for (int z = 0; z < sizeZ; z += step)
                    {
                        int nextX = Math.Min(x + step, sizeX);
                        int nextY = Math.Min(y + step, sizeY);
                        int nextZ = Math.Min(z + step, sizeZ);

                        // Get world positions
                        float wx0 = chunk.PositionX + x;
                        float wy0 = chunk.PositionY + y;
                        float wz0 = chunk.PositionZ + z;
                        float wx1 = chunk.PositionX + nextX;
                        float wy1 = chunk.PositionY + nextY;
                        float wz1 = chunk.PositionZ + nextZ;

                        // Sample 8 corners
                        var col0 = WorldGen.GetColumnData((int)wx0, (int)wz0);
                        var col1 = WorldGen.GetColumnData((int)wx1, (int)wz0);
                        var col2 = WorldGen.GetColumnData((int)wx1, (int)wz1);
                        var col3 = WorldGen.GetColumnData((int)wx0, (int)wz1);

                        float v0 = WorldGen.SampleDensityFast((int)wx0, (int)wy0, (int)wz0, col0);
                        float v1 = WorldGen.SampleDensityFast((int)wx1, (int)wy0, (int)wz0, col1);
                        float v2 = WorldGen.SampleDensityFast((int)wx1, (int)wy0, (int)wz1, col2);
                        float v3 = WorldGen.SampleDensityFast((int)wx0, (int)wy0, (int)wz1, col3);
                        float v4 = WorldGen.SampleDensityFast((int)wx0, (int)wy1, (int)wz0, col0);
                        float v5 = WorldGen.SampleDensityFast((int)wx1, (int)wy1, (int)wz0, col1);
                        float v6 = WorldGen.SampleDensityFast((int)wx1, (int)wy1, (int)wz1, col2);
                        float v7 = WorldGen.SampleDensityFast((int)wx0, (int)wy1, (int)wz1, col3);

                        // Calculate cube index
                        int cubeIndex = 0;
                        if (v0 > iso) cubeIndex |= 1;
                        if (v1 > iso) cubeIndex |= 2;
                        if (v2 > iso) cubeIndex |= 4;
                        if (v3 > iso) cubeIndex |= 8;
                        if (v4 > iso) cubeIndex |= 16;
                        if (v5 > iso) cubeIndex |= 32;
                        if (v6 > iso) cubeIndex |= 64;
                        if (v7 > iso) cubeIndex |= 128;

                        // Skip if completely inside or outside
                        if (cubeIndex == 0 || cubeIndex == 255) continue;

                        // For collision, just add a simple box at surface voxels
                        // This is much simpler than full marching cubes triangulation
                        vertices.AddRange(new[] { 
                    // Top face (most important)
                    wx0, wy1, wz0, wx1, wy1, wz1, wx1, wy1, wz0,
                    wx0, wy1, wz0, wx0, wy1, wz1, wx1, wy1, wz1,
                    
                    // Sides (for wall collision)
                    wx0, wy0, wz0, wx0, wy1, wz0, wx1, wy1, wz0,
                    wx0, wy0, wz0, wx1, wy1, wz0, wx1, wy0, wz0,

                    wx0, wy0, wz1, wx1, wy0, wz1, wx1, wy1, wz1,
                    wx0, wy0, wz1, wx1, wy1, wz1, wx0, wy1, wz1
                });
                    }
                }
            }

            return vertices.ToArray();
        }
        private float[] GenerateColliderMesh(Chunk chunk, ChunkCoord coord)
        {
            // Generate simplified collision mesh (lower resolution than render mesh)
            int step = ServerConfig.STEP; // Use larger step for simpler collision
            var vertices = new List<float>();

            int sizeX = Chunk.SizeX;
            int sizeY = Chunk.SizeY;
            int sizeZ = Chunk.SizeZ;

            for (int x = 0; x < sizeX; x += step)
            {
                for (int z = 0; z < sizeZ; z += step)
                {
                    for (int y = 0; y < sizeY; y += step)
                    {
                        int worldX = chunk.PositionX + x;
                        int worldY = chunk.PositionY + y;
                        int worldZ = chunk.PositionZ + z;

                        var columnData = WorldGen.GetColumnData(worldX, worldZ);
                        float density = WorldGen.SampleDensityFast(worldX, worldY, worldZ, columnData);

                        if (density > 0.5f) // Solid voxel
                        {
                            // Add a cube worth of triangles (simplified)
                            AddColliderCube(vertices, worldX, worldY, worldZ, step);
                        }
                    }
                }
            }

            return vertices.ToArray();
        }

        private void AddColliderCube(List<float> vertices, float x, float y, float z, int size)
        {
            float s = size;

            // Simplified: just add triangles for the cube
            // Each face = 2 triangles = 6 vertices = 18 floats (xyz per vertex)

            // Bottom face
            vertices.AddRange(new[] { x, y, z, x + s, y, z, x + s, y, z + s });
            vertices.AddRange(new[] { x, y, z, x + s, y, z + s, x, y, z + s });

            // Top face  
            vertices.AddRange(new[] { x, y + s, z, x + s, y + s, z + s, x + s, y + s, z });
            vertices.AddRange(new[] { x, y + s, z, x, y + s, z + s, x + s, y + s, z + s });

            // Front face
            vertices.AddRange(new[] { x, y, z, x, y + s, z, x + s, y + s, z });
            vertices.AddRange(new[] { x, y, z, x + s, y + s, z, x + s, y, z });

            // Back face
            vertices.AddRange(new[] { x, y, z + s, x + s, y, z + s, x + s, y + s, z + s });
            vertices.AddRange(new[] { x, y, z + s, x + s, y + s, z + s, x, y + s, z + s });

            // Left face
            vertices.AddRange(new[] { x, y, z, x, y, z + s, x, y + s, z + s });
            vertices.AddRange(new[] { x, y, z, x, y + s, z + s, x, y + s, z });

            // Right face
            vertices.AddRange(new[] { x + s, y, z, x + s, y + s, z, x + s, y + s, z + s });
            vertices.AddRange(new[] { x + s, y, z, x + s, y + s, z + s, x + s, y, z + s });
        }

        private readonly SemaphoreSlim sendSemaphore = new SemaphoreSlim(1, 1);

        private async Task SendMeshPairAsync(NetworkStream stream, float[] renderMesh, float[] colliderMesh, ChunkCoord coord, CancellationToken token)
        {
            // Send render mesh
            int renderVertexCount = renderMesh.Length / 7;
            int renderPayloadSize = sizeof(int) + renderMesh.Length * sizeof(float);
            var renderPayload = ArrayPool<byte>.Shared.Rent(renderPayloadSize);

            try
            {
                Array.Copy(BitConverter.GetBytes(renderVertexCount), 0, renderPayload, 0, sizeof(int));
                Buffer.BlockCopy(renderMesh, 0, renderPayload, sizeof(int), renderMesh.Length * sizeof(float));

                // Send collider mesh
                int colliderPayloadSize = colliderMesh.Length * sizeof(float);
                var colliderPayload = ArrayPool<byte>.Shared.Rent(colliderPayloadSize);

                try
                {
                    Buffer.BlockCopy(colliderMesh, 0, colliderPayload, 0, colliderPayloadSize);

                    await sendSemaphore.WaitAsync(token);
                    try
                    {
                        // Send render mesh length + data
                        var renderLenBytes = BitConverter.GetBytes(renderPayloadSize);
                        await stream.WriteAsync(renderLenBytes, 0, renderLenBytes.Length, token);
                        await stream.WriteAsync(renderPayload, 0, renderPayloadSize, token);

                        // Send collider mesh length + data
                        var colliderLenBytes = BitConverter.GetBytes(colliderPayloadSize);
                        await stream.WriteAsync(colliderLenBytes, 0, colliderLenBytes.Length, token);
                        await stream.WriteAsync(colliderPayload, 0, colliderPayloadSize, token);

                        await stream.FlushAsync(token);
                    }
                    finally
                    {
                        sendSemaphore.Release();
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(colliderPayload);
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
                        var entries = new List<(ChunkCoord coord, long lastAccessed)>();

                        foreach (var kvp in meshCache)
                        {
                            entries.Add((kvp.Key, kvp.Value.LastAccessed));
                        }

                        entries.Sort((a, b) => a.lastAccessed.CompareTo(b.lastAccessed));

                        int toRemove = Math.Min(entries.Count / 4, entries.Count - MaxCachedMeshes + 200);
                        int removed = 0;

                        for (int i = 0; i < toRemove; i++)
                        {
                            if (meshCache.TryRemove(entries[i].coord, out _))
                            {
                                removed++;
                                Interlocked.Decrement(ref cacheSize);
                            }
                        }

                        foreach (var coord in entries.Take(toRemove))
                        {
                            if (generationLocks.TryRemove(coord.coord, out var lockObj))
                            {
                                lockObj.Dispose();
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
