// FontRenderer.cs
using System;
using System.Collections.Generic;
using System.IO;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

using SixLabors.ImageSharp.Advanced;
// SixLabors namespaces
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.Fonts;

namespace Aetheris.UI
{
    // Character glyph information
    public struct CharGlyph
    {
        public int TextureId;
        public Vector2 Size;    // pixel size of the bitmap
        public Vector2 Bearing; // x = left bearing (pixels), y = top offset from baseline (pixels)
        public float Advance;   // advance in pixels
    }

    public class FontRenderer : ITextRenderer, IDisposable
    {
        private readonly Dictionary<char, CharGlyph> glyphs = new Dictionary<char, CharGlyph>();
        private readonly int shaderProgram;
        private readonly int vao;
        private readonly int vbo;
        private readonly int fontSize;

        // SixLabors font objects
        private readonly FontCollection fontCollection;
        private readonly FontFamily fontFamily;
        private readonly Font font;

        public FontRenderer(string fontPath, int fontSize = 24)
        {
            this.fontSize = fontSize;

            // Load font using SixLabors.Fonts
            fontCollection = new FontCollection();
            fontFamily = fontCollection.Add(fontPath);
            font = fontFamily.CreateFont(fontSize, FontStyle.Regular);

            // Generate glyphs for ASCII printable characters
            for (char c = (char)32; c < (char)127; c++)
            {
                GenerateGlyph(c);
            }

            // Create shader for text rendering
            shaderProgram = CreateTextShader();

            // Setup VAO/VBO for text quads
            vao = GL.GenVertexArray();
            vbo = GL.GenBuffer();

            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, sizeof(float) * 6 * 4, IntPtr.Zero, BufferUsageHint.DynamicDraw);

            // Position + TexCoords
            GL.VertexAttribPointer(0, 4, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);

            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.BindVertexArray(0);
        }

        private void GenerateGlyph(char c)
        {
            var textOptions = new TextOptions(font)
            {
                Dpi = 96,
                KerningMode = KerningMode.Auto
            };

            string s = c.ToString();

            FontRectangle bounds = TextMeasurer.MeasureSize(s, textOptions);
            FontRectangle advanceRect = TextMeasurer.MeasureAdvance(s, textOptions);

            // Add padding to ensure full glyph is captured
            int padding = 5;
            int width = Math.Max(1, (int)Math.Ceiling(bounds.Width) + padding * 2);
            int height = Math.Max(1, (int)Math.Ceiling(bounds.Height) + padding * 2);

            using (var img = new Image<Rgba32>(width, height))
            {
                img.Mutate(ctx =>
                {
                    ctx.Clear(Color.Transparent);
                    var drawPoint = new PointF(-bounds.Left + padding, -bounds.Top + padding);
                    ctx.DrawText(s, font, Color.White, drawPoint);
                });

                byte[] bitmap = new byte[width * height];
                for (int y = 0; y < height; y++)
                {
                    var rowSpan = img.DangerousGetPixelRowMemory(y).Span;
                    for (int x = 0; x < width; x++)
                    {
                        bitmap[y * width + x] = rowSpan[x].A;
                    }
                }

                int texture = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, texture);

                GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8,
                    width, height, 0, PixelFormat.Red, PixelType.UnsignedByte, bitmap);

                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

                // For top-left origin (Y increases downward):
                // bearingX: horizontal offset from pen position to left edge of glyph
                // bearingY: vertical offset from baseline to top of glyph
                float bearingX = bounds.Left - padding;
                float bearingY = bounds.Top + padding;  // Distance from baseline to top
                float advance = advanceRect.Width;

                glyphs[c] = new CharGlyph
                {
                    TextureId = texture,
                    Size = new Vector2(width, height),
                    Bearing = new Vector2(bearingX, bearingY),
                    Advance = advance
                };

