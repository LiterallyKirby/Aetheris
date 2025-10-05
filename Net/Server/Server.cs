using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Aetheris
{
    public class Server
    {
        private TcpListener? listener;
        private CancellationTokenSource? cts;
        private readonly ChunkManager chunkManager = new();

        // Mesh cache with LRU eviction
        private readonly ConcurrentDictionary<ChunkCoord, CachedMesh> meshCache = new();
        private readonly ConcurrentDictionary<ChunkCoord, SemaphoreSlim> generationLocks = new();

        private const int MaxCachedMeshes = 20000;
        private int cacheSize = 0;

        // 60 TPS timing
        private const double TickRate = 60.0;
        private const double TickDuration = 1000.0 / TickRate; // ms per tick
        private long tickCount = 0;

        // Performance tracking
        private long totalRequests = 0;
        private double totalChunkGenTime = 0;
        private double totalMeshGenTime = 0;
        private double totalSendTime = 0;
        private readonly object perfLock = new();

        // File logging
        private StreamWriter? logWriter;
        private readonly object logLock = new();

        private class CachedMesh
        {
            public float[] Data { get; }
            public long LastAccessed { get; set; }

            public CachedMesh(float[] data)
            {
                Data = data;
                LastAccessed = DateTime.UtcNow.Ticks;
            }
        }


        private void Log(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string logMessage = $"[{timestamp}] {message}";

            lock (logLock)
            {
                // Write to console
                Console.WriteLine(logMessage);  // ✅ not Log(logMessage)

                // Write to file
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
            // Initialize log file
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
            WorldGen.Initialize(); // <- Ensure noise generators are ready

            listener = new TcpListener(IPAddress.Any, Config.SERVER_PORT);
            listener.Start();
            listener.Server.NoDelay = true;
            cts = new CancellationTokenSource();

            Log($"[[Server]] Listening on port {Config.SERVER_PORT} @ {TickRate} TPS");
            Log("[[Server]] Performance timing enabled - waiting for connections...");

            // Start background tasks
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
                    // Server tick logic (if needed)
                    tickCount++;
                    accumulator -= TickDuration;
                }

                // Sleep to maintain tick rate
                double sleepTime = TickDuration - sw.Elapsed.TotalMilliseconds;
                if (sleepTime > 1)
                {
                    await Task.Delay((int)sleepTime, token);
                }

                // Log stats every 5 seconds
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

                        // Process request asynchronously without blocking
                        _ = Task.Run(async () =>
                        {
                            var requestSw = Stopwatch.StartNew();
                            double chunkTime = 0, meshTime = 0, sendTime = 0;

                            try
                            {
                                var result = await GetOrGenerateMeshAsync(coord.Value, token);
                                var mesh = result.mesh;
                                chunkTime = result.chunkGenTime;
                                meshTime = result.meshGenTime;

                                var sendSw = Stopwatch.StartNew();
                                await SendMeshAsync(stream, mesh, coord.Value, token);
                                sendTime = sendSw.Elapsed.TotalMilliseconds;

                                // Update performance stats
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


        private async Task<(float[] mesh, double chunkGenTime, double meshGenTime)> GetOrGenerateMeshAsync(ChunkCoord coord, CancellationToken token)
        {
            double chunkGenTime = 0;
            double meshGenTime = 0;

            // Check cache first
            if (meshCache.TryGetValue(coord, out var cached))
            {
                cached.LastAccessed = DateTime.UtcNow.Ticks;
                Log($"[[Cache]] Hit for {coord}");
                return (cached.Data, 0, 0);
            }

            // Ensure only one generation per chunk at a time
            var lockObj = generationLocks.GetOrAdd(coord, _ => new SemaphoreSlim(1, 1));

            await lockObj.WaitAsync(token);
            try
            {
                // Double-check after acquiring lock
                if (meshCache.TryGetValue(coord, out cached))
                {
                    cached.LastAccessed = DateTime.UtcNow.Ticks;
                    Log($"[[Cache]] Hit after lock for {coord}");
                    return (cached.Data, 0, 0);
                }

                var totalSw = Stopwatch.StartNew();

                // Generate chunk
                var chunkSw = Stopwatch.StartNew();
                var chunk = await Task.Run(() => chunkManager.GetOrGenerateChunk(coord), token);
                chunkGenTime = chunkSw.Elapsed.TotalMilliseconds;

                // Print biome info
                var columns = chunkManager.GetColumnDataForChunk(coord);
                var centerBiome = columns[Config.CHUNK_SIZE / 2, Config.CHUNK_SIZE / 2].Biome;
                Log($"[[Biome]] Chunk {coord} center biome: {centerBiome}");

                // Generate mesh
                var meshSw = Stopwatch.StartNew();
                var mesh = await Task.Run(() =>
                {
                    return MarchingCubes.GenerateMesh(chunk, isoLevel: 0.5f);
                }, token);
                meshGenTime = meshSw.Elapsed.TotalMilliseconds;

                // Cache the result
                var cachedMesh = new CachedMesh(mesh);
                meshCache[coord] = cachedMesh;
                Interlocked.Increment(ref cacheSize);

                Log($"[[Generation]] {coord}: Chunk={chunkGenTime:F2}ms Mesh={meshGenTime:F2}ms Vertices={mesh.Length / 7}");

                return (mesh, chunkGenTime, meshGenTime);
            }
            finally
            {
                lockObj.Release();
            }
        }


        private readonly SemaphoreSlim sendSemaphore = new SemaphoreSlim(1, 1);

        private async Task SendMeshAsync(NetworkStream stream, float[] mesh, ChunkCoord coord, CancellationToken token)
        {
            const int floatsPerVertex = 7; // pos(3) + normal(3) + blockType(1)
            int vertexCount = mesh.Length / floatsPerVertex;

            int payloadSize = sizeof(int) + mesh.Length * sizeof(float);

            var payload = ArrayPool<byte>.Shared.Rent(payloadSize);
            try
            {
                Array.Copy(BitConverter.GetBytes(vertexCount), 0, payload, 0, sizeof(int));
                Buffer.BlockCopy(mesh, 0, payload, sizeof(int), mesh.Length * sizeof(float));

                var lenBytes = BitConverter.GetBytes(payloadSize);

                // Serialize sends to prevent interleaving
                await sendSemaphore.WaitAsync(token);
                try
                {
                    await stream.WriteAsync(lenBytes, 0, lenBytes.Length, token);
                    await stream.WriteAsync(payload, 0, payloadSize, token);
                    await stream.FlushAsync(token);
                }
                finally
                {
                    sendSemaphore.Release();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }

        private async Task CacheCleanupLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(60000, token); // Clean up every 60 seconds

                    if (cacheSize > MaxCachedMeshes)
                    {
                        var cleanupSw = Stopwatch.StartNew();

                        var entries = new System.Collections.Generic.List<(ChunkCoord coord, long lastAccessed)>();

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
