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

        // Keep splashes exactly as defined
        private string[] Splashes = {
            "I stole this from Minecraft!",
            "I'm out of step!",
            "Stop! You've violated the law!",
            "I've got a straight edge!",
            "I'm not trying to ruin your fun!",
            "Poyo!",
	    "TCP, UDP, Bro just suck my P",
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
            "My armor's held together with safety pins.",
            "Counterspell this!",
        };

        private float time = 0f;
        private Random random = new Random();

        // UI Elements
        private Label? titleLabel;
        private Label? subtitleLabel;
        private Button? singlePlayerBtn;
        private Button? multiplayerBtn;
        private Button? settingsBtn;
        private Button? exitBtn;

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

            // Deep twilight background
            GL.ClearColor(0.01f, 0.015f, 0.03f, 1.0f);

            CreateShader();

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

            uiManager = new UIManager(this, shaderProgram, vao, vbo);

            try
            {
                fontRenderer = new FontRenderer("assets/font.ttf", 48);
                uiManager.TextRenderer = fontRenderer;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MenuWindow] Warning: Could not load font: {ex.Message}");
                Console.WriteLine("[MenuWindow] Menu will run without text rendering");
            }

            CreateUI();

            Console.WriteLine("[MenuWindow] Twilight Princess menu initialized");
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
                uniform vec2 u_resolution;
                uniform float u_time;

                float hash21(vec2 p) {
                    p = fract(p * vec2(123.34, 456.21));
                    p += dot(p, p + 78.233);
                    return fract(p.x * p.y);
                }

                void main()
                {
                    vec2 uv = gl_FragCoord.xy / u_resolution;
                    vec3 base = fragColor.rgb;

                    // Ethereal glow from center top - blue-green tint
                    vec2 center = vec2(0.5, 0.2);
                    float dist = distance(uv, center);
                    float glow = smoothstep(0.4, 0.0, dist);
                    
                    // Blue-green mystical tint
                    vec3 glowColor = vec3(0.2, 0.6, 0.55) * pow(glow, 1.5) * 0.4;

                    // Animated twilight particles
                    float particle = 0.012 * sin(u_time * 0.7 + uv.y * 30.0 + uv.x * 15.0);

                    // Vignette - darker edges
                    float vignette = smoothstep(0.5, 1.2, dist + 0.2);

                    // Film grain
                    float grain = (hash21(gl_FragCoord.xy * (0.5 + mod(u_time, 1.0))) - 0.5) * 0.02;

                    vec3 color = base + glowColor + particle + grain;
                    
                    // Deep blue-green tint on edges
                    color *= mix(vec3(1.0), vec3(0.5, 0.7, 0.75), vignette * 0.7);

                    finalColor = vec4(color, fragColor.a);
                }
            ";

            int vs = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vs, vertexShader);
            GL.CompileShader(vs);

            int fs = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fs, fragmentShader);
            GL.CompileShader(fs);

            shaderProgram = GL.CreateProgram();
            GL.AttachShader(shaderProgram, vs);
            GL.AttachShader(shaderProgram, fs);
            GL.LinkProgram(shaderProgram);

            GL.DeleteShader(vs);
            GL.DeleteShader(fs);
        }

        private void CreateUI()
        {
            if (uiManager == null) return;

            float cx = ClientSize.X / 2f;
            float cy = ClientSize.Y / 2f;

            // Large ethereal backdrop behind title
            var backdrop = new Panel()
            {
                Position = new Vector2(cx - 400, 20),
                Size = new Vector2(800, 260),
                CornerRadius = 200f,
                BackgroundColor = new Vector4(0.08f, 0.06f, 0.12f, 0.25f),
                ShowBorder = false
            };
            uiManager.AddElement(backdrop);

            // Triforce-inspired emblem
            var emblem = new Label("▲")
            {
                Position = new Vector2(cx - 20, 70),
                Size = new Vector2(40, 40),
                Scale = 2.2f,
                Color = new Vector4(0.4f, 0.8f, 0.75f, 0.8f), // Cyan-green
                TextAlign = TextAlign.Center
            };
            uiManager.AddElement(emblem);

            // Title with twilight glow
            titleLabel = new Label("AETHERIS")
            {
                Position = new Vector2(cx - 280, 100),
                Size = new Vector2(560, 90),
                Scale = 3.2f,
                Color = new Vector4(0.4f, 0.75f, 0.7f, 1f), // Cyan-green
                TextAlign = TextAlign.Center
            };
            uiManager.AddElement(titleLabel);

            // Decorative line under title
            var line = new Panel()
            {
                Position = new Vector2(cx - 250, 200),
                Size = new Vector2(500, 2),
                BackgroundColor = new Vector4(0.3f, 0.6f, 0.55f, 0.4f),
                ShowBorder = false
            };
            uiManager.AddElement(line);

            // Subtitle with splash text (moved down below title)
            int roll = random.Next(0, Splashes.Length);
            subtitleLabel = new Label(Splashes[roll])
            {
                Position = new Vector2(cx - 300, 220),
                Size = new Vector2(600, 35),
                Scale = 1.1f,
                Color = new Vector4(0.5f, 0.7f, 0.85f, 0.75f), // Light blue
                TextAlign = TextAlign.Center
            };
            uiManager.AddElement(subtitleLabel);

            // Button layout
            float buttonW = 380f;
            float buttonH = 72f;
            float startY = cy - 20f;
            float spacing = 88f;

            // Twilight color scheme - dark blue/green
            Vector4 buttonBase = new Vector4(0.05f, 0.08f, 0.1f, 0.92f); // Dark blue-green
            Vector4 hoverTint = new Vector4(0.15f, 0.25f, 0.3f, 0.96f); // Lighter blue-green
            Vector4 pressedTint = new Vector4(0.02f, 0.04f, 0.05f, 0.92f);
            Vector4 cyanText = new Vector4(0.5f, 0.85f, 0.8f, 1f); // Cyan text
            Vector4 borderCyan = new Vector4(0.3f, 0.65f, 0.6f, 0.85f);

            // SINGLE PLAYER
            singlePlayerBtn = new Button("Single Player")
            {
                Position = new Vector2(cx - buttonW / 2f, startY),
                Size = new Vector2(buttonW, buttonH),
                LabelScale = 1.4f,
                CornerRadius = 8f,
                Color = buttonBase,
                HoverColor = hoverTint,
                PressedColor = pressedTint,
                TextColor = cyanText,
                BorderColor = borderCyan,
                ShowBorder = true,
                HaloColor = new Vector4(0.4f, 0.8f, 0.75f, 1f), // Cyan-green glow
                HaloIntensity = 1.2f
            };
            singlePlayerBtn.OnPressed = () =>
            {
                Result = MenuResult.SinglePlayer;
                Close();
            };
            uiManager.AddElement(singlePlayerBtn);

            // MULTIPLAYER
            multiplayerBtn = new Button("Multiplayer")
            {
                Position = new Vector2(cx - buttonW / 2f, startY + spacing),
                Size = new Vector2(buttonW, buttonH),
                LabelScale = 1.4f,
                CornerRadius = 8f,
                Color = buttonBase,
                HoverColor = hoverTint,
                PressedColor = pressedTint,
                TextColor = cyanText,
                BorderColor = borderCyan,
                ShowBorder = true,
                HaloColor = new Vector4(0.3f, 0.65f, 0.85f, 1f), // Blue glow
                HaloIntensity = 1.0f
            };
            multiplayerBtn.OnPressed = () =>
            {
                Result = MenuResult.Multiplayer;
                Close();
            };
            uiManager.AddElement(multiplayerBtn);

            // SETTINGS
            settingsBtn = new Button("Settings")
            {
                Position = new Vector2(cx - buttonW / 2f, startY + spacing * 2),
                Size = new Vector2(buttonW, buttonH),
                LabelScale = 1.4f,
                CornerRadius = 8f,
                Color = buttonBase,
                HoverColor = hoverTint,
                PressedColor = pressedTint,
                TextColor = cyanText,
                BorderColor = borderCyan,
                ShowBorder = true,
                HaloColor = new Vector4(0.35f, 0.75f, 0.7f, 1f), // Teal glow
                HaloIntensity = 0.9f
            };
            settingsBtn.OnPressed = () =>
            {
                Result = MenuResult.Settings;
                Console.WriteLine("[MenuWindow] Settings not yet implemented");
            };
            uiManager.AddElement(settingsBtn);

            // EXIT
            exitBtn = new Button("Exit")
            {
                Position = new Vector2(cx - buttonW / 2f, startY + spacing * 3),
                Size = new Vector2(buttonW, buttonH),
                LabelScale = 1.4f,
                CornerRadius = 8f,
                Color = new Vector4(0.08f, 0.05f, 0.06f, 0.92f),
                HoverColor = new Vector4(0.22f, 0.12f, 0.14f, 0.96f),
                PressedColor = new Vector4(0.04f, 0.02f, 0.03f, 0.92f),
                TextColor = new Vector4(0.8f, 0.6f, 0.65f, 1f),
                BorderColor = new Vector4(0.45f, 0.25f, 0.28f, 0.85f),
                ShowBorder = true,
                HaloColor = new Vector4(0.8f, 0.4f, 0.5f, 1f), // Muted red glow
                HaloIntensity = 0.8f
            };
            exitBtn.OnPressed = () =>
            {
                Result = MenuResult.Exit;
                Close();
            };
            uiManager.AddElement(exitBtn);

            // Footer elements
            var footer = new Label("Press ESC to exit")
            {
                Position = new Vector2(cx - 90, ClientSize.Y - 65),
                Size = new Vector2(180, 30),
                Scale = 0.9f,
                Color = new Vector4(0.45f, 0.65f, 0.7f, 0.5f), // Blue-green tint
                TextAlign = TextAlign.Center
            };
            uiManager.AddElement(footer);

            var version = new Label("v0.1.0 Alpha")
            {
                Position = new Vector2(25, ClientSize.Y - 45),
                Size = new Vector2(150, 30),
                Scale = 0.75f,
                Color = new Vector4(0.3f, 0.4f, 0.45f, 0.35f) // Dark blue-green
            };
            uiManager.AddElement(version);
        }

        protected override void OnResize(ResizeEventArgs e)
        {
            base.OnResize(e);
            GL.Viewport(0, 0, e.Width, e.Height);
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
            uiManager?.Update(MouseState, KeyboardState, (float)e.Time);

            // Animate subtitle with gentle wave (blue-green tint)
            if (subtitleLabel != null)
            {
                float wave = MathF.Sin(time * 1.2f) * 0.08f;
                subtitleLabel.Color = new Vector4(
                    0.5f + wave * 0.3f,
                    0.7f + wave * 0.5f,
                    0.85f + wave * 0.2f,
                    0.75f + wave * 0.15f
                );
            }

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

            var projection = Matrix4.CreateOrthographicOffCenter(0, ClientSize.X, ClientSize.Y, 0, -1, 1);

            GL.UseProgram(shaderProgram);
            int projLoc = GL.GetUniformLocation(shaderProgram, "projection");
            GL.UniformMatrix4(projLoc, false, ref projection);

            int resLoc = GL.GetUniformLocation(shaderProgram, "u_resolution");
            GL.Uniform2(resLoc, new Vector2(ClientSize.X, ClientSize.Y));
            int timeLoc = GL.GetUniformLocation(shaderProgram, "u_time");
            GL.Uniform1(timeLoc, time);

            RenderBackground(projection);
            RenderTwilightEffects(projection);

            uiManager?.Render(projection);

            SwapBuffers();
        }

        private void RenderBackground(Matrix4 projection)
        {
            GL.UseProgram(shaderProgram);
            int projLoc = GL.GetUniformLocation(shaderProgram, "projection");
            GL.UniformMatrix4(projLoc, false, ref projection);

            // Deep blue-green twilight gradient (top to bottom)
            float[] vertices = new float[]
            {
                // Top: deep dark blue
                0, 0,                 0.01f, 0.02f, 0.04f, 1.0f,
                ClientSize.X, 0,      0.02f, 0.04f, 0.06f, 1.0f,

                // Bottom: dark teal-green
                ClientSize.X, ClientSize.Y, 0.08f, 0.14f, 0.12f, 1.0f,
                0, 0,                 0.01f, 0.02f, 0.04f, 1.0f,
                ClientSize.X, ClientSize.Y, 0.08f, 0.14f, 0.12f, 1.0f,
                0, ClientSize.Y,      0.05f, 0.10f, 0.09f, 1.0f,
            };

            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }

        private void RenderTwilightEffects(Matrix4 projection)
        {
            GL.UseProgram(shaderProgram);
            int projLoc = GL.GetUniformLocation(shaderProgram, "projection");
            GL.UniformMatrix4(projLoc, false, ref projection);

            // Floating cyan-green particles (ethereal wisps)
            for (int i = 0; i < 30; i++)
            {
                float ox = MathF.Sin(time * (0.15f + i * 0.02f) + i * 0.5f) * 140f;
                float oy = MathF.Cos(time * (0.12f + i * 0.015f) + i * 0.8f) * 90f;
                float x = 80f + i * 100f + ox;
                float y = 60f + (i % 4) * 270f + oy;
                float size = 2.0f + MathF.Sin(time * 1.2f + i) * 1.2f;
                float alpha = 0.35f + MathF.Sin(time * 1.4f + i * 0.3f) * 0.2f;

                // Cyan-green glow
                RenderRect(x, y, size, size,
                    new Vector4(0.3f, 0.8f, 0.7f, alpha),
                    projection);
            }

            // Mystical deep blue wisps (fewer, slower)
            for (int i = 0; i < 12; i++)
            {
                float ox = MathF.Sin(time * (0.08f + i * 0.03f) + i * 0.7f) * 160f;
                float oy = MathF.Cos(time * (0.06f + i * 0.025f) + i) * 70f;
                float x = 120f + i * 200f + ox;
                float y = 30f + (i % 5) * 250f + oy;
                float w = 3.0f + MathF.Sin(time * 0.8f + i * 0.5f) * 1.6f;
                float alpha = 0.22f + MathF.Sin(time * 1.0f + i * 0.4f) * 0.12f;

                // Deep blue
                RenderRect(x, y, w, w,
                    new Vector4(0.25f, 0.5f, 0.75f, alpha),
                    projection);
            }

            // Distant dark forest silhouette at bottom
            for (int i = 0; i < 8; i++)
            {
                float x = -150f + i * (ClientSize.X / 7f);
                float y = ClientSize.Y - 180f - MathF.Sin(time * 0.04f + i * 0.5f) * 12f;
                float w = ClientSize.X / 5f + 80f;
                float h = 200f + MathF.Sin(i * 1.2f) * 40f;
                
                RenderRect(x, y, w, h, 
                    new Vector4(0.01f, 0.02f, 0.03f, 0.75f), 
                    projection);
            }

            // Subtle horizontal blue-green bands (atmospheric layers)
            for (int i = 0; i < 3; i++)
            {
                float y = ClientSize.Y * 0.3f + i * 180f + MathF.Sin(time * 0.3f + i) * 20f;
                float alpha = 0.03f + MathF.Sin(time * 0.5f + i * 2f) * 0.015f;
                
                RenderRect(0, y, ClientSize.X, 60f,
                    new Vector4(0.2f, 0.5f, 0.45f, alpha),
                    projection);
            }
        }

        private void RenderRect(float x, float y, float w, float h, Vector4 color, Matrix4 projection)
        {
            float[] vertices = new float[]
            {
                x, y, color.X, color.Y, color.Z, color.W,
                x + w, y, color.X, color.Y, color.Z, color.W,
                x + w, y + h, color.X, color.Y, color.Z, color.W,

                x, y, color.X, color.Y, color.Z, color.W,
                x + w, y + h, color.X, color.Y, color.Z, color.W,
                x, y + h, color.X, color.Y, color.Z, color.W
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
            GL.DeleteBuffer(vbo);
            GL.DeleteVertexArray(vao);
            GL.DeleteProgram(shaderProgram);
        }
    }
}
