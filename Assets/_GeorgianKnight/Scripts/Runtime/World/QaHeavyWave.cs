#if GK_QA
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GeorgianKnight.World
{
    /// <summary>
    /// QA builds only (GK_QA): -gkwave heavy swaps every loaded WaveManager's waves for a heavy test set down the level's
    /// real lanes, so Build &amp; QA's capture measures a ceiling instead of the M1 floor (at most 4 alive). 10 then 12 men
    /// alive at once, warriors and veterans, no hold point (all go for the hero). The shipping waves and the shipping build
    /// are untouched: the define and this code exist only in the qa build. Replaced on scene load, before WaveManager.Start.
    /// </summary>
    public static class QaHeavyWave
    {
        static bool on;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Read()
        {
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-gkwave");
            on = i >= 0 && i + 1 < args.Length && args[i + 1] == "heavy";
            SceneManager.sceneLoaded -= OnLoaded;
            if (on) SceneManager.sceneLoaded += OnLoaded;
        }

        static void OnLoaded(Scene scene, LoadSceneMode mode)
        {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var wm in root.GetComponentsInChildren<WaveManager>(true))
                {
                    var ship = wm.waves;
                    wm.waves = new[]
                    {
                        Heavy(ship, 0, 6, 4, 3f),    // 10 alive
                        Heavy(ship, 1, 8, 4, 6f),    // 12 alive
                    };
                    Debug.Log("[GK] -gkwave heavy: " + wm.name + " in " + scene.name + ": waves of 10 and 12 down lanes 1-3 (GK_QA build)");
                }
        }

        static WaveManager.Wave Heavy(WaveManager.Wave[] ship, int k, int warriors, int veterans, float delay)
        {
            var w = new WaveManager.Wave { warriors = warriors, veterans = veterans, delayBefore = delay, lanes = new[] { 1, 2, 3 } };
            if (ship != null && k < ship.Length) { w.title = ship[k].title; w.titleKey = ship[k].titleKey; }
            return w;
        }
    }
}
#endif
