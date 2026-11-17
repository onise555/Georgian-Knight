using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// The arena's wave pacing as data (M1 master list row 2, gap review G-03 / G-04 / G-13 / G-30): spawns pulled in to
    /// 30 m of the hero and off screen instead of 94-120 m walk-ins; the first wave opens as a duel the second man joins;
    /// waves 2 and 3 pour in as assaults; wave 3 comes for the hero instead of holding Black Banner Rise 134 m out;
    /// veterans fight with their own move set (the mace).
    /// </summary>
    public static class GKArenaPacing
    {
        const string VeteranPrefab = "Assets/_GeorgianKnight/Prefabs/Seljuk_Veteran.prefab";

        [GKCommand("arena_pacing", "arena_pacing [check]: set the open scene's WaveManager to the M1 pacing (30 m approach, duel opener, 2/4/5 men with stagger, no hold point, mace veterans) and save the scene; 'check' only reports")]
        static void Pacing(string arg)
        {
            if (EditorApplication.isPlaying) { GKBridge.Log("arena_pacing: stop play mode first"); return; }
            var wm = Object.FindAnyObjectByType<WaveManager>(FindObjectsInactive.Include);
            if (wm == null) { GKBridge.Log("arena_pacing: no WaveManager in the open scene"); return; }
            if (arg.Trim() == "check") { GKBridge.Log("arena_pacing: " + Describe(wm)); return; }

            Undo.RecordObject(wm, "Arena pacing");
            wm.approachDistance = 30f;
            var vet = AssetDatabase.LoadAssetAtPath<GameObject>(VeteranPrefab);
            if (vet != null) wm.veteranPrefab = vet;
            else GKBridge.Log("arena_pacing: " + VeteranPrefab + " not found; veterans stay re-tuned warriors");
            if (wm.waves == null || wm.waves.Length < 3) { GKBridge.Log("arena_pacing: expected 3 waves, found " + (wm.waves == null ? 0 : wm.waves.Length)); return; }

            // W1: a duel, the second man 10 s behind (he arrives as the first one tires)
            Set(wm.waves[0], 2, 0, 3f, 10f, new[] { 3 });
            // W2: four men down two lanes, 2.5 s apart: an assault, not a trickle
            Set(wm.waves[1], 4, 0, 6f, 2.5f, new[] { 3, 1 });
            // W3: three swordsmen and two mace veterans, coming for the hero
            Set(wm.waves[2], 3, 2, 7f, 2f, new[] { 1, 2 });

            EditorUtility.SetDirty(wm);
            EditorSceneManager.MarkSceneDirty(wm.gameObject.scene);
            EditorSceneManager.SaveScene(wm.gameObject.scene);
            GKBridge.Log("arena_pacing: saved " + wm.gameObject.scene.name + " | " + Describe(wm));
        }

        static void Set(WaveManager.Wave w, int warriors, int veterans, float delay, float stagger, int[] lanes)
        {
            w.warriors = warriors;
            w.veterans = veterans;
            w.delayBefore = delay;
            w.stagger = stagger;
            w.lanes = lanes;
            w.holdPoint = "";
        }

        static string Describe(WaveManager wm)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("approach ").Append(wm.approachDistance).Append(" m, veteran prefab ").Append(wm.veteranPrefab != null ? wm.veteranPrefab.name : "none");
            for (int i = 0; wm.waves != null && i < wm.waves.Length; i++)
            {
                var w = wm.waves[i];
                sb.Append(" | W").Append(i + 1).Append(": ").Append(w.warriors).Append("w+").Append(w.veterans).Append("v")
                  .Append(w.champion ? "+champ" : "").Append(" delay ").Append(w.delayBefore).Append(" stagger ").Append(w.stagger)
                  .Append(" lanes ").Append(w.lanes == null ? "-" : string.Join(",", System.Array.ConvertAll(w.lanes, x => x.ToString())))
                  .Append(string.IsNullOrEmpty(w.holdPoint) ? "" : " hold " + w.holdPoint);
            }
            return sb.ToString();
        }
    }
}
