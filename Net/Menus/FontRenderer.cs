// FontRenderer.cs - Enhanced with multi-font support and better scaling
using System;
using System.Collections.Generic;
using System.IO;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.Fonts;

namespace Aetheris.UI
{
    public struct CharGlyph
    {
        public int TextureId;
        public Vector2 Size;
        public Vector2 Bearing;
        public float Advance;
    }

    public class FontRenderer : ITextRenderer, IDisposable
    {
        // Store glyphs at multiple sizes for better quality
        private readonly Dictionary<(char, int), CharGlyph> glyphCache = new Dictionary<(char, int), CharGlyph>();
        private readonly int shaderProgram;
        private readonly int vao;
        private readonly int vbo;
        private readonly int baseFontSize;

        private readonly FontCollection fontCollection;
        private readonly FontFamily fontFamily;
        
        // Pregenerated font sizes for crisp rendering at common scales
        private readonly int[] pregenSizes = { 24, 32, 48, 64, 96, 128 };

        public FontRenderer(string fontPath, int baseFontSize = 48)
        {
            this.baseFontSize = baseFontSize;

            fontCollection = new FontCollection();
            fontFamily = fontCollection.Add(fontPath);

            // Pregenerate common sizes
            foreach (int size in pregenSizes)
            {
                for (char c = (char)32; c < (char)127; c++)
                {
                    GenerateGlyph(c, size);
                }
            }

            shaderProgram = CreateTextShader();

            vao = GL.GenVertexArray();
            vbo = GL.GenBuffer();

            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, sizeof(float) * 6 * 4, IntPtr.Zero, BufferUsageHint.DynamicDraw);

            GL.VertexAttribPointer(0, 4, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);

            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.BindVertexArray(0);
        }

        private void GenerateGlyph(char c, int fontSize)
        {
            var font = fontFamily.CreateFont(fontSize, FontStyle.Regular);
            var textOptions = new TextOptions(font)
            {
                Dpi = 96,
                KerningMode = KerningMode.Auto
            };

            string s = c.ToString();

            FontRectangle bounds = TextMeasurer.MeasureSize(s, textOptions);
            FontRectangle advanceRect = TextMeasurer.MeasureAdvance(s, textOptions);

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

                float bearingX = bounds.Left - padding;
                float bearingY = bounds.Top + padding;
                float advance = advanceRect.Width;

                glyphCache[(c, fontSize)] = new CharGlyph
                {
                    TextureId = texture,
                    Size = new Vector2(width, height),
                    Bearing = new Vector2(bearingX, bearingY),
                    Advance = advance
                };

                GL.BindTexture(TextureTarget.Texture2D, 0);
            }
        }

        private int GetBestFontSize(float scale)
        {
            int targetSize = (int)(baseFontSize * scale);
            
            // Find closest pregenerated size
            int bestSize = pregenSizes[0];
            int minDiff = Math.Abs(targetSize - bestSize);
            
            foreach (int size in pregenSizes)
            {
                int diff = Math.Abs(targetSize - size);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    bestSize = size;
                }
            }
            
            return bestSize;
        }

        private int CreateTextShader()
        {
            string vertexShader = @"
                #version 330 core
                layout (location = 0) in vec4 vertex;
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
                    vec4 sampled = vec4(1.0, 1.0, 1.0, texture(text, TexCoords).r);
                    color = textColor * sampled;
                }
            ";

            int vertShader = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vertShader, vertexShader);
            GL.CompileShader(vertShader);

            int fragShader = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fragShader, fragmentShader);
            GL.CompileShader(fragShader);

            int program = GL.CreateProgram();
            GL.AttachShader(program, vertShader);
            GL.AttachShader(program, fragShader);
            GL.LinkProgram(program);

            GL.DeleteShader(vertShader);
            GL.DeleteShader(fragShader);

            return program;
        }

        public void DrawText(string text, Vector2 position, float scale = 1f, Vector4? color = null)
        {
            if (string.IsNullOrEmpty(text)) return;

            var textColor = color ?? new Vector4(1, 1, 1, 1);
            int fontSize = GetBestFontSize(scale);
            float renderScale = (baseFontSize * scale) / fontSize;

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
                if (!glyphCache.ContainsKey((c, fontSize)))
                    continue;

                CharGlyph glyph = glyphCache[(c, fontSize)];

                float xpos = x + glyph.Bearing.X * renderScale;
                float ypos = y - glyph.Bearing.Y * renderScale;

                float w = glyph.Size.X * renderScale;
                float h = glyph.Size.Y * renderScale;

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

                x += glyph.Advance * renderScale;
            }

            GL.BindVertexArray(0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        public Vector2 MeasureText(string text, float scale = 1f)
        {
            if (string.IsNullOrEmpty(text))
                return Vector2.Zero;

            int fontSize = GetBestFontSize(scale);
            float renderScale = (baseFontSize * scale) / fontSize;

            float width = 0f;
            float maxHeight = 0f;

            foreach (char c in text)
            {
                if (!glyphCache.ContainsKey((c, fontSize)))
                    continue;

                CharGlyph glyph = glyphCache[(c, fontSize)];
                width += glyph.Advance * renderScale;

                float charHeight = glyph.Size.Y * renderScale;
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
            foreach (var g in glyphCache.Values)
            {
                GL.DeleteTexture(g.TextureId);
            }
            glyphCache.Clear();

            GL.DeleteVertexArray(vao);
            GL.DeleteBuffer(vbo);
            GL.DeleteProgram(shaderProgram);
        }
    }

    // Multi-font manager for different font styles
    public class FontManager : IDisposable
    {
        private readonly Dictionary<string, FontRenderer> fonts = new Dictionary<string, FontRenderer>();
        private string currentFont = "default";

        public void AddFont(string name, string path, int baseSize = 48)
        {
            if (!fonts.ContainsKey(name))
            {
                fonts[name] = new FontRenderer(path, baseSize);
            }
        }

        public void SetCurrentFont(string name)
        {
            if (fonts.ContainsKey(name))
                currentFont = name;
        }

        public FontRenderer GetFont(string name)
        {
            return fonts.ContainsKey(name) ? fonts[name] : fonts[currentFont];
        }

        public FontRenderer GetCurrentFont()
        {
            return fonts[currentFont];
        }

        public void Dispose()
        {
            foreach (var font in fonts.Values)
            {
                font.Dispose();
            }
            fonts.Clear();
        }
    }
}
