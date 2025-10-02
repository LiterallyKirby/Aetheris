# Aetheris World Generation Documentation

## Overview
The Aetheris world generator creates infinite voxel terrain using **noise-based generation** and the **marching cubes algorithm**. The system uses a density field where `density > 0.5 = solid` and `density < 0.5 = air`, with support for **textured block types** and **smooth biome blending**.

---

## Core Concepts

### 1. Density Fields
The world is defined by a continuous 3D density function:
```csharp
float density = WorldGen.SampleDensity(x, y, z);
```

- **Positive density** = solid terrain (stone, dirt, etc.)
- **Negative density** = empty space (air, caves)
- **ISO_SURFACE (0.5)** = the boundary between solid and air

### 2. Block Types
Each solid voxel has a **BlockType** that determines its texture:

```csharp
public enum BlockType : byte
{
    Air = 0,
    Stone = 1,
    Dirt = 2,
    Grass = 3,
    Sand = 4,
    Snow = 5,
    Gravel = 6,
    Wood = 7,
    Leaves = 8
}
```

Block types are determined by:
- **Biome** (Desert=Sand, Plains=Grass)
- **Depth below surface** (Surface=Grass, Subsurface=Dirt, Deep=Stone)
- **Altitude** (Mountains above Y=45 get Snow)

### 3. Pillar Generation (Minecraft-style)
Underground terrain is generated as **solid vertical pillars** that extend down from the surface:

```csharp
if (y > surfaceY)
{
    density = ISO - (y - surfaceY) * 0.2f;  // Air above
}
else
{
    float depth = surfaceY - y;
    density = ISO + 2.0f + (depth * 0.02f);  // Solid below
}
```

The key is starting at `ISO + 2.0f` (not just `ISO`) to ensure underground is **very solid** by default.

---

## Biome System

### How Biomes Work

1. **Biome Selection** uses cellular noise to create large regions:
```csharp
float biomeValue = biomeNoise.GetNoise(x, z);  // 2D noise
Biome biome = MapValueToBiome(biomeValue);
```

2. **Biome Blending** creates smooth transitions:
```csharp
var (primary, secondary, blendFactor) = GetBiomeBlend(x, z);
```
- Transition zones are 0.15 units wide
- Terrain height, cave intensity, and block types blend smoothly
- Eliminates z-fighting at biome boundaries

3. **Each biome has parameters**:
   - `baseHeight` - Average terrain height
   - `amplitude` - How much terrain varies
   - `caveIntensity` - How many caves spawn
   - `blockTypes` - Surface and subsurface materials

4. **Biomes are determined by XZ position** (not Y) so they span from bedrock to sky.

### Current Biomes

| Biome | Base Height | Amplitude | Cave Intensity | Surface Block | Subsurface |
|-------|------------|-----------|----------------|---------------|------------|
| **Plains** | 25 | 5 | 1.0x | Grass | Dirt |
| **Forest** | 30 | 12 | 0.8x | Grass | Dirt |
| **Desert** | 20 | 8 | 0.6x | Sand | Sand |
| **Mountains** | 40 | 35 | 1.5x | Stone/Snow (>Y45) | Stone |

### Biome Transition Zones

At biome boundaries, the system creates natural-looking transitions:

```csharp
// In transition zone between Plains and Desert:
- Terrain height gradually changes from 25 to 20
- Cave intensity blends from 1.0x to 0.6x
- Blocks mix naturally (patches of grass and sand)
- Uses noise to create organic mixing pattern
```

**Benefits:**
- No z-fighting or visual artifacts
- Natural "edge of biome" feeling
- Smooth gameplay experience when crossing biomes

### Adding a New Biome

**Step 1:** Add to the `Biome` enum:
```csharp
private enum Biome
{
    Plains,
    Mountains,
    Desert,
    Forest,
    Swamp      // <- New biome
}
```

**Step 2:** Add to `GetBiome()` mapping:
```csharp
private static Biome GetBiome(int x, int z)
{
    float biomeValue = biomeNoise.GetNoise(x, z);
    
    if (biomeValue < -0.6f) return Biome.Plains;
    if (biomeValue < -0.2f) return Biome.Forest;
    if (biomeValue < 0.2f) return Biome.Desert;
    if (biomeValue < 0.6f) return Biome.Swamp;    // <- Add here
    return Biome.Mountains;
}
```

**Step 3:** Add to `GetBiomeBlend()` transition logic:
```csharp
// Add transition handling in GetBiomeBlend()
else if (biomeValue < 0.2f + blendWidth && biomeValue >= 0.2f - blendWidth)
{
    primary = Biome.Swamp;
    secondary = Biome.Desert;
    blend = (biomeValue - (0.2f - blendWidth)) / (blendWidth * 2);
}
```

