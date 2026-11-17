using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Four quality tiers (Low / Medium / High / Ultra) = four quality levels, each with its own URP asset cloned from the
    /// project's configured asset (sharing its renderer, so renderer features like SSAO exist in all tiers). The
    /// player's GameSettings.quality selects the level at runtime; finer options are applied by GraphicsApplier.
    /// </summary>
    public static class GKQuality
    {
        const string Dir = "Assets/_GeorgianKnight/Settings/Rendering";

        struct Tier
        {
            public string name; public float shadowDist; public int cascades; public int shadowRes; public int addShadowRes;
            public bool soft; public int msaa; public float lodBias; public int maxLod; public int mipLimit; public AnisotropicFiltering aniso;
            public float terrainPixelError; public float detailDensity; public float detailDistance; public int lut;
        }

        static readonly Tier[] Tiers =
        {
            new Tier { name = "Low",    shadowDist = 60f,  cascades = 2, shadowRes = 1024, addShadowRes = 512,  soft = false, msaa = 1, lodBias = 0.8f, maxLod = 0, mipLimit = 1, aniso = AnisotropicFiltering.Enable,      terrainPixelError = 12f, detailDensity = 0.35f, detailDistance = 50f,  lut = 32 },
            new Tier { name = "Medium", shadowDist = 95f,  cascades = 2, shadowRes = 2048, addShadowRes = 1024, soft = true,  msaa = 2, lodBias = 1.2f, maxLod = 0, mipLimit = 0, aniso = AnisotropicFiltering.Enable,      terrainPixelError = 7f,  detailDensity = 0.6f,  detailDistance = 70f,  lut = 32 },
            new Tier { name = "High",   shadowDist = 140f, cascades = 4, shadowRes = 4096, addShadowRes = 2048, soft = true,  msaa = 4, lodBias = 2f,   maxLod = 0, mipLimit = 0, aniso = AnisotropicFiltering.ForceEnable, terrainPixelError = 4f,  detailDensity = 0.85f, detailDistance = 130f, lut = 64 },
            new Tier { name = "Ultra",  shadowDist = 200f, cascades = 4, shadowRes = 4096, addShadowRes = 4096, soft = true,  msaa = 4, lodBias = 2.6f, maxLod = 0, mipLimit = 0, aniso = AnisotropicFiltering.ForceEnable, terrainPixelError = 2f,  detailDensity = 1f,    detailDistance = 180f, lut = 64 },
        };

        [GKCommand("quality", "create the Low/Medium/High/Ultra quality tiers")]
        public static void Build(string arg)
        {
            GKPaths.Ensure(Dir);
            var source = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (source == null) source = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
            if (source == null) { GKBridge.Log("quality: no URP asset found"); return; }
            string srcPath = AssetDatabase.GetAssetPath(source);

            var assets = new UniversalRenderPipelineAsset[Tiers.Length];
            for (int i = 0; i < Tiers.Length; i++)
            {
                var t = Tiers[i];
                string p = Dir + "/GK_URP_" + t.name + ".asset";
                var a = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(p);
                if (a == null)
                {
                    if (!AssetDatabase.CopyAsset(srcPath, p)) { GKBridge.Log("quality: copy failed " + p); return; }
                    a = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(p);
                }
                // share the source's renderer list (features such as SSAO live there)
                var listF = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance);
                if (listF != null) listF.SetValue(a, listF.GetValue(source));
                a.shadowDistance = t.shadowDist;
                a.shadowCascadeCount = t.cascades;
                a.cascadeBorder = 0.15f;
                a.supportsHDR = true;
                a.msaaSampleCount = t.msaa;
                a.renderScale = 1f;
                Set(a, "m_SoftShadowsSupported", t.soft);
                Set(a, "m_MainLightShadowmapResolution", t.shadowRes);
                Set(a, "m_AdditionalLightsShadowmapResolution", t.addShadowRes);
                Set(a, "m_ColorGradingMode", 1);
                Set(a, "m_ColorGradingLutSize", t.lut);
                Set(a, "m_RequireDepthTexture", true);
                Set(a, "m_RequireOpaqueTexture", true);
                EditorUtility.SetDirty(a);
                assets[i] = a;
            }

            var qsObj = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0];
            var so = new SerializedObject(qsObj);
            var levels = so.FindProperty("m_QualitySettings");
            levels.arraySize = Tiers.Length;
            for (int i = 0; i < Tiers.Length; i++)
            {
                var t = Tiers[i];
                var e = levels.GetArrayElementAtIndex(i);
                Prop(e, "name", t.name);
                PropObj(e, "customRenderPipeline", assets[i]);
                PropF(e, "lodBias", t.lodBias);
                PropI(e, "maximumLODLevel", t.maxLod);
                PropI(e, "globalTextureMipmapLimit", t.mipLimit);
                PropI(e, "anisotropicTextures", (int)t.aniso);
                // Character Creator garments carry up to 6 influences per vertex (the coat at the shoulders and sleeves):
                // High / Ultra skin with all of them, Low / Medium keep the cheaper 4
                PropI(e, "skinWeights", (int)(i >= 2 ? SkinWeights.Unlimited : SkinWeights.FourBones));
                PropI(e, "vSyncCount", 1);
                PropB(e, "softParticles", i >= 1);
                PropB(e, "realtimeReflectionProbes", i >= 2);
                PropB(e, "billboardsFaceCameraPosition", true);
                PropB(e, "enableLODCrossFade", true);
                PropI(e, "antiAliasing", 0);           // MSAA comes from the URP asset
                PropI(e, "terrainQualityOverrides", 0);
                PropF(e, "terrainPixelError", t.terrainPixelError);
                PropF(e, "terrainDetailDensityScale", t.detailDensity);
                PropF(e, "terrainDetailDistance", t.detailDistance);
            }
            so.FindProperty("m_CurrentQuality").intValue = 2;
            var perPlatform = so.FindProperty("m_PerPlatformDefaultQuality");
            if (perPlatform != null)
                for (int i = 0; i < perPlatform.arraySize; i++)
                {
                    var el = perPlatform.GetArrayElementAtIndex(i);
                    var second = el.FindPropertyRelative("second");
                    if (second != null) second.intValue = 2;
                }
            so.ApplyModifiedPropertiesWithoutUndo();
            QualitySettings.SetQualityLevel(2, true);
            AssetDatabase.SaveAssets();
            GKBridge.Log("quality: tiers " + string.Join("/", QualitySettings.names) + ", current " + QualitySettings.names[QualitySettings.GetQualityLevel()]);
        }

        /// <summary>TerrainQualityOverrides.DetailDensity | DetailDistance: the level replaces the terrain's own detail values.</summary>
        const int GrassOverrides = 4 | 8;

        const string TemplateRenderer = "Assets/Settings/PC_Renderer.asset";
        const string OwnRenderer = Dir + "/GK_Renderer.asset";

        /// <summary>
        /// GK-5 / GK-11 without rebuilding the tiers (which would overwrite their tuning): moves the template renderer
        /// into Settings/Rendering (GUID kept, so every tier still points at it), makes the quality levels exactly
        /// Low / Medium / High / Ultra on their GK_URP_* assets (SettingsService maps QualityPreset 0..3 onto them by
        /// index), sets High as the current and per-platform default, and GK_URP_High as the Graphics default pipeline.
        /// "check" only reports.
        /// </summary>
        [GKCommand("qa.quality", "qa.quality [check|fix]: quality levels Low/Medium/High/Ultra on GK_URP_*, Graphics default, renderer in Settings/Rendering (GK-5, GK-11)")]
        public static void QualityCheck(string arg)
        {
            if ((arg ?? "").Trim() == "fix")
            {
                MoveRenderer();
                FixLevels();
                var high = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Dir + "/GK_URP_High.asset");
                if (high != null) GraphicsSettings.defaultRenderPipeline = high;
                AssetDatabase.SaveAssets();
            }
            Report();
        }

        static void MoveRenderer()
        {
            if (AssetDatabase.LoadMainAssetAtPath(OwnRenderer) != null) { GKBridge.Log("qa.quality: renderer already at " + OwnRenderer); return; }
            if (AssetDatabase.LoadMainAssetAtPath(TemplateRenderer) == null) { GKBridge.Log("qa.quality: no " + TemplateRenderer + " to move"); return; }
            string err = AssetDatabase.MoveAsset(TemplateRenderer, OwnRenderer);
            GKBridge.Log(string.IsNullOrEmpty(err) ? "qa.quality: moved " + TemplateRenderer + " -> " + OwnRenderer + " (GUID kept)" : "qa.quality: move FAILED: " + err);
        }

        /// <summary>Rebuilds the level list in tier order, keeping each existing level's settings and adding missing ones from the tier table.</summary>
        static void FixLevels()
        {
            var qsObj = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0];
            var so = new SerializedObject(qsObj);
            var levels = so.FindProperty("m_QualitySettings");
            for (int i = 0; i < Tiers.Length; i++)
            {
                var t = Tiers[i];
                int at = -1;
                for (int j = 0; j < levels.arraySize; j++)
                    if (levels.GetArrayElementAtIndex(j).FindPropertyRelative("name").stringValue == t.name) { at = j; break; }
                if (at < 0)
                {
                    // duplicate the nearest existing level (the copy lands at src, the original moves to src + 1), then give it the tier's numbers
                    int src = Mathf.Min(i, levels.arraySize - 1);
                    levels.InsertArrayElementAtIndex(src);
                    at = src;
                    var e = levels.GetArrayElementAtIndex(at);
                    Prop(e, "name", t.name);
                    PropF(e, "lodBias", t.lodBias);
                    PropI(e, "maximumLODLevel", t.maxLod);
                    PropI(e, "globalTextureMipmapLimit", t.mipLimit);
                    PropI(e, "anisotropicTextures", (int)t.aniso);
                    PropI(e, "skinWeights", (int)(i >= 2 ? SkinWeights.Unlimited : SkinWeights.FourBones));
                    PropB(e, "softParticles", i >= 1);
                    PropB(e, "realtimeReflectionProbes", i >= 2);
                    PropF(e, "terrainPixelError", t.terrainPixelError);
                    PropF(e, "terrainDetailDensityScale", t.detailDensity);
                    PropF(e, "terrainDetailDistance", t.detailDistance);
                    GKBridge.Log("qa.quality: added level " + t.name + " at " + at);
                }
                if (at != i) levels.MoveArrayElement(at, i);
                // grass: Low and Medium thin density and distance (below High's terrain value of 80 m); High and Ultra use the terrain's own
                var lvl = levels.GetArrayElementAtIndex(i);
                PropI(lvl, "terrainQualityOverrides", i < 2 ? GrassOverrides : 0);
                PropF(lvl, "terrainDetailDensityScale", t.detailDensity);
                PropF(lvl, "terrainDetailDistance", t.detailDistance);
                var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Dir + "/GK_URP_" + t.name + ".asset");
                if (asset != null) PropObj(levels.GetArrayElementAtIndex(i), "customRenderPipeline", asset);
                else GKBridge.Log("qa.quality: missing " + Dir + "/GK_URP_" + t.name + ".asset");
            }
            while (levels.arraySize > Tiers.Length) levels.DeleteArrayElementAtIndex(levels.arraySize - 1);
            so.FindProperty("m_CurrentQuality").intValue = 2;
            var perPlatform = so.FindProperty("m_PerPlatformDefaultQuality");
            if (perPlatform != null)
                for (int i = 0; i < perPlatform.arraySize; i++)
                {
                    var second = perPlatform.GetArrayElementAtIndex(i).FindPropertyRelative("second");
                    if (second != null) second.intValue = 2;
                }
            so.ApplyModifiedPropertiesWithoutUndo();
            QualitySettings.SetQualityLevel(2, true);
        }

        static void Report()
        {
            var names = QualitySettings.names;
            for (int i = 0; i < names.Length; i++)
            {
                var rp = QualitySettings.GetRenderPipelineAssetAt(i) as UniversalRenderPipelineAsset;
                string renderer = "?";
                if (rp != null)
                {
                    var listF = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance);
                    var list = listF != null ? listF.GetValue(rp) as ScriptableRendererData[] : null;
                    renderer = list != null && list.Length > 0 && list[0] != null ? AssetDatabase.GetAssetPath(list[0]) : "none";
                }
                var qs = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0]).FindProperty("m_QualitySettings");
                var el = i < qs.arraySize ? qs.GetArrayElementAtIndex(i) : null;
                string grass = el == null ? "?" : (el.FindPropertyRelative("terrainQualityOverrides").intValue == 0 ? "terrain's own" :
                    el.FindPropertyRelative("terrainDetailDensityScale").floatValue.ToString("0.##") + " / " + el.FindPropertyRelative("terrainDetailDistance").floatValue.ToString("0") + " m");
                GKBridge.Log("qa.quality: level " + i + " " + names[i] + " -> " + (rp != null ? AssetDatabase.GetAssetPath(rp) : "NO PIPELINE") + ", renderer " + renderer + ", grass " + grass);
            }
            GKBridge.Log("qa.quality: current " + names[QualitySettings.GetQualityLevel()] + ", Graphics default " +
                (GraphicsSettings.defaultRenderPipeline != null ? AssetDatabase.GetAssetPath(GraphicsSettings.defaultRenderPipeline) : "none") +
                (names.Length == Tiers.Length ? "" : " — EXPECTED " + Tiers.Length + " levels"));
        }

        static void Set(object o, string name, object value)
        {
            var f = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) { GKBridge.Log("quality: no field " + name); return; }
            if (f.FieldType.IsEnum) f.SetValue(o, System.Enum.ToObject(f.FieldType, value));
            else f.SetValue(o, System.Convert.ChangeType(value, f.FieldType));
        }

        static void Prop(SerializedProperty e, string n, string v) { var p = e.FindPropertyRelative(n); if (p != null) p.stringValue = v; else GKBridge.Log("quality: missing " + n); }
        static void PropF(SerializedProperty e, string n, float v) { var p = e.FindPropertyRelative(n); if (p != null) p.floatValue = v; else GKBridge.Log("quality: missing " + n); }
        static void PropI(SerializedProperty e, string n, int v) { var p = e.FindPropertyRelative(n); if (p != null) p.intValue = v; else GKBridge.Log("quality: missing " + n); }
        static void PropB(SerializedProperty e, string n, bool v) { var p = e.FindPropertyRelative(n); if (p != null) p.boolValue = v; else GKBridge.Log("quality: missing " + n); }
        static void PropObj(SerializedProperty e, string n, Object v) { var p = e.FindPropertyRelative(n); if (p != null) p.objectReferenceValue = v; else GKBridge.Log("quality: missing " + n); }
    }
}
