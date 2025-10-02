# Texture Atlas Setup Guide for Aetheris

## Quick Start

1. Install StbImageSharp: `dotnet add package StbImageSharp`
2. Create a 256×256 PNG with 4×4 grid of textures (each tile 64×64)
3. Save as `textures/atlas.png` in your project root
4. Add to `.csproj` to auto-copy on build
5. Load in `OnLoad()` method

## Atlas Specifications

### Image Dimensions
- **Atlas Size:** 256×256 pixels (PNG format recommended)
- **Grid Layout:** 4×4 (16 tiles total)
- **Each Tile:** 64×64 pixels
- **Format:** RGBA (RGB with alpha channel)

### Tile Layout Map

```
     Column 0    Column 1    Column 2    Column 3
Row 0: Stone   | Dirt      | Grass     | Sand      
Row 1: Snow    | Gravel    | Wood      | Leaves    
Row 2: (Empty) | (Empty)   | (Empty)   | (Empty)   
Row 3: (Empty) | (Empty)   | (Empty)   | (Empty)   
```

**Pixel Coordinates:**
- Stone:  (0, 0) to (64, 64)
- Dirt:   (64, 0) to (128, 64)
- Grass:  (128, 0) to (192, 64)
- Sand:   (192, 0) to (256, 64)
- Snow:   (0, 64) to (64, 128)
- Gravel: (64, 64) to (128, 128)
- Wood:   (128, 64) to (192, 128)
- Leaves: (192, 64) to (256, 128)

## Installation

### 1. Install Required Package

```bash
dotnet add package StbImageSharp
```

### 2. Configure .csproj

Add this to your `Aetheris.csproj` file:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- your existing properties -->
  </PropertyGroup>

  <ItemGroup>
    <!-- Auto-copy texture atlas to output directory -->
    <None Update="textures\atlas.png">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>

  <!-- Your existing PackageReferences -->
</Project>
```

### 3. Project Structure

Create this folder structure:

```
YourProject/
├── Aetheris.csproj
├── textures/
│   └── atlas.png          ← Place your 256×256 PNG here
├── Renderer.cs
├── MarchingCubes.cs
├── WorldGen.cs
└── bin/
    └── Debug/
        └── net8.0/
            └── textures/
                └── atlas.png  ← Auto-copied by build
```

### 4. Load in Your Game Code

In your Game class `OnLoad()` method:

```csharp
protected override void OnLoad()
{
    base.OnLoad();
    GL.ClearColor(0.15f, 0.18f, 0.2f, 1.0f);
    GL.Enable(EnableCap.DepthTest);
    CursorState = CursorState.Grabbed;
    
    // Load texture atlas (auto-fallback to procedural if missing)
    Renderer.LoadTextureAtlas("textures/atlas.png");
    
    // Load chunks...
    foreach (var kv in loadedChunks)
    {
        var coord = kv.Key;
        var chunk = kv.Value;
        var meshFloats = MarchingCubes.GenerateMesh(chunk, 0.5f);
        // Note: now 8 floats per vertex (pos3 + normal3 + uv2)
        Console.WriteLine($"[Game] Loading chunk {coord} with {meshFloats.Length / 8} vertices");
        Renderer.LoadMeshForChunk(coord.Item1, coord.Item2, coord.Item3, meshFloats);
    }
    Console.WriteLine($"[Game] Loaded {loadedChunks.Count} chunks");
}
```

## Creating Your Atlas

### Method 1: Using Image Editor (GIMP)

1. **Create New Image**
   - File → New
   - Size: 256×256 pixels
   - Fill with: Transparency

2. **Add Guides for Grid**
   - Image → Guides → New Guide (By Percent)
   - Add guides at: 0%, 25%, 50%, 75%, 100% (both horizontal and vertical)
   - This creates your 4×4 grid

3. **Add Textures**
   - Create or import 64×64 textures for each block type
   - Place them in correct grid positions using guides
   - Stone at (0,0), Dirt at (64,0), etc.

4. **Export**
   - File → Export As
   - Format: PNG
   - Save as `atlas.png` in your `textures/` folder

### Method 2: Using Photoshop

1. **New Document**
   - 256×256 px
   - Color Mode: RGB, 8 bit
   - Background: Transparent

2. **Create Grid**
   - View → New Guide Layout
   - Columns: 4, Rows: 4
   - This gives you 64px squares

3. **Add Block Textures**
   - Paste or create 64×64 textures in each grid square
   - Follow the layout map above

4. **Save**
   - File → Export → Export As
   - Format: PNG-24
   - Transparency: Yes
   - Save to `textures/atlas.png`

### Method 3: Using Python (PIL/Pillow)

```python
from PIL import Image, ImageDraw