**Step 4:** Add parameters in `GetBiomeParams()`:
```csharp
private static (float baseHeight, float amplitude) GetBiomeParams(Biome biome)
{
    return biome switch
    {
        Biome.Plains => (25f, 5f),
        Biome.Forest => (30f, 12f),
        Biome.Desert => (20f, 8f),
        Biome.Mountains => (40f, 35f),
        Biome.Swamp => (18f, 3f),      // <- Low, flat terrain
        _ => (30f, 10f)
    };
}
```

**Step 5:** Add cave intensity in `GetCaveIntensity()`:
```csharp
private static float GetCaveIntensity(Biome biome)
{
    return biome switch
    {
        Biome.Plains => 1.0f,
        Biome.Forest => 0.8f,
        Biome.Desert => 0.6f,
        Biome.Mountains => 1.5f,
        Biome.Swamp => 0.3f,          // <- Very few caves
        _ => 1.0f
    };
}
```

**Step 6:** Define block types in `GetBlockType()`:
```csharp
// Surface layer
return selectedBiome switch
{
    Biome.Plains => BlockType.Grass,
    Biome.Forest => BlockType.Grass,
    Biome.Desert => BlockType.Sand,
    Biome.Mountains => y > 45 ? BlockType.Snow : BlockType.Stone,
    Biome.Swamp => BlockType.Dirt,    // <- Muddy surface
    _ => BlockType.Grass
};

// Subsurface
return selectedBiome switch
{
    Biome.Desert => BlockType.Sand,
    Biome.Mountains => BlockType.Stone,
    Biome.Swamp => BlockType.Dirt,    // <- Thick mud layer
    _ => BlockType.Dirt
};
```

---

## Block Type System

### How Block Types Are Assigned

Block types are determined in `GetBlockType()` based on:

1. **Density** - Must be solid (`density > ISO`)
2. **Y-level** - Bedrock (Y≤2), Surface, Subsurface, Deep
3. **Biome** - Different biomes use different materials
4. **Altitude** - Mountains get snow above Y=45

```csharp
public static BlockType GetBlockType(int x, int y, int z, float density)
{
    if (density <= ISO) return BlockType.Air;
    if (y <= 2) return BlockType.Stone;  // Bedrock
    
    var (primary, secondary, blend) = GetBiomeBlend(x, z);
    // ... determine surface height ...
    
    int depthBelowSurface = (int)(surfaceY - y);
    
    if (depthBelowSurface <= 1)  // Surface layer
        return GetSurfaceBlock(selectedBiome, y);
    else if (depthBelowSurface <= 4)  // Subsurface
        return GetSubsurfaceBlock(selectedBiome);
    else  // Deep underground
        return BlockType.Stone;
}
```

### Adding New Block Types

**Step 1:** Add to `BlockType` enum:
```csharp
public enum BlockType : byte
{
    Air = 0,
    Stone = 1,
    // ... existing types ...
    Clay = 9,      // New block type
    Obsidian = 10
}
```

**Step 2:** Add texture mapping in `BlockTypeExtensions.GetAtlasUV()`:
```csharp
int atlasIndex = block switch
{
    BlockType.Stone => 0,
    // ... existing mappings ...
    BlockType.Clay => 8,      // Position (0,2) in 4x4 atlas
    BlockType.Obsidian => 9,  // Position (1,2)
    _ => 0
};
```

**Step 3:** Update texture atlas:
- Create or expand your atlas image to include new textures
- For 4x4 grid: rows 2-3 are available (indices 8-15)
- For more types: increase to 8x8 grid (512×512 atlas)

**Step 4:** Use in world generation:
```csharp
// Example: Clay near water level in swamps
if (selectedBiome == Biome.Swamp && y < 20 && y > 15)
{
    return BlockType.Clay;
}
```

### Texture Atlas Configuration

The system uses a **texture atlas** to map block types to textures:

**Default Setup:**
- Atlas size: 256×256 pixels (4×4 grid)
- Tile size: 64×64 pixels per block type
- Format: PNG with RGBA

**Atlas Layout:**
```
Row 0: [Stone] [Dirt ] [Grass] [Sand ]  (indices 0-3)
Row 1: [Snow ] [Gravel] [Wood ] [Leaves] (indices 4-7)
Row 2: [Empty] [Empty ] [Empty] [Empty ] (indices 8-11)
Row 3: [Empty] [Empty ] [Empty] [Empty ] (indices 12-15)
```

**Expanding the Atlas:**

For more than 16 block types, increase `ATLAS_SIZE` in `BlockTypeExtensions.cs`:

```csharp
private const int ATLAS_SIZE = 8;  // 8×8 = 64 textures
```

Then create a 512×512 atlas with 64×64 tiles, or 1024×1024 with 128×128 tiles.

---

## Cave System

### Cave Types

The system generates **three types of caves** that layer on top of each other:

