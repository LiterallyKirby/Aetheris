using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using OpenTK.Windowing.Common;
using System;
using OtkMath = OpenTK.Mathematics.MathHelper;

namespace Aetheris
{
    public class Player
    {
        public Vector3 Position { get; set; }
        public float Pitch { get; private set; } = 0f;
        public float Yaw { get; private set; } = -90f;

        // Movement parameters
        private const float GroundAcceleration = 80f;
        private const float AirAcceleration = 12f;
        private const float MaxGroundSpeed = 8f;
        private const float MaxAirSpeed = 8f;
        private const float Friction = 6f;
        private const float StopSpeed = 1.0f;
        private const float JumpSpeed = 8.5f;
        private const float SprintMultiplier = 1.5f;
        private const float Gravity = 20f;

        private float mouseSensitivity = 0.2f;
        private Vector2 lastMousePos;
        private bool firstMouse = true;

        // Collision parameters
        private readonly float playerRadius = 0.4f;
        private readonly float playerHeight = 1.8f;
        private readonly float eyeHeight = 1.6f;

        // Physics state
        private Vector3 velocity = Vector3.Zero;
        private bool isGrounded = false;
        private float timeSinceGrounded = 0f;
        private float timeSinceJump = 999f;

        // Feel improvements
        private const float CoyoteTime = 0.12f;
        private const float JumpBufferTime = 0.15f;
        private float jumpBufferTimer = 0f;

        // Collision detection tuning
        private const float SkinWidth = 0.02f;
        private const float MaxStepHeight = 0.6f;

        private int debugCounter = 0;

        private Game? game;

        public Player(Vector3 startPosition, Game? gameRef = null)
        {
            Position = startPosition;
            game = gameRef;
            Console.WriteLine($"[Player] Manual collision system initialized at {startPosition}");
        }

        public void SetGame(Game gameRef)
        {
            game = gameRef;
        }

        public Matrix4 GetViewMatrix()
        {
            Vector3 front = GetFront();
            Vector3 eyePosition = Position + new Vector3(0, eyeHeight, 0);
            return Matrix4.LookAt(eyePosition, eyePosition + front, Vector3.UnitY);
        }

        public Vector3 GetForward() => GetFront();

        public void TeleportTo(Vector3 newPosition)
        {
            Position = newPosition;
            velocity = Vector3.Zero;
            timeSinceGrounded = 999f;
            Console.WriteLine($"[Player] Teleported to {newPosition}");
        }

        public void Update(FrameEventArgs e, KeyboardState keys, MouseState mouse)
        {
            float delta = (float)e.Time;
            UpdateMouseLook(mouse);
            UpdateManualPhysics(delta, keys);
        }

        private void UpdateManualPhysics(float delta, KeyboardState keys)
        {
            delta = Math.Min(delta, 0.1f);

            // Clear collision cache periodically
            collisionCacheFrame++;
            if (collisionCacheFrame >= CacheClearInterval)
            {
                collisionCache.Clear();
                collisionCacheFrame = 0;
            }

            // Update timers
            if (isGrounded)
                timeSinceGrounded = 0f;
            else
                timeSinceGrounded += delta;

            timeSinceJump += delta;

            // Jump buffering
            if (keys.IsKeyPressed(Keys.Space))
                jumpBufferTimer = JumpBufferTime;
            jumpBufferTimer -= delta;

            // Movement input
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

            // Apply movement
            bool canJump = timeSinceGrounded < CoyoteTime && timeSinceJump > 0.2f;

            if (timeSinceGrounded < CoyoteTime * 0.5f)
            {
                ApplyFriction(delta);

                float wishSpeed = MaxGroundSpeed;
                if (keys.IsKeyDown(Keys.LeftShift))
                    wishSpeed *= SprintMultiplier;

                Accelerate(wishDir, wishSpeed, GroundAcceleration, delta);
            }
            else
            {
                float wishSpeed = MaxAirSpeed;
                Accelerate(wishDir, wishSpeed, AirAcceleration, delta);
            }

            // Jump
            if (jumpBufferTimer > 0 && canJump)
            {
                velocity.Y = JumpSpeed;
                jumpBufferTimer = 0f;
                timeSinceJump = 0f;
                timeSinceGrounded = 999f;
            }

            // Gravity
            if (!isGrounded)
            {
                velocity.Y -= Gravity * delta;
                velocity.Y = Math.Max(velocity.Y, -50f); // Terminal velocity
            }

            // Move with collision
            MoveWithCollision(delta);

            // Ground check AFTER movement
            PerformGroundCheck();
            ResolveAndFixIfStuck();
            // Debug output
            debugCounter++;
            if (debugCounter % 120 == 0)
            {
                Console.WriteLine($"[Player] Pos: {Position:F1}, Vel: {velocity:F1}, Grounded: {isGrounded}");
            }
        }

