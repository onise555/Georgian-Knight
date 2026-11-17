using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Switches the player's after-animation pose fixes on or off in play mode, to compare the raw retargeted clip
    /// with what the fixes make of it: posefix on | off | report.
    /// </summary>
    public static class GKPoseFixQa
    {
        static readonly System.Type[] Fixes =
        {
            typeof(PoseInertializer), typeof(PostureCorrector), typeof(SwordArmPose), typeof(HandGrip), typeof(AnatomyLimiter)
        };

        [GKCommand("posefix", "posefix on|off|report: enable or disable the player's pose fixes (inertializer, posture, sword arm, hand grip, anatomy limits) (play mode)")]
        static void Cmd(string arg)
        {
            if (!Application.isPlaying) { GKBridge.Log("posefix: play mode only"); return; }
            var p = GameObject.FindWithTag("Player");
            if (p == null) { GKBridge.Log("posefix: no player"); return; }
            string a = string.IsNullOrEmpty(arg) ? "report" : arg.Trim();
            var sb = new System.Text.StringBuilder("posefix:");
            foreach (var t in Fixes)
            {
                var c = p.GetComponent(t) as Behaviour;
                if (c == null) { sb.Append(' ').Append(t.Name).Append("=missing"); continue; }
                if (a == "on") c.enabled = true;
                else if (a == "off") c.enabled = false;
                sb.Append(' ').Append(t.Name).Append('=').Append(c.enabled ? "on" : "off");
            }
            GKBridge.Log(sb.ToString());
        }

        [GKCommand("cliptrim", "cliptrim <Mixamo clip> <first frame> <last frame>: keep only that frame range of the clip (import setting)")]
        static void ClipTrim(string arg)
        {
            var parts = (arg ?? "").Trim().Split(' ');
            if (parts.Length < 3) { GKBridge.Log("cliptrim: cliptrim <clip> <first> <last>"); return; }
            string path = GKPaths.Mixamo + "/" + parts[0] + ".fbx";
            var imp = UnityEditor.AssetImporter.GetAtPath(path) as UnityEditor.ModelImporter;
            if (imp == null) { GKBridge.Log("cliptrim: no clip " + path); return; }
            float first = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            float last = float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            // through the import rule (GKImport), which rebuilds the clip ranges on every reimport
            imp.userData = "trim:" + first.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";end:" + last.ToString(System.Globalization.CultureInfo.InvariantCulture);
            imp.SaveAndReimport();
            GKBridge.Log("cliptrim: " + parts[0] + " frames " + first + "-" + last);
        }

        [GKCommand("keepdrawn", "keepdrawn on|off: stop the player sheathing on his own when no enemy is near, for test takes (play mode)")]
        static void KeepDrawn(string arg)
        {
            var p = Application.isPlaying ? GameObject.FindWithTag("Player") : null;
            var pc = p != null ? p.GetComponent<PlayerController>() : null;
            if (pc == null) { GKBridge.Log("keepdrawn: play mode, player needed"); return; }
            pc.sheatheAfterCalm = (arg ?? "").Trim() == "off" ? 6f : 1e6f;
            GKBridge.Log("keepdrawn: sheathe after calm " + pc.sheatheAfterCalm + " s");
        }

        /// <summary>A 1-damage blow from the front with the given reaction (poise broken, unblockable).</summary>
        public static HitResult HitPlayer(Combatant self, HitReaction reaction)
        {
            var t = self.transform;
            var hit = new HitData
            {
                damage = 1f, poiseDamage = 999f, unblockable = true, reaction = reaction,
                knockback = reaction == HitReaction.Knockdown ? 1.5f : 0.6f,
                point = t.position + t.forward * 0.6f + Vector3.up * 1.2f, direction = -t.forward
            };
            return self.ReceiveHit(hit);
        }

        [GKCommand("hitme", "hitme light|heavy|stagger|knockdown: a 1-damage blow from the front with that reaction, to check hit reactions, falls and get-ups (play mode)")]
        static void HitMe(string arg)
        {
            if (!Application.isPlaying) { GKBridge.Log("hitme: play mode only"); return; }
            var p = GameObject.FindWithTag("Player");
            var self = p != null ? p.GetComponent<Combatant>() : null;
            if (self == null) { GKBridge.Log("hitme: no player"); return; }
            string a = string.IsNullOrEmpty(arg) ? "light" : arg.Trim();
            var reaction = a == "heavy" ? HitReaction.Heavy : a == "stagger" ? HitReaction.Stagger : a == "knockdown" ? HitReaction.Knockdown : HitReaction.Light;
            var result = HitPlayer(self, reaction);
            GKBridge.Log("hitme: " + reaction + " -> " + result + ", state " + self.CurrentState);
        }
    }
}