#### 1. Worm Caves (Tunnels)
```csharp
float worm = (c1 + c2) * 0.5f;
if (worm > 0.3f)
{
    density -= (worm - 0.3f) * 50.0f * caveIntensity;
}
```
- **What it does**: Creates winding tunnel networks
- **Frequency**: 0.015-0.03 (medium to large)
- **Active depth**: Y=5 to surface-5

#### 2. Cheese Caves (Pockets)
```csharp
if (c3 > 0.5f && y < surfaceY * 0.7f)
{
    density -= (c3 - 0.5f) * 6.0f * caveIntensity;
}
```
- **What it does**: Random air pockets like Swiss cheese
- **Frequency**: 0.05 (small)
- **Active depth**: Only in middle layers (not near surface)

#### 3. Large Caverns
```csharp
if (y < 20)
{
    float cavern = c1 * c2;
    if (cavern > 0.4f)
    {
        density -= (cavern - 0.4f) * 10000.0f * caveIntensity;
    }
}
```
- **What it does**: Huge open spaces
- **Active depth**: Only deep underground (Y < 20)
- **Method**: Multiplies two noise fields (rare intersections)

### Cave Intensity Blending

Cave density smoothly transitions between biomes:

```csharp
float caveIntensity1 = GetCaveIntensity(primaryBiome);
float caveIntensity2 = GetCaveIntensity(secondaryBiome);
float caveIntensity = caveIntensity1 * (1.0f - blendFactor) + caveIntensity2 * blendFactor;
```

This prevents sudden changes in cave frequency at biome boundaries.

### Cave Parameters

| Parameter | Effect | Recommended Range |
|-----------|--------|-------------------|
| **Threshold** | Higher = fewer caves | 0.2 - 0.5 |
| **Strength** | Higher = bigger caves | 5.0 - 50.0 |
| **Frequency** | Higher = smaller caves | 0.01 - 0.1 |
| **Min Depth** | How close to surface | 3 - 10 blocks |

### Adding a New Cave Type

**Example: Crystal Caves (rare, only in mountains)**

```csharp
// In SampleDensity(), after existing caves:

// Crystal caves - only in mountains, very rare
if (primaryBiome == Biome.Mountains && y < 30 && y > 15)
{
    float crystal = caveNoise3.GetNoise(x * 0.1f, y * 0.1f, z * 0.1f);
    if (crystal > 0.7f)  // Very high threshold = rare
    {
        density -= (crystal - 0.7f) * 20.0f;  // Strong carving
    }
}
```

---

## Rendering & Textures

### Texture Loading

The system supports both **image-based** and **procedural** texture atlases:

```csharp
// In OnLoad():
Renderer.LoadTextureAtlas("textures/atlas.png");  // Loads image or falls back to procedural
```

**Procedural Fallback:**
If no atlas image exists, the system generates colored textures automatically:
- Stone: Gray
- Dirt: Brown
- Grass: Green
- Sand: Tan
- (etc.)

### Triplanar UV Mapping

The marching cubes mesh uses **triplanar mapping** to avoid stretching:

```csharp
// Choose UV coordinates based on surface normal
if (absY > absX && absY > absZ)
    // Top/bottom face - use XZ coordinates
else if (absX > absZ)
    // Side face - use ZY coordinates
else
    // Front/back face - use XY coordinates
```

This ensures textures look correct on all surface angles.

### Vertex Format

Each vertex contains 8 floats:
- Position (3): X, Y, Z
- Normal (3): NX, NY, NZ
- UV (2): U, V

```csharp
var meshFloats = MarchingCubes.GenerateMesh(chunk, 0.5f);
int vertexCount = meshFloats.Length / 8;
```

---

## Configuration

### World Seed
Change `Config.WORLD_SEED` to generate different worlds:
```csharp
public static int WORLD_SEED = 12345;  // Each seed = unique world
```

### Chunk Settings
```csharp
CHUNK_SIZE = 32;      // XZ size (must divide evenly by STEP)
CHUNK_SIZE_Y = 96;    // Y height
STEP = 8;             // Marching cubes resolution (lower = more detail)
```

**STEP Guidelines:**
- `STEP = 2`: Very detailed, slow
- `STEP = 4`: High detail, good performance
- `STEP = 8`: Medium detail, fast (recommended)
- `STEP = 16`: Low detail, very fast

---

## Noise Functions

### FastNoiseLite Types

| Noise Type | Characteristics | Best For |
|------------|-----------------|----------|
| **OpenSimplex2** | Smooth, organic | Terrain, caves |
| **Perlin** | Classic, layered | Detail noise |
| **Cellular** | Cell-like patterns | Biomes, crystal formations |

### Key Parameters

```csharp
noise.SetFrequency(0.008f);      // Lower = larger features
noise.SetFractalOctaves(4);      // More = more detail layers
noise.SetFractalLacunarity(2.0f);// How octaves scale
noise.SetFractalGain(0.5f);      // Octave strength
```

