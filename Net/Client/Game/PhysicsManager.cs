using System;
using System.Numerics;
using System.Collections.Generic;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuUtilities;
using BepuUtilities.Memory;

namespace Aetheris
{
    public class PhysicsManager : IDisposable
    {
        public Simulation Simulation { get; private set; }
        private BufferPool bufferPool;
        private readonly Dictionary<int, StaticHandle> chunkColliders = new();
        private SimpleThreadDispatcher? threadDispatcher;

        public PhysicsManager()
        {
            // Create buffer pool for memory management
            bufferPool = new BufferPool();

            // Create thread dispatcher (no buffer pool parameter needed)
            threadDispatcher = new SimpleThreadDispatcher(Environment.ProcessorCount);

            // Create simulation with gravity
            var narrowPhaseCallbacks = new NarrowPhaseCallbacks();
            var poseIntegratorCallbacks = new PoseIntegratorCallbacks(new Vector3(0, -9.81f, 0)); // Gravity

            Simulation = Simulation.Create(
                bufferPool,
                narrowPhaseCallbacks,
                poseIntegratorCallbacks,
                new SolveDescription(8, 1) // velocity iterations, substep count
            );

            Console.WriteLine("[PhysicsManager] Initialized with gravity=-20");
        }

        public void Update(float deltaTime)
        {
            if (deltaTime > 0 && deltaTime < 1f) // Sanity check
            {
                Simulation.Timestep(deltaTime, threadDispatcher);
            }
        }



