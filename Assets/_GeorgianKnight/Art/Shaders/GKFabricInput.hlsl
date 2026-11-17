#ifndef GK_FABRIC_INPUT_INCLUDED
#define GK_FABRIC_INPUT_INCLUDED

// GK/Fabric inputs. One UnityPerMaterial buffer shared by every pass (SRP Batcher). _BaseMap / _BaseColor / _Cutoff keep
// URP's names so URP's own ShadowCaster and DepthOnly passes can be used as they are.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"   // _BaseMap, _BumpMap, Alpha()

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    half4 _BaseColor;
    half _Cutoff;
    half _BumpScale;
    half _BumpTiled;            // 0: the normal on UV0 (baked folds); 1: it tiles with the albedo (Art's cord tile)
    // cloth
    half4 _SheenColor;          // rgb tint of the fuzz, a unused
    half _SheenStrength;
    half _SheenRoughness;       // 0.3 tight satin sheen .. 1 soft wool fuzz
    half _Wrap;                 // 0 hard Lambert .. 0.6 light wrapping round the folds
    half _Roughness;            // the fibre lobe's roughness without a surface map (cloth fibres, not the fuzz)
    half _ClothF0;              // fibre reflectance at normal incidence (wool ~0.02: never glossy)
    half _ClothSpecular;        // strength of the fibre lobe on non-metal cloth
    half _ClothAnisotropy;      // -1..1 along / across the thread on silk; wool ~0
    half _Anisotropy;           // -1..1 on the metal thread (gold cord ~0.65)
    // metal thread (gold couched cord)
    half4 _MetalTint;           // multiplies the albedo where the metal mask is 1
    half _MetalRoughness;
    // thread-scale detail
    float4 _DetailNormalMap_ST; // tiling and offset of the weave, independent of the base UVs
    half _DetailNormalScale;
    // wear and dust (Art's bake)
    half _WearAmount;           // flattened, rubbed nap: less sheen, a little lighter
    half4 _DustColor;
    half _DustAmount;
    half _OcclusionStrength;
CBUFFER_END

// Art's bake (2026-10-31): albedo, folds normal, masks and flow on UV0 (the albedo may still tile with _BaseMap_ST);
// the weave tiles with its own _DetailNormalMap_ST at true thread scale.
TEXTURE2D(_MaskMap);            SAMPLER(sampler_MaskMap);            // R metal (gold), G AO, B wear, A dust (linear)
TEXTURE2D(_DetailNormalMap);    SAMPLER(sampler_DetailNormalMap);    // tileable thread-scale normal
TEXTURE2D(_FlowMap);            SAMPLER(sampler_FlowMap);            // RG = 0.5 + 0.5 thread dir (tangent), B anisotropy mask

struct FabricSurface
{
    half3 albedo;
    half alpha;
    half3 normalTS;
    half metal;
    half roughness;     // perceptual
    half occlusion;
    half wear;
    half dust;
    half2 threadTS;     // the thread direction in tangent space (unit)
    half anisotropy;    // -1..1
};

// The folds normal (UV0) with the weave on top (Reoriented Normal Mapping keeps the folds' slope under the threads).
half3 FabricNormalTS(float2 uv0)
{
    half3 n = half3(0, 0, 1);
#ifdef _NORMALMAP
    float2 nuv = _BumpTiled > 0.5 ? uv0 * _BaseMap_ST.xy + _BaseMap_ST.zw : uv0;
    n = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, nuv), _BumpScale);
#endif
#ifdef _DETAIL_NORMAL
    float2 duv = uv0 * _DetailNormalMap_ST.xy + _DetailNormalMap_ST.zw;
    half3 d = UnpackNormalScale(SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap, duv), _DetailNormalScale);
    half3 t = n + half3(0, 0, 1);
    half3 u = d * half3(-1, -1, 1);
    n = normalize(t * dot(t, u) - u * t.z);
#endif
    return n;
}

FabricSurface SampleFabric(float2 uv0)
{
    FabricSurface s;
    float2 uv = uv0 * _BaseMap_ST.xy + _BaseMap_ST.zw;
    half4 albedoAlpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
    s.alpha = Alpha(albedoAlpha.a, _BaseColor, _Cutoff);
    s.albedo = albedoAlpha.rgb * _BaseColor.rgb;
    s.normalTS = FabricNormalTS(uv0);

    half4 m = half4(0, 1, 0, 0);
#ifdef _MASKMAP
    m = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, uv0);
#endif
    s.metal = m.r;
    s.occlusion = LerpWhiteTo(m.g, _OcclusionStrength);
    s.wear = m.b * _WearAmount;
    s.dust = m.a * _DustAmount;
    s.roughness = lerp(_Roughness, _MetalRoughness, s.metal);

    // the thread runs along U (a cord's UVs) unless the flow map says otherwise; its B masks the anisotropy
    s.threadTS = half2(1, 0);
    half anisoMask = 1.0h;
#ifdef _FLOWMAP
    half3 f = SAMPLE_TEXTURE2D(_FlowMap, sampler_FlowMap, uv0).rgb;
    half2 dir = f.rg * 2.0h - 1.0h;
    s.threadTS = dot(dir, dir) > 1e-4h ? normalize(dir) : half2(1, 0);
    anisoMask = f.b;
#endif
    s.anisotropy = lerp(_ClothAnisotropy, _Anisotropy, s.metal) * anisoMask;

    // metal thread takes its colour from the albedo; worn nap lifts a little; dust settles over everything
    s.albedo = lerp(s.albedo, s.albedo * _MetalTint.rgb, s.metal);
    s.albedo = lerp(s.albedo, s.albedo * 1.12h + 0.015h, s.wear);
    s.albedo = lerp(s.albedo, _DustColor.rgb, s.dust);
    return s;
}

#endif
