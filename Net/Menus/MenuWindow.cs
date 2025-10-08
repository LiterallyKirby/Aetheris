using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Aetheris
{
    public enum MenuResult
    {
        None,
        SinglePlayer,
        Multiplayer,
        Exit
    }

    public class MenuWindow : GameWindow
    {
        public MenuResult Result { get; private set; } = MenuResult.None;

        private int shaderProgram;
        private int vao;
        private int vbo;
        
        private List<MenuButton> buttons = new List<MenuButton>();
        private int hoveredButtonIndex = -1;
        
        private float time = 0f;

        public MenuWindow()
            : base(GameWindowSettings.Default, new NativeWindowSettings()
            {
                ClientSize = new Vector2i(1920, 1080),
                Title = "Aetheris - Main Menu"
            })
        {
        }

        protected override void OnLoad()
        {
            base.OnLoad();
            
            GL.ClearColor(0.1f, 0.1f, 0.15f, 1.0f);
            
            // Create simple shader for rendering colored quads
            string vertexShader = @"
                #version 330 core
                layout(location = 0) in vec2 aPosition;
                layout(location = 1) in vec4 aColor;
                
                out vec4 fragColor;
                
                uniform mat4 projection;
                
                void main()
                {
                    gl_Position = projection * vec4(aPosition, 0.0, 1.0);
                    fragColor = aColor;
                }
            ";
            
            string fragmentShader = @"
                #version 330 core
                in vec4 fragColor;
                out vec4 finalColor;
                
                void main()
                {
                    finalColor = fragColor;
                }
            ";
            
            int vertShader = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vertShader, vertexShader);
            GL.CompileShader(vertShader);
            
            int fragShader = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fragShader, fragmentShader);
            GL.CompileShader(fragShader);
            
            shaderProgram = GL.CreateProgram();
            GL.AttachShader(shaderProgram, vertShader);
            GL.AttachShader(shaderProgram, fragShader);
            GL.LinkProgram(shaderProgram);
            
            GL.DeleteShader(vertShader);
            GL.DeleteShader(fragShader);
            
            // Setup VAO/VBO
            vao = GL.GenVertexArray();
            vbo = GL.GenBuffer();
            
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            
            GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 6 * sizeof(float), 2 * sizeof(float));
            GL.EnableVertexAttribArray(1);
            
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            
            // Create menu buttons (centered on screen)
            float centerX = ClientSize.X / 2f;
            float startY = ClientSize.Y / 2f - 100f;
            float buttonWidth = 300f;
            float buttonHeight = 60f;
            float spacing = 80f;
            
            buttons.Add(new MenuButton("Single Player", centerX, startY, buttonWidth, buttonHeight, MenuResult.SinglePlayer));
            buttons.Add(new MenuButton("Multiplayer", centerX, startY + spacing, buttonWidth, buttonHeight, MenuResult.Multiplayer));
            buttons.Add(new MenuButton("Exit", centerX, startY + spacing * 2, buttonWidth, buttonHeight, MenuResult.Exit));
            
            Console.WriteLine("[MenuWindow] Menu initialized with {0} buttons", buttons.Count);
        }

        protected override void OnUpdateFrame(FrameEventArgs e)
        {
            base.OnUpdateFrame(e);
            
            time += (float)e.Time;
            
            // Check mouse position against buttons
            var mousePos = MouseState.Position;
            hoveredButtonIndex = -1;
            
            for (int i = 0; i < buttons.Count; i++)
            {
                if (buttons[i].Contains(mousePos.X, mousePos.Y))
                {
                    hoveredButtonIndex = i;
                    break;
                }
            }
            
            // Handle mouse clicks
            if (MouseState.IsButtonPressed(MouseButton.Left) && hoveredButtonIndex >= 0)
            {
                Result = buttons[hoveredButtonIndex].Action;
                Console.WriteLine("[MenuWindow] Selected: {0}", Result);
                Close();
            }
            
            // ESC to exit
            if (KeyboardState.IsKeyPressed(Keys.Escape))
            {
                Result = MenuResult.Exit;
                Close();
            }
        }

        protected override void OnRenderFrame(FrameEventArgs e)
        {
            base.OnRenderFrame(e);
            
            GL.Clear(ClearBufferMask.ColorBufferBit);
            
            GL.UseProgram(shaderProgram);
            
            // Set projection matrix (orthographic for 2D)
            var projection = Matrix4.CreateOrthographicOffCenter(0, ClientSize.X, ClientSize.Y, 0, -1, 1);
            int projLoc = GL.GetUniformLocation(shaderProgram, "projection");
            GL.UniformMatrix4(projLoc, false, ref projection);
            
            // Render background gradient (animated)
            RenderBackground();
            
            // Render title
            RenderTitle();
            
            // Render buttons
            for (int i = 0; i < buttons.Count; i++)
            {
                bool isHovered = (i == hoveredButtonIndex);
                RenderButton(buttons[i], isHovered);
            }
            
            SwapBuffers();
        }

        private void RenderBackground()
        {
            // Create animated gradient background
            float wave = MathF.Sin(time * 0.5f) * 0.5f + 0.5f;
            
            float[] vertices = new float[]
            {
                // Positions          // Colors (RGBA)
                0, 0,                 0.1f, 0.1f, 0.15f + wave * 0.05f, 1.0f,
                ClientSize.X, 0,      0.15f + wave * 0.05f, 0.1f, 0.2f, 1.0f,
                ClientSize.X, ClientSize.Y, 0.1f, 0.15f + wave * 0.05f, 0.25f, 1.0f,
                
                0, 0,                 0.1f, 0.1f, 0.15f + wave * 0.05f, 1.0f,
                ClientSize.X, ClientSize.Y, 0.1f, 0.15f + wave * 0.05f, 0.25f, 1.0f,
                0, ClientSize.Y,      0.15f, 0.1f, 0.2f + wave * 0.05f, 1.0f,
            };
            
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }

        private void RenderTitle()
        {
            // Render "AETHERIS" title box
            float titleWidth = 400f;
            float titleHeight = 80f;
            float titleX = ClientSize.X / 2f - titleWidth / 2f;
            float titleY = 100f;
            
            float pulse = MathF.Sin(time * 2f) * 0.1f + 0.9f;
            
            float[] vertices = new float[]
            {
                // Positions                                    // Colors (RGBA)
                titleX, titleY,                                 0.2f * pulse, 0.3f * pulse, 0.5f * pulse, 0.9f,
                titleX + titleWidth, titleY,                    0.3f * pulse, 0.4f * pulse, 0.6f * pulse, 0.9f,
                titleX + titleWidth, titleY + titleHeight,      0.3f * pulse, 0.4f * pulse, 0.6f * pulse, 0.9f,
                
                titleX, titleY,                                 0.2f * pulse, 0.3f * pulse, 0.5f * pulse, 0.9f,
                titleX + titleWidth, titleY + titleHeight,      0.3f * pulse, 0.4f * pulse, 0.6f * pulse, 0.9f,
                titleX, titleY + titleHeight,                   0.2f * pulse, 0.3f * pulse, 0.5f * pulse, 0.9f,
            };
            
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }

        private void RenderButton(MenuButton button, bool isHovered)
        {
            float x = button.X - button.Width / 2f;
            float y = button.Y;
            float w = button.Width;
            float h = button.Height;
            
            // Button color (brighter when hovered)
            float r = isHovered ? 0.3f : 0.2f;
            float g = isHovered ? 0.5f : 0.3f;
            float b = isHovered ? 0.7f : 0.4f;
            float a = isHovered ? 1.0f : 0.8f;
            
            // Add subtle animation
            if (isHovered)
            {
                float pulse = MathF.Sin(time * 5f) * 0.05f;
                r += pulse;
                g += pulse;
                b += pulse;
            }
            
            float[] vertices = new float[]
            {
                // Positions      // Colors (RGBA)
                x, y,             r, g, b, a,
                x + w, y,         r, g, b, a,
                x + w, y + h,     r * 0.8f, g * 0.8f, b * 0.8f, a,
                
                x, y,             r, g, b, a,
                x + w, y + h,     r * 0.8f, g * 0.8f, b * 0.8f, a,
                x, y + h,         r * 0.8f, g * 0.8f, b * 0.8f, a,
            };
            
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
            
            // Render border
            if (isHovered)
            {
                float borderWidth = 3f;
                RenderButtonBorder(x, y, w, h, borderWidth);
            }
        }

        private void RenderButtonBorder(float x, float y, float w, float h, float thickness)
        {
            float r = 0.5f, g = 0.7f, b = 1.0f, a = 1.0f;
            
            // Top
            RenderRect(x, y, w, thickness, r, g, b, a);
            // Bottom
            RenderRect(x, y + h - thickness, w, thickness, r, g, b, a);
            // Left
            RenderRect(x, y, thickness, h, r, g, b, a);
            // Right
            RenderRect(x + w - thickness, y, thickness, h, r, g, b, a);
        }

        private void RenderRect(float x, float y, float w, float h, float r, float g, float b, float a)
        {
            float[] vertices = new float[]
            {
                x, y,         r, g, b, a,
                x + w, y,     r, g, b, a,
                x + w, y + h, r, g, b, a,
                
                x, y,         r, g, b, a,
                x + w, y + h, r, g, b, a,
                x, y + h,     r, g, b, a,
            };
            
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }

        protected override void OnUnload()
        {
            base.OnUnload();
            
            GL.DeleteVertexArray(vao);
            GL.DeleteBuffer(vbo);
            GL.DeleteProgram(shaderProgram);
        }

        private class MenuButton
        {
            public string Label { get; }
            public float X { get; }
            public float Y { get; }
            public float Width { get; }
            public float Height { get; }
            public MenuResult Action { get; }

            public MenuButton(string label, float x, float y, float width, float height, MenuResult action)
            {
                Label = label;
                X = x;
                Y = y;
                Width = width;
                Height = height;
                Action = action;
            }

            public bool Contains(float mouseX, float mouseY)
            {
                float left = X - Width / 2f;
                float right = X + Width / 2f;
                float top = Y;
                float bottom = Y + Height;
                
                return mouseX >= left && mouseX <= right && mouseY >= top && mouseY <= bottom;
            }
        }
    }
}
