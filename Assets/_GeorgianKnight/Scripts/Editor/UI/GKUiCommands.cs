using System.IO;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>Bridge commands for UI checks: the Game view as the player sees it, UI Toolkit panels included
    /// (<c>gameshot</c> renders the camera only, so it never shows the HUD or a menu).</summary>
    public static class GKUiCommands
    {
        [GKCommand("uishot", "uishot <name>: the Game view with every UI panel, to GKBridge/<name>.png (play mode; lands at the end of the frame)")]
        static void UiShot(string arg)
        {
            string name = string.IsNullOrEmpty(arg) ? "uishot" : arg.Split(' ')[0];
            if (!EditorApplication.isPlaying) { GKBridge.Log("uishot: needs play mode"); return; }
            string path = Path.Combine(GKBridge.Dir, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            GKBridge.Log("uishot: queued " + path);
        }

        [GKCommand("ui_nav", "ui_nav <down|up|left|right|submit|back|pause|tab n|lang>: drive the M1 menus without a device (play mode)")]
        static void UiNav(string arg)
        {
            if (!EditorApplication.isPlaying) { GKBridge.Log("ui_nav: needs play mode"); return; }
            GKBridge.Log("ui_nav " + arg + ": " + GeorgianKnight.UI.GKMenus.Drive(arg));
        }
    }
}
