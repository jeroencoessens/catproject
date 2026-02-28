// =============================================================================
// Hidden/EdgeSpawner/FoliageRender
//
// URP-compatible alpha-cutout instanced foliage shader.
// Reads FoliageInstance data from a StructuredBuffer via
// DrawMeshInstancedIndirect. Works with any mesh (cross-quad, rock, etc.).
//
// Forward Lit + ShadowCaster passes.
// Wind animation, per-instance colour variation, distance fade.
// =============================================================================
Shader "Hidden/EdgeSpawner/FoliageRender"
{
    Properties
    {
        _MainTex        ("Texture", 2D) = "white" {}
        _Cutoff         ("Alpha Cutoff", Range(0,1)) = 0.4
        _BaseColor      ("Base Color", Color) = (0.35, 0.55, 0.2, 1)
        _TipColor       ("Tip Color",  Color) = (0.6, 0.8, 0.3, 1)
        _WindSpeed      ("Wind Speed", Float) = 1.5
        _WindStrength   ("Wind Strength", Float) = 0.15
        _ColorVariation ("Color Variation", Range(0,1)) = 0.15
        _UniformScale   ("Uniform Scale", Float) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
        }

        Cull Off
        ZWrite On

        // =============================================================
        // FORWARD LIT PASS
        // =============================================================
        Pass
        {
            Name "FoliageForward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct FoliageInstance
            {
                float3 position;
                float  rotation;
                float2 scale;
                float  colorVar;
                float  fade;
            };

            StructuredBuffer<FoliageInstance> _VisibleBuffer;

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half   _Cutoff;
                half4  _BaseColor;
                half4  _TipColor;
                float  _WindSpeed;
                float  _WindStrength;
                half   _ColorVariation;
                float  _UniformScale;
            CBUFFER_END

            struct Attributes
            {
                float4 posOS : POSITION;
                float2 uv    : TEXCOORD0;
            };

            struct Varyings
            {
                float4 posCS    : SV_POSITION;
                float2 uv       : TEXCOORD0;
                float  fogCoord : TEXCOORD1;
                half   colorVar : TEXCOORD2;
                float3 worldPos : TEXCOORD3;
                half   fade     : TEXCOORD4;
            };

            Varyings vert(Attributes IN, uint instanceID : SV_InstanceID)
            {
                Varyings OUT;
                FoliageInstance gi = _VisibleBuffer[instanceID];

                float w = gi.scale.x * _UniformScale;
                float h = gi.scale.y * _UniformScale;

                float3 localPos = IN.posOS.xyz;
                localPos.x *= w;
                localPos.z *= w;
                localPos.y *= h;

                // Anchor at base
                localPos.y += h * 0.5;

                // Rotate around Y
                float s, c;
                sincos(gi.rotation, s, c);
                float3 rotated;
                rotated.x = localPos.x * c - localPos.z * s;
                rotated.z = localPos.x * s + localPos.z * c;
                rotated.y = localPos.y;

                float3 worldPos = gi.position + rotated;

                // Wind displacement (stronger at top)
                float windPhase = _Time.y * _WindSpeed + gi.position.x * 0.3 + gi.position.z * 0.2;
                float windAmount = sin(windPhase) * _WindStrength;
                float heightFactor = saturate(IN.posOS.y + 0.5);
                worldPos.x += windAmount * heightFactor;
                worldPos.z += windAmount * 0.5 * heightFactor;

                // Distance fade — shrink into ground
                worldPos.y = gi.position.y + (worldPos.y - gi.position.y) * gi.fade;

                OUT.posCS    = TransformWorldToHClip(worldPos);
                OUT.uv       = TRANSFORM_TEX(IN.uv, _MainTex);
                OUT.fogCoord = ComputeFogFactor(OUT.posCS.z);
                OUT.colorVar = gi.colorVar;
                OUT.worldPos = worldPos;
                OUT.fade     = gi.fade;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                half fadedCutoff = _Cutoff + (1.0 - IN.fade) * 0.5;
                clip(tex.a - fadedCutoff);

                // Base-to-tip gradient colour
                float heightGrad = saturate(IN.uv.y);
                half3 grassColor = lerp(_BaseColor.rgb, _TipColor.rgb, heightGrad);

                // Per-instance variation
                half3 variation = half3(
                    IN.colorVar * 0.10 - 0.05,
                    IN.colorVar * 0.15 - 0.075,
                    IN.colorVar * 0.05 - 0.025
                );
                grassColor += variation * _ColorVariation;

                // Simple diffuse
                Light mainLight = GetMainLight();
                half NdotL = saturate(dot(float3(0, 1, 0), mainLight.direction));
                half3 diffuse = grassColor * tex.rgb * (NdotL * 0.6 + 0.4) * mainLight.color;

                half3 finalColor = MixFog(diffuse, IN.fogCoord);
                return half4(finalColor, 1.0);
            }
            ENDHLSL
        }

        // =============================================================
        // SHADOW CASTER PASS
        // =============================================================
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex vertShadow
            #pragma fragment fragShadow
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct FoliageInstance
            {
                float3 position;
                float  rotation;
                float2 scale;
                float  colorVar;
                float  fade;
            };

            StructuredBuffer<FoliageInstance> _VisibleBuffer;

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half   _Cutoff;
                half4  _BaseColor;
                half4  _TipColor;
                float  _WindSpeed;
                float  _WindStrength;
                half   _ColorVariation;
                float  _UniformScale;
            CBUFFER_END

            struct Attributes
            {
                float4 posOS : POSITION;
                float2 uv    : TEXCOORD0;
            };

            struct Varyings
            {
                float4 posCS : SV_POSITION;
                float2 uv    : TEXCOORD0;
            };

            Varyings vertShadow(Attributes IN, uint instanceID : SV_InstanceID)
            {
                Varyings OUT;
                FoliageInstance gi = _VisibleBuffer[instanceID];

                float w = gi.scale.x * _UniformScale;
                float h = gi.scale.y * _UniformScale;

                float3 localPos = IN.posOS.xyz;
                localPos.x *= w;
                localPos.z *= w;
                localPos.y *= h;
                localPos.y += h * 0.5;

                float s, c;
                sincos(gi.rotation, s, c);
                float3 rotated;
                rotated.x = localPos.x * c - localPos.z * s;
                rotated.z = localPos.x * s + localPos.z * c;
                rotated.y = localPos.y;

                float3 worldPos = gi.position + rotated;

                // Wind — must match forward pass
                float windPhase = _Time.y * _WindSpeed + gi.position.x * 0.3 + gi.position.z * 0.2;
                float windAmount = sin(windPhase) * _WindStrength;
                float heightFactor = saturate(IN.posOS.y + 0.5);
                worldPos.x += windAmount * heightFactor;
                worldPos.z += windAmount * 0.5 * heightFactor;

                worldPos.y = gi.position.y + (worldPos.y - gi.position.y) * gi.fade;

                OUT.posCS = TransformWorldToHClip(worldPos);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            half4 fragShadow(Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                clip(tex.a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
}
