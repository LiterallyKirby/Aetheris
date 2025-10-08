# OpenTK Menu System Guide for Absolute Beginners

## Table of Contents
1. [What is a Menu?](#what-is-a-menu)
2. [The Big Picture](#the-big-picture)
3. [Building Blocks](#building-blocks)
4. [Step-by-Step Breakdown](#step-by-step-breakdown)
5. [Common Patterns](#common-patterns)
6. [Troubleshooting](#troubleshooting)

---

## What is a Menu?

A menu in a game is just a **window that shows buttons and images** before the actual game starts. Think of it like a welcome screen where you can click "Start Game" or "Exit".

In OpenTK (a C# graphics library), we create menus the same way we create the game itself - by making a window and drawing shapes on it.

---

## The Big Picture

Here's how menus work at a high level:

```
1. Program starts
2. Open a MenuWindow
3. Draw buttons and background
4. Wait for user to click something
5. Close the menu
6. Start the game based on what they clicked
```

### Key Concept: The Game Loop

Both menus and games use something called a **game loop**:

```
while (window is open) {
    1. Check for input (mouse clicks, keyboard)
    2. Update stuff (button colors, animations)
    3. Draw everything to screen
    4. Repeat 60 times per second
}
```

This happens automatically when you call `menu.Run()` in OpenTK!

---

## Building Blocks

### 1. The Window Class

Every OpenTK menu starts by inheriting from `GameWindow`:

```csharp
public class MenuWindow : GameWindow
{
    // Your menu code goes here
}
```

**What does this give you?**
- A window that can open and close
- Automatic game loop (60 FPS by default)
- Methods you can override to add your own code

### 2. Important Methods You Override

Think of these as "hooks" - OpenTK calls them automatically at the right time:

```csharp
protected override void OnLoad()
{
    // Called ONCE when the window first opens
    // Setup shaders, create buttons, load textures
}

protected override void OnUpdateFrame(FrameEventArgs e)
{
    // Called every frame (60 times per second)
    // Check mouse position, handle clicks, update animations
}

protected override void OnRenderFrame(FrameEventArgs e)
{
    // Called every frame (60 times per second)
    // Draw everything to the screen
}

protected override void OnUnload()
{
    // Called ONCE when window closes
    // Clean up resources (delete shaders, buffers)
}
```

---

## Step-by-Step Breakdown

### Step 1: Creating the Window

```csharp
public MenuWindow()
    : base(GameWindowSettings.Default, new NativeWindowSettings()
    {
        ClientSize = new Vector2i(1280, 720),  // Window size
        Title = "My Awesome Menu"               // Window title
    })
{
}
```

**What's happening:**
- `base(...)` calls the parent GameWindow constructor
- We set the window size to 1280x720 pixels
- We set the title bar text

### Step 2: Setting Up Shaders (OnLoad)

**What are shaders?**
Shaders are small programs that run on your graphics card. They tell the GPU how to draw things.

You need TWO shaders:
1. **Vertex Shader**: Positions things on screen
2. **Fragment Shader**: Colors the pixels

```csharp
protected override void OnLoad()
{
    base.OnLoad();
    
    // Background color (dark blue)
    GL.ClearColor(0.1f, 0.1f, 0.15f, 1.0f);
    
    // Create vertex shader
    string vertexCode = @"
        #version 330 core
        layout(location = 0) in vec2 aPosition;  // Input: position
        layout(location = 1) in vec4 aColor;     // Input: color
        
        out vec4 fragColor;  // Output to fragment shader
        
        uniform mat4 projection;  // How to convert positions to screen space
        
        void main()
        {
            gl_Position = projection * vec4(aPosition, 0.0, 1.0);
            fragColor = aColor;
        }
    ";
    
    // Create fragment shader
    string fragmentCode = @"
        #version 330 core
        in vec4 fragColor;      // Input from vertex shader
        out vec4 finalColor;    // Output: final pixel color
        
        void main()
        {
            finalColor = fragColor;  // Just use the color we got
        }
    ";
    
    // Compile and link shaders (don't worry about details yet)
    int vertShader = GL.CreateShader(ShaderType.VertexShader);
    GL.ShaderSource(vertShader, vertexCode);
    GL.CompileShader(vertShader);
    
    int fragShader = GL.CreateShader(ShaderType.FragmentShader);
    GL.ShaderSource(fragShader, fragmentCode);
    GL.CompileShader(fragShader);
    
    shaderProgram = GL.CreateProgram();
    GL.AttachShader(shaderProgram, vertShader);
    GL.AttachShader(shaderProgram, fragShader);
    GL.LinkProgram(shaderProgram);
    
    // Clean up - we don't need these anymore
    GL.DeleteShader(vertShader);
    GL.DeleteShader(fragShader);
}
```

**In simple terms:**
- Vertex shader takes positions and colors and passes them through
- Fragment shader colors each pixel
- We compile these into a "shader program" we can use later

### Step 3: Creating Vertex Arrays and Buffers

**What are these?**
- **VBO (Vertex Buffer Object)**: Stores vertex data (positions, colors) on the GPU
- **VAO (Vertex Array Object)**: Remembers how to interpret that data

```csharp
// Create VAO and VBO
vao = GL.GenVertexArray();
vbo = GL.GenBuffer();

GL.BindVertexArray(vao);
GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);

// Tell OpenGL how to read our vertex data
// Position: 2 floats (x, y)
GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
GL.EnableVertexAttribArray(0);

// Color: 4 floats (r, g, b, a)
GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 6 * sizeof(float), 2 * sizeof(float));
GL.EnableVertexAttribArray(1);
```

**Breaking it down:**
- Each vertex has 6 floats: `[x, y, r, g, b, a]`
- Position is at index 0 (location 0 in shader)
- Color is at index 1 (location 1 in shader)
- `6 * sizeof(float)` is the "stride" - how many bytes until the next vertex

### Step 4: Creating Buttons

Let's make a simple button class:

```csharp
private class MenuButton
{
    public string Label { get; }
    public float X { get; }      // Center X position
    public float Y { get; }      // Top Y position
    public float Width { get; }
    public float Height { get; }
    public MenuResult Action { get; }  // What happens when clicked
    
    public MenuButton(string label, float x, float y, float width, float height, MenuResult action)
    {
        Label = label;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Action = action;
    }
    
    // Check if mouse is over this button
    public bool Contains(float mouseX, float mouseY)
    {
        float left = X - Width / 2f;   // Button is centered on X
        float right = X + Width / 2f;
        float top = Y;
        float bottom = Y + Height;
        
        return mouseX >= left && mouseX <= right && 
               mouseY >= top && mouseY <= bottom;
    }
}
```

**Creating buttons in OnLoad:**

```csharp
buttons = new List<MenuButton>();

float centerX = ClientSize.X / 2f;  // Center of screen
float startY = ClientSize.Y / 2f;   // Middle height
float buttonWidth = 300f;
float buttonHeight = 60f;
float spacing = 80f;  // Space between buttons

buttons.Add(new MenuButton("Single Player", centerX, startY, buttonWidth, buttonHeight, MenuResult.SinglePlayer));
buttons.Add(new MenuButton("Multiplayer", centerX, startY + spacing, buttonWidth, buttonHeight, MenuResult.Multiplayer));
buttons.Add(new MenuButton("Exit", centerX, startY + spacing * 2, buttonWidth, buttonHeight, MenuResult.Exit));
```

### Step 5: Handling Input (OnUpdateFrame)

```csharp
protected override void OnUpdateFrame(FrameEventArgs e)
{
    base.OnUpdateFrame(e);
    
    // Get mouse position
    var mousePos = MouseState.Position;
    
    // Check which button the mouse is over
    hoveredButtonIndex = -1;  // -1 means no button hovered
    for (int i = 0; i < buttons.Count; i++)
    {
        if (buttons[i].Contains(mousePos.X, mousePos.Y))
        {
            hoveredButtonIndex = i;
            break;
        }
    }
    
    // Check if left mouse button was clicked
    if (MouseState.IsButtonPressed(MouseButton.Left) && hoveredButtonIndex >= 0)
    {
        // Set the result based on which button was clicked
        Result = buttons[hoveredButtonIndex].Action;
        Close();  // Close the menu window
    }
    
    // ESC key also exits
    if (KeyboardState.IsKeyPressed(Keys.Escape))
    {
        Result = MenuResult.Exit;
        Close();
    }
}
```

**What's happening:**
1. Every frame, check where the mouse is
2. See if it's hovering over any button
3. If mouse is clicked and over a button, close the window with that result

### Step 6: Drawing Everything (OnRenderFrame)

```csharp
protected override void OnRenderFrame(FrameEventArgs e)
{
    base.OnRenderFrame(e);
    
    // Clear the screen
    GL.Clear(ClearBufferMask.ColorBufferBit);
    
    // Activate our shader
    GL.UseProgram(shaderProgram);
    
    // Set up 2D projection (screen coordinates)
    var projection = Matrix4.CreateOrthographicOffCenter(
        0, ClientSize.X,      // Left to right: 0 to window width
        ClientSize.Y, 0,      // Top to bottom: window height to 0
        -1, 1                 // Near and far (doesn't matter for 2D)
    );
    int projLoc = GL.GetUniformLocation(shaderProgram, "projection");
    GL.UniformMatrix4(projLoc, false, ref projection);
    
    // Draw background
    RenderBackground();
    
    // Draw all buttons
    for (int i = 0; i < buttons.Count; i++)
    {
        bool isHovered = (i == hoveredButtonIndex);
        RenderButton(buttons[i], isHovered);
    }
    
    // Show what we drew
    SwapBuffers();
}
```

### Step 7: Drawing a Rectangle

This is the fundamental building block - everything is made of rectangles!

```csharp
private void RenderRect(float x, float y, float w, float h, float r, float g, float b, float a)
{
    // A rectangle is made of 2 triangles (6 vertices total)
    // Triangle 1: top-left, top-right, bottom-right
    // Triangle 2: top-left, bottom-right, bottom-left
    
    float[] vertices = new float[]
    {
        // First triangle
        // X    Y          R    G    B    A
        x,      y,         r,   g,   b,   a,  // Top-left
        x + w,  y,         r,   g,   b,   a,  // Top-right
        x + w,  y + h,     r,   g,   b,   a,  // Bottom-right
        
        // Second triangle
        x,      y,         r,   g,   b,   a,  // Top-left
        x + w,  y + h,     r,   g,   b,   a,  // Bottom-right
        x,      y + h,     r,   g,   b,   a,  // Bottom-left
    };
    
    // Upload to GPU
    GL.BindVertexArray(vao);
    GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
    GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);
    
    // Draw it!
    GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
}
```

**Why triangles?**
GPUs only understand triangles. Everything you see in 3D games is made of triangles!

### Step 8: Drawing a Button

```csharp
private void RenderButton(MenuButton button, bool isHovered)
{
    // Calculate position (button is centered on X)
    float x = button.X - button.Width / 2f;
    float y = button.Y;
    float w = button.Width;
    float h = button.Height;
    
    // Choose color based on hover state
    float r = isHovered ? 0.3f : 0.2f;  // Brighter when hovered
    float g = isHovered ? 0.5f : 0.3f;
    float b = isHovered ? 0.7f : 0.4f;
    float a = isHovered ? 1.0f : 0.8f;  // More opaque when hovered
    
    // Draw the button rectangle
    RenderRect(x, y, w, h, r, g, b, a);
    
    // Draw border if hovered
    if (isHovered)
    {
        float borderThickness = 3f;
        float br = 0.5f, bg = 0.7f, bb = 1.0f;  // Light blue
        
        // Top border
        RenderRect(x, y, w, borderThickness, br, bg, bb, 1.0f);
        // Bottom border
        RenderRect(x, y + h - borderThickness, w, borderThickness, br, bg, bb, 1.0f);
        // Left border
        RenderRect(x, y, borderThickness, h, br, bg, bb, 1.0f);
        // Right border
        RenderRect(x + w - borderThickness, y, borderThickness, h, br, bg, bb, 1.0f);
    }
}
```

---

## Common Patterns

### Pattern 1: Animated Background

Add a timer and use sin/cos for smooth animation:

```csharp
private float time = 0f;

protected override void OnUpdateFrame(FrameEventArgs e)
{
    base.OnUpdateFrame(e);
    time += (float)e.Time;  // e.Time is seconds since last frame
}

protected override void OnRenderFrame(FrameEventArgs e)
{
    // Use sin wave for pulsing effect (goes from 0 to 1)
    float pulse = MathF.Sin(time * 2f) * 0.5f + 0.5f;
    
    // Make background color pulse
    float r = 0.1f + pulse * 0.05f;
    float g = 0.1f + pulse * 0.05f;
    float b = 0.15f + pulse * 0.05f;
    
    RenderRect(0, 0, ClientSize.X, ClientSize.Y, r, g, b, 1.0f);
}
```

### Pattern 2: Button Hover Animation

```csharp
private void RenderButton(MenuButton button, bool isHovered)
{
    // ... position code ...
    
    // Pulsing animation when hovered
    float pulse = 0f;
    if (isHovered)
    {
        pulse = MathF.Sin(time * 5f) * 0.1f;  // Fast pulse
    }
    
    float r = (isHovered ? 0.3f : 0.2f) + pulse;
    float g = (isHovered ? 0.5f : 0.3f) + pulse;
    float b = (isHovered ? 0.7f : 0.4f) + pulse;
    
    // ... draw code ...
}
```

### Pattern 3: Gradient Background

```csharp
private void RenderGradient(float x, float y, float w, float h)
{
    // Different colors for each corner
    float[] vertices = new float[]
    {
        // X      Y        R     G     B     A
        x,        y,       0.1f, 0.1f, 0.2f, 1.0f,  // Top-left (dark)
        x + w,    y,       0.2f, 0.1f, 0.3f, 1.0f,  // Top-right (purple)
        x + w,    y + h,   0.1f, 0.2f, 0.3f, 1.0f,  // Bottom-right (blue)
        
        x,        y,       0.1f, 0.1f, 0.2f, 1.0f,  // Top-left
        x + w,    y + h,   0.1f, 0.2f, 0.3f, 1.0f,  // Bottom-right
        x,        y + h,   0.2f, 0.1f, 0.2f, 1.0f,  // Bottom-left (purple)
    };
    
    // GPU automatically blends between the colors!
    GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);
    GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
}
```

### Pattern 4: Returning Menu Result

```csharp
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
    
    // ... in OnUpdateFrame ...
    if (buttonClicked)
    {
        Result = buttons[hoveredButtonIndex].Action;
        Close();
    }
}

// In your main program:
using (var menu = new MenuWindow())
{
    menu.Run();  // Blocks until window closes
    
    switch (menu.Result)
    {
        case MenuResult.SinglePlayer:
            StartGame();
            break;
        case MenuResult.Exit:
            return;
    }
}
```

---

## Troubleshooting

### Nothing appears on screen
- Check that `GL.ClearColor()` is set
- Make sure `GL.Clear()` is called in `OnRenderFrame`
- Verify `SwapBuffers()` is called at the end of `OnRenderFrame`
- Check shader compilation (add error checking)

### Buttons don't respond to clicks
- Print mouse position to console: `Console.WriteLine($"Mouse: {MouseState.Position}")`
- Print button bounds: `Console.WriteLine($"Button: {x}, {y}, {w}, {h}")`
- Make sure you're using `IsButtonPressed` not `IsButtonDown` (pressed = clicked this frame)

### Shader errors
Add error checking after compiling:

```csharp
GL.CompileShader(vertShader);
string infoLog = GL.GetShaderInfoLog(vertShader);
if (!string.IsNullOrEmpty(infoLog))
    Console.WriteLine("Vertex Shader Error: " + infoLog);
```

### Colors look wrong
- Colors are 0.0 to 1.0, not 0 to 255
- Make sure alpha (A) is 1.0 for fully opaque
- Enable blending: `GL.Enable(EnableCap.Blend)`

### Performance issues
- Don't create new buffers every frame - reuse them
- Use `BufferUsageHint.DynamicDraw` for frequently updated data
- Use `BufferUsageHint.StaticDraw` for data that doesn't change

---

## Next Steps

Once you understand this basic menu, you can:
1. **Add text rendering** using a font texture atlas
2. **Add images** by loading textures
3. **Create sub-menus** (settings, options)
4. **Add sound effects** when clicking buttons
5. **Create fancier animations** (sliding, fading)
6. **Add keyboard navigation** (arrow keys + Enter)

The principles are the same - you're just drawing more rectangles with different textures and colors!

---

## Key Takeaways

1. **Everything is triangles** - Rectangles are just 2 triangles
2. **Shaders are programs** that run on the GPU
3. **Game loop** = Update → Draw → Repeat
4. **Coordinates** in OpenTK: (0,0) is top-left, X increases right, Y increases down
5. **Colors** are 0.0 to 1.0 floats (Red, Green, Blue, Alpha)
6. **Buffers** store data on the GPU for fast rendering
7. **Input checking** happens every frame in `OnUpdateFrame`

You've now learned the fundamentals of creating game menus! The same concepts apply to any GUI in OpenTK.
