using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// The MainMenu scene (GameConfig.mainMenuScene): one camera that clears to ink 950, nothing else. Everything the
    /// player sees there is GKMenus (the painted plate, the logotype, the entries), so the scene stays this small.
    /// </summary>
    public static class GKMenuScene
    {
        const string Path = "Assets/_GeorgianKnight/Scenes/MainMenu.unity";

        [GKCommand("ui_mainmenu_scene", "ui_mainmenu_scene: create Scenes/MainMenu.unity (camera only; GKMenus draws the menu), then put it first in Build Settings")]
        static void Create(string arg)
        {
            if (EditorApplication.isPlaying) { GKBridge.Log("ui_mainmenu_scene: stop play mode first"); return; }
            if (!System.IO.File.Exists(Path))
            {
                // never a dialog from the bridge: an open scene with unsaved changes stops the command instead
                if (SceneManager.GetActiveScene().isDirty) { GKBridge.Log("ui_mainmenu_scene: the open scene has unsaved changes; save or revert it first"); return; }
                string back = SceneManager.GetActiveScene().path;
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var go = new GameObject("Main Camera");
                go.tag = "MainCamera";
                var cam = go.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color32(0x0B, 0x0E, 0x10, 0xFF);
                cam.cullingMask = 0;
                go.AddComponent<AudioListener>();
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.skybox = null;
                EditorSceneManager.SaveScene(scene, Path);
                GKBridge.Log("ui_mainmenu_scene: created " + Path);
                if (!string.IsNullOrEmpty(back)) EditorSceneManager.OpenScene(back);
            }
            else GKBridge.Log("ui_mainmenu_scene: " + Path + " exists");
            GKBuildScenes.BuildScenes("");
        }
    }
}
