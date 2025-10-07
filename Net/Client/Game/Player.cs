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
        private const float PLAYER_WIDTH = 1.2f;   // X/Z dimensions
        private const float PLAYER_HEIGHT = 3.6f;  // Y dimension
        private static readonly float EYE_HEIGHT = PLAYER_HEIGHT - 0.4f;

        // Movement / PM parameters (Quake-like tuning)
        private const float MAX_VELOCITY = 9.5f;    // horizontal speed cap
        private const float GROUND_ACCEL = 14f;     // ground acceleration
        private const float AIR_ACCEL = 2.8f;       // air acceleration (thrust)
        private const float AIR_CONTROL = 0.95f;    // air control (rotate velocity in air)
        private const float FRICTION = 6f;          // ground friction
        private const float STOP_SPEED = 0.1f;      // friction control
        private const float JUMP_VELOCITY = 7f;     
        private const float GRAVITY = 20f;
        private const float STRAFE_ACCEL_MULT = 1.0f;
        private const float BHOP_PRESERVE = 1.0f;   // 1 => preserve horizontal velocity on jump

        // Step and slope tuning
        private const float STEP_HEIGHT = 1.25f;    // how high the player can step up
        private const float DROP_SNAP = 0.4f;       // slope follow/drop snap distance

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
            for (int y = (int)start.Y; y > (int)start.Y - 200; y--)
            {
                Vector3 testPos = new Vector3(start.X, y, start.Z);
                if (!IsBoxInSolid(testPos) && IsGroundBelow(testPos))
                {
                    return testPos + Vector3.UnitY * 0.1f;
                }
            }
            return new Vector3(start.X, 100f, start.Z);
        }

        private bool IsGroundBelow(Vector3 pos)
        {
            // check a few units below (account for taller player)
            for (int i = 1; i <= 3; i++)
            {
                if (IsSolidAt(pos - Vector3.UnitY * i)) return true;
            }
            return false;
        }

        public Matrix4 GetViewMatrix()
        {
            Vector3 eyePos = Position + Vector3.UnitY * EYE_HEIGHT;
            Vector3 front = GetFront();
            return Matrix4.LookAt(eyePos, eyePos + front, Vector3.UnitY);
        }

        public Vector3 GetForward() => GetFront();

        public void Update(FrameEventArgs e, KeyboardState keys, MouseState mouse)
        {
            float deltaTime = (float)e.Time;

            UpdateMouseLook(mouse);

            // perform ground check first
            CheckGround();

            // compute wish direction (horizontal only)
            Vector3 wishDir = GetWishDirection(keys);

            // Jumping (bunnyhop-friendly: preserves horizontal velocity)
            if (keys.IsKeyDown(Keys.Space) && isGrounded)
            {
                // preserve horizontal momentum (BHOP_PRESERVE == 1.0 keeps everything)
                velocity.Y = JUMP_VELOCITY;
                isGrounded = false;
            }

            // Movement: ground vs air
            if (isGrounded)
            {
                ApplyFriction(deltaTime);
                Accelerate(wishDir, GROUND_ACCEL, deltaTime, true);
                
                // clamp small downward velocity
                if (velocity.Y < 0) velocity.Y = 0;
            }
            else
            {
                // air thrust + Q3-like air control
                Accelerate(wishDir, AIR_ACCEL, deltaTime, false);
                AirControl(wishDir, deltaTime);

                // gravity
                velocity.Y -= GRAVITY * deltaTime;
                velocity.Y = Math.Max(velocity.Y, -50f);
            }

            // Move and collide (includes step-up and slope snapping)
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

            Vector3 wish = Vector3.Zero;
            if (keys.IsKeyDown(Keys.W)) wish += front;
            if (keys.IsKeyDown(Keys.S)) wish -= front;
            if (keys.IsKeyDown(Keys.D)) wish += right;
            if (keys.IsKeyDown(Keys.A)) wish -= right;

            if (wish.LengthSquared > 0.001f) wish = Vector3.Normalize(wish);
            return wish;
        }

        private void CheckGround()
        {
            Vector3 feetPos = Position;
            bool hasGround = false;
            float groundDistance = 0.4f;
            Vector3 groundCheck = feetPos - Vector3.UnitY * groundDistance;

            Vector3[] offsets = {
                Vector3.Zero,
                new Vector3(PLAYER_WIDTH * 0.45f, 0, 0),
                new Vector3(-PLAYER_WIDTH * 0.45f, 0, 0),
                new Vector3(0, 0, PLAYER_WIDTH * 0.45f),
                new Vector3(0, 0, -PLAYER_WIDTH * 0.45f)
            };

            foreach (var offset in offsets)
            {
                if (IsSolidAt(groundCheck + offset))
                {
                    hasGround = true;
                    break;
                }
            }

            isGrounded = hasGround && velocity.Y <= 0.1f;
            if (isGrounded) velocity.Y = 0;
        }

        private void ApplyFriction(float deltaTime)
        {
            Vector3 horizontalVel = new Vector3(velocity.X, 0, velocity.Z);
            float speed = horizontalVel.Length;

            if (speed < 0.001f)
            {
                velocity.X = 0;
                velocity.Z = 0;
                return;
            }

            float control = speed < STOP_SPEED ? STOP_SPEED : speed;
            float drop = control * FRICTION * deltaTime;
            float newSpeed = Math.Max(speed - drop, 0f);

            if (newSpeed != speed)
            {
                float scale = newSpeed / speed;
                velocity.X *= scale;
                velocity.Z *= scale;
            }
        }

        // Quake-style accelerate (works for ground and air thrust)
        private void Accelerate(Vector3 wishDir, float accel, float deltaTime, bool onGround)
        {
            if (wishDir.LengthSquared < 0.0001f) return;

            float wishSpeed = MAX_VELOCITY;

            Vector3 velH = new Vector3(velocity.X, 0, velocity.Z);
            float currentSpeed = Vector3.Dot(velH, wishDir);
            float addSpeed = wishSpeed - currentSpeed;
            if (addSpeed <= 0f) return;

            // base accel contribution
            float accelSpeed = accel * wishSpeed * deltaTime;

            if (accelSpeed > addSpeed) accelSpeed = addSpeed;

            // in-air tweak: allow smaller controlled increments (gives bhop potential)
            if (!onGround)
            {
                accelSpeed = accel * deltaTime * (wishSpeed * 0.7f);
                if (accelSpeed > addSpeed) accelSpeed = addSpeed;
            }

            Vector3 add = wishDir * accelSpeed;
            velocity.X += add.X;
            velocity.Z += add.Z;
        }

        // Quake-like air control: rotate horizontal velocity toward wishDir
        private void AirControl(Vector3 wishDir, float deltaTime)
        {
            if (wishDir.LengthSquared < 0.0001f) return;

            Vector3 velH = new Vector3(velocity.X, 0, velocity.Z);
            float speed = velH.Length;
            if (speed < 0.0001f) return;

            Vector3 vNorm = velH / speed;
            float dot = Vector3.Dot(vNorm, wishDir);
            // only apply control when somewhat aligned; dot can be negative but then k small
            float k = AIR_CONTROL * dot * dot * deltaTime * 8f;
            if (k <= 0f) return;

            Vector3 newDir = Vector3.Normalize(vNorm + wishDir * k);
            velocity.X = newDir.X * speed;
            velocity.Z = newDir.Z * speed;
        }

        // Step-up + sub-step Move + slope snapping
        private void MoveAndSlide(float deltaTime)
        {
            Vector3 movement = velocity * deltaTime;

            // sub-step to avoid snagging
            int subSteps = Math.Max(1, (int)Math.Ceiling(movement.Length / 0.2f));
            Vector3 stepMove = movement / subSteps;

            for (int s = 0; s < subSteps; s++)
            {
                // try full small step
                if (!IsBoxInSolid(Position + stepMove))
                {
                    Position += stepMove;
                    continue;
                }

                // try horizontal-only
                Vector3 horizontal = new Vector3(stepMove.X, 0, stepMove.Z);
                if (horizontal.LengthSquared > 0.0001f)
                {
                    if (!IsBoxInSolid(Position + horizontal))
                    {
                        Position += horizontal;
                        continue;
                    }

                    // attempt step-up
                    Vector3 up = Vector3.UnitY * STEP_HEIGHT;
                    if (!IsBoxInSolid(Position + up) && !IsBoxInSolid(Position + up + horizontal))
                    {
                        if (IsGroundBelow(Position + up + horizontal))
                        {
                            Position += up + horizontal;
                            velocity.Y = 0f;
                            isGrounded = true;
                            continue;
                        }
                    }

                    // axis-aligned fallback (slide along walls)
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

                // vertical attempt
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

                // if nothing works, kill horizontal vel so we don't keep pushing
                velocity.X = 0f;
                velocity.Z = 0f;
                break;
            }

            // slope follow / drop snap when grounded
            if (isGrounded)
            {
                for (float d = 0.05f; d <= DROP_SNAP; d += 0.05f)
                {
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
            float hw = PLAYER_WIDTH * 0.5f;
            float[] heights = {
                0.05f,
                PLAYER_HEIGHT * 0.25f,
                PLAYER_HEIGHT * 0.5f,
                PLAYER_HEIGHT * 0.75f,
                PLAYER_HEIGHT - 0.1f
            };
            float cornerOffset = hw * 0.9f;

            foreach (float height in heights)
            {
                Vector3 checkPos = center + Vector3.UnitY * height;
                if (IsSolidAt(checkPos)) return true;

                Vector3[] corners = {
                    checkPos + new Vector3(-cornerOffset, 0, -cornerOffset),
                    checkPos + new Vector3(cornerOffset, 0, -cornerOffset),
                    checkPos + new Vector3(-cornerOffset, 0, cornerOffset),
                    checkPos + new Vector3(cornerOffset, 0, cornerOffset),
                };

                foreach (var c in corners)
                {
                    if (IsSolidAt(c)) return true;
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
