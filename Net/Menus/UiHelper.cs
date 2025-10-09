// UIManager.cs - Enhanced Version
using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using OpenTK.Windowing.Desktop;

namespace Aetheris.UI
{
    // Text alignment options
    public enum TextAlign
    {
        Left,
        Center,
        Right
    }

    public enum VerticalAlign
    {
        Top,
        Middle,
        Bottom
    }

    // Layout helper for automatic positioning
    public class LayoutHelper
    {
        public static void VerticalStack(List<UIElement> elements, float startY, float spacing)
        {
            float currentY = startY;
            foreach (var element in elements)
            {
                element.Position = new Vector2(element.Position.X, currentY);
                currentY += element.Size.Y + spacing;
            }
        }

        public static void HorizontalStack(List<UIElement> elements, float startX, float spacing)
        {
            float currentX = startX;
            foreach (var element in elements)
            {
                element.Position = new Vector2(currentX, element.Position.Y);
                currentX += element.Size.X + spacing;
            }
        }

        public static void CenterHorizontally(UIElement element, float containerWidth)
        {
            element.Position = new Vector2((containerWidth - element.Size.X) / 2f, element.Position.Y);
        }

        public static void CenterVertically(UIElement element, float containerHeight)
        {
            element.Position = new Vector2(element.Position.X, (containerHeight - element.Size.Y) / 2f);
        }

        public static void Center(UIElement element, float containerWidth, float containerHeight)
        {
            element.Position = new Vector2(
                (containerWidth - element.Size.X) / 2f,
                (containerHeight - element.Size.Y) / 2f
            );
        }
    }

    // Padding and margin support
    public struct Spacing
    {
        public float Top, Right, Bottom, Left;

        public Spacing(float all) : this(all, all, all, all) { }
        public Spacing(float vertical, float horizontal) : this(vertical, horizontal, vertical, horizontal) { }
        public Spacing(float top, float right, float bottom, float left)
        {
            Top = top;
            Right = right;
            Bottom = bottom;
            Left = left;
        }

        public static Spacing Zero => new Spacing(0);
    }

    public interface ITextRenderer
    {
        void DrawText(string text, Vector2 position, float scale = 1f, Vector4? color = null);
        Vector2 MeasureText(string text, float scale = 1f);
        void SetProjection(Matrix4 projection);
    }

    public class UIManager : IDisposable
    {
        public readonly GameWindow window;
        private readonly List<UIElement> elements = new List<UIElement>();

        private readonly int shaderProgram;
        private readonly int vao;
        private readonly int vbo;

        public ITextRenderer? TextRenderer { get; set; }
        public GameWindow Window => window; // Public accessor for window

        private bool previousLeftDown = false;
        private Matrix4 currentProjection;

        public UIManager(GameWindow window, int shaderProgram, int vao, int vbo)
        {
            this.window = window ?? throw new ArgumentNullException(nameof(window));
            this.shaderProgram = shaderProgram;
            this.vao = vao;
            this.vbo = vbo;

            // Subscribe to text input events
            window.TextInput += OnTextInput;
            window.KeyDown += OnKeyDown;
        }

        public void AddElement(UIElement el)
        {
            elements.Add(el);
            el.Manager = this;
        }

        public void RemoveElement(UIElement el)
        {
            elements.Remove(el);
            el.Manager = null;
        }

        public void Clear()
        {
            foreach (var e in elements)
                e.Manager = null;
            elements.Clear();
        }

        public void Update(MouseState mouse, KeyboardState keys, float dt)
        {
            var mousePos = new Vector2(mouse.Position.X, mouse.Position.Y);
            bool leftDown = mouse.IsButtonDown(MouseButton.Left);

            // Find topmost element under mouse
            UIElement? hit = null;
            for (int i = elements.Count - 1; i >= 0; i--)
            {
                var e = elements[i];
                if (!e.Visible) continue;
                if (e.ContainsPoint(mousePos))
                {
                    hit = e;
                    break;
                }
            }

            // Update hover states
            foreach (var e in elements)
            {
                e.IsHovered = (e == hit);
            }

            // Click handling
            if (!previousLeftDown && leftDown)
            {
                foreach (var e in elements)
                    e.WasPressedThisFrame = e.IsHovered;

                // Set focus on click
                if (hit != null && hit.CanFocus)
                {
                    SetFocus(hit);
                }
            }
            else if (previousLeftDown && !leftDown)
            {
                foreach (var e in elements)
                {
                    if (e.WasPressedThisFrame && e.IsHovered)
                    {
                        e.OnClick();
                    }
                    e.WasPressedThisFrame = false;
                }
            }

            previousLeftDown = leftDown;

            // Update elements
            foreach (var e in elements)
                e.Update(dt);
        }

