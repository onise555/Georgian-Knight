using UnityEngine;

namespace GeorgianKnight.World
{
    /// <summary>
    /// Publishes the arena's baked sky-visibility maps as shader globals (World, 2026-10-31; the Lead's decision: the terrain
    /// occludes the sky light). GK/Terrain Lit (Sky Occlusion) multiplies its GI occlusion by them; Feel's Fabric samples the
    /// inner one, so a character in a fold darkens with the ground. _GK_SkyVis / _GK_SkyVis_ST: the inner terrain;
    /// _GK_SkyVisFar / _GK_SkyVisFar_ST: the far ranges. ST: xy = 1 / size, zw = -origin / size (world XZ to UV). Cleared on
    /// disable (ST zero: no occlusion), so a scene without this component renders as plain TerrainLit.
    /// </summary>
    [ExecuteAlways]
    public class SkyVisibility : MonoBehaviour
    {
        public Texture2D Inner;
        public Vector4 InnerST;
        public Texture2D Far;
        public Vector4 FarST;

        static readonly int SkyVis = Shader.PropertyToID("_GK_SkyVis"), SkyVisST = Shader.PropertyToID("_GK_SkyVis_ST");
        static readonly int SkyVisFar = Shader.PropertyToID("_GK_SkyVisFar"), SkyVisFarST = Shader.PropertyToID("_GK_SkyVisFar_ST");

        void OnEnable() { Apply(); }
        void OnValidate() { if (isActiveAndEnabled) Apply(); }

        void OnDisable()
        {
            Shader.SetGlobalVector(SkyVisST, Vector4.zero);
            Shader.SetGlobalVector(SkyVisFarST, Vector4.zero);
        }

        public void Apply()
        {
            if (Inner != null) { Shader.SetGlobalTexture(SkyVis, Inner); Shader.SetGlobalVector(SkyVisST, InnerST); }
            else Shader.SetGlobalVector(SkyVisST, Vector4.zero);
            if (Far != null) { Shader.SetGlobalTexture(SkyVisFar, Far); Shader.SetGlobalVector(SkyVisFarST, FarST); }
            else Shader.SetGlobalVector(SkyVisFarST, Vector4.zero);
        }
    }
}
