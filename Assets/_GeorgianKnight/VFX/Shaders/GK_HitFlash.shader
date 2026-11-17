// Hit flash over a struck body: the mesh drawn again, additive, bright at the silhouette (fresnel) with a faint
// fill across the face of it. Drawn by GK.Feel's FeelFlash render pass; depth-tested against the body itself.
Shader "GK/Feel/HitFlash"
{
    Properties
    {
        [HDR] _Color ("Color", Color) = (1.6, 1.45, 1.3, 1)
        _Intensity ("Intensity", Float) = 0
        _RimPower ("Rim power", Range(0.5, 8)) = 2.2
        _Fill ("Fill", Range(0, 1)) = 0.28
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Flash"
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Back
            // the body already wrote this depth: pull the second draw a hair forward so it never z-fights
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Intensity;
                half _RimPower;
                half _Fill;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewWS : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs p = GetVertexPositionInputs(i.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.viewWS = GetWorldSpaceViewDir(p.positionWS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half ndv = saturate(dot(normalize(i.normalWS), normalize(i.viewWS)));
                half rim = pow(1.0h - ndv, _RimPower);
                return half4(_Color.rgb * _Intensity * (_Fill + rim), 0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
