// =============================================================================
// Custom/GreekIslandTerrainV2
//
// 4-layer slope-based terrain shader for URP + contact-dirt 5th layer.
// Built from URP Lit.shader's proven structure — no custom terrain instancing.
//
// Layers blend based on terrain steepness:
//   Layer 1 — Dry sandy ground   (flattest areas)
//   Layer 2 — Mediterranean scrub (gentle slopes)
//   Layer 3 — Rocky ground        (moderate slopes)
//   Layer 4 — Cliff face          (steepest, triplanar-mapped)
//   Layer 5 — Contact dirt         (blended where objects touch terrain)
//
// Uses world-space UVs so textures tile seamlessly across huge terrains.
// Tiling value = world units per texture repeat (e.g. 30 = one repeat every 30m)
// =============================================================================
Shader "Custom/GreekIslandTerrainV2"
{
    Properties
    {
        [Header(__________ Layer 1  Dry Ground  Flat Areas __________)]
        _Layer1Albedo ("Albedo", 2D) = "white" {}
        [Normal] _Layer1Normal ("Normal Map", 2D) = "bump" {}
        _Layer1Tiling ("Tiling (world units per repeat)", Float) = 30.0
        _Layer1Smoothness ("Smoothness", Range(0,1)) = 0.15
        _Layer1Color ("Tint", Color) = (1, 0.96, 0.88, 1)

        [Header(__________ Layer 2  Mediterranean Scrub  Gentle Slopes __________)]
        _Layer2Albedo ("Albedo", 2D) = "white" {}
        [Normal] _Layer2Normal ("Normal Map", 2D) = "bump" {}
        _Layer2Tiling ("Tiling (world units per repeat)", Float) = 25.0
        _Layer2Smoothness ("Smoothness", Range(0,1)) = 0.2
        _Layer2Color ("Tint", Color) = (0.88, 0.95, 0.78, 1)

        [Header(__________ Layer 3  Rocky Ground  Moderate Slopes __________)]
        _Layer3Albedo ("Albedo", 2D) = "white" {}
        [Normal] _Layer3Normal ("Normal Map", 2D) = "bump" {}
        _Layer3Tiling ("Tiling (world units per repeat)", Float) = 20.0
        _Layer3Smoothness ("Smoothness", Range(0,1)) = 0.25
        _Layer3Color ("Tint", Color) = (0.92, 0.89, 0.83, 1)

        [Header(__________ Layer 4  Cliff Face  Steep Slopes __________)]
        _Layer4Albedo ("Albedo", 2D) = "white" {}
        [Normal] _Layer4Normal ("Normal Map", 2D) = "bump" {}
        _Layer4Tiling ("Tiling (world units per repeat)", Float) = 15.0
        _Layer4Smoothness ("Smoothness", Range(0,1)) = 0.3
        _Layer4Color ("Tint", Color) = (0.93, 0.91, 0.87, 1)

        [Header(__________ Slope Based Blending __________)]
        _SlopeThreshold1 ("Flat to Scrub (dot product)", Range(0,1)) = 0.85
        _SlopeThreshold2 ("Scrub to Rock", Range(0,1)) = 0.65
        _SlopeThreshold3 ("Rock to Cliff", Range(0,1)) = 0.4
        _SlopeBlend ("Blend Smoothness", Range(0.01, 0.3)) = 0.1

        [Header(__________ Triplanar  Cliffs __________)]
        [Toggle] _UseTriplanar ("Triplanar for Steep Surfaces", Float) = 1.0
        _TriplanarSharpness ("Blend Sharpness", Range(1, 8)) = 4.0

        [Header(__________ Anti Tiling __________)]
        _NoiseScale ("Noise Scale", Float) = 0.05
        _NoiseStrength ("Noise Strength", Range(0, 0.3)) = 0.08
        _MacroScale ("Macro Variation Scale", Float) = 0.003
        _MacroStrength ("Macro Variation Strength", Range(0, 0.4)) = 0.12

        [Header(__________ Global Scale __________)]
        _GlobalScale ("Base Texture Scale (larger = bigger textures)", Range(0.01, 50)) = 1.0

        [Header(__________ Distance LOD Scaling __________)]
        [Toggle] _UseDistanceLOD ("Enable Distance-Based Scale", Float) = 1.0
        _NearDist ("Near Distance (m)", Float) = 50.0
        _MidDist ("Mid Distance (m)", Float) = 200.0
        _FarDist ("Far Distance (m)", Float) = 600.0
        _NearScale ("Near Scale Multiplier", Range(0.1, 5.0)) = 1.0
        _MidScale ("Mid Scale Multiplier", Range(0.1, 10.0)) = 2.0
        _FarScale ("Far Scale Multiplier", Range(0.1, 20.0)) = 5.0

        [Header(__________ Layer 5  Contact Dirt  Near Objects __________)]
        [Toggle] _UseContactDirt ("Enable Contact Dirt", Float) = 1.0
        _Layer5Albedo ("Albedo", 2D) = "white" {}
        [Normal] _Layer5Normal ("Normal Map", 2D) = "bump" {}
        _Layer5Tiling ("Tiling (world units per repeat)", Float) = 10.0
        _Layer5Smoothness ("Smoothness", Range(0,1)) = 0.1
        _Layer5Color ("Tint", Color) = (0.55, 0.45, 0.35, 1)
        _ContactStrength ("Blend Strength", Range(0, 1)) = 0.8
        _ContactRadius ("Blend Radius (mask softness)", Range(0, 1)) = 0.3
        _ContactMask ("Contact Mask (auto-generated)", 2D) = "black" {}
        _ContactOrigin ("Mask World Origin XZ", Vector) = (0, 0, 0, 0)
        _ContactSize ("Mask World Size XZ", Vector) = (2000, 3300, 0, 0)

        [Header(__________ Open Area Variation __________)]
        _OpenAreaMacroBoost ("Extra Variation in Open Areas", Range(0, 0.5)) = 0.15

        [Header(__________ Global PBR __________)]
        _Metallic ("Metallic", Range(0,1)) = 0.0
        _NormalStrength ("Normal Map Strength", Range(0, 2)) = 1.0
        _OcclusionStrength ("Ambient Occlusion", Range(0,1)) = 1.0

        // Hidden — needed by URP / SRP Batcher
        [HideInInspector] _Surface ("__surface", Float) = 0.0
        [HideInInspector] _Cutoff ("__cutoff", Float) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry-100"
            "IgnoreProjector" = "True"
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
            #pragma target 2.0

            #pragma vertex Vert
            #pragma fragment Frag

            // ---- URP pipeline keywords (identical to Lit.shader) ----
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            // ---- Unity keywords ----
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fragment _ LIGHTMAP_BICUBIC_SAMPLING
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ USE_LEGACY_LIGHTMAPS
            #pragma multi_compile_fragment _ DEBUG_DISPLAY
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"

            // ---- GPU Instancing (same as Lit.shader — NO terrain-specific options) ----
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            // ---- URP includes ----
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // ---- Texture declarations ----
            TEXTURE2D(_Layer1Albedo);   SAMPLER(sampler_Layer1Albedo);
            TEXTURE2D(_Layer1Normal);   SAMPLER(sampler_Layer1Normal);
            TEXTURE2D(_Layer2Albedo);
            TEXTURE2D(_Layer2Normal);
            TEXTURE2D(_Layer3Albedo);
            TEXTURE2D(_Layer3Normal);
            TEXTURE2D(_Layer4Albedo);
            TEXTURE2D(_Layer4Normal);
            TEXTURE2D(_Layer5Albedo);
            TEXTURE2D(_Layer5Normal);
            TEXTURE2D(_ContactMask);    SAMPLER(sampler_ContactMask);

            // ---- SRP Batcher compatible CBUFFER ----
            CBUFFER_START(UnityPerMaterial)
                float  _Layer1Tiling;
                float  _Layer2Tiling;
                float  _Layer3Tiling;
                float  _Layer4Tiling;
                float  _Layer5Tiling;
                half   _Layer1Smoothness;
                half   _Layer2Smoothness;
                half   _Layer3Smoothness;
                half   _Layer4Smoothness;
                half   _Layer5Smoothness;
                half4  _Layer1Color;
                half4  _Layer2Color;
                half4  _Layer3Color;
                half4  _Layer4Color;
                half4  _Layer5Color;
                float  _SlopeThreshold1;
                float  _SlopeThreshold2;
                float  _SlopeThreshold3;
                float  _SlopeBlend;
                float  _UseTriplanar;
                float  _TriplanarSharpness;
                float  _NoiseScale;
                float  _NoiseStrength;
                float  _MacroScale;
                float  _MacroStrength;
                float  _GlobalScale;
                float  _UseDistanceLOD;
                float  _NearDist;
                float  _MidDist;
                float  _FarDist;
                float  _NearScale;
                float  _MidScale;
                float  _FarScale;
                float  _UseContactDirt;
                float  _ContactStrength;
                float  _ContactRadius;
                float4 _ContactOrigin;
                float4 _ContactSize;
                float  _OpenAreaMacroBoost;
                float4 _Layer1Albedo_ST;
                float4 _Layer2Albedo_ST;
                float4 _Layer3Albedo_ST;
                float4 _Layer4Albedo_ST;
                float4 _Layer5Albedo_ST;
                half   _Metallic;
                half   _NormalStrength;
                half   _OcclusionStrength;
                half   _Surface;
                half   _Cutoff;
            CBUFFER_END

            // ---- Structs ----
            struct Attributes
            {
                float4 positionOS       : POSITION;
                float3 normalOS         : NORMAL;
                float4 tangentOS        : TANGENT;
                float2 texcoord         : TEXCOORD0;
                float2 staticLightmapUV : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS       : SV_POSITION;
                float3 positionWS       : TEXCOORD0;
                float3 normalWS         : TEXCOORD1;
                half4  tangentWS        : TEXCOORD2;
                half   fogFactor        : TEXCOORD3;
            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord      : TEXCOORD4;
            #endif
                DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 5);
            #ifdef USE_APV_PROBE_OCCLUSION
                float4 probeOcclusion   : TEXCOORD6;
            #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // =============================================================
            // UTILITY: Procedural noise & triplanar helpers
            // =============================================================
            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

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

            float FBM(float2 uv, int octaves)
            {
                float value = 0.0;
                float amp = 0.5;
                float freq = 1.0;
                for (int i = 0; i < octaves; i++)
                {
                    value += amp * ValueNoise(uv * freq);
                    freq *= 2.0;
                    amp *= 0.5;
                }
                return value;
            }

            half3 SampleTriplanarAlbedo(TEXTURE2D_PARAM(tex, samp), float3 wPos, float3 wNorm,
                                        float tiling, float sharpness, half3 tint)
            {
                float3 blend = pow(abs(wNorm), sharpness);
                blend /= dot(blend, 1.0);
                half3 xS = SAMPLE_TEXTURE2D(tex, samp, wPos.yz * tiling).rgb;
                half3 yS = SAMPLE_TEXTURE2D(tex, samp, wPos.xz * tiling).rgb;
                half3 zS = SAMPLE_TEXTURE2D(tex, samp, wPos.xy * tiling).rgb;
                return (xS * blend.x + yS * blend.y + zS * blend.z) * tint;
            }

            half3 SampleTriplanarNormal(TEXTURE2D_PARAM(tex, samp), float3 wPos, float3 wNorm,
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
            // Helper: sample all 4 layers at a given uniform scale and
            // return blended albedo, tangent-space normal, and smoothness.
            // Scale is a single discrete value — no per-pixel UV warping.
            // =============================================================
            struct LayerResult
            {
                half3 albedo;
                half3 normalTS;
                half  smoothness;
            };

            LayerResult SampleAllLayers(
                float3 worldPos, float3 worldNormal, float slope,
                float scale,
                float weight1, float weight2, float weight3, float weight4)
            {
                LayerResult result;

                // World-space base UV, then apply per-texture ST (tiling/offset from inspector)
                float2 uv1 = worldPos.xz / max(_Layer1Tiling * scale, 0.01);
                uv1 = uv1 * _Layer1Albedo_ST.xy + _Layer1Albedo_ST.zw;
                float2 uv2 = worldPos.xz / max(_Layer2Tiling * scale, 0.01);
                uv2 = uv2 * _Layer2Albedo_ST.xy + _Layer2Albedo_ST.zw;
                float2 uv3 = worldPos.xz / max(_Layer3Tiling * scale, 0.01);
                uv3 = uv3 * _Layer3Albedo_ST.xy + _Layer3Albedo_ST.zw;
                float2 uv4 = worldPos.xz / max(_Layer4Tiling * scale, 0.01);
                uv4 = uv4 * _Layer4Albedo_ST.xy + _Layer4Albedo_ST.zw;

                half3 albedo1 = SAMPLE_TEXTURE2D(_Layer1Albedo, sampler_Layer1Albedo, uv1).rgb * _Layer1Color.rgb;
                half3 albedo2 = SAMPLE_TEXTURE2D(_Layer2Albedo, sampler_Layer1Albedo, uv2).rgb * _Layer2Color.rgb;
                half3 albedo3 = SAMPLE_TEXTURE2D(_Layer3Albedo, sampler_Layer1Albedo, uv3).rgb * _Layer3Color.rgb;

                half3 norm1 = UnpackNormalScale(SAMPLE_TEXTURE2D(_Layer1Normal, sampler_Layer1Normal, uv1), _NormalStrength);
                half3 norm2 = UnpackNormalScale(SAMPLE_TEXTURE2D(_Layer2Normal, sampler_Layer1Normal, uv2), _NormalStrength);
                half3 norm3 = UnpackNormalScale(SAMPLE_TEXTURE2D(_Layer3Normal, sampler_Layer1Normal, uv3), _NormalStrength);

                half3 albedo4;
                half3 norm4;
                // For triplanar, apply ST scale (xy) to the tiling factor; offset not meaningful in 3D projection
                // For triplanar, fold per-texture ST scale into the tiling factor
                float tiling4 = (1.0 / max(_Layer4Tiling * scale, 0.01)) * ((_Layer4Albedo_ST.x + _Layer4Albedo_ST.y) * 0.5);

                if (_UseTriplanar > 0.5 && slope < 0.7)
                {
                    albedo4 = SampleTriplanarAlbedo(
                        TEXTURE2D_ARGS(_Layer4Albedo, sampler_Layer1Albedo),
                        worldPos, worldNormal, tiling4, _TriplanarSharpness, _Layer4Color.rgb);
                    norm4 = SampleTriplanarNormal(
                        TEXTURE2D_ARGS(_Layer4Normal, sampler_Layer1Normal),
                        worldPos, worldNormal, tiling4, _TriplanarSharpness, _NormalStrength);
                }
                else
                {
                    albedo4 = SAMPLE_TEXTURE2D(_Layer4Albedo, sampler_Layer1Albedo, uv4).rgb * _Layer4Color.rgb;
                    norm4   = UnpackNormalScale(SAMPLE_TEXTURE2D(_Layer4Normal, sampler_Layer1Normal, uv4), _NormalStrength);
                }

                result.albedo = albedo1 * weight1 + albedo2 * weight2
                              + albedo3 * weight3 + albedo4 * weight4;
                result.normalTS = normalize(
                    norm1 * weight1 + norm2 * weight2
                  + norm3 * weight3 + norm4 * weight4);
                result.smoothness = _Layer1Smoothness * weight1 + _Layer2Smoothness * weight2
                                  + _Layer3Smoothness * weight3 + _Layer4Smoothness * weight4;

                return result;
            }

            // =============================================================
            // VERTEX
            // =============================================================
            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs   normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                output.normalWS   = normalInput.normalWS;

                real sign = input.tangentOS.w * GetOddNegativeScale();
                output.tangentWS = half4(normalInput.tangentWS.xyz, sign);

                #if !defined(_FOG_FRAGMENT)
                    output.fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
                #endif

                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    output.shadowCoord = GetShadowCoord(vertexInput);
                #endif

                OUTPUT_LIGHTMAP_UV(input.staticLightmapUV, unity_LightmapST, output.staticLightmapUV);
                OUTPUT_SH4(vertexInput.positionWS, normalInput.normalWS, GetWorldSpaceNormalizeViewDir(vertexInput.positionWS), output.vertexSH, output.probeOcclusion);

                return output;
            }

            // =============================================================
            // FRAGMENT
            // =============================================================
            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 worldPos    = input.positionWS;
                float3 worldNormal = normalize(input.normalWS);

                // ---- Slope: 1 = flat, 0 = vertical ----
                float slope = saturate(dot(worldNormal, float3(0, 1, 0)));

                // ---- Noise for organic transitions ----
                float noise = (ValueNoise(worldPos.xz * _NoiseScale) - 0.5) * 2.0 * _NoiseStrength;
                float noisySlope = saturate(slope + noise);

                // ---- Macro variation to break large-scale tiling repetition ----
                float macro = (FBM(worldPos.xz * _MacroScale, 3) - 0.5) * 2.0 * _MacroStrength;

                // ---- Layer weights from slope ----
                //  weight1 = dry ground (flattest) → weight4 = cliff (steepest)
                float w = _SlopeBlend;
                float weight1 = smoothstep(_SlopeThreshold1 - w, _SlopeThreshold1 + w, noisySlope);
                float weight2 = smoothstep(_SlopeThreshold2 - w, _SlopeThreshold2 + w, noisySlope)
                              - smoothstep(_SlopeThreshold1 - w, _SlopeThreshold1 + w, noisySlope);
                float weight3 = smoothstep(_SlopeThreshold3 - w, _SlopeThreshold3 + w, noisySlope)
                              - smoothstep(_SlopeThreshold2 - w, _SlopeThreshold2 + w, noisySlope);
                float weight4 = 1.0 - smoothstep(_SlopeThreshold3 - w, _SlopeThreshold3 + w, noisySlope);

                weight1 = max(weight1, 0.0);
                weight2 = max(weight2, 0.0);
                weight3 = max(weight3, 0.0);
                weight4 = max(weight4, 0.0);

                float totalW = weight1 + weight2 + weight3 + weight4;
                totalW = max(totalW, 0.001);
                weight1 /= totalW;
                weight2 /= totalW;
                weight3 /= totalW;
                weight4 /= totalW;

                // ---- Distance-based LOD: sample at two discrete scales, crossfade ----
                // This avoids UV warping artifacts by keeping each sample's UVs uniform.
                float baseScale = max(_GlobalScale, 0.01);
                half3 finalAlbedo;
                half3 finalNormalTS;
                half  finalSmoothness;

                if (_UseDistanceLOD > 0.5)
                {
                    float camDist = distance(worldPos, _WorldSpaceCameraPos.xyz);

                    // Determine which two bands and the blend factor between them
                    float scaleA, scaleB, blendT;
                    if (camDist < _NearDist)
                    {
                        // Fully in Near band
                        scaleA = _NearScale;
                        scaleB = _NearScale;
                        blendT = 0.0;
                    }
                    else if (camDist < _MidDist)
                    {
                        // Near → Mid transition
                        scaleA = _NearScale;
                        scaleB = _MidScale;
                        blendT = smoothstep(_NearDist, _MidDist, camDist);
                    }
                    else if (camDist < _FarDist)
                    {
                        // Mid → Far transition
                        scaleA = _MidScale;
                        scaleB = _FarScale;
                        blendT = smoothstep(_MidDist, _FarDist, camDist);
                    }
                    else
                    {
                        // Fully in Far band
                        scaleA = _FarScale;
                        scaleB = _FarScale;
                        blendT = 0.0;
                    }

                    float sA = baseScale * scaleA;
                    float sB = baseScale * scaleB;

                    // Sample all layers at both discrete scales
                    LayerResult rA = SampleAllLayers(worldPos, worldNormal, slope, sA,
                                                     weight1, weight2, weight3, weight4);
                    LayerResult rB = SampleAllLayers(worldPos, worldNormal, slope, sB,
                                                     weight1, weight2, weight3, weight4);

                    // Crossfade the final results — smooth color blend, no UV distortion
                    finalAlbedo     = lerp(rA.albedo,     rB.albedo,     blendT);
                    finalNormalTS   = normalize(lerp(rA.normalTS, rB.normalTS, blendT));
                    finalSmoothness = lerp(rA.smoothness, rB.smoothness, blendT);
                }
                else
                {
                    // No distance LOD — single sample at base scale
                    LayerResult r = SampleAllLayers(worldPos, worldNormal, slope, baseScale,
                                                    weight1, weight2, weight3, weight4);
                    finalAlbedo     = r.albedo;
                    finalNormalTS   = r.normalTS;
                    finalSmoothness = r.smoothness;
                }

                // ---- Contact Dirt (Layer 5) via world-space mask ----
                float contactMaskVal = 0.0;
                if (_UseContactDirt > 0.5)
                {
                    // Map world XZ to mask UV: (worldPos - origin) / size
                    float2 maskUV = (worldPos.xz - _ContactOrigin.xy) / max(_ContactSize.xy, 0.01);
                    contactMaskVal = SAMPLE_TEXTURE2D(_ContactMask, sampler_ContactMask, maskUV).r;

                    // Apply softness / radius: expand the mask edge
                    contactMaskVal = smoothstep(_ContactRadius, 1.0, contactMaskVal);
                    contactMaskVal *= _ContactStrength;

                    // Sample Layer 5 at its own tiling
                    float2 uv5 = worldPos.xz / max(_Layer5Tiling * max(_GlobalScale, 0.01), 0.01);
                    uv5 = uv5 * _Layer5Albedo_ST.xy + _Layer5Albedo_ST.zw;
                    half3 albedo5 = SAMPLE_TEXTURE2D(_Layer5Albedo, sampler_Layer1Albedo, uv5).rgb * _Layer5Color.rgb;
                    half3 norm5   = UnpackNormalScale(SAMPLE_TEXTURE2D(_Layer5Normal, sampler_Layer1Normal, uv5), _NormalStrength);

                    // Blend contact dirt over the slope-based result
                    finalAlbedo     = lerp(finalAlbedo, albedo5, contactMaskVal);
                    finalNormalTS   = normalize(lerp(finalNormalTS, norm5, contactMaskVal));
                    finalSmoothness = lerp(finalSmoothness, _Layer5Smoothness, contactMaskVal);
                }

                // ---- Apply macro variation for less repetitive look ----
                // In open areas (low contact mask), boost the macro variation
                float macroAmount = macro * (1.0 + _OpenAreaMacroBoost * (1.0 - contactMaskVal));
                finalAlbedo *= (1.0 + macroAmount);

                // ---- Build tangent-to-world from vertex data ----
                float  sgn = input.tangentWS.w;
                float3 bitangent = sgn * cross(input.normalWS.xyz, input.tangentWS.xyz);
                half3x3 tangentToWorld = half3x3(input.tangentWS.xyz, bitangent.xyz, input.normalWS.xyz);

                // ---- InputData (matches Lit.shader structure exactly) ----
                InputData inputData = (InputData)0;
                inputData.positionWS = worldPos;
                inputData.tangentToWorld = tangentToWorld;
                inputData.normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(finalNormalTS, tangentToWorld));
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(worldPos);

                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    inputData.shadowCoord = input.shadowCoord;
                #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                    inputData.shadowCoord = TransformWorldToShadowCoord(worldPos);
                #else
                    inputData.shadowCoord = float4(0, 0, 0, 0);
                #endif

                inputData.fogCoord = InitializeInputDataFog(float4(worldPos, 1.0), input.fogFactor);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

                // ---- Baked GI (same branches as LitForwardPass.hlsl) ----
                #if !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
                    inputData.bakedGI = SAMPLE_GI(input.vertexSH,
                        GetAbsolutePositionWS(inputData.positionWS),
                        inputData.normalWS,
                        inputData.viewDirectionWS,
                        input.positionCS.xy,
                        input.probeOcclusion,
                        inputData.shadowMask);
                #else
                    inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, inputData.normalWS);
                    inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
                #endif

                // ---- SurfaceData ----
                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo     = finalAlbedo;
                surfaceData.alpha      = 1.0;
                surfaceData.metallic   = _Metallic;
                surfaceData.smoothness = finalSmoothness;
                surfaceData.normalTS   = finalNormalTS;
                surfaceData.occlusion  = _OcclusionStrength;
                surfaceData.specular   = half3(0, 0, 0);

                // ---- PBR Lighting ----
                half4 color = UniversalFragmentPBR(inputData, surfaceData);
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
            #pragma target 2.0
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag

            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // SRP Batcher: must match UnityPerMaterial layout across all passes
            CBUFFER_START(UnityPerMaterial)
                float  _Layer1Tiling;
                float  _Layer2Tiling;
                float  _Layer3Tiling;
                float  _Layer4Tiling;
                float  _Layer5Tiling;
                half   _Layer1Smoothness;
                half   _Layer2Smoothness;
                half   _Layer3Smoothness;
                half   _Layer4Smoothness;
                half   _Layer5Smoothness;
                half4  _Layer1Color;
                half4  _Layer2Color;
                half4  _Layer3Color;
                half4  _Layer4Color;
                half4  _Layer5Color;
                float  _SlopeThreshold1;
                float  _SlopeThreshold2;
                float  _SlopeThreshold3;
                float  _SlopeBlend;
                float  _UseTriplanar;
                float  _TriplanarSharpness;
                float  _NoiseScale;
                float  _NoiseStrength;
                float  _MacroScale;
                float  _MacroStrength;
                float  _GlobalScale;
                float  _UseDistanceLOD;
                float  _NearDist;
                float  _MidDist;
                float  _FarDist;
                float  _NearScale;
                float  _MidScale;
                float  _FarScale;
                float  _UseContactDirt;
                float  _ContactStrength;
                float  _ContactRadius;
                float4 _ContactOrigin;
                float4 _ContactSize;
                float  _OpenAreaMacroBoost;
                float4 _Layer1Albedo_ST;
                float4 _Layer2Albedo_ST;
                float4 _Layer3Albedo_ST;
                float4 _Layer4Albedo_ST;
                float4 _Layer5Albedo_ST;
                half   _Metallic;
                half   _NormalStrength;
                half   _OcclusionStrength;
                half   _Surface;
                half   _Cutoff;
            CBUFFER_END

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings ShadowVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

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

                return output;
            }

            half4 ShadowFrag(Varyings input) : SV_Target
            {
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
            #pragma target 2.0
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float  _Layer1Tiling;
                float  _Layer2Tiling;
                float  _Layer3Tiling;
                float  _Layer4Tiling;
                float  _Layer5Tiling;
                half   _Layer1Smoothness;
                half   _Layer2Smoothness;
                half   _Layer3Smoothness;
                half   _Layer4Smoothness;
                half   _Layer5Smoothness;
                half4  _Layer1Color;
                half4  _Layer2Color;
                half4  _Layer3Color;
                half4  _Layer4Color;
                half4  _Layer5Color;
                float  _SlopeThreshold1;
                float  _SlopeThreshold2;
                float  _SlopeThreshold3;
                float  _SlopeBlend;
                float  _UseTriplanar;
                float  _TriplanarSharpness;
                float  _NoiseScale;
                float  _NoiseStrength;
                float  _MacroScale;
                float  _MacroStrength;
                float  _GlobalScale;
                float  _UseDistanceLOD;
                float  _NearDist;
                float  _MidDist;
                float  _FarDist;
                float  _NearScale;
                float  _MidScale;
                float  _FarScale;
                float  _UseContactDirt;
                float  _ContactStrength;
                float  _ContactRadius;
                float4 _ContactOrigin;
                float4 _ContactSize;
                float  _OpenAreaMacroBoost;
                float4 _Layer1Albedo_ST;
                float4 _Layer2Albedo_ST;
                float4 _Layer3Albedo_ST;
                float4 _Layer4Albedo_ST;
                float4 _Layer5Albedo_ST;
                half   _Metallic;
                half   _NormalStrength;
                half   _OcclusionStrength;
                half   _Surface;
                half   _Cutoff;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 DepthFrag(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // =================================================================
        // DEPTH NORMALS PASS
        // =================================================================
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex DepthNormVert
            #pragma fragment DepthNormFrag

            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float  _Layer1Tiling;
                float  _Layer2Tiling;
                float  _Layer3Tiling;
                float  _Layer4Tiling;
                float  _Layer5Tiling;
                half   _Layer1Smoothness;
                half   _Layer2Smoothness;
                half   _Layer3Smoothness;
                half   _Layer4Smoothness;
                half   _Layer5Smoothness;
                half4  _Layer1Color;
                half4  _Layer2Color;
                half4  _Layer3Color;
                half4  _Layer4Color;
                half4  _Layer5Color;
                float  _SlopeThreshold1;
                float  _SlopeThreshold2;
                float  _SlopeThreshold3;
                float  _SlopeBlend;
                float  _UseTriplanar;
                float  _TriplanarSharpness;
                float  _NoiseScale;
                float  _NoiseStrength;
                float  _MacroScale;
                float  _MacroStrength;
                float  _GlobalScale;
                float  _UseDistanceLOD;
                float  _NearDist;
                float  _MidDist;
                float  _FarDist;
                float  _NearScale;
                float  _MidScale;
                float  _FarScale;
                float  _UseContactDirt;
                float  _ContactStrength;
                float  _ContactRadius;
                float4 _ContactOrigin;
                float4 _ContactSize;
                float  _OpenAreaMacroBoost;
                float4 _Layer1Albedo_ST;
                float4 _Layer2Albedo_ST;
                float4 _Layer3Albedo_ST;
                float4 _Layer4Albedo_ST;
                float4 _Layer5Albedo_ST;
                half   _Metallic;
                half   _NormalStrength;
                half   _OcclusionStrength;
                half   _Surface;
                half   _Cutoff;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthNormVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS   = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 DepthNormFrag(Varyings input) : SV_Target
            {
                return half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
            }
            ENDHLSL
        }
    }

    Fallback "Universal Render Pipeline/Lit"
}
