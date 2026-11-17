// Skin for URP 17 (Forward and Forward+). URP has no subsurface scattering, so this does what games without a
// screen-space SSS pass do (Penner 2011, d'Eon & Luebke 2007, Jimenez):
//  - pre-integrated scattering: diffuse looked up in a LUT built from the 6-Gaussian skin profile by N.L and the
//    surface's curvature (baked per vertex in colour R), so light bleeds red past the terminator on curved parts;
//  - blurred normals per channel: red lit with a softened normal (the normal map's mip + 3), green between, blue with
//    the full detail, so pores and fine lines read in the specular and in blue, not as dirt in the red;
//  - softened shadow edges per channel (light scattered under a shadow's edge is red);
//  - dual-lobe GGX specular with F0 0.028 (IOR 1.4), roughness from a map (T-zone and lips shinier), lobes x0.75 and
//    x1.3 mixed 0.85 : 0.15; micro pores tiled over the UVs; SH ambient sampled with the softened normals.
Shader "GK/Skin"
{
    Properties
    {
        [MainTexture] _BaseMap ("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor ("Tint", Color) = (1, 1, 1, 1)
        [Normal] _BumpMap ("Normal", 2D) = "bump" {}
        _BumpScale ("Normal strength", Float) = 1
        _RoughMap ("Roughness (R)", 2D) = "grey" {}
        _RoughnessScale ("Roughness scale", Range(0.5, 1.5)) = 1
        [Normal] _PoreMap ("Pores (micro normal)", 2D) = "bump" {}
        _PoreTiling ("Pore tiling (per UV)", Float) = 48
        _PoreStrength ("Pore strength", Range(0, 2)) = 0.45
        _SkinLUT ("Pre-integrated scattering (N.L, curvature)", 2D) = "white" {}
        _CurvatureScale ("Curvature scale", Range(0, 3)) = 1
        _BlurBias ("Diffuse normal blur (mip bias)", Range(0, 6)) = 3
        _LobeMix ("Specular lobe mix", Range(0, 1)) = 0.85
        _SpecularOcclusion ("Specular in cavities", Range(0, 1)) = 0.6
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
        TEXTURE2D(_RoughMap); SAMPLER(sampler_RoughMap);
        TEXTURE2D(_PoreMap); SAMPLER(sampler_PoreMap);
        TEXTURE2D(_SkinLUT); SAMPLER(sampler_SkinLUT);
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _BumpScale;
            half _RoughnessScale;
            float _PoreTiling;
            half _PoreStrength;
            half _CurvatureScale;
            half _BlurBias;
            half _LobeMix;
            half _SpecularOcclusion;
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
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
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
                float4 tangentWS : TEXCOORD2;
                float2 uv0 : TEXCOORD3;
                half curv : TEXCOORD4;
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
                o.tangentWS = float4(n.tangentWS, i.tangentOS.w * GetOddNegativeScale());
                o.uv0 = TRANSFORM_TEX(i.uv0, _BaseMap);
                o.curv = i.color.r;
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            struct SkinSurface
            {
                half3 albedo;
                half3 nR, nG, nB;     // diffuse normals per channel (soft to sharp)
                half3 nSpec;          // specular normal (full detail + pores)
                half rough;
                half curv;
            };

            half3 LUT(half ndlR, half ndlG, half ndlB, half y)
            {
                half r = SAMPLE_TEXTURE2D_LOD(_SkinLUT, sampler_SkinLUT, float2(ndlR * 0.5 + 0.5, y), 0).r;
                half g = SAMPLE_TEXTURE2D_LOD(_SkinLUT, sampler_SkinLUT, float2(ndlG * 0.5 + 0.5, y), 0).g;
                half b = SAMPLE_TEXTURE2D_LOD(_SkinLUT, sampler_SkinLUT, float2(ndlB * 0.5 + 0.5, y), 0).b;
                return half3(r, g, b);
            }

            half GGX(half NoH, half a)
            {
                half a2 = a * a;
                half d = NoH * NoH * (a2 - 1.0h) + 1.0h;
                return a2 / (PI * d * d + 1e-5h);
            }

            half3 SkinLight(Light l, SkinSurface s, half3 V)
            {
                half3 L = l.direction;
                // shadow edges scatter: red reaches furthest into the shadow
                half sh = l.shadowAttenuation;
                half3 shadow = half3(sqrt(sh), pow(sh, 0.8h), sh);
                half3 diffuse = LUT(dot(s.nR, L), dot(s.nG, L), dot(s.nB, L), s.curv) * shadow * l.distanceAttenuation;
                half3 N = s.nSpec;
                half3 H = SafeNormalize(L + V);
                half NoL = saturate(dot(N, L)), NoV = saturate(dot(N, V)) + 1e-4h, NoH = saturate(dot(N, H)), VoH = saturate(dot(V, H));
                half a1 = max(0.02h, s.rough * s.rough * 0.5625h);          // (r * 0.75)^2
                half a2 = max(0.02h, s.rough * s.rough * 1.69h);            // (r * 1.3)^2
                half D = lerp(GGX(NoH, a2), GGX(NoH, a1), _LobeMix);
                half ar = s.rough * s.rough;
                half Vis = 0.5h / (NoL * (NoV * (1.0h - ar) + ar) + NoV * (NoL * (1.0h - ar) + ar) + 1e-5h);
                half F = 0.028h + 0.972h * Pow4(1.0h - VoH) * (1.0h - VoH);
                half spec = PI * D * Vis * F * NoL * sh * l.distanceAttenuation;
                return (s.albedo * diffuse + spec) * l.color;
            }

            half3 EnvBRDFApprox(half3 F0, half rough, half NoV)
            {
                const half4 c0 = half4(-1, -0.0275, -0.572, 0.022);
                const half4 c1 = half4(1, 0.0425, 1.04, -0.04);
                half4 r = rough * c0 + c1;
                half a004 = min(r.x * r.x, exp2(-9.28h * NoV)) * r.x + r.y;
                half2 AB = half2(-1.04h, 1.04h) * a004 + r.zw;
                return F0 * AB.x + AB.y;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv0).rgb * _BaseColor.rgb;
                half4 nSharpS = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, i.uv0);
                half4 nSoftS = SAMPLE_TEXTURE2D_BIAS(_BumpMap, sampler_BumpMap, i.uv0, _BlurBias);
                half3 nSharp = UnpackNormalScale(nSharpS, _BumpScale);
                half3 nSoft = UnpackNormalScale(nSoftS, _BumpScale);
                half3 pore = UnpackNormalScale(SAMPLE_TEXTURE2D(_PoreMap, sampler_PoreMap, i.uv0 * _PoreTiling), _PoreStrength);
                half3 nDetail = normalize(half3(nSharp.xy + pore.xy, nSharp.z * pore.z));
                float3 bit = i.tangentWS.w * cross(i.normalWS, i.tangentWS.xyz);
                half3x3 tbn = half3x3(i.tangentWS.xyz, bit, i.normalWS);
                SkinSurface s;
                s.albedo = albedo;
                s.nSpec = normalize(TransformTangentToWorld(nDetail, tbn));
                half3 soft = normalize(TransformTangentToWorld(nSoft, tbn));
                s.nR = soft;
                s.nG = normalize(lerp(soft, s.nSpec, 0.5h));
                s.nB = s.nSpec;
                s.rough = saturate(SAMPLE_TEXTURE2D(_RoughMap, sampler_RoughMap, i.uv0).r * _RoughnessScale);
                s.curv = saturate(i.curv * _CurvatureScale);
                half3 V = SafeNormalize(GetWorldSpaceViewDir(i.positionWS));

                InputData inputData = (InputData)0;
                inputData.positionWS = i.positionWS;
                inputData.normalWS = s.nSpec;
                inputData.viewDirectionWS = V;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                inputData.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                half4 shadowMask = half4(1, 1, 1, 1);
                AmbientOcclusionFactor ao = CreateAmbientOcclusionFactor(inputData.normalizedScreenSpaceUV, 1.0h);

                // ambient: SH with the soft normals per channel (no grey, flat ambient), probe reflection for the sheen
                half3 sh = half3(SampleSH(s.nR).r, SampleSH(s.nG).g, SampleSH(s.nB).b);
                half NoV = saturate(dot(s.nSpec, V));
                half3 env = GlossyEnvironmentReflection(reflect(-V, s.nSpec), i.positionWS, s.rough, 1.0h, inputData.normalizedScreenSpaceUV);
                half cavity = lerp(1.0h, saturate(nSharp.z * nSharp.z), _SpecularOcclusion);
                half3 col = (albedo * sh + env * EnvBRDFApprox(half3(0.028h, 0.028h, 0.028h), s.rough, NoV) * cavity) * ao.indirectAmbientOcclusion;

                Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, shadowMask);
                mainLight.color *= ao.directAmbientOcclusion;
                col += SkinLight(mainLight, s, V);

                #if defined(_ADDITIONAL_LIGHTS)
                uint pixelLightCount = GetAdditionalLightsCount();
                #if USE_FORWARD_PLUS
                for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
                {
                    Light l = GetAdditionalLight(lightIndex, inputData.positionWS, shadowMask);
                    col += SkinLight(l, s, V);
                }
                #endif
                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light l = GetAdditionalLight(lightIndex, inputData.positionWS, shadowMask);
                    col += SkinLight(l, s, V);
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
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            float4 vert(Attributes i) : SV_POSITION
            {
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
                return cs;
            }
            half4 frag() : SV_Target { return 0; }
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
            struct Attributes { float4 positionOS : POSITION; };
            float4 vert(Attributes i) : SV_POSITION { return TransformObjectToHClip(i.positionOS.xyz); }
            half frag(float4 p : SV_POSITION) : SV_Target { return p.z; }
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
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };
            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                return o;
            }
            half4 frag(Varyings i) : SV_Target { return half4(NormalizeNormalPerPixel(i.normalWS), 0.0); }
            ENDHLSL
        }
    }
    FallBack Off
}
