Shader "TerrainGrassDemo/GrassIndirect"
{
    Properties
    {
        _MainTex ("Grass Blade Atlas", 2D) = "white" {}
        _AtlasColumns ("Atlas Columns", Float) = 4
        _ColorBottom ("Root Color", Color) = (0.055, 0.14, 0.025, 1)
        _ColorTop ("Tip Color", Color) = (0.38, 0.68, 0.12, 1)
        _DryColor ("Dry Accent", Color) = (0.58, 0.52, 0.18, 1)
        _MaxDrawDistance ("Max Draw Distance", Float) = 160
        _FadeRange ("Fade Range", Float) = 20
        _InteractionStrength ("Interaction Strength", Float) = 0.4
        _Transmission ("Transmission", Range(0,2)) = 0.72
        _Smoothness ("Smoothness", Range(0,1)) = 0.18
        _AlphaCutoff ("Alpha Cutoff", Range(0,1)) = 0.42
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        Cull Off
        ZWrite On
        AlphaToMask On

        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        struct GrassInstance
        {
            float3 position;
            float yaw;
            float height;
            float width;
            float type01;
            float seed01;
        };

        #if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
            StructuredBuffer<GrassInstance> _VisibleIndices;
        #endif

        TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
        TEXTURE2D(_GrassGustTex); SAMPLER(sampler_GrassGustTex);
        TEXTURE2D(_GrassInteractionTex); SAMPLER(sampler_GrassInteractionTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
            half4 _ColorBottom;
            half4 _ColorTop;
            half4 _DryColor;
            float _AtlasColumns;
            float _MaxDrawDistance;
            float _FadeRange;
            float _InteractionStrength;
            float _Transmission;
            float _Smoothness;
            float _AlphaCutoff;
            float4 _GrassModelScale;
        CBUFFER_END

        float4 _GrassWindDirection;
        float4 _GrassWindGust;
        float4 _GrassWindTime;
        float4 _GrassInteractionCenter;
        float _GrassFullDensity;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;
            float4 color : COLOR;
            uint instanceID : SV_InstanceID;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            half3 normalWS : TEXCOORD1;
            float2 uv : TEXCOORD2;
            half hFrac : TEXCOORD3;
            half fade : TEXCOORD4;
            half type01 : TEXCOORD5;
            half slice : TEXCOORD6;
            half fogFactor : TEXCOORD7;
        };

        void ConfigureProcedural() { }

        float Hash21(float2 p)
        {
            p = frac(p * float2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return frac(p.x * p.y);
        }

        GrassInstance LoadGrass(uint instanceID)
        {
            GrassInstance grass;
            #if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
                grass = _VisibleIndices[instanceID];
            #else
                grass.position = 0;
                grass.yaw = 0;
                grass.height = 1;
                grass.width = 0.25;
                grass.type01 = 0.5;
                grass.seed01 = 0.5;
            #endif
            return grass;
        }

        void BuildGrassVertex(Attributes input, out float3 positionWS, out half3 normalWS,
            out half hFrac, out half fade, out half type01, out half slice)
        {
            GrassInstance grass = LoadGrass(input.instanceID);
            grass.position.y += 0.018 + grass.seed01 * 0.025;

            float c = cos(grass.yaw);
            float s = sin(grass.yaw);
            float3 localPos;

            #if defined(GRASS_MODEL_SHAPE)
                hFrac = saturate(input.positionOS.y * _GrassModelScale.y);
                localPos = float3(input.positionOS.x * grass.width * _GrassModelScale.x,
                                  input.positionOS.y * grass.height * _GrassModelScale.y,
                                  input.positionOS.z * grass.width * _GrassModelScale.x);
            #else
                hFrac = saturate(input.positionOS.y);
                localPos = float3(input.positionOS.x * grass.width,
                                  input.positionOS.y * grass.height,
                                  input.positionOS.z * grass.width);
            #endif

            float3 rotated = float3(c * localPos.x + s * localPos.z,
                                    localPos.y,
                                   -s * localPos.x + c * localPos.z);
            float3 rotatedNormal = normalize(float3(c * input.normalOS.x + s * input.normalOS.z,
                                                    input.normalOS.y,
                                                   -s * input.normalOS.x + c * input.normalOS.z));
            positionWS = grass.position + rotated;

            float2 rawWind = _GrassWindDirection.xy;
            float2 windXZ = dot(rawWind, rawWind) > 0.0001 ? normalize(rawWind) : float2(1, 0);
            float3 bend = 0;

            #if defined(_GRASS_WIND_ON)
                float phase = _GrassWindTime.x + dot(grass.position.xz, windXZ) * 0.085 + grass.seed01 * 2.4;
                float primary = sin(phase) * 0.58 + sin(phase * 0.47 + 1.8) * 0.26;
                #if defined(GRASS_LOD1)
                    float gust = sin(phase * 0.23) * 0.16;
                #else
                    float2 gustUV = grass.position.xz * 0.004 - windXZ * _GrassWindTime.y * 0.004;
                    float gust = (SAMPLE_TEXTURE2D_LOD(_GrassGustTex, sampler_GrassGustTex, gustUV, 0).r - 0.5) * _GrassWindGust.x;
                #endif
                float bendWeight = hFrac * hFrac;
                float bendAmount = (_GrassWindDirection.z * 0.30 + primary * 0.14 + gust * 0.18) * bendWeight;
                bend = float3(windXZ.x, 0, windXZ.y) * bendAmount;
            #endif

            #if defined(_GRASS_INTERACTION_ON) && !defined(GRASS_LOD1)
                float extent = max(_GrassInteractionCenter.z, 0.001);
                float2 interactionUV = (positionWS.xz - _GrassInteractionCenter.xy) / extent * 0.5 + 0.5;
                float4 interaction = SAMPLE_TEXTURE2D_LOD(_GrassInteractionTex, sampler_GrassInteractionTex, interactionUV, 0);
                float2 push = (interaction.rg - 0.5) * 2.0;
                bend += float3(push.x, 0, push.y) * interaction.b * _InteractionStrength * hFrac * hFrac;
            #endif

            positionWS += bend;
            normalWS = normalize(rotatedNormal * 0.62 + half3(0, 1, 0) * 0.38 - normalize(bend + 0.0001) * length(bend) * 0.35);

            float distanceToCamera = distance(GetCameraPositionWS(), positionWS);
            float individualLimit = lerp(_GrassFullDensity, _MaxDrawDistance, grass.seed01);
            fade = 1.0 - saturate((distanceToCamera - (individualLimit - max(_FadeRange, 1.0))) / max(_FadeRange, 1.0));
            type01 = grass.type01;
            slice = input.color.r;
        }

        half4 SampleBlade(float2 uv, half type01, half slice)
        {
            #if defined(GRASS_MODEL_SHAPE)
                return half4(1, 1, 1, 1);
            #else
                float column = floor(frac(type01 + slice) * max(_AtlasColumns, 1.0));
                float2 atlasUV = float2((column + saturate(uv.x)) / max(_AtlasColumns, 1.0), saturate(uv.y));
                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, atlasUV);
            #endif
        }
        ENDHLSL

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:ConfigureProcedural
            #pragma multi_compile _ GRASS_LOD1
            #pragma multi_compile _ GRASS_MODEL_SHAPE
            #pragma multi_compile _ _GRASS_WIND_ON
            #pragma multi_compile _ _GRASS_INTERACTION_ON
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            Varyings Vert(Attributes input)
            {
                Varyings output;
                BuildGrassVertex(input, output.positionWS, output.normalWS, output.hFrac,
                    output.fade, output.type01, output.slice);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv = input.uv;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                half4 blade = SampleBlade(input.uv, input.type01, input.slice);
                clip(blade.a - _AlphaCutoff);
                clip(input.fade - Hash21(input.positionCS.xy));

                half rootAO = lerp(0.76, 1.0, smoothstep(0.0, 0.34, input.hFrac));
                half3 tint = lerp(_ColorBottom.rgb, _ColorTop.rgb, smoothstep(0.02, 0.92, input.hFrac));
                half dryMask = smoothstep(0.72, 1.0, input.type01) * smoothstep(0.35, 0.92, input.hFrac);
                tint = lerp(tint, _DryColor.rgb, dryMask * 0.20);
                float paletteWave = sin(input.positionWS.x * 0.031 + sin(input.positionWS.z * 0.017) * 1.7)
                    + sin(input.positionWS.z * 0.026 - input.positionWS.x * 0.009);
                half coolPatch = saturate(paletteWave * 0.24 + 0.5);
                half warmPatch = saturate(-paletteWave * 0.18 + 0.34) * smoothstep(0.45, 0.95, input.type01);
                tint = lerp(tint, tint * half3(0.72, 1.06, 1.12), coolPatch * 0.28);
                tint = lerp(tint, half3(0.48, 0.68, 0.22), warmPatch * 0.18);
                half instanceVariation = lerp(0.82, 1.16, frac(input.type01 * 7.31));
                half3 albedo = blade.rgb * tint * rootAO * instanceVariation;

                half faceSign = IS_FRONT_VFACE(face, 1.0, -1.0);
                half3 normalWS = normalize(input.normalWS * faceSign);
                half3 viewDir = SafeNormalize(GetCameraPositionWS() - input.positionWS);
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));

                half ndl = dot(normalWS, mainLight.direction);
                half wrapped = saturate(ndl * 0.44 + 0.56);
                half shadow = lerp(0.66, 1.0, mainLight.shadowAttenuation);
                half3 indirect = (SampleSH(half3(0, 1, 0)) + half3(0.055, 0.09, 0.075)) * albedo;
                half3 direct = albedo * mainLight.color * wrapped * shadow;

                half backLight = pow(saturate(dot(-mainLight.direction, viewDir)), 3.0) * (1.0 - saturate(ndl));
                half tipTransmission = lerp(0.42, 1.0, input.hFrac);
                half3 transmission = albedo * mainLight.color * backLight * _Transmission * tipTransmission * mainLight.shadowAttenuation;

                half3 halfDir = SafeNormalize(mainLight.direction + viewDir);
                half spec = pow(saturate(dot(normalWS, halfDir)), lerp(96.0, 18.0, _Smoothness));
                half rim = pow(1.0 - saturate(dot(normalWS, viewDir)), 3.0);
                half3 color = indirect + direct + transmission + albedo * rim * 0.18
                    + mainLight.color * spec * _Smoothness * 0.12;
                color = MixFog(color, input.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:ConfigureProcedural
            #pragma multi_compile _ GRASS_LOD1
            #pragma multi_compile _ GRASS_MODEL_SHAPE
            #pragma multi_compile _ _GRASS_WIND_ON
            #pragma multi_compile _ _GRASS_INTERACTION_ON

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half type01 : TEXCOORD1;
                half slice : TEXCOORD2;
                half fade : TEXCOORD3;
            };

            ShadowVaryings ShadowVert(Attributes input)
            {
                ShadowVaryings output;
                float3 positionWS;
                half3 normalWS;
                half hFrac;
                BuildGrassVertex(input, positionWS, normalWS, hFrac, output.fade, output.type01, output.slice);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = input.uv;
                return output;
            }

            half4 ShadowFrag(ShadowVaryings input) : SV_Target
            {
                half4 blade = SampleBlade(input.uv, input.type01, input.slice);
                clip(blade.a - _AlphaCutoff);
                clip(input.fade - Hash21(input.positionCS.xy));
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:ConfigureProcedural
            #pragma multi_compile _ GRASS_LOD1
            #pragma multi_compile _ GRASS_MODEL_SHAPE
            #pragma multi_compile _ _GRASS_WIND_ON
            #pragma multi_compile _ _GRASS_INTERACTION_ON

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half type01 : TEXCOORD1;
                half slice : TEXCOORD2;
                half fade : TEXCOORD3;
            };

            DepthVaryings DepthVert(Attributes input)
            {
                DepthVaryings output;
                float3 positionWS;
                half3 normalWS;
                half hFrac;
                BuildGrassVertex(input, positionWS, normalWS, hFrac, output.fade, output.type01, output.slice);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = input.uv;
                return output;
            }

            half4 DepthFrag(DepthVaryings input) : SV_Target
            {
                half4 blade = SampleBlade(input.uv, input.type01, input.slice);
                clip(blade.a - _AlphaCutoff);
                clip(input.fade - Hash21(input.positionCS.xy));
                return 0;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