### Biome Noise Configuration

```csharp
biomeNoise.SetNoiseType(NoiseType.Cellular);
biomeNoise.SetFrequency(0.001f);  // Very large biomes
biomeNoise.SetCellularReturnType(CellularReturnType.CellValue);
```

The low frequency (0.001f) creates biomes that span thousands of blocks.

---

## Advanced Techniques

### Custom Biome-Specific Features

Add unique structures or materials to specific biomes:

```csharp
// In GetBlockType():
if (selectedBiome == Biome.Desert && y < 25 && y > 20)
{
    // Sandstone layer in deserts
    return BlockType.Sand;  // Or BlockType.Sandstone if you add it
}

if (selectedBiome == Biome.Mountains && y > 60)
{
    // Ice caps on mountain peaks
    return BlockType.Snow;
}
```

### Height-Based Features
Add features that only appear at certain elevations:

```csharp
// Floating islands (high altitude)
if (y > 80)
{
    float island = islandNoise.GetNoise(x, y, z);
    if (island > 0.6f)
    {
        density += 5.0f;  // Create floating mass
    }
}
```

### Ore Veins
Add resource deposits using 3D noise:

```csharp
// In GetBlockType(), after determining base block type:
if (y < 40 && density > ISO)
{
    float ore = oreNoise.GetNoise(x, y, z);
    if (ore > 0.85f)  // Rare
    {
        return BlockType.Iron;  // Or gold, coal, etc.
    }
}
```

### Blending Optimization

The current blend calculation samples density twice (once for primary, once for secondary biome). For optimization:

```csharp
// Cache blended terrain height instead of calculating per-block
float surfaceY = CalculateBlendedSurfaceHeight(x, z, primaryBiome, secondaryBiome, blendFactor);
```

---

## Performance Tips

1. **Cache noise values** - `GetBlockType()` now uses pre-calculated density
2. **Use ThreadStatic** - Already implemented in MarchingCubes
3. **Adjust STEP size** - Larger STEP = fewer triangles
4. **Limit cave density** - Too many caves = too many vertices
5. **Pre-generate chunks** - Server caches meshes
6. **Biome blend width** - Smaller transitions = less blending overhead

---

## Troubleshooting

### "Chunks have gaps between them"
- Ensure marching cubes samples beyond chunk boundaries
- Check that STEP divides evenly into CHUNK_SIZE

### "Z-fighting at biome boundaries"
- This is fixed by biome blending system
- Verify `GetBiomeBlend()` is being called
- Check that both `SampleDensity()` and `GetBlockType()` use blending

### "Caves are too shallow"
- Increase cave depth range: `y < surfaceY - 5` → `y < surfaceY - 2`
- Lower the min Y: `y > 5` → `y > 2`

### "World is all air/all solid"
- Check ISO_SURFACE threshold (should be 0.5)
- Verify density calculation returns positive for underground

### "Terrain looks blocky"
- Reduce STEP size (8 → 4 or 2)
- Add more noise octaves for detail

### "Wrong textures on blocks"
- Verify atlas indices match `BlockTypeExtensions.GetAtlasUV()`
- Check that atlas image has textures in correct grid positions
- Ensure atlas is being loaded in `OnLoad()`

### "Textures are blurry/pixelated"
- Adjust texture filtering in `Renderer.LoadTextureFromFile()`
- For pixel art: Use `TextureMagFilter.Nearest`
- For smooth: Use `TextureMagFilter.Linear`

---

## Example: Creating an Ocean Biome

```csharp
// Step 1: Add to enum
private enum Biome { Plains, Mountains, Desert, Forest, Ocean }

// Step 2: Map in GetBiome
if (biomeValue < -0.7f) return Biome.Ocean;

// Step 3: Add to GetBiomeBlend transitions
else if (biomeValue < -0.7f + blendWidth && biomeValue >= -0.7f - blendWidth)
{
    primary = Biome.Ocean;
    secondary = Biome.Plains;
    blend = (biomeValue - (-0.7f - blendWidth)) / (blendWidth * 2);
}

// Step 4: Parameters
Biome.Ocean => (5f, 2f),  // Sea floor with small variation

// Step 5: Special ocean logic in SampleDensity
if (primaryBiome == Biome.Ocean || secondaryBiome == Biome.Ocean)
{
    // Water level at Y=12
    if (y < 12 && y > surfaceY)
    {
        density = ISO + 0.5f;  // Water is "solid" for rendering
    }
}

// Step 6: Ocean floor blocks
Biome.Ocean => BlockType.Sand  // Sandy ocean floor

// Step 7: No caves in ocean
Biome.Ocean => 0.0f  // No caves underwater
```

This creates underwater terrain with a water surface at Y=12 and smooth transitions to beaches.
