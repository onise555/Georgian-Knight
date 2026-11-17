using System.Globalization;
using System.Text;
using GeorgianKnight.Feel;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>Play-mode commands for the Feel layers: trace, status, synthetic moments, and a review capture.</summary>
    public static class GKFeelPlay
    {
        static bool Playing(string cmd)
        {
            if (EditorApplication.isPlaying) return true;
            GKBridge.Log(cmd + ": play mode only");
            return false;
        }

        [GKCommand("feel_trace", "feel_trace on|off: log every Feel moment (kind, heavy, crit, source, target) to warnings.txt")]
        static void Trace(string arg)
        {
            FeelDirector.Trace = arg == null || arg.Trim() != "off";
            GKBridge.Log("feel_trace: " + (FeelDirector.Trace ? "on" : "off"));
        }

        [GKCommand("feel_status", "feel_status: moments received by kind, and what each layer is doing now (play mode)")]
        static void Status(string arg)
        {
            if (!Playing("feel_status")) return;
            var d = FeelDirector.I;
            if (d == null) { GKBridge.Log("feel_status: no FeelDirector in the scene"); return; }
            var sb = new StringBuilder("feel_status: settings=").Append(d.settings != null ? AssetDatabase.GetAssetPath(d.settings) : "none");
            sb.Append(" | moments");
            foreach (FeelKind k in System.Enum.GetValues(typeof(FeelKind))) sb.Append(' ').Append(k).Append('=').Append(d.Counts[(int)k]);
            sb.AppendFormat(CultureInfo.InvariantCulture, " | rumble on={0} low={1:F2} high={2:F2} pad={3}", d.Rumble.Enabled, d.Rumble.Low, d.Rumble.High,
                            UnityEngine.InputSystem.Gamepad.current != null ? UnityEngine.InputSystem.Gamepad.current.displayName : "none");
            sb.AppendFormat(CultureInfo.InvariantCulture, " | flash running={0} drawn={1}", d.Flash.Running, d.Flash.Drawn);
            sb.AppendFormat(CultureInfo.InvariantCulture, " | dust puffs={0}", d.Dust.Puffs);
            sb.AppendFormat(CultureInfo.InvariantCulture, " | pulse ca={0:F2} vig+={1:F2} exp={2:F2}", d.Pulse.Chromatic, d.Pulse.VignetteAdd, d.Pulse.Exposure);
            sb.AppendFormat(" | sound layers played={0}", d.Sound.Played);
            GKBridge.Log(sb.ToString());
        }

        [GKCommand("feel_test", "feel_test hit|heavy|kill|block|guardbreak|parry|execution|step [self]: fire one synthetic moment from the player at the nearest enemy, or at the player (self) (play mode)")]
        static void Test(string arg)
        {
            if (!Playing("feel_test")) return;
            var parts = (arg ?? "hit").Trim().ToLowerInvariant().Split(' ');
            var director = CombatDirector.Existing;
            var player = director != null ? director.Player : null;
            if (player == null) { GKBridge.Log("feel_test: no player"); return; }
            bool self = parts.Length > 1 && parts[1] == "self";
            Combatant target = self ? player : director.FindTarget(player, player.transform.forward, 40f, 180f);
            if (target == null) target = player;
            Vector3 point = target.transform.position + Vector3.up * 1.3f;
            Vector3 dir = target == player ? -player.transform.forward : (target.transform.position - player.transform.position).normalized;
            GameObject src = self ? null : player.gameObject;
            bool heavy = parts[0] == "heavy";
            FeelKind kind;
            switch (parts[0])
            {
                case "hit": case "heavy": kind = FeelKind.Hit; break;
                case "kill": kind = FeelKind.Kill; break;
                case "block": kind = FeelKind.Block; break;
                case "guardbreak": kind = FeelKind.GuardBreak; break;
                case "parry": kind = FeelKind.Parry; break;
                case "execution": kind = FeelKind.Execution; break;
                case "step":
                    FeelHub.Footstep(player.gameObject, player.transform.position + Vector3.up * 0.08f, 5f, true);
                    GKBridge.Log("feel_test: footstep at the player");
                    return;
                default: GKBridge.Log("feel_test: unknown moment " + parts[0]); return;
            }
            FeelHub.Impact(kind, point, dir, src, target.gameObject, heavy, false, !self, target == player);
            GKBridge.Log("feel_test: " + kind + (heavy ? " heavy" : "") + " on " + target.name);
        }
    }
}
