// =============================================================================
// Custom/GreekIslandTerrain
// 
// A 4-layer slope-based terrain shader for URP, designed for a Greek island
// aesthetic. Automatically blends textures based on terrain steepness:
//   Layer 1 - Dry sandy ground   (flat areas)
//   Layer 2 - Mediterranean scrub (gentle slopes)
//   Layer 3 - Rocky ground        (moderate slopes)
//   Layer 4 - Cliff face          (steep slopes, triplanar-mapped)
//
// Features:
//   - Slope-driven blending with procedural noise for organic transitions
//   - Triplanar mapping on steep surfaces to eliminate texture stretching
//   - Macro variation to reduce visible tiling on large terrains
//   - Optional height-based influence
//   - Full URP PBR lighting, shadows, fog, GI, and SSAO support
//   - SRP Batcher compatible
// =============================================================================
Shader "Custom/GreekIslandTerrain"
{
    Properties
    {
        [Header(__________ Layer 1  Dry Ground  Flat Areas __________)]
        _Layer1Albedo ("Albedo", 2D) = "white" {}
        [Normal] _Layer1Normal ("Normal Map", 2D) = "bump" {}
        _Layer1Tiling ("Tiling", Float) = 10.0
        _Layer1Smoothness ("Smoothness", Range(0,1)) = 0.15
        _Layer1Color ("Tint", Color) = (1, 0.96, 0.88, 1)

        [Header(__________ Layer 2  Mediterranean Scrub  Gentle Slopes __________)]
        _Layer2Albedo ("Albedo", 2D) = "white" {}
        [Normal] _Layer2Normal ("Normal Map", 2D) = "bump" {}
        _Layer2Tiling ("Tiling", Float) = 12.0
        _Layer2Smoothness ("Smoothness", Range(0,1)) = 0.2
        _Layer2Color ("Tint", Color) = (0.88, 0.95, 0.78, 1)

        [Header(__________ Layer 3  Rocky Ground  Moderate Slopes __________)]
        _Layer3Albedo ("Albedo", 2D) = "white" {}
        [Normal] _Layer3Normal ("Normal Map", 2D) = "bump" {}
        _Layer3Tiling ("Tiling", Float) = 8.0
        _Layer3Smoothness ("Smoothness", Range(0,1)) = 0.25
        _Layer3Color ("Tint", Color) = (0.92, 0.89, 0.83, 1)

        [Header(__________ Layer 4  Cliff Face  Steep Slopes __________)]
        _Layer4Albedo ("Albedo", 2D) = "white" {}
        [Normal] _Layer4Normal ("Normal Map", 2D) = "bump" {}
        _Layer4Tiling ("Tiling", Float) = 5.0
        _Layer4Smoothness ("Smoothness", Range(0,1)) = 0.3
        _Layer4Color ("Tint", Color) = (0.93, 0.91, 0.87, 1)

        [Header(__________ Slope Based Blending __________)]
        _SlopeStart1 ("Dry Ground Threshold (flattest)", Range(0,1)) = 0.85
        _SlopeStart2 ("Scrub Threshold", Range(0,1)) = 0.65
        _SlopeStart3 ("Rock to Cliff Threshold (steepest)", Range(0,1)) = 0.4
        _SlopeBlendWidth ("Blend Smoothness", Range(0.01, 0.3)) = 0.1

        [Header(__________ Height Influence  Optional __________)]
        _HeightInfluence ("Height Influence Strength", Range(0,1)) = 0.0
        _GrassMaxHeight ("Scrub Max Height", Float) = 50.0
        _RockMinHeight ("Rock Min Height", Float) = 30.0

        [Header(__________ Triplanar Mapping __________)]
        [Toggle] _UseTriplanar ("Triplanar for Steep Surfaces", Float) = 1.0
        _TriplanarSharpness ("Triplanar Blend Sharpness", Range(1, 8)) = 4.0
        _TriplanarSlopeStart ("Triplanar Activation Slope", Range(0, 0.8)) = 0.5

        [Header(__________ Anti Tiling and Detail __________)]
        _NoiseScale ("Variation Noise Scale", Float) = 0.05
        _NoiseStrength ("Variation Noise Strength", Range(0, 0.3)) = 0.1
        _MacroScale ("Macro Variation Scale", Float) = 0.003
        _MacroStrength ("Macro Variation Strength", Range(0, 0.4)) = 0.15

        [Header(__________ Global Settings __________)]
        _Metallic ("Metallic", Range(0,1)) = 0.0
        _NormalStrength ("Normal Map Strength", Range(0, 2)) = 1.0
        _OcclusionStrength ("Ambient Occlusion", Range(0,1)) = 1.0

        // Hidden terrain properties - Unity injects these automatically
        [HideInInspector] _TerrainHolesTexture ("Terrain Holes", 2D) = "white" {}
        [HideInInspector] _Control ("SplatAlpha 0", 2D) = "red" {}
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry-100"
            "TerrainCompatible" = "True"
        }
        LOD 300

        // =================================================================
        // FORWARD LIT PASS
        // =================================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma prefer_hlslcc gles
            #pragma exclude_renderers d3d11_9x

            #pragma vertex Vert
            #pragma fragment Frag

            // --- URP multi-compile keywords ---
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap

            // Terrain holes support — multi_compile ensures both variants are always
            // compiled, which is required because Unity controls this keyword at runtime.
            #pragma multi_compile_fragment __ _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // ---- Texture declarations ----
            // We declare one sampler per type and reuse it (saves sampler slots).
            TEXTURE2D(_Layer1Albedo);   SAMPLER(sampler_Layer1Albedo);
            TEXTURE2D(_Layer1Normal);   SAMPLER(sampler_Layer1Normal);
            TEXTURE2D(_Layer2Albedo);
            TEXTURE2D(_Layer2Normal);
            TEXTURE2D(_Layer3Albedo);
            TEXTURE2D(_Layer3Normal);
            TEXTURE2D(_Layer4Albedo);
            TEXTURE2D(_Layer4Normal);

            // ---- SRP Batcher compatible CBUFFER ----
            CBUFFER_START(UnityPerMaterial)
                float  _Layer1Tiling;
                float  _Layer2Tiling;
                float  _Layer3Tiling;
                float  _Layer4Tiling;
                half   _Layer1Smoothness;
                half   _Layer2Smoothness;
                half   _Layer3Smoothness;
                half   _Layer4Smoothness;
                half4  _Layer1Color;
                half4  _Layer2Color;
                half4  _Layer3Color;
                half4  _Layer4Color;
                float  _SlopeStart1;
                float  _SlopeStart2;
                float  _SlopeStart3;
                float  _SlopeBlendWidth;
                float  _HeightInfluence;
                float  _GrassMaxHeight;
                float  _RockMinHeight;
                float  _UseTriplanar;
                float  _TriplanarSharpness;
                float  _TriplanarSlopeStart;
                float  _NoiseScale;
                float  _NoiseStrength;
                float  _MacroScale;
                float  _MacroStrength;
                half   _Metallic;
                half   _NormalStrength;
                half   _OcclusionStrength;
            CBUFFER_END

            // ---- Terrain GPU instancing + holes (shared across passes) ----
            #include "GreekIslandTerrainInput.hlsl"

            // ---- Structs ----
            // NOTE: Attributes does NOT include TANGENT because Unity Terrain
            // meshes do not provide tangent vertex data. We compute TBN from
            // the world-space normal in the fragment shader instead.
            struct Attributes
            {
                float4 positionOS        : POSITION;
                float3 normalOS          : NORMAL;
                float2 texcoord          : TEXCOORD0;
                float2 staticLightmapUV  : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS    : SV_POSITION;
                float3 positionWS    : TEXCOORD0;
                float3 normalWS      : TEXCOORD1;
                float2 uv            : TEXCOORD2;
                half   fogFactor     : TEXCOORD3;
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord   : TEXCOORD4;
                #endif
                DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 5);
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // =============================================================
            // UTILITY FUNCTIONS
            // =============================================================

            // Fast hash for procedural noise
            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            // Value noise with Hermite interpolation
            float ValueNoise(float2 uv)
            {
                float2 id = floor(uv);
                float2 gv = frac(uv);
                gv = gv * gv * (3.0 - 2.0 * gv);

                float bl = Hash21(id);
                float br = Hash21(id + float2(1, 0));
                float tl = Hash21(id + float2(0, 1));
                float tr = Hash21(id + float2(1, 1));

                return lerp(lerp(bl, br, gv.x), lerp(tl, tr, gv.x), gv.y);
            }

            // Multi-octave FBM for richer large-scale variation
            float FBM(float2 uv, int octaves)
            {
                float value = 0.0;
                float amplitude = 0.5;
                float frequency = 1.0;
                for (int i = 0; i < octaves; i++)
                {
                    value += amplitude * ValueNoise(uv * frequency);
                    frequency *= 2.0;
                    amplitude *= 0.5;
                }
                return value;
            }

            // Triplanar albedo sampling (avoids stretching on steep faces)
            half3 TriplanarAlbedo(TEXTURE2D_PARAM(tex, samp), float3 wPos, float3 wNorm,
                                  float tiling, float sharpness, half3 tint)
            {
                float3 blend = pow(abs(wNorm), sharpness);
                blend /= dot(blend, 1.0);

                half3 xS = SAMPLE_TEXTURE2D(tex, samp, wPos.yz * tiling).rgb;
                half3 yS = SAMPLE_TEXTURE2D(tex, samp, wPos.xz * tiling).rgb;
                half3 zS = SAMPLE_TEXTURE2D(tex, samp, wPos.xy * tiling).rgb;

                return (xS * blend.x + yS * blend.y + zS * blend.z) * tint;
            }

            // Triplanar normal sampling
            half3 TriplanarNormal(TEXTURE2D_PARAM(tex, samp), float3 wPos, float3 wNorm,
                                  float tiling, float sharpness, float strength)
            {
                float3 blend = pow(abs(wNorm), sharpness);
                blend /= dot(blend, 1.0);

                half3 xN = UnpackNormalScale(SAMPLE_TEXTURE2D(tex, samp, wPos.yz * tiling), strength);
                half3 yN = UnpackNormalScale(SAMPLE_TEXTURE2D(tex, samp, wPos.xz * tiling), strength);
                half3 zN = UnpackNormalScale(SAMPLE_TEXTURE2D(tex, samp, wPos.xy * tiling), strength);

                return normalize(xN * blend.x + yN * blend.y + zN * blend.z);
            }

            // =============================================================
            // VERTEX SHADER
            // =============================================================
            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // Reconstruct vertex from terrain heightmap when GPU instancing is active
                TerrainInstancing(input.positionOS, input.normalOS, input.texcoord);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS   = normalWS;
                output.uv         = input.texcoord;
                output.fogFactor  = ComputeFogFactor(posInputs.positionCS.z);

                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    output.shadowCoord = GetShadowCoord(posInputs);
                #endif

                OUTPUT_LIGHTMAP_UV(input.staticLightmapUV, unity_LightmapST, output.staticLightmapUV);
                OUTPUT_SH(normalWS, output.vertexSH);

                return output;
            }

            // =============================================================
            // FRAGMENT SHADER
            // =============================================================
            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 worldPos    = input.positionWS;
                float3 worldNormal = normalize(input.normalWS);

                // ---- Terrain holes: discard pixel if hole texture says so ----
                ClipTerrainHoles(input.uv);

                // ---- Slope: 1.0 = perfectly flat, 0.0 = vertical cliff ----
                float slope = saturate(dot(worldNormal, float3(0, 1, 0)));

                // ---- Procedural noise for organic slope transitions ----
                float variation = (ValueNoise(worldPos.xz * _NoiseScale) - 0.5) * 2.0 * _NoiseStrength;
                float noisySlope = saturate(slope + variation);

                // ---- Macro variation to break large-scale tiling repetition ----
                float macro = (FBM(worldPos.xz * _MacroScale, 3) - 0.5) * 2.0 * _MacroStrength;

                // ---- Compute layer weights from slope ----
                float w = _SlopeBlendWidth;

                //  weight1 = dry ground (flattest)  ->  weight4 = cliff (steepest)
                float weight1 = smoothstep(_SlopeStart1 - w, _SlopeStart1 + w, noisySlope);
                float weight2 = smoothstep(_SlopeStart2 - w, _SlopeStart2 + w, noisySlope)
                              - smoothstep(_SlopeStart1 - w, _SlopeStart1 + w, noisySlope);
                float weight3 = smoothstep(_SlopeStart3 - w, _SlopeStart3 + w, noisySlope)
                              - smoothstep(_SlopeStart2 - w, _SlopeStart2 + w, noisySlope);
                float weight4 = 1.0 - smoothstep(_SlopeStart3 - w, _SlopeStart3 + w, noisySlope);

                weight1 = max(weight1, 0.0);
                weight2 = max(weight2, 0.0);
                weight3 = max(weight3, 0.0);
                weight4 = max(weight4, 0.0);

                // ---- Optional height-based blending ----
                float height = worldPos.y;
                float heightGrassFade = saturate(1.0 - (height - _GrassMaxHeight) * 0.05);
                float heightRockBoost = saturate((height - _RockMinHeight) * 0.05);
                weight2 *= lerp(1.0, heightGrassFade, _HeightInfluence);
                weight3  = saturate(weight3 + heightRockBoost * _HeightInfluence * 0.3);

                // ---- Normalize weights ----
                float totalW = weight1 + weight2 + weight3 + weight4;
                totalW = max(totalW, 0.001);
                weight1 /= totalW;
                weight2 /= totalW;
                weight3 /= totalW;
                weight4 /= totalW;

                // ---- World-space UVs (consistent across terrain chunks) ----
                // 0.01 factor converts the tiling slider to a comfortable world-space scale
                float2 uv1 = worldPos.xz * _Layer1Tiling * 0.01;
                float2 uv2 = worldPos.xz * _Layer2Tiling * 0.01;
                float2 uv3 = worldPos.xz * _Layer3Tiling * 0.01;
                float2 uv4 = worldPos.xz * _Layer4Tiling * 0.01;

                // ---- Sample flat layers (albedo + normal) ----
                half3 albedo1 = SAMPLE_TEXTURE2D(_Layer1Albedo, sampler_Layer1Albedo, uv1).rgb * _Layer1Color.rgb;
                half3 albedo2 = SAMPLE_TEXTURE2D(_Layer2Albedo, sampler_Layer1Albedo, uv2).rgb * _Layer2Color.rgb;
                half3 albedo3 = SAMPLE_TEXTURE2D(_Layer3Albedo, sampler_Layer1Albedo, uv3).rgb * _Layer3Color.rgb;

                half3 norm1 = UnpackNormalScale(SAMPLE_TEXTURE2D(_Layer1Normal, sampler_Layer1Normal, uv1), _NormalStrength);
                half3 norm2 = UnpackNormalScale(SAMPLE_TEXTURE2D(_Layer2Normal, sampler_Layer1Normal, uv2), _NormalStrength);
                half3 norm3 = UnpackNormalScale(SAMPLE_TEXTURE2D(_Layer3Normal, sampler_Layer1Normal, uv3), _NormalStrength);

                // ---- Cliff layer: triplanar when steep, regular when flat ----
                half3 albedo4;
                half3 norm4;

                float triplanarBlend = (_UseTriplanar > 0.5)
                    ? saturate((1.0 - slope - _TriplanarSlopeStart) / max(1.0 - _TriplanarSlopeStart, 0.01))
                    : 0.0;

                if (triplanarBlend > 0.01)
                {
                    half3 triAlb = TriplanarAlbedo(
                        TEXTURE2D_ARGS(_Layer4Albedo, sampler_Layer1Albedo),
                        worldPos, worldNormal, _Layer4Tiling * 0.01, _TriplanarSharpness, _Layer4Color.rgb);
                    half3 triNrm = TriplanarNormal(
                        TEXTURE2D_ARGS(_Layer4Normal, sampler_Layer1Normal),
                        worldPos, worldNormal, _Layer4Tiling * 0.01, _TriplanarSharpness, _NormalStrength);

                    half3 flatAlb4 = SAMPLE_TEXTURE2D(_Layer4Albedo, sampler_Layer1Albedo, uv4).rgb * _Layer4Color.rgb;
                    half3 flatNrm4 = UnpackNormalScale(SAMPLE_TEXTURE2D(_Layer4Normal, sampler_Layer1Normal, uv4), _NormalStrength);

                    albedo4 = lerp(flatAlb4, triAlb, triplanarBlend);
                    norm4   = normalize(lerp(flatNrm4, triNrm, triplanarBlend));
                }
                else
                {
                    albedo4 = SAMPLE_TEXTURE2D(_Layer4Albedo, sampler_Layer1Albedo, uv4).rgb * _Layer4Color.rgb;
                    norm4   = UnpackNormalScale(SAMPLE_TEXTURE2D(_Layer4Normal, sampler_Layer1Normal, uv4), _NormalStrength);
                }

                // ---- Blend all four layers ----
                half3 finalAlbedo = albedo1 * weight1
                                  + albedo2 * weight2
                                  + albedo3 * weight3
                                  + albedo4 * weight4;

                half3 finalNormalTS = normalize(norm1 * weight1
                                              + norm2 * weight2
                                              + norm3 * weight3
                                              + norm4 * weight4);

                half finalSmoothness = _Layer1Smoothness * weight1
                                     + _Layer2Smoothness * weight2
                                     + _Layer3Smoothness * weight3
                                     + _Layer4Smoothness * weight4;

                // ---- Apply macro variation for less repetitive look ----
                finalAlbedo *= (1.0 + macro);

                // ---- Compute tangent frame from world normal ----
                // Terrain meshes don't provide vertex tangents, so we derive
                // an orthonormal TBN basis from the interpolated world normal.
                float3 up = abs(worldNormal.y) < 0.999 ? float3(0, 1, 0) : float3(0, 0, 1);
                float3 tangentWS  = normalize(cross(up, worldNormal));
                float3 bitangentWS = cross(worldNormal, tangentWS);
                float3x3 TBN = float3x3(tangentWS, bitangentWS, worldNormal);
                float3 finalNormalWS = normalize(TransformTangentToWorld(finalNormalTS, TBN));

                // ---- Build InputData for URP PBR ----
                InputData inputData = (InputData)0;
                inputData.positionWS  = worldPos;
                inputData.normalWS    = finalNormalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(worldPos);

                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    inputData.shadowCoord = input.shadowCoord;
                #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                    inputData.shadowCoord = TransformWorldToShadowCoord(worldPos);
                #else
                    inputData.shadowCoord = float4(0, 0, 0, 0);
                #endif

                inputData.fogCoord    = input.fogFactor;
                inputData.bakedGI     = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, finalNormalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask  = SAMPLE_SHADOWMASK(input.staticLightmapUV);

                // ---- Build SurfaceData ----
                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo     = finalAlbedo;
                surfaceData.metallic   = _Metallic;
                surfaceData.smoothness = finalSmoothness;
                surfaceData.normalTS   = finalNormalTS;
                surfaceData.occlusion  = _OcclusionStrength;
                surfaceData.alpha      = 1.0;

                // ---- Final PBR lighting ----
                half4 color = UniversalFragmentPBR(inputData, surfaceData);

                // ---- Fog ----
                color.rgb = MixFog(color.rgb, inputData.fogCoord);

                return half4(color.rgb, 1.0);
            }
            ENDHLSL
        }

        // =================================================================
        // SHADOW CASTER PASS
        // =================================================================
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma prefer_hlslcc gles
            #pragma exclude_renderers d3d11_9x

            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag

            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap
            #pragma multi_compile _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_fragment __ _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "GreekIslandTerrainInput.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 texcoord   : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings ShadowVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                TerrainInstancing(input.positionOS, input.normalOS, input.texcoord);

                float3 posWS    = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDir = normalize(_LightPosition - posWS);
                #else
                    float3 lightDir = _LightDirection;
                #endif

                output.positionCS = TransformWorldToHClip(ApplyShadowBias(posWS, normalWS, lightDir));

                #if UNITY_REVERSED_Z
                    output.positionCS.z = min(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    output.positionCS.z = max(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                output.uv = input.texcoord;
                return output;
            }

            half4 ShadowFrag(Varyings input) : SV_Target
            {
                ClipTerrainHoles(input.uv);
                return 0;
            }
            ENDHLSL
        }

        // =================================================================
        // DEPTH ONLY PASS
        // =================================================================
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma prefer_hlslcc gles
            #pragma exclude_renderers d3d11_9x

            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap
            #pragma multi_compile_fragment __ _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "GreekIslandTerrainInput.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 texcoord   : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                TerrainInstancing(input.positionOS, input.normalOS, input.texcoord);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.texcoord;
                return output;
            }

            half4 DepthFrag(Varyings input) : SV_Target
            {
                ClipTerrainHoles(input.uv);
                return 0;
            }
            ENDHLSL
        }

        // =================================================================
        // DEPTH NORMALS PASS  (required for SSAO and screen-space effects)
        // =================================================================
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma prefer_hlslcc gles
            #pragma exclude_renderers d3d11_9x

            #pragma vertex DepthNormVert
            #pragma fragment DepthNormFrag

            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap
            #pragma multi_compile_fragment __ _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "GreekIslandTerrainInput.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 texcoord   : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float2 uv         : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthNormVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                TerrainInstancing(input.positionOS, input.normalOS, input.texcoord);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS   = TransformObjectToWorldNormal(input.normalOS);
                output.uv         = input.texcoord;
                return output;
            }

            half4 DepthNormFrag(Varyings input) : SV_Target
            {
                ClipTerrainHoles(input.uv);
                return half4(normalize(input.normalWS), 0.0);
            }
            ENDHLSL
        }
    }

    Fallback "Universal Render Pipeline/Lit"
}