# Create 256x256 atlas
atlas = Image.new('RGBA', (256, 256), (0, 0, 0, 0))

# Define your 64x64 textures
textures = {
    'stone':  Image.open('stone_64.png'),
    'dirt':   Image.open('dirt_64.png'),
    'grass':  Image.open('grass_64.png'),
    'sand':   Image.open('sand_64.png'),
    'snow':   Image.open('snow_64.png'),
    'gravel': Image.open('gravel_64.png'),
    'wood':   Image.open('wood_64.png'),
    'leaves': Image.open('leaves_64.png'),
}

# Ensure all are 64x64
for key in textures:
    textures[key] = textures[key].resize((64, 64))

# Paste into grid (Column, Row format)
atlas.paste(textures['stone'],  (0, 0))      # Col 0, Row 0
atlas.paste(textures['dirt'],   (64, 0))     # Col 1, Row 0
atlas.paste(textures['grass'],  (128, 0))    # Col 2, Row 0
atlas.paste(textures['sand'],   (192, 0))    # Col 3, Row 0
atlas.paste(textures['snow'],   (0, 64))     # Col 0, Row 1
atlas.paste(textures['gravel'], (64, 64))    # Col 1, Row 1
atlas.paste(textures['wood'],   (128, 64))   # Col 2, Row 1
atlas.paste(textures['leaves'], (192, 64))   # Col 3, Row 1

atlas.save('atlas.png')
print("Atlas created: 256x256 pixels")
```

### Method 4: Quick Test Atlas (Solid Colors)

If you just want to test, create this simple atlas in any image editor:

1. Create 256×256 image
2. Fill each 64×64 square with solid colors:
   - Stone: Gray (#808080)
   - Dirt: Brown (#8B4513)
   - Grass: Green (#228B22)
   - Sand: Tan (#C2B280)
   - Snow: White (#FFFFFF)
   - Gravel: Dark Gray (#696969)
   - Wood: Dark Brown (#654321)
   - Leaves: Dark Green (#2E8B57)

## Texture Quality Guidelines

### Resolution per Tile
- **Minimum:** 16×16 pixels per tile (atlas: 64×64)
- **Recommended:** 64×64 pixels per tile (atlas: 256×256) ← **Default**
- **High Quality:** 128×128 pixels per tile (atlas: 512×512)
- **Ultra:** 256×256 pixels per tile (atlas: 1024×1024)

### File Format
- **PNG** (recommended) - lossless, supports transparency
- JPG (not recommended) - compression artifacts, no transparency
- BMP (works but large file size)

### Color Depth
- Use **RGBA** (32-bit) for transparency support
- RGB (24-bit) works if no transparency needed

## Customizing Block Textures

### Changing Texture Assignments

Edit `BlockTypeExtensions.cs`:

```csharp
public static (float uMin, float vMin, float uMax, float vMax) GetAtlasUV(this BlockType block)
{
    // Map block types to atlas positions
    int atlasIndex = block switch
    {
        BlockType.Stone => 0,   // Position (0,0) - change index to reassign
        BlockType.Dirt => 1,    // Position (1,0)
        BlockType.Grass => 2,   // Position (2,0)
        BlockType.Sand => 3,    // Position (3,0)
        BlockType.Snow => 4,    // Position (0,1)
        BlockType.Gravel => 5,  // Position (1,1)
        BlockType.Wood => 6,    // Position (2,1)
        BlockType.Leaves => 7,  // Position (3,1)
        _ => 0
    };
    
    // Calculate UV from atlas index (don't change this part)
    int x = atlasIndex % ATLAS_SIZE;
    int y = atlasIndex / ATLAS_SIZE;
    // ...
}
```

### Expanding to More Textures

To use more than 8 block types:

1. **Update ATLAS_SIZE:**
```csharp
private const int ATLAS_SIZE = 8; // For 8×8 grid = 64 textures
```

2. **Create larger atlas:**
   - 512×512 for 8×8 grid (64 textures of 64×64 each)
   - 1024×1024 for 16×16 grid (256 textures of 64×64 each)

3. **Add new BlockType values:**
```csharp
public enum BlockType : byte
{
    Air = 0,
    Stone = 1,
    // ... existing types ...
    Coal = 9,
    Iron = 10,
    // etc.
}
```

4. **Update GetAtlasUV() mapping**

## Rendering Style Settings

### Pixelated/Minecraft Style (Default)

In `Renderer.cs` → `LoadTextureFromFile()`:

```csharp
GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, 
    (int)TextureMinFilter.NearestMipmapLinear);
GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, 
    (int)TextureMagFilter.Nearest);
```

### Smooth/Filtered Style

```csharp
GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, 
    (int)TextureMinFilter.LinearMipmapLinear);
GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, 
    (int)TextureMagFilter.Linear);
```

## Troubleshooting

### "Texture file not found"
- Verify `textures/atlas.png` exists in project root
- Check `.csproj` has `<CopyToOutputDirectory>` setting
- Build project to trigger file copy
- Check `bin/Debug/net8.0/textures/atlas.png` exists

### Textures appear upside down
- StbImageSharp flips automatically with `stbi_set_flip_vertically_on_load(1)`
- If still wrong, change to `(0)` in `LoadTextureFromFile()`

### Wrong colors/textures on blocks
1. Verify atlas layout matches the tile map above
2. Check block type assignments in `GetAtlasUV()`
3. Use image viewer to confirm atlas coordinates

### Seams/lines between textures
- Ensure exact 64×64 pixel tiles with no gaps
- Add 1-pixel padding between tiles
- Use Nearest filtering: `TextureMagFilter.Nearest`

### Blurry textures
- Use `TextureMagFilter.Nearest` for pixel-art style
- Increase per-tile resolution (e.g., 128×128 tiles)
- Check mipmap generation is enabled

### Build doesn't copy PNG
- Verify `.csproj` syntax is correct
- Use backslashes on Windows: `textures\atlas.png`
- Try "Copy always" instead of "PreserveNewest"
- Check file properties in IDE

## Procedural Fallback

If `atlas.png` is missing, the system automatically generates a colored atlas:

```
Stone: Gray      Dirt: Brown     Grass: Green    Sand: Tan
Snow: White      Gravel: D.Gray  Wood: D.Brown   Leaves: D.Green
```

This lets you develop without creating textures first!

## Free Texture Resources

- **OpenGameArt.org** - CC0/CC-BY licensed game textures
- **itch.io** - Many free texture packs
- **Kenney.nl** - Free game assets including textures
- **CC0Textures.com** - Public domain PBR textures

**Always check licenses before use!**

## Performance Notes

- 256×256 atlas is optimal for most uses
- Larger atlases (512×512+) use more VRAM
- Mipmaps improve performance at distance
- Anisotropic filtering adds quality with minimal cost
- Power-of-2 dimensions (256, 512, 1024) are GPU-friendly