                GL.BindTexture(TextureTarget.Texture2D, 0);
            }
        }
        private int CreateTextShader()
        {
            string vertexShader = @"
                #version 330 core
                layout (location = 0) in vec4 vertex; // <vec2 pos, vec2 tex>
                out vec2 TexCoords;

                uniform mat4 projection;

                void main()
                {
                    gl_Position = projection * vec4(vertex.xy, 0.0, 1.0);
                    TexCoords = vertex.zw;
                }
            ";

            string fragmentShader = @"
                #version 330 core
                in vec2 TexCoords;
                out vec4 color;

                uniform sampler2D text;
                uniform vec4 textColor;

                void main()
                {
                    // We uploaded alpha into the red channel, so sample .r
                    vec4 sampled = vec4(1.0, 1.0, 1.0, texture(text, TexCoords).r);
                    color = textColor * sampled;
                }
            ";

            int vertShader = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vertShader, vertexShader);
            GL.CompileShader(vertShader);
            CheckShaderCompilation(vertShader, "Vertex");

            int fragShader = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fragShader, fragmentShader);
            GL.CompileShader(fragShader);
            CheckShaderCompilation(fragShader, "Fragment");

            int program = GL.CreateProgram();
            GL.AttachShader(program, vertShader);
            GL.AttachShader(program, fragShader);
            GL.LinkProgram(program);
            CheckProgramLinking(program);

            GL.DeleteShader(vertShader);
            GL.DeleteShader(fragShader);

            return program;
        }

        private void CheckShaderCompilation(int shader, string type)
        {
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int success);
            if (success == 0)
            {
                string infoLog = GL.GetShaderInfoLog(shader);
                throw new Exception($"{type} shader compilation failed: {infoLog}");
            }
        }

        private void CheckProgramLinking(int program)
        {
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int success);
            if (success == 0)
            {
                string infoLog = GL.GetProgramInfoLog(program);
                throw new Exception($"Program linking failed: {infoLog}");
            }
        }

        public void DrawText(string text, Vector2 position, float scale = 1f, Vector4? color = null)
        {
            if (string.IsNullOrEmpty(text)) return;

            var textColor = color ?? new Vector4(1, 1, 1, 1);

            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            GL.UseProgram(shaderProgram);
            GL.Uniform4(GL.GetUniformLocation(shaderProgram, "textColor"), textColor);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindVertexArray(vao);

            float x = position.X;
            float y = position.Y;

            foreach (char c in text)
            {
                if (!glyphs.ContainsKey(c))
                    continue;

                CharGlyph glyph = glyphs[c];

                // For top-left origin with Y increasing downward:
                // xpos: pen position + horizontal bearing
                // ypos: baseline position - distance from baseline to top of glyph
                float xpos = x + glyph.Bearing.X * scale;
                float ypos = y - glyph.Bearing.Y * scale;

                float w = glyph.Size.X * scale;
                float h = glyph.Size.Y * scale;

                // Texture coordinates: 0,0 is top-left in ImageSharp, which matches our screen coords
                float[] vertices = {
            xpos,     ypos,       0.0f, 0.0f,
            xpos,     ypos + h,   0.0f, 1.0f,
            xpos + w, ypos + h,   1.0f, 1.0f,

            xpos,     ypos,       0.0f, 0.0f,
            xpos + w, ypos + h,   1.0f, 1.0f,
            xpos + w, ypos,       1.0f, 0.0f
        };

                GL.BindTexture(TextureTarget.Texture2D, glyph.TextureId);
                GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
                GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, sizeof(float) * vertices.Length, vertices);
                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

                GL.DrawArrays(PrimitiveType.Triangles, 0, 6);

                x += glyph.Advance * scale;
            }

            GL.BindVertexArray(0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }
        public Vector2 MeasureText(string text, float scale = 1f)
        {
            if (string.IsNullOrEmpty(text))
                return Vector2.Zero;

            float width = 0f;
            float maxHeight = 0f;

            foreach (char c in text)
            {
                if (!glyphs.ContainsKey(c))
                    continue;

                CharGlyph glyph = glyphs[c];
                width += glyph.Advance * scale;

                float charHeight = glyph.Size.Y * scale;
                if (charHeight > maxHeight)
                    maxHeight = charHeight;
            }

            return new Vector2(width, maxHeight);
        }

        public void SetProjection(Matrix4 projection)
        {
            GL.UseProgram(shaderProgram);
            int projLoc = GL.GetUniformLocation(shaderProgram, "projection");
            GL.UniformMatrix4(projLoc, false, ref projection);
        }

        public void Dispose()
        {
            foreach (var g in glyphs.Values)
            {
                GL.DeleteTexture(g.TextureId);
            }
            glyphs.Clear();

            GL.DeleteVertexArray(vao);
            GL.DeleteBuffer(vbo);
            GL.DeleteProgram(shaderProgram);
        }
    }
}
