// Shell hair for short beards and stubble (URP 17). Each shell layer carries its height t (0..1) in vertex colour A;
// UV0 tiles the strand map (R length, G tint, B radial distance in the strand, A coverage), UV1 is the body UV that
// samples the region mask (the trimmed beard outline). A strand is drawn on layer t where length * mask > t, tapering
// as it rises. Beyond arm's length a strand is far thinner than a pixel and the mip only knows how much of the tile
// the strands cover (A) and how long they are on average (R / A): there each shell keeps that fraction of its
// surface, in a pattern fixed to the surface at the mip's cell size and different for every layer, so the layers add
// up to the beard's true density instead of vanishing (a thresholded mip loses every strand).
// Lighting: wrapped Lambert + a Kajiya-Kay highlight along the combed direction, main-light shadows, SH ambient,
// additional lights; alpha-tested, so it sorts and shadows like opaque geometry.
Shader "GK/HairShell"
{
    Properties
    {
        _StrandTex ("Strands (R length, G tint, B radial, A coverage)", 2D) = "black" {}
        _MaskTex ("Region mask (UV1)", 2D) = "black" {}
        _RootColor ("Root colour", Color) = (0.10, 0.08, 0.07, 1)
        _TipColor ("Tip colour", Color) = (0.22, 0.17, 0.13, 1)
        _Taper ("Taper", Range(0, 1)) = 0.75
        _Smoothness ("Smoothness", Range(0, 1)) = 0.55
        _SpecShift ("Highlight shift", Range(-1, 1)) = 0.1
        _HighlightColor ("Highlight colour", Color) = (0.55, 0.47, 0.40, 1)
        _CombDir ("Comb direction (object space)", Vector) = (0, 0, -1, 0)
        _RootOcclusion ("Root occlusion", Range(0, 1)) = 0.55
        _Density ("Distant density", Range(0.5, 6)) = 3
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" }
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_StrandTex); SAMPLER(sampler_StrandTex);
        TEXTURE2D(_MaskTex); SAMPLER(sampler_MaskTex);
        CBUFFER_START(UnityPerMaterial)
            float4 _StrandTex_ST;
            float4 _StrandTex_TexelSize;
            float4 _MaskTex_ST;
            half4 _RootColor;
            half4 _TipColor;
            half _Taper;
            half _Smoothness;
            half _SpecShift;
            half4 _HighlightColor;
            float4 _CombDir;
            half _RootOcclusion;
            half _Density;
        CBUFFER_END

        float Hash21(float2 p)
        {
            p = frac(p * float2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return frac(p.x * p.y);
        }

        half ShellAlive(float2 uv0, float2 uv1, half layer, out half4 s)
        {
            s = SAMPLE_TEXTURE2D(_StrandTex, sampler_StrandTex, uv0);
            half m = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, uv1).r;
            float2 tp = uv0 * _StrandTex_TexelSize.zw;
            float2 dx = ddx(tp), dy = ddy(tp);
            float lod = 0.5 * log2(max(max(dot(dx, dx), dot(dy, dy)), 1e-8));
            if (lod < 1.5)          // strands resolved: the strand itself, tapering with height
                return step(layer, s.r * m) * step(s.b, 1.0h - _Taper * layer) * step(0.5h, s.a);
            half cov = s.a;
            half len = s.r / max(cov, 1e-3h);
            half tall = saturate((len * m - layer) * 4.0h + 0.5h);
            half w = 1.0h - _Taper * layer;
            half p = saturate(cov * tall * w * w * _Density) * step(1e-3h, m);
            // the kept fraction is drawn as short strokes along the strand UVs' v (down the face, the comb's way):
            // cells one mip texel wide and four long, each column's rows staggered, a fresh pattern per layer
            float cell = exp2(floor(lod));
            float col = floor(tp.x / cell);
            float row = floor(tp.y / (cell * 4.0) + Hash21(float2(col, layer * 13.0)));
            float n = Hash21(float2(col, row) + layer * float2(91.7, 37.3));
            return step(n, p);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
                half4 color : COLOR;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv0 : TEXCOORD2;
                float2 uv1 : TEXCOORD3;
                half layer : TEXCOORD4;
                float3 combWS : TEXCOORD5;
                half fog : TEXCOORD6;
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(i.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.uv0 = i.uv0 * _StrandTex_ST.xy + _StrandTex_ST.zw;
                o.uv1 = i.uv1;
                o.layer = i.color.a;
                float3 c = TransformObjectToWorldDir(_CombDir.xyz);
                o.combWS = c - o.normalWS * dot(c, o.normalWS);
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half3 HairLight(Light l, float3 n, float3 t, float3 v, half3 albedo)
            {
                half wrap = saturate(dot(n, l.direction) * 0.7h + 0.3h);
                float3 h = normalize(l.direction + v);
                float th = dot(t, h);
                float sinTH = sqrt(saturate(1.0 - th * th));
                float spec = pow(sinTH, lerp(8.0, 240.0, _Smoothness)) * saturate(dot(n, l.direction) + 0.25);
                half att = l.distanceAttenuation * l.shadowAttenuation;
                return (albedo * wrap + _HighlightColor.rgb * spec * 0.3h) * l.color * att;
            }

            half4 frag(Varyings i, bool front : SV_IsFrontFace) : SV_Target
            {
                half4 s;
                clip(ShellAlive(i.uv0, i.uv1, i.layer, s) - 0.5h);
                float3 n = normalize(i.normalWS);
                if (!front) n = -n;
                float3 t = normalize(i.combWS + n * _SpecShift + 1e-5);
                float3 v = normalize(GetWorldSpaceViewDir(i.positionWS));
                half tint = s.a > 0.5h ? s.g : saturate(s.g / max(s.a, 1e-3h));     // the mip's tint is diluted by the gaps
                half3 albedo = lerp(_RootColor.rgb, _TipColor.rgb, i.layer) * lerp(0.65h, 1.35h, tint);
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                half3 col = HairLight(mainLight, n, t, v, albedo) + albedo * SampleSH(n);
                #if defined(_ADDITIONAL_LIGHTS)
                uint count = GetAdditionalLightsCount();
                for (uint li = 0u; li < count; li++)
                {
                    Light l = GetAdditionalLight(li, i.positionWS);
                    col += HairLight(l, n, t, v, albedo);
                }
                #endif
                col *= lerp(1.0h - _RootOcclusion, 1.0h, i.layer);
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
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv0 : TEXCOORD0; float2 uv1 : TEXCOORD1; half4 color : COLOR; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv0 : TEXCOORD0; float2 uv1 : TEXCOORD1; half layer : TEXCOORD2; };
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
                o.positionCS = cs;
                o.uv0 = i.uv0 * _StrandTex_ST.xy + _StrandTex_ST.zw; o.uv1 = i.uv1; o.layer = i.color.a;
                return o;
            }
            half4 frag(Varyings i) : SV_Target
            {
                half4 s;
                clip(ShellAlive(i.uv0, i.uv1, i.layer, s) - 0.5h);
                return 0;
            }
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
            struct Attributes { float4 positionOS : POSITION; float2 uv0 : TEXCOORD0; float2 uv1 : TEXCOORD1; half4 color : COLOR; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv0 : TEXCOORD0; float2 uv1 : TEXCOORD1; half layer : TEXCOORD2; };
            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.uv0 = i.uv0 * _StrandTex_ST.xy + _StrandTex_ST.zw; o.uv1 = i.uv1; o.layer = i.color.a;
                return o;
            }
            half4 frag(Varyings i) : SV_Target
            {
                half4 s;
                clip(ShellAlive(i.uv0, i.uv1, i.layer, s) - 0.5h);
                return i.positionCS.z;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
