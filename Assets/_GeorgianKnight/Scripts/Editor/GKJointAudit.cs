using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Measures every arm, neck and spine joint of the hero, in degrees, against what the animations themselves do.
    /// <c>jointaudit</c> samples every clip of the animator on the hero skeleton (edit mode) and records each joint
    /// angle's range and the elbow hinge axes; <c>jointaudit apply</c> also stores them in the prefab's
    /// <see cref="AnatomyLimiter"/>. <c>jointqa start / report</c> tracks the same angles live in play mode, after all
    /// the pose fixes, and flags every joint that leaves the range the clips use.
    /// </summary>
    static class GKJointAudit
    {
        public const string RangesPath = "Assets/_GeorgianKnight/Generated/joint_ranges.txt";
        static readonly System.Globalization.CultureInfo CI = System.Globalization.CultureInfo.InvariantCulture;

        [GKCommand("jointaudit", "jointaudit [apply]: sample every animator clip on the hero, measure every joint angle (deg), store the ranges")]
        static void Audit(string arg)
        {
            if (Application.isPlaying) { GKBridge.Log("jointaudit: stop play mode first"); return; }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKHeroCC.PrefabPath);
            if (prefab == null) { GKBridge.Log("jointaudit: no hero prefab"); return; }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                var lim = inst.GetComponent<AnatomyLimiter>() ?? inst.AddComponent<AnatomyLimiter>();
                lim.Init();
                var anim = inst.GetComponent<Animator>();
                var clips = anim.runtimeAnimatorController.animationClips.Where(c => c != null && anim.isHuman && c.isHumanMotion).Distinct().ToList();
                const int N = 24;
                // pass 1: the elbow hinge axes (mean bend direction, weighted by the bend)
                Vector3 hr = Vector3.zero, hl = Vector3.zero, kr = Vector3.zero, kl = Vector3.zero;
                foreach (var c in clips)
                    for (int i = 0; i <= N; i++)
                    {
                        c.SampleAnimation(inst, c.length * i / N);
                        lim.HingeSample(true, ref hr);
                        lim.HingeSample(false, ref hl);
                        lim.KneeSample(true, ref kr);
                        lim.KneeSample(false, ref kl);
                    }
                lim.right.hinge = hr.normalized; lim.left.hinge = hl.normalized;
                lim.kneeHingeR = kr.normalized; lim.kneeHingeL = kl.normalized;
                // pass 2: the range of every joint angle
                int K = AnatomyLimiter.Names.Length;
                var mn = Enumerable.Repeat(float.MaxValue, K).ToArray();
                var mx = Enumerable.Repeat(float.MinValue, K).ToArray();
                var mnAt = new string[K]; var mxAt = new string[K];
                foreach (var c in clips)
                    for (int i = 0; i <= N; i++)
                    {
                        c.SampleAnimation(inst, c.length * i / N);
                        inst.transform.position = Vector3.zero;   // root motion is not baked into the pose: feet against y = 0
                        var m = lim.Measure();
                        for (int k = 0; k < K; k++)
                        {
                            if (m[k] < mn[k]) { mn[k] = m[k]; mnAt[k] = c.name + "@" + (100 * i / N); }
                            if (m[k] > mx[k]) { mx[k] = m[k]; mxAt[k] = c.name + "@" + (100 * i / N); }
                        }
                    }
                var sb = new StringBuilder();
                sb.Append("hinges " + N3(lim.right.hinge) + " " + N3(lim.left.hinge) + " " + N3(lim.kneeHingeR) + " " + N3(lim.kneeHingeL) + "\n");
                for (int k = 0; k < K; k++)
                    sb.Append(AnatomyLimiter.Names[k] + " " + mn[k].ToString("F1", CI) + " " + mx[k].ToString("F1", CI) + " " + mnAt[k] + " " + mxAt[k] + "\n");
                System.IO.File.WriteAllText(RangesPath, sb.ToString());
                GKBridge.Log("jointaudit: " + clips.Count + " clips x " + (N + 1) + " poses, elbow hinge R " + V(lim.right.hinge) + " L " + V(lim.left.hinge));
                for (int k = 0; k < K; k++)
                    GKBridge.Log("jointaudit:   " + AnatomyLimiter.Names[k].PadRight(16) + mn[k].ToString("F0").PadLeft(5) + " .. " + mx[k].ToString("F0").PadRight(5) + "  (" + mnAt[k] + " / " + mxAt[k] + ")");
                if ((arg ?? "").Trim() == "apply")
                {
                    var root = PrefabUtility.LoadPrefabContents(GKHeroCC.PrefabPath);
                    try
                    {
                        var pl = root.GetComponent<AnatomyLimiter>() ?? root.AddComponent<AnatomyLimiter>();
                        if (ApplyRanges(pl)) PrefabUtility.SaveAsPrefabAsset(root, GKHeroCC.PrefabPath);
                        GKBridge.Log("jointaudit: ranges stored in the hero's AnatomyLimiter");
                    }
                    finally { PrefabUtility.UnloadPrefabContents(root); }
                }
            }
            finally { Object.DestroyImmediate(inst); }
        }

        static string N3(Vector3 v) { return v.x.ToString("F4", CI) + " " + v.y.ToString("F4", CI) + " " + v.z.ToString("F4", CI); }

        /// <summary>Gives a limiter the measured hinges and ranges (from <see cref="RangesPath"/>); false if never measured.</summary>
        public static bool ApplyRanges(AnatomyLimiter pl)
        {
            if (pl == null || !System.IO.File.Exists(RangesPath)) return false;
            var lines = System.IO.File.ReadAllLines(RangesPath);
            if (lines.Length == 0 || !lines[0].StartsWith("hinges ")) return false;
            var h = lines[0].Split(' ').Skip(1).Select(t => float.Parse(t, CI)).ToArray();
            if (h.Length < 12) return false;
            pl.right.hinge = new Vector3(h[0], h[1], h[2]); pl.left.hinge = new Vector3(h[3], h[4], h[5]);
            pl.kneeHingeR = new Vector3(h[6], h[7], h[8]); pl.kneeHingeL = new Vector3(h[9], h[10], h[11]);
            var r = ReadRanges();
            foreach (var side in new[] { "R.", "L." })
            {
                var rg = side == "R." ? pl.right : pl.left;
                Vector2 v;
                if (r.TryGetValue(side + "elbowFlex", out v)) rg.flex = v;
                if (r.TryGetValue(side + "elbowOffHinge", out v)) rg.offHinge = v.y;
                if (r.TryGetValue(side + "forearmRoll", out v)) rg.forearmRoll = v;
                if (r.TryGetValue(side + "wristA", out v)) rg.wristA = v;
                if (r.TryGetValue(side + "wristB", out v)) rg.wristB = v;
                if (r.TryGetValue(side + "wristRoll", out v)) rg.wristRoll = v;
                if (r.TryGetValue(side + "pronation", out v)) rg.pronation = v;
                if (r.TryGetValue(side + "humerusRoll", out v)) rg.humerusRoll = v;
                rg.measured = true;
            }
            return true;
        }

        static string V(Vector3 v) { return "(" + v.x.ToString("F2", CI) + ", " + v.y.ToString("F2", CI) + ", " + v.z.ToString("F2", CI) + ")"; }

        // ------------------------------------------------------------------ live

        static AnatomyLimiter qaLim; static Combatant qaSelf;
        static float[] qaMin, qaMax; static string[] qaMinAt, qaMaxAt; static float qaFix; static string qaFixAt; static int qaFrames;
        static System.Collections.Generic.Dictionary<string, Vector2> qaClip; static System.Collections.Generic.HashSet<string> qaShot; static int qaShots;

        [GKCommand("jointqa", "jointqa start | report: live joint angles (after every pose fix) against the clip ranges (play mode)")]
        static void Live(string arg)
        {
            var p = GameObject.FindWithTag("Player");
            qaLim = p != null ? p.GetComponent<AnatomyLimiter>() : null;
            qaSelf = p != null ? p.GetComponent<Combatant>() : null;
            if (qaLim == null && p != null && Application.isPlaying)
            {
                // a player built without limits (Knight_Clean): measure through one that corrects nothing
                qaLim = p.AddComponent<AnatomyLimiter>();
                qaLim.limit = false;
                ApplyRanges(qaLim);
                GKBridge.Log("jointqa: no AnatomyLimiter on the player, measuring through a non-limiting one");
            }
            if (qaLim == null) { GKBridge.Log("jointqa: no AnatomyLimiter on the player"); return; }
            if ((arg ?? "").Trim() == "start")
            {
                int K = AnatomyLimiter.Names.Length;
                qaMin = Enumerable.Repeat(float.MaxValue, K).ToArray(); qaMax = Enumerable.Repeat(float.MinValue, K).ToArray();
                qaMinAt = new string[K]; qaMaxAt = new string[K]; qaFix = 0f; qaFixAt = ""; qaFrames = 0;
                qaClip = ReadRanges(); qaShot = new System.Collections.Generic.HashSet<string>(); qaShots = 0;
                EditorApplication.update -= Track; EditorApplication.update += Track;
                GKBridge.Log("jointqa: tracking");
                return;
            }
            EditorApplication.update -= Track;
            if (qaMin == null) { GKBridge.Log("jointqa: not started"); return; }
            var clip = ReadRanges();
            int over = 0;
            var sb = new StringBuilder();
            for (int k = 0; k < AnatomyLimiter.Names.Length; k++)
            {
                string n = AnatomyLimiter.Names[k];
                Vector2 c;
                bool has = clip.TryGetValue(n, out c);
                float beyond = has ? Mathf.Max(c.x - qaMin[k], qaMax[k] - c.y) : 0f;
                string flag = beyond > 5f ? "  << " + beyond.ToString("F0") + " deg beyond the clips" : "";
                if (beyond > 5f) over++;
                GKBridge.Log("jointqa:   " + n.PadRight(16) + qaMin[k].ToString("F0").PadLeft(5) + " .. " + qaMax[k].ToString("F0").PadRight(5) +
                             (has ? " clips " + c.x.ToString("F0") + ".." + c.y.ToString("F0") : "") + "  (" + qaMinAt[k] + " / " + qaMaxAt[k] + ")" + flag);
            }
            GKBridge.Log("jointqa: " + qaFrames + " frames, " + over + " joints outside the clip ranges, largest limiter correction " + qaFix.ToString("F0") + " deg (" + qaFixAt + ")");
        }

        static void Track()
        {
            if (!Application.isPlaying || qaLim == null) { EditorApplication.update -= Track; return; }
            if (EditorApplication.isPaused) return;
            var m = qaLim.Measure();
            string at = qaSelf != null ? qaSelf.CurrentState + (qaSelf.IsBlocking ? "+block" : "") : "?";
            for (int k = 0; k < m.Length; k++)
            {
                if (m[k] < qaMin[k]) { qaMin[k] = m[k]; qaMinAt[k] = at; }
                if (m[k] > qaMax[k]) { qaMax[k] = m[k]; qaMaxAt[k] = at; }
                // the first time a joint goes well past what any clip does: photograph it
                Vector2 c;
                string n = AnatomyLimiter.Names[k];
                if (qaShots < 8 && qaClip != null && qaClip.TryGetValue(n, out c) && (m[k] < c.x - 15f || m[k] > c.y + 15f) && qaShot.Add(n))
                {
                    qaShots++;
                    string tag = "jq_" + n.Replace(".", "_") + "_" + m[k].ToString("F0");
                    Transform t = qaLim.transform;
                    Vector3 look = t.position + Vector3.up * (n.Contains("knee") || n.Contains("ankle") || n.Contains("hip") || n.Contains("toe") || n.Contains("heel") ? 0.55f : 1.3f);
                    Shot(tag + "_a", look + t.forward * 2.2f + t.right * 0.9f + Vector3.up * 0.1f, look);
                    Shot(tag + "_b", look - t.right * 2.3f + t.forward * 0.3f + Vector3.up * 0.1f, look);
                    GKBridge.Log("jointqa: " + n + " " + m[k].ToString("F0") + " deg (clips " + c.x.ToString("F0") + ".." + c.y.ToString("F0") + ") in " + at + " -> " + tag + "_a/_b.png");
                }
            }
            if (qaLim.LastCorrection > qaFix) { qaFix = qaLim.LastCorrection; qaFixAt = at; }
            qaFrames++;
        }

        static void Shot(string name, Vector3 pos, Vector3 look)
        {
            var go = new GameObject("~jqshot");
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.nearClipPlane = 0.03f; cam.farClipPlane = 3000f; cam.fieldOfView = 36f;
                UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(cam).renderPostProcessing = true;
                go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(look - pos, Vector3.up));
                GKPreview.Capture(cam, name, 960, 720);
            }
            finally { Object.DestroyImmediate(go); }
        }

        static System.Collections.Generic.Dictionary<string, Vector2> ReadRanges()
        {
            var d = new System.Collections.Generic.Dictionary<string, Vector2>();
            if (!System.IO.File.Exists(RangesPath)) return d;
            foreach (var line in System.IO.File.ReadAllLines(RangesPath))
            {
                var p = line.Split(' ');
                float a, b;
                if (p.Length >= 3 && float.TryParse(p[1], System.Globalization.NumberStyles.Float, CI, out a) && float.TryParse(p[2], System.Globalization.NumberStyles.Float, CI, out b))
                    d[p[0]] = new Vector2(a, b);
            }
            return d;
        }
    }
}
