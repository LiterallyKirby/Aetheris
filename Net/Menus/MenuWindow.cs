using System;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Aetheris.UI;

namespace Aetheris
{
    public enum MenuResult
    {
        None,
        SinglePlayer,
        Multiplayer,
        Settings,
        Exit
    }
    public class MenuWindow : GameWindow
    {
        public MenuResult Result { get; private set; } = MenuResult.None;

        private int shaderProgram;
        private int vao;
        private int vbo;
        private UIManager? uiManager;
        private FontRenderer? fontRenderer;

        private string[] Splashes = {
    "I stole this from Minecraft!",
    "I'm out of step!",
    "Stop! You've violated the law!",
    "I've got a straight edge!",
    "I'm not trying to ruin your fun!",
    "Poyo!",
    "I'm Literally Kirby!",
    "If you see this ur gay.",
    "I'm running out of ideas",
    "Born to rage, bound by mana.",
    "Raise your horns, not the rent!",
    "This realm runs on pure caffeine.",
    "Everyone that plays chaotic neutral is edgy.",
    "Fuck elves. All my homies hate elves.",
    "Your mom failed her perception check.",
    "Bro got ginug'd.",
"Rat Republic!",
    "My armor’s held together with safety pins.",
    "Counterspell this!",
};

        private float time = 0f;

        // UI Elements
        private Label? titleLabel;
        private Button? singlePlayerBtn;
        private Button? multiplayerBtn;
        private Button? settingsBtn;
        private Button? exitBtn;

    Random random = new Random(); 
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

            // Darker background for dark fantasy
            GL.ClearColor(0.02f, 0.03f, 0.08f, 1.0f);

            // Create shader for rendering
            CreateShader();

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

            // Initialize UI Manager
            uiManager = new UIManager(this, shaderProgram, vao, vbo);

            try
            {
                fontRenderer = new FontRenderer("assets/font.ttf", 32);
                uiManager.TextRenderer = fontRenderer;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MenuWindow] Warning: Could not load font: {ex.Message}");
                Console.WriteLine("[MenuWindow] Menu will run without text rendering");
            }

            // Create UI elements
            CreateUI();

