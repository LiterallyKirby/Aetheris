using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using OpenTK.Windowing.Common;
using System;

namespace Aetheris
{
    public class Player
    {

        public Player(Vector3 startPosition)
        {
            Position = startPosition;
        }
        public Vector3 Position { get; private set; } = new Vector3(0, 10, 20);
        public float Pitch { get; private set; } = 0f;  // up/down
        public float Yaw { get; private set; } = -90f;  // left/right

        private float moveSpeed = 10f;
        private float mouseSensitivity = 0.2f;

        private Vector2 lastMousePos;
        private bool firstMouse = true;

        public Matrix4 GetViewMatrix()
        {
            Vector3 front = GetFront();
            return Matrix4.LookAt(Position, Position + front, Vector3.UnitY);
        }

        public void Update(FrameEventArgs e, KeyboardState keys, MouseState mouse)
        {
            float delta = (float)e.Time;
            float velocity = moveSpeed * delta;

            Vector3 front = GetFront();
            Vector3 right = Vector3.Normalize(Vector3.Cross(front, Vector3.UnitY));
            Vector3 up = Vector3.UnitY;

            // Movement
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

            // Mouse look
            if (firstMouse)
            {
                lastMousePos = new Vector2(mouse.X, mouse.Y);
                firstMouse = false;
            }

            float dx = mouse.X - lastMousePos.X;
            float dy = lastMousePos.Y - mouse.Y; // reversed Y
            lastMousePos = new Vector2(mouse.X, mouse.Y);

            dx *= mouseSensitivity;
            dy *= mouseSensitivity;

            Yaw += dx;
            Pitch += dy;

            // clamp pitch
            Pitch = Math.Clamp(Pitch, -89f, 89f);
        }

        private Vector3 GetFront()
        {
            float yawRad = MathHelper.DegreesToRadians(Yaw);
            float pitchRad = MathHelper.DegreesToRadians(Pitch);

            Vector3 front;
            front.X = MathF.Cos(pitchRad) * MathF.Cos(yawRad);
            front.Y = MathF.Sin(pitchRad);
            front.Z = MathF.Cos(pitchRad) * MathF.Sin(yawRad);
            return Vector3.Normalize(front);
        }


        public Vector3 GetPlayersChunk()
        {
            return new Vector3(
                (int)Math.Floor(Position.X / Config.CHUNK_SIZE),
                (int)Math.Floor(Position.Y / Config.CHUNK_SIZE_Y),
                (int)Math.Floor(Position.Z / Config.CHUNK_SIZE)
            );
        }

    }
}

