// Cloth for every garment (Director 2026-10-31: "cloth, not plastic"). URP Lit has no fabric model, so wool and silk
// read as plastic. This one returns what cloth returns: a coloured sheen from the fuzz that grows toward grazing angles
// (silhouettes and folds), light wrapping through the folds, a thread-scale weave normal tiled on its own, a weak
// rough fibre specular stretched along the thread (strong and metallic on the gold cord), and Art's wear and dust
// masks. Two-sided, SRP Batcher compatible (one material buffer for every pass), Forward+, shadows, SSAO, LOD fade.
Shader "GeorgianKnight/Fabric"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo (sRGB)", 2D) = "white" {}
        [MainColor] _BaseColor("Colour", Color) = (1, 1, 1, 1)
        [Toggle(_ALPHATEST_ON)] _AlphaClip("Alpha clip", Float) = 0
        _Cutoff("Alpha cut-off", Range(0, 1)) = 0.5
        [Normal] _BumpMap("Normal (folds, seams)", 2D) = "bump" {}
        _BumpScale("Normal strength", Float) = 1
        [ToggleUI] _BumpTiled("Normal tiles with the albedo (cords; off = UV0 folds)", Float) = 0

        [Header(Masks)]
        [NoScaleOffset] _MaskMap("Masks on UV0: R metal (gold), G AO, B wear, A dust (linear)", 2D) = "black" {}
        _OcclusionStrength("AO strength", Range(0, 1)) = 1

        [Header(Weave)]
        [Normal] _DetailNormalMap("Weave normal (tileable)", 2D) = "bump" {}
        _DetailNormalScale("Weave strength", Range(0, 2)) = 0.6

        [Header(Sheen)]
        _SheenColor("Sheen colour", Color) = (0.68, 0.67, 0.67, 1)
        _SheenStrength("Sheen strength", Range(0, 2)) = 1
        _SheenRoughness("Sheen roughness (lower = tighter halo)", Range(0.07, 1)) = 0.42
        _Wrap("Wrap (light through the folds)", Range(0, 1)) = 0.3

        [Header(Fibre and thread specular)]
        _Roughness("Fibre roughness", Range(0.05, 1)) = 0.9
        _ClothF0("Fibre reflectance F0 (wool 0.02)", Range(0, 0.08)) = 0.02
        _ClothSpecular("Fibre specular strength", Range(0, 1)) = 1
        _ClothAnisotropy("Silk anisotropy along the thread (-1..1)", Range(-1, 1)) = 0
        _Anisotropy("Metal thread anisotropy (-1..1)", Range(-1, 1)) = 0.65
        [NoScaleOffset] _FlowMap("Flow on UV0: RG = 0.5 + 0.5 thread direction, B anisotropy mask (linear)", 2D) = "white" {}
        _MetalTint("Metal thread tint", Color) = (1, 1, 1, 1)
        _MetalRoughness("Metal thread roughness", Range(0.05, 1)) = 0.35

        [Header(Wear and dust)]
        _WearAmount("Wear", Range(0, 1)) = 1
        _DustColor("Dust colour", Color) = (0.48, 0.45, 0.4, 1)
        _DustAmount("Dust", Range(0, 1)) = 1

        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull (Off = two-sided)", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "UniversalMaterialType" = "Lit" "IgnoreProjector" = "True" }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull[_Cull]
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex FabricVertex
            #pragma fragment FabricFragment

            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local_fragment _DETAIL_NORMAL
            #pragma shader_feature_local_fragment _MASKMAP
            #pragma shader_feature_local_fragment _FLOWMAP
            #pragma shader_feature_local_fragment _ALPHATEST_ON

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _FORWARD_PLUS
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer

            #include "GKFabricInput.hlsl"
            #include "GKFabricForward.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "GKFabricInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_instancing

            #include "GKFabricInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex FabricDepthNormalsVertex
            #pragma fragment FabricDepthNormalsFragment
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _DETAIL_NORMAL
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_instancing

            #include "GKFabricInput.hlsl"
            #include "GKFabricDepthNormals.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
    CustomEditor "GeorgianKnight.EditorTools.GKFabricGUI"
}
