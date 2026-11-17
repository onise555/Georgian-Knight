// Blade swing ribbon for WeaponTrail meshes (uv.x = age 0..1 from the blade, uv.y = across, 1 = the tip's path;
// vertex colour = tint and fade). A hot, thin edge along the tip's path, a soft streaked body behind it that thins
// with age, all HDR so bloom catches the edge. Procedural: no texture to import or lose.
Shader "GK/Feel/BladeTrail"
{
    Properties
    {
        _Intensity ("Intensity", Range(0, 4)) = 0.55
        _EdgeWidth ("Edge width", Range(0.01, 0.4)) = 0.08
        _EdgeBoost ("Edge boost", Range(0, 4)) = 0.5
        _Body ("Body", Range(0, 1)) = 0.25
        _Streaks ("Streak count", Range(4, 64)) = 26
        _StreakDepth ("Streak depth", Range(0, 1)) = 0.5
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent+100" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Trail"
            Blend SrcAlpha One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half _Intensity, _EdgeWidth, _EdgeBoost, _Body, _Streaks, _StreakDepth;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; half4 color : COLOR; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; half4 color : COLOR; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.color = i.color;
                o.uv = i.uv;
                return o;
            }

            // cheap 1D value noise for the streaks
            half hash(half n) { return frac(sin(n) * 43758.5453h); }
            half noise1(half x) { half i = floor(x); half f = frac(x); f = f * f * (3.0h - 2.0h * f); return lerp(hash(i), hash(i + 1.0h), f); }

            half4 frag(Varyings i) : SV_Target
            {
                half age = saturate(i.uv.x), across = saturate(i.uv.y);
                // the edge: a gaussian on the tip's path that narrows as it ages
                half w = _EdgeWidth * (1.0h - 0.6h * age);
                half d = (1.0h - across) / max(w, 1e-3h);
                half edge = exp(-d * d) * pow(1.0h - age, 0.8h);
                // the body: rises toward the tip, streaked along the motion, gone sooner than the edge
                half streak = lerp(1.0h, noise1(across * _Streaks + age * 3.0h), _StreakDepth);
                half body = pow(across, 2.0h) * pow(1.0h - age, 1.6h) * _Body * streak;
                half a = saturate(edge + body) * i.color.a;
                half3 rgb = i.color.rgb * _Intensity * (1.0h + edge * _EdgeBoost);
                return half4(rgb, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