        public void Render(Matrix4 projection)
        {
            currentProjection = projection;

            GL.UseProgram(shaderProgram);
            int projLoc = GL.GetUniformLocation(shaderProgram, "projection");
            GL.UniformMatrix4(projLoc, false, ref projection);

            // Update text renderer projection if available
            TextRenderer?.SetProjection(projection);

            foreach (var e in elements)
            {
                if (e.Visible)
                    e.Render();
            }
        }

        // Enhanced rectangle drawing with optional rounding
        public void DrawRect(float x, float y, float w, float h, Vector4 color, float cornerRadius = 0f)
        {
            if (cornerRadius > 0f)
            {
                DrawRoundedRect(x, y, w, h, color, cornerRadius);
            }
            else
            {
                DrawSimpleRect(x, y, w, h, color);
            }
        }

        private void DrawSimpleRect(float x, float y, float w, float h, Vector4 color)
        {
            float[] vertices = new float[]
            {
                x,         y,         color.X, color.Y, color.Z, color.W,
                x + w,     y,         color.X, color.Y, color.Z, color.W,
                x + w,     y + h,     color.X, color.Y, color.Z, color.W,

                x,         y,         color.X, color.Y, color.Z, color.W,
                x + w,     y + h,     color.X, color.Y, color.Z, color.W,
                x,         y + h,     color.X, color.Y, color.Z, color.W,
            };

            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }

