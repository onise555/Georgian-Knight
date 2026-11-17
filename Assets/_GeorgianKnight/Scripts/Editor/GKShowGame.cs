using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// showgame: puts the editor in a state a person can watch and play: no maximized panel, the Game view in front,
    /// and the Scene view framed on the player (or on the scene's origin when there is none).
    /// </summary>
    public static class GKShowGame
    {
        /// <summary>Script changes made while the game runs wait until play stops: a hot reload in play lost every
        /// component's unserialized set-up (the CC twist bones froze at the bind pose and the arms twisted like tubes).</summary>
        [InitializeOnLoadMethod]
        static void NoHotReloadInPlay()
        {
            const string Key = "ScriptCompilationDuringPlay";
            if (EditorPrefs.GetInt(Key, 0) != 1) { EditorPrefs.SetInt(Key, 1); Debug.Log("[GK] script changes now recompile after play stops"); }
        }

        [GKCommand("showgame","showgame: un-maximize panels, bring the Game view forward and frame the Scene view on the player")]
        static void Cmd(string arg)
        {
            foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
                if (w != null && w.maximized) w.maximized = false;

            GameObject player = null;
            foreach (var c in Object.FindObjectsByType<Combatant>(FindObjectsSortMode.None))
                if (c.team == Team.Player) { player = c.gameObject; break; }
            var sv = SceneView.lastActiveSceneView;
            if (sv != null)
            {
                Vector3 at = player != null ? player.transform.position + Vector3.up * 1.1f : Vector3.up;
                Quaternion look = player != null ? Quaternion.LookRotation(-player.transform.forward + Vector3.down * 0.25f) : Quaternion.Euler(15f, 0f, 0f);
                sv.LookAt(at, look, 3.2f);
                sv.Repaint();
            }

            var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (gameViewType != null)
            {
                var gv = EditorWindow.GetWindow(gameViewType, false, "Game", true);
                if (gv != null) { gv.Show(); gv.Focus(); gv.Repaint(); }
            }
            GKBridge.Log("showgame: Game view in front, Scene view on " + (player != null ? player.name : "the origin") +
                         ", play mode " + EditorApplication.isPlaying);
        }
    }
}
