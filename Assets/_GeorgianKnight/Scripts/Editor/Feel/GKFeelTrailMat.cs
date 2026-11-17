using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Blade trails with an empty material slot (QA-0 PREFAB/SCENE.MISSING_MATERIAL): the trail took its material only at
    /// runtime, so prefabs and scenes kept the slot empty. `check` lists every WeaponTrail renderer without a material in
    /// the project's prefabs (and in a scene given by path) and saves nothing; `apply` fills the slot with Feel's trail
    /// material in the one prefab or scene named, and saves only that file, so each owner applies it to their own files.
    /// </summary>
    public static class GKFeelTrailMat
    {
        const string PrefabRoot = "Assets/_GeorgianKnight";

        [GKCommand("feel_trailmat", "feel_trailmat check [scene.unity] | apply <file.prefab|file.unity>: list blade trails with an empty material slot (saves nothing), or give them Feel's trail material in that one file and save it (edit mode)")]
        static void Run(string arg)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("feel_trailmat: edit mode only"); return; }
            var parts = (arg ?? "").Trim().Split(new[] { ' ' }, 2, System.StringSplitOptions.RemoveEmptyEntries);
            string mode = parts.Length > 0 ? parts[0].ToLowerInvariant() : "check";
            string path = parts.Length > 1 ? parts[1].Trim() : "";
            var mat = Feel.FeelSettings.Active != null ? Feel.FeelSettings.Active.trail.material : null;
            if (mat == null) { GKBridge.Log("feel_trailmat: Feel_Default has no trail material (run feel_build)"); return; }

            if (mode == "check")
            {
                int bad = 0;
                foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { PrefabRoot }))
                {
                    string p = AssetDatabase.GUIDToAssetPath(guid);
                    var root = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                    if (root == null) continue;
                    foreach (var n in Empty(root)) { GKBridge.Log("feel_trailmat: empty  " + p + " > " + n); bad++; }
                }
                if (path.EndsWith(".unity")) bad += CheckScene(path);
                GKBridge.Log("feel_trailmat: " + bad + " empty trail slot(s); material to use " + AssetDatabase.GetAssetPath(mat));
                return;
            }
            if (mode != "apply" || path.Length == 0) { GKBridge.Log("feel_trailmat: usage: check [scene.unity] | apply <file.prefab|file.unity>"); return; }
            if (path.EndsWith(".prefab")) ApplyPrefab(path, mat);
            else if (path.EndsWith(".unity")) ApplyScene(path, mat);
            else GKBridge.Log("feel_trailmat: not a prefab or scene: " + path);
        }

        /// <summary>Paths (under the root) of the WeaponTrail renderers whose material slots are all empty.</summary>
        static List<string> Empty(GameObject root)
        {
            var list = new List<string>();
            foreach (var t in root.GetComponentsInChildren<WeaponTrail>(true))
            {
                var r = t.GetComponent<MeshRenderer>();
                if (r != null && (r.sharedMaterials.Length == 0 || r.sharedMaterials.All(m => m == null))) list.Add(PathOf(t.transform, root.transform));
            }
            return list;
        }

        static int Fill(GameObject root, Material mat)
        {
            int n = 0;
            foreach (var t in root.GetComponentsInChildren<WeaponTrail>(true))
            {
                var r = t.GetComponent<MeshRenderer>();
                if (r == null || (r.sharedMaterials.Length > 0 && r.sharedMaterials.Any(m => m != null))) continue;
                r.sharedMaterial = mat;
                n++;
            }
            return n;
        }

        static void ApplyPrefab(string path, Material mat)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null) { GKBridge.Log("feel_trailmat: no prefab at " + path); return; }
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                int n = Fill(root, mat);
                if (n > 0) PrefabUtility.SaveAsPrefabAsset(root, path);
                GKBridge.Log("feel_trailmat: " + path + " - " + n + " trail slot(s) filled" + (n > 0 ? ", saved" : ", nothing to save"));
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        static void ApplyScene(string path, Material mat)
        {
            var scene = SceneManager.GetSceneByPath(path);
            bool opened = false;
            if (scene.IsValid() && scene.isLoaded)
            {
                if (scene.isDirty) { GKBridge.Log("feel_trailmat: " + path + " has unsaved changes; its owner saves first"); return; }
            }
            else { scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive); opened = true; }
            try
            {
                int n = 0;
                foreach (var go in scene.GetRootGameObjects()) n += Fill(go, mat);
                if (n > 0) { EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); }
                GKBridge.Log("feel_trailmat: " + path + " - " + n + " trail slot(s) filled" + (n > 0 ? ", saved" : ", nothing to save"));
            }
            finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
        }

        static int CheckScene(string path)
        {
            var scene = SceneManager.GetSceneByPath(path);
            bool opened = false;
            if (!(scene.IsValid() && scene.isLoaded)) { scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive); opened = true; }
            int bad = 0;
            try
            {
                foreach (var go in scene.GetRootGameObjects())
                    foreach (var n in Empty(go)) { GKBridge.Log("feel_trailmat: empty  " + path + " > " + n); bad++; }
            }
            finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
            return bad;
        }

        static string PathOf(Transform t, Transform root)
        {
            var names = new List<string>();
            for (var x = t; x != null; x = x.parent) { names.Add(x.name); if (x == root) break; }
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