        private void PerformGroundCheck()
        {
            // Check from actual feet position
            Vector3 feetPos = Position;

            // Check if we're standing on solid ground
            float checkDist = 0.15f;
            int hits = 0;

            // Check multiple points around player base
            Vector3[] checkPoints = new Vector3[]
            {
                feetPos, // Center
                feetPos + new Vector3(playerRadius * 0.6f, 0, 0),
                feetPos + new Vector3(-playerRadius * 0.6f, 0, 0),
                feetPos + new Vector3(0, 0, playerRadius * 0.6f),
                feetPos + new Vector3(0, 0, -playerRadius * 0.6f),
            };

            foreach (var point in checkPoints)
            {
                // Check just below feet
                for (float d = 0.01f; d <= checkDist; d += 0.05f)
                {
                    Vector3 checkPos = point + new Vector3(0, -d, 0);
                    if (IsSolid(checkPos))
                    {
                        hits++;
                        break;
                    }
                }
            }

            isGrounded = hits >= 2;

            // Stop downward velocity when grounded
            if (isGrounded && velocity.Y < 0)
            {
                velocity.Y = 0;
            }
        }
        private float FindGroundBelow(Vector3 pos, float maxDrop)
        {
            // Step downward in small increments to find first collision beneath pos.
            // This is a conservative but simple approach for small drops (step heights).
            const float step = 0.01f;
            float traveled = 0f;

            for (; traveled <= maxDrop; traveled += step)
            {
                Vector3 test = pos + new Vector3(0, -traveled, 0);
                if (WouldCollide(test))
                {
                    // we hit geometry at this offset; return traveled (distance to collision)
                    return traveled;
                }
            }

            // nothing found within maxDrop
            return -1f;
        }
        private void SnapToGroundBelow()
        {
            // Start the probe a little above the player so we don't miss shallow floors.
            float probeAbove = MaxStepHeight + 0.4f;
            Vector3 probeStart = Position + new Vector3(0f, probeAbove, 0f);

            float maxDrop = probeAbove + 0.4f;
            float drop = FindGroundBelow(probeStart, maxDrop); // returns distance to collision or -1

            if (drop >= 0f)
            {
                // Place player just above collision using SkinWidth so we're not intersecting.
                Position = probeStart + new Vector3(0f, -drop + SkinWidth, 0f);
                // Make sure we don't have tiny downward velocity leftover that would cause immediate penetration
                if (velocity.Y < 0f) velocity.Y = 0f;
            }
        }
        private void ResolvePenetrationAndSnap(float maxRadius = 0.8f, bool allowVerticalLift = true)
        {
            // quick sanity: if clean, just snap down
            if (!WouldCollide(Position))
            {
                SnapToGroundBelow();
                return;
            }

            const float radialStep = 0.05f;
            const int angleStepDeg = 30; // try 12 directions per ring
            float maxUpTry = allowVerticalLift ? MaxStepHeight : 0f;

            // Rings outward in horizontal plane — prefer horizontal shifts so we don't accidentally climb.
            for (float r = radialStep; r <= maxRadius; r += radialStep)
            {
                for (int a = 0; a < 360; a += angleStepDeg)
                {
                    float rad = a * MathF.PI / 180f;
                    Vector3 offset = new Vector3(MathF.Cos(rad) * r, 0f, MathF.Sin(rad) * r);

                    // 1) Try same Y first (pure horizontal nudge)
                    if (!WouldCollide(Position + offset))
                    {
                        Position += offset;
                        SnapToGroundBelow();
                        return;
                    }

                    // 2) Try small upward lifts at this offset (to step up over a low edge)
                    if (maxUpTry > 0f)
                    {
                        for (float up = radialStep; up <= maxUpTry; up += radialStep)
                        {
                            Vector3 tryPos = Position + offset + new Vector3(0f, up + SkinWidth, 0f);
                            if (!WouldCollide(tryPos))
                            {
                                // Ensure this lift actually lands on ground within MaxStepHeight.
                                float groundY = GetGroundYAt(tryPos);
                                if (!float.IsNaN(groundY))
                                {
                                    float currentGroundY = GetGroundYAt(Position);
                                    if (!float.IsNaN(currentGroundY))
                                    {
                                        float diff = groundY - currentGroundY;
                                        // only accept small step-ups
                                        if (diff >= 0.01f && diff <= MaxStepHeight + 0.05f)
                                        {
                                            Position = new Vector3(tryPos.X, groundY + SkinWidth, tryPos.Z);
                                            if (velocity.Y < 0f) velocity.Y = 0f;
                                            SnapToGroundBelow();
                                            return;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // 3) As a last resort: small straight-up tries, but only up to MaxStepHeight and only if allowed.
            if (allowVerticalLift && Math.Abs(velocity.Y) <= 0.6f)
            {
                for (float up = radialStep; up <= MaxStepHeight; up += radialStep)
                {
                    Vector3 tryPos = Position + new Vector3(0f, up + SkinWidth, 0f);
                    if (!WouldCollide(tryPos))
                    {
                        float groundY = GetGroundYAt(tryPos);
                        float currentGroundY = GetGroundYAt(Position);
                        if (!float.IsNaN(groundY) && !float.IsNaN(currentGroundY))
                        {
                            float diff = groundY - currentGroundY;
                            if (diff >= 0.01f && diff <= MaxStepHeight + 0.05f)
                            {
                                Position = new Vector3(tryPos.X, groundY + SkinWidth, tryPos.Z);
                                if (velocity.Y < 0f) velocity.Y = 0f;
                                SnapToGroundBelow();
                                return;
                            }
                        }
                    }
                }
            }

            // If we get here, nothing worked within conservative limits — don't teleport far.
            // Log so we can tune parameters later.
            // Console.WriteLine("[Player] ResolvePenetrationAndSnap: couldn't find non-colliding spot within conservative limits.");
        }
        private void ResolveAndFixIfStuck()
        {
            // quick early-out
            if (!WouldCollide(Position))
            {
                // if we're grounded, make sure Y sits on the surface
                if (isGrounded) SnapToGroundBelow();
                return;
            }

            // If the player is moving vertically fast (jump/fall), don't aggressively lift them — try mild horizontal nudges first.
            // This prevents the resolver from helping the player "climb" while in mid-air.
            if (Math.Abs(velocity.Y) > 0.6f)
            {
                // Only allow small horizontal escape attempts when in motion
                ResolvePenetrationAndSnap(maxRadius: 0.4f, allowVerticalLift: false);
            }
            else
            {
                // If mostly still (likely wedged), allow full resolver but keep vertical lifts conservative.
                ResolvePenetrationAndSnap(maxRadius: 0.8f, allowVerticalLift: true);
            }
        }

        private void ResolvePenetrationAndSnap(float maxRadius = 0.8f)
        {
            // If we're already clean, just snap to ground to make sure our Y is correct.
            if (!WouldCollide(Position))
            {
                SnapToGroundBelow();
                return;
            }

            const float radialStep = 0.05f;
            const int angleStepDeg = 30; // try 12 directions per ring
            float maxUpTry = MaxStepHeight;

            // Try rings outward in horizontal plane, with small upward attempts at each angle.
            for (float r = radialStep; r <= maxRadius; r += radialStep)
            {
                for (int a = 0; a < 360; a += angleStepDeg)
                {
                    float rad = a * MathF.PI / 180f;
                    Vector3 offset = new Vector3(MathF.Cos(rad) * r, 0f, MathF.Sin(rad) * r);

                    // 1) Try same Y
                    if (!WouldCollide(Position + offset))
                    {
                        Position += offset;
                        SnapToGroundBelow();
                        return;
                    }

                    // 2) Try small upward lifts at this offset (to step up over an edge)
                    for (float up = radialStep; up <= maxUpTry; up += radialStep)
                    {
                        Vector3 tryPos = Position + offset + new Vector3(0f, up + SkinWidth, 0f);
                        if (!WouldCollide(tryPos))
                        {
                            Position = tryPos;
                            SnapToGroundBelow();
                            return;
                        }
                    }
                }
            }

            // 3) As a last resort, try lifting straight up until free (avoid teleporting too far)
            for (float up = radialStep; up <= maxRadius; up += radialStep)
            {
                Vector3 tryPos = Position + new Vector3(0f, up + SkinWidth, 0f);
                if (!WouldCollide(tryPos))
                {
                    Position = tryPos;
                    SnapToGroundBelow();
                    return;
                }
            }

            // If we get here, we failed to find a safe spot within the radius — log so we can tweak.
            Console.WriteLine("[Player] ResolvePenetrationAndSnap: couldn't find non-colliding spot (increase maxRadius?)");
        }

        private void MoveWithCollision(float delta)
        {
            Vector3 movement = velocity * delta;

            // Horizontal movement first
            Vector3 horizontalMove = new Vector3(movement.X, 0, movement.Z);

            if (horizontalMove.LengthSquared > 0.001f)
            {
                // Probe movement from current position
                Vector3 actualHorizontal = MoveAxis(Position, horizontalMove);

                // If blocked significantly and grounded, try a step-up (only if player was grounded)
                if (isGrounded && actualHorizontal.LengthSquared < horizontalMove.LengthSquared * 0.9f)
                {
                    if (TryStepUp(horizontalMove, out Vector3 stepMove))
                    {
                        // Apply the step move (which contains the upward + forward + drop to ground)
                        Position += stepMove;

                        // landed, stop vertical velocity
                        velocity.Y = 0f;

                        // we've applied horizontal move already
                        horizontalMove = Vector3.Zero;
                    }
                    else
                    {
                        // no step possible, apply best-effort horizontal movement
                        Position += actualHorizontal;
                    }
                }
                else
                {
                    Position += actualHorizontal;
                }

                // Reduce horizontal velocity if severely blocked (keep momentum otherwise)
                if (actualHorizontal.LengthSquared < horizontalMove.LengthSquared * 0.5f)
                {
                    float blockAmount = 1f - (actualHorizontal.Length / Math.Max(horizontalMove.Length, 1e-6f));
                    velocity.X *= (1f - blockAmount);
                    velocity.Z *= (1f - blockAmount);
                }
            }

            // Vertical movement second
            Vector3 verticalMove = new Vector3(0, movement.Y, 0);
            Vector3 actualVertical = MoveAxis(Position, verticalMove);
            Position += actualVertical;

            // If we hit something vertically, stop vertical velocity
            if (Math.Abs(actualVertical.Y) < Math.Abs(verticalMove.Y) * 0.9f)
            {
                velocity.Y = 0;
            }
        }


        private float GetGroundYAt(Vector3 pos, float probeAbove = -1f, float maxDrop = -1f)
        {
            // Defaults tuned around MaxStepHeight
            if (probeAbove <= 0f) probeAbove = MaxStepHeight + 0.5f;
            if (maxDrop <= 0f) maxDrop = probeAbove + 0.5f;

            Vector3 probeStart = pos + new Vector3(0f, probeAbove, 0f);
            float drop = FindGroundBelow(probeStart, maxDrop);
            if (drop < 0f) return float.NaN;
            return probeStart.Y - drop;
        }

        private bool TryStepUp(Vector3 horizontalMove, out Vector3 finalMove)
        {
            finalMove = Vector3.Zero;

            if (horizontalMove.LengthSquared < 1e-8f) return false;

            // Get current ground height (if any). If we have no ground under us, don't step.
            float currentGroundY = GetGroundYAt(Position);
            if (float.IsNaN(currentGroundY))
                return false;

            const float probeStep = 0.05f;
            const float forwardPassThreshold = 0.85f; // fraction of forward movement we require
            const float minStepToConsider = 0.05f;     // ignore micro-steps

            for (float stepHeight = probeStep; stepHeight <= MaxStepHeight + 0.0001f; stepHeight += probeStep)
            {
                // position raised to attempt stepping (a tiny SkinWidth lift so we truly clear the obstruction)
                Vector3 upPos = Position + new Vector3(0f, stepHeight + SkinWidth, 0f);

                // must be free at raised position
                if (WouldCollide(upPos))
                    continue;

                // attempt to advance forward at that raised position
                Vector3 forwardActual = MoveAxis(upPos, horizontalMove);

                // require a large fraction of the requested forward displacement to avoid sneaking up walls
                if (forwardActual.LengthSquared < horizontalMove.LengthSquared * forwardPassThreshold)
                    continue;

                // candidate location after moving
                Vector3 candidate = upPos + forwardActual;

                // find real ground height under candidate
                float candidateGroundY = GetGroundYAt(candidate);
                if (float.IsNaN(candidateGroundY))
                    continue; // no ground under candidate -> probably a ledge or wall

                // height difference between candidate ground and our current ground
                float heightDiff = candidateGroundY - currentGroundY;

                // we only want to step up onto ground that is slightly higher,
                // and within the MaxStepHeight range (ignore tiny bumps too)
                if (heightDiff < minStepToConsider || heightDiff > MaxStepHeight + 0.05f)
                    continue;

                // Construct final position sitting just above candidate ground (using SkinWidth)
                Vector3 finalPos = new Vector3(candidate.X, candidateGroundY + SkinWidth, candidate.Z);

                // sanity: ensure final position is free
                if (WouldCollide(finalPos))
                    continue;

                // success: return the delta (will be applied to Position by caller)
                finalMove = finalPos - Position;
                return true;
            }

            return false;
        }


        private Vector3 MoveAxis(Vector3 startPos, Vector3 movement)
        {
            if (movement.LengthSquared <= 1e-8f) return Vector3.Zero;

            // Quick test: if target position is free, take it.
            Vector3 targetPos = startPos + movement;
            if (!WouldCollide(targetPos))
                return movement;

            // Binary search for maximum safe fraction of movement
            float low = 0f;
            float high = 1f;
            float safe = 0f;

            for (int i = 0; i < 10; i++) // 10 iterations gives good precision
            {
                float mid = (low + high) * 0.5f;
                Vector3 testPos = startPos + movement * mid;
                if (WouldCollide(testPos))
                {
                    high = mid;
                }
                else
                {
                    safe = mid;
                    low = mid;
                }
            }

            return movement * safe;
        }

        private bool WouldCollide(Vector3 position)
        {
            // Check bottom capsule cap
            if (CheckSphereCollision(position + new Vector3(0, playerRadius, 0), playerRadius - SkinWidth))
                return true;

            // Check top capsule cap
            if (CheckSphereCollision(position + new Vector3(0, playerHeight - playerRadius, 0), playerRadius - SkinWidth))
                return true;

            // Check middle cylinder with more samples
            for (float h = playerRadius + 0.2f; h < playerHeight - playerRadius; h += 0.2f)
            {
                Vector3 center = position + new Vector3(0, h, 0);

                // Check 8 points around perimeter
                for (int angle = 0; angle < 360; angle += 45)
                {
                    float rad = angle * MathF.PI / 180f;
                    float r = playerRadius - SkinWidth;
                    Vector3 checkPos = center + new Vector3(
                        MathF.Cos(rad) * r,
                        0,
                        MathF.Sin(rad) * r
                    );

                    if (IsSolid(checkPos))
                        return true;
                }
            }

            return false;
        }

        private bool CheckSphereCollision(Vector3 center, float radius)
        {
            int checks = (int)Math.Ceiling(radius) + 1;

            for (int x = -checks; x <= checks; x++)
            {
                for (int y = -checks; y <= checks; y++)
                {
                    for (int z = -checks; z <= checks; z++)
                    {
                        Vector3 offset = new Vector3(x * 0.25f, y * 0.25f, z * 0.25f);
                        if (offset.LengthSquared <= radius * radius)
                        {
                            Vector3 checkPos = center + offset;
                            if (IsSolid(checkPos))
                                return true;
                        }
                    }
                }
            }

            return false;
        }

        // Collision cache
        private readonly Dictionary<(int, int, int), bool> collisionCache = new();
        private int collisionCacheFrame = 0;
        private const int CacheClearInterval = 30; // Clear cache every 30 frames

        private bool IsSolid(Vector3 position)
        {
            if (game == null)
            {
                // Fallback to density sampling if no game reference
                return SampleDensityAt(position) > 0.5f;
            }

            try
            {
                // Cache key based on position rounded to 0.25 block precision
                int cacheX = (int)(position.X * 4);
                int cacheY = (int)(position.Y * 4);
                int cacheZ = (int)(position.Z * 4);
                var cacheKey = (cacheX, cacheY, cacheZ);

                // Check cache first
                if (collisionCache.TryGetValue(cacheKey, out bool cached))
                    return cached;

                // Only check the chunk containing this position
                int chunkX = (int)Math.Floor(position.X / ClientConfig.CHUNK_SIZE);
                int chunkY = (int)Math.Floor(position.Y / ClientConfig.CHUNK_SIZE_Y);
                int chunkZ = (int)Math.Floor(position.Z / ClientConfig.CHUNK_SIZE);

                bool result = CheckMeshCollisionFast(chunkX, chunkY, chunkZ, position);

                // Cache the result
                collisionCache[cacheKey] = result;

                return result;
            }
            catch
            {
                return false;
            }
        }

        private bool CheckMeshCollisionFast(int cx, int cy, int cz, Vector3 point)
        {
            var meshData = game?.Renderer.GetMeshData(cx, cy, cz);
            if (meshData == null || meshData.Length < 24)
            {
                // Fallback to density if mesh not loaded yet
                return SampleDensityAt(point) > 0.5f;
            }

            // Broad phase: Only check triangles in a small radius
            const float checkRadius = 2.0f;

            // Check every Nth triangle for performance (trade accuracy for speed)
            const int stride = 24; // Check every triangle

            for (int i = 0; i < meshData.Length; i += stride)
            {
                // Extract first vertex for quick distance check
                float vx = meshData[i + 0];
                float vy = meshData[i + 1];
                float vz = meshData[i + 2];

                // Quick distance check
                float dx = vx - point.X;
                float dy = vy - point.Y;
                float dz = vz - point.Z;
                float distSq = dx * dx + dy * dy + dz * dz;

                if (distSq > checkRadius * checkRadius)
                    continue;

                // Extract all three vertices
                Vector3 v0 = new Vector3(meshData[i + 0], meshData[i + 1], meshData[i + 2]);
                Vector3 v1 = new Vector3(meshData[i + 8], meshData[i + 9], meshData[i + 10]);
                Vector3 v2 = new Vector3(meshData[i + 16], meshData[i + 17], meshData[i + 18]);

                // Simplified point-in-triangle test
                if (PointNearTriangleFast(point, v0, v1, v2))
                    return true;
            }

            return false;
        }

        private bool PointNearTriangleFast(Vector3 point, Vector3 v0, Vector3 v1, Vector3 v2)
        {
            // Calculate triangle normal
            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            Vector3 normal = Vector3.Cross(edge1, edge2);
            float normalLen = normal.Length;

            if (normalLen < 0.0001f) return false;

            normal /= normalLen;

            // Distance to plane
            Vector3 toPoint = point - v0;
            float distanceToPlane = Vector3.Dot(toPoint, normal);

            // Check if point is within thickness threshold
            const float thickness = 0.4f;
            if (Math.Abs(distanceToPlane) > thickness)
                return false;

            // Simplified inside-triangle check using sign of cross products
            Vector3 edge0 = v1 - v0;
            Vector3 edge1b = v2 - v1;
            Vector3 edge2b = v0 - v2;

            Vector3 vp0 = point - v0;
            Vector3 vp1 = point - v1;
            Vector3 vp2 = point - v2;

            // Check if point is on the same side of all edges
            float c0 = Vector3.Dot(normal, Vector3.Cross(edge0, vp0));
            float c1 = Vector3.Dot(normal, Vector3.Cross(edge1b, vp1));
            float c2 = Vector3.Dot(normal, Vector3.Cross(edge2b, vp2));

            // All same sign = inside
            return (c0 >= -0.01f && c1 >= -0.01f && c2 >= -0.01f) ||
                   (c0 <= 0.01f && c1 <= 0.01f && c2 <= 0.01f);
        }

        private float SampleDensityAt(Vector3 position)
        {
            float fx = position.X;
            float fy = position.Y;
            float fz = position.Z;

            int x0 = (int)Math.Floor(fx);
            int y0 = (int)Math.Floor(fy);
            int z0 = (int)Math.Floor(fz);

            int x1 = x0 + 1;
            int y1 = y0 + 1;
            int z1 = z0 + 1;

            float tx = fx - x0;
            float ty = fy - y0;
            float tz = fz - z0;

            // Sample density at 8 corners
            float d000 = WorldGen.SampleDensity(x0, y0, z0);
            float d100 = WorldGen.SampleDensity(x1, y0, z0);
            float d010 = WorldGen.SampleDensity(x0, y1, z0);
            float d110 = WorldGen.SampleDensity(x1, y1, z0);
            float d001 = WorldGen.SampleDensity(x0, y0, z1);
            float d101 = WorldGen.SampleDensity(x1, y0, z1);
            float d011 = WorldGen.SampleDensity(x0, y1, z1);
            float d111 = WorldGen.SampleDensity(x1, y1, z1);

            // Trilinear interpolation
            float d00 = d000 * (1 - tx) + d100 * tx;
            float d01 = d001 * (1 - tx) + d101 * tx;
            float d10 = d010 * (1 - tx) + d110 * tx;
            float d11 = d011 * (1 - tx) + d111 * tx;

            float d0 = d00 * (1 - ty) + d10 * ty;
            float d1 = d01 * (1 - ty) + d11 * ty;

            return d0 * (1 - tz) + d1 * tz;
        }

        private void ApplyFriction(float delta)
        {
            float speed = new Vector3(velocity.X, 0, velocity.Z).Length;
            if (speed < 0.1f)
            {
                velocity.X = 0;
                velocity.Z = 0;
                return;
            }

            float control = speed < StopSpeed ? StopSpeed : speed;
            float drop = control * Friction * delta;
            float newSpeed = Math.Max(speed - drop, 0f);

            if (speed > 0.01f)
            {
                float scale = newSpeed / speed;
                velocity.X *= scale;
                velocity.Z *= scale;
            }
        }

        private void Accelerate(Vector3 wishDir, float wishSpeed, float accel, float delta)
        {
            if (wishDir.LengthSquared < 0.01f) return;

            Vector3 horizVel = new Vector3(velocity.X, 0, velocity.Z);
            float currentSpeed = Vector3.Dot(horizVel, wishDir);
            float addSpeed = wishSpeed - currentSpeed;

            if (addSpeed <= 0) return;

            float accelSpeed = Math.Min(accel * delta * wishSpeed, addSpeed);

            velocity.X += wishDir.X * accelSpeed;
            velocity.Z += wishDir.Z * accelSpeed;
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
            Console.WriteLine("[Player] Manual collision system cleaned up");
        }

        public BepuPhysics.BodyHandle GetBodyHandle()
        {
            return default;
        }
    }
}
