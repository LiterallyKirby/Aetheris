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
        public Vector3 Position { get; private set; }
        public float Pitch { get; private set; } = 0f;
        public float Yaw { get; private set; } = -90f;

        private float moveSpeed = 6f;
        private float sprintMultiplier = 1.6f;
        private float jumpForce = 6f;
        private float mouseSensitivity = 0.2f;
        private Vector2 lastMousePos;
        private bool firstMouse = true;

        // Physics components
        private PhysicsManager? physics;
        private BodyHandle bodyHandle;
        private bool isGrounded = false;
        private readonly float capsuleRadius = 0.4f;
        private readonly float capsuleHeight = 1.8f;
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

            // Create capsule shape for player
            var capsule = new Capsule(capsuleRadius, capsuleHeight);
            var shapeIndex = physics.Simulation.Shapes.Add(capsule);

            // Proper mass and inertia for a human
            var mass = 70f; // 70kg

            // Create body inertia with locked rotation
            var inertia = new BodyInertia
            {
                InverseMass = 1f / mass,
                InverseInertiaTensor = default // Zero rotational inertia = no rotation (prevents tipping)
            };

            var bodyDescription = BodyDescription.CreateDynamic(
                new System.Numerics.Vector3(startPosition.X, startPosition.Y, startPosition.Z),
                inertia,
                new CollidableDescription(shapeIndex, 0.1f),
                new BodyActivityDescription(0.01f)
            );

            bodyHandle = physics.Simulation.Bodies.Add(bodyDescription);

            Console.WriteLine($"[Player] Physics body created at {startPosition}, handle={bodyHandle.Value}, mass={mass}kg");
        }

        public Matrix4 GetViewMatrix()
        {
            Vector3 front = GetFront();
            Vector3 eyePosition = Position + new Vector3(0, capsuleHeight * 0.4f, 0);
            return Matrix4.LookAt(eyePosition, eyePosition + front, Vector3.UnitY);
        }

        public Vector3 GetForward()
        {
            return GetFront();
        }

        public void Update(FrameEventArgs e, KeyboardState keys, MouseState mouse)
        {
            float delta = (float)e.Time;

            // Update camera rotation
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

            // Validate body still exists
            if (!physics.Simulation.Bodies.BodyExists(bodyHandle))
            {
                Console.WriteLine("[Player] Physics body no longer exists, reinitializing...");
                InitializePhysicsBody(Position);
                return;
            }

            var bodyRef = physics.Simulation.Bodies.GetBodyReference(bodyHandle);

            // Get current position from physics (capsule center is at Y position)
            var physPos = bodyRef.Pose.Position;
            Position = new Vector3(physPos.X, physPos.Y - capsuleHeight * 0.5f, physPos.Z);

            // Get current velocity
            var vel = bodyRef.Velocity.Linear;
            Vector3 currentVelocity = new Vector3(vel.X, vel.Y, vel.Z);

            // Better ground check: look for near-zero or small downward velocity
            isGrounded = vel.Y > -2f && vel.Y < 0.5f;

            // Calculate movement direction (only horizontal)
            Vector3 front = GetFront();
            Vector3 right = Vector3.Normalize(Vector3.Cross(front, Vector3.UnitY));
            front.Y = 0;
            front = front.LengthSquared > 0.01f ? Vector3.Normalize(front) : Vector3.Zero;
            right.Y = 0;
            right = right.LengthSquared > 0.01f ? Vector3.Normalize(right) : Vector3.Zero;

            Vector3 inputDirection = Vector3.Zero;
            if (keys.IsKeyDown(Keys.W)) inputDirection += front;
            if (keys.IsKeyDown(Keys.S)) inputDirection -= front;
            if (keys.IsKeyDown(Keys.A)) inputDirection -= right;
            if (keys.IsKeyDown(Keys.D)) inputDirection += right;

            if (inputDirection.LengthSquared > 0.01f)
                inputDirection = Vector3.Normalize(inputDirection);

            // Apply sprint
            float currentSpeed = moveSpeed;
            if (keys.IsKeyDown(Keys.LeftShift) && isGrounded)
                currentSpeed *= sprintMultiplier;

            // FORCE-BASED MOVEMENT (feels responsive and grounded)
            if (inputDirection.LengthSquared > 0.01f)
            {
                Vector3 targetVelocity = inputDirection * currentSpeed;
                Vector3 currentHorizontal = new Vector3(vel.X, 0, vel.Z);
                Vector3 velocityDiff = targetVelocity - currentHorizontal;

                // Strong acceleration when grounded, weaker in air
                float acceleration = isGrounded ? 500f : 100f;
                Vector3 impulse = velocityDiff * acceleration * delta;

                // Apply impulse (converted to System.Numerics)
                bodyRef.ApplyLinearImpulse(new System.Numerics.Vector3(
                    impulse.X,
                    0,
                    impulse.Z
                ));

                // SAFE AWAKE: Only wake if body is in active set
                if (bodyRef.Awake == false)
                {
                    try
                    {
                        bodyRef.Awake = true;
                    }
                    catch
                    {
                        // Body not ready yet, impulse will be applied next frame
                    }
                }
            }
            else if (isGrounded)
            {
                // FRICTION: Aggressively dampen horizontal velocity when no input
                float friction = 0.15f; // Keep 15% of velocity each frame = strong friction
                bodyRef.Velocity.Linear = new System.Numerics.Vector3(
                    vel.X * friction,
                    vel.Y,
                    vel.Z * friction
                );
            }

            // Jump
            if (keys.IsKeyPressed(Keys.Space) && isGrounded)
            {
                bodyRef.Velocity.Linear = new System.Numerics.Vector3(
                    vel.X,
                    jumpForce,
                    vel.Z
                );

                // SAFE AWAKE: Only wake if body is in active set
                if (bodyRef.Awake == false)
                {
                    try
                    {
                        bodyRef.Awake = true;
                    }
                    catch
                    {
                        // Body not ready yet, jump will apply next frame
                    }
                }
            }

            // Debug output (less frequent)
            debugCounter++;
            if (debugCounter % 60 == 0)
            {

            }
        }
        private void UpdateSimpleMovement(float delta, KeyboardState keys)
        {
            float velocity = moveSpeed * delta;
            Vector3 front = GetFront();
            Vector3 right = Vector3.Normalize(Vector3.Cross(front, Vector3.UnitY));
            Vector3 up = Vector3.UnitY;

            if (keys.IsKeyDown(Keys.W))
                Position += front * velocity;
            if (keys.IsKeyDown(Keys.S))
                Position -= front * velocity;
            if (keys.IsKeyDown(Keys.A))
                Position -= right * velocity;
            if (keys.IsKeyDown(Keys.D))
                Position += right * velocity;
            if (keys.IsKeyDown(Keys.Space))
                Position += up * velocity;
            if (keys.IsKeyDown(Keys.LeftShift))
                Position -= up * velocity;
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
