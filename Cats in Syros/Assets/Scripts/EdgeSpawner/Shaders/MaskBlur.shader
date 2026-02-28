// =============================================================================
// Hidden/EdgeSpawner/MaskBlur
//
// Separable Gaussian blur for per-chunk footprint masks.
// Called twice per pass: horizontal then vertical (set _BlurDir from C#).
// A small blur smooths noisy geometry edges before Sobel detection.
// =============================================================================
Shader "Hidden/EdgeSpawner/MaskBlur"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "GaussBlur"

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            float4 _MainTex_TexelSize;
            float4 _BlurDir; // (dx, dy, 0, 0)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv;
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv  = input.uv;
                float2 dir = _BlurDir.xy;

                // 9-tap Gaussian (sigma ~2)
                static const float weights[5] = {
                    0.2416, 0.1872, 0.1218, 0.0540, 0.0162
                };

                half sum = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).r * weights[0];
                for (int i = 1; i < 5; i++)
                {
                    float2 offset = dir * (float)i;
                    sum += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + offset).r * weights[i];
                    sum += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv - offset).r * weights[i];
                }

                return half4(sum, sum, sum, 1.0);
            }
            ENDHLSL
        }
    }
}