        private void DrawRoundedRect(float x, float y, float w, float h, Vector4 color, float radius)
        {
            // Approximate rounded corners with small rectangles
            int segments = 8;
            List<float> vertices = new List<float>();

            // Center rectangle
            AddQuad(vertices, x + radius, y, w - 2 * radius, h, color);
            AddQuad(vertices, x, y + radius, radius, h - 2 * radius, color);
            AddQuad(vertices, x + w - radius, y + radius, radius, h - 2 * radius, color);

            // Corners (simplified rounded effect)
            // Top-left
            AddQuad(vertices, x, y, radius, radius, color);
            // Top-right
            AddQuad(vertices, x + w - radius, y, radius, radius, color);
            // Bottom-left
            AddQuad(vertices, x, y + h - radius, radius, radius, color);
            // Bottom-right
            AddQuad(vertices, x + w - radius, y + h - radius, radius, radius, color);

            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float), vertices.ToArray(), BufferUsageHint.DynamicDraw);
            GL.DrawArrays(PrimitiveType.Triangles, 0, vertices.Count / 6);
        }

        private void AddQuad(List<float> vertices, float x, float y, float w, float h, Vector4 color)
        {
            vertices.AddRange(new float[] {
                x,         y,         color.X, color.Y, color.Z, color.W,
                x + w,     y,         color.X, color.Y, color.Z, color.W,
                x + w,     y + h,     color.X, color.Y, color.Z, color.W,

                x,         y,         color.X, color.Y, color.Z, color.W,
                x + w,     y + h,     color.X, color.Y, color.Z, color.W,
                x,         y + h,     color.X, color.Y, color.Z, color.W,
            });
        }

        public void DrawBorder(float x, float y, float w, float h, float thickness, Vector4 color)
        {
            DrawRect(x, y, w, thickness, color);
            DrawRect(x, y + h - thickness, w, thickness, color);
            DrawRect(x, y, thickness, h, color);
            DrawRect(x + w - thickness, y, thickness, h, color);
        }

        // Gradient rectangle
        public void DrawGradientRect(float x, float y, float w, float h, Vector4 colorTop, Vector4 colorBottom)
        {
            float[] vertices = new float[]
            {
                x,         y,         colorTop.X, colorTop.Y, colorTop.Z, colorTop.W,
                x + w,     y,         colorTop.X, colorTop.Y, colorTop.Z, colorTop.W,
                x + w,     y + h,     colorBottom.X, colorBottom.Y, colorBottom.Z, colorBottom.W,

                x,         y,         colorTop.X, colorTop.Y, colorTop.Z, colorTop.W,
                x + w,     y + h,     colorBottom.X, colorBottom.Y, colorBottom.Z, colorBottom.W,
                x,         y + h,     colorBottom.X, colorBottom.Y, colorBottom.Z, colorBottom.W,
            };

            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }

        private void OnTextInput(TextInputEventArgs e)
        {
            foreach (var elm in elements)
            {
                if (elm is TextInput ti && ti.Focused)
                {
                    ti.OnCharInput(e);
                    break;
                }
            }
        }

        private void OnKeyDown(KeyboardKeyEventArgs e)
        {
            foreach (var elm in elements)
            {
                if (elm is TextInput ti && ti.Focused)
                {
                    ti.OnKeyDown(e);
                    break;
                }
            }

            if (e.Key == Keys.Tab)
            {
                CycleFocus();
            }
        }

        private void CycleFocus()
        {
            int start = elements.FindIndex(x => x.Focused);
            int count = elements.Count;
            for (int i = 1; i <= count; i++)
            {
                int idx = (start + i) % count;
                if (elements[idx].CanFocus)
                {
                    SetFocus(elements[idx]);
                    break;
                }
            }
        }

        public void SetFocus(UIElement? e)
        {
            foreach (var elm in elements)
                elm.Focused = false;
            if (e != null && elements.Contains(e))
            {
                e.Focused = true;
            }
        }

        public void Dispose()
        {
            window.TextInput -= OnTextInput;
            window.KeyDown -= OnKeyDown;
            Clear();
        }
    }

    // Base UI element with enhanced properties
    public abstract class UIElement
    {
        public UIManager? Manager { get; internal set; }
        public Vector2 Position { get; set; } = Vector2.Zero;
        public Vector2 Size { get; set; } = Vector2.One;
        public bool Visible { get; set; } = true;
        public bool IsHovered { get; internal set; } = false;
        public bool WasPressedThisFrame { get; internal set; } = false;
        public bool Focused { get; set; } = false;
        public virtual bool CanFocus => false;

        public Spacing Padding { get; set; } = Spacing.Zero;
        public Spacing Margin { get; set; } = Spacing.Zero;

        public virtual void Update(float dt) { }
        public virtual void Render() { }
        public virtual void OnClick() { }

        public bool ContainsPoint(Vector2 p)
        {
            return p.X >= Position.X && p.X <= Position.X + Size.X &&
                   p.Y >= Position.Y && p.Y <= Position.Y + Size.Y;
        }

        protected Vector2 GetContentPosition()
        {
            return new Vector2(Position.X + Padding.Left, Position.Y + Padding.Top);
        }

        protected Vector2 GetContentSize()
        {
            return new Vector2(
                Size.X - Padding.Left - Padding.Right,
                Size.Y - Padding.Top - Padding.Bottom
            );
        }
    }

    // Enhanced Button with more styling options
    public class Button : UIElement
    {
        public string Label { get; set; }
        public Action? OnPressed { get; set; }
        public float LabelScale { get; set; } = 1.0f;
        public TextAlign TextAlign { get; set; } = TextAlign.Center;

        // inside Button class
        public Vector4 HaloColor { get; set; } = new Vector4(0.86f, 0.72f, 0.4f, 1f); // default warm gold
        public float HaloIntensity { get; set; } = 1.0f; // multiplier for alpha/pulse
                                                         // inside Button class

        public Vector4 Color { get; set; } = new Vector4(0.2f, 0.3f, 0.45f, 0.95f);
        public Vector4 HoverColor { get; set; } = new Vector4(0.28f, 0.42f, 0.7f, 1f);
        public Vector4 PressedColor { get; set; } = new Vector4(0.15f, 0.25f, 0.4f, 1f);
        public Vector4 TextColor { get; set; } = new Vector4(1, 1, 1, 1);
        public Vector4 BorderColor { get; set; } = new Vector4(0.7f, 0.8f, 1f, 1f);

        public float CornerRadius { get; set; } = 4f;
        public bool ShowBorder { get; set; } = true;

        public Button(string label)
        {
            Label = label;
            Padding = new Spacing(10, 20, 10, 20);
        }



        public override void Render()
        {
            if (Manager == null) return;

            // primary color based on state
            var baseCol = WasPressedThisFrame && IsHovered ? PressedColor : (IsHovered ? HoverColor : Color);

            // subtle gradient tints
            Vector4 topTint = new Vector4(
                MathF.Min(baseCol.X + 0.06f, 1f),
                MathF.Min(baseCol.Y + 0.06f, 1f),
                MathF.Min(baseCol.Z + 0.06f, 1f),
                baseCol.W
            );
            Vector4 bottomTint = new Vector4(
                MathF.Max(baseCol.X - 0.04f, 0f),
                MathF.Max(baseCol.Y - 0.04f, 0f),
                MathF.Max(baseCol.Z - 0.04f, 0f),
                baseCol.W
            );

            // TIMING for subtle pulse
            float t = (float)DateTime.Now.TimeOfDay.TotalSeconds;
            float pulse = 0.9f + 0.2f * MathF.Sin(t * 1.8f + (Label?.GetHashCode() ?? 0) * 0.001f);

            // --- OUTER halo (always present, faint) ---
            {
                // additive blending so halo reads like light
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);

                int outerLayers = 3;
                for (int i = outerLayers; i >= 1; i--)
                {
                    float layerFrac = i / (float)outerLayers;            // 1 .. 0.33
                    float pad = 10f + layerFrac * 18f;                   // outer extent
                                                                         // base faint alpha, scaled by HaloIntensity and a small pulse
                    float alpha = 0.04f * HaloIntensity * (0.9f + 0.12f * pulse) * (1f / layerFrac);
                    alpha = Math.Clamp(alpha, 0.005f, 0.18f);

                    var layerCol = new Vector4(HaloColor.X, HaloColor.Y, HaloColor.Z, alpha);
                    Manager.DrawRect(Position.X - pad, Position.Y - pad, Size.X + pad * 2f, Size.Y + pad * 2f, layerCol, CornerRadius + pad * 0.45f);
                }

                // restore normal blending for button draw
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            }

            // 1) draw the button base (gradient)
            Manager.DrawGradientRect(Position.X, Position.Y, Size.X, Size.Y, topTint, bottomTint);

            // inner subtle darker overlay for depth
            var innerOverlay = new Vector4(0f, 0f, 0f, 0.06f);
            Manager.DrawRect(Position.X + 4f, Position.Y + 4f, Size.X - 8f, Size.Y - 8f, innerOverlay, 0f);

            // thin inner carved line
            var innerLine = new Vector4(0f, 0f, 0f, 0.18f);
            Manager.DrawRect(Position.X + 2f, Position.Y + Size.Y - 6f, Size.X - 4f, 2f, innerLine);

            // border / rim
            if (ShowBorder)
            {
                var rim = IsHovered ? new Vector4(BorderColor.X, BorderColor.Y, BorderColor.Z, 1.0f) :
                                      new Vector4(BorderColor.X, BorderColor.Y, BorderColor.Z, 0.65f);
                Manager.DrawBorder(Position.X, Position.Y, Size.X, Size.Y, 3f, rim);
            }

            // --- INNER halo ring (subtle always, stronger when hovered) ---
            {
                float innerPad = 4f;
                float baseAlpha = 0.09f * HaloIntensity;
                float hoverBoost = IsHovered ? 0.36f * HaloIntensity * (1f + 0.25f * MathF.Sin(t * 3.0f)) : 0f;
                float alpha = Math.Clamp(baseAlpha + hoverBoost, 0f, 0.9f);

                if (alpha > 0.005f)
                {
                    // additive to make it feel like light
                    GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);

                    var innerCol = new Vector4(HaloColor.X, HaloColor.Y, HaloColor.Z, alpha);
                    Manager.DrawRect(Position.X - innerPad, Position.Y - innerPad, Size.X + innerPad * 2f, Size.Y + innerPad * 2f, innerCol, CornerRadius + innerPad * 0.3f);

                    GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                }
            }

            // finally: draw the text (with small drop shadow when hovered)
            if (Manager.TextRenderer != null)
            {
                var textSize = Manager.TextRenderer.MeasureText(Label, LabelScale);
                Vector2 textPos;

                switch (TextAlign)
                {
                    case TextAlign.Left:
                        textPos = new Vector2(Position.X + Padding.Left, Position.Y + (Size.Y - textSize.Y) / 2f);
                        break;
                    case TextAlign.Right:
                        textPos = new Vector2(Position.X + Size.X - textSize.X - Padding.Right, Position.Y + (Size.Y - textSize.Y) / 2f);
                        break;
                    default:
                        textPos = new Vector2(Position.X + (Size.X - textSize.X) / 2f, Position.Y + (Size.Y - textSize.Y) / 2f);
                        break;
                }

                if (IsHovered)
                {
                    Manager.TextRenderer.DrawText(Label, textPos + new Vector2(0, 2), LabelScale, new Vector4(0f, 0f, 0f, 0.30f));
                }

                Manager.TextRenderer.DrawText(Label, textPos, LabelScale, TextColor);
            }
        }

        public override void OnClick()
        {
            OnPressed?.Invoke();
        }

        public override bool CanFocus => true;
    }

    // Enhanced Label with alignment options
    public class Label : UIElement
    {
        public string Text { get; set; }
        public float Scale { get; set; } = 1f;
        public Vector4 Color { get; set; } = new Vector4(1, 1, 1, 1);
        public TextAlign TextAlign { get; set; } = TextAlign.Left;
        public VerticalAlign VerticalAlign { get; set; } = VerticalAlign.Top;
        public bool WordWrap { get; set; } = false;

        public Label(string text)
        {
            Text = text;
        }

        public override void Render()
        {
            if (Manager == null || Manager.TextRenderer == null) return;

            var textSize = Manager.TextRenderer.MeasureText(Text, Scale);
            Vector2 textPos = Position;

            // Horizontal alignment
            switch (TextAlign)
            {
                case TextAlign.Center:
                    textPos.X = Position.X + (Size.X - textSize.X) / 2f;
                    break;
                case TextAlign.Right:
                    textPos.X = Position.X + Size.X - textSize.X;
                    break;
            }

            // Vertical alignment
            switch (VerticalAlign)
            {
                case VerticalAlign.Middle:
                    textPos.Y = Position.Y + (Size.Y - textSize.Y) / 2f;
                    break;
                case VerticalAlign.Bottom:
                    textPos.Y = Position.Y + Size.Y - textSize.Y;
                    break;
            }

            Manager.TextRenderer.DrawText(Text, textPos, Scale, Color);
        }
    }

    // Enhanced TextInput
    public class TextInput : UIElement
    {
        public string Text { get; private set; } = "";
        public string Placeholder { get; set; } = "";
        public int CaretIndex { get; private set; } = 0;
        public int MaxLength { get; set; } = 100;
        public float LabelScale { get; set; } = 1f;

        public Vector4 BackgroundColor { get; set; } = new Vector4(0.08f, 0.08f, 0.1f, 0.95f);
        public Vector4 BorderColor { get; set; } = new Vector4(0.4f, 0.6f, 0.9f, 1f);
        public Vector4 FocusedBorderColor { get; set; } = new Vector4(0.5f, 0.7f, 1f, 1f);
        public Vector4 TextColor { get; set; } = new Vector4(1, 1, 1, 1);
        public Vector4 PlaceholderColor { get; set; } = new Vector4(0.5f, 0.5f, 0.5f, 1f);

        private float caretTimer = 0f;
        private bool caretVisible = true;
        private const float CARET_BLINK_PERIOD = 0.6f;

        public event Action<string>? OnEnterPressed;
        public event Action<string>? OnTextChanged;

        public TextInput()
        {
            Padding = new Spacing(8, 10, 8, 10);
        }

        public override bool CanFocus => true;

        public override void Update(float dt)
        {
            if (Focused)
            {
                caretTimer += dt;
                if (caretTimer >= CARET_BLINK_PERIOD)
                {
                    caretTimer = 0f;
                    caretVisible = !caretVisible;
                }
            }
        }

        public override void Render()
        {
            if (Manager == null) return;

            // Background
            Manager.DrawRect(Position.X, Position.Y, Size.X, Size.Y, BackgroundColor);
            Manager.DrawBorder(Position.X, Position.Y, Size.X, Size.Y, 2f, Focused ? FocusedBorderColor : BorderColor);

            // Text or placeholder
            if (Manager.TextRenderer != null)
            {
                var contentPos = GetContentPosition();
                var contentSize = GetContentSize();

                string displayText = string.IsNullOrEmpty(Text) ? Placeholder : Text;
                var displayColor = string.IsNullOrEmpty(Text) ? PlaceholderColor : TextColor;

                var textSize = Manager.TextRenderer.MeasureText(displayText, LabelScale);
                var textPos = new Vector2(contentPos.X, contentPos.Y + (contentSize.Y - textSize.Y) / 2f);

                Manager.TextRenderer.DrawText(displayText, textPos, LabelScale, displayColor);

                // Caret
                if (Focused && caretVisible && !string.IsNullOrEmpty(Text))
                {
                    string left = Text.Substring(0, CaretIndex);
                    var leftSize = Manager.TextRenderer.MeasureText(left, LabelScale);
                    float caretX = textPos.X + leftSize.X;
                    float caretY = textPos.Y;
                    float caretH = Manager.TextRenderer.MeasureText("M", LabelScale).Y;
                    Manager.DrawRect(caretX, caretY, 2f, caretH, new Vector4(1, 1, 1, 1));
                }
            }
        }

        internal void OnCharInput(TextInputEventArgs e)
        {
            if (char.IsControl(e.AsString, 0)) return;
            if (Text.Length >= MaxLength) return;

            Text = Text.Insert(CaretIndex, e.AsString);
            CaretIndex += e.AsString.Length;
            OnTextChanged?.Invoke(Text);
        }

        internal void OnKeyDown(KeyboardKeyEventArgs e)
        {
            if (e.Key == Keys.Backspace)
            {
                if (CaretIndex > 0)
                {
                    Text = Text.Remove(CaretIndex - 1, 1);
                    CaretIndex--;
                    OnTextChanged?.Invoke(Text);
                }
            }
            else if (e.Key == Keys.Delete)
            {
                if (CaretIndex < Text.Length)
                {
                    Text = Text.Remove(CaretIndex, 1);
                    OnTextChanged?.Invoke(Text);
                }
            }
            else if (e.Key == Keys.Left)
            {
                if (CaretIndex > 0) CaretIndex--;
            }
            else if (e.Key == Keys.Right)
            {
                if (CaretIndex < Text.Length) CaretIndex++;
            }
            else if (e.Key == Keys.Home)
            {
                CaretIndex = 0;
            }
            else if (e.Key == Keys.End)
            {
                CaretIndex = Text.Length;
            }
            else if (e.Key == Keys.Enter || e.Key == Keys.KeyPadEnter)
            {
                OnEnterPressed?.Invoke(Text);
            }
        }

        public void SetText(string text)
        {
            Text = text ?? "";
            CaretIndex = Math.Min(CaretIndex, Text.Length);
        }

        public void Clear()
        {
            Text = "";
            CaretIndex = 0;
        }
    }

    // Panel - Container for grouping elements
    public class Panel : UIElement
    {
        public Vector4 BackgroundColor { get; set; } = new Vector4(0.15f, 0.15f, 0.2f, 0.9f);
        public Vector4 BorderColor { get; set; } = new Vector4(0.3f, 0.3f, 0.4f, 1f);
        public float BorderThickness { get; set; } = 1f;
        public float CornerRadius { get; set; } = 0f;
        public bool ShowBorder { get; set; } = true;

        public override void Render()
        {
            if (Manager == null) return;

            Manager.DrawRect(Position.X, Position.Y, Size.X, Size.Y, BackgroundColor, CornerRadius);

            if (ShowBorder && BorderThickness > 0)
            {
                Manager.DrawBorder(Position.X, Position.Y, Size.X, Size.Y, BorderThickness, BorderColor);
            }
        }
    }

    // Checkbox
    public class Checkbox : UIElement
    {
        public string Label { get; set; }
        public bool Checked { get; set; } = false;
        public Action<bool>? OnChanged { get; set; }

        public float CheckboxSize { get; set; } = 20f;
        public float LabelScale { get; set; } = 1f;
        public float Spacing { get; set; } = 10f;

        public Vector4 BoxColor { get; set; } = new Vector4(0.2f, 0.2f, 0.25f, 1f);
        public Vector4 CheckColor { get; set; } = new Vector4(0.4f, 0.7f, 1f, 1f);
        public Vector4 BorderColor { get; set; } = new Vector4(0.4f, 0.5f, 0.6f, 1f);
        public Vector4 TextColor { get; set; } = new Vector4(1, 1, 1, 1);

        public Checkbox(string label)
        {
            Label = label;
            Size = new Vector2(200, 30);
        }

        public override void Render()
        {
            if (Manager == null) return;

            float boxY = Position.Y + (Size.Y - CheckboxSize) / 2f;

            // Draw checkbox box
            Manager.DrawRect(Position.X, boxY, CheckboxSize, CheckboxSize, BoxColor);
            Manager.DrawBorder(Position.X, boxY, CheckboxSize, CheckboxSize, 2f, BorderColor);

            // Draw check mark if checked
            if (Checked)
            {
                float padding = 4f;
                Manager.DrawRect(
                    Position.X + padding,
                    boxY + padding,
                    CheckboxSize - padding * 2,
                    CheckboxSize - padding * 2,
                    CheckColor
                );
            }

            // Draw label
            if (Manager.TextRenderer != null && !string.IsNullOrEmpty(Label))
            {
                var textSize = Manager.TextRenderer.MeasureText(Label, LabelScale);
                var textPos = new Vector2(
                    Position.X + CheckboxSize + Spacing,
                    Position.Y + (Size.Y - textSize.Y) / 2f
                );
                Manager.TextRenderer.DrawText(Label, textPos, LabelScale, TextColor);
            }
        }

        public override void OnClick()
        {
            Checked = !Checked;
            OnChanged?.Invoke(Checked);
        }

        public override bool CanFocus => true;
    }

    // Slider
    public class Slider : UIElement
    {
        public float Value { get; set; } = 0.5f; // 0.0 to 1.0
        public float Min { get; set; } = 0f;
        public float Max { get; set; } = 1f;
        public Action<float>? OnValueChanged { get; set; }

        public string Label { get; set; } = "";
        public float LabelScale { get; set; } = 1f;
        public bool ShowValue { get; set; } = true;

        public Vector4 TrackColor { get; set; } = new Vector4(0.2f, 0.2f, 0.25f, 1f);
        public Vector4 FillColor { get; set; } = new Vector4(0.4f, 0.6f, 0.9f, 1f);
        public Vector4 HandleColor { get; set; } = new Vector4(0.6f, 0.8f, 1f, 1f);
        public Vector4 TextColor { get; set; } = new Vector4(1, 1, 1, 1);

        public float TrackHeight { get; set; } = 6f;
        public float HandleSize { get; set; } = 16f;

        private bool isDragging = false;

        public Slider(string label = "")
        {
            Label = label;
            Size = new Vector2(200, 30);
        }

        public override void Update(float dt)
        {
            if (Manager == null) return;

            if (WasPressedThisFrame && IsHovered)
            {
                isDragging = true;
            }

            if (isDragging && Manager.window.MouseState.IsButtonDown(MouseButton.Left))
            {
                float mouseX = Manager.window.MouseState.Position.X;
                float trackX = Position.X;
                float trackWidth = Size.X;

                float normalizedValue = (mouseX - trackX) / trackWidth;
                normalizedValue = Math.Clamp(normalizedValue, 0f, 1f);

                float newValue = Min + normalizedValue * (Max - Min);
                if (Math.Abs(newValue - Value) > 0.001f)
                {
                    Value = newValue;
                    OnValueChanged?.Invoke(GetRealValue());
                }
            }
            else
            {
                isDragging = false;
            }
        }

        public float GetRealValue() => Min + Value * (Max - Min);

        public override void Render()
        {
            if (Manager == null) return;

            float trackY = Position.Y + (Size.Y - TrackHeight) / 2f;

            // Draw track
            Manager.DrawRect(Position.X, trackY, Size.X, TrackHeight, TrackColor);

            // Draw filled portion
            float fillWidth = Size.X * Value;
            Manager.DrawRect(Position.X, trackY, fillWidth, TrackHeight, FillColor);

            // Draw handle
            float handleX = Position.X + fillWidth - HandleSize / 2f;
            float handleY = Position.Y + (Size.Y - HandleSize) / 2f;
            Manager.DrawRect(handleX, handleY, HandleSize, HandleSize, HandleColor);

            // Draw label and value
            if (Manager.TextRenderer != null)
            {
                if (!string.IsNullOrEmpty(Label))
                {
                    var textPos = new Vector2(Position.X, Position.Y - 20);
                    Manager.TextRenderer.DrawText(Label, textPos, LabelScale, TextColor);
                }

                if (ShowValue)
                {
                    string valueText = GetRealValue().ToString("F2");
                    var textSize = Manager.TextRenderer.MeasureText(valueText, LabelScale);
                    var textPos = new Vector2(Position.X + Size.X - textSize.X, Position.Y - 20);
                    Manager.TextRenderer.DrawText(valueText, textPos, LabelScale, TextColor);
                }
            }
        }

        public override bool CanFocus => true;
    }
}
