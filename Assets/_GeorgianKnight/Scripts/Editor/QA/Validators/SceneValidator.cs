using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// SCENE.* rules. Opens every enabled scene in Build Settings (never saves), runs integrity, placement, lighting,
    /// post-processing, camera, NavMesh, audio-listener and UI checks, test assets, rescaled art and sky / sun agreement,
    /// then restores the scenes that were open. Refuses
    /// to touch the editor state when an open scene has unsaved changes (reports SCENE.SKIPPED so the gate fails).
    /// </summary>
    public sealed class SceneValidator : IValidator
    {
        static readonly Regex PlaceholderName = new Regex(@"^(GameObject|Cube|Sphere|Capsule|Cylinder|Plane|Quad|New Game Object|Empty|Object)( \(\d+\))?$");

        public string Id { get { return "scene"; } }
        public string Category { get { return QaCategory.Scene; } }

        public IEnumerable<QaIssue> Run(QaContext ctx)
        {
            var result = new List<QaIssue>();
            var buildScenes = new List<string>();
            foreach (var s in EditorBuildSettings.scenes) if (s.enabled && !string.IsNullOrEmpty(s.path)) buildScenes.Add(s.path);
            foreach (var path in ctx.FindAssets("t:Scene"))
                if (!buildScenes.Contains(path) && !path.Contains("/Generated/") && !path.Contains("Preview"))
                    result.Add(QaRules.SceneNotInBuild.Issue(path, 0, "scene is not enabled in Build Settings (not validated, not shipped)"));
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var open = SceneManager.GetSceneAt(i);
                if (!open.isDirty) continue;
                foreach (var p in buildScenes)
                    result.Add(QaRules.SceneSkipped.Issue(p, 0, "not validated: open scene '" + open.name + "' has unsaved changes"));
                return result;
            }
            bool rigHasListener = PlayerRigHasListener(ctx);
            var refs = new DataRefs(ctx.Definitions);
            var setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                for (int i = 0; i < buildScenes.Count; i++)
                {
                    string path = buildScenes[i];
                    ctx.Progress(path, i / (float)Mathf.Max(1, buildScenes.Count));
                    var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                    var issues = new List<QaIssue>();
                    Check(ctx, path, scene, rigHasListener, refs, issues);
                    result.AddRange(Cap(issues, ctx.Settings.scene.maxListedPerRule));
                }
            }
            finally { QaUnity.RestoreScenes(setup); }
            return result;
        }

        static void Check(QaContext ctx, string path, Scene scene, bool rigHasListener, DataRefs refs, List<QaIssue> issues)
        {
            var lim = ctx.Settings.scene;
            var roots = scene.GetRootGameObjects();
            var byType = new Dictionary<string, List<Component>>();
            foreach (var root in roots)
            {
                issues.AddRange(PrefabValidator.Integrity(path, root, QaRules.SceneMissingScript, QaRules.SceneMissingPrefab, QaRules.SceneMissingReference, QaRules.SceneMissingMaterial));
                issues.AddRange(QaArt.TestAssets(path, root));
                issues.AddRange(QaArt.Rescaled(path, root, ctx.Settings.prefab.rescaleTolerance));
                foreach (var c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null) continue;
                    if (c is MonoBehaviour) refs.Check(c, path, QaUnity.HierarchyPath(c.transform), issues);
                    string n = c.GetType().Name;
                    List<Component> list;
                    if (!byType.TryGetValue(n, out list)) { list = new List<Component>(); byType.Add(n, list); }
                    list.Add(c);
                }
            }
            ScenePlacement.Check(path, scene, lim, byType, issues);
            SceneSetup.Check(path, scene, lim, byType, rigHasListener, issues);
            SkySun(path, lim, byType, issues);
            Hygiene(path, roots, lim, issues);
        }

        /// <summary>Static flags on scenery, grey-box primitives and placeholder names.</summary>
        static void Hygiene(string path, GameObject[] roots, QaSettings.SceneLimits lim, List<QaIssue> issues)
        {
            var notStatic = new List<string>();
            var names = new List<string>();
            foreach (var root in roots)
            {
                bool environment = lim.environmentRoots.Contains(root.name);
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    var go = t.gameObject;
                    if (PlaceholderName.IsMatch(go.name)) names.Add(QaUnity.HierarchyPath(t));
                    var mf = go.GetComponent<MeshFilter>();
                    var mr = go.GetComponent<MeshRenderer>();
                    if (mf == null || mr == null || !mr.enabled || !go.activeInHierarchy || go.CompareTag("EditorOnly")) continue;
                    if (QaUnity.IsBuiltinPrimitive(mf.sharedMesh))
                        issues.Add(QaRules.ScenePrimitiveMesh.Issue(path, 0, QaUnity.HierarchyPath(t), "renders Unity's built-in " + mf.sharedMesh.name + " mesh", QaSeverity.Major, null));
                    if (environment && go.GetComponentInParent<Rigidbody>() == null && go.GetComponentInParent<Animator>() == null &&
                        (GameObjectUtility.GetStaticEditorFlags(go) & StaticEditorFlags.BatchingStatic) == 0)
                        notStatic.Add(QaUnity.HierarchyPath(t));
                }
            }
            if (notStatic.Count > 0)
                issues.Add(QaRules.SceneStaticFlags.Issue(path, 0, notStatic.Count + " scenery renderer(s) under environment roots are not Batching Static: " + QaUnity.Join(notStatic, 6)));
            if (names.Count > 0)
                issues.Add(QaRules.ScenePlaceholderName.Issue(path, 0, names.Count + " object(s) with placeholder names: " + QaUnity.Join(names, 8)));
        }

        /// <summary>The sky texture's known sun elevation against the scene's directional light (RenderSettings.sun or the first active one).</summary>
        static void SkySun(string path, QaSettings.SceneLimits lim, Dictionary<string, List<Component>> byType, List<QaIssue> issues)
        {
            var sky = RenderSettings.skybox;
            if (sky == null) return;
            Texture tex = null;
            foreach (var prop in sky.GetTexturePropertyNames()) if (sky.GetTexture(prop) != null) { tex = sky.GetTexture(prop); break; }
            if (tex == null) return;
            Light sun = RenderSettings.sun;
            List<Component> lights;
            if (sun == null && byType.TryGetValue("Light", out lights))
                foreach (var c in lights)
                {
                    var l = c as Light;
                    if (l != null && l.type == LightType.Directional && l.enabled && l.gameObject.activeInHierarchy) { sun = l; break; }
                }
            if (sun == null) return;
            float elevation = Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            float known;
            if (!AssetMath.Lookup(tex.name, lim.skySuns, out known))
            {
                issues.Add(QaRules.SceneSkySunMatch.Issue(path, 0, "", "sun elevation of sky '" + tex.name + "' unknown (scene sun " + elevation.ToString("0") +
                    "°): add it to scene.skySuns in Docs/qa_config.json", QaSeverity.Info, null));
                return;
            }
            if (Mathf.Abs(known - elevation) > lim.skySunTolerance)
                issues.Add(QaRules.SceneSkySunMatch.Issue(path, 0, QaUnity.HierarchyPath(sun.transform), "sky '" + tex.name + "' has its sun at " + known.ToString("0") +
                    "°, the scene's sun is at " + elevation.ToString("0") + "° (max " + lim.skySunTolerance.ToString("0") + "° apart)", QaSeverity.Minor, null));
        }

        static bool PlayerRigHasListener(QaContext ctx)
        {
            foreach (var p in ctx.FindAssets("t:Prefab", QaContext.AssetRoot + "/Prefabs"))
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (go != null && go.GetComponentInChildren<AudioListener>(true) != null) return true;
            }
            return false;
        }

        /// <summary>Keeps at most <paramref name="max"/> issues per rule and scene, plus one summary line for the rest.</summary>
        public static List<QaIssue> Cap(List<QaIssue> issues, int max)
        {
            var result = new List<QaIssue>();
            var counts = new Dictionary<string, int>();
            var extra = new Dictionary<string, QaIssue>();
            foreach (var i in issues)
            {
                string key = i.Rule + "|" + i.Path;
                int n;
                counts.TryGetValue(key, out n);
                counts[key] = n + 1;
                if (n < max) { result.Add(i); continue; }
                if (!extra.ContainsKey(key)) extra[key] = i;
            }
            foreach (var kv in extra)
            {
                var first = kv.Value;
                int more = counts[kv.Key] - max;
                result.Add(QaRules.Find(first.Rule).Issue(first.Path, 0, "", "… and " + more + " more " + first.Rule + " issue(s) in this scene", first.Severity, first.Fix));
            }
            return result;
        }
    }
}
