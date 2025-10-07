using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using OpenTK.Windowing.Common;
using System;

namespace Aetheris
{
    public class Player
    {
        // Position and rotation
        public Vector3 Position { get; private set; }
        public float Pitch { get; private set; } = 0f;
        public float Yaw { get; private set; } = -90f;

        // Physics state
        private Vector3 velocity = Vector3.Zero;
        private bool isGrounded = false;

        // Debug properties
        public Vector3 Velocity => velocity;
        public bool IsGrounded => isGrounded;

        // Player dimensions (box collision)
        // === Made bigger here ===
        private const float PLAYER_WIDTH = 1.2f;   // X/Z dimensions (was 0.6)
        private const float PLAYER_HEIGHT = 3.6f;  // Y dimension (was 1.8)
        private static readonly float EYE_HEIGHT = PLAYER_HEIGHT - 0.4f; // derived, keeps eye near top

        // Movement parameters (unchanged)
        private const float GROUND_ACCEL = 14f;
        private const float AIR_ACCEL = 2f;
        private const float MAX_VELOCITY = 8f;
        private const float FRICTION = 8f;
        private const float STOP_SPEED = 1.5f;
        private const float JUMP_VELOCITY = 7f;
        private const float GRAVITY = 20f;
        private const float AIR_CONTROL = 0.3f;

        // Mouse
        private float mouseSensitivity = 0.2f;
        private Vector2 lastMousePos;
        private bool firstMouse = true;

        public Player(Vector3 startPosition)
        {
            Position = FindSafeSpawn(startPosition);
            Console.WriteLine($"[Player] Spawned at {Position}");
        }

        private Vector3 FindSafeSpawn(Vector3 start)
        {
            // Search downward for a safe position (air with ground below).
            // Return position to use as the player's "feet" location (matching the rest of the code).
            for (int y = (int)start.Y; y > (int)start.Y - 200; y--)
            {
                Vector3 testPos = new Vector3(start.X, y, start.Z);

                // Check if this position is air and has ground below
                if (!IsBoxInSolid(testPos) && IsGroundBelow(testPos))
                {
                    // keep the player feet slightly above block center to avoid being stuck
                    return testPos + Vector3.UnitY * 0.1f; // small offset; you can increase if you clip into floors
                }
            }

            // Fallback: spawn high and fall
            return new Vector3(start.X, 100f, start.Z);
        }

        private bool IsGroundBelow(Vector3 pos)
        {
            // Check if there's solid ground within 3 units below (in case player is tall)
            for (int i = 1; i <= 3; i++)
            {
                if (IsSolidAt(pos - Vector3.UnitY * i))
                    return true;
            }
            return false;
        }

        public Matrix4 GetViewMatrix()
        {
            // Position is feet; eye is feet + EYE_HEIGHT
            Vector3 eyePos = Position + Vector3.UnitY * EYE_HEIGHT;
            Vector3 front = GetFront();
            return Matrix4.LookAt(eyePos, eyePos + front, Vector3.UnitY);
        }

        public Vector3 GetForward() => GetFront();

        public void Update(FrameEventArgs e, KeyboardState keys, MouseState mouse)
        {
            float deltaTime = (float)e.Time;

            UpdateMouseLook(mouse);

            Vector3 wishDir = GetWishDirection(keys);

            // Ground check
            CheckGround();

            // Jumping
            if (keys.IsKeyDown(Keys.Space) && isGrounded)
            {
                velocity.Y = JUMP_VELOCITY;
                isGrounded = false;
            }

            // Movement
            if (isGrounded)
            {
                ApplyFriction(deltaTime);
                Accelerate(wishDir, GROUND_ACCEL, deltaTime);

                // Clamp downward velocity when grounded
                if (velocity.Y < 0)
                    velocity.Y = 0;
            }
            else
            {
                float accel = AIR_ACCEL + (wishDir.LengthSquared > 0.001f ? AIR_CONTROL : 0);
                Accelerate(wishDir, accel, deltaTime);
                velocity.Y -= GRAVITY * deltaTime;
                velocity.Y = Math.Max(velocity.Y, -50f); // Terminal velocity
            }

            // Collide and slide
            MoveAndSlide(deltaTime);

            // Safety respawn
            if (Position.Y < -200f)
            {
                Position = new Vector3(Position.X, 100f, Position.Z);
                velocity = Vector3.Zero;
            }
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

            Yaw += dx * mouseSensitivity;
            Pitch += dy * mouseSensitivity;
            Pitch = Math.Clamp(Pitch, -89f, 89f);
        }

