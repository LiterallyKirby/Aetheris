# Aetheris Game Architecture Documentation

## Table of Contents
1. [Overview](#overview)
2. [Server Architecture](#server-architecture)
3. [Client Architecture](#client-architecture)
4. [World Generation System](#world-generation-system)
5. [Networking Protocol](#networking-protocol)
6. [Rendering Pipeline](#rendering-pipeline)
7. [Physics & Collision](#physics--collision)
8. [Data Flow](#data-flow)

---

## Overview

**Aetheris** is a multiplayer voxel-based game built in C# using OpenTK for graphics and networking. It features:

- **Procedurally generated terrain** using noise-based algorithms
- **Marching cubes** for smooth voxel-to-mesh conversion
- **Client-server architecture** with TCP for chunk data and UDP for player state
- **Quake-style movement** with bunny-hopping physics
- **Dual mesh system**: render meshes for visuals, collision meshes for physics
- **Advanced rendering**: PSX-style effects, texture atlas support, frustum culling

---

## Server Architecture

### Core Components

#### `Server.cs`
The main server class that orchestrates all server-side operations.

**Key Responsibilities:**
- **Network Management**: Handles TCP connections (port 42069) and UDP packets (port 42070)
- **Chunk Generation**: Generates chunks on-demand using `ChunkManager` and `WorldGen`
- **Mesh Caching**: LRU cache for up to 20,000 pre-generated meshes
- **Player State**: Tracks player positions, velocities, and rotations via UDP
- **Tick Loop**: Runs at 60 TPS for game logic updates

**Important Features:**
```csharp
// Mesh cache for performance
ConcurrentDictionary<ChunkCoord, (float[] renderMesh, CollisionMesh collisionMesh)> meshCache

// Generation locks prevent duplicate work
ConcurrentDictionary<ChunkCoord, SemaphoreSlim> generationLocks

// 60 TPS timing
const double TickRate = 60.0;
```

**Server Workflow:**
1. Client connects via TCP
2. Client requests chunk at coordinate (cx, cy, cz)
3. Server checks cache → if miss, generates chunk
4. Server generates both render mesh and collision mesh
5. Server sends both meshes to client
6. Mesh is cached for future requests

#### `ServerConfig.cs`
Configuration constants for the server:
```csharp
SERVER_PORT = 42069      // TCP port
CHUNK_SIZE = 32          // X/Z dimensions
CHUNK_SIZE_Y = 96        // Y dimension (vertical)
RENDER_DISTANCE = 4      // Default render distance
WORLD_SEED = 69420       // Procedural generation seed
```

### World Generation

#### `WorldGen.cs`
Sophisticated noise-based terrain generator with biome blending.

**Biome System:**
- **Plains**: Flat grasslands (base height: 32, amplitude: 5)
- **Forest**: Rolling hills with trees (base height: 36, amplitude: 8)
- **Desert**: Sandy flatlands (base height: 30, amplitude: 4)
- **Mountains**: Dramatic peaks (base height: 55, amplitude: 28)

**Cave Generation:**
Uses multiple noise layers for realistic cave systems:
- **Worm caves**: 3D tunnels using noise intersection
- **Caverns**: Large open chambers
- **Deep caves**: Complex underground networks
- **Smooth transitions**: Uses `SmoothThreshold()` to prevent chunk boundary artifacts

**Critical Feature - Continuous Density:**
```csharp
// Smooth threshold prevents gaps at chunk boundaries
private static float SmoothThreshold(float value, float threshold, float smoothness)
{
    // Hermite interpolation for seamless transitions
    float t = (value - (threshold - smoothness)) / (2f * smoothness);
    t = Clamp(t, 0f, 1f);
    float smooth = t * t * (3f - 2f * t);
    return smooth * (value - (threshold - smoothness));
}
```

#### `ChunkManager.cs`
Optimized chunk storage and column caching.

**Features:**
- **Column Caching**: Pre-computes 2D noise data (20,000 entry LRU cache)
- **Parallel Generation**: Uses `Parallel.For` for large chunks
- **Collision Mesh Support**: Optional collision mesh generation
- **Fast Sampling**: `SampleDensityFast()` avoids redundant noise calculations

#### `MarchingCubes.cs`
Converts voxel density fields into triangular meshes.

**Algorithm:**
1. Sample density at 8 corners of each cube
2. Determine cube configuration (0-255 possibilities)
3. Interpolate edge vertices using linear interpolation
4. Generate triangles based on lookup tables
5. Calculate normals via cross product
6. Validate triangles (no degenerate/zero-area triangles)

**Dual Mesh Generation:**
```csharp
public static (float[] renderMesh, CollisionMesh collisionMesh) GenerateMeshes(...)
{
    // Render mesh: 7 floats per vertex (pos, normal, blockType)
    // Collision mesh: separate vertex/index lists for physics
}
```

**Quality Features:**
- **Epsilon tolerance**: Prevents numerical instability at boundaries
- **Triangle validation**: Filters degenerate triangles
- **Improved interpolation**: `LerpImproved()` for stable vertex positions
- **Normal validation**: Ensures valid normals (no NaN values)

### Networking

#### TCP Protocol (Chunk Data)
**Request Format:**
```
[4 bytes] chunk_x (int32)
[4 bytes] chunk_y (int32)
[4 bytes] chunk_z (int32)
```

**Response Format:**
```
// Render Mesh
[4 bytes] payload_length (int32)
[4 bytes] vertex_count (int32)
[N bytes] vertex_data (7 floats per vertex: x, y, z, nx, ny, nz, blockType)

// Collision Mesh
[4 bytes] payload_length (int32)
[4 bytes] vertex_count (int32)
[4 bytes] index_count (int32)
[N bytes] vertices (3 floats per vertex: x, y, z)
[M bytes] indices (int32 per index)
```

#### UDP Protocol (Player State)
**Packet Types:**
- `PlayerPosition` (1): Position, velocity, rotation
- `PlayerInput` (2): Input state
- `EntityUpdate` (3): Broadcast player positions
- `KeepAlive` (4): Connection health check

**PlayerPosition Packet:**
```
[1 byte]  packet_type
[4 bytes] position_x (float)
[4 bytes] position_y (float)
[4 bytes] position_z (float)
[4 bytes] velocity_x (float)
[4 bytes] velocity_y (float)
[4 bytes] velocity_z (float)
[4 bytes] yaw (float)
[4 bytes] pitch (float)
```

---

## Client Architecture

### Core Components

#### `Client.cs`
Manages server connection and chunk loading.

**Auto-Tuning System:**
Based on render distance, automatically adjusts:
- `MaxConcurrentLoads`: Parallel chunk requests
- `ChunksPerUpdateBatch`: Chunks to request per update
- `UpdatesPerSecond`: Frequency of chunk checks
- `MaxPendingUploads`: Queue size limit

**Example:**
```csharp
// Render distance 16:
MaxConcurrentLoads = 16
ChunksPerUpdateBatch = 128
UpdatesPerSecond = 20
MaxPendingUploads = 64
```

**Chunk Loading Pipeline:**
1. `ChunkUpdateLoopAsync()`: Checks player position every 50ms
2. `CheckAndUpdateChunks()`: Determines needed chunks
3. **Priority System**: Closer chunks load first, chunks below player prioritized
4. `ChunkLoaderLoopAsync()`: Parallel loading with semaphore
5. `LoadChunkAsync()`: Request → Receive → Upload to renderer

**Unloading Strategy:**
- Chunks beyond `renderDistance + 2` are unloaded
- Gradual unloading (4 chunks per check) prevents lag spikes

#### `Game.cs`
Main game window and loop using OpenTK.

**Responsibilities:**
- **Window Management**: 1920x1080 window with grabbed cursor
- **Input Handling**: Keyboard/mouse input processing
- **Render Loop**: Calls renderer at display refresh rate
- **Update Loop**: Player physics and chunk loading updates
- **Logging**: Dual output to console and `physics_debug.log`

**Key Features:**
```csharp
// Chunk update throttling
private float chunkUpdateTimer = 0f;
private const float CHUNK_UPDATE_INTERVAL = 0.5f;

// Debug commands
Keys.R: Raycast debug (shows block at crosshair)
Keys.B: Biome info at player position
Keys.Equal/Minus: Adjust render distance
```

#### `Player.cs`
Quake-style movement physics with bunny-hopping.

**Physics Constants:**
```csharp
MAX_VELOCITY = 9.5f          // Speed cap
GROUND_ACCEL = 14f           // Ground acceleration
AIR_ACCEL = 2.8f             // Air thrust
AIR_CONTROL = 0.95f          // Air strafing control
FRICTION = 6f                // Ground friction
JUMP_VELOCITY = 7f           // Jump strength
GRAVITY = 20f                // Downward acceleration
STEP_HEIGHT = 1.25f          // Max step-up height
```

**Movement Algorithm:**
1. **Mouse Look**: Update pitch/yaw from mouse delta
2. **Ground Check**: Raycast downward at 5 positions
3. **Wish Direction**: Compute movement vector from WASD
4. **Friction/Acceleration**: Apply physics based on grounded state
5. **Air Control**: Quake-style velocity rotation in air
6. **Move and Slide**: Sub-stepped collision resolution
7. **Step-Up**: Automatic climbing of small obstacles
8. **Slope Snapping**: Stay attached to slopes when grounded

**Collision Detection:**
Uses a **box collider** with 5 height samples and 4 corner checks per sample:
```csharp
PLAYER_WIDTH = 1.2f    // X/Z dimensions
PLAYER_HEIGHT = 3.6f   // Y dimension
EYE_HEIGHT = 3.2f      // Camera offset
```

Collision queries `WorldGen.SampleDensity()` at integer coordinates.

### Rendering Pipeline

#### `Renderer.cs`
Advanced rendering system with PSX effects and frustum culling.

**Features:**
- **Dual Shader System**: PSX effects or standard shader
- **Frustum Culling**: Only renders visible chunks
- **Mesh Caching**: Stores uploaded mesh data for physics
- **Texture Atlas**: Supports external or procedural textures
- **Distance Sorting**: Front-to-back rendering for early-Z
- **Concurrent Uploads**: Max 8 mesh uploads per frame

**Rendering Pipeline:**
1. `ProcessPendingUploads()`: Upload queued meshes to GPU
2. `ExtractFrustumPlanes()`: Calculate view frustum
3. **Frustum Culling**: Test each chunk's bounding sphere
4. **Distance Culling**: Filter chunks beyond render distance
5. **Sort Meshes**: Front-to-back by distance to camera
6. **Render**: Draw all visible meshes with shader

**Mesh Upload Process:**
```csharp
// Convert 7-float stride (pos, normal, blockType) to 8-float (pos, normal, UV)
ConvertMeshWithBlockTypeToUVs(interleavedData)

// Upload to GPU
VAO: Vertex Array Object
VBO: Vertex Buffer Object (position, normal, UV)

// Attributes:
layout(location = 0) in vec3 aPos;      // Position
layout(location = 1) in vec3 aNormal;   // Normal
layout(location = 2) in vec2 aUV;       // Texture coordinates
```

#### `AtlasManager.cs`
Texture atlas management for block textures.

**Block-to-Tile Mapping:**
```csharp
Stone   → Tile 0
Dirt    → Tile 1
Grass   → Tile 2
Sand    → Tile 3
Snow    → Tile 4
Gravel  → Tile 5
Wood    → Tile 6
Leaves  → Tile 7
```

**UV Calculation:**
- 4x4 texture atlas (256x256 typical)
- Each tile is 64x64 pixels
- 0.2% padding prevents texture bleeding
- Supports face-specific textures (grass top vs side)

**Triplanar Projection:**
The renderer uses world-space coordinates to project textures:
1. Determine dominant normal axis (X, Y, or Z)
2. Project texture using perpendicular axes
3. Add per-block random rotation (0°, 90°, 180°, 270°)
4. Map to atlas UV coordinates

#### `PSXVisualEffects.cs`
PlayStation 1-style visual effects (mentioned but not included in documents).

**Typical PSX Effects:**
- Vertex snapping (grid-based jitter)
- Affine texture mapping
- Low-res rendering with upscaling
- Dithering
- Limited color depth

---

## Physics & Collision

### Collision Mesh System

**Purpose:** Separate physics mesh from render mesh for performance.

**Render Mesh:**
- High detail (every marching cubes triangle)
- Used for visuals only
- Format: `float[] { x, y, z, nx, ny, nz, blockType, ... }`

**Collision Mesh:**
- Simplified geometry (optional decimation)
- Used for physics queries
- Format: `List<Vector3> vertices, List<int> indices`

**Generation:**
```csharp
// In MarchingCubes.GenerateMeshes():
var renderMesh = /* full detail triangles */
var collisionMesh = new CollisionMesh {
    Vertices = /* triangle vertices */,
    Indices = /* triangle indices */
};
```

**Simplification:**
```csharp
// Optional: Keep only every Nth triangle
CollisionSimplification = 2f  // Half the triangles
```

### Player Collision

Currently uses **voxel-based collision** (not mesh-based):

```csharp
private bool IsSolidAt(Vector3 pos)
{
    float density = WorldGen.SampleDensity(
        (int)MathF.Round(pos.X),
        (int)MathF.Round(pos.Y),
        (int)MathF.Round(pos.Z)
    );
    return density > 0.5f;  // Isosurface threshold
}
```

**Box Collider Sampling:**
- 5 height levels
- 5 sample points per level (center + 4 corners)
- Total: 25 density queries per collision check

**Move and Slide Algorithm:**
1. Sub-step movement into small increments (0.2 units)
2. For each sub-step:
   - Try full movement → blocked? Try horizontal only
   - Try step-up → blocked? Try axis-aligned slide
3. Apply slope snapping when grounded

---

## Data Flow

### Chunk Request Flow

```
[Client]                    [Server]
   |                           |
   |-- TCP: (cx, cy, cz) ----->|
   |                           |
   |                  Check mesh cache
   |                           |
   |                    Cache miss?
   |                           |
   |              Generate chunk (WorldGen)
   |                           |
   |          Generate render mesh (MarchingCubes)
   |                           |
   |         Generate collision mesh (MarchingCubes)
   |                           |
   |               Cache both meshes
   |                           |
   |<---- TCP: render mesh ----|
   |<--- TCP: collision mesh --|
   |                           |
Upload to GPU (Renderer)      |
Store collision mesh          |
   |                           |
```

### Player State Flow

```
[Client]                    [Server]
   |                           |
   |                     UDP: PlayerPosition
   |-------------------------->|
   |                           |
   |                  Update player state
   |                           |
   |                  Broadcast to other players
   |                           |
   |<------- UDP: EntityUpdate --|
   |                           |
Update other player positions  |
   |                           |
```

### Rendering Frame Flow

```
[Game Loop]
   |
   ├─> Player.Update(deltaTime)
   |    ├─> Update mouse look
   |    ├─> Check ground
   |    ├─> Apply friction/acceleration
   |    ├─> Move and slide
   |    └─> Collision detection
   |
   ├─> Client.CheckAndUpdateChunks()
   |    ├─> Calculate needed chunks
   |    ├─> Priority sort
   |    └─> Enqueue requests
   |
   ├─> Renderer.ProcessPendingUploads()
   |    └─> Upload up to 8 meshes/frame
   |
   └─> Renderer.Render(projection, view, cameraPos)
        ├─> Extract frustum planes
        ├─> Frustum culling
        ├─> Distance culling
        ├─> Sort by distance
        └─> Draw visible chunks
```

---

## Key Design Patterns

### Performance Optimizations

1. **LRU Caching**
   - 20,000 mesh cache on server
   - 20,000 column cache in ChunkManager
   - Prevents redundant generation

2. **Parallel Processing**
   - Column generation (WorldGen)
   - Chunk loading (Client)
   - Density sampling (MarchingCubes)

3. **Frustum Culling**
   - Bounding sphere tests
   - Only renders visible chunks
   - Typically 30-60% of chunks rendered

4. **Sub-Stepping**
   - Movement divided into small steps
   - Prevents tunneling through walls
   - Maintains collision accuracy

5. **Lazy Generation**
   - Chunks generated on-demand
   - Collision meshes optional
   - Meshes uploaded gradually (8/frame)

### Thread Safety

- **ConcurrentDictionary**: Used for all shared state
- **SemaphoreSlim**: Guards generation and network access
- **ConcurrentQueue**: Thread-safe mesh upload queue
- **Interlocked**: Atomic cache size updates

---

## Configuration Guide

### Server Performance Tuning

**High Player Count (32+):**
```csharp
MaxCachedMeshes = 50000  // More cache
CHUNK_SIZE = 16          // Smaller chunks
```

**Low-End Server:**
```csharp
MaxCachedMeshes = 5000   // Less memory
SIMULATION_DISTANCE = 2  // Less active area
```

### Client Performance Tuning

**High-End PC:**
```csharp
RENDER_DISTANCE = 16
MaxConcurrentLoads = 32
ChunksPerUpdateBatch = 256
```

**Low-End PC:**
```csharp
RENDER_DISTANCE = 4
MaxConcurrentLoads = 4
ChunksPerUpdateBatch = 32
UsePSXEffects = false  // Disable fancy shaders
```

### World Generation Tuning

**More Caves:**
```csharp
caveIntensity *= 1.5f    // In WorldGen.GetColumnData()
```

**Flatter Terrain:**
```csharp
ampPlains = 3f           // Reduce amplitude
ampMount = 15f
```

**Different Biomes:**
```csharp
// Adjust biome center values in GetBiomeWeights()
const float cPlains = -0.8f;
const float cForest = -0.2f;
const float cDesert = 0.3f;
const float cMountains = 0.85f;
```

---

## Common Issues & Solutions

### Chunk Boundary Gaps

**Cause:** Inconsistent density sampling at chunk edges.

**Solution:** 
- Use world coordinates for density sampling
- Apply epsilon tolerance in marching cubes
- Use `SmoothThreshold()` for caves

### Collision Tunneling

**Cause:** High velocity + large time steps.

**Solution:**
- Sub-step movement (0.2 unit increments)
- Box collider with multiple sample points
- Step-up logic for small obstacles

### Texture Bleeding

**Cause:** Texture atlas sampling outside tile bounds.

**Solution:**
- 0.2% padding in UV coordinates
- Nearest-neighbor filtering (no mipmapping between tiles)
- Clamp UV coordinates to tile bounds

### Memory Growth

**Cause:** Unlimited mesh caching.

**Solution:**
- LRU cache with fixed size
- Periodic cleanup (every 60 seconds)
- Remove 25% of cache when full

---

## Future Enhancements

### Planned Features

1. **Mesh-Based Collision**
   - Use collision meshes instead of voxel queries
   - Physics engine integration (Jitter, BepuPhysics)
   - Ray-triangle intersection for raycasts

2. **Dynamic Lighting**
   - Per-vertex lighting baking
   - Shadow maps for directional lights
   - Ambient occlusion

3. **Multiplayer Improvements**
   - Entity interpolation
   - Client-side prediction
   - Server-authoritative physics

4. **World Persistence**
   - Save/load chunk data
   - Player inventory
   - Block modification

5. **Optimization**
   - Greedy meshing (reduce triangles)
   - Level of Detail (LOD) system
   - Occlusion culling

---

## Conclusion

Aetheris demonstrates a sophisticated voxel game architecture with:
- Efficient procedural generation
- Smooth marching cubes meshing
- Robust client-server networking
- Quake-style movement physics
- Advanced rendering techniques

The dual mesh system (render + collision) and continuous density fields ensure visual quality while maintaining performance. The modular design allows easy extension for new features like multiplayer gameplay, world editing, and advanced physics.
