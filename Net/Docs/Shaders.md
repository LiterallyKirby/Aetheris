# OpenGL Shader Programming Guide

## Table of Contents
1. [Shader Basics](#shader-basics)
2. [The Graphics Pipeline](#the-graphics-pipeline)
3. [GLSL Language Reference](#glsl-language-reference)
4. [Writing Your First Shader](#writing-your-first-shader)
5. [Advanced Techniques](#advanced-techniques)
6. [PSX Effects Breakdown](#psx-effects-breakdown)
7. [Common Patterns](#common-patterns)
8. [Debugging Shaders](#debugging-shaders)

---

## Shader Basics

### What Are Shaders?

Shaders are small programs that run **on the GPU** (graphics card). They process vertices and pixels in parallel, making them extremely fast for graphics work.

**Two main types:**
- **Vertex Shader**: Runs once per vertex (corner of triangle)
- **Fragment Shader**: Runs once per pixel on screen

### Why Use Shaders?

- **Performance**: GPU can process millions of vertices/pixels per frame
- **Visual Effects**: Lighting, fog, distortion, color grading
- **Customization**: Complete control over how things look

---

## The Graphics Pipeline

```
Your Mesh Data → Vertex Shader → Rasterization → Fragment Shader → Screen
   (CPU)             (GPU)                             (GPU)
```

### Step-by-Step:

1. **CPU sends data**: positions, normals, UVs, textures
2. **Vertex Shader**: Transforms vertices to screen space
3. **Rasterization**: GPU fills in pixels between vertices
4. **Fragment Shader**: Colors each pixel
5. **Output**: Final image on screen

---

## GLSL Language Reference

GLSL (OpenGL Shading Language) is similar to C but designed for parallel processing.

### Basic Types

```glsl
float x = 1.0;           // Single number
vec2 uv = vec2(0.5, 0.5); // 2D vector
vec3 pos = vec3(1, 2, 3); // 3D vector
vec4 color = vec4(1, 0, 0, 1); // RGBA color
mat4 transform;           // 4x4 matrix
sampler2D texture;        // Texture reference
```

### Built-in Functions

```glsl
// Math
float d = length(vec3(1, 2, 3));  // Vector length
vec3 n = normalize(someVector);    // Make length = 1
float d = dot(v1, v2);             // Dot product
vec3 c = cross(v1, v2);            // Cross product
float x = mix(a, b, 0.5);          // Linear interpolate (lerp)
float x = clamp(value, 0.0, 1.0);  // Constrain to range
float x = smoothstep(0.0, 1.0, t); // Smooth interpolation

// Trigonometry
float s = sin(angle);
float c = cos(angle);
float t = tan(angle);

// Exponential
float p = pow(base, exponent);
float e = exp(x);
float l = log(x);
float r = sqrt(x);

// Common
float a = abs(x);
float f = floor(x);
float c = ceil(x);
float m = mod(x, y);
float mn = min(a, b);
float mx = max(a, b);

// Texture sampling
vec4 color = texture(sampler, uv);
```

### Variable Qualifiers

```glsl
in vec3 aPos;           // INPUT from previous stage
out vec3 vColor;        // OUTPUT to next stage
uniform mat4 uModel;    // UNIFORM (set from C#, same for all vertices/pixels)
const float PI = 3.14159; // CONSTANT
```

---

## Writing Your First Shader

### Minimal Vertex Shader

```glsl
#version 330 core

// Inputs from mesh
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUV;

// Outputs to fragment shader
out vec2 vUV;

// Uniforms from C#
uniform mat4 uProjection;
uniform mat4 uView;
uniform mat4 uModel;

void main()
{
    // Transform vertex to screen space
    vec4 worldPos = uModel * vec4(aPos, 1.0);
    vec4 viewPos = uView * worldPos;
    gl_Position = uProjection * viewPos;
    
    // Pass UV to fragment shader
    vUV = aUV;
}
```

**What this does:**
1. Takes vertex position and UV from mesh
2. Transforms position through model → view → projection matrices
3. Sets `gl_Position` (REQUIRED - tells GPU where vertex is on screen)
4. Passes UV coordinates to fragment shader

### Minimal Fragment Shader

```glsl
#version 330 core

// Input from vertex shader (automatically interpolated)
in vec2 vUV;

// Output color
out vec4 FragColor;

// Texture
uniform sampler2D uTexture;

void main()
{
    // Sample texture at UV coordinate
    vec3 texColor = texture(uTexture, vUV).rgb;
    
    // Output final color
    FragColor = vec4(texColor, 1.0);
}
```

**What this does:**
1. Receives interpolated UV from vertex shader
2. Samples texture at that UV
3. Outputs final pixel color

---

## Advanced Techniques

### Lighting Models

#### Lambert Diffuse (Basic)
```glsl
vec3 lightDir = normalize(vec3(0.5, 1.0, 0.3));
float diffuse = max(dot(normal, lightDir), 0.0);
vec3 color = baseColor * diffuse;
```

#### Blinn-Phong (Diffuse + Specular)
```glsl
// Diffuse
vec3 lightDir = normalize(lightPosition - worldPos);
float diffuse = max(dot(normal, lightDir), 0.0);

// Specular
vec3 viewDir = normalize(cameraPos - worldPos);
vec3 halfDir = normalize(lightDir + viewDir);
float spec = pow(max(dot(normal, halfDir), 0.0), shininess);

vec3 color = ambient + diffuse * baseColor + spec * specularColor;
```

### Fog

#### Linear Fog
```glsl
float fogStart = 10.0;
float fogEnd = 100.0;
float fogFactor = clamp((fogEnd - dist) / (fogEnd - fogStart), 0.0, 1.0);
vec3 final = mix(fogColor, objectColor, fogFactor);
```

#### Exponential Fog (Better)
```glsl
float fogDensity = 0.003;
float fogFactor = exp(-dist * fogDensity);
fogFactor = clamp(fogFactor, 0.0, 1.0);
vec3 final = mix(fogColor, objectColor, fogFactor);
```

#### Exponential Squared (Best)
```glsl
float fogFactor = exp(-(dist * fogDensity) * (dist * fogDensity));
vec3 final = mix(fogColor, objectColor, fogFactor);
```

### Normal Mapping
```glsl
// Sample normal from texture
vec3 tangentNormal = texture(normalMap, uv).xyz * 2.0 - 1.0;

// Transform to world space (requires TBN matrix)
vec3 N = normalize(TBN * tangentNormal);
```

### Parallax Mapping
```glsl
// Offset UV based on height map and view direction
vec2 viewDirTangent = normalize(tangentViewDir);
float height = texture(heightMap, uv).r;
vec2 offset = viewDirTangent.xy * (height * heightScale);
vec2 parallaxUV = uv - offset;
```

---

## PSX Effects Breakdown

### 1. Vertex Snapping (Geometric Jitter)

**How it works:**
```glsl
// In vertex shader
vec4 clipPos = uProjection * viewPos;

// Convert to screen space (-1 to 1)
vec2 screenPos = clipPos.xy / clipPos.w;

// Snap to grid (key magic here!)
screenPos = floor(screenPos / uVertexSnap) * uVertexSnap;

// Convert back to clip space
clipPos.xy = screenPos * clipPos.w;

gl_Position = clipPos;
```

**Why it works:**
- PS1 had limited vertex precision (integers, not floats)
- `floor()` rounds down, creating "steps" in position
- Smaller `uVertexSnap` = more jitter
- Creates that iconic PS1 wobble as camera moves

**Tweaking:**
- `1.0 / 32.0` = Heavy jitter (authentic PS1)
- `1.0 / 64.0` = Medium jitter (playable)
- `1.0 / 128.0` = Subtle jitter (modern)

### 2. Affine Texture Mapping

**How it works:**
```glsl
// In fragment shader
if (uAffineMapping > 0) {
    float scale = 32.0; // Precision reduction
    uv = floor(uv * scale) / scale;
}
vec3 color = texture(uTexture, uv).rgb;
```

**Why it works:**
- PS1 didn't do perspective-correct interpolation
- Reducing UV precision simulates this
- Causes textures to "warp" across polygons

**Warning:** Makes gameplay difficult! Skip for playable games.

### 3. Color Quantization (Banding)

**How it works:**
```glsl
// After all lighting/tonemapping
if (uColorBits > 0) {
    float levels = pow(2.0, float(uColorBits));
    color = floor(color * levels) / levels;
}
```

**Why it works:**
- PS1 had 5-bit color (32 levels per R/G/B)
- `floor()` rounds to nearest level
- Creates visible color steps (banding)

**Examples:**
- 5 bits = 32 levels (authentic PS1)
- 6 bits = 64 levels (playable, still looks retro)
- 8 bits = 256 levels (modern, no banding)

### 4. Dithering

**How it works:**
```glsl
// Simple ordered dithering
vec2 screenPos = gl_FragCoord.xy;
float pattern = mod(screenPos.x + screenPos.y * 2.0, 4.0) / 4.0;
float dither = (pattern - 0.5) / 255.0; // Very subtle noise
color += dither;
```

**Why it works:**
- Adds spatial noise pattern
- Tricks eye into seeing more colors than actually present
- Smooths out harsh color banding

---

## Common Patterns

### Coordinate Spaces

```glsl
// Local space (mesh coordinates)
vec3 localPos = aPos;

// World space (scene coordinates)
vec3 worldPos = (uModel * vec4(localPos, 1.0)).xyz;

// View space (camera coordinates)
vec3 viewPos = (uView * vec4(worldPos, 1.0)).xyz;

// Clip space (screen coordinates, before perspective divide)
vec4 clipPos = uProjection * vec4(viewPos, 1.0);

// NDC (Normalized Device Coordinates, -1 to 1)
vec3 ndc = clipPos.xyz / clipPos.w;
```

### Normal Transformation

```glsl
// WRONG (distorts normals when scaling)
vec3 normal = (uModel * vec4(aNormal, 0.0)).xyz;

// CORRECT (use normal matrix)
mat3 normalMatrix = transpose(inverse(mat3(uModel)));
vec3 normal = normalize(normalMatrix * aNormal);
```

### Texture Atlas UV Mapping

```glsl
// Single texture (tile in 4x4 atlas)
int tileX = tileID % 4;
int tileY = tileID / 4;

float uMin = float(tileX) / 4.0;
float vMin = float(tileY) / 4.0;
float uMax = float(tileX + 1) / 4.0;
float vMax = float(tileY + 1) / 4.0;

// Map local UV (0-1) to atlas region
vec2 atlasUV = vec2(
    mix(uMin, uMax, localUV.x),
    mix(vMin, vMax, localUV.y)
);
```

### Triplanar Mapping (No UVs needed!)

```glsl
// Sample texture from 3 directions
vec3 blendWeights = abs(normal);
blendWeights = blendWeights / (blendWeights.x + blendWeights.y + blendWeights.z);

vec3 xColor = texture(uTexture, worldPos.yz * scale).rgb;
vec3 yColor = texture(uTexture, worldPos.xz * scale).rgb;
vec3 zColor = texture(uTexture, worldPos.xy * scale).rgb;

vec3 color = xColor * blendWeights.x + 
             yColor * blendWeights.y + 
             zColor * blendWeights.z;
```

---

## Debugging Shaders

### Visualize Values as Colors

```glsl
// Visualize normals
FragColor = vec4(normal * 0.5 + 0.5, 1.0);

// Visualize UVs
FragColor = vec4(uv, 0.0, 1.0);

// Visualize depth
float depth = gl_FragCoord.z;
FragColor = vec4(vec3(depth), 1.0);

// Visualize any float value
float value = someCalculation;
FragColor = vec4(vec3(value), 1.0);
```

### Common Errors

**Black screen:**
- Uniforms not set (default to 0)
- Lighting calculations multiply by 0
- Texture not bound
- Missing `gl_Position` in vertex shader

**Pink/magenta:**
- Texture failed to load
- UV coordinates out of range

**Geometry invisible:**
- Wrong coordinate transformation
- Culling facing wrong way
- Vertices behind camera

**Compilation errors:**
- Check console for shader log
- Syntax error in GLSL
- Wrong GLSL version
- Undefined uniform/variable

---

## Creating Custom Effects

### Outline Effect

```glsl
// Vertex shader - extrude along normal
vec3 outlinePos = aPos + aNormal * outlineThickness;
gl_Position = uProjection * uView * uModel * vec4(outlinePos, 1.0);

// Fragment shader - solid color
FragColor = vec4(outlineColor, 1.0);
```

### Dissolve Effect

```glsl
float noise = texture(noiseTexture, uv).r;
if (noise < dissolveAmount) {
    discard; // Don't draw this pixel
}
// Add glow at edge
float edge = smoothstep(dissolveAmount - 0.1, dissolveAmount, noise);
FragColor = mix(glowColor, baseColor, edge);
```

### Water Effect

```glsl
// Animate UVs
vec2 wave1 = uv + vec2(sin(uTime * 0.5 + uv.y * 10.0) * 0.02);
vec2 wave2 = uv + vec2(cos(uTime * 0.3 + uv.x * 8.0) * 0.03);

vec3 color1 = texture(uTexture, wave1).rgb;
vec3 color2 = texture(uTexture, wave2).rgb;
vec3 final = mix(color1, color2, 0.5);

// Add fresnel (edges brighter)
float fresnel = pow(1.0 - dot(viewDir, normal), 3.0);
final += vec3(0.3, 0.5, 0.7) * fresnel;
```

### Cell Shading (Toon)

```glsl
float diffuse = max(dot(normal, lightDir), 0.0);

// Quantize to bands
if (diffuse > 0.8) diffuse = 1.0;
else if (diffuse > 0.4) diffuse = 0.6;
else if (diffuse > 0.1) diffuse = 0.3;
else diffuse = 0.1;

vec3 color = baseColor * diffuse;
```

---

## Best Practices

1. **Keep shaders simple** - GPU is fast but parallel, avoid branches when possible
2. **Use uniforms sparingly** - Uniforms are expensive to update
3. **Prefer texture lookups** - Pack data into textures (noise, gradients, etc)
4. **Watch precision** - Use `highp`, `mediump`, `lowp` appropriately on mobile
5. **Test on target hardware** - Desktop GPUs are very forgiving
6. **Comment complex math** - Future you will thank present you
7. **Use constants** - `const float PI = 3.14159;` instead of magic numbers

---

## Resources

- **OpenGL Reference**: https://www.khronos.org/opengl/wiki/
- **GLSL Reference**: https://www.khronos.org/opengl/wiki/OpenGL_Shading_Language
- **Shader Toy**: https://www.shadertoy.com (live shader playground)
- **Book of Shaders**: https://thebookofshaders.com (excellent tutorial)

---

## Quick Reference Card

```glsl
// Vertex Shader Template
#version 330 core
layout(location = 0) in vec3 aPos;
out vec3 vWorldPos;
uniform mat4 uModel, uView, uProjection;

void main() {
    vec4 worldPos = uModel * vec4(aPos, 1.0);
    vWorldPos = worldPos.xyz;
    gl_Position = uProjection * uView * worldPos;
}

// Fragment Shader Template
#version 330 core
in vec3 vWorldPos;
out vec4 FragColor;
uniform sampler2D uTexture;

void main() {
    vec3 color = vec3(1.0); // Your calculations here
    FragColor = vec4(color, 1.0);
}
```
