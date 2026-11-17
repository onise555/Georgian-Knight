// Fabric for URP 17 (Forward and Forward+). Wool, linen and leather share one baked atlas; the atlas's smoothness tells
// them apart: rough texels (wool, linen) get the cloth model, smoother ones (leather, lacquer) the standard one.
//  - cloth: wrapped diffuse (light creeps round the folds of a thick fabric) + a Charlie sheen lobe (the halo of
//    fibres standing off the surface: the soft bright rim dark wool shows against the light) + a weak, rough GGX;
//  - a woven detail normal tiled at world scale on UV1 (twill a few millimetres across), faded by its own mips, so
//    close up the cloth shows its weave and at a distance it reads as a matte surface with the right sheen;
//  - everything else as URP Lit: main-light cascades, additional lights (clustered in Forward+), SH, reflection
//    probes, SSAO, fog; ShadowCaster, DepthOnly and DepthNormals passes.
Shader "GK/Cloth"
{
    Properties
    {
        [MainTexture] _BaseMap ("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor ("Tint", Color) = (1, 1, 1, 1)
        _MetallicGlossMap ("Metal (R) Smoothness (A)", 2D) = "black" {}
        [Normal] _BumpMap ("Normal", 2D) = "bump" {}
        _BumpScale ("Normal strength", Float) = 1
        _OcclusionMap ("Occlusion", 2D) = "white" {}
        _OcclusionStrength ("Occlusion strength", Range(0, 1)) = 1
        [Normal] _WeaveMap ("Weave normal (UV1, world scale)", 2D) = "bump" {}
        _WeaveScale ("Weave tiles per UV1 unit", Float) = 1
        _WeaveStrength ("Weave strength", Range(0, 2)) = 0.8
        _SheenTint ("Sheen tint", Range(0, 1)) = 0.45
        _SheenRoughness ("Sheen roughness", Range(0.2, 1)) = 0.55
        _Wrap ("Diffuse wrap", Range(0, 0.5)) = 0.22
        _ClothSmoothnessMax ("Cloth below this smoothness", Range(0, 1)) = 0.36
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_MetallicGlossMap); SAMPLER(sampler_MetallicGlossMap);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
        TEXTURE2D(_OcclusionMap); SAMPLER(sampler_OcclusionMap);
        TEXTURE2D(_WeaveMap); SAMPLER(sampler_WeaveMap);
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _BumpScale;
            half _OcclusionStrength;
            float _WeaveScale;
            half _WeaveStrength;
            half _SheenTint;
            half _SheenRoughness;
            half _Wrap;
            half _ClothSmoothnessMax;
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/AmbientOcclusion.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 tangentWS : TEXCOORD2;
                float2 uv0 : TEXCOORD3;
                float2 uv1 : TEXCOORD4;
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
                o.uv1 = i.uv1 * _WeaveScale;
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            // Charlie sheen distribution (Estevez & Kulla 2017) and Neubelt's visibility
            half D_Charlie(half rough, half NoH)
            {
                half invA = 1.0h / max(rough * rough, 1e-3h);
                half sin2 = max(1.0h - NoH * NoH, 0.0078125h);
                return (2.0h + invA) * pow(sin2, invA * 0.5h) / (2.0h * PI);
            }
            half V_Neubelt(half NoV, half NoL) { return saturate(1.0h / (4.0h * (NoL + NoV - NoL * NoV))); }

            half3 ClothLight(Light l, half3 N, half3 V, half3 albedo, half3 sheen, half rough, BRDFData brdf, half cloth)
            {
                half3 L = l.direction;
                half NoL = dot(N, L);
                half att = l.distanceAttenuation * l.shadowAttenuation;
                half3 H = SafeNormalize(L + V);
                half NoH = saturate(dot(N, H)), NoV = saturate(dot(N, V)), sNoL = saturate(NoL);
                // thick fabric: the diffuse wraps a little past the terminator
                half wrapped = saturate((NoL + _Wrap) / ((1.0h + _Wrap) * (1.0h + _Wrap)));
                half3 diffuseCloth = albedo * wrapped;
                half3 sheenTerm = sheen * D_Charlie(rough, NoH) * V_Neubelt(NoV, sNoL) * sNoL;
                half3 spec = DirectBRDFSpecular(brdf, N, L, V) * brdf.specular;
                half3 standard = (brdf.diffuse + spec) * sNoL;                                   // URP Lit: Lambert + GGX
                half3 c = lerp(standard, diffuseCloth * (1.0h - 0.5h * max(sheen.r, max(sheen.g, sheen.b))) + sheenTerm
                               + spec * sNoL * 0.3h, cloth);
                return c * l.color * att;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 base = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv0) * _BaseColor;
                half4 mg = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, i.uv0);
                half smooth = mg.a, metal = mg.r;
                half cloth = saturate((_ClothSmoothnessMax - smooth) / 0.08h) * (1.0h - metal);
                half3 nTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, i.uv0), _BumpScale);
                half3 wTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_WeaveMap, sampler_WeaveMap, i.uv1), _WeaveStrength * cloth);
                nTS = normalize(half3(nTS.xy + wTS.xy, nTS.z * wTS.z));
                float sgn = i.tangentWS.w;
                float3 bit = sgn * cross(i.normalWS, i.tangentWS.xyz);
                half3 N = normalize(TransformTangentToWorld(nTS, half3x3(i.tangentWS.xyz, bit, i.normalWS)));
                half3 V = SafeNormalize(GetWorldSpaceViewDir(i.positionWS));
                half occ = lerp(1.0h, SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, i.uv0).g, _OcclusionStrength);

                InputData inputData = (InputData)0;
                inputData.positionWS = i.positionWS;
                inputData.normalWS = N;
                inputData.viewDirectionWS = V;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                inputData.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                half4 shadowMask = half4(1, 1, 1, 1);

                // cloth: dull, a broad rough highlight; the sheen tint follows the dye (bright fibres on a dark cloth
                // still glow grey) and is stronger the rougher the fabric
                half3 albedo = base.rgb;
                half3 sheen = sqrt(max(albedo, 0.004h)) * _SheenTint * cloth;
                half clothSmooth = min(smooth, 0.3h);
                BRDFData brdf;
                half alpha = 1.0h;
                InitializeBRDFData(albedo, metal, half3(0, 0, 0), lerp(smooth, clothSmooth, cloth), alpha, brdf);

                AmbientOcclusionFactor ao = CreateAmbientOcclusionFactor(inputData.normalizedScreenSpaceUV, occ);
                Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, shadowMask);
                mainLight.color *= ao.directAmbientOcclusion;

                half NoV = saturate(dot(N, V));
                half3 sh = SampleSH(N);
                // indirect: SH diffuse, probe reflection for the non-cloth parts, a soft grazing sheen for the cloth
                half3 reflectVector = reflect(-V, N);
                half3 env = GlossyEnvironmentReflection(reflectVector, i.positionWS, brdf.perceptualRoughness, 1.0h, inputData.normalizedScreenSpaceUV);
                half fresnel = Pow4(1.0h - NoV);
                half3 indirect = sh * brdf.diffuse + env * EnvironmentBRDFSpecular(brdf, fresnel) * (1.0h - 0.6h * cloth);
                indirect += sh * sheen * (0.25h + 0.75h * fresnel) * cloth;
                half3 col = indirect * ao.indirectAmbientOcclusion;

                col += ClothLight(mainLight, N, V, albedo, sheen, _SheenRoughness, brdf, cloth);

                #if defined(_ADDITIONAL_LIGHTS)
                uint pixelLightCount = GetAdditionalLightsCount();
                #if USE_FORWARD_PLUS
                for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
                {
                    Light l = GetAdditionalLight(lightIndex, inputData.positionWS, shadowMask);
                    col += ClothLight(l, N, V, albedo, sheen, _SheenRoughness, brdf, cloth);
                }
                #endif
                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light l = GetAdditionalLight(lightIndex, inputData.positionWS, shadowMask);
                    col += ClothLight(l, N, V, albedo, sheen, _SheenRoughness, brdf, cloth);
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