        // In PhysicsManager
        public void AddChunkCollider(int chunkId, OpenTK.Mathematics.Vector3 chunkOffset, OpenTK.Mathematics.Vector3[] vertices)
        {
            if (vertices == null || vertices.Length < 3)
            {
                Console.WriteLine($"[PhysicsManager] Invalid mesh for chunk {chunkId}");
                return;
            }

            RemoveChunkCollider(chunkId);

            try
            {
                // Convert chunkOffset to System.Numerics for Bepu
                var chunkOffsetNum = new System.Numerics.Vector3(chunkOffset.X, chunkOffset.Y, chunkOffset.Z);

                // Build triangles in SHAPE-LOCAL space by subtracting the chunk offset from each vertex.
                // This lets us set StaticDescription.Position = chunkOffsetNum and Bepu will place the shape correctly.
                int triangleCount = vertices.Length / 3;
                var triangles = new Triangle[triangleCount * 2];

                float minY = float.MaxValue, maxY = float.MinValue;
                for (int i = 0; i < triangleCount; i++)
                {
                    var v0 = vertices[i * 3 + 0];
                    var v1 = vertices[i * 3 + 1];
                    var v2 = vertices[i * 3 + 2];

                    // track bounds for diagnostics (use world-space values)
                    minY = Math.Min(minY, Math.Min(v0.Y, Math.Min(v1.Y, v2.Y)));
                    maxY = Math.Max(maxY, Math.Max(v0.Y, Math.Max(v1.Y, v2.Y)));

                    // Convert to System.Numerics and transform into local-shape coords:
                    var p0 = new System.Numerics.Vector3(v0.X - chunkOffset.X, v0.Y - chunkOffset.Y, v0.Z - chunkOffset.Z);
                    var p1 = new System.Numerics.Vector3(v1.X - chunkOffset.X, v1.Y - chunkOffset.Y, v1.Z - chunkOffset.Z);
                    var p2 = new System.Numerics.Vector3(v2.X - chunkOffset.X, v2.Y - chunkOffset.Y, v2.Z - chunkOffset.Z);

                    // front face

                    var normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
                    Console.WriteLine($"Tri {i}: Normal {normal}");
                    triangles[i * 2] = new Triangle(p0, p1, p2);
                    // back face (reversed winding) - keeps mesh double-sided
                    triangles[i * 2 + 1] = new Triangle(p0, p2, p1);

Console.WriteLine($"Chunk {chunkId} bounds: minY={minY:F2}, maxY={maxY:F2}");
                }

                // Diagnostics
                Console.WriteLine($"[Physics] Adding collider for chunk {chunkId} at chunkOffset(world)={chunkOffset} triangles={triangleCount * 2} minY={minY:F2} maxY={maxY:F2}");

                // Upload to Bepu
                int totalTriangles = triangleCount * 2;
                bufferPool.Take<Triangle>(totalTriangles, out var triangleBuffer);
                triangles.CopyTo(triangleBuffer); // copy into Bepu buffer

                var mesh = new Mesh(triangleBuffer, new System.Numerics.Vector3(1, 1, 1), bufferPool);
                var shapeIndex = Simulation.Shapes.Add(mesh);

                // Place the mesh in world at chunkOffsetNum (mesh triangles are local to the chunk)
                var staticDescription = new StaticDescription(chunkOffsetNum, shapeIndex);
                var handle = Simulation.Statics.Add(staticDescription);
                chunkColliders[chunkId] = handle;

                // Log first few local vertices for debugging if you want:
                if (triangleCount > 0)
                {
                    var t0 = triangles[0];
                    Console.WriteLine($"[Physics] First triangle local p0={t0.A}, p1={t0.B}, p2={t0.C} - static pos={chunkOffsetNum}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PhysicsManager] ERROR adding chunk {chunkId}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        public void RemoveChunkCollider(int chunkId)
        {
            if (chunkColliders.TryGetValue(chunkId, out var handle))
            {
                try
                {
                    // Get the shape index before removing
                    var staticReference = Simulation.Statics[handle];
                    var shapeIndex = staticReference.Shape;

                    // Remove the static body
                    Simulation.Statics.Remove(handle);

                    // Remove the shape from the shapes collection
                    Simulation.Shapes.Remove(shapeIndex);

                    chunkColliders.Remove(chunkId);
                    Console.WriteLine($"[PhysicsManager] Removed chunk {chunkId} collider");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PhysicsManager] Error removing chunk {chunkId}: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            // Clean up all colliders first
            var chunkIds = new List<int>(chunkColliders.Keys);
            foreach (var chunkId in chunkIds)
            {
                RemoveChunkCollider(chunkId);
            }

            threadDispatcher?.Dispose();
            Simulation.Dispose();
            bufferPool.Clear();
            Console.WriteLine("[PhysicsManager] Disposed");
        }

        // Callbacks for collision detection
        private struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
        {
            public void Initialize(Simulation simulation) { }

            public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
            {
                return true; // Allow all collisions
            }

            public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
            {
                return true;
            }

            public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial) where TManifold : unmanaged, IContactManifold<TManifold>
            {
                // Higher friction to prevent sliding through surfaces
                pairMaterial.FrictionCoefficient = 0.6f;
                pairMaterial.MaximumRecoveryVelocity = 10f;
                // Stiffer springs for more solid collisions
                pairMaterial.SpringSettings = new BepuPhysics.Constraints.SpringSettings(240, 10);
                return true;
            }

            public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold)
            {
                return true;
            }

            public void Dispose() { }
        }

        // Callbacks for physics integration (gravity, etc.)
        private struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
        {
            public Vector3 Gravity;
            private Vector3 gravityDt;

            public PoseIntegratorCallbacks(Vector3 gravity)
            {
                Gravity = gravity;
                gravityDt = default;
            }

            public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
            public readonly bool AllowSubstepsForUnconstrainedBodies => false;
            public readonly bool IntegrateVelocityForKinematics => false;

            public void Initialize(Simulation simulation) { }

            public void PrepareForIntegration(float dt)
            {
                gravityDt = Gravity * dt;
            }

            public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
            {
                // Apply gravity to all dynamic bodies
                velocity.Linear.Y = velocity.Linear.Y + new Vector<float>(gravityDt.Y);
            }
        }

        // Simple thread dispatcher for parallel physics
        private class SimpleThreadDispatcher : IThreadDispatcher
        {
            private readonly int threadCount;
            private readonly AutoResetEvent[] signals;
            private WorkerBufferPools? workerPools;

            public int ThreadCount => threadCount;
            public object ManagedContext => this;
            public WorkerBufferPools WorkerPools => workerPools ?? throw new InvalidOperationException("WorkerPools not initialized");
            public unsafe void* UnmanagedContext => null;

            public SimpleThreadDispatcher(int threadCount)
            {
                this.threadCount = Math.Max(1, threadCount);
                signals = new AutoResetEvent[this.threadCount];

                for (int i = 0; i < this.threadCount; i++)
                {
                    signals[i] = new AutoResetEvent(false);
                }

                // Initialize worker buffer pools - CRITICAL for BepuPhysics
                workerPools = new WorkerBufferPools(this.threadCount);

                Console.WriteLine($"[SimpleThreadDispatcher] Created {this.threadCount} worker threads with buffer pools");
            }

            public unsafe void DispatchWorkers(Action<int> workerBody, int maximumWorkerCount, void* dispatcherUnmanagedContext, object dispatcherManagedContext)
            {
                int workerCount = Math.Min(maximumWorkerCount, threadCount);

                for (int i = 0; i < workerCount; i++)
                {
                    int workerIndex = i;
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            workerBody(workerIndex);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Worker {workerIndex}] Exception: {ex.Message}\n{ex.StackTrace}");
                        }
                        finally
                        {
                            signals[workerIndex].Set();
                        }
                    });
                }

                // Wait for all workers to complete
                for (int i = 0; i < workerCount; i++)
                {
                    signals[i].WaitOne();
                }
            }

            public unsafe void DispatchWorkers(delegate*<int, IThreadDispatcher, void> workerBody, int maximumWorkerCount, void* dispatcherUnmanagedContext, object dispatcherManagedContext)
            {
                int workerCount = Math.Min(maximumWorkerCount, threadCount);

                for (int i = 0; i < workerCount; i++)
                {
                    int workerIndex = i;
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            workerBody(workerIndex, this);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Worker {workerIndex}] Exception: {ex.Message}\n{ex.StackTrace}");
                        }
                        finally
                        {
                            signals[workerIndex].Set();
                        }
                    });
                }

                // Wait for all workers to complete
                for (int i = 0; i < workerCount; i++)
                {
                    signals[i].WaitOne();
                }
            }

            public void Dispose()
            {
                foreach (var signal in signals)
                {
                    signal?.Dispose();
                }
                workerPools?.Dispose();
                Console.WriteLine("[SimpleThreadDispatcher] Disposed");
            }
        }
    }
}
