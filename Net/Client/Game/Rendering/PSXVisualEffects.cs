using System;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Aetheris
{
    /// <summary>
    /// PSX-style visual effects for authentic PlayStation 1 aesthetic
    /// Features: vertex snapping, affine texture mapping, color quantization, dithering
    /// </summary>
    public class PSXVisualEffects
    {
        // PSX effect parameters
        public bool EnableVertexSnapping { get; set; } = true;
        public bool EnableAffineMapping { get; set; } = false;
        public bool EnableColorQuantization { get; set; } = true;
        public bool EnableDithering { get; set; } = true;
        
        private int locCameraPos, locSpecPower, locSpecStrength;
        private int locFogColor, locSkyColor, locGroundColor, locNormalMatrix;
        
        // Vertex snapping resolution (lower = more jitter)
        public float VertexSnapResolution { get; set; } = 1.0f / 64.0f;

        // Color bit depth (5 bits per channel = 32768 colors, authentic PSX)
        public int ColorBits { get; set; } = 6;

        private int psxShaderProgram;
        private int locProjection, locView, locModel;
        private int locVertexSnap, locAffineMapping, locColorBits, locDithering;
        private int locAtlasTexture, locFogDecay;
        private int locScreenResolution;

        #region PSX Shaders
        private const string PSXVertexShader = @"
#version 330 core

layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;

out vec3 vNormal;
out vec3 vWorldPos;
out vec3 vViewPos;
out vec2 vUV;
out float vDepth;

uniform mat4 uProjection;
uniform mat4 uView;
uniform mat4 uModel;
uniform mat3 uNormalMatrix;
uniform float uVertexSnap;

void main()
{
    vec4 worldPos = uModel * vec4(aPos, 1.0);
    vWorldPos = worldPos.xyz;
    vec4 viewPos4 = uView * worldPos;
    vViewPos = viewPos4.xyz;
    vNormal = normalize(uNormalMatrix * aNormal);
    vUV = aUV;
    
    vec4 clipPos = uProjection * viewPos4;
    vDepth = clipPos.w;
    
    // PSX VERTEX SNAPPING - Creates geometric jitter
    if (uVertexSnap > 0.0) {
        // Convert to normalized device coordinates
        vec2 screenPos = clipPos.xy / clipPos.w;
        
        // Snap to grid (this creates the PS1 wobble effect)
        screenPos = floor(screenPos / uVertexSnap) * uVertexSnap;
        
        // Convert back to clip space
        clipPos.xy = screenPos * clipPos.w;
    }
    
    gl_Position = clipPos;
}
";

        private const string PSXFragmentShader = @"
#version 330 core

in vec3 vNormal;
in vec3 vWorldPos;
in vec3 vViewPos;
in vec2 vUV;
in float vDepth;

out vec4 FragColor;

uniform sampler2D uAtlasTexture;
uniform float uFogDecay;
uniform vec3 uCameraPos;
uniform float uSpecPower;
uniform float uSpecStrength;
uniform vec3 uFogColor;
uniform vec3 uSkyColor;
uniform vec3 uGroundColor;

// PSX effect uniforms
uniform int uAffineMapping;
uniform int uColorBits;
uniform int uDithering;
uniform vec2 uScreenResolution;

void main()
{
    vec2 uv = vUV;
    
    // PSX AFFINE TEXTURE MAPPING - Creates texture warping
    // (Disabled by default for playability)
    if (uAffineMapping > 0) {
        // Simulate affine mapping by reducing UV precision
        float affineScale = 32.0; // Lower = more warping
        uv = floor(uv * affineScale) / affineScale;
    }
    
    vec3 N = normalize(vNormal);
    vec3 baseTex = texture(uAtlasTexture, uv).rgb;

    // --- Lighting (two directional lights)
    vec3 light1Dir = normalize(vec3(0.5, 1.0, 0.3));
    vec3 light2Dir = normalize(vec3(-0.3, 0.5, -0.8));

    float diff1 = max(dot(N, light1Dir), 0.0);
    float diff2 = max(dot(N, light2Dir), 0.0) * 0.45;

    // Hemispheric ambient
    float hemi = N.y * 0.5 + 0.5;
    vec3 hemiAmbient = mix(uGroundColor, uSkyColor, hemi) * 0.28;

    // Diffuse
    vec3 diffuse = (diff1 + diff2) * baseTex;

    // Blinn-Phong specular
    vec3 viewDir = normalize(uCameraPos - vWorldPos);
    vec3 half1 = normalize(light1Dir + viewDir);
    vec3 half2 = normalize(light2Dir + viewDir);
    float spec1 = pow(max(dot(N, half1), 0.0), uSpecPower);
    float spec2 = pow(max(dot(N, half2), 0.0), uSpecPower);
    float spec = (spec1 + spec2) * 0.5 * uSpecStrength;

    // Combine lighting
    vec3 lit = hemiAmbient + diffuse + vec3(spec);

    // Tonemapping (Reinhard)
    vec3 tone = lit / (lit + vec3(1.0));
    
    // Slight desaturation
    float gray = dot(tone, vec3(0.299, 0.587, 0.114));
    tone = mix(vec3(gray), tone, 0.88);

    // Gamma correction
    tone = pow(tone, vec3(1.0/2.2));

    // PSX COLOR QUANTIZATION - Creates color banding
    if (uColorBits > 0) {
        float levels = pow(2.0, float(uColorBits));
        tone = floor(tone * levels) / levels;
    }

    // Fog
    float fogDist = length(vViewPos);
    float f = exp(-(fogDist * uFogDecay) * (fogDist * uFogDecay));
    f = clamp(f, 0.0, 1.0);

    vec3 final = mix(uFogColor, tone * baseTex, f);

    // PSX DITHERING - Reduces banding artifacts
    if (uDithering > 0) {
        // Simple ordered dithering pattern
        vec2 screenPos = gl_FragCoord.xy;
        float dither = mod(screenPos.x + screenPos.y * 2.0, 4.0) / 4.0;
        dither = (dither - 0.5) / 255.0; // Very subtle
        final += dither;
    }

    FragColor = vec4(final, 1.0);
}
";
        #endregion

        public PSXVisualEffects()
        {
            CompileShaders();
        }

        private void CompileShaders()
        {
            int vs = CompileShader(ShaderType.VertexShader, PSXVertexShader);
            int fs = CompileShader(ShaderType.FragmentShader, PSXFragmentShader);

            psxShaderProgram = GL.CreateProgram();
            GL.AttachShader(psxShaderProgram, vs);
            GL.AttachShader(psxShaderProgram, fs);
            GL.LinkProgram(psxShaderProgram);

            GL.GetProgram(psxShaderProgram, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0)
            {
                string info = GL.GetProgramInfoLog(psxShaderProgram);
                throw new Exception("PSX shader link error: " + info);
            }

            GL.DetachShader(psxShaderProgram, vs);
            GL.DetachShader(psxShaderProgram, fs);
            GL.DeleteShader(vs);
            GL.DeleteShader(fs);

            // Get uniform locations
            locProjection = GL.GetUniformLocation(psxShaderProgram, "uProjection");
            locView = GL.GetUniformLocation(psxShaderProgram, "uView");
            locModel = GL.GetUniformLocation(psxShaderProgram, "uModel");
            locVertexSnap = GL.GetUniformLocation(psxShaderProgram, "uVertexSnap");
            locAffineMapping = GL.GetUniformLocation(psxShaderProgram, "uAffineMapping");
            locColorBits = GL.GetUniformLocation(psxShaderProgram, "uColorBits");
            locDithering = GL.GetUniformLocation(psxShaderProgram, "uDithering");
            locAtlasTexture = GL.GetUniformLocation(psxShaderProgram, "uAtlasTexture");
            locFogDecay = GL.GetUniformLocation(psxShaderProgram, "uFogDecay");
            locScreenResolution = GL.GetUniformLocation(psxShaderProgram, "uScreenResolution");
            locCameraPos = GL.GetUniformLocation(psxShaderProgram, "uCameraPos");
            locSpecPower = GL.GetUniformLocation(psxShaderProgram, "uSpecPower");
            locSpecStrength = GL.GetUniformLocation(psxShaderProgram, "uSpecStrength");
            locFogColor = GL.GetUniformLocation(psxShaderProgram, "uFogColor");
            locSkyColor = GL.GetUniformLocation(psxShaderProgram, "uSkyColor");
            locGroundColor = GL.GetUniformLocation(psxShaderProgram, "uGroundColor");
            locNormalMatrix = GL.GetUniformLocation(psxShaderProgram, "uNormalMatrix");
            
            Console.WriteLine("[PSX Effects] Shaders compiled successfully");
        }

        public int BeginPSXRender(Matrix4 projection, Matrix4 view, float fogDecay,
                                   int atlasTexture, Vector2 screenResolution, Vector3 cameraPos)
        {
            GL.UseProgram(psxShaderProgram);

            // Set matrices
            GL.UniformMatrix4(locProjection, false, ref projection);
            GL.UniformMatrix4(locView, false, ref view);
            GL.Uniform1(locFogDecay, fogDecay);

            // Set PSX effect parameters
            float snapValue = EnableVertexSnapping ? VertexSnapResolution : 0.0f;
            GL.Uniform1(locVertexSnap, snapValue);
            GL.Uniform1(locAffineMapping, EnableAffineMapping ? 1 : 0);
            GL.Uniform1(locColorBits, EnableColorQuantization ? ColorBits : 0);
            GL.Uniform1(locDithering, EnableDithering ? 1 : 0);
            GL.Uniform2(locScreenResolution, screenResolution);
            
            // Set lighting uniforms
            GL.Uniform3(locCameraPos, cameraPos);
            GL.Uniform1(locSpecPower, 32.0f);
            GL.Uniform1(locSpecStrength, 0.6f);
            GL.Uniform3(locFogColor, 0.5f, 0.6f, 0.7f);
            GL.Uniform3(locSkyColor, 0.6f, 0.7f, 0.95f);
            GL.Uniform3(locGroundColor, 0.28f, 0.22f, 0.16f);
            
            // Bind texture
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, atlasTexture);
            GL.Uniform1(locAtlasTexture, 0);

            return psxShaderProgram;
        }

        public void SetModelMatrix(Matrix4 model)
        {
            GL.UniformMatrix4(locModel, false, ref model);
            
            // Also set normal matrix
            Matrix3 normalMat = new Matrix3(model);
            normalMat = Matrix3.Transpose(normalMat.Inverted());
            GL.UniformMatrix3(locNormalMatrix, false, ref normalMat);
        }

        public void Dispose()
        {
            if (psxShaderProgram != 0)
            {
                GL.DeleteProgram(psxShaderProgram);
                psxShaderProgram = 0;
            }
        }

        private int CompileShader(ShaderType type, string src)
        {
            int s = GL.CreateShader(type);
            GL.ShaderSource(s, src);
            GL.CompileShader(s);
            GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0)
            {
                string info = GL.GetShaderInfoLog(s);
                GL.DeleteShader(s);
                throw new Exception($"PSX shader compile error ({type}): {info}");
            }
            return s;
        }

        /// <summary>
        /// Authentic PS1 look - all effects enabled at full strength
        /// Warning: Affine mapping makes gameplay difficult!
        /// </summary>
        public static PSXVisualEffects CreateAuthenticPSX()
        {
            return new PSXVisualEffects
            {
                EnableVertexSnapping = true,
                EnableAffineMapping = true,
                EnableColorQuantization = true,
                EnableDithering = true,
                VertexSnapResolution = 1.0f / 32.0f,
                ColorBits = 5
            };
        }

        /// <summary>
        /// Modern take on PS1 aesthetic - keeps the look, improves playability
        /// No affine mapping, softer vertex snapping, slightly more colors
        /// </summary>
        public static PSXVisualEffects CreateModernPSX()
        {
            return new PSXVisualEffects
            {
                EnableVertexSnapping = true,
                EnableAffineMapping = false,
                EnableColorQuantization = true,
                EnableDithering = true,
                VertexSnapResolution = 1.0f / 64.0f,
                ColorBits = 6
            };
        }

        /// <summary>
        /// RECOMMENDED: Playable PS1 aesthetic - best balance of style and gameplay
        /// Captures the PS1 feel without the parts that hurt gameplay
        /// </summary>
        public static PSXVisualEffects CreatePlayablePSX()
        {
            return new PSXVisualEffects
            {
                EnableVertexSnapping = true,      // Gentle geometric wobble
                EnableAffineMapping = false,      // Keep textures stable
                EnableColorQuantization = true,   // Color banding effect
                EnableDithering = true,           // Smooth out the banding
                VertexSnapResolution = 1.0f / 64.0f,  // Subtle vertex jitter
                ColorBits = 6                     // 64 levels per channel
            };
        }

        /// <summary>
        /// Minimal PS1 hint - very subtle effects
        /// </summary>
        public static PSXVisualEffects CreateMinimal()
        {
            return new PSXVisualEffects
            {
                EnableVertexSnapping = true,
                EnableAffineMapping = false,
                EnableColorQuantization = false,
                EnableDithering = false,
                VertexSnapResolution = 1.0f / 80.0f
            };
        }
    }
}
