// Blood film on a sword (QUALITY 3b, blood on the blade). Drawn by Feel's BladeBloodFilm render pass over the sword's
// own renderer, so the steel material is untouched. Blade space comes from uniforms (no UVs needed): u along
// base -> tip, v across the width, and which flat. The film texture (128 x 32, R = thickness, G = age) is written by
// the CPU flow simulation. Thickness lifts a real shell off the steel (readable edge-on), its gradient gives the
// relief; wet blood is dark red and glossy with a sharp highlight, dry blood matte brown-black, drying in from the
// thin edges first.
Shader "GK/Feel/BladeBlood"
{
    Properties
    {
        _Film ("Film (R thickness, G age)", 2D) = "black" {}
        _WetColor ("Wet colour", Color) = (0.23, 0.012, 0.009, 1)
        _DryColor ("Dry colour", Color) = (0.085, 0.035, 0.025, 1)
        _RimColor ("Dry rim colour (thin edges)", Color) = (0.13, 0.06, 0.035, 1)
        _MaxShell ("Shell at full thickness (m)", Float) = 0.0012
        _Relief ("Relief", Range(0, 4)) = 1.6
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-10" "RenderType" = "Transparent" }
        Pass
        {
            Name "Film"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_Film); SAMPLER(sampler_Film);
            CBUFFER_START(UnityPerMaterial)
                float4 _Film_TexelSize;
                half4 _WetColor, _DryColor, _RimColor;
                float _MaxShell, _Relief;
                float4 _BladeBase;      // xyz world
                float4 _BladeAxis;      // xyz world unit, w = length
                float4 _BladeSide;      // xyz world unit (width), w = half width
                float4 _BladeFlat;      // xyz world unit (flat A normal)
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 film : TEXCOORD2;     // film texture uv
                float span : TEXCOORD3;      // along the blade, unclamped (the guard and the grip are outside 0..1)
            };

            float Span(float3 wp) { return dot(wp - _BladeBase.xyz, _BladeAxis.xyz) / max(_BladeAxis.w, 1e-3); }

            float2 FilmUV(float3 wp, float3 nw)
            {
                float3 d = wp - _BladeBase.xyz;
                float u = saturate(Span(wp));
                float v = saturate(dot(d, _BladeSide.xyz) / max(_BladeSide.w, 1e-4) * 0.5 + 0.5);
                float face = dot(nw, _BladeFlat.xyz) >= 0 ? 0.0 : 0.5;
                return float2(u, face + v * 0.5);
            }

            Varyings vert(Attributes i)
            {
                Varyings o;
                float3 wp = TransformObjectToWorld(i.positionOS.xyz);
                float3 nw = normalize(TransformObjectToWorldNormal(i.normalOS));
                float2 uv = FilmUV(wp, nw);
                float t = SAMPLE_TEXTURE2D_LOD(_Film, sampler_Film, uv, 0).r;
                // the film stands off the steel by its own thickness: an edge-on blade still shows it
                wp += nw * (t * _MaxShell + 0.00005);
                o.positionWS = wp;
                o.normalWS = nw;
                o.film = uv;
                o.span = Span(TransformObjectToWorld(i.positionOS.xyz));
                o.positionCS = TransformWorldToHClip(wp);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half2 s = SAMPLE_TEXTURE2D(_Film, sampler_Film, i.film).rg;
                half t = s.r;                       // 0..1 = 0..2 mm
                half age = s.g;                     // 0 wet .. 1 dry
                // a ragged edge: the cut-off wanders with a fine hash in film space (0.3 mm across, 3 mm along)
                float2 cell = floor(i.film * float2(256.0, 128.0));
                half rag = (half)frac(sin(dot(cell, float2(12.9898, 78.233))) * 43758.5453);
                clip(t - 0.004h - 0.014h * rag);
                clip(i.span + 0.01);                // the guard, the grip and the pommel stay clean
                // relief from the film's gradient, bent into the surface normal
                float2 px = _Film_TexelSize.xy;
                half tx = SAMPLE_TEXTURE2D(_Film, sampler_Film, i.film + float2(px.x, 0)).r - SAMPLE_TEXTURE2D(_Film, sampler_Film, i.film - float2(px.x, 0)).r;
                half ty = SAMPLE_TEXTURE2D(_Film, sampler_Film, i.film + float2(0, px.y)).r - SAMPLE_TEXTURE2D(_Film, sampler_Film, i.film - float2(0, px.y)).r;
                float3 n = normalize(i.normalWS - (_BladeAxis.xyz * tx + _BladeSide.xyz * ty) * _Relief);
                // dries from the thin edges in: a thin cell is older than a thick one
                half dry = saturate(age * (1.6h - 0.8h * saturate(t * 2.0h)));
                half3 albedo = lerp(_WetColor.rgb, _DryColor.rgb, dry);
                // a thin edge dries to a lighter rust rim, the thick middle to a dark crust (reference 2026-10-30)
                albedo = lerp(albedo, _RimColor.rgb, dry * saturate(1.0h - t * 10.0h));
                half smooth = lerp(0.93h, 0.28h, dry);
                Light L = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                float3 v = normalize(GetWorldSpaceViewDir(i.positionWS));
                float3 h = normalize(L.direction + v);
                half ndl = saturate(dot(n, L.direction));
                half spec = pow(saturate(dot(n, h)), lerp(16.0h, 900.0h, smooth)) * smooth * smooth * 4.0h;
                half3 light = L.color * L.shadowAttenuation;
                half3 col = albedo * (light * ndl + SampleSH(n)) + light * spec * (ndl > 0 ? 1 : 0);
                // blood absorbs hard: ~80% at 0.1 mm (t = 0.05), opaque by 0.2 mm; a run's thin trail lets the steel through
                half a = (1.0h - exp(-t * 30.0h)) * lerp(0.94h, 1.0h, dry);
                return half4(col, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