        private Vector3 GetWishDirection(KeyboardState keys)
        {
            Vector3 front = GetFrontHorizontal();
            Vector3 right = Vector3.Normalize(Vector3.Cross(front, Vector3.UnitY));

            Vector3 wishDir = Vector3.Zero;

            if (keys.IsKeyDown(Keys.W)) wishDir += front;
            if (keys.IsKeyDown(Keys.S)) wishDir -= front;
            if (keys.IsKeyDown(Keys.D)) wishDir += right;
            if (keys.IsKeyDown(Keys.A)) wishDir -= right;

            if (wishDir.LengthSquared > 0.001f)
                wishDir = Vector3.Normalize(wishDir);

            return wishDir;
        }

        private void CheckGround()
        {
            // Position represents feet. Sample slightly below the feet plane for ground.
            Vector3 feetPos = Position;

            // Check slightly below feet
            bool hasGround = false;
            float groundDistance = 0.4f; // increased because player is taller/wider
            Vector3 groundCheck = feetPos - Vector3.UnitY * groundDistance;

            // Check multiple points across the base for stability
            Vector3[] offsets = {
                Vector3.Zero,
                new Vector3(PLAYER_WIDTH * 0.45f, 0, 0),
                new Vector3(-PLAYER_WIDTH * 0.45f, 0, 0),
                new Vector3(0, 0, PLAYER_WIDTH * 0.45f),
                new Vector3(0, 0, -PLAYER_WIDTH * 0.45f)
            };

            foreach (var offset in offsets)
            {
                Vector3 checkPos = groundCheck + offset;
                if (IsSolidAt(checkPos))
                {
                    hasGround = true;
                    break;
                }
            }

            // Only grounded if we have ground AND moving down or stationary
            isGrounded = hasGround && velocity.Y <= 0.1f;

            if (isGrounded)
            {
                velocity.Y = 0;
            }
        }

        private void ApplyFriction(float deltaTime)
        {
            Vector3 horizontalVel = new Vector3(velocity.X, 0, velocity.Z);
            float speed = horizontalVel.Length;

            if (speed < 0.1f)
            {
                velocity.X = 0;
                velocity.Z = 0;
                return;
            }

            float control = speed < STOP_SPEED ? STOP_SPEED : speed;
            float drop = control * FRICTION * deltaTime;
            float newSpeed = Math.Max(speed - drop, 0f);

            if (speed > 0)
            {
                float scale = newSpeed / speed;
                velocity.X *= scale;
                velocity.Z *= scale;
            }
        }

        private void Accelerate(Vector3 wishDir, float accel, float deltaTime)
        {
            if (wishDir.LengthSquared < 0.001f) return;

            float currentSpeed = Vector3.Dot(velocity, wishDir);
            float addSpeed = MAX_VELOCITY - currentSpeed;

            if (addSpeed <= 0) return;

            float accelSpeed = accel * MAX_VELOCITY * deltaTime;
            if (accelSpeed > addSpeed) accelSpeed = addSpeed;

            velocity += wishDir * accelSpeed;
        }

        private const float STEP_HEIGHT = 1.25f; // how high the player can step up; tune down if too floaty

