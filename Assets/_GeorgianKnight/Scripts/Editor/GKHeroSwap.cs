using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Which prefab the builders place as the player. The M1 hero is Knight_Clean (Director, DECISIONS 2026-10-30, final):
    /// the finished Clean_Sandbox knight, the same man as in the intro, in his Character Creator outfit until the period
    /// outfit is made. One place to change it; GKHeroFit gives any later hero the same combat fit.
    /// </summary>
    public static class GKHeroSwap
    {
        public const string M1PlayerPath = GKHeroCC.CleanPrefabPath;

        public static GameObject M1Player { get { return AssetDatabase.LoadAssetAtPath<GameObject>(M1PlayerPath); } }

        /// <summary>The open scenes' M1 hero instances lose every transform override below the root (a pose saved from
        /// the editor kept old bone positions: the rescaled prefab did not show), keeping the root's place, the camera
        /// links and the rendering layers. Saves the scenes it changed.</summary>
        [GKCommand("heroreset", "heroreset: revert the M1 hero's bone / child transform overrides in the open scenes (root place, camera links and layers kept), save")]
        static void HeroReset(string arg)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("heroreset: stop play mode first"); return; }
            for (int si = 0; si < UnityEngine.SceneManagement.SceneManager.sceneCount; si++)
            {
                var s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(si);
                if (!s.isLoaded) continue;
                foreach (var r in s.GetRootGameObjects())
                {
                    if (!PrefabUtility.IsAnyPrefabInstanceRoot(r) || PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(r) != M1PlayerPath) continue;
                    var mods = PrefabUtility.GetPropertyModifications(r);
                    if (mods == null) continue;
                    var rootSrc = PrefabUtility.GetCorrespondingObjectFromSource(r.transform);
                    var keep = new System.Collections.Generic.List<PropertyModification>();
                    int dropped = 0;
                    foreach (var m in mods)
                    {
                        bool childTransform = m.target is Transform && m.target != rootSrc;
                        if (childTransform) { dropped++; continue; }
                        keep.Add(m);
                    }
                    PrefabUtility.SetPropertyModifications(r, keep.ToArray());
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(s);
                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(s);
                    GKBridge.Log("heroreset " + s.name + ": " + dropped + " child transform overrides reverted, " + keep.Count + " kept, saved");
                }
            }
        }
    }
}
