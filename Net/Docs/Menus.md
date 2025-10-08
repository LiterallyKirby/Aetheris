# OpenTK Menu & UI System Guide for Absolute Beginners

## Table of Contents
1. [What is a Menu?](#what-is-a-menu)
2. [The Big Picture](#the-big-picture)
3. [Building Blocks](#building-blocks)
4. [Step-by-Step Breakdown](#step-by-step-breakdown)
5. [UI Manager System](#ui-manager-system)
6. [Text Rendering with Fonts](#text-rendering-with-fonts)
7. [Advanced UI Components](#advanced-ui-components)
8. [Creating an In-Game Inventory](#creating-an-in-game-inventory)
9. [Common Patterns](#common-patterns)
10. [Troubleshooting](#troubleshooting)

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
        ClientSize = new Vector2i(1920, 1080),  // Window size
        Title = "My Awesome Menu"               // Window title
    })
{
}
```

**What's happening:**
- `base(...)` calls the parent GameWindow constructor
- We set the window size to 1920x1080 pixels
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
    
    // Enable blending for transparency
    GL.Enable(EnableCap.Blend);
    GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    
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
    
    // Compile and link shaders
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
    
    // Clean up
    GL.DeleteShader(vertShader);
    GL.DeleteShader(fragShader);
}
```

**In simple terms:**
- Vertex shader takes positions and colors and passes them through
- Fragment shader colors each pixel
- We compile these into a "shader program" we can use later
- **Blending is enabled** so transparent UI elements work properly

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

---

## UI Manager System

Instead of manually managing buttons and input, we use a **UIManager** system that handles everything for us!

### What is UIManager?

UIManager is a helper class that:
- Tracks all UI elements (buttons, labels, text inputs)
- Handles mouse and keyboard input automatically
- Renders all elements in the correct order
- Manages focus (which element is selected)

### Setting Up UIManager

```csharp
protected override void OnLoad()
{
    base.OnLoad();
    
    // ... shader and buffer setup ...
    
    // Create UI Manager
    uiManager = new UIManager(this, shaderProgram, vao, vbo);
    
    // Load font for text rendering
    try
    {
        fontRenderer = new FontRenderer("assets/font.ttf", 32);
        uiManager.TextRenderer = fontRenderer;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Could not load font: {ex.Message}");
    }
}
```

### Using UIManager in Game Loop

```csharp
protected override void OnUpdateFrame(FrameEventArgs e)
{
    base.OnUpdateFrame(e);
    
    // UIManager handles ALL input automatically!
    uiManager?.Update(MouseState, KeyboardState, (float)e.Time);
}

protected override void OnRenderFrame(FrameEventArgs e)
{
    base.OnRenderFrame(e);
    
    GL.Clear(ClearBufferMask.ColorBufferBit);
    
    // Set up projection
    var projection = Matrix4.CreateOrthographicOffCenter(
        0, ClientSize.X,
        ClientSize.Y, 0,
        -1, 1
    );
    
    // UIManager renders ALL elements automatically!
    uiManager?.Render(projection);
    
    SwapBuffers();
}
```

**What just happened?**
- `Update()` checks mouse position, clicks, and keyboard for ALL UI elements
- `Render()` draws ALL UI elements with proper colors and text
- You don't need to write any input or rendering code!

### Creating UI Elements

Now creating buttons is super easy:

```csharp
private void CreateUI()
{
    float centerX = ClientSize.X / 2f;
    float centerY = ClientSize.Y / 2f;
    
    // Create a button
    var startButton = new Button("Start Game")
    {
        Position = new Vector2(centerX - 160, centerY),
        Size = new Vector2(320, 65),
        LabelScale = 1.2f,
        CornerRadius = 8f,
        Color = new Vector4(0.25f, 0.35f, 0.5f, 0.92f),
        HoverColor = new Vector4(0.35f, 0.5f, 0.75f, 1f)
    };
    
    // Set what happens when clicked
    startButton.OnPressed = () => {
        Result = MenuResult.SinglePlayer;
        Close();
    };
    
    // Add to UI Manager - it handles everything else!
    uiManager.AddElement(startButton);
}
```

**Benefits:**
- No manual input checking needed
- Automatic hover effects
- Built-in animations
- Consistent styling

---

## Text Rendering with Fonts

### What is FontRenderer?

FontRenderer uses the **SixLabors.ImageSharp** library to:
1. Load TrueType font files (.ttf)
2. Render each character to a texture
3. Draw text on screen with proper spacing and alignment

### How It Works

```csharp
// 1. Load font file
fontRenderer = new FontRenderer("assets/font.ttf", 32);

// 2. Set as UI Manager's text renderer
uiManager.TextRenderer = fontRenderer;

// 3. Now ALL UI elements can display text!
```

### Behind the Scenes

For each character (A-Z, 0-9, symbols):
1. **Generate a bitmap** of the character using the font
2. **Upload to GPU** as a texture
3. **Store metrics**: width, height, baseline offset, advance
4. When drawing text, **render quads** with these textures

### Important Glyph Metrics

```csharp
public struct CharGlyph
{
    public int TextureId;      // OpenGL texture ID
    public Vector2 Size;       // Pixel size of the character
    public Vector2 Bearing;    // Offset from baseline
    public float Advance;      // How far to move for next character
}
```

**Bearing explained:**
- `Bearing.X`: Horizontal offset from pen position (can be negative)
- `Bearing.Y`: Distance from baseline to top of glyph
- **Baseline**: The invisible line that letters sit on (like ruled paper)

### Text Coordinate System

Your projection uses **Y=0 at top, Y increases downward** (screen coordinates):
```csharp
Matrix4.CreateOrthographicOffCenter(0, width, height, 0, -1, 1);
```

This means:
- Position.Y is the **baseline** of the text
- Characters are drawn upward from the baseline
- Descenders (like 'g', 'y', 'p') go below the baseline

### Drawing Text

```csharp
// Draw text at position
fontRenderer.DrawText("Hello World", new Vector2(100, 200), scale: 1.0f, color: new Vector4(1, 1, 1, 1));

// Measure text before drawing (for centering)
var textSize = fontRenderer.MeasureText("Hello World", 1.0f);
float centerX = (screenWidth - textSize.X) / 2f;
```

### Common Text Issues and Solutions

**Problem: Text is cut off at the edges**
- **Solution**: Increase padding in `GenerateGlyph()`:
  ```csharp
  int padding = 8;  // Try 4-10 depending on font
  ```

**Problem: Text baseline is wrong**
- **Solution**: Make sure your projection matrix matches the bearing calculations
- For top-left origin: `ypos = y - glyph.Bearing.Y * scale`

**Problem: Characters overlap or have gaps**
- **Solution**: Check that `Advance` is being used correctly
- Each character should move the pen by `glyph.Advance * scale`

### Font File Requirements

- Use **.ttf** (TrueType) fonts
- Place in `assets/` folder
- Mark as "Copy to Output Directory" in Visual Studio
- Recommended fonts for UI: Roboto, Open Sans, Arial

---

## Advanced UI Components

### Button Component

Full-featured button with hover, press, and focus states:

```csharp
var button = new Button("Click Me")
{
    Position = new Vector2(100, 100),
    Size = new Vector2(200, 50),
    LabelScale = 1.0f,
    TextAlign = TextAlign.Center,
    
    // Colors
    Color = new Vector4(0.2f, 0.3f, 0.45f, 0.95f),
    HoverColor = new Vector4(0.28f, 0.42f, 0.7f, 1f),
    PressedColor = new Vector4(0.15f, 0.25f, 0.4f, 1f),
    TextColor = new Vector4(1, 1, 1, 1),
    BorderColor = new Vector4(0.7f, 0.8f, 1f, 1f),
    
    // Style
    CornerRadius = 4f,
    ShowBorder = true,
    
    // Spacing
    Padding = new Spacing(10, 20, 10, 20)  // top, right, bottom, left
};

button.OnPressed = () => {
    Console.WriteLine("Button clicked!");
};

uiManager.AddElement(button);
```

### Label Component

Display text with alignment options:

```csharp
var titleLabel = new Label("Game Title")
{
    Position = new Vector2(100, 50),
    Size = new Vector2(400, 60),
    Scale = 2.0f,
    Color = new Vector4(1, 1, 1, 1),
    TextAlign = TextAlign.Center,
    VerticalAlign = VerticalAlign.Middle
};

uiManager.AddElement(titleLabel);
```

### TextInput Component

Interactive text field with cursor and keyboard support:

```csharp
var nameInput = new TextInput()
{
    Position = new Vector2(100, 150),
    Size = new Vector2(300, 40),
    Placeholder = "Enter your name...",
    MaxLength = 20,
    LabelScale = 1.0f,
    
    BackgroundColor = new Vector4(0.08f, 0.08f, 0.1f, 0.95f),
    BorderColor = new Vector4(0.4f, 0.6f, 0.9f, 1f),
    FocusedBorderColor = new Vector4(0.5f, 0.7f, 1f, 1f)
};

// Handle when user presses Enter
nameInput.OnEnterPressed = (text) => {
    Console.WriteLine($"User entered: {text}");
};

// Handle when text changes
nameInput.OnTextChanged = (text) => {
    Console.WriteLine($"Current text: {text}");
};

uiManager.AddElement(nameInput);
```

**Features:**
- Blinking cursor
- Backspace/Delete support
- Arrow key navigation (Left/Right/Home/End)
- Tab to cycle between inputs
- Automatic focus management

### Panel Component

Container for grouping elements:

```csharp
var panel = new Panel()
{
    Position = new Vector2(50, 50),
    Size = new Vector2(400, 300),
    BackgroundColor = new Vector4(0.15f, 0.15f, 0.2f, 0.9f),
    BorderColor = new Vector4(0.3f, 0.3f, 0.4f, 1f),
    BorderThickness = 2f,
    CornerRadius = 8f,
    ShowBorder = true
};

uiManager.AddElement(panel);
```

### Checkbox Component

Toggle option with label:

```csharp
var checkbox = new Checkbox("Enable Sound")
{
    Position = new Vector2(100, 200),
    Size = new Vector2(200, 30),
    Checked = true,
    CheckboxSize = 20f,
    LabelScale = 1.0f
};

checkbox.OnChanged = (isChecked) => {
    Console.WriteLine($"Sound: {(isChecked ? "On" : "Off")}");
};

uiManager.AddElement(checkbox);
```

### Slider Component

Adjustable value slider:

```csharp
var volumeSlider = new Slider("Volume")
{
    Position = new Vector2(100, 250),
    Size = new Vector2(200, 30),
    Value = 0.5f,  // 0.0 to 1.0
    Min = 0f,
    Max = 100f,
    ShowValue = true,
    TrackHeight = 6f,
    HandleSize = 16f
};

volumeSlider.OnValueChanged = (value) => {
    Console.WriteLine($"Volume: {value}");
};

uiManager.AddElement(volumeSlider);
```

---

## Creating an In-Game Inventory

### Inventory System Overview

An inventory system needs:
1. **Data model** - Store items and their properties
2. **UI representation** - Visual grid of slots
3. **Interaction** - Click to select, drag to move (future)
4. **Management** - Add/remove items, stack items

### Step 1: Define Items

```csharp
public class InventoryItem
{
    public string Name { get; set; }
    public string Description { get; set; }
    public int Quantity { get; set; } = 1;
    public int MaxStack { get; set; } = 99;
    
    public InventoryItem(string name, int quantity = 1)
    {
        Name = name;
        Quantity = quantity;
        Description = "";
    }
}
```

### Step 2: Create Inventory Slots

```csharp
public class InventorySlot : UIElement
{
    public InventoryItem? Item { get; set; }
    public int SlotIndex { get; set; }
    public bool IsSelected { get; set; }
    
    public override void Render()
    {
        // Draw slot background
        var bgColor = IsSelected ? SelectedColor : (IsHovered ? HoverColor : BackgroundColor);
        Manager.DrawRect(Position.X, Position.Y, Size.X, Size.Y, bgColor);
        Manager.DrawBorder(Position.X, Position.Y, Size.X, Size.Y, 2f, BorderColor);
        
        // Draw item if present
        if (Item != null && Manager.TextRenderer != null)
        {
            // Item name
            Manager.TextRenderer.DrawText(Item.Name, Position + new Vector2(5, 5), 0.8f);
            
            // Quantity
            if (Item.Quantity > 1)
            {
                string qty = Item.Quantity.ToString();
                var qtyPos = new Vector2(Position.X + Size.X - 20, Position.Y + Size.Y - 20);
                Manager.TextRenderer.DrawText(qty, qtyPos, 0.8f);
            }
        }
    }
}
```

### Step 3: Build Inventory UI

```csharp
public class InventoryUI
{
    private UIManager uiManager;
    private List<InventorySlot> slots = new List<InventorySlot>();
    private List<InventoryItem> inventory = new List<InventoryItem>();
    
    public int Columns { get; set; } = 8;
    public int Rows { get; set; } = 4;
    public bool IsVisible { get; private set; }
    
    public void Initialize(float screenWidth, float screenHeight)
    {
        // Create background panel
        float panelWidth = 720;
        float panelHeight = 480;
        float panelX = (screenWidth - panelWidth) / 2f;
        float panelY = (screenHeight - panelHeight) / 2f;
        
        var panel = new Panel
        {
            Position = new Vector2(panelX, panelY),
            Size = new Vector2(panelWidth, panelHeight),
            BackgroundColor = new Vector4(0.1f, 0.1f, 0.15f, 0.96f)
        };
        
        // Create grid of slots
        float slotSize = 80f;
        float spacing = 10f;
        float startX = panelX + 20;
        float startY = panelY + 70;
        
        for (int row = 0; row < Rows; row++)
        {
            for (int col = 0; col < Columns; col++)
            {
                int index = row * Columns + col;
                var slot = new InventorySlot(index)
                {
                    Position = new Vector2(
                        startX + col * (slotSize + spacing),
                        startY + row * (slotSize + spacing)
                    ),
                    Size = new Vector2(slotSize, slotSize)
                };
                slot.OnSlotClicked = OnSlotClicked;
                slots.Add(slot);
            }
        }
    }
    
    public void Show()
    {
        IsVisible = true;
        // Add all elements to UI manager
        foreach (var slot in slots)
        {
            uiManager.AddElement(slot);
        }
    }
    
    public void Hide()
    {
        IsVisible = false;
        // Remove all elements from UI manager
        foreach (var slot in slots)
        {
            uiManager.RemoveElement(slot);
        }
    }
    
    public void Toggle()
    {
        if (IsVisible) Hide();
        else Show();
    }
}
```

### Step 4: Integrate with Game

```csharp
public class GameWindow : OpenTK.Windowing.Desktop.GameWindow
{
    private UIManager uiManager;
    private FontRenderer fontRenderer;
    private InventoryUI inventoryUI;
    
    protected override void OnLoad()
    {
        base.OnLoad();
        
        // Setup UI
        uiManager = new UIManager(this, shaderProgram, vao, vbo);
        fontRenderer = new FontRenderer("assets/font.ttf", 24);
        uiManager.TextRenderer = fontRenderer;
        
        // Create inventory
        inventoryUI = new InventoryUI(uiManager);
        inventoryUI.Initialize(ClientSize.X, ClientSize.Y);
        
        // Add some test items
        inventoryUI.AddItem(new InventoryItem("Health Potion", 5));
        inventoryUI.AddItem(new InventoryItem("Iron Sword", 1));
        inventoryUI.AddItem(new InventoryItem("Gold Coin", 50));
    }
    
    protected override void OnUpdateFrame(FrameEventArgs e)
    {
        base.OnUpdateFrame(e);
        
        // Toggle inventory with 'I' key
        if (KeyboardState.IsKeyPressed(Keys.I))
        {
            inventoryUI.Toggle();
        }
        
        // Update UI
        uiManager.Update(MouseState, KeyboardState, (float)e.Time);
    }
}
```

### Managing Inventory Items

```csharp
// Add item (auto-stacks if possible)
bool added = inventoryUI.AddItem(new InventoryItem("Arrow", 20));
if (!added)
{
    Console.WriteLine("Inventory full!");
}

// Remove item
bool removed = inventoryUI.RemoveItem("Arrow", 5);
if (removed)
{
    Console.WriteLine("Removed 5 arrows");
}

// Check if player has item
if (inventoryUI.HasItem("Health Potion", 3))
{
    Console.WriteLine("Has at least 3 potions");
}

// Get total count
int arrows = inventoryUI.GetItemCount("Arrow");
Console.WriteLine($"Player has {arrows} arrows");

// Clear everything
inventoryUI.ClearInventory();
```

---

## Common Patterns

### Pattern 1: Layout Helpers

Center elements automatically:

```csharp
var button = new Button("Start");
button.Size = new Vector2(200, 50);

// Center horizontally
LayoutHelper.CenterHorizontally(button, ClientSize.X);

// Center vertically
LayoutHelper.CenterVertically(button, ClientSize.Y);

// Center both
LayoutHelper.Center(button, ClientSize.X, ClientSize.Y);
```

Stack elements vertically:

```csharp
var buttons = new List<UIElement> { button1, button2, button3 };
LayoutHelper.VerticalStack(buttons, startY: 200, spacing: 20);
```

### Pattern 2: Spacing and Padding

```csharp
// Uniform padding (all sides)
button.Padding = new Spacing(10);

// Vertical and horizontal
button.Padding = new Spacing(vertical: 10, horizontal: 20);

// Individual sides
button.Padding = new Spacing(top: 10, right: 15, bottom: 10, left: 15);
```

### Pattern 3: Gradient Backgrounds

```csharp
protected override void OnRenderFrame(FrameEventArgs e)
{
    var topColor = new Vector4(0.1f, 0.1f, 0.2f, 1f);
    var bottomColor = new Vector4(0.2f, 0.1f, 0.3f, 1f);
    
    uiManager.DrawGradientRect(0, 0, ClientSize.X, ClientSize.Y, topColor, bottomColor);
    
    uiManager.Render(projection);
}
```

### Pattern 4: Animated Background

```csharp
private float time = 0f;

protected override void OnUpdateFrame(FrameEventArgs e)
{
    time += (float)e.Time;
}

protected override void OnRenderFrame(FrameEventArgs e)
{
    // Pulsing stars
    for (int i = 0; i < 15; i++)
    {
        float offsetX = MathF.Sin(time * 0.3f + i * 0.5f) * 50f;
        float offsetY = MathF.Cos(time * 0.2f + i * 0.7f) * 30f;
        float x = 100f + i * 120f + offsetX;
        float y = 80f + (i % 3) * 300f + offsetY;
        float size = 3f + MathF.Sin(time * 2f + i) * 1.5f;
        float brightness = 0.3f + MathF.Sin(time * 3f + i * 0.3f) * 0.2f;
        
        uiManager.DrawRect(x, y, size, size, 
            new Vector4(brightness, brightness + 0.1f, brightness + 0.2f, 0.6f));
    }
}
```

### Pattern 5: Handling Window Resize

```csharp
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
    
    // Reinitialize inventory
    if (inventoryUI != null)
    {
        inventoryUI.Initialize(e.Width, e.Height);
    }
}
```

---

## Troubleshooting

### Text Issues

**Text is cut off or missing parts**
- Increase padding in `FontRenderer.GenerateGlyph()`:
  ```csharp
  int padding = 8;  // Try values between 4-12
  ```
- Check that font file exists and is copied to output

**Text baseline is wrong**
- Verify projection matrix matches coordinate system
- For top-left origin: `ypos = y - glyph.Bearing.Y * scale`

**Text is blurry**
- Use higher font size when loading: `new FontRenderer("font.ttf", 48)`
- Scale down when drawing instead of scaling up

### UI Manager Issues

**Elements don't respond to clicks**
- Make sure `uiManager.Update()` is called in `OnUpdateFrame`
- Check that elements have `Visible = true`
- Verify projection matrix matches window size

**Elements render in wrong order**
- Elements are drawn in the order they're added
- Add background panels first, buttons last
- Or remove and re-add to change order

**Focus not working**
- Set `CanFocus = true` for interactive elements
- Only one element can be focused at a time
- Tab key cycles through focusable elements

### Performance Issues

**Slow rendering with many UI elements**
- Use panels to group elements
- Hide elements that aren't visible: `element.Visible = false`
- Don't create new elements every frame

**Text rendering is slow**
- FontRenderer caches glyphs - first render is slow, rest are fast
- Don't create new FontRenderer instances every frame
- Reuse the same FontRenderer for all text

### Inventory Issues

**Items not stacking correctly**
- Check `MaxStack` property on items
- Verify `AddItem()` checks for existing stacks first
- Make sure item names match exactly (case-sensitive)

**Slots not updating**
- Call `RefreshSlots()` after adding/removing items
- Make sure `InventorySlot.Item` is being set correctly

---

## Key Takeaways

### Core Concepts
1. **UI Manager** handles all input and rendering automatically
2. **Font Renderer** loads fonts and draws text with proper metrics
3. **UI Elements** inherit from `UIElement` base class
4. **Event-driven** programming - use callbacks like `OnPressed`, `OnChanged`

### Text Rendering
5. **Glyphs** are individual character textures with metrics
6. **Baseline** is the invisible line text sits on
7. **Padding** prevents glyphs from being cut off
8. **Advance** controls horizontal spacing between characters

### UI Components
9. **Buttons** have hover, pressed, and focus states
10. **Labels** support alignment (left, center, right, top, middle, bottom)
11. **TextInput** handles keyboard events automatically
12. **Panels** group related elements together
13. **Checkboxes** and **Sliders** provide interactive controls

### Inventory System
14. **Item stacking** consolidates identical items
15. **Slot-based** layout uses a grid of InventorySlot elements
16. **Show/Hide** toggles inventory visibility without recreating elements
17. **Selection** highlights the currently selected slot

### Best Practices
18. **Reuse resources** - don't create new shaders/fonts each frame
19. **Enable blending** for transparent UI elements
20. **Use padding** to prevent text cutoff
21. **Cache measurements** when possible (e.g., text size)
22. **Clean up** in `OnUnload()` - dispose fonts, delete buffers
23. **Event-driven** - use callbacks instead of polling
24. **Layout helpers** simplify positioning and alignment

---

## Advanced Topics

### Custom UI Elements

Create your own UI components by inheriting from `UIElement`:

```csharp
public class HealthBar : UIElement
{
    public float CurrentHealth { get; set; } = 100f;
    public float MaxHealth { get; set; } = 100f;
    
    public Vector4 BackgroundColor { get; set; } = new Vector4(0.2f, 0.2f, 0.2f, 0.9f);
    public Vector4 HealthColor { get; set; } = new Vector4(0.8f, 0.2f, 0.2f, 1f);
    public Vector4 BorderColor { get; set; } = new Vector4(0.5f, 0.5f, 0.5f, 1f);
    
    public override void Render()
    {
        if (Manager == null) return;
        
        // Background
        Manager.DrawRect(Position.X, Position.Y, Size.X, Size.Y, BackgroundColor);
        
        // Health fill
        float fillWidth = (CurrentHealth / MaxHealth) * Size.X;
        Manager.DrawRect(Position.X, Position.Y, fillWidth, Size.Y, HealthColor);
        
        // Border
        Manager.DrawBorder(Position.X, Position.Y, Size.X, Size.Y, 2f, BorderColor);
        
        // Text
        if (Manager.TextRenderer != null)
        {
            string text = $"{CurrentHealth:F0} / {MaxHealth:F0}";
            var textSize = Manager.TextRenderer.MeasureText(text, 0.8f);
            var textPos = new Vector2(
                Position.X + (Size.X - textSize.X) / 2f,
                Position.Y + (Size.Y - textSize.Y) / 2f
            );
            Manager.TextRenderer.DrawText(text, textPos, 0.8f, new Vector4(1, 1, 1, 1));
        }
    }
}
```

Usage:

```csharp
var healthBar = new HealthBar()
{
    Position = new Vector2(20, 20),
    Size = new Vector2(200, 30),
    CurrentHealth = 75f,
    MaxHealth = 100f
};

uiManager.AddElement(healthBar);

// Update health
healthBar.CurrentHealth -= 10f;  // Takes damage
```

### Progress Bars with Animation

```csharp
public class AnimatedProgressBar : UIElement
{
    private float targetProgress = 0f;
    private float currentProgress = 0f;
    private float animationSpeed = 2f;  // Units per second
    
    public float Progress
    {
        get => targetProgress;
        set => targetProgress = Math.Clamp(value, 0f, 1f);
    }
    
    public override void Update(float dt)
    {
        // Smoothly animate to target
        if (Math.Abs(currentProgress - targetProgress) > 0.001f)
        {
            float diff = targetProgress - currentProgress;
            currentProgress += Math.Sign(diff) * Math.Min(Math.Abs(diff), animationSpeed * dt);
        }
    }
    
    public override void Render()
    {
        if (Manager == null) return;
        
        // Background
        Manager.DrawRect(Position.X, Position.Y, Size.X, Size.Y, 
            new Vector4(0.2f, 0.2f, 0.25f, 0.9f));
        
        // Progress fill with gradient
        float fillWidth = currentProgress * Size.X;
        var colorStart = new Vector4(0.3f, 0.6f, 0.9f, 1f);
        var colorEnd = new Vector4(0.5f, 0.8f, 1f, 1f);
        Manager.DrawGradientRect(Position.X, Position.Y, fillWidth, Size.Y, colorStart, colorEnd);
        
        // Border
        Manager.DrawBorder(Position.X, Position.Y, Size.X, Size.Y, 2f, 
            new Vector4(0.4f, 0.5f, 0.6f, 1f));
    }
}
```

### Tooltip System

```csharp
public class Tooltip : UIElement
{
    public string Text { get; set; } = "";
    private float fadeAlpha = 0f;
    
    public void ShowAt(Vector2 position, string text)
    {
        Text = text;
        Position = position;
        Visible = true;
        fadeAlpha = 0f;
    }
    
    public override void Update(float dt)
    {
        // Fade in
        if (Visible && fadeAlpha < 1f)
        {
            fadeAlpha = Math.Min(1f, fadeAlpha + dt * 5f);
        }
    }
    
    public override void Render()
    {
        if (Manager == null || Manager.TextRenderer == null || string.IsNullOrEmpty(Text)) return;
        
        // Measure text
        var textSize = Manager.TextRenderer.MeasureText(Text, 0.9f);
        float padding = 10f;
        
        Size = new Vector2(textSize.X + padding * 2, textSize.Y + padding * 2);
        
        // Background with fade
        var bgColor = new Vector4(0.1f, 0.1f, 0.15f, 0.95f * fadeAlpha);
        Manager.DrawRect(Position.X, Position.Y, Size.X, Size.Y, bgColor, cornerRadius: 4f);
        
        // Border
        var borderColor = new Vector4(0.5f, 0.6f, 0.7f, fadeAlpha);
        Manager.DrawBorder(Position.X, Position.Y, Size.X, Size.Y, 1f, borderColor);
        
        // Text
        var textColor = new Vector4(1f, 1f, 1f, fadeAlpha);
        var textPos = new Vector2(Position.X + padding, Position.Y + padding);
        Manager.TextRenderer.DrawText(Text, textPos, 0.9f, textColor);
    }
}
```

Usage with buttons:

```csharp
private Tooltip tooltip;

protected override void OnLoad()
{
    // Create tooltip (always add last so it renders on top)
    tooltip = new Tooltip();
    tooltip.Visible = false;
    uiManager.AddElement(tooltip);
}

protected override void OnUpdateFrame(FrameEventArgs e)
{
    // Show tooltip on hover
    bool anyHovered = false;
    foreach (var button in buttons)
    {
        if (button.IsHovered)
        {
            tooltip.ShowAt(
                new Vector2(MouseState.X + 10, MouseState.Y + 10),
                "Click to start the game"
            );
            anyHovered = true;
            break;
        }
    }
    
    if (!anyHovered)
    {
        tooltip.Visible = false;
    }
}
```

### Modal Dialogs

```csharp
public class ModalDialog
{
    private Panel background;
    private Panel dialogPanel;
    private Label titleLabel;
    private Label messageLabel;
    private Button okButton;
    private Button cancelButton;
    
    public Action? OnConfirm { get; set; }
    public Action? OnCancel { get; set; }
    
    public void Show(UIManager manager, string title, string message, float screenWidth, float screenHeight)
    {
        // Semi-transparent background overlay
        background = new Panel
        {
            Position = Vector2.Zero,
            Size = new Vector2(screenWidth, screenHeight),
            BackgroundColor = new Vector4(0, 0, 0, 0.7f),
            ShowBorder = false
        };
        manager.AddElement(background);
        
        // Dialog panel
        float dialogWidth = 400;
        float dialogHeight = 200;
        dialogPanel = new Panel
        {
            Position = new Vector2((screenWidth - dialogWidth) / 2, (screenHeight - dialogHeight) / 2),
            Size = new Vector2(dialogWidth, dialogHeight),
            BackgroundColor = new Vector4(0.15f, 0.15f, 0.2f, 1f),
            BorderColor = new Vector4(0.5f, 0.6f, 0.8f, 1f),
            BorderThickness = 3f,
            CornerRadius = 8f
        };
        manager.AddElement(dialogPanel);
        
        // Title
        titleLabel = new Label(title)
        {
            Position = new Vector2(dialogPanel.Position.X + 20, dialogPanel.Position.Y + 15),
            Size = new Vector2(dialogWidth - 40, 30),
            Scale = 1.3f,
            Color = new Vector4(0.9f, 0.9f, 1f, 1f)
        };
        manager.AddElement(titleLabel);
        
        // Message
        messageLabel = new Label(message)
        {
            Position = new Vector2(dialogPanel.Position.X + 20, dialogPanel.Position.Y + 60),
            Size = new Vector2(dialogWidth - 40, 60),
            Scale = 1.0f,
            Color = new Vector4(0.8f, 0.8f, 0.9f, 1f)
        };
        manager.AddElement(messageLabel);
        
        // OK button
        okButton = new Button("OK")
        {
            Position = new Vector2(dialogPanel.Position.X + dialogWidth - 180, 
                                  dialogPanel.Position.Y + dialogHeight - 50),
            Size = new Vector2(80, 35),
            LabelScale = 1.0f
        };
        okButton.OnPressed = () => {
            Hide(manager);
            OnConfirm?.Invoke();
        };
        manager.AddElement(okButton);
        
        // Cancel button
        cancelButton = new Button("Cancel")
        {
            Position = new Vector2(dialogPanel.Position.X + dialogWidth - 90, 
                                  dialogPanel.Position.Y + dialogHeight - 50),
            Size = new Vector2(80, 35),
            LabelScale = 1.0f,
            Color = new Vector4(0.4f, 0.2f, 0.2f, 0.9f)
        };
        cancelButton.OnPressed = () => {
            Hide(manager);
            OnCancel?.Invoke();
        };
        manager.AddElement(cancelButton);
    }
    
    public void Hide(UIManager manager)
    {
        manager.RemoveElement(background);
        manager.RemoveElement(dialogPanel);
        manager.RemoveElement(titleLabel);
        manager.RemoveElement(messageLabel);
        manager.RemoveElement(okButton);
        manager.RemoveElement(cancelButton);
    }
}
```

Usage:

```csharp
var dialog = new ModalDialog();
dialog.OnConfirm = () => {
    Console.WriteLine("User confirmed!");
    StartNewGame();
};
dialog.OnCancel = () => {
    Console.WriteLine("User cancelled");
};
dialog.Show(uiManager, "Confirm", "Start a new game?", ClientSize.X, ClientSize.Y);
```

### Multiple Screens/Pages

```csharp
public enum GameScreen
{
    MainMenu,
    Settings,
    Inventory,
    Gameplay
}

private GameScreen currentScreen = GameScreen.MainMenu;
private Dictionary<GameScreen, List<UIElement>> screenElements = new Dictionary<GameScreen, List<UIElement>>();

private void CreateScreen(GameScreen screen)
{
    var elements = new List<UIElement>();
    
    switch (screen)
    {
        case GameScreen.MainMenu:
            elements.Add(new Button("Start Game") { /* ... */ });
            elements.Add(new Button("Settings") { /* ... */ });
            break;
            
        case GameScreen.Settings:
            elements.Add(new Label("Settings") { /* ... */ });
            elements.Add(new Slider("Volume") { /* ... */ });
            elements.Add(new Button("Back") { /* ... */ });
            break;
    }
    
    screenElements[screen] = elements;
}

private void ShowScreen(GameScreen screen)
{
    // Hide current screen
    if (screenElements.ContainsKey(currentScreen))
    {
        foreach (var element in screenElements[currentScreen])
        {
            uiManager.RemoveElement(element);
        }
    }
    
    // Show new screen
    currentScreen = screen;
    if (!screenElements.ContainsKey(screen))
    {
        CreateScreen(screen);
    }
    
    foreach (var element in screenElements[screen])
    {
        uiManager.AddElement(element);
    }
}
```

---

## Complete Example: Settings Menu

Here's a full example combining everything:

```csharp
public class SettingsMenu
{
    private UIManager uiManager;
    private Panel backgroundPanel;
    private Label titleLabel;
    private Slider volumeSlider;
    private Slider brightnessSlider;
    private Checkbox fullscreenCheckbox;
    private Checkbox vsyncCheckbox;
    private Button applyButton;
    private Button cancelButton;
    
    public bool IsVisible { get; private set; }
    
    public Action? OnApply { get; set; }
    public Action? OnCancel { get; set; }
    
    // Settings values
    public float Volume { get; set; } = 0.8f;
    public float Brightness { get; set; } = 1.0f;
    public bool Fullscreen { get; set; } = false;
    public bool VSync { get; set; } = true;
    
    public SettingsMenu(UIManager manager)
    {
        uiManager = manager;
    }
    
    public void Initialize(float screenWidth, float screenHeight)
    {
        float panelWidth = 500;
        float panelHeight = 400;
        float panelX = (screenWidth - panelWidth) / 2;
        float panelY = (screenHeight - panelHeight) / 2;
        
        // Background
        backgroundPanel = new Panel
        {
            Position = new Vector2(panelX, panelY),
            Size = new Vector2(panelWidth, panelHeight),
            BackgroundColor = new Vector4(0.12f, 0.12f, 0.18f, 0.98f),
            BorderColor = new Vector4(0.4f, 0.5f, 0.7f, 1f),
            BorderThickness = 3f,
            CornerRadius = 10f
        };
        
        // Title
        titleLabel = new Label("Settings")
        {
            Position = new Vector2(panelX + 20, panelY + 20),
            Size = new Vector2(460, 40),
            Scale = 1.8f,
            Color = new Vector4(0.9f, 0.95f, 1f, 1f),
            TextAlign = TextAlign.Center
        };
        
        // Volume slider
        volumeSlider = new Slider("Volume")
        {
            Position = new Vector2(panelX + 50, panelY + 90),
            Size = new Vector2(400, 20),
            Value = Volume,
            Min = 0f,
            Max = 1f,
            ShowValue = true
        };
        volumeSlider.OnValueChanged = (value) => Volume = value;
        
        // Brightness slider
        brightnessSlider = new Slider("Brightness")
        {
            Position = new Vector2(panelX + 50, panelY + 150),
            Size = new Vector2(400, 20),
            Value = Brightness,
            Min = 0.5f,
            Max = 1.5f,
            ShowValue = true
        };
        brightnessSlider.OnValueChanged = (value) => Brightness = value;
        
        // Fullscreen checkbox
        fullscreenCheckbox = new Checkbox("Fullscreen")
        {
            Position = new Vector2(panelX + 50, panelY + 210),
            Size = new Vector2(200, 30),
            Checked = Fullscreen
        };
        fullscreenCheckbox.OnChanged = (value) => Fullscreen = value;
        
        // VSync checkbox
        vsyncCheckbox = new Checkbox("Vertical Sync")
        {
            Position = new Vector2(panelX + 50, panelY + 250),
            Size = new Vector2(200, 30),
            Checked = VSync
        };
        vsyncCheckbox.OnChanged = (value) => VSync = value;
        
        // Apply button
        applyButton = new Button("Apply")
        {
            Position = new Vector2(panelX + panelWidth - 180, panelY + panelHeight - 60),
            Size = new Vector2(80, 40),
            LabelScale = 1.0f,
            Color = new Vector4(0.3f, 0.5f, 0.3f, 0.95f),
            HoverColor = new Vector4(0.4f, 0.7f, 0.4f, 1f)
        };
        applyButton.OnPressed = () => {
            OnApply?.Invoke();
            Hide();
        };
        
        // Cancel button
        cancelButton = new Button("Cancel")
        {
            Position = new Vector2(panelX + panelWidth - 90, panelY + panelHeight - 60),
            Size = new Vector2(80, 40),
            LabelScale = 1.0f,
            Color = new Vector4(0.5f, 0.3f, 0.3f, 0.95f),
            HoverColor = new Vector4(0.7f, 0.4f, 0.4f, 1f)
        };
        cancelButton.OnPressed = () => {
            OnCancel?.Invoke();
            Hide();
        };
    }
    
    public void Show()
    {
        if (IsVisible) return;
        IsVisible = true;
        
        uiManager.AddElement(backgroundPanel);
        uiManager.AddElement(titleLabel);
        uiManager.AddElement(volumeSlider);
        uiManager.AddElement(brightnessSlider);
        uiManager.AddElement(fullscreenCheckbox);
        uiManager.AddElement(vsyncCheckbox);
        uiManager.AddElement(applyButton);
        uiManager.AddElement(cancelButton);
    }
    
    public void Hide()
    {
        if (!IsVisible) return;
        IsVisible = false;
        
        uiManager.RemoveElement(backgroundPanel);
        uiManager.RemoveElement(titleLabel);
        uiManager.RemoveElement(volumeSlider);
        uiManager.RemoveElement(brightnessSlider);
        uiManager.RemoveElement(fullscreenCheckbox);
        uiManager.RemoveElement(vsyncCheckbox);
        uiManager.RemoveElement(applyButton);
        uiManager.RemoveElement(cancelButton);
    }
    
    public void Toggle()
    {
        if (IsVisible) Hide();
        else Show();
    }
}
```

Usage in game:

```csharp
private SettingsMenu settingsMenu;

protected override void OnLoad()
{
    base.OnLoad();
    
    // Create settings menu
    settingsMenu = new SettingsMenu(uiManager);
    settingsMenu.Initialize(ClientSize.X, ClientSize.Y);
    
    settingsMenu.OnApply = () => {
        Console.WriteLine($"Applied: Volume={settingsMenu.Volume}, Brightness={settingsMenu.Brightness}");
        ApplySettings();
    };
    
    settingsMenu.OnCancel = () => {
        Console.WriteLine("Settings cancelled");
    };
}

protected override void OnUpdateFrame(FrameEventArgs e)
{
    // Toggle with Escape or specific key
    if (KeyboardState.IsKeyPressed(Keys.F1))
    {
        settingsMenu.Toggle();
    }
}

private void ApplySettings()
{
    // Apply volume
    SetGameVolume(settingsMenu.Volume);
    
    // Apply fullscreen
    if (settingsMenu.Fullscreen != (WindowState == WindowState.Fullscreen))
    {
        WindowState = settingsMenu.Fullscreen ? WindowState.Fullscreen : WindowState.Normal;
    }
    
    // Apply VSync
    VSync = settingsMenu.VSync ? VSyncMode.On : VSyncMode.Off;
}
```

---

## Performance Optimization Tips

### 1. Batch Rendering
Instead of drawing each element individually, batch similar elements:

```csharp
// Bad: Multiple draw calls
for (int i = 0; i < 100; i++)
{
    DrawRect(i * 10, 0, 8, 8, color);
}

// Good: Single draw call with many vertices
float[] allVertices = new float[100 * 6 * 6];
// ... fill allVertices ...
GL.BufferData(BufferTarget.ArrayBuffer, allVertices.Length * sizeof(float), allVertices, BufferUsageHint.DynamicDraw);
GL.DrawArrays(PrimitiveType.Triangles, 0, 600);
```

### 2. Dirty Flags
Only redraw when something changes:

```csharp
public class OptimizedLabel : Label
{
    private bool isDirty = true;
    private string lastText = "";
    
    public new string Text
    {
        get => base.Text;
        set
        {
            if (base.Text != value)
            {
                base.Text = value;
                isDirty = true;
            }
        }
    }
    
    public override void Render()
    {
        if (!isDirty) return;
        
        base.Render();
        isDirty = false;
    }
}
```

### 3. Object Pooling
Reuse UI elements instead of creating new ones:

```csharp
public class UIElementPool<T> where T : UIElement, new()
{
    private Queue<T> pool = new Queue<T>();
    
    public T Get()
    {
        if (pool.Count > 0)
        {
            var element = pool.Dequeue();
            element.Visible = true;
            return element;
        }
        return new T();
    }
    
    public void Return(T element)
    {
        element.Visible = false;
        pool.Enqueue(element);
    }
}
```

### 4. Culling
Don't render off-screen elements:

```csharp
public override void Render()
{
    // Skip if completely off-screen
    if (Position.X + Size.X < 0 || Position.X > screenWidth ||
        Position.Y + Size.Y < 0 || Position.Y > screenHeight)
    {
        return;
    }
    
    // Normal rendering
    base.Render();
}
```

---

## Conclusion

You now have a complete understanding of creating UI systems in OpenTK! You've learned:

- Basic rendering with shaders and buffers
- Text rendering with TrueType fonts
- Complete UI component system
- Event-driven input handling
- Advanced patterns like tooltips and dialogs
- Building complex interfaces like inventory systems
- Performance optimization techniques

**Remember**: Start simple and build up complexity gradually. Master buttons and labels before moving on to custom components and complex layouts.

Happy coding!