        // replace your MoveAndSlide(...) with this implementation
        private void MoveAndSlide(float deltaTime)
        {
            Vector3 movement = velocity * deltaTime;

            // break big moves into smaller sub-steps to avoid snagging on geometry
            int subSteps = Math.Max(1, (int)Math.Ceiling(movement.Length / 0.2f));
            Vector3 stepMove = movement / subSteps;

            for (int s = 0; s < subSteps; s++)
            {
                // try the full small step first
                if (!IsBoxInSolid(Position + stepMove))
                {
                    Position += stepMove;
                    continue;
                }

                // horizontal-only attempt
                Vector3 horizontal = new Vector3(stepMove.X, 0, stepMove.Z);
                if (horizontal.LengthSquared > 0.0001f)
                {
                    // if horizontal-only is clear, do it
                    if (!IsBoxInSolid(Position + horizontal))
                    {
                        Position += horizontal;
                        continue;
                    }

                    // Try stepping up: check feet + STEP_HEIGHT and feet + STEP_HEIGHT + horizontal are free,
                    // and that there's ground underneath the destination (so we don't step into mid-air)
                    Vector3 up = Vector3.UnitY * STEP_HEIGHT;
                    if (!IsBoxInSolid(Position + up) && !IsBoxInSolid(Position + up + horizontal))
                    {
                        if (IsGroundBelow(Position + up + horizontal))
                        {
                            Position += up + horizontal;
                            velocity.Y = 0f; // clear vertical velocity on a successful step-up
                            isGrounded = true;
                            continue;
                        }
                    }

                    // fallback: try axis-aligned moves (helps slide along walls)
                    Vector3 xMove = new Vector3(horizontal.X, 0, 0);
                    if (xMove.LengthSquared > 0.0001f && !IsBoxInSolid(Position + xMove))
                    {
                        Position += xMove;
                        continue;
                    }
                    else
                    {
                        velocity.X = 0f;
                    }

                    Vector3 zMove = new Vector3(0, 0, horizontal.Z);
                    if (zMove.LengthSquared > 0.0001f && !IsBoxInSolid(Position + zMove))
                    {
                        Position += zMove;
                        continue;
                    }
                    else
                    {
                        velocity.Z = 0f;
                    }
                }

                // vertical-only attempt (stairs, falling, ceiling)
                Vector3 vertical = new Vector3(0, stepMove.Y, 0);
                if (vertical.LengthSquared > 0.0001f && !IsBoxInSolid(Position + vertical))
                {
                    Position += vertical;
                    if (vertical.Y < 0)
                    {
                        velocity.Y = 0;
                        isGrounded = true;
                    }
                    else if (vertical.Y > 0)
                    {
                        velocity.Y = 0;
                    }
                    continue;
                }

                // if all else fails: nudge horizontal velocity off so player won't keep pressing into the wall forever
                velocity.X = 0f;
                velocity.Z = 0f;
                break;
            }

            // Basic slope snapping: if we're considered grounded, attempt to follow small drops so the player
            // smoothly walks down ramps instead of hovering (tunable drop value).
            if (isGrounded)
            {
                const float DROP_SNAP = 0.4f;
                for (float d = 0.05f; d <= DROP_SNAP; d += 0.05f)
                {
                    // if there's space below but ground exists a little further down, drop to it.
                    if (!IsBoxInSolid(Position - Vector3.UnitY * d))
                    {
                        if (IsGroundBelow(Position - Vector3.UnitY * d + Vector3.UnitY * 0.05f))
                        {
                            Position -= Vector3.UnitY * d;
                            break;
                        }
                    }
                }
            }
        }
        private bool IsBoxInSolid(Vector3 center)
        {
            // center is treated as the player's feet location in this code.
            float hw = PLAYER_WIDTH * 0.5f;
            float hh = PLAYER_HEIGHT; // we'll sample relative to feet, so use full height

            // Sample multiple heights from near the feet up to near the head (more samples for larger body).
            float[] heights = {
                0.05f,                       // just above feet
                PLAYER_HEIGHT * 0.25f,
                PLAYER_HEIGHT * 0.5f,
                PLAYER_HEIGHT * 0.75f,
                PLAYER_HEIGHT - 0.1f         // just below the top
            };
            float cornerOffset = hw * 0.9f; // reach closer to edges for wide players

            foreach (float height in heights)
            {
                Vector3 checkPos = center + Vector3.UnitY * height;

                // Check center
                if (IsSolidAt(checkPos))
                    return true;

                // Check 4 corners at this height
                Vector3[] corners = {
                    checkPos + new Vector3(-cornerOffset, 0, -cornerOffset),
                    checkPos + new Vector3(cornerOffset, 0, -cornerOffset),
                    checkPos + new Vector3(-cornerOffset, 0, cornerOffset),
                    checkPos + new Vector3(cornerOffset, 0, cornerOffset),
                };

                foreach (var corner in corners)
                {
                    if (IsSolidAt(corner))
                        return true;
                }
            }

            return false;
        }

        private bool IsSolidAt(Vector3 pos)
        {
            float density = WorldGen.SampleDensity(
                (int)MathF.Round(pos.X),
                (int)MathF.Round(pos.Y),
                (int)MathF.Round(pos.Z)
            );

            return density > 0.5f;
        }

        private Vector3 GetFront()
        {
            float yawRad = MathHelper.DegreesToRadians(Yaw);
            float pitchRad = MathHelper.DegreesToRadians(Pitch);

            return Vector3.Normalize(new Vector3(
                MathF.Cos(pitchRad) * MathF.Cos(yawRad),
                MathF.Sin(pitchRad),
                MathF.Cos(pitchRad) * MathF.Sin(yawRad)
            ));
        }

        private Vector3 GetFrontHorizontal()
        {
            float yawRad = MathHelper.DegreesToRadians(Yaw);
            return Vector3.Normalize(new Vector3(MathF.Cos(yawRad), 0, MathF.Sin(yawRad)));
        }

        public Vector3 GetPlayersChunk()
        {
            return new Vector3(
                (int)Math.Floor(Position.X / ClientConfig.CHUNK_SIZE),
                (int)Math.Floor(Position.Y / ClientConfig.CHUNK_SIZE_Y),
                (int)Math.Floor(Position.Z / ClientConfig.CHUNK_SIZE)
            );
        }
    }
}
