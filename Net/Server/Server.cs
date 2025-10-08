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

        private readonly ConcurrentDictionary<ChunkCoord, (float[] renderMesh, CollisionMesh collisionMesh)> meshCache = new();

        private readonly ConcurrentDictionary<ChunkCoord, SemaphoreSlim> generationLocks = new();

        private const int MaxCachedMeshes = 20000;
        private int cacheSize = 0;

        // 60 TPS timing
        private const double TickRate = 60.0;
        private const double TickDuration = 1000.0 / TickRate;
        private long tickCount = 0;

        private readonly ConcurrentDictionary<string, PlayerState> playerStates = new();
        private enum TcpPacketType : byte
        {
            ChunkRequest = 0,
            BlockBreak = 1
        }
        private class PlayerState
        {
            public string PlayerId { get; set; } = Guid.NewGuid().ToString();
            public Vector3 Position { get; set; }
            public Vector3 Velocity { get; set; }
            public Vector2 Rotation { get; set; } // X = Yaw, Y = Pitch
            public long LastUpdate { get; set; }
            public IPEndPoint? EndPoint { get; set; }
            public bool IsGrounded { get; set; }

            // Input validation
            public uint LastProcessedSequence { get; set; }
            public Vector3 LastValidatedPosition { get; set; }
            public long LastValidationTime { get; set; }

            // Anti-cheat tuned for Quake movement
            private const float BASE_MAX_SPEED = 9.5f;      // Normal max speed from Player.cs
            private const float BHOP_MAX_SPEED = 25f;       // Allow bunnyhopping up to this speed
            private const float ACCELERATION_TOLERANCE = 1.3f; // Allow 30% over expected accel
            private const float VERTICAL_SPEED_MAX = 60f;   // Max vertical speed (gravity/jumps)


            // Track movement history for better validation
            private Queue<Vector3> recentPositions = new Queue<Vector3>(5);
            private Queue<float> recentSpeeds = new Queue<float>(5);
            private int violationCount = 0;
            private const int MAX_VIOLATIONS = 5; // Allow some packet loss/jitter

            public bool ValidatePosition(Vector3 newPosition, float deltaTime)
            {
                if (LastValidatedPosition == Vector3.Zero || deltaTime > 1.0f)
                {
                    // First update or reconnection - accept it
                    LastValidatedPosition = newPosition;
                    LastValidationTime = DateTime.UtcNow.Ticks;
                    recentPositions.Clear();
                    recentSpeeds.Clear();
                    violationCount = 0;
                    return true;
                }

                // Clamp deltaTime to reasonable values
                deltaTime = Math.Clamp(deltaTime, 0.001f, 0.5f);

                // Calculate movement
                Vector3 movement = newPosition - LastValidatedPosition;
                float distance = movement.Length();
                float horizontalDistance = new Vector3(movement.X, 0, movement.Z).Length();
                float verticalDistance = Math.Abs(movement.Y);

                // Calculate current speed
                float currentSpeed = distance / deltaTime;
                float horizontalSpeed = horizontalDistance / deltaTime;
                float verticalSpeed = verticalDistance / deltaTime;

                // Track recent speeds for pattern analysis
                recentSpeeds.Enqueue(horizontalSpeed);
                if (recentSpeeds.Count > 5) recentSpeeds.Dequeue();

                bool isValid = true;
                string reason = "";

                // 1. Check vertical speed (catches flying/jetpack cheats)
                if (verticalSpeed > VERTICAL_SPEED_MAX)
                {
                    isValid = false;
                    reason = $"excessive vertical speed: {verticalSpeed:F2} m/s (max {VERTICAL_SPEED_MAX})";
                }
                // 2. Check horizontal speed with bhop tolerance
                else if (horizontalSpeed > BHOP_MAX_SPEED)
                {
                    isValid = false;
                    reason = $"excessive horizontal speed: {horizontalSpeed:F2} m/s (max {BHOP_MAX_SPEED})";
                }
                // 3. Check for impossible acceleration (teleportation detection)
                else if (recentSpeeds.Count >= 2)
                {
                    float avgRecentSpeed = recentSpeeds.Average();
                    float speedChange = Math.Abs(horizontalSpeed - avgRecentSpeed);

                    // Allow reasonable acceleration jumps (ground accel = 14, air = 2.8)
                    // In deltaTime, max accel = 14 * 9.5 * deltaTime ≈ 133 * deltaTime
                    float maxAccelChange = 150f * deltaTime; // Generous buffer

                    if (speedChange > maxAccelChange && horizontalSpeed > avgRecentSpeed)
                    {
                        isValid = false;
                        reason = $"impossible acceleration: {speedChange:F2} m/s in {deltaTime:F3}s";
                    }
                }
                // 4. Check for teleportation (distance too far for any speed)
                else if (distance > BHOP_MAX_SPEED * deltaTime * ACCELERATION_TOLERANCE)
                {
                    isValid = false;
                    reason = $"teleport detected: {distance:F2}m in {deltaTime:F3}s (max {BHOP_MAX_SPEED * deltaTime:F2}m)";
                }

                if (!isValid)
                {
                    violationCount++;

                    if (violationCount >= MAX_VIOLATIONS)
                    {
                        Console.WriteLine($"[AntiCheat] Player {PlayerId} validation failed: {reason}");
                        Console.WriteLine($"  Position: {LastValidatedPosition} -> {newPosition}");
                        Console.WriteLine($"  Speed: H={horizontalSpeed:F2} V={verticalSpeed:F2} Total={currentSpeed:F2}");
                        Console.WriteLine($"  Violations: {violationCount}/{MAX_VIOLATIONS}");

                        // Reset violation counter after logging
                        violationCount = Math.Max(0, violationCount - 2);
                        return false;
                    }
                    else
                    {
                        // Allow minor violations (packet loss, jitter)
                        isValid = true;
                    }
                }
                else
                {
                    // Decay violations on good behavior
                    if (violationCount > 0) violationCount--;
                }

                // Update tracking
                LastValidatedPosition = newPosition;
                LastValidationTime = DateTime.UtcNow.Ticks;

                recentPositions.Enqueue(newPosition);
                if (recentPositions.Count > 5) recentPositions.Dequeue();

                return isValid;
            }

            // Optional: Reset validation state (useful for respawns)
            public void ResetValidation()
            {
                recentPositions.Clear();
                recentSpeeds.Clear();
                violationCount = 0;
                LastValidatedPosition = Position;
            }
        }

        private async Task SendPositionAcknowledgment(PlayerState state, uint sequence)
        {
            if (state.EndPoint == null || udpServer == null) return;

            // Packet format:
            // [0] = PacketType (5 = PositionAck)
            // [1-4] = Acknowledged sequence
            // [5-8] = Position X (server's validated position)
            // [9-12] = Position Y
            // [13-16] = Position Z
            // [17-20] = Velocity X
            // [21-24] = Velocity Y
            // [25-28] = Velocity Z
            // [29-32] = Yaw
            // [33-36] = Pitch

            byte[] packet = new byte[37];
            packet[0] = 5; // PositionAck packet type

            BitConverter.TryWriteBytes(packet.AsSpan(1, 4), sequence);
            BitConverter.TryWriteBytes(packet.AsSpan(5, 4), state.Position.X);
            BitConverter.TryWriteBytes(packet.AsSpan(9, 4), state.Position.Y);
            BitConverter.TryWriteBytes(packet.AsSpan(13, 4), state.Position.Z);
            BitConverter.TryWriteBytes(packet.AsSpan(17, 4), state.Velocity.X);
            BitConverter.TryWriteBytes(packet.AsSpan(21, 4), state.Velocity.Y);
            BitConverter.TryWriteBytes(packet.AsSpan(25, 4), state.Velocity.Z);
            BitConverter.TryWriteBytes(packet.AsSpan(29, 4), state.Rotation.X);
            BitConverter.TryWriteBytes(packet.AsSpan(33, 4), state.Rotation.Y);

            try
            {
                await udpServer.SendAsync(packet, packet.Length, state.EndPoint);
            }
            catch (Exception ex)
            {
                Log($"[UDP] Error sending ack to {state.PlayerId}: {ex.Message}");
            }
        }

        private async Task BroadcastPlayerState(string excludePlayer, PlayerState state)
        {
            byte[] packet = new byte[38];
            packet[0] = (byte)UdpPacketType.EntityUpdate;

            // Include player ID (first 4 bytes of GUID as simple identifier)
            var playerIdBytes = Guid.Parse(state.PlayerId).ToByteArray();
            Array.Copy(playerIdBytes, 0, packet, 1, 4);

            BitConverter.TryWriteBytes(packet.AsSpan(5, 4), state.Position.X);
            BitConverter.TryWriteBytes(packet.AsSpan(9, 4), state.Position.Y);
            BitConverter.TryWriteBytes(packet.AsSpan(13, 4), state.Position.Z);
            BitConverter.TryWriteBytes(packet.AsSpan(17, 4), state.Velocity.X);
            BitConverter.TryWriteBytes(packet.AsSpan(21, 4), state.Velocity.Y);
            BitConverter.TryWriteBytes(packet.AsSpan(25, 4), state.Velocity.Z);
            BitConverter.TryWriteBytes(packet.AsSpan(29, 4), state.Rotation.X);
            BitConverter.TryWriteBytes(packet.AsSpan(33, 4), state.Rotation.Y);
            packet[37] = (byte)(state.IsGrounded ? 1 : 0);

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
                        Log($"[UDP] Error broadcasting to {player.Key}: {ex.Message}");
                    }
                }
            }
        }

        // Add PositionAck to UdpPacketType enum

        private enum UdpPacketType : byte
        {
            PlayerPosition = 1,
            PlayerInput = 2,
            EntityUpdate = 3,
            KeepAlive = 4,
            PositionAck = 5,
            BlockBreak = 6  // NEW
        }



        private async Task HandleBlockBreakTcpAsync(NetworkStream stream, CancellationToken token)
        {
            // Read block coordinates (12 bytes)
            var buf = new byte[12];
            int totalRead = 0;
            while (totalRead < 12)
            {
                int bytesRead = await stream.ReadAsync(buf, totalRead, 12 - totalRead, token);
                if (bytesRead == 0) return;
                totalRead += bytesRead;
            }

            int x = BitConverter.ToInt32(buf, 0);
            int y = BitConverter.ToInt32(buf, 4);
            int z = BitConverter.ToInt32(buf, 8);

            Console.WriteLine($"[Server] Block broken at ({x}, {y}, {z}) via TCP");

            // Use density-based removal for smoother terrain modification
            WorldGen.RemoveBlock(x, y, z, radius: 1.5f, strength: 3.0f);

            // Invalidate affected chunk meshes
            InvalidateChunksAroundBlock(x, y, z, radius: 1.5f);

            // Broadcast to all clients via TCP
            await BroadcastBlockBreakTcp(x, y, z);
        }

        // Store active client streams for broadcasting
        private readonly ConcurrentDictionary<string, NetworkStream> activeClientStreams = new();

        // Update HandleClientAsync to track streams
        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            {
                var stream = client.GetStream();
                string clientId = client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();

                // Register this client's stream
                activeClientStreams[clientId] = stream;
                Log($"[[Server]] Client connected: {clientId}");

                try
                {
                    while (!token.IsCancellationRequested && client.Connected)
                    {
                        // Read packet type first (1 byte)
                        var packetTypeBuf = new byte[1];
                        int bytesRead = await stream.ReadAsync(packetTypeBuf, 0, 1, token);
                        if (bytesRead == 0) break;

                        TcpPacketType packetType = (TcpPacketType)packetTypeBuf[0];

                        switch (packetType)
                        {
                            case TcpPacketType.ChunkRequest:
                                await HandleChunkRequestAsync(stream, clientId, token);
                                break;

                            case TcpPacketType.BlockBreak:
                                await HandleBlockBreakTcpAsync(stream, token);
                                break;

                            default:
                                Log($"[[Server]] Unknown TCP packet type: {packetType} from {clientId}");
                                break;
                        }
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    Log($"[[Server]] Client error ({clientId}): {ex.Message}");
                }
                finally
                {
                    // Unregister client stream
                    activeClientStreams.TryRemove(clientId, out _);
                    Log($"[[Server]] Client disconnected: {clientId}");
                }
            }
        }

        private async Task BroadcastBlockBreakTcp(int x, int y, int z)
        {
            // Packet format:
            // [0] = PacketType (1 = BlockBreak)
            // [1-4] = Block X
            // [5-8] = Block Y
            // [9-12] = Block Z
            byte[] packet = new byte[13];
            packet[0] = (byte)TcpPacketType.BlockBreak;

            BitConverter.TryWriteBytes(packet.AsSpan(1, 4), x);
            BitConverter.TryWriteBytes(packet.AsSpan(5, 4), y);
            BitConverter.TryWriteBytes(packet.AsSpan(9, 4), z);

            var deadStreams = new List<string>();

            // Broadcast to all connected clients
            foreach (var kvp in activeClientStreams)
            {
                try
                {
                    await kvp.Value.WriteAsync(packet, 0, packet.Length);
                    await kvp.Value.FlushAsync();
                }
                catch (Exception ex)
                {
                    Log($"[Server] Error broadcasting block break to {kvp.Key}: {ex.Message}");
                    deadStreams.Add(kvp.Key);
                }
            }

            // Clean up dead streams
            foreach (var id in deadStreams)
            {
                activeClientStreams.TryRemove(id, out _);
            }

            Log($"[Server] Broadcasted block break at ({x}, {y}, {z}) to {activeClientStreams.Count} clients");
        }


        private async Task HandleChunkRequestAsync(NetworkStream stream, string clientId, CancellationToken token)
        {
            // Read chunk coordinates (12 bytes)
            var coord = await ReadChunkRequestAsync(stream, token);
            if (!coord.HasValue)
                return;

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
                    await SendBothMeshesAsync(stream, result.renderMesh, result.collisionMesh, coord.Value, token);
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

        private void InvalidateChunksAroundBlock(int x, int y, int z, float radius)
        {
            int affectRadius = (int)Math.Ceiling(radius);
            HashSet<ChunkCoord> chunksToInvalidate = new HashSet<ChunkCoord>();

            for (int dx = -affectRadius; dx <= affectRadius; dx++)
            {
                for (int dy = -affectRadius; dy <= affectRadius; dy++)
                {
                    for (int dz = -affectRadius; dz <= affectRadius; dz++)
                    {
                        int wx = x + dx;
                        int wy = y + dy;
                        int wz = z + dz;

                        int cx = wx / ServerConfig.CHUNK_SIZE;
                        int cy = wy / ServerConfig.CHUNK_SIZE_Y;
                        int cz = wz / ServerConfig.CHUNK_SIZE;

                        chunksToInvalidate.Add(new ChunkCoord(cx, cy, cz));
                    }
                }
            }

            foreach (var coord in chunksToInvalidate)
            {
                meshCache.TryRemove(coord, out _);
                Log($"[Server] Invalidated chunk {coord} due to block break");
            }
        }

        // Add this method to Server.cs

        private async Task BroadcastBlockBreak(int x, int y, int z)
        {
            if (udpServer == null) return;

            // Packet format:
            // [0] = PacketType (6 = BlockBreak)
            // [1-4] = Block X
            // [5-8] = Block Y
            // [9-12] = Block Z
            byte[] packet = new byte[13];
            packet[0] = (byte)UdpPacketType.BlockBreak;

            BitConverter.TryWriteBytes(packet.AsSpan(1, 4), x);
            BitConverter.TryWriteBytes(packet.AsSpan(5, 4), y);
            BitConverter.TryWriteBytes(packet.AsSpan(9, 4), z);

            // Broadcast to all connected players
            foreach (var player in playerStates.Values)
            {
                if (player.EndPoint != null)
                {
                    try
                    {
                        await udpServer.SendAsync(packet, packet.Length, player.EndPoint);
                    }
                    catch (Exception ex)
                    {
                        Log($"[UDP] Error broadcasting block break to {player.PlayerId}: {ex.Message}");
                    }
                }
            }

            Log($"[Server] Broadcasted block break at ({x}, {y}, {z}) to {playerStates.Count} players");
        }

        private void InvalidateChunkAt(int x, int y, int z)
        {
            int cx = x / ServerConfig.CHUNK_SIZE;
            int cy = y / ServerConfig.CHUNK_SIZE_Y;
            int cz = z / ServerConfig.CHUNK_SIZE;

            var coord = new ChunkCoord(cx, cy, cz);
            meshCache.TryRemove(coord, out _);
        }

        private void HandlePlayerPositionPacket(byte[] data, IPEndPoint remoteEndPoint)
        {
            if (data.Length < 38) return;

            string playerKey = remoteEndPoint.ToString();

            // Parse packet
            uint sequence = BitConverter.ToUInt32(data, 1);
            float x = BitConverter.ToSingle(data, 5);
            float y = BitConverter.ToSingle(data, 9);
            float z = BitConverter.ToSingle(data, 13);
            float velX = BitConverter.ToSingle(data, 17);
            float velY = BitConverter.ToSingle(data, 21);
            float velZ = BitConverter.ToSingle(data, 25);
            float yaw = BitConverter.ToSingle(data, 29);
            float pitch = BitConverter.ToSingle(data, 33);
            byte inputFlags = data[37];

            var state = playerStates.GetOrAdd(playerKey, _ => new PlayerState
            {
                EndPoint = remoteEndPoint,
                PlayerId = Guid.NewGuid().ToString()
            });

            // Calculate time since last update
            long now = DateTime.UtcNow.Ticks;
            float deltaTime = state.LastUpdate > 0
                ? (float)TimeSpan.FromTicks(now - state.LastUpdate).TotalSeconds
                : 0.016f;

            Vector3 newPosition = new Vector3(x, y, z);

            // Validate position (anti-cheat)
            bool isValid = state.ValidatePosition(newPosition, deltaTime);

            if (isValid)
            {
                // Accept client's position
                state.Position = newPosition;
                state.Velocity = new Vector3(velX, velY, velZ);
                state.Rotation = new Vector2(yaw, pitch);
                state.LastProcessedSequence = sequence;
            }
            else
            {
                // Position rejected - force correction
                Log($"[Server] Rejecting position from {playerKey}, forcing correction");
                newPosition = state.Position; // Use last valid position
            }

            state.LastUpdate = now;
            state.EndPoint = remoteEndPoint;

            // Send acknowledgment back to client
            _ = SendPositionAcknowledgment(state, sequence);

            // Broadcast to other players
            _ = BroadcastPlayerState(playerKey, state);
        }
        [Flags]
        private enum PlayerStateFlags : byte
        {
            None = 0,
            IsGrounded = 1,
            IsJumping = 2,
            IsCrouching = 4
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


        // New method to send both meshes:
        private async Task SendBothMeshesAsync(
            NetworkStream stream, float[] renderMesh, CollisionMesh collisionMesh,
            ChunkCoord coord, CancellationToken token)
        {
            // Render mesh
            int renderVertexCount = renderMesh.Length / 7;
            int renderPayloadSize = sizeof(int) + renderMesh.Length * sizeof(float);

            // Collision mesh
            int collisionVertexCount = collisionMesh.Vertices.Count;
            int collisionIndexCount = collisionMesh.Indices.Count;
            int collisionPayloadSize = sizeof(int) * 2 + // vertex count + index count
                                       collisionVertexCount * sizeof(float) * 3 + // vertices (Vector3)
                                       collisionIndexCount * sizeof(int); // indices

            var renderPayload = ArrayPool<byte>.Shared.Rent(renderPayloadSize);
            var collisionPayload = ArrayPool<byte>.Shared.Rent(collisionPayloadSize);

            try
            {
                // Pack render mesh
                Array.Copy(BitConverter.GetBytes(renderVertexCount), 0, renderPayload, 0, sizeof(int));
                Buffer.BlockCopy(renderMesh, 0, renderPayload, sizeof(int), renderMesh.Length * sizeof(float));

                // Pack collision mesh
                int offset = 0;
                Array.Copy(BitConverter.GetBytes(collisionVertexCount), 0, collisionPayload, offset, sizeof(int));
                offset += sizeof(int);
                Array.Copy(BitConverter.GetBytes(collisionIndexCount), 0, collisionPayload, offset, sizeof(int));
                offset += sizeof(int);

                // Pack vertices
                foreach (var v in collisionMesh.Vertices)
                {
                    Array.Copy(BitConverter.GetBytes(v.X), 0, collisionPayload, offset, sizeof(float));
                    offset += sizeof(float);
                    Array.Copy(BitConverter.GetBytes(v.Y), 0, collisionPayload, offset, sizeof(float));
                    offset += sizeof(float);
                    Array.Copy(BitConverter.GetBytes(v.Z), 0, collisionPayload, offset, sizeof(float));
                    offset += sizeof(float);
                }

                // Pack indices
                foreach (var idx in collisionMesh.Indices)
                {
                    Array.Copy(BitConverter.GetBytes(idx), 0, collisionPayload, offset, sizeof(int));
                    offset += sizeof(int);
                }

                await sendSemaphore.WaitAsync(token);
                try
                {
                    // Send render mesh length
                    var renderLenBytes = BitConverter.GetBytes(renderPayloadSize);
                    await stream.WriteAsync(renderLenBytes, 0, renderLenBytes.Length, token);
                    await stream.WriteAsync(renderPayload, 0, renderPayloadSize, token);

                    // Send collision mesh length
                    var collisionLenBytes = BitConverter.GetBytes(collisionPayloadSize);
                    await stream.WriteAsync(collisionLenBytes, 0, collisionLenBytes.Length, token);
                    await stream.WriteAsync(collisionPayload, 0, collisionPayloadSize, token);

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
                ArrayPool<byte>.Shared.Return(collisionPayload);
            }
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
                    await udpServer!.SendAsync(data, data.Length);
                }
                catch (Exception ex)
                {
                    Log($"[[UDP]] Error sending keep-alive: {ex.Message}");
                }
            });
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

        private async Task<(float[] renderMesh, CollisionMesh collisionMesh, double chunkGenTime, double meshGenTime)>
         GetOrGenerateMeshAsync(ChunkCoord coord, CancellationToken token)
        {
            // Check cache first
            if (meshCache.TryGetValue(coord, out var cached))
            {
                Log($"[[Cache]] Hit for {coord}");
                return (cached.renderMesh, cached.collisionMesh, 0, 0);
            }

            var lockObj = generationLocks.GetOrAdd(coord, _ => new SemaphoreSlim(1, 1));

            await lockObj.WaitAsync(token);
            try
            {
                if (meshCache.TryGetValue(coord, out cached))
                {
                    Log($"[[Cache]] Hit after lock for {coord}");
                    return (cached.renderMesh, cached.collisionMesh, 0, 0);
                }

                // Generate chunk
                var chunkSw = Stopwatch.StartNew();
                var chunk = await Task.Run(() => chunkManager.GetOrGenerateChunk(coord), token);
                double chunkGenTime = chunkSw.Elapsed.TotalMilliseconds;

                // Generate BOTH render and collision meshes
                var meshSw = Stopwatch.StartNew();
                var (renderMesh, collisionMesh) = await Task.Run(() =>
                    MarchingCubes.GenerateMeshes(chunk, coord, chunkManager, isoLevel: 0.5f), token);
                double meshGenTime = meshSw.Elapsed.TotalMilliseconds;

                // Cache both meshes
                meshCache[coord] = (renderMesh, collisionMesh);
                Interlocked.Increment(ref cacheSize);

                Log($"[[Generation]] {coord}: Render verts={renderMesh.Length / 7}, Collision verts={collisionMesh.Vertices.Count}");

                return (renderMesh, collisionMesh, chunkGenTime, meshGenTime);
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
