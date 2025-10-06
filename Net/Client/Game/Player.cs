using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using OpenTK.Windowing.Common;
using System;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;
using OtkMath = OpenTK.Mathematics.MathHelper;

namespace Aetheris
{
    public class Player
    {
        public Vector3 Position { get; set; }
        public float Pitch { get; private set; } = 0f;
        public float Yaw { get; private set; } = -90f;

        // Quake 3 movement constants (scaled for our physics system)
        private const float GroundAccelerate = 10f;     // Ground acceleration
        private const float AirAccelerate = 1f;         // Air acceleration (for air control)
        private const float MaxGroundSpeed = 12f;       // Max ground speed
        private const float MaxAirSpeed = 1f;           // Max speed with air control
        private const float Friction = 6f;              // Ground friction coefficient
        private const float StopSpeed = 1f;             // Speed below which friction applies more
        private const float JumpSpeed = 9f;             // Jump velocity
        
        private float mouseSensitivity = 0.2f;
        private Vector2 lastMousePos;
        private bool firstMouse = true;

        // Physics components
        private PhysicsManager? physics;
        private BodyHandle bodyHandle;
        private bool isGrounded = false;
        private readonly float capsuleRadius = 0.4f;
        private readonly float capsuleHeight = 2.5f;
        private int debugCounter = 0;

        public Player(Vector3 startPosition, PhysicsManager? physicsManager = null)
        {
            Position = startPosition;
            physics = physicsManager;

            if (physics != null)
            {
                InitializePhysicsBody(startPosition);
            }
        }

        private void InitializePhysicsBody(Vector3 startPosition)
        {
            if (physics == null) return;

            var capsule = new Capsule(capsuleRadius, capsuleHeight);
            var shapeIndex = physics.Simulation.Shapes.Add(capsule);

            var mass = 70f;
            var inertia = new BodyInertia
            {
                InverseMass = 1f / mass,
                InverseInertiaTensor = default // Locked rotation
            };

            var bodyDescription = BodyDescription.CreateDynamic(
                new System.Numerics.Vector3(startPosition.X, startPosition.Y, startPosition.Z),
                inertia,
                new CollidableDescription(shapeIndex, 0.1f),
                new BodyActivityDescription(0.01f)
            );

            bodyHandle = physics.Simulation.Bodies.Add(bodyDescription);
            Console.WriteLine($"[Player] Q3-style physics body created at {startPosition}");
        }

        public Matrix4 GetViewMatrix()
        {
            Vector3 front = GetFront();
            Vector3 eyePosition = Position + new Vector3(0, capsuleHeight * 0.8f, 0);
            return Matrix4.LookAt(eyePosition, eyePosition + front, Vector3.UnitY);
        }

        public Vector3 GetForward() => GetFront();

        public void TeleportTo(Vector3 newPosition)
        {
            if (physics != null && physics.Simulation.Bodies.BodyExists(bodyHandle))
            {
                var bodyRef = physics.Simulation.Bodies.GetBodyReference(bodyHandle);
                bodyRef.Pose.Position = new System.Numerics.Vector3(
                    newPosition.X,
                    newPosition.Y + capsuleHeight * 0.5f,
                    newPosition.Z
                );
                bodyRef.Velocity.Linear = System.Numerics.Vector3.Zero;
                bodyRef.Awake = true;
                Position = newPosition;
                Console.WriteLine($"[Player] Teleported to {newPosition}");
            }
            else
            {
                Position = newPosition;
            }
        }

        public void Update(FrameEventArgs e, KeyboardState keys, MouseState mouse)
        {
            float delta = (float)e.Time;
            UpdateMouseLook(mouse);

            if (physics != null)
            {
                UpdatePhysicsMovement(delta, keys);
            }
            else
            {
                UpdateSimpleMovement(delta, keys);
            }
        }

