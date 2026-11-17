using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// The build scene list as data (QA GK-4): shipping scenes in play order, enabled; work scenes listed but disabled so
    /// they stay one click away without being built or validated as shipping scenes. Scenes not on the list (the Unity
    /// template SampleScene) are dropped. A listed scene that does not exist yet is skipped and reported.
    /// </summary>
    public static class GKBuildScenes
    {
        const string Root = "Assets/_GeorgianKnight/Scenes/";

        // play order: the main menu first (GameConfig.mainMenuScene), then the intro film, then the battle (MENUS_M1 §7)
        static readonly string[] Shipping = { Root + "MainMenu.unity", Root + "Intro_Cutscene.unity", Root + "Level_Didgori_Arena.unity" };
        static readonly string[] Work = { Root + "Didgori.unity", Root + "Combat_Sandbox.unity", Root + "Clean_Sandbox.unity" };

        [GKCommand("tools_buildscenes", "tools_buildscenes [check] - set Build Settings to the M1 scene list; 'check' only reports")]
        public static void BuildScenes(string arg)
        {
            var wanted = new List<EditorBuildSettingsScene>();
            var missing = new List<string>();
            Add(wanted, missing, Shipping, true);
            Add(wanted, missing, Work, false);
            bool same = wanted.Count == EditorBuildSettings.scenes.Length;
            for (int i = 0; same && i < wanted.Count; i++)
                same = wanted[i].path == EditorBuildSettings.scenes[i].path && wanted[i].enabled == EditorBuildSettings.scenes[i].enabled;
            bool apply = arg.Trim() != "check";
            if (apply && !same)
            {
                EditorBuildSettings.scenes = wanted.ToArray();
                AssetDatabase.SaveAssets();   // the setter only changes the editor's copy; this writes EditorBuildSettings.asset
            }
            var now = new List<string>();
            foreach (var s in EditorBuildSettings.scenes) now.Add((s.enabled ? "+" : "-") + Path.GetFileNameWithoutExtension(s.path));
            GKBridge.Log("tools_buildscenes: " + (same ? "already in order" : apply ? "updated" : "differs from the M1 list") +
                         (missing.Count > 0 ? "; not found yet: " + string.Join(", ", missing.ToArray()) : "") +
                         " | " + string.Join(" ", now.ToArray()));
        }

        static void Add(List<EditorBuildSettingsScene> list, List<string> missing, string[] paths, bool enabled)
        {
            foreach (var p in paths)
            {
                if (File.Exists(p)) list.Add(new EditorBuildSettingsScene(p, enabled));
                else missing.Add(Path.GetFileNameWithoutExtension(p));
            }
        }
    }
}
