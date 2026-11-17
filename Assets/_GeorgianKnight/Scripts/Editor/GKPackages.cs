using UnityEditor.PackageManager;

namespace GeorgianKnight.EditorTools
{
    /// <summary>Lets a session that edited Packages/manifest.json make the editor resolve it now instead of on the next
    /// focus; the compile and domain reload that follow defer the rest of the batch.</summary>
    public static class GKPackages
    {
        [GKCommand("tools_resolve", "tools_resolve - re-read Packages/manifest.json and install/remove packages now")]
        public static bool Resolve(string arg)
        {
            Client.Resolve();
            GKBridge.Log("tools_resolve: package resolve requested");
            return true;
        }

        /// <summary>Player Settings → Frame Timing Stats; without it FrameTimingManager reports no GPU time (QA perf runs).</summary>
        [GKCommand("tools_frametiming", "tools_frametiming [on|off] - Player Settings Frame Timing Stats; no argument reports it")]
        public static void FrameTiming(string arg)
        {
            arg = arg.Trim();
            if (arg == "on" || arg == "off")
            {
                UnityEditor.PlayerSettings.enableFrameTimingStats = arg == "on";
                UnityEditor.AssetDatabase.SaveAssets();
            }
            GKBridge.Log("tools_frametiming: enableFrameTimingStats=" + UnityEditor.PlayerSettings.enableFrameTimingStats);
        }
    }
}
