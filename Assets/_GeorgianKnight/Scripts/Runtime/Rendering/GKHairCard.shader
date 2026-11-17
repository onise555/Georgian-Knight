// Hair cards for beards and hair (URP 17, Forward and Forward+), the card technique of current games: strips textured
// with strand clumps, alpha-tested on an atlas whose mips preserve coverage.
//  Atlas (linear): R strand tint, G position along the strand (0 root, 1 tip), B clump depth, A coverage.
//  Vertex colour R: layer (0 base .. 1 top) - the lower layers sit in the beard's own shadow.
// Lighting: Kajiya-Kay / Scheuermann - two highlights along the strand (which runs along the card's V): a white primary
// shifted toward the root and a broader secondary tinted by the hair colour; wrapped diffuse and a little light
// through the hair from behind; main-light shadows; SH ambient; additional lights (clustered in Forward+).
Shader "GK/HairCard"
{
    Properties
    {
        _BaseMap ("Strand atlas (R tint, G along, B depth, A coverage)", 2D) = "black" {}
        _Cutoff ("Alpha cutoff", Range(0.05, 0.95)) = 0.4
        _RootColor ("Root colour", Color) = (0.05, 0.035, 0.025, 1)
        _TipColor ("Tip colour", Color) = (0.16, 0.11, 0.075, 1)
        _TintVariation ("Strand tint variation", Range(0, 1)) = 0.35
        _LayerShadow ("Lower layers' shadow", Range(0, 1)) = 0.35
        _Shift1 ("Primary highlight shift", Range(-1, 1)) = 0.12
        _Shift2 ("Secondary highlight shift", Range(-1, 1)) = -0.12
        _Spec1 ("Primary highlight", Range(0, 2)) = 0.35
        _Spec2 ("Secondary highlight", Range(0, 2)) = 0.6
        _Gloss1 ("Primary sharpness", Range(8, 512)) = 140
        _Gloss2 ("Secondary sharpness", Range(4, 256)) = 36
        _Wrap ("Diffuse wrap", Range(0, 1)) = 0.45
        _Transmission ("Back light through hair", Range(0, 1)) = 0.25
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" }
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half _Cutoff;
            half4 _RootColor;
            half4 _TipColor;
            half _TintVariation;
            half _LayerShadow;
            half _Shift1, _Shift2, _Spec1, _Spec2, _Gloss1, _Gloss2;
            half _Wrap, _Transmission;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_LAYERS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv0 : TEXCOORD0;
                half4 color : COLOR;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 strandWS : TEXCOORD2;
                float2 uv0 : TEXCOORD3;
                half layer : TEXCOORD4;
                half fog : TEXCOORD5;
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(i.positionOS.xyz);
                VertexNormalInputs n = GetVertexNormalInputs(i.normalOS, i.tangentOS);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = n.normalWS;
                o.strandWS = n.bitangentWS;            // the strands run along V
                o.uv0 = TRANSFORM_TEX(i.uv0, _BaseMap);
                o.layer = i.color.r;
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half StrandSpec(float3 T, float3 V, float3 L, half exponent)
            {
                float3 H = normalize(L + V);
                float th = dot(T, H);
                float s = sqrt(saturate(1.0 - th * th));
                return pow(s, exponent) * smoothstep(-1.0, 0.0, th);
            }

            half3 HairLight(Light l, float3 N, float3 T, float3 V, half3 albedo, half occ)
            {
                float3 L = l.direction;
                half att = l.distanceAttenuation * l.shadowAttenuation;
                half ndl = dot(N, L);
                half diffuse = saturate((ndl + _Wrap) / ((1.0h + _Wrap) * (1.0h + _Wrap)));
                half back = saturate(dot(-V, L)) * _Transmission * (1.0h - saturate(ndl));
                float3 T1 = normalize(T + N * _Shift1), T2 = normalize(T + N * _Shift2);
                half spec = StrandSpec(T1, V, L, _Gloss1) * _Spec1;
                half3 spec2 = StrandSpec(T2, V, L, _Gloss2) * _Spec2 * albedo * 2.0h;
                return ((albedo * (diffuse + back)) * occ + (spec + spec2) * saturate(ndl + 0.3h) * occ) * l.color * att;
            }

            half4 frag(Varyings i, bool front : SV_IsFrontFace) : SV_Target
            {
                half4 a = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv0);
                clip(a.a - _Cutoff);
                float3 N = normalize(i.normalWS);
                if (!front) N = -N;
                float3 T = normalize(i.strandWS);
                float3 V = SafeNormalize(GetWorldSpaceViewDir(i.positionWS));
                half3 albedo = lerp(_RootColor.rgb, _TipColor.rgb, saturate(a.g)) * lerp(1.0h - _TintVariation, 1.0h + _TintVariation, a.r);
                // the clump's back strands and the lower layers are in the beard's own shadow
                half occ = lerp(0.55h, 1.0h, a.b) * lerp(1.0h - _LayerShadow, 1.0h, i.layer) * lerp(0.7h, 1.0h, saturate(a.g * 1.5h));

                InputData inputData = (InputData)0;
                inputData.positionWS = i.positionWS;
                inputData.normalWS = N;
                inputData.viewDirectionWS = V;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                half4 shadowMask = half4(1, 1, 1, 1);
                AmbientOcclusionFactor ao = CreateAmbientOcclusionFactor(inputData.normalizedScreenSpaceUV, 1.0h);

                half3 col = albedo * SampleSH(N) * occ * ao.indirectAmbientOcclusion;
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(i.positionWS), i.positionWS, shadowMask);
                col += HairLight(mainLight, N, T, V, albedo, occ);
                #if defined(_ADDITIONAL_LIGHTS)
                uint pixelLightCount = GetAdditionalLightsCount();
                #if USE_FORWARD_PLUS
                for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
                {
                    Light l = GetAdditionalLight(lightIndex, inputData.positionWS, shadowMask);
                    col += HairLight(l, N, T, V, albedo, occ);
                }
                #endif
                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light l = GetAdditionalLight(lightIndex, inputData.positionWS, shadowMask);
                    col += HairLight(l, N, T, V, albedo, occ);
                LIGHT_LOOP_END
                #endif
                col = MixFog(col, i.fog);
                return half4(col, 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            float3 _LightDirection;
            float3 _LightPosition;
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv0 : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv0 : TEXCOORD0; };
            Varyings vert(Attributes i)
            {
                Varyings o;
                float3 pws = TransformObjectToWorld(i.positionOS.xyz);
                float3 nws = TransformObjectToWorldNormal(i.normalOS);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 ld = normalize(_LightPosition - pws);
                #else
                float3 ld = _LightDirection;
                #endif
                float4 cs = TransformWorldToHClip(ApplyShadowBias(pws, nws, ld));
                #if UNITY_REVERSED_Z
                cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
                #else
                cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                o.positionCS = cs; o.uv0 = TRANSFORM_TEX(i.uv0, _BaseMap);
                return o;
            }
            half4 frag(Varyings i) : SV_Target { clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv0).a - _Cutoff); return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            struct Attributes { float4 positionOS : POSITION; float2 uv0 : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv0 : TEXCOORD0; };
            Varyings vert(Attributes i) { Varyings o; o.positionCS = TransformObjectToHClip(i.positionOS.xyz); o.uv0 = TRANSFORM_TEX(i.uv0, _BaseMap); return o; }
            half frag(Varyings i) : SV_Target { clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv0).a - _Cutoff); return i.positionCS.z; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv0 : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float2 uv0 : TEXCOORD1; };
            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.uv0 = TRANSFORM_TEX(i.uv0, _BaseMap);
                return o;
            }
            half4 frag(Varyings i) : SV_Target
            {
                clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv0).a - _Cutoff);
                return half4(NormalizeNormalPerPixel(i.normalWS), 0.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
