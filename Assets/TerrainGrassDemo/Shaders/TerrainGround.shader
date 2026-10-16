Shader "TerrainGrassDemo/TerrainGround"
{
    Properties
    {
        _MainTex ("Ground Albedo", 2D) = "white" {}
        _GroundAO ("Ground AO", 2D) = "white" {}
        _GroundNormal ("Ground Normal", 2D) = "bump" {}
        _Tiling ("Macro Tiling", Float) = 4
        _TexTint ("Texture Strength", Range(0,1)) = 0.75
        _ColorLow ("Valley Color", Color) = (0.10, 0.22, 0.08, 1)
        _ColorMid ("Meadow Color", Color) = (0.22, 0.38, 0.12, 1)
        _ColorHigh ("Ridge Color", Color) = (0.42, 0.39, 0.27, 1)
        _LowHeight ("Low Height", Float) = 0
        _HighHeight ("High Height", Float) = 16
        _NormalStrength ("Normal Strength", Range(0,2)) = 0.65
        _Roughness ("Roughness", Range(0,1)) = 0.86
        _WorldCenter ("World Center XZ", Vector) = (512, 512, 0, 0)
        _PathParams ("Path Width, Amplitude, Frequency, Enabled", Vector) = (2.4, 8, 0.018, 1)
        _PathColor ("Path Color", Color) = (0.28, 0.22, 0.13, 1)
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Back
        ZWrite On

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_MainTex);       SAMPLER(sampler_MainTex);
            TEXTURE2D(_GroundAO);      SAMPLER(sampler_GroundAO);
            TEXTURE2D(_GroundNormal);  SAMPLER(sampler_GroundNormal);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _ColorLow;
                half4 _ColorMid;
                half4 _ColorHigh;
                float _LowHeight;
                float _HighHeight;
                float _Tiling;
                float _TexTint;
                float _NormalStrength;
                float _Roughness;
                float4 _WorldCenter;
                float4 _PathParams;
                half4 _PathColor;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                float fogFactor : TEXCOORD2;
            };

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float SmoothNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(Hash21(i), Hash21(i + float2(1, 0)), f.x),
                    lerp(Hash21(i + float2(0, 1)), Hash21(i + 1), f.x), f.y);
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = pos.positionCS;
                output.positionWS = pos.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 detailUV = input.positionWS.xz / 2.35;
                half3 detail = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, detailUV).rgb;
                half ao = SAMPLE_TEXTURE2D(_GroundAO, sampler_GroundAO, detailUV).r;
                half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_GroundNormal, sampler_GroundNormal, detailUV), _NormalStrength);

                half height01 = saturate((input.positionWS.y - _LowHeight) / max(_HighHeight - _LowHeight, 0.001));
                half ridge = smoothstep(0.52, 1.0, height01);
                half valley = 1.0 - smoothstep(0.0, 0.28, height01);
                half3 baseColor = _ColorMid.rgb;
                baseColor = lerp(baseColor, _ColorLow.rgb, valley * 0.58);
                baseColor = lerp(baseColor, _ColorHigh.rgb, ridge * 0.72);
                baseColor = lerp(baseColor, baseColor * detail * 1.75, _TexTint * 0.44);
                float macroNoise = SmoothNoise(input.positionWS.xz / 22.0);
                baseColor *= lerp(0.90, 1.08, macroNoise);

                float pathX = _WorldCenter.x + sin((input.positionWS.z - _WorldCenter.y) * _PathParams.z) * _PathParams.y;
                float pathDistance = abs(input.positionWS.x - pathX);
                half pathMask = (1.0 - smoothstep(_PathParams.x - 0.18, _PathParams.x + 0.52, pathDistance)) * _PathParams.w;
                half pathVariation = lerp(0.88, 1.08, SmoothNoise(input.positionWS.xz * 0.42));
                half3 pathColor = _PathColor.rgb * pathVariation;
                baseColor = lerp(baseColor, pathColor, pathMask * 0.92);

                half3 geometricN = normalize(input.normalWS);
                half3 tangentWS = normalize(cross(half3(0, 0, 1), geometricN));
                if (dot(tangentWS, tangentWS) < 0.01) tangentWS = half3(1, 0, 0);
                half3 bitangentWS = normalize(cross(geometricN, tangentWS));
                half3 normalWS = normalize(tangentWS * normalTS.x + bitangentWS * normalTS.y + geometricN * normalTS.z);

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half wrapped = saturate(dot(normalWS, mainLight.direction) * 0.60 + 0.40);
                half shadow = lerp(0.62, 1.0, mainLight.shadowAttenuation);
                half3 indirect = (SampleSH(normalWS) + half3(0.04, 0.065, 0.055)) * baseColor;
                half3 direct = baseColor * mainLight.color * wrapped * shadow;
                half3 viewDir = SafeNormalize(GetCameraPositionWS() - input.positionWS);
                half3 halfDir = SafeNormalize(mainLight.direction + viewDir);
                half spec = pow(saturate(dot(normalWS, halfDir)), lerp(72.0, 8.0, _Roughness));
                half3 color = (indirect + direct + mainLight.color * spec * 0.035) * lerp(0.72, 1.0, ao);
                return half4(MixFog(color, input.fogFactor), 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; };
            Varyings ShadowVert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
                #if UNITY_REVERSED_Z
                    output.positionCS.z = min(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    output.positionCS.z = max(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return output;
            }
            half4 ShadowFrag(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    FallBack Off
}
