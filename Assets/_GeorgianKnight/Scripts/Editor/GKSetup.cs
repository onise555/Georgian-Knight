using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GeorgianKnight.EditorTools
{
    public static class GKPaths
    {
        public const string Root = "Assets/_GeorgianKnight";
        public const string Characters = Root + "/Art/Characters";
        public const string Weapons = Root + "/Art/Weapons";
        public const string Environment = Root + "/Art/Environment";
        public const string Downloaded = Environment + "/Downloaded";
        public const string Materials = Root + "/Materials";
        public const string Mixamo = Root + "/Animations/Generated/Mixamo";
        public const string Generated = Root + "/Generated";
        public const string Prefabs = Root + "/Prefabs";
        public const string Scenes = Root + "/Scenes";
        public const string Data = Root + "/Data";
        public const string Audio = Root + "/Audio";

        public const string KnightFbx = Characters + "/Knight/Knight.fbx";
        public const string XBotFbx = Mixamo + "/X Bot.fbx";
        // "Medieval Arming Sword and Scabbard" by Pippa (Planetrix23), CC BY 4.0 (see Art/Weapons/CREDITS.txt)
        public const string SwordFbx = Weapons + "/ArmingSword/ArmingSword.fbx";
        public const string ScabbardFbx = Weapons + "/ArmingScabbard/ArmingScabbard.fbx";
        // a 70 cm round shield built for the sword-and-shield captures, which were taken with a small round shield
        // (Tools: scratchpad round_shield.py; CC0 Poly Haven wood and iron, see RoundShield/Textures/LICENSE.txt).
        // The kite shield ("Teardrop Shields" by I.Z., CC BY 4.0) reached the knee and stood in the path of every sweep.
        public const string ShieldFbx = Weapons + "/RoundShield/RoundShield.fbx";

        public static void Ensure(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = System.IO.Path.GetDirectoryName(folder).Replace('\\', '/');
            Ensure(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(folder));
        }
    }

    /// <summary>Layers, collision matrix, URP quality — idempotent.</summary>
    public static class GKSetup
    {
        public static void ConfigureProject()
        {
            SetLayer(GK.LayerCharacter, "Character");
            SetLayer(GK.LayerHurtbox, "Hurtbox");
            SetLayer(GK.LayerTerrain, "Terrain");
            SetLayer(GK.LayerInteractable, "Interactable");
            // interactable volumes are query-only: they never block or push anything
            for (int i = 0; i < 32; i++) Physics.IgnoreLayerCollision(GK.LayerInteractable, i, true);
            // Hurtboxes are queried explicitly; they never collide physically with anything except ragdoll-to-world
            for (int i = 0; i < 32; i++) Physics.IgnoreLayerCollision(GK.LayerHurtbox, i, i != 0 && i != 10);
            Physics.IgnoreLayerCollision(GK.LayerHurtbox, GK.LayerHurtbox, true);
            Physics.IgnoreLayerCollision(GK.LayerCharacter, GK.LayerHurtbox, true);

            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.runInBackground = true;   // keep the game running while the editor is unfocused (remote play-tests)
            QualitySettings.vSyncCount = 1;
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            QualitySettings.lodBias = 2f;
            QualitySettings.skinWeights = SkinWeights.FourBones;
            QualitySettings.globalTextureMipmapLimit = 0;

            ConfigureURP();
            AssetDatabase.SaveAssets();
            GKBridge.Log("project configured (layers, physics, URP)");
        }

        static void SetLayer(int index, string name)
        {
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("layers");
            var p = layers.GetArrayElementAtIndex(index);
            if (p.stringValue != name) { p.stringValue = name; tagManager.ApplyModifiedPropertiesWithoutUndo(); }
        }

        static void ConfigureURP()
        {
            // Apply to every URP asset referenced by quality levels so whichever is active looks right.
            var seen = new System.Collections.Generic.HashSet<UniversalRenderPipelineAsset>();
            var current = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (current != null) seen.Add(current);
            int levels = QualitySettings.names.Length;
            for (int i = 0; i < levels; i++)
            {
                var a = QualitySettings.GetRenderPipelineAssetAt(i) as UniversalRenderPipelineAsset;
                if (a != null) seen.Add(a);
            }
            foreach (var a in seen)
            {
                a.shadowDistance = 140f;
                a.shadowCascadeCount = 4;
                a.cascadeBorder = 0.15f;
                a.supportsHDR = true;
                a.msaaSampleCount = 4;
                a.renderScale = 1f;
                SetPrivate(a, "m_SoftShadowsSupported", true);
                SetPrivate(a, "m_MainLightShadowmapResolution", 4096);
                SetPrivate(a, "m_AdditionalLightsShadowmapResolution", 2048);
                SetPrivate(a, "m_ColorGradingMode", 1); // HDR grading
                SetPrivate(a, "m_ColorGradingLutSize", 64);
                SetPrivate(a, "m_RequireDepthTexture", true);
                SetPrivate(a, "m_RequireOpaqueTexture", true);
                EditorUtility.SetDirty(a);
                foreach (var rd in GetRendererData(a)) EnsureSSAO(rd);
            }
        }

        static System.Collections.Generic.IEnumerable<ScriptableRendererData> GetRendererData(UniversalRenderPipelineAsset a)
        {
            var f = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance);
            var arr = f != null ? f.GetValue(a) as ScriptableRendererData[] : null;
            if (arr == null) yield break;
            foreach (var r in arr) if (r != null) yield return r;
        }

        static void EnsureSSAO(ScriptableRendererData rd)
        {
            foreach (var f in rd.rendererFeatures)
                if (f != null && f.GetType().Name == "ScreenSpaceAmbientOcclusion") { ConfigureSSAO(f); return; }
            var type = typeof(UniversalRenderPipelineAsset).Assembly.GetType("UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion");
            if (type == null) return;
            var feature = ScriptableObject.CreateInstance(type) as ScriptableRendererFeature;
            feature.name = "SSAO";
            AssetDatabase.AddObjectToAsset(feature, rd);
            rd.rendererFeatures.Add(feature);
            var mapF = typeof(ScriptableRendererData).GetField("m_RendererFeatureMap", BindingFlags.NonPublic | BindingFlags.Instance);
            if (mapF != null)
            {
                var map = mapF.GetValue(rd) as System.Collections.Generic.List<long>;
                long id; string guid;
                if (map != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out guid, out id)) map.Add(id);
            }
            ConfigureSSAO(feature);
            EditorUtility.SetDirty(rd);
        }

        static void ConfigureSSAO(ScriptableRendererFeature f)
        {
            var sf = f.GetType().GetField("m_Settings", BindingFlags.NonPublic | BindingFlags.Instance);
            if (sf == null) return;
            object s = sf.GetValue(f);
            SetField(s, "Intensity", 1.1f);
            SetField(s, "Radius", 0.3f);
            SetField(s, "DirectLightingStrength", 0.3f);
            SetField(s, "Falloff", 60f);
            sf.SetValue(f, s);
            EditorUtility.SetDirty(f);
        }

        static void SetField(object o, string name, object value)
        {
            var f = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null && f.FieldType == value.GetType()) f.SetValue(o, value);
        }

        static void SetPrivate(object o, string name, object value)
        {
            var f = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return;
            try
            {
                if (f.FieldType.IsEnum) f.SetValue(o, System.Enum.ToObject(f.FieldType, value));
                else f.SetValue(o, System.Convert.ChangeType(value, f.FieldType));
            }
            catch { }
        }
    }
}
