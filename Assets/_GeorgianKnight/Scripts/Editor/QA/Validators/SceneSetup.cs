using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// Scene-wide setup checks: sun/shadows, post-processing bounds (bloom, exposure, saturation, vignette, aberration,
    /// tonemapping), cameras, NavMesh presence and coverage of spawn/quest points, AudioListener count and duplicated
    /// EventSystems/UIDocuments.
    /// </summary>
    public static class SceneSetup
    {
        public static void Check(string path, Scene scene, QaSettings.SceneLimits lim, Dictionary<string, List<Component>> byType, bool rigHasListener, List<QaIssue> issues)
        {
            Lights(path, lim, byType, issues);
            bool anyVolume = PostProcessing(path, lim, byType, issues);
            Cameras(path, lim, byType, anyVolume, issues);
            Navigation(path, lim, byType, issues);
            Listeners(path, byType, rigHasListener, issues);
            Ui(path, byType, issues);
        }

        static IEnumerable<T> All<T>(Dictionary<string, List<Component>> byType) where T : Component
        {
            List<Component> list;
            if (!byType.TryGetValue(typeof(T).Name, out list)) yield break;
            foreach (var c in list)
            {
                var t = c as T;
                if (t != null && t.gameObject.activeInHierarchy) yield return t;
            }
        }

        static void Lights(string path, QaSettings.SceneLimits lim, Dictionary<string, List<Component>> byType, List<QaIssue> issues)
        {
            var suns = new List<string>();
            var leaky = new List<string>();
            foreach (var l in All<Light>(byType))
            {
                if (!l.enabled) continue;
                string obj = QaUnity.HierarchyPath(l.transform);
                if (l.type == LightType.Directional)
                {
                    suns.Add(obj);
                    if (l.shadows == LightShadows.None) issues.Add(QaRules.SceneSunShadows.Issue(path, 0, obj, "directional light casts no shadows", QaSeverity.Major, null));
                }
                else if ((l.type == LightType.Point || l.type == LightType.Spot) && l.range >= lim.bigLightRange && l.shadows == LightShadows.None &&
                         l.lightmapBakeType != LightmapBakeType.Baked)
                    leaky.Add(obj + " (" + l.range.ToString("0") + " m)");
            }
            if (suns.Count > 1) issues.Add(QaRules.SceneMultipleSuns.Issue(path, 0, suns.Count + " active directional lights: " + QaUnity.Join(suns, 4)));
            if (leaky.Count > 0) issues.Add(QaRules.SceneLightShadows.Issue(path, 0, leaky.Count + " large realtime light(s) without shadows: " + QaUnity.Join(leaky, 5)));
        }

        /// <summary>Checks global volumes; returns true when the scene has any volume.</summary>
        static bool PostProcessing(string path, QaSettings.SceneLimits lim, Dictionary<string, List<Component>> byType, List<QaIssue> issues)
        {
            bool any = false, tonemapped = false;
            var priorities = new Dictionary<float, string>();
            foreach (var v in All<Volume>(byType))
            {
                if (!v.enabled || v.sharedProfile == null) continue;
                any = true;
                string obj = QaUnity.HierarchyPath(v.transform);
                string other;
                if (v.isGlobal && priorities.TryGetValue(v.priority, out other))
                    issues.Add(PostFx(path, obj, "global volume shares priority " + v.priority + " with " + other + " (blend order undefined)", QaSeverity.Minor));
                else if (v.isGlobal) priorities.Add(v.priority, obj);
                Overrides(path, obj, v.sharedProfile, lim, issues);
                Tonemapping tm;
                if (v.sharedProfile.TryGet(out tm) && tm.active && tm.mode.overrideState && tm.mode.value != TonemappingMode.None) tonemapped = true;
            }
            if (any && !tonemapped) issues.Add(QaRules.SceneNoTonemapping.Issue(path, 0, "no active volume sets Tonemapping (ACES/Neutral)"));
            return any;
        }

        /// <summary>Bloom, exposure, saturation, vignette and chromatic aberration bounds of one profile.</summary>
        static void Overrides(string path, string obj, VolumeProfile p, QaSettings.SceneLimits lim, List<QaIssue> issues)
        {
            Bloom bloom;
            if (p.TryGet(out bloom) && bloom.active)
            {
                Above(path, obj, bloom.intensity, lim.bloomIntensityMax, "bloom intensity", QaSeverity.Major, issues);
                if (bloom.threshold.overrideState && bloom.threshold.value < lim.bloomThresholdMin)
                    issues.Add(PostFx(path, obj, "bloom threshold " + bloom.threshold.value.ToString("0.00") + " (min " + lim.bloomThresholdMin + "): everything glows", QaSeverity.Minor));
            }
            ColorAdjustments color;
            if (p.TryGet(out color) && color.active)
            {
                if (color.postExposure.overrideState && Mathf.Abs(color.postExposure.value) > lim.postExposureMax)
                    issues.Add(PostFx(path, obj, "post exposure " + color.postExposure.value.ToString("0.00") + " EV (max ±" + lim.postExposureMax + ")", QaSeverity.Major));
                Above(path, obj, color.saturation, lim.saturationMax, "saturation", QaSeverity.Minor, issues);
            }
            Vignette vignette;
            if (p.TryGet(out vignette) && vignette.active) Above(path, obj, vignette.intensity, lim.vignetteMax, "vignette", QaSeverity.Minor, issues);
            ChromaticAberration ca;
            if (p.TryGet(out ca) && ca.active) Above(path, obj, ca.intensity, lim.chromaticMax, "chromatic aberration", QaSeverity.Minor, issues);
        }

        static void Above(string path, string obj, VolumeParameter<float> p, float max, string what, QaSeverity severity, List<QaIssue> issues)
        {
            if (p.overrideState && p.value > max)
                issues.Add(PostFx(path, obj, what + " " + p.value.ToString("0.00") + " (max " + max + ")", severity));
        }

        static QaIssue PostFx(string path, string obj, string message, QaSeverity severity)
        {
            return QaRules.ScenePostFx.Issue(path, 0, obj, message, severity, null);
        }

        static void Cameras(string path, QaSettings.SceneLimits lim, Dictionary<string, List<Component>> byType, bool anyVolume, List<QaIssue> issues)
        {
            var depths = new Dictionary<float, string>();
            foreach (var cam in All<Camera>(byType))
            {
                if (!cam.enabled || cam.targetTexture != null) continue;
                string obj = QaUnity.HierarchyPath(cam.transform);
                var data = cam.GetComponent<UniversalAdditionalCameraData>();
                bool overlay = data != null && data.renderType == CameraRenderType.Overlay;
                if (anyVolume && data != null && !overlay && !data.renderPostProcessing)
                    issues.Add(QaRules.SceneCamera.Issue(path, 0, obj, "post-processing disabled on a camera while the scene has volumes", QaSeverity.Major, null));
                if (cam.nearClipPlane < lim.nearClipMin)
                    issues.Add(QaRules.SceneCamera.Issue(path, 0, obj, "near clip " + cam.nearClipPlane + " m (z-fighting at distance)", QaSeverity.Minor, null));
                if (cam.farClipPlane / Mathf.Max(1e-4f, cam.nearClipPlane) > lim.farNearRatioMax)
                    issues.Add(QaRules.SceneCamera.Issue(path, 0, obj, "far/near ratio " + (cam.farClipPlane / cam.nearClipPlane).ToString("0") + " (depth precision)", QaSeverity.Minor, null));
                if (overlay) continue;
                string other;
                if (depths.TryGetValue(cam.depth, out other))
                    issues.Add(QaRules.SceneCamera.Issue(path, 0, obj, "renders to screen at depth " + cam.depth + " like " + other, QaSeverity.Minor, null));
                else depths.Add(cam.depth, obj);
            }
        }

        static void Navigation(string path, QaSettings.SceneLimits lim, Dictionary<string, List<Component>> byType, List<QaIssue> issues)
        {
            var critical = new List<Transform>();
            var quest = new List<Transform>();
            foreach (var name in lim.spawnComponents) Collect(byType, name, critical);
            Collect(byType, "NavMeshAgent", critical);
            foreach (var name in lim.questComponents) Collect(byType, name, quest);
            if (critical.Count == 0 && quest.Count == 0) return;
            var tri = NavMesh.CalculateTriangulation();
            if (tri.vertices == null || tri.vertices.Length == 0)
            {
                issues.Add(QaRules.SceneNavMeshMissing.Issue(path, 0, "no NavMesh, but the scene has " + critical.Count + " spawn/agent and " + quest.Count + " quest point(s)"));
                return;
            }
            foreach (var t in critical) OnMesh(path, t, lim, QaSeverity.Critical, issues);
            foreach (var t in quest) OnMesh(path, t, lim, QaSeverity.Major, issues);
        }

        static void Collect(Dictionary<string, List<Component>> byType, string name, List<Transform> into)
        {
            List<Component> list;
            if (!byType.TryGetValue(name, out list)) return;
            foreach (var c in list) if (c != null && c.gameObject.activeInHierarchy && !into.Contains(c.transform)) into.Add(c.transform);
        }

        static void OnMesh(string path, Transform t, QaSettings.SceneLimits lim, QaSeverity severity, List<QaIssue> issues)
        {
            NavMeshHit hit;
            if (NavMesh.SamplePosition(t.position, out hit, lim.navMeshSnap, NavMesh.AllAreas)) return;
            issues.Add(QaRules.SceneNavMeshCoverage.Issue(path, 0, QaUnity.HierarchyPath(t), "no NavMesh within " + lim.navMeshSnap + " m of " + t.position, severity, null));
        }

        static void Listeners(string path, Dictionary<string, List<Component>> byType, bool rigHasListener, List<QaIssue> issues)
        {
            var listeners = new List<string>();
            foreach (var l in All<AudioListener>(byType)) if (l.enabled) listeners.Add(QaUnity.HierarchyPath(l.transform));
            if (listeners.Count > 1)
                issues.Add(QaRules.SceneAudioListeners.Issue(path, 0, listeners.Count + " active AudioListeners: " + QaUnity.Join(listeners, 4)));
            else if (listeners.Count == 0 && !rigHasListener)
                issues.Add(QaRules.SceneAudioListeners.Issue(path, 0, "no AudioListener in the scene and no prefab under Prefabs/ brings one"));
        }

        static void Ui(string path, Dictionary<string, List<Component>> byType, List<QaIssue> issues)
        {
            List<Component> systems;
            if (byType.TryGetValue("EventSystem", out systems) && systems.Count > 1)
                issues.Add(QaRules.SceneUiDuplicates.Issue(path, 0, systems.Count + " EventSystems"));
            var docs = new Dictionary<string, string>();
            foreach (var d in All<UIDocument>(byType))
            {
                if (!d.enabled || d.visualTreeAsset == null) continue;
                string key = d.visualTreeAsset.GetInstanceID() + "|" + (d.panelSettings != null ? d.panelSettings.GetInstanceID() : 0);
                string obj = QaUnity.HierarchyPath(d.transform), other;
                if (docs.TryGetValue(key, out other))
                    issues.Add(QaRules.SceneUiDuplicates.Issue(path, 0, obj, "UIDocument shows " + d.visualTreeAsset.name + " on the same panel as " + other, QaSeverity.Major, null));
                else docs.Add(key, obj);
            }
        }
    }
}
