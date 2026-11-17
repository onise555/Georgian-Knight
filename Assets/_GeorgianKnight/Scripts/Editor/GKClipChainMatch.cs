using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Where a chained attack should begin so the arm does not jump (the f548 jerk: C_L2_Downward cancelled at ~0.74 into
    /// C_L3_Combo's frame 0, whose sword hand is elsewhere: the blade starts 4 cm inside the shield and the solver escapes
    /// 21 deg in a frame). On the hero probe, the outgoing attack is sampled at its exit and the next one frame by frame
    /// over its first part: the sword hand's distance (root space) and the blade's direction difference per frame, and the
    /// frame that matches best. Edit mode; nothing is changed.
    /// </summary>
    public static class GKClipChainMatch
    {
        [GKCommand("chainmatch", "chainmatch <fromAttack> <toAttack> [exit 0-1, default its comboWindow] [search 0-1, default 0.4]: best entry frame of a chained attack (edit mode)")]
        static void Cmd(string arg)
        {
            var p = (arg ?? "").Trim().Split(' ');
            if (p.Length < 2) { GKBridge.Log("chainmatch: give two attack asset names, e.g. C_L2_Downward C_L3_Combo"); return; }
            var from = AssetDatabase.LoadAssetAtPath<AttackData>(GKPaths.Data + "/Attacks/" + p[0] + ".asset");
            var to = AssetDatabase.LoadAssetAtPath<AttackData>(GKPaths.Data + "/Attacks/" + p[1] + ".asset");
            if (from == null || to == null || from.clip == null || to.clip == null) { GKBridge.Log("chainmatch: attack or clip missing"); return; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            float exit = p.Length > 2 ? float.Parse(p[2], inv) : from.comboWindow;
            float search = p.Length > 3 ? float.Parse(p[3], inv) : 0.4f;
            var probe = GKCharacters.InstantiateKnightVisual();
            try
            {
                var anim = probe.GetComponentInChildren<Animator>();
                if (anim == null || !anim.isHuman) { GKBridge.Log("chainmatch: the probe has no humanoid animator"); return; }
                Transform root = anim.transform, hand = anim.GetBoneTransform(HumanBodyBones.RightHand);
                Transform tip = probe.GetComponentsInChildren<Transform>(true) is Transform[] all ? System.Array.Find(all, t => t.name == "BladeTip") : null;
                Vector3 h0, b0;
                Pose(anim.gameObject, from.clip, exit, root, hand, tip, out h0, out b0);
                int frames = Mathf.Max(2, Mathf.RoundToInt(to.clip.length * to.clip.frameRate * search));
                float best = float.MaxValue; int bestF = 0;
                var sb = new StringBuilder();
                for (int f = 0; f <= frames; f++)
                {
                    float n = f / (to.clip.length * to.clip.frameRate);
                    Vector3 h, b;
                    Pose(anim.gameObject, to.clip, n, root, hand, tip, out h, out b);
                    float dh = Vector3.Distance(h, h0) * 100f, db = Vector3.Angle(b, b0);
                    float score = dh + db * 0.5f;            // 1 cm of the hand ~ 2 deg of the blade
                    if (score < best) { best = score; bestF = f; }
                    if (f % 3 == 0 || f == frames) sb.Append(" | f").Append(f).Append(" (").Append(n.ToString("F2", inv)).Append(") hand ").Append(dh.ToString("F0")).Append(" cm blade ").Append(db.ToString("F0")).Append(" deg");
                }
                float bn = bestF / (to.clip.length * to.clip.frameRate);
                Vector3 hb, bb; Pose(anim.gameObject, to.clip, bn, root, hand, tip, out hb, out bb);
                GKBridge.Log("chainmatch " + p[0] + " (" + from.clip.name + " at " + exit.ToString("F2", inv) + ") -> " + p[1] + " (" + to.clip.name + "): best entry f" + bestF +
                             " (" + bn.ToString("F2", inv) + ", " + (bestF / to.clip.frameRate).ToString("F2", inv) + " s): hand " + (Vector3.Distance(hb, h0) * 100f).ToString("F0") +
                             " cm, blade " + Vector3.Angle(bb, b0).ToString("F0") + " deg (frame 0: see list); its hitStart " + to.hitStart.ToString("F2", inv) + sb);
            }
            finally { Object.DestroyImmediate(probe); }
        }

        /// <summary>
        /// Every cut clip in the library ranked as the next blow after an attack's exit (Team Lead, f548): its first cut
        /// (the blade tip's speed in the hips' frame passing 3 m/s), that cut's direction, and its best entry frame before
        /// the cut: the sword hand's distance and the blade's angle to the exit pose. Pass bar: hand <= 20 cm, blade <=
        /// 20 deg. Edit mode; nothing is changed.
        /// </summary>
        [GKCommand("chainrank", "chainrank <fromAttack> [exit 0-1]: every cut clip ranked as the next blow after it (hand <= 20 cm, blade <= 20 deg at an entry before its first cut; edit mode)")]
        static void Rank(string arg)
        {
            var p = (arg ?? "").Trim().Split(' ');
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var from = AssetDatabase.LoadAssetAtPath<AttackData>(GKPaths.Data + "/Attacks/" + p[0] + ".asset");
            if (from == null || from.clip == null) { GKBridge.Log("chainrank: no attack " + p[0]); return; }
            float exit = p.Length > 1 ? float.Parse(p[1], inv) : from.comboWindow;
            var cut = new System.Text.RegularExpressions.Regex("slash|attack|combo|cut|strike|thrust|swing|stab|uppercut", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var skip = new System.Text.RegularExpressions.Regex("kick|jump|block|torch|dual|run_|to_|death|hit_|react|crouch_idle", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var probe = GKCharacters.InstantiateKnightVisual();
            var rows = new System.Collections.Generic.List<KeyValuePair<float, string>>();
            try
            {
                var anim = probe.GetComponentInChildren<Animator>();
                Transform root = anim.transform, hand = anim.GetBoneTransform(HumanBodyBones.RightHand);
                var all = probe.GetComponentsInChildren<Transform>(true);
                Transform tip = System.Array.Find(all, t => t.name == "BladeTip");
                Vector3 h0, b0;
                Pose(anim.gameObject, from.clip, exit, root, hand, tip, out h0, out b0);
                foreach (var g in AssetDatabase.FindAssets("t:AnimationClip", new[] { GKPaths.Mixamo }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(g);
                    foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                    {
                        var c = o as AnimationClip;
                        if (c == null || c.name.StartsWith("__preview") || !cut.IsMatch(c.name) || skip.IsMatch(c.name) || c == from.clip) continue;
                        int n = Mathf.RoundToInt(c.length * c.frameRate);
                        if (n < 6) continue;
                        // the first cut: the tip in the hips' frame passing 3 m/s
                        Vector3 prevTip = Vector3.zero; int first = -1; Vector3 dir = Vector3.zero;
                        var tips = new Vector3[n + 1];
                        for (int f = 0; f <= n; f++)
                        {
                            c.SampleAnimation(anim.gameObject, f / c.frameRate);
                            Transform hips = anim.GetBoneTransform(HumanBodyBones.Hips);
                            Vector3 fwd = Vector3.ProjectOnPlane(hips.forward, Vector3.up);
                            Quaternion q = Quaternion.Inverse(Quaternion.LookRotation(fwd.sqrMagnitude > 1e-4f ? fwd : Vector3.forward, Vector3.up));
                            tips[f] = q * ((tip != null ? tip.position : hand.position) - hips.position);
                            if (f > 0 && first < 0 && (tips[f] - tips[f - 1]).magnitude * c.frameRate > 3f) first = f;
                        }
                        if (first < 0) continue;
                        for (int f = first; f <= Mathf.Min(n, first + 3); f++) dir += tips[f] - tips[Mathf.Max(0, f - 1)];
                        string way = Mathf.Abs(dir.y) > Mathf.Max(Mathf.Abs(dir.x), Mathf.Abs(dir.z)) ? (dir.y < 0f ? "downward" : "upward")
                                   : Mathf.Abs(dir.x) > Mathf.Abs(dir.z) ? (dir.x < 0f ? "to the left" : "to the right") : "thrust";
                        float best = float.MaxValue; int bf = 0; float bh = 0f, bb = 0f;
                        for (int f = 0; f < first; f++)
                        {
                            Vector3 h, b;
                            Pose(anim.gameObject, c, (float)f / n, root, hand, tip, out h, out b);
                            float dh = Vector3.Distance(h, h0) * 100f, db = Vector3.Angle(b, b0);
                            float s = dh + db * 0.5f;
                            if (s < best) { best = s; bf = f; bh = dh; bb = db; }
                        }
                        bool pass = bh <= 20f && bb <= 20f;
                        rows.Add(new KeyValuePair<float, string>(best, (pass ? "PASS " : "") + c.name + ": entry f" + bf + " (" + ((float)bf / n).ToString("F2", inv) + ") hand " +
                                 bh.ToString("F0") + " cm blade " + bb.ToString("F0") + " deg; first cut f" + first + " (" + ((float)first / n).ToString("F2", inv) + ") " + way));
                    }
                }
            }
            finally { Object.DestroyImmediate(probe); }
            rows.Sort((a, b) => a.Key.CompareTo(b.Key));
            var sb = new StringBuilder("chainrank after " + p[0] + " (" + from.clip.name + " at " + exit.ToString("F2", inv) + "), " + rows.Count + " cut clips, best first:");
            for (int i = 0; i < rows.Count && i < 12; i++) sb.Append(" | ").Append(rows[i].Value);
            GKBridge.Log(sb.ToString());
        }

        static void Pose(GameObject go, AnimationClip clip, float n, Transform root, Transform hand, Transform tip, out Vector3 handLocal, out Vector3 bladeDir)
        {
            clip.SampleAnimation(go, Mathf.Clamp01(n) * clip.length);
            // measured from the hips, in their facing (flat): a humanoid sample carries the clip's own root travel, so the
            // root is no reference (1.1-1.6 m read between two standing poses); in play the root motion is the transform's
            var anim = go.GetComponent<Animator>();
            Transform hips = anim != null ? anim.GetBoneTransform(HumanBodyBones.Hips) : root;
            Vector3 fwd = Vector3.ProjectOnPlane(hips.forward, Vector3.up);
            Quaternion inv = Quaternion.Inverse(Quaternion.LookRotation(fwd.sqrMagnitude > 1e-4f ? fwd : Vector3.forward, Vector3.up));
            handLocal = inv * (hand.position - hips.position);
            bladeDir = (inv * (tip != null ? tip.position - hand.position : hand.forward)).normalized;
        }
    }
}
