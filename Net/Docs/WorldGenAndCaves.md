# Aetheris World Generation Documentation

## Overview
The Aetheris world generator creates infinite voxel terrain using **noise-based generation** and the **marching cubes algorithm**. The system uses a density field where `density > 0.5 = solid` and `density < 0.5 = air`, with support for **textured block types**, **negative Y coordinates**, and **extremely deep cave systems**.

---

## World Coordinate System

### Y-Axis Range
The world now supports **negative Y coordinates** for ultra-deep underground exploration:

| Y Range | Description |
|---------|-------------|
| **Y > 80** | Sky / High mountains |
| **Y = 20-80** | Surface terrain (varies by biome) |
| **Y = 0-20** | Shallow underground |
| **Y = -32 to 0** | Deep underground |
| **Y = -64 to -32** | Ultra-deep caverns (the "Deep Dark") |
| **Y ≤ -64** | Bedrock (unbreakable) |

**Total vertical range**: ~144 blocks from bedrock to mountain peaks (compared to Minecraft's ~384 blocks in 1.18+)

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
    // Add more: DeepSlate = 9, AncientStone = 10, etc.
}
```

---

## Cave System (ULTRA-DEEP)

### Cave Depth Layers

The new cave system extends **158 blocks deep** (from surface at Y=80 down to Y=-64):

#### 1. **Shallow Caves** (Surface to Surface-20)
```csharp
if (y > surfaceY - 20)
{
    float worm = (c1 + c2) * 0.5f;
    if (worm > 0.12f)
        density -= (worm - 0.12f) * 100.0f * caveIntensity;
}
```
- **Characteristics**: Tight winding tunnels
- **Frequency**: Medium
- **Purpose**: Early exploration, connects to surface

#### 2. **Mid-Depth Caves** (Y=30 to shallow)
```csharp
else if (y > 30)
{
    // Worm tunnels, medium caverns, large chambers
    // Thresholds: 0.08, 0.2, 0.35
    // Strength: 150x, 40x, 80x
}
```
- **Characteristics**: Large interconnected tunnel networks
- **Frequency**: High
- **Purpose**: Main exploration layer, resource-rich

#### 3. **Deep Caves** (Y=0 to 30)
```csharp
else if (y > 0)
{
    // Dense tunnels, mega caverns
    // Threshold: 0.05 (very permissive)
    // Strength: Up to 50,000x density reduction
}
```
- **Characteristics**: MASSIVE chambers, underground lakes
- **Frequency**: Very high
- **Features**: Huge open spaces suitable for underground bases

#### 4. **Ultra-Deep Caves** (Y=-64 to 0) ⭐ NEW
```csharp
else  // y <= 0 && y > -64
{
    // Ancient caverns with colossal chambers
    // Thresholds: 0.02 (extremely permissive)
    // Strength: Up to 100,000x reduction
}
```
- **Characteristics**: Void-like spaces, ancient chambers
- **Frequency**: Extremely high
- **Features**: 
  - Deepest layer at Y < -32: The "Abyss" with 1200x strength carving
  - Perfect for endgame content, rare ores, boss arenas
  - Dangerous to navigate due to scale

### Cave Generation Parameters

| Layer | Y Range | Threshold | Max Strength | Coverage |
|-------|---------|-----------|--------------|----------|
| Shallow | Surface-20 | 0.12 | 100x | ~40% |
| Mid-Depth | 30 to shallow | 0.08 | 150x | ~60% |
| Deep | 0 to 30 | 0.05 | 50,000x | ~75% |
| **Ultra-Deep** | **-64 to 0** | **0.02** | **100,000x** | **~85%** |
| **Abyss** | **< -32** | **0.08** | **1,200x** | **~90%** |

**Result**: The deeper you go, the more open and cavernous it becomes, creating a true sense of descending into an alien underworld.

---

## Adding Features to Deep Underground

### Example: Depth-Based Ore Distribution

```csharp
// In GetBlockType() after determining base stone type:

// Coal - common, shallow (like real coal seams)
if (y >= 10 && y <= 70)
{
    float coalNoise = oreNoise.GetNoise(x, y, z);
    if (coalNoise > 0.6f)
        return BlockType.Coal;
}

// Iron - common to mid-depth
if (y >= -10 && y <= 50)
{
    float ironNoise = oreNoise.GetNoise(x * 1.1f, y * 1.1f, z * 1.1f);
    if (ironNoise > 0.65f)
        return BlockType.Iron;
}