        private void UpdatePhysicsMovement(float delta, KeyboardState keys)
        {
            if (physics == null) return;

            if (!physics.Simulation.Bodies.BodyExists(bodyHandle))
            {
                Console.WriteLine("[Player] Physics body no longer exists, reinitializing...");
                InitializePhysicsBody(Position);
                return;
            }

            var bodyRef = physics.Simulation.Bodies.GetBodyReference(bodyHandle);
            var physPos = bodyRef.Pose.Position;
            Position = new Vector3(physPos.X, physPos.Y - capsuleHeight * 0.5f, physPos.Z);

            var vel = bodyRef.Velocity.Linear;
            
            // Ground detection - check if vertical velocity is small
            isGrounded = Math.Abs(vel.Y) < 0.5f;

            // Get wish direction from input
            Vector3 front = GetFront();
            Vector3 right = Vector3.Normalize(Vector3.Cross(front, Vector3.UnitY));
            front.Y = 0;
            front = front.LengthSquared > 0.01f ? Vector3.Normalize(front) : Vector3.Zero;
            right.Y = 0;
            right = right.LengthSquared > 0.01f ? Vector3.Normalize(right) : Vector3.Zero;

            Vector3 wishDir = Vector3.Zero;
            if (keys.IsKeyDown(Keys.W)) wishDir += front;
            if (keys.IsKeyDown(Keys.S)) wishDir -= front;
            if (keys.IsKeyDown(Keys.A)) wishDir -= right;
            if (keys.IsKeyDown(Keys.D)) wishDir += right;

            if (wishDir.LengthSquared > 0.01f)
                wishDir = Vector3.Normalize(wishDir);

            // Current horizontal velocity
            Vector3 horizVel = new Vector3(vel.X, 0, vel.Z);
            float speed = horizVel.Length;

            if (isGrounded)
            {
                // Apply Quake 3 ground friction
                ApplyFriction(ref bodyRef, speed, delta);
                
                // Ground acceleration
                float wishSpeed = MaxGroundSpeed;
                if (keys.IsKeyDown(Keys.LeftShift))
                    wishSpeed *= 1.5f;
                
                Accelerate(ref bodyRef, wishDir, wishSpeed, GroundAccelerate, delta);
                
                // Slope assist - applies upward force when moving on slopes
                if (wishDir.LengthSquared > 0.01f)
                {
                    // Add a small upward component to help climb slopes
                    float slopeAssist = 0.3f;
                    var movement = new System.Numerics.Vector3(
                        wishDir.X * wishSpeed * delta,
                        slopeAssist,
                        wishDir.Z * wishSpeed * delta
                    );
                    
                    bodyRef.Velocity.Linear = new System.Numerics.Vector3(
                        bodyRef.Velocity.Linear.X,
                        Math.Max(bodyRef.Velocity.Linear.Y, movement.Y),
                        bodyRef.Velocity.Linear.Z
                    );
                }
            }
            else
            {
                // Air control (Quake 3 style)
                float wishSpeed = MaxAirSpeed;
                AirAccelerate_Q3(ref bodyRef, wishDir, wishSpeed, AirAccelerate, delta);
            }

            // Jump
            if (keys.IsKeyPressed(Keys.Space) && isGrounded)
            {
                bodyRef.Velocity.Linear = new System.Numerics.Vector3(
                    vel.X,
                    JumpSpeed,
                    vel.Z
                );
                
                if (!bodyRef.Awake)
                {
                    try { bodyRef.Awake = true; } catch { }
                }
            }

            // Wake body if needed
            if (!bodyRef.Awake && wishDir.LengthSquared > 0.01f)
            {
                try { bodyRef.Awake = true; } catch { }
            }

            debugCounter++;
            if (debugCounter % 60 == 0)
            {
                // Uncomment for speed display:
                // Console.WriteLine($"[Player] Speed: {speed:F1}, Grounded: {isGrounded}");
            }
        }

        // Quake 3 friction implementation
        private void ApplyFriction(ref BodyReference bodyRef, float speed, float delta)
        {
            if (speed < 0.1f) return;

            var vel = bodyRef.Velocity.Linear;
            float control = speed < StopSpeed ? StopSpeed : speed;
            float drop = control * Friction * delta;

            float newSpeed = Math.Max(speed - drop, 0f);
            if (speed > 0.01f)
            {
                float scale = newSpeed / speed;
                bodyRef.Velocity.Linear = new System.Numerics.Vector3(
                    vel.X * scale,
                    vel.Y,
                    vel.Z * scale
                );
            }
        }

