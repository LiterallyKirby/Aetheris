# Adding Text and Images to OpenTK Menus

## Table of Contents
1. [Understanding Textures](#understanding-textures)
2. [Adding Images](#adding-images)
3. [Adding Text (Simple Method)](#adding-text-simple-method)
4. [Adding Text (Bitmap Font Method)](#adding-text-bitmap-font-method)
5. [Complete Example](#complete-example)

---

## Understanding Textures

In OpenGL/OpenTK, images and text are both rendered using **textures** - images stored on the GPU that get mapped onto rectangles.

Think of it like this:
- **Without texture**: A colored rectangle
- **With texture**: A rectangle with an image/text on it

---

## Adding Images

### Step 1: Install STB Image (for loading images)

Add this NuGet package to your project:
```
StbImageSharp
```

### Step 2: Create a Texture Loading Method

```csharp
using StbImageSharp;
using System.IO;

private int LoadTexture(string path)
{
    // Load image file
    byte[] imageData = File.ReadAllBytes(path);
    
    // Decode image using StbImageSharp
    ImageResult image = ImageResult.FromMemory(imageData, ColorComponents.RedGreenBlueAlpha);
    
    // Create OpenGL texture
    int textureId = GL.GenTexture();
    GL.BindTexture(TextureTarget.Texture2D, textureId);
    
    // Upload image data to GPU
    GL.TexImage2D(
        TextureTarget.Texture2D,
        0,                                    // Mipmap level
        PixelInternalFormat.Rgba,            // How GPU stores it
        image.Width,
        image.Height,
        0,
        PixelFormat.Rgba,                    // Format of our data
        PixelType.UnsignedByte,
        image.Data                           // The actual pixel data
    );
    
    // Set texture parameters (how it's displayed)
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    
    Console.WriteLine($"Loaded texture: {path} ({image.Width}x{image.Height})");
    
    return textureId;
}
```

### Step 3: Update Your Shader to Support Textures

**Vertex Shader** (now includes texture coordinates):
```glsl
#version 330 core
layout(location = 0) in vec2 aPosition;
layout(location = 1) in vec4 aColor;
layout(location = 2) in vec2 aTexCoord;  // NEW: Texture coordinates

out vec4 fragColor;
out vec2 texCoord;  // NEW: Pass to fragment shader

uniform mat4 projection;

void main()
{
    gl_Position = projection * vec4(aPosition, 0.0, 1.0);
    fragColor = aColor;
    texCoord = aTexCoord;  // NEW
}
```

**Fragment Shader** (now samples from texture):
```glsl
#version 330 core
in vec4 fragColor;
in vec2 texCoord;  // NEW

out vec4 finalColor;

uniform sampler2D textureSampler;  // NEW: The texture
uniform bool useTexture;           // NEW: Toggle texture on/off

void main()
{
    if (useTexture) {
        vec4 texColor = texture(textureSampler, texCoord);
        finalColor = texColor * fragColor;  // Multiply by color for tinting
    } else {
        finalColor = fragColor;  // No texture, just color
    }
}
```

### Step 4: Update VAO Setup for Texture Coordinates

```csharp
protected override void OnLoad()
{
    base.OnLoad();
    
    // ... shader compilation code ...
    
    vao = GL.GenVertexArray();
    vbo = GL.GenBuffer();
    
    GL.BindVertexArray(vao);
    GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
    
    // Now each vertex has 8 floats: [x, y, r, g, b, a, u, v]
    // Position (x, y)
    GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 8 * sizeof(float), 0);
    GL.EnableVertexAttribArray(0);
    
    // Color (r, g, b, a)
    GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 8 * sizeof(float), 2 * sizeof(float));
    GL.EnableVertexAttribArray(1);
    
    // Texture coordinates (u, v) - NEW
    GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 8 * sizeof(float), 6 * sizeof(float));
    GL.EnableVertexAttribArray(2);
    
    // Load textures
    logoTexture = LoadTexture("textures/logo.png");
    buttonBackgroundTexture = LoadTexture("textures/button.png");
}
```

### Step 5: Drawing a Textured Rectangle

```csharp
private void RenderTexturedRect(float x, float y, float w, float h, int textureId)
{
    // Texture coordinates: (0,0) = top-left, (1,1) = bottom-right
    float[] vertices = new float[]
    {
        // Positions     Colors (white)         Texture coords
        // X    Y        R    G    B    A       U    V
        x,      y,       1f,  1f,  1f,  1f,    0f,  0f,  // Top-left
        x + w,  y,       1f,  1f,  1f,  1f,    1f,  0f,  // Top-right
        x + w,  y + h,   1f,  1f,  1f,  1f,    1f,  1f,  // Bottom-right
        
        x,      y,       1f,  1f,  1f,  1f,    0f,  0f,  // Top-left
        x + w,  y + h,   1f,  1f,  1f,  1f,    1f,  1f,  // Bottom-right
        x,      y + h,   1f,  1f,  1f,  1f,    0f,  1f,  // Bottom-left
    };
    
    GL.BindVertexArray(vao);
    GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
    GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);
    
    // Enable texture
    GL.BindTexture(TextureTarget.Texture2D, textureId);
    int useTextureLoc = GL.GetUniformLocation(shaderProgram, "useTexture");
    GL.Uniform1(useTextureLoc, 1);  // true
    
    GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
    
    // Disable texture for subsequent draws
    GL.Uniform1(useTextureLoc, 0);  // false
}
```

### Step 6: Using Images in Your Menu

```csharp
protected override void OnRenderFrame(FrameEventArgs e)
{
    base.OnRenderFrame(e);
    GL.Clear(ClearBufferMask.ColorBufferBit);
    GL.UseProgram(shaderProgram);
    
    // Set projection
    var projection = Matrix4.CreateOrthographicOffCenter(0, ClientSize.X, ClientSize.Y, 0, -1, 1);
    int projLoc = GL.GetUniformLocation(shaderProgram, "projection");
    GL.UniformMatrix4(projLoc, false, ref projection);
    
    // Draw logo at top center
    float logoWidth = 400f;
    float logoHeight = 200f;
    float logoX = ClientSize.X / 2f - logoWidth / 2f;
    float logoY = 50f;
    RenderTexturedRect(logoX, logoY, logoWidth, logoHeight, logoTexture);
    
    // Draw buttons with textured backgrounds
    foreach (var button in buttons)
    {
        float x = button.X - button.Width / 2f;
        float y = button.Y;
        RenderTexturedRect(x, y, button.Width, button.Height, buttonBackgroundTexture);
    }
    
    SwapBuffers();
}
```

---

## Adding Text (Simple Method)

### Using System.Drawing for Text

This method uses C#'s built-in text rendering to create textures on-the-fly.

**Note**: This requires `System.Drawing.Common` NuGet package.

```csharp
using System.Drawing;
using System.Drawing.Imaging;

private int CreateTextTexture(string text, Font font, Color textColor, Color backgroundColor)
{
    // Measure text size
    Bitmap bitmap = new Bitmap(1, 1);
    Graphics graphics = Graphics.FromImage(bitmap);
    SizeF textSize = graphics.MeasureString(text, font);
    graphics.Dispose();
    bitmap.Dispose();
    
    // Create bitmap with text
    int width = (int)Math.Ceiling(textSize.Width);
    int height = (int)Math.Ceiling(textSize.Height);
    bitmap = new Bitmap(width, height);
    graphics = Graphics.FromImage(bitmap);
    
    // High quality rendering
    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
    
    // Draw background
    graphics.Clear(backgroundColor);
    
    // Draw text
    Brush brush = new SolidBrush(textColor);
    graphics.DrawString(text, font, brush, 0, 0);
    
    graphics.Flush();
    graphics.Dispose();
    
    // Convert to OpenGL texture
    BitmapData data = bitmap.LockBits(
        new Rectangle(0, 0, bitmap.Width, bitmap.Height),
        ImageLockMode.ReadOnly,
        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    
    int textureId = GL.GenTexture();
    GL.BindTexture(TextureTarget.Texture2D, textureId);
    
    GL.TexImage2D(
        TextureTarget.Texture2D,
        0,
        PixelInternalFormat.Rgba,
        data.Width,
        data.Height,
        0,
        OpenTK.Graphics.OpenGL4.PixelFormat.Bgra,  // System.Drawing uses BGRA
        PixelType.UnsignedByte,
        data.Scan0);
    
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
    
    bitmap.UnlockBits(data);
    bitmap.Dispose();
    
    return textureId;
}
```

### Using Text Textures

```csharp
private Dictionary<string, int> textTextures = new Dictionary<string, int>();
private Font buttonFont;

protected override void OnLoad()
{
    base.OnLoad();
    
    // Create font
    buttonFont = new Font("Arial", 24, FontStyle.Bold);
    
    // Create text textures for each button
    textTextures["Single Player"] = CreateTextTexture("Single Player", buttonFont, Color.White, Color.Transparent);
    textTextures["Multiplayer"] = CreateTextTexture("Multiplayer", buttonFont, Color.White, Color.Transparent);
    textTextures["Exit"] = CreateTextTexture("Exit", buttonFont, Color.White, Color.Transparent);
}

private void RenderButton(MenuButton button, bool isHovered)
{
    float x = button.X - button.Width / 2f;
    float y = button.Y;
    float w = button.Width;
    float h = button.Height;
    
    // Draw button background (colored rectangle)
    float r = isHovered ? 0.3f : 0.2f;
    float g = isHovered ? 0.5f : 0.3f;
    float b = isHovered ? 0.7f : 0.4f;
    RenderColoredRect(x, y, w, h, r, g, b, 1.0f);
    
    // Draw text on top (centered)
    if (textTextures.ContainsKey(button.Label))
    {
        float textWidth = 200f;  // Adjust based on actual text size
        float textHeight = 40f;
        float textX = button.X - textWidth / 2f;
        float textY = button.Y + (button.Height - textHeight) / 2f;
        
        RenderTexturedRect(textX, textY, textWidth, textHeight, textTextures[button.Label]);
    }
}
```

---

## Adding Text (Bitmap Font Method)

This is the professional method used in most games - faster and more flexible.

### Step 1: Create a Font Atlas

A font atlas is a single image containing all characters:

```
[A][B][C][D][E][F]...
[a][b][c][d][e][f]...
[0][1][2][3][4][5]...
```

You can create one using tools like:
- **BMFont** (free, Windows)
- **Hiero** (free, Java-based)
- Or create programmatically with System.Drawing

### Step 2: Font Atlas Data Structure

```csharp
public class CharacterInfo
{
    public int X { get; set; }        // Position in atlas
    public int Y { get; set; }
    public int Width { get; set; }    // Character size
    public int Height { get; set; }
    public int XOffset { get; set; }  // Drawing offset
    public int YOffset { get; set; }
    public int XAdvance { get; set; } // How much to move cursor
}

public class BitmapFont
{
    public int AtlasTexture { get; set; }
    public int AtlasWidth { get; set; }
    public int AtlasHeight { get; set; }
    public Dictionary<char, CharacterInfo> Characters { get; set; } = new Dictionary<char, CharacterInfo>();
    
    public float MeasureString(string text)
    {
        float width = 0;
        foreach (char c in text)
        {
            if (Characters.ContainsKey(c))
                width += Characters[c].XAdvance;
        }
        return width;
    }
}
```

### Step 3: Create Font Atlas Programmatically

```csharp
private BitmapFont CreateBitmapFont(Font font, string characters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,!?-+")
{
    var bitmapFont = new BitmapFont();
    
    // Measure all characters
    Bitmap tempBitmap = new Bitmap(1, 1);
    Graphics tempGraphics = Graphics.FromImage(tempBitmap);
    
    int maxWidth = 0;
    int maxHeight = 0;
    
    foreach (char c in characters)
    {
        SizeF size = tempGraphics.MeasureString(c.ToString(), font);
        maxWidth = Math.Max(maxWidth, (int)Math.Ceiling(size.Width));
        maxHeight = Math.Max(maxHeight, (int)Math.Ceiling(size.Height));
    }
    
    tempGraphics.Dispose();
    tempBitmap.Dispose();
    
    // Calculate atlas size
    int charsPerRow = 16;
    int atlasWidth = maxWidth * charsPerRow;
    int atlasHeight = maxHeight * ((characters.Length + charsPerRow - 1) / charsPerRow);
    
    bitmapFont.AtlasWidth = atlasWidth;
    bitmapFont.AtlasHeight = atlasHeight;
    
    // Create atlas bitmap
    Bitmap atlasBitmap = new Bitmap(atlasWidth, atlasHeight);
    Graphics graphics = Graphics.FromImage(atlasBitmap);
    graphics.Clear(Color.Transparent);
    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
    
    // Draw each character
    int currentX = 0;
    int currentY = 0;
    
    foreach (char c in characters)
    {
        // Draw character
        graphics.DrawString(c.ToString(), font, Brushes.White, currentX, currentY);
        
        // Measure actual size
        SizeF size = graphics.MeasureString(c.ToString(), font);
        
        // Store character info
        bitmapFont.Characters[c] = new CharacterInfo
        {
            X = currentX,
            Y = currentY,
            Width = (int)Math.Ceiling(size.Width),
            Height = (int)Math.Ceiling(size.Height),
            XOffset = 0,
            YOffset = 0,
            XAdvance = (int)Math.Ceiling(size.Width)
        };
        
        // Move to next position
        currentX += maxWidth;
        if (currentX >= atlasWidth)
        {
            currentX = 0;
            currentY += maxHeight;
        }
    }
    
    graphics.Dispose();
    
    // Convert to OpenGL texture
    BitmapData data = atlasBitmap.LockBits(
        new Rectangle(0, 0, atlasBitmap.Width, atlasBitmap.Height),
        ImageLockMode.ReadOnly,
        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    
    int textureId = GL.GenTexture();
    GL.BindTexture(TextureTarget.Texture2D, textureId);
    
    GL.TexImage2D(
        TextureTarget.Texture2D,
        0,
        PixelInternalFormat.Rgba,
        data.Width,
        data.Height,
        0,
        OpenTK.Graphics.OpenGL4.PixelFormat.Bgra,
        PixelType.UnsignedByte,
        data.Scan0);
    
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
    
    atlasBitmap.UnlockBits(data);
    atlasBitmap.Dispose();
    
    bitmapFont.AtlasTexture = textureId;
    
    // Optionally save atlas for debugging
    // atlasBitmap.Save("font_atlas.png");
    
    return bitmapFont;
}
```

### Step 4: Render Text Using Bitmap Font

```csharp
private void RenderText(BitmapFont font, string text, float x, float y, float scale = 1.0f)
{
    GL.BindTexture(TextureTarget.Texture2D, font.AtlasTexture);
    
    int useTextureLoc = GL.GetUniformLocation(shaderProgram, "useTexture");
    GL.Uniform1(useTextureLoc, 1);
    
    float currentX = x;
    
    foreach (char c in text)
    {
        if (!font.Characters.ContainsKey(c))
            continue;
        
        CharacterInfo charInfo = font.Characters[c];
        
        // Calculate texture coordinates (normalized 0-1)
        float u1 = (float)charInfo.X / font.AtlasWidth;
        float v1 = (float)charInfo.Y / font.AtlasHeight;
        float u2 = (float)(charInfo.X + charInfo.Width) / font.AtlasWidth;
        float v2 = (float)(charInfo.Y + charInfo.Height) / font.AtlasHeight;
        
        // Calculate screen position
        float w = charInfo.Width * scale;
        float h = charInfo.Height * scale;
        float posX = currentX + charInfo.XOffset * scale;
        float posY = y + charInfo.YOffset * scale;
        
        // Create vertices
        float[] vertices = new float[]
        {
            // Pos              Color           TexCoord
            posX,     posY,     1f, 1f, 1f, 1f, u1, v1,
            posX + w, posY,     1f, 1f, 1f, 1f, u2, v1,
            posX + w, posY + h, 1f, 1f, 1f, 1f, u2, v2,
            
            posX,     posY,     1f, 1f, 1f, 1f, u1, v1,
            posX + w, posY + h, 1f, 1f, 1f, 1f, u2, v2,
            posX,     posY + h, 1f, 1f, 1f, 1f, u1, v2,
        };
        
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        
        // Advance cursor
        currentX += charInfo.XAdvance * scale;
    }
    
    GL.Uniform1(useTextureLoc, 0);
}
```

---

## Complete Example

Here's a complete menu with images and text:

```csharp
public class MenuWindow : GameWindow
{
    private int shaderProgram;
    private int vao, vbo;
    
    private int logoTexture;
    private BitmapFont buttonFont;
    private List<MenuButton> buttons;
    
    public MenuWindow() : base(GameWindowSettings.Default, 
        new NativeWindowSettings() { ClientSize = new Vector2i(1280, 720), Title = "Game Menu" })
    {
    }
    
    protected override void OnLoad()
    {
        base.OnLoad();
        
        // Setup OpenGL
        GL.ClearColor(0.1f, 0.1f, 0.15f, 1.0f);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        
        // Load shaders (with texture support)
        // ... shader compilation code from earlier ...
        
        // Setup VAO/VBO (8 floats per vertex: pos, color, texcoord)
        // ... VAO setup code from earlier ...
        
        // Load assets
        logoTexture = LoadTexture("textures/logo.png");
        buttonFont = CreateBitmapFont(new Font("Arial", 32, FontStyle.Bold));
        
        // Create buttons
        buttons = new List<MenuButton>
        {
            new MenuButton("Single Player", ClientSize.X / 2f, 350f, 300f, 60f, MenuResult.SinglePlayer),
            new MenuButton("Multiplayer", ClientSize.X / 2f, 430f, 300f, 60f, MenuResult.Multiplayer),
            new MenuButton("Exit", ClientSize.X / 2f, 510f, 300f, 60f, MenuResult.Exit)
        };
    }
    
    protected override void OnRenderFrame(FrameEventArgs e)
    {
        base.OnRenderFrame(e);
        
        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.UseProgram(shaderProgram);
        
        // Set projection
        var projection = Matrix4.CreateOrthographicOffCenter(0, ClientSize.X, ClientSize.Y, 0, -1, 1);
        int projLoc = GL.GetUniformLocation(shaderProgram, "projection");
        GL.UniformMatrix4(projLoc, false, ref projection);
        
        // Draw background gradient
        RenderGradientBackground();
        
        // Draw logo
        float logoW = 500f, logoH = 200f;
        RenderTexturedRect(ClientSize.X / 2f - logoW / 2f, 50f, logoW, logoH, logoTexture);
        
        // Draw buttons
        foreach (var button in buttons)
        {
            bool isHovered = button.Contains(MouseState.Position.X, MouseState.Position.Y);
            
            // Button background
            float x = button.X - button.Width / 2f;
            float y = button.Y;
            float r = isHovered ? 0.4f : 0.2f;
            float g = isHovered ? 0.6f : 0.3f;
            float b = isHovered ? 0.8f : 0.4f;
            RenderColoredRect(x, y, button.Width, button.Height, r, g, b, 0.9f);
            
            // Button text (centered)
            float textWidth = buttonFont.MeasureString(button.Label);
            float textX = button.X - textWidth / 2f;
            float textY = button.Y + (button.Height - 32f) / 2f;  // Assuming font height ~32
            RenderText(buttonFont, button.Label, textX, textY, 1.0f);
            
            // Border if hovered
            if (isHovered)
            {
                RenderBorder(x, y, button.Width, button.Height, 3f, 0.5f, 0.8f, 1.0f);
            }
        }
        
        SwapBuffers();
    }
}
```

---

## Performance Tips

1. **Cache text textures** - Don't recreate them every frame
2. **Use bitmap fonts for dynamic text** - Much faster than System.Drawing
3. **Batch rendering** - Draw all text at once instead of one character at a time
4. **Texture atlases** - Combine multiple images into one to reduce texture switches
5. **Disable text textures** when not needed - Less GPU memory

## Summary

- **Images**: Load with StbImageSharp → Create GL texture → Draw with texture coordinates
- **Text (Simple)**: Use System.Drawing → Convert to texture → Draw like an image
- **Text (Fast)**: Create font atlas → Render characters from atlas → Much faster for dynamic text

The bitmap font method is what professional games use because it's fast and flexible!