// Gold - rare, deep
if (y >= -30 && y <= 20)
{
    float goldNoise = oreNoise.GetNoise(x * 1.3f, y * 1.3f, z * 1.3f);
    if (goldNoise > 0.75f)
        return BlockType.Gold;
}

// Diamond - very rare, very deep
if (y >= -50 && y <= -5)
{
    float diamondNoise = oreNoise.GetNoise(x * 0.7f, y * 0.7f, z * 0.7f);
    if (diamondNoise > 0.82f)
        return BlockType.Diamond;
}

// Ancient/Mythril - ultra-rare, ultra-deep (endgame ore)
if (y >= -64 && y <= -35)
{
    float ancientNoise = oreNoise.GetNoise(x * 0.5f, y * 0.5f, z * 0.5f);
    if (ancientNoise > 0.88f)
        return BlockType.AncientOre;
}
```

### Example: Deep Stone Variants

Add visual variety to deep underground:

```csharp
// In GetBlockType(), before returning default BlockType.Stone:

// Transition to darker stone in deep underground
if (y < 0 && y > -32)
{
    return BlockType.DeepSlate;  // Darker, tougher-looking stone
}

// Ancient stone in the deepest depths
if (y <= -32 && y > -64)
{
    return BlockType.AncientStone;  // Mysterious, ancient-looking blocks
}

// Could add glowing fungi/crystals for ambiance
if (y < -20 && ((x + y + z) % 23) == 0)
{
    return BlockType.GlowingCrystal;
}
```

### Example: Lava Lakes at Depth

```csharp
// In SampleDensity(), after cave carving:

// Create lava pools at deep levels
if (y < -40)
{
    float lavaPool = caveNoise3.GetNoise(x * 0.05f, y * 0.05f, z * 0.05f);
    if (lavaPool > 0.5f && lavaPool < 0.55f)  // Thin layer = pool bottom
    {
        density = ISO + 1.0f;  // Solid for lava to sit on
    }
}

// Then in GetBlockType():
if (y < -40 && y > GetLavaPoolFloor(x, z) && y < GetLavaPoolFloor(x, z) + 3)
{
    return BlockType.Lava;  // 3-block deep lava pool
}
```

---

## Biome System (Unchanged)

The biome system works identically but now applies to the full -64 to 80+ Y range:

- Biomes are determined by XZ position only (2D)
- Cave intensity varies by biome
- Deep underground retains biome influence for ore distribution

---

## Performance Considerations

### Negative Y Impact

**Chunk Height**: With Y=-64 to Y=96, your chunks are now **160 blocks tall** (compared to 96 before).

**Options:**
1. **Keep CHUNK_SIZE_Y = 96**, chunks stack vertically (Y=-64 to -32, Y=-32 to 64, Y=64 to 96)
2. **Increase to CHUNK_SIZE_Y = 160** for single-piece chunks (more memory, simpler)
3. **Use variable chunk height**: Surface chunks = 96 tall, underground chunks = 32 tall

**Recommended**: Keep 96 and let chunks stack. Underground chunks (Y=-64 to 32) will be VERY full of geometry due to dense cave systems.

### Memory Usage

Deep caves = more triangles. Monitor:
- Vertex buffer sizes (underground chunks may be 2-3x larger)
- Mesh generation time (may need to increase `maxPerFrame` budget)
- GPU memory (more visible chunks when underground)

---

## Configuration

### Adjusting Cave Depth

To change bedrock level:

```csharp
// In SampleDensity():
if (y <= -128)  // Move bedrock deeper
{
    density += (-127 - y) * 100f;
}

// In cave generation:
if (y > -128 && y < surfaceY - 2 && caveIntensity > 0)  // Extend caves
```

### Adjusting Cave Density

Make caves more/less common:

```csharp
// More caves: Lower thresholds
if (worm > 0.05f)  // Was 0.12f

// Fewer caves: Raise thresholds
if (worm > 0.25f)  // Was 0.12f

// Bigger caves: Increase strength multipliers
density -= (worm - 0.12f) * 200.0f;  // Was 100.0f

// Smaller caves: Decrease strength
density -= (worm - 0.12f) * 50.0f;  // Was 100.0f
```

---

## Gameplay Implications

### Progression Design

The depth system naturally creates a progression curve:

1. **Surface (Y=20-80)**: Building, farming, initial resources
2. **Shallow Caves (Y=0-20)**: Coal, iron, basic exploration
3. **Deep Caves (Y=-32-0)**:
