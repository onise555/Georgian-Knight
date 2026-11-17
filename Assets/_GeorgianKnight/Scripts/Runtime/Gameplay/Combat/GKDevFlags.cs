#if GK_QA
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Command-line switches for QA builds only: compiled in under the GK_QA scripting define (Build &amp; QA's optimised
    /// release + GK_QA "qa" build, where the frame budgets count); the shipping build has neither the define nor this code.
    /// -gkgod: the hero takes no damage and cannot be executed (Build &amp; QA's autopilot frame-time runs reach the end
    /// of the arena instead of losing before wave 3).
    /// </summary>
    public static class GKDevFlags
    {
        public static bool God { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Read()
        {
            God = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-gkgod") >= 0;
            if (God) Debug.Log("[GK] -gkgod: the hero takes no damage (GK_QA build)");
        }
    }
}
#endif