        // Quake 3 ground acceleration
        private void Accelerate(ref BodyReference bodyRef, Vector3 wishDir, float wishSpeed, float accel, float delta)
        {
            if (wishDir.LengthSquared < 0.01f) return;

            var vel = bodyRef.Velocity.Linear;
            Vector3 currentVel = new Vector3(vel.X, 0, vel.Z);
            
            float currentSpeed = Vector3.Dot(currentVel, wishDir);
            float addSpeed = wishSpeed - currentSpeed;
            
            if (addSpeed <= 0) return;

            float accelSpeed = Math.Min(accel * delta * wishSpeed, addSpeed);
            
            // Set velocity directly instead of applying impulse
            bodyRef.Velocity.Linear = new System.Numerics.Vector3(
                vel.X + wishDir.X * accelSpeed,
                vel.Y,
                vel.Z + wishDir.Z * accelSpeed
            );
        }

        // Quake 3 air acceleration (for strafe jumping)
        private void AirAccelerate_Q3(ref BodyReference bodyRef, Vector3 wishDir, float wishSpeed, float accel, float delta)
        {
            if (wishDir.LengthSquared < 0.01f) return;

            var vel = bodyRef.Velocity.Linear;
            Vector3 currentVel = new Vector3(vel.X, 0, vel.Z);
            
            float currentSpeed = Vector3.Dot(currentVel, wishDir);
            float addSpeed = wishSpeed - currentSpeed;
            
            if (addSpeed <= 0) return;

            float accelSpeed = Math.Min(accel * delta * wishSpeed, addSpeed);
            
            // Set velocity directly instead of applying impulse
            bodyRef.Velocity.Linear = new System.Numerics.Vector3(
                vel.X + wishDir.X * accelSpeed,
                vel.Y,
                vel.Z + wishDir.Z * accelSpeed
            );
        }

        private void UpdateSimpleMovement(float delta, KeyboardState keys)
        {
            float velocity = 6f * delta;
            Vector3 front = GetFront();
            Vector3 right = Vector3.Normalize(Vector3.Cross(front, Vector3.UnitY));
            Vector3 up = Vector3.UnitY;

            if (keys.IsKeyDown(Keys.W)) Position += front * velocity;
            if (keys.IsKeyDown(Keys.S)) Position -= front * velocity;
            if (keys.IsKeyDown(Keys.A)) Position -= right * velocity;
            if (keys.IsKeyDown(Keys.D)) Position += right * velocity;
            if (keys.IsKeyDown(Keys.Space)) Position += up * velocity;
            if (keys.IsKeyDown(Keys.LeftShift)) Position -= up * velocity;
        }

        private void UpdateMouseLook(MouseState mouse)
        {
            if (firstMouse)
            {
                lastMousePos = new Vector2(mouse.X, mouse.Y);
                firstMouse = false;
                return;
            }

            float dx = mouse.X - lastMousePos.X;
            float dy = lastMousePos.Y - mouse.Y;
            lastMousePos = new Vector2(mouse.X, mouse.Y);

            dx *= mouseSensitivity;
            dy *= mouseSensitivity;

            Yaw += dx;
            Pitch += dy;
            Pitch = Math.Clamp(Pitch, -89f, 89f);
        }

        private Vector3 GetFront()
        {
            float yawRad = OtkMath.DegreesToRadians(Yaw);
            float pitchRad = OtkMath.DegreesToRadians(Pitch);

            Vector3 front;
            front.X = MathF.Cos(pitchRad) * MathF.Cos(yawRad);
            front.Y = MathF.Sin(pitchRad);
            front.Z = MathF.Cos(pitchRad) * MathF.Sin(yawRad);
            return Vector3.Normalize(front);
        }

        public Vector3 GetPlayersChunk()
        {
            return new Vector3(
                (int)Math.Floor(Position.X / ClientConfig.CHUNK_SIZE),
                (int)Math.Floor(Position.Y / ClientConfig.CHUNK_SIZE_Y),
                (int)Math.Floor(Position.Z / ClientConfig.CHUNK_SIZE)
            );
        }

        public void Cleanup()
        {
            if (physics != null && bodyHandle.Value >= 0)
            {
                try
                {
                    physics.Simulation.Bodies.Remove(bodyHandle);
                    Console.WriteLine("[Player] Physics body removed");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Player] Error removing body: {ex.Message}");
                }
            }
        }
    }
}
