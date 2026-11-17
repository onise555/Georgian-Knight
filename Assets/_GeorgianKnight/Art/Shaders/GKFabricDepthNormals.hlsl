#ifndef GK_FABRIC_DEPTH_NORMALS_INCLUDED
#define GK_FABRIC_DEPTH_NORMALS_INCLUDED

// GK/Fabric depth-normals (SSAO and screen-space effects): the shading normal with the weave, a back face turned to
// the viewer like the forward pass.

#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

struct FabricDNAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float4 tangentOS : TANGENT;
    float2 texcoord : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct FabricDNVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv : TEXCOORD0;
    half3 normalWS : TEXCOORD1;
    half4 tangentWS : TEXCOORD2;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

FabricDNVaryings FabricDepthNormalsVertex(FabricDNAttributes input)
{
    FabricDNVaryings output = (FabricDNVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    output.uv = input.texcoord;      // UV0, like the forward pass
    VertexNormalInputs nrm = GetVertexNormalInputs(input.normalOS, input.tangentOS);
    output.normalWS = nrm.normalWS;
    output.tangentWS = half4(nrm.tangentWS, input.tangentOS.w * GetOddNegativeScale());
    return output;
}

void FabricDepthNormalsFragment(
    FabricDNVaryings input
    , FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC
    , out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out float4 outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
#if defined(_ALPHATEST_ON)
    Alpha(SampleAlbedoAlpha(TRANSFORM_TEX(input.uv, _BaseMap), TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
#endif
#if defined(LOD_FADE_CROSSFADE)
    LODFadeCrossFade(input.positionCS);
#endif
    half faceSign = IS_FRONT_VFACE(face, 1.0h, -1.0h);
    half3 n = normalize(input.normalWS) * faceSign;
    half3 t = normalize(input.tangentWS.xyz);
    half3 bt = cross(n, t) * (input.tangentWS.w * faceSign);
    float3 normalWS = normalize(mul(FabricNormalTS(input.uv), half3x3(t, bt, n)));
#if defined(_GBUFFER_NORMALS_OCT)
    float2 oct = PackNormalOctQuadEncode(normalWS);
    outNormalWS = half4(PackFloat2To888(saturate(oct * 0.5 + 0.5)), 0.0);
#else
    outNormalWS = half4(NormalizeNormalPerPixel(normalWS), 0.0);
#endif
#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = float4(EncodeMeshRenderingLayer(GetMeshRenderingLayer()), 0, 0, 0);
#endif
}

#endif