            Console.WriteLine("[MenuWindow] Menu initialized");
        }

        private void CreateShader()
        {
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
        }

        private void CreateUI()
        {
            if (uiManager == null) return;

            float centerX = ClientSize.X / 2f;
            float centerY = ClientSize.Y / 2f;

            // Title - Icy blue glow
            titleLabel = new Label("AETHERIS")
            {
                Position = new Vector2(centerX - 150, 120),
                Size = new Vector2(300, 60),
                Scale = 2.5f,
                Color = new Vector4(0.4f, 0.7f, 1.0f, 1f),  // Bright icy blue
                TextAlign = TextAlign.Center
            };
            uiManager.AddElement(titleLabel);

            // Subtitle - Ethereal blue
        int roll = random.Next(0, Splashes.Length); // Generates a number between 1 and 6
            var subtitleLabel = new Label(Splashes[roll])
            {
                Position = new Vector2(centerX - 120, 190),
                Size = new Vector2(240, 30),
                Scale = 1.0f,
                Color = new Vector4(0.3f, 0.5f, 0.8f, 0.7f),  // Muted ethereal blue
                TextAlign = TextAlign.Center
            };
            uiManager.AddElement(subtitleLabel);

            // Buttons - Dark blue/purple fantasy theme
            float buttonWidth = 320f;
            float buttonHeight = 65f;
            float startY = centerY - 50f;
            float spacing = 85f;

            singlePlayerBtn = new Button("Single Player")
            {
                Position = new Vector2(centerX - buttonWidth / 2f, startY),
                Size = new Vector2(buttonWidth, buttonHeight),
                LabelScale = 1.2f,
                CornerRadius = 8f,
                Color = new Vector4(0.08f, 0.12f, 0.25f, 0.85f),          // Dark blue base
                HoverColor = new Vector4(0.15f, 0.25f, 0.45f, 0.95f),     // Brighter blue hover
                PressedColor = new Vector4(0.05f, 0.08f, 0.18f, 0.95f),   // Darker pressed
                TextColor = new Vector4(0.6f, 0.8f, 1.0f, 1f),            // Ice blue text
                BorderColor = new Vector4(0.2f, 0.4f, 0.7f, 0.8f)         // Blue border
            };
            singlePlayerBtn.OnPressed = () =>
            {
                Result = MenuResult.SinglePlayer;
                Close();
            };
            uiManager.AddElement(singlePlayerBtn);

            multiplayerBtn = new Button("Multiplayer")
            {
                Position = new Vector2(centerX - buttonWidth / 2f, startY + spacing),
                Size = new Vector2(buttonWidth, buttonHeight),
                LabelScale = 1.2f,
                CornerRadius = 8f,
                Color = new Vector4(0.08f, 0.12f, 0.25f, 0.85f),
                HoverColor = new Vector4(0.15f, 0.25f, 0.45f, 0.95f),
                PressedColor = new Vector4(0.05f, 0.08f, 0.18f, 0.95f),
                TextColor = new Vector4(0.6f, 0.8f, 1.0f, 1f),
                BorderColor = new Vector4(0.2f, 0.4f, 0.7f, 0.8f)
            };
            multiplayerBtn.OnPressed = () =>
            {
                Result = MenuResult.Multiplayer;
                Close();
            };
            uiManager.AddElement(multiplayerBtn);

            settingsBtn = new Button("Settings")
            {
                Position = new Vector2(centerX - buttonWidth / 2f, startY + spacing * 2),
                Size = new Vector2(buttonWidth, buttonHeight),
                LabelScale = 1.2f,
                CornerRadius = 8f,
                Color = new Vector4(0.08f, 0.12f, 0.25f, 0.85f),
                HoverColor = new Vector4(0.15f, 0.25f, 0.45f, 0.95f),
                PressedColor = new Vector4(0.05f, 0.08f, 0.18f, 0.95f),
                TextColor = new Vector4(0.6f, 0.8f, 1.0f, 1f),
                BorderColor = new Vector4(0.2f, 0.4f, 0.7f, 0.8f)
            };
            settingsBtn.OnPressed = () =>
            {
                Result = MenuResult.Settings;
                Console.WriteLine("[MenuWindow] Settings not yet implemented");
            };
            uiManager.AddElement(settingsBtn);

            exitBtn = new Button("Exit")
            {
                Position = new Vector2(centerX - buttonWidth / 2f, startY + spacing * 3),
                Size = new Vector2(buttonWidth, buttonHeight),
                LabelScale = 1.2f,
                CornerRadius = 8f,
                Color = new Vector4(0.12f, 0.05f, 0.08f, 0.85f),          // Dark red-purple
                HoverColor = new Vector4(0.25f, 0.08f, 0.12f, 0.95f),     // Blood red hover
                PressedColor = new Vector4(0.08f, 0.03f, 0.05f, 0.95f),   // Darker pressed
                TextColor = new Vector4(0.9f, 0.5f, 0.6f, 1f),            // Light red text
                BorderColor = new Vector4(0.5f, 0.2f, 0.3f, 0.8f)         // Dark red border
            };
            exitBtn.OnPressed = () =>
            {
                Result = MenuResult.Exit;
                Close();
            };
            uiManager.AddElement(exitBtn);

            // Footer text - Dim blue
            var footerLabel = new Label("Press ESC to exit")
            {
                Position = new Vector2(centerX - 80, ClientSize.Y - 60),
                Size = new Vector2(160, 30),
                Scale = 0.8f,
                Color = new Vector4(0.2f, 0.3f, 0.5f, 0.5f),
                TextAlign = TextAlign.Center
            };
            uiManager.AddElement(footerLabel);

            // Version label - Very dim
            var versionLabel = new Label("v0.1.0 Alpha")
            {
                Position = new Vector2(20, ClientSize.Y - 40),
                Size = new Vector2(150, 30),
                Scale = 0.7f,
                Color = new Vector4(0.15f, 0.2f, 0.3f, 0.4f)
            };
            uiManager.AddElement(versionLabel);
        }

        protected override void OnResize(ResizeEventArgs e)
        {
            base.OnResize(e);
            GL.Viewport(0, 0, e.Width, e.Height);

            // Recreate UI with new dimensions
            if (uiManager != null)
            {
                uiManager.Clear();
                CreateUI();
            }
        }

        protected override void OnUpdateFrame(FrameEventArgs e)
        {
            base.OnUpdateFrame(e);

            time += (float)e.Time;

            // Update UI
            uiManager?.Update(MouseState, KeyboardState, (float)e.Time);

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

            // Set projection matrix
            var projection = Matrix4.CreateOrthographicOffCenter(0, ClientSize.X, ClientSize.Y, 0, -1, 1);

            // Render animated background
            RenderBackground(projection);

            // Render decorative elements
            RenderDecorations(projection);

            // Render UI
            uiManager?.Render(projection);

            SwapBuffers();
        }

        private void RenderBackground(Matrix4 projection)
        {
            GL.UseProgram(shaderProgram);
            int projLoc = GL.GetUniformLocation(shaderProgram, "projection");
            GL.UniformMatrix4(projLoc, false, ref projection);

            // Slower, more ominous wave
            float wave = MathF.Sin(time * 0.3f) * 0.5f + 0.5f;

            // Dark blue/purple gradient background
            float[] vertices = new float[]
            {
                // Top: Very dark blue-black
                0, 0,                 0.01f, 0.02f + wave * 0.01f, 0.06f, 1.0f,
                ClientSize.X, 0,      0.02f, 0.03f, 0.08f + wave * 0.01f, 1.0f,
                
                // Bottom: Deep dark blue with purple tint
                ClientSize.X, ClientSize.Y, 0.03f + wave * 0.01f, 0.04f, 0.12f, 1.0f,

                0, 0,                 0.01f, 0.02f + wave * 0.01f, 0.06f, 1.0f,
                ClientSize.X, ClientSize.Y, 0.03f + wave * 0.01f, 0.04f, 0.12f, 1.0f,
                0, ClientSize.Y,      0.02f, 0.03f, 0.10f + wave * 0.01f, 1.0f,
            };

            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }

        private void RenderDecorations(Matrix4 projection)
        {
            GL.UseProgram(shaderProgram);
            int projLoc = GL.GetUniformLocation(shaderProgram, "projection");
            GL.UniformMatrix4(projLoc, false, ref projection);

            // Mystical floating particles - slower, more ethereal
            for (int i = 0; i < 25; i++)
            {
                float offsetX = MathF.Sin(time * 0.15f + i * 0.8f) * 60f;
                float offsetY = MathF.Cos(time * 0.1f + i * 0.6f) * 40f;
                float x = 80f + i * 75f + offsetX;
                float y = 60f + (i % 4) * 270f + offsetY;

                // Slower pulsing
                float size = 2f + MathF.Sin(time * 1.2f + i * 0.4f) * 1.2f;

                // Blue/cyan mystical particles
                float brightness = 0.15f + MathF.Sin(time * 1.5f + i * 0.5f) * 0.1f;
                float blue = 0.3f + MathF.Sin(time * 1.8f + i * 0.3f) * 0.15f;

                RenderRect(x, y, size, size,
                    new Vector4(brightness * 0.5f, brightness * 0.8f, blue, 0.7f),
                    projection);
            }

            // Larger, dimmer ambient particles
            for (int i = 0; i < 12; i++)
            {
                float offsetX = MathF.Sin(time * 0.08f + i * 1.2f) * 100f;
                float offsetY = MathF.Cos(time * 0.06f + i * 0.9f) * 80f;
                float x = 150f + i * 160f + offsetX;
                float y = 100f + (i % 3) * 350f + offsetY;

                float size = 4f + MathF.Sin(time * 0.8f + i * 0.6f) * 2f;
                float alpha = 0.15f + MathF.Sin(time * 1.2f + i * 0.4f) * 0.1f;

                // Very dim blue glow
                RenderRect(x, y, size, size,
                    new Vector4(0.1f, 0.15f, 0.35f, alpha),
                    projection);
            }

            // Add some mystical wisps (elongated particles)
            for (int i = 0; i < 8; i++)
            {
                float offsetY = MathF.Sin(time * 0.12f + i * 0.7f) * 120f;
                float x = 100f + i * 240f;
                float y = -50f + offsetY;

                float height = 60f + MathF.Sin(time + i) * 20f;
                float alpha = 0.08f + MathF.Sin(time * 1.5f + i * 0.5f) * 0.05f;

                // Vertical wisps with blue/cyan tint
                RenderRect(x, y, 2f, height,
                    new Vector4(0.15f, 0.25f, 0.5f, alpha),
                    projection);
            }
        }

        private void RenderRect(float x, float y, float w, float h, Vector4 color, Matrix4 projection)
        {
            float[] vertices = new float[]
            {
                x, y,         color.X, color.Y, color.Z, color.W,
                x + w, y,     color.X, color.Y, color.Z, color.W,
                x + w, y + h, color.X, color.Y, color.Z, color.W,

                x, y,         color.X, color.Y, color.Z, color.W,
                x + w, y + h, color.X, color.Y, color.Z, color.W,
                x, y + h,     color.X, color.Y, color.Z, color.W,
            };

            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }

        protected override void OnUnload()
        {
            base.OnUnload();

            fontRenderer?.Dispose();
            uiManager?.Dispose();
            GL.DeleteVertexArray(vao);
            GL.DeleteBuffer(vbo);
            GL.DeleteProgram(shaderProgram);
        }
    }
}
