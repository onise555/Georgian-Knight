#ifndef GK_FABRIC_FORWARD_INCLUDED
#define GK_FABRIC_FORWARD_INCLUDED

// GK/Fabric forward pass. Cloth is not plastic: the light it returns is mostly the fuzz of fibres standing off the weave
// (a sheen lobe that brightens toward grazing angles, Charlie distribution, Estevez & Kulla 2017, with the Ashikhmin /
// Neubelt visibility), light wrapping round the folds through the cloth (wrap diffuse), and only a weak, rough specular
// from the fibres themselves, stretched along the thread (anisotropic GGX). Metal thread (the gold cord) is a full
// metallic anisotropic lobe. Main light, every additional light (Forward+ clusters included), SH ambient, reflection
// probes, SSAO, fog and LOD cross-fade like URP Lit; two-sided with the back face lit from the viewer's side.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

struct FabricAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float4 tangentOS : TANGENT;
    float2 texcoord : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct FabricVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv : TEXCOORD0;
    float3 positionWS : TEXCOORD1;
    half3 normalWS : TEXCOORD2;
    half4 tangentWS : TEXCOORD3;     // w = bitangent sign
    half fogFactor : TEXCOORD4;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

FabricVaryings FabricVertex(FabricAttributes input)
{
    FabricVaryings output = (FabricVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
    VertexNormalInputs nrm = GetVertexNormalInputs(input.normalOS, input.tangentOS);
    output.positionCS = pos.positionCS;
    output.positionWS = pos.positionWS;
    output.uv = input.texcoord;      // UV0; the albedo tiling is applied per map in SampleFabric
    output.normalWS = nrm.normalWS;
    output.tangentWS = half4(nrm.tangentWS, input.tangentOS.w * GetOddNegativeScale());
    output.fogFactor = ComputeFogFactor(pos.positionCS.z);
    return output;
}

// ---------------------------------------------------------------------------------------------------------- the BRDF

struct FabricBRDF
{
    half3 diffuse;          // albedo of the cloth body (zero where metal)
    half3 f0;               // specular colour at normal incidence
    half specWeight;        // how much of the fibre / metal specular there is
    half3 sheen;            // sheen colour x strength
    half sheenRough;        // Charlie roughness (alpha)
    half ax, ay;            // anisotropic GGX alphas along the thread and across it
    half3 T, B;             // thread direction and its cross direction in world space
    half wrap;
};

// Charlie sheen distribution: sin(theta_h)^(1/alpha), normalised.
half CharlieD(half alpha, half NoH)
{
    half invA = 1.0h / alpha;
    half sin2 = max(1.0h - NoH * NoH, 0.0078125h);
    return (2.0h + invA) * pow(sin2, invA * 0.5h) / (2.0h * PI);
}

// Ashikhmin / Neubelt & Pettineo visibility, the cloth-friendly one: no shadowing of the fuzz.
half SheenV(half NoL, half NoV)
{
    return 1.0h / (4.0h * (NoL + NoV - NoL * NoV) + 1e-4h);
}

// Anisotropic GGX (Burley) with the height-correlated Smith visibility (Heitz).
half AnisoSpecular(FabricBRDF b, half3 N, half3 V, half3 L, half3 H, half NoL, half NoV)
{
    half ToH = dot(b.T, H), BoH = dot(b.B, H), NoH = saturate(dot(N, H));
    half a2 = b.ax * b.ay;
    half3 v = half3(b.ay * ToH, b.ax * BoH, a2 * NoH);
    half w2 = a2 / max(dot(v, v), 1e-5h);
    half D = a2 * w2 * w2 * INV_PI;
    half ToV = dot(b.T, V), BoV = dot(b.B, V), ToL = dot(b.T, L), BoL = dot(b.B, L);
    half lv = NoL * length(half3(b.ax * ToV, b.ay * BoV, NoV));
    half ll = NoV * length(half3(b.ax * ToL, b.ay * BoL, NoL));
    half Vis = 0.5h / max(lv + ll, 1e-4h);
    return D * Vis;
}

half3 FabricLight(FabricBRDF b, half3 N, half3 V, Light light, half bodyScale)
{
    half3 L = light.direction;
    half NoLraw = dot(N, L);
    half NoL = saturate(NoLraw);
    half NoV = saturate(abs(dot(N, V)) + 1e-4h);
    half3 radiance = light.color * (light.distanceAttenuation * light.shadowAttenuation);

    // wrap: light reaches past the terminator through the cloth, normalised so the total stays the same
    half wrapped = saturate((NoLraw + b.wrap) / ((1.0h + b.wrap) * (1.0h + b.wrap)));
    half3 color = b.diffuse * (wrapped * bodyScale);

    if (NoL > 0.0h)
    {
        half3 H = SafeNormalize(L + V);
        half NoH = saturate(dot(N, H));
        half LoH = saturate(dot(L, H));
        // URP leaves the diffuse un-divided by pi, so the lobes carry a pi here to match Lit's balance
        // the fuzz
        color += b.sheen * (PI * CharlieD(b.sheenRough, NoH) * SheenV(NoL, NoV) * NoL);
        // the fibres / the metal thread
        half3 F = b.f0 + (1.0h - b.f0) * pow(1.0h - LoH, 5.0h);
        color += F * (PI * AnisoSpecular(b, N, V, L, H, NoL, NoV) * NoL * b.specWeight);
    }
    return color * radiance;
}

// How much light the fuzz takes for itself at this view angle (a fit of the Charlie sheen's directional albedo: small
// face-on, rising toward grazing, higher for a softer sheen). The body underneath gets the rest, so adding sheen does
// not add light that was never there (the first A/B washed the coat grey-blue).
half SheenAlbedo(half rough, half NoV)
{
    half g = 1.0h - NoV;
    return saturate(lerp(0.08h, 0.2h, rough) + lerp(0.25h, 0.45h, rough) * g * g);
}

// Analytic split-sum environment BRDF (Karis), for the probe reflection.
half3 EnvBRDFApprox(half3 f0, half roughness, half NoV)
{
    const half4 c0 = half4(-1.0h, -0.0275h, -0.572h, 0.022h);
    const half4 c1 = half4(1.0h, 0.0425h, 1.04h, -0.04h);
    half4 r = roughness * c0 + c1;
    half a004 = min(r.x * r.x, exp2(-9.28h * NoV)) * r.x + r.y;
    half2 AB = half2(-1.04h, 1.04h) * a004 + r.zw;
    return f0 * AB.x + AB.y;
}

// ---------------------------------------------------------------------------------------------------------- fragment

void FabricFragment(
    FabricVaryings input
    , FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC
    , out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out float4 outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    FabricSurface s = SampleFabric(input.uv);
#ifdef LOD_FADE_CROSSFADE
    LODFadeCrossFade(input.positionCS);
#endif

    // tangent frame; a back face (the lining at an open hem) is lit from the viewer's side
    half faceSign = IS_FRONT_VFACE(face, 1.0h, -1.0h);
    half3 nGeo = normalize(input.normalWS) * faceSign;
    half3 tGeo = normalize(input.tangentWS.xyz);
    half3 bGeo = cross(nGeo, tGeo) * (input.tangentWS.w * faceSign);
    half3x3 tbn = half3x3(tGeo, bGeo, nGeo);
    half3 N = normalize(mul(s.normalTS, tbn));
    half3 V = GetWorldSpaceNormalizeViewDir(input.positionWS);

    // the thread direction, kept square to the shading normal
    half3 T = normalize(mul(half3(s.threadTS, 0.0h), tbn));
    T = normalize(T - N * dot(T, N));
    half3 B = cross(N, T);

    FabricBRDF b;
    b.diffuse = s.albedo * (1.0h - s.metal);
    b.f0 = lerp(half3(_ClothF0, _ClothF0, _ClothF0), s.albedo, s.metal);
    b.specWeight = lerp(_ClothSpecular, 1.0h, s.metal) * (1.0h - s.dust * 0.8h);
    half sheenAmount = _SheenStrength * (1.0h - s.metal) * (1.0h - s.wear * 0.7h) * (1.0h + s.dust * 0.3h);
    b.sheen = _SheenColor.rgb * sheenAmount;
    b.sheenRough = clamp(_SheenRoughness, 0.07h, 1.0h);
    half rough = max(s.roughness, 0.05h);
    half alpha = max(rough * rough, 0.002h);
    half an = clamp(s.anisotropy, -0.95h, 0.95h);
    b.ax = max(alpha * (1.0h + an), 0.002h);
    b.ay = max(alpha * (1.0h - an), 0.002h);
    b.T = T; b.B = B;
    b.wrap = _Wrap;

    InputData inputData = (InputData)0;
    inputData.positionWS = input.positionWS;
    inputData.positionCS = input.positionCS;
    inputData.normalWS = N;
    inputData.viewDirectionWS = V;
    inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
    inputData.fogCoord = input.fogFactor;
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
    inputData.shadowMask = half4(1, 1, 1, 1);
    half4 shadowMask = CalculateShadowMask(inputData);
    AmbientOcclusionFactor ao = CreateAmbientOcclusionFactor(inputData.normalizedScreenSpaceUV, s.occlusion);
    uint meshRenderingLayers = GetMeshRenderingLayer();

    half NoV = saturate(dot(N, V));
    half sheenE = SheenAlbedo(b.sheenRough, NoV) * Max3(b.sheen.r, b.sheen.g, b.sheen.b);
    half bodyScale = 1.0h - saturate(sheenE);

    half3 color = 0;
    Light mainLight = GetMainLight(inputData, shadowMask, ao);
#ifdef _LIGHT_LAYERS
    if (IsMatchingLightLayer(mainLight.layerMask, meshRenderingLayers))
#endif
        color += FabricLight(b, N, V, mainLight, bodyScale);

#if defined(_ADDITIONAL_LIGHTS)
    uint pixelLightCount = GetAdditionalLightsCount();
    #if USE_FORWARD_PLUS
    [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
    {
        FORWARD_PLUS_SUBTRACTIVE_LIGHT_CHECK
        Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, ao);
    #ifdef _LIGHT_LAYERS
        if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
    #endif
            color += FabricLight(b, N, V, light, bodyScale);
    }
    #endif
    LIGHT_LOOP_BEGIN(pixelLightCount)
        Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, ao);
    #ifdef _LIGHT_LAYERS
        if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
    #endif
            color += FabricLight(b, N, V, light, bodyScale);
    LIGHT_LOOP_END
#endif

    // ambient: SH for the body and the fuzz (the fuzz glows toward grazing angles, so silhouettes and folds turned
    // away from the viewer keep a soft rim), the probe for the fibre / metal lobe, bent along the thread
    half3 sh = SampleSH(N);
    half indirectAO = ao.indirectAmbientOcclusion;
    color += sh * b.diffuse * (bodyScale * indirectAO);
    color += sh * b.sheen * (SheenAlbedo(b.sheenRough, NoV) * indirectAO);
    half3 anisoDir = an >= 0.0h ? B : T;
    half3 anisoN = normalize(lerp(N, cross(cross(anisoDir, V), anisoDir), abs(an) * 0.5h));
    half3 R = reflect(-V, anisoN);
    half3 env = GlossyEnvironmentReflection(R, input.positionWS, rough, indirectAO, inputData.normalizedScreenSpaceUV);
    color += env * EnvBRDFApprox(b.f0, rough, NoV) * b.specWeight;

    color = MixFog(color, inputData.fogCoord);
    outColor = half4(color, s.alpha);

#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = float4(EncodeMeshRenderingLayer(meshRenderingLayers), 0, 0, 0);
#endif
}

#endif
