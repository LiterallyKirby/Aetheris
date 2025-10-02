# Aetheris World Generation Documentation

## Overview
The Aetheris world generator creates infinite voxel terrain using **noise-based generation** and the **marching cubes algorithm**. The system uses a density field where `density > 0.5 = solid` and `density < 0.5 = air`.

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

### 2. Pillar Generation (Minecraft-style)
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

2. **Each biome has parameters**:
   - `baseHeight` - Average terrain height
   - `amplitude` - How much terrain varies
   - `caveIntensity` - How many caves spawn

3. **Biomes are determined by XZ position** (not Y) so they span from bedrock to sky.

### Current Biomes

| Biome | Base Height | Amplitude | Cave Intensity | Description |
|-------|------------|-----------|----------------|-------------|
| **Plains** | 25 | 5 | 1.0x | Flat grasslands with normal caves |
| **Forest** | 30 | 12 | 0.8x | Rolling hills with fewer caves |
| **Desert** | 20 | 8 | 0.6x | Sandy terrain with sparse caves |
| **Mountains** | 40 | 35 | 1.5x | Dramatic peaks with many caves |

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

**Step 3:** Add parameters in `GetBiomeParams()`:
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

**Step 4:** Add cave intensity in `GetCaveIntensity()`:
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

---

## Cave System

### Cave Types

The system generates **three types of caves** that layer on top of each other:

#### 1. Worm Caves (Tunnels)
```csharp
float worm = (c1 + c2) * 0.5f;
if (worm > 0.3f)
{
    density -= (worm - 0.3f) * 8.0f;
}
```
- **What it does**: Creates winding tunnel networks
- **Frequency**: 0.015-0.03 (medium to large)
- **Active depth**: Y=5 to surface-5

#### 2. Cheese Caves (Pockets)
```csharp
if (c3 > 0.5f && y < surfaceY * 0.7f)
{
    density -= (c3 - 0.5f) * 6.0f;
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
        density -= (cavern - 0.4f) * 10.0f;
    }
}
```
- **What it does**: Huge open spaces
- **Active depth**: Only deep underground (Y < 20)
- **Method**: Multiplies two noise fields (rare intersections)

### Cave Parameters

| Parameter | Effect | Recommended Range |
|-----------|--------|-------------------|
| **Threshold** | Higher = fewer caves | 0.2 - 0.5 |
| **Strength** | Higher = bigger caves | 5.0 - 15.0 |
| **Frequency** | Higher = smaller caves | 0.01 - 0.1 |
| **Min Depth** | How close to surface | 3 - 10 blocks |

### Adding a New Cave Type

**Example: Crystal Caves (rare, only in mountains)**

```csharp
// In SampleDensity(), after existing caves:

// Crystal caves - only in mountains, very rare
if (biome == Biome.Mountains && y < 30 && y > 15)
{
    float crystal = caveNoise3.GetNoise(x * 0.1f, y * 0.1f, z * 0.1f);
    if (crystal > 0.7f)  // Very high threshold = rare
    {
        density -= (crystal - 0.7f) * 20.0f;  // Strong carving
    }
}
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

---

## Advanced Techniques

### Blending Biomes
To make smooth transitions between biomes:

```csharp
// Sample neighboring biomes
Biome b1 = GetBiome(x, z);
Biome b2 = GetBiome(x + 100, z);

// Blend based on distance
float blend = GetBlendFactor(x, z);
float height = Lerp(GetHeight(b1), GetHeight(b2), blend);
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
float ore = oreNoise.GetNoise(x, y, z);
if (ore > 0.8f && density > ISO)  // Only in solid terrain
{
    // Mark as ore (requires block type system)
}
```

---

## Performance Tips

1. **Cache noise values** - Don't sample the same position twice
2. **Use ThreadStatic** - Already implemented in MarchingCubes
3. **Adjust STEP size** - Larger STEP = fewer triangles
4. **Limit cave density** - Too many caves = too many vertices
5. **Pre-generate chunks** - Server caches meshes

---

## Troubleshooting

### "Chunks have gaps between them"
- Ensure marching cubes samples beyond chunk boundaries
- Check that STEP divides evenly into CHUNK_SIZE

### "Caves are too shallow"
- Increase cave depth range: `y < surfaceY - 5` → `y < surfaceY - 2`
- Lower the min Y: `y > 5` → `y > 2`

### "World is all air/all solid"
- Check ISO_SURFACE threshold (should be 0.5)
- Verify density calculation returns positive for underground

### "Terrain looks blocky"
- Reduce STEP size (8 → 4 or 2)
- Add more noise octaves for detail

---

## Example: Creating an Ocean Biome

```csharp
// Step 1: Add to enum
private enum Biome { Plains, Mountains, Desert, Forest, Ocean }

// Step 2: Map in GetBiome
if (biomeValue < -0.7f) return Biome.Ocean;

// Step 3: Parameters
Biome.Ocean => (0f, 2f),  // Sea level with small variation

// Step 4: Special ocean logic in SampleDensity
if (biome == Biome.Ocean)
{
    // Water level at Y=10
    if (y < 10)
    {
        density = ISO + 1.0f;  // Water is "solid" for rendering
    }
    else
    {
        density = ISO - 1.0f;  // Air above water
    }
}

// Step 5: No caves in ocean floor
Biome.Ocean => 0.0f  // No caves
```

This creates underwater terrain with a water surface at Y=10.
