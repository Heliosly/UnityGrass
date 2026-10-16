Shader "TerrainGrassDemo/SkyGradient"
{
    Properties
    {
        _TopColor ("Zenith", Color) = (0.16, 0.32, 0.56, 1)
        _HorizonColor ("Horizon", Color) = (0.72, 0.81, 0.86, 1)
        _BottomColor ("Ground Bounce", Color) = (0.40, 0.43, 0.38, 1)
        _SunColor ("Sun", Color) = (1.0, 0.70, 0.38, 1)
        _SunDirection ("Sun Direction", Vector) = (0.3, 0.45, 0.55, 1)
        _SunSize ("Sun Size", Range(64, 2048)) = 420
        _SunGlow ("Sun Glow", Range(1, 32)) = 8
        _Exposure ("Exposure", Range(0, 4)) = 1
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off
        ZWrite Off

        Pass
        {
            Name "Sky"
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _TopColor;
                half4 _HorizonColor;
                half4 _BottomColor;
                half4 _SunColor;
                float4 _SunDirection;
                float _SunSize;
                float _SunGlow;
                float _Exposure;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 direction : TEXCOORD0; };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.direction = input.positionOS.xyz;
                return output;
            }

            float SkyHash(float2 p)
            {
                p = frac(p * float2(123.34, 345.45));
                p += dot(p, p + 34.345);
                return frac(p.x * p.y);
            }

            float SkyNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(SkyHash(i), SkyHash(i + float2(1, 0)), f.x),
                    lerp(SkyHash(i + float2(0, 1)), SkyHash(i + 1), f.x), f.y);
            }

            float SkyFbm(float2 p)
            {
                float value = SkyNoise(p) * 0.58;
                value += SkyNoise(p * 2.03 + 7.1) * 0.28;
                value += SkyNoise(p * 4.11 + 19.7) * 0.14;
                return value;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 direction = normalize(input.direction);
                float height = direction.y;
                float above = pow(saturate(height), 0.46);
                float below = saturate(-height * 3.2);
                half3 sky = height >= 0.0
                    ? lerp(_HorizonColor.rgb, _TopColor.rgb, above)
                    : lerp(_HorizonColor.rgb, _BottomColor.rgb, below);

                float sunDot = saturate(dot(direction, normalize(_SunDirection.xyz)));
                float disc = pow(sunDot, _SunSize);
                float glow = pow(sunDot, _SunGlow) * 0.22;
                sky += _SunColor.rgb * (disc * 7.5 + glow);

                // 柔和高空云层：低频形状 + 两层细节，保持静态以免抢夺草风动画的视觉焦点。
                float2 cloudUV = direction.xz / max(direction.y + 0.42, 0.12) * 3.6 + float2(2.7, -1.3);
                float cloudNoise = SkyFbm(cloudUV);
                float cloudBand = smoothstep(0.01, 0.20, height) * (1.0 - smoothstep(0.68, 0.93, height));
                float clouds = smoothstep(0.58, 0.74, cloudNoise) * cloudBand;
                half3 cloudColor = lerp(half3(0.98, 0.94, 1.0), half3(1.0, 0.82, 0.90), saturate(1.0 - height));
                sky = lerp(sky, cloudColor * _Exposure, clouds * 0.52);

                // Subtle warm band provides depth without fake procedural cloud stripes.
                float horizonBand = exp2(-abs(height) * 18.0);
                sky = lerp(sky, sky * half3(1.08, 0.97, 0.86), horizonBand * 0.18);
                return half4(sky * _Exposure, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
