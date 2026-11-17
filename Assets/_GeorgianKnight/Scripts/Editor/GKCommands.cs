using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>Bridge commands for the game's editor tooling (see GKBridge).</summary>
    public static class GKCommands
    {
        [GKCommand("setup", "layers, physics, URP, quality")] static void Setup(string a) { GKSetup.ConfigureProject(); }
        [GKCommand("characters", "humanoid import + materials")] static void Characters(string a) { GKCharacters.PrepareAll(); }
        [GKCommand("environment", "Didgori terrain + sky")] static void Environment(string a) { GKEnvironment.Build(); }
        [GKCommand("preview")] static void Preview(string a) { GKPreview.BuildPreviewScene(); }
        [GKCommand("capture", "capture <name> from the scene camera")] static void Capture(string a) { GKPreview.CaptureFromSceneCamera(string.IsNullOrEmpty(a) ? "preview" : a); }
        [GKCommand("build", "build everything")] static void Build(string a) { GKBuildAll.BuildEverything(); }
        [GKCommand("combat", "attacks, movesets, animator")] static void Combat(string a) { GKCombatBuilder.BuildAll(); }
        [GKCommand("audio", "audio library")] static void Audio(string a) { GKAudioBuilder.Build(); }
        [GKCommand("inspect", "inspect [clip] [time] [nocol]: collider overlays + grip shots")] static void Inspect(string a) { GKInspect.Run(a); }
        [GKCommand("ragdoll", "ragdoll [impulse]: drop test")] static void Ragdoll(string a) { GKInspect.RagdollDrop(a); }
        [GKCommand("game", "prefabs + Didgori scene")] static void Game(string a) { GKGameBuilder.BuildGame(); }
        [GKCommand("attacks")] static void Attacks(string a) { GKEval.Run("attacks"); }
        [GKCommand("eval")] static void Eval(string a) { GKEval.Run(a); }

        [GKCommand("autopilot", "autopilot [report|off]: attach the autopilot to the player / log its swing ledger / remove it (play mode)")]
        static void Autopilot(string a)
        {
            var p = GameObject.FindWithTag("Player");
            if (p == null) { GKBridge.Log("autopilot: no player"); return; }
            var ap = p.GetComponent<GKAutoPilot>();
            a = (a ?? "").Trim();
            if (a == "report") { GKBridge.Log(ap != null ? ap.Report() : "autopilot: not attached"); return; }
            if (a == "off") { if (ap != null) { GKBridge.Log(ap.Report()); Object.Destroy(ap); } return; }
            if (ap == null) p.AddComponent<GKAutoPilot>();
            GKBridge.Log("autopilot: on");
        }

        [GKCommand("exportmixamo", "exportmixamo <modelPath>|<outDir>")]
        static void ExportMixamo(string a)
        {
            var parts = a.Split('|');
            GKExport.ExportForMixamo(parts[0], parts.Length > 1 ? parts[1] : System.IO.Path.Combine(GKBridge.Dir, "mixamo"));
        }
    }
}
