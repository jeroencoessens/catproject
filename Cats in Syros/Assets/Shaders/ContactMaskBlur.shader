// =============================================================================
// Hidden/ContactMaskBlur
//
// Simple separable Gaussian blur for the contact mask.
// Called twice per pass: once horizontal, once vertical.
// The direction is set via _BlurDir from C#.
// =============================================================================
Shader "Hidden/ContactMaskBlur"
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
            Name "GaussianBlur"

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            float4 _MainTex_TexelSize; // (1/w, 1/h, w, h)
            float4 _BlurDir;           // (dx, dy, 0, 0)

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
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float2 dir = _BlurDir.xy;

                // 9-tap Gaussian kernel (sigma ≈ 2)
                // Weights: 0.0162, 0.0540, 0.1218, 0.1872, 0.2416,
                //          0.1872, 0.1218, 0.0540, 0.0162
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
