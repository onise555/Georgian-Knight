// Unity's Skybox/Panoramic (latitude-longitude, rotation, exposure, tint) with one addition: a gain that rises with the
// angle away from the sun, like a graduated filter. A dusk/dawn HDRI is many times brighter on the sun side than opposite
// it; one exposure either burns the sun side white (no sun disk) or leaves the anti-sun sky night-dark over sunlit ground
// (Critics AR-3, Didgori arena 2026-10-31). _AwayGain lifts only the far side; the sun side stays as photographed.
Shader "GK/SkyPanoramicGraded"
{
    Properties
    {
        _Tint ("Tint Color", Color) = (.5, .5, .5, .5)
        [Gamma] _Exposure ("Exposure", Range(0, 8)) = 1.0
        _Rotation ("Rotation", Range(0, 360)) = 0
        [NoScaleOffset] _MainTex ("Spherical (HDR)", 2D) = "grey" {}
        _SunDir ("Sun direction (world, normalized)", Vector) = (0, 0.2, 1, 0)
        _AwayGain ("Gain opposite the sun", Range(1, 4)) = 1
        _GainPower ("Gain falloff power", Range(0.25, 4)) = 1.5
        _AwayTint ("Tint opposite the sun", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            half4 _MainTex_HDR;
            half4 _Tint;
            half _Exposure;
            float _Rotation;
            float4 _SunDir;
            half _AwayGain;
            half _GainPower;
            half4 _AwayTint;

            float3 RotateAroundYInDegrees(float3 vertex, float degrees)
            {
                float alpha = degrees * UNITY_PI / 180.0;
                float sina, cosa;
                sincos(alpha, sina, cosa);
                float2x2 m = float2x2(cosa, -sina, sina, cosa);
                return float3(mul(m, vertex.xz), vertex.y).xzy;
            }

            inline float2 ToRadialCoords(float3 coords)
            {
                float3 n = normalize(coords);
                float latitude = acos(n.y);
                float longitude = atan2(n.z, n.x);
                float2 sphereCoords = float2(longitude, latitude) * float2(0.5 / UNITY_PI, 1.0 / UNITY_PI);
                return float2(0.5, 1.0) - sphereCoords;
            }

            struct appdata_t { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 texcoord : TEXCOORD0;   // the sky-space direction (the texture lookup)
                float3 worldDir : TEXCOORD1;   // the world direction (for the gain)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 rotated = RotateAroundYInDegrees(v.vertex.xyz, _Rotation);
                o.vertex = UnityObjectToClipPos(rotated);
                o.texcoord = v.vertex.xyz;
                o.worldDir = rotated;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 tc = ToRadialCoords(i.texcoord);
                if (tc.x > 1.0) return half4(0, 0, 0, 1);
                half4 tex = tex2D(_MainTex, tc);
                half3 c = DecodeHDR(tex, _MainTex_HDR);
                c = c * _Tint.rgb * unity_ColorSpaceDouble.rgb;
                c *= _Exposure;
                // 0 toward the sun, 1 opposite it
                float away = saturate(0.5 - 0.5 * dot(normalize(i.worldDir), normalize(_SunDir.xyz)));
                float w = pow(away, _GainPower);
                c *= lerp(half3(1, 1, 1), _AwayGain * _AwayTint.rgb, w);
                return half4(c, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
