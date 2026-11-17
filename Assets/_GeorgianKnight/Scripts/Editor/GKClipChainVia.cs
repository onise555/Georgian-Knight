using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Option (d) for the f548 jerk (Team Lead): an authored transition clip from one cut's exit into the next. No library
    /// clip meets C_L2_Downward's exit (chainrank: 0 of 29 within 20 cm / 20 deg) and a runtime IK over the arm fought the
    /// animation and the wrist solver (bridge v1, v2). So the transition is keyed: the outgoing clip's muscles at its exit
    /// carried into the next clip's entry by a cubic that leaves with the exit's own velocity and arrives with the entry's,
    /// then the next clip as authored. The body keeps its place through the transition (no root travel of its own) and the
    /// next clip's travel follows on from there.
    /// A smooth path between the two poses is still a blend of them, and the old crossfade's went through the shield: on
    /// the clean hero every transition frame is measured as the game measures it (its own blade solver against the body
    /// colliders and the shield plate, the film's bladeInBodyCm), and where the blade comes within
    /// <see cref="Clearance"/> a middle key pushes the sword hand out along the solver's contact normal, with margin, the
    /// ends keeping their velocity match (Team Lead's conditions 1-2; the check agrees with play on the old crossfade).
    /// Our own keys from our own Mixamo clips: the licence is the source clips'. Written to Animations/Generated/[name].anim;
    /// the builder plays it as C_L3_Combo_Via while AttackData.UseVia is on.
    /// </summary>
    public static class GKClipChainVia
    {
        const float Fps = 60f;
        const float Clearance = 0.02f, Margin = 0.035f;
        public const string DefaultName = "GK_Chain_L2L3";
        public static string PathOf(string name) { return GKPaths.Root + "/Animations/Generated/" + name + ".anim"; }

        /// <summary>The normalized end of the transition, stored with the clip by 'chainvia build' (0 = none).</summary>
        public static float ViaEnd(string name)
        {
            var imp = AssetImporter.GetAtPath(PathOf(name));
            if (imp == null || string.IsNullOrEmpty(imp.userData)) return 0f;
            foreach (var kv in imp.userData.Split(';'))
                if (kv.StartsWith("viaEnd:"))
                {
                    float v;
                    if (float.TryParse(kv.Substring(7), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
                }
            return 0f;
        }

        [GKCommand("chainvia", "chainvia on|off (play: the A/B switch) | ft <fromAttack> <toAttack>: re-key the clip's wind-up off the head and its follow-through out of the shield into the next attack (GK_SS_Downward_Slash_FT) | scan <fromAttack> <toAttack> [from 0-1] [to 0-1]: its exit frames clear of body and shield (2 cm) | build <fromAttack> <toAttack> <exit 0-1> <entry 0-1> <seconds> [exit velocity 0-1, default 1] [nomid] [name] (edit mode): the authored chain transition, its shield clearance per frame")]
        static void Cmd(string arg)
        {
            var p = new List<string>((arg ?? "").Trim().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries));
            if (p.Count == 0 || p[0] == "on" || p[0] == "off")
            {
                AttackData.UseVia = p.Count == 0 || p[0] == "on";
                GKBridge.Log("chainvia: " + (AttackData.UseVia ? "on" : "off"));
                return;
            }
            if (p[0] == "scan" && p.Count >= 3)
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("chainvia: stop play mode first"); return; }
                var fa = AssetDatabase.LoadAssetAtPath<AttackData>(GKPaths.Data + "/Attacks/" + p[1] + ".asset");
                var ta = AssetDatabase.LoadAssetAtPath<AttackData>(GKPaths.Data + "/Attacks/" + p[2] + ".asset");
                if (fa == null || ta == null || fa.clip == null || ta.clip == null) { GKBridge.Log("chainvia: attack or clip missing"); return; }
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                Scan(fa, ta, p.Count > 3 ? float.Parse(p[3], ci) : -1f, p.Count > 4 ? float.Parse(p[4], ci) : -1f);
                return;
            }
            if (p[0] == "ft" && p.Count >= 3)
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("chainvia: stop play mode first"); return; }
                var fa = AssetDatabase.LoadAssetAtPath<AttackData>(GKPaths.Data + "/Attacks/" + p[1] + ".asset");
                var ta = AssetDatabase.LoadAssetAtPath<AttackData>(GKPaths.Data + "/Attacks/" + p[2] + ".asset");
                var src0 = fa != null ? GKPreview.Clip(fa.state == FtName ? "SS_Downward_Slash" : fa.state) : null;
                if (fa == null || ta == null || src0 == null || ta.clip == null) { GKBridge.Log("chainvia ft: attack or clip missing"); return; }
                FollowThrough(src0, fa, ta);
                return;
            }
            if (p[0] != "build" || p.Count < 6) { GKBridge.Log("chainvia: build <fromAttack> <toAttack> <exit> <entry> <seconds> [velocity] [nomid] [name]"); return; }
            if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("chainvia: stop play mode first"); return; }
            bool mid = !p.Remove("nomid");
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var from = AssetDatabase.LoadAssetAtPath<AttackData>(GKPaths.Data + "/Attacks/" + p[1] + ".asset");
            var to = AssetDatabase.LoadAssetAtPath<AttackData>(GKPaths.Data + "/Attacks/" + p[2] + ".asset");
            if (from == null || to == null || from.clip == null || to.clip == null) { GKBridge.Log("chainvia: attack or clip missing"); return; }
            float exit = float.Parse(p[3], inv), entry = float.Parse(p[4], inv), T = Mathf.Max(2f / Fps, float.Parse(p[5], inv));
            float w = p.Count > 6 ? Mathf.Clamp01(float.Parse(p[6], inv)) : 1f;
            string name = p.Count > 7 ? p[7] : DefaultName;
            Build(from, to, exit, entry, T, w, mid, name);
        }

        static float D(AnimationCurve c, float t, float len)
        {
            const float h = 1f / 120f;
            float t0 = Mathf.Max(0f, t - h), t1 = Mathf.Min(len, t + h);
            return t1 > t0 ? (c.Evaluate(t1) - c.Evaluate(t0)) / (t1 - t0) : 0f;
        }

        static float Hermite(float p0, float m0, float p1, float m1, float u)
        {
            float u2 = u * u, u3 = u2 * u;
            return (2f * u3 - 3f * u2 + 1f) * p0 + (u3 - 2f * u2 + u) * m0 + (-2f * u3 + 3f * u2) * p1 + (u3 - u2) * m1;
        }

        static float HermiteSlope(float p0, float m0, float p1, float m1, float u)
        {
            float u2 = u * u;
            return (6f * u2 - 6f * u) * p0 + (3f * u2 - 4f * u + 1f) * m0 + (-6f * u2 + 6f * u) * p1 + (3f * u2 - 2f * u) * m1;
        }

        static bool IsSwordArm(string prop)
        {
            return prop.StartsWith("Right Shoulder") || prop.StartsWith("Right Arm") || prop.StartsWith("Right Forearm") || prop.StartsWith("Right Hand ");
        }

        /// <summary>One curve's transition: the ends (value, velocity) and any middle keys, piecewise Hermite.</summary>
        class Seg
        {
            public float p0, v0, p1, v1;
            public readonly List<float> kt = new List<float>(), kp = new List<float>(), kv = new List<float>();
            public void Ends(float T)
            {
                if (kt.Count == 0) { kt.Add(0f); kp.Add(p0); kv.Add(v0); kt.Add(T); kp.Add(p1); kv.Add(v1); }
                kp[0] = p0; kv[0] = v0; kp[kp.Count - 1] = p1; kv[kv.Count - 1] = v1;
            }
            /// <summary>A key at <paramref name="tk"/> (replacing one there); interior velocities Catmull-Rom.</summary>
            public void Key(float tk, float pk)
            {
                int i = kt.FindIndex(x => Mathf.Abs(x - tk) < 1e-4f);
                if (i >= 0) kp[i] = pk;
                else { i = kt.FindIndex(x => x > tk); kt.Insert(i, tk); kp.Insert(i, pk); kv.Insert(i, 0f); }
                for (int k = 1; k < kt.Count - 1; k++) kv[k] = (kp[k + 1] - kp[k - 1]) / (kt[k + 1] - kt[k - 1]);
            }
        }

        static float Eval(Seg s, float T, float t)
        {
            s.Ends(T);
            int i = 0;
            while (i < s.kt.Count - 2 && t > s.kt[i + 1]) i++;
            float d = s.kt[i + 1] - s.kt[i];
            return Hermite(s.kp[i], s.kv[i] * d, s.kp[i + 1], s.kv[i + 1] * d, Mathf.Clamp01((t - s.kt[i]) / d));
        }

        // ---- the clean hero, posed from muscle values, and its blade against its own body and shield ----
        class Probe
        {
            public GameObject go; public Animator anim; public HumanPoseHandler hph; public HumanPose pose;
            public WeaponSelfCollision solver; public Vector3 wPos; public Quaternion wRot;
            public Transform upper, lower, hand, hips, foreL;
            public readonly Dictionary<string, int> muscle = new Dictionary<string, int>();
        }

        static Probe MakeProbe()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKHeroCC.CleanPrefabPath);
            if (prefab == null) return null;
            var pr = new Probe { go = (GameObject)PrefabUtility.InstantiatePrefab(prefab) };
            pr.go.transform.SetPositionAndRotation(new Vector3(0f, -500f, 0f), Quaternion.identity);
            foreach (var mb in pr.go.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false;
            pr.anim = pr.go.GetComponent<Animator>();
            if (pr.anim == null || !pr.anim.isHuman) { Object.DestroyImmediate(pr.go); return null; }
            pr.anim.applyRootMotion = false;
            pr.hph = new HumanPoseHandler(pr.anim.avatar, pr.anim.transform);
            pr.pose = new HumanPose();
            pr.hph.GetHumanPose(ref pr.pose);
            foreach (var s in pr.go.GetComponentsInChildren<WeaponSelfCollision>(true)) if (!s.leftHand) pr.solver = s;
            pr.upper = pr.anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
            pr.lower = pr.anim.GetBoneTransform(HumanBodyBones.RightLowerArm);
            pr.hand = pr.anim.GetBoneTransform(HumanBodyBones.RightHand);
            pr.hips = pr.anim.GetBoneTransform(HumanBodyBones.Hips);
            pr.foreL = pr.anim.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            for (int i = 0; i < HumanTrait.MuscleCount; i++) pr.muscle[CurveName(HumanTrait.MuscleName[i])] = i;
            if (pr.solver != null && pr.solver.weapon != null) { pr.solver.Init(); pr.wPos = pr.solver.weapon.localPosition; pr.wRot = pr.solver.weapon.localRotation; }
            return pr;
        }

        static string CurveName(string muscle)
        {
            foreach (var side in new[] { "Left", "Right" })
                foreach (var f in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
                {
                    string pre = side + " " + f + " ";
                    if (!muscle.StartsWith(pre)) continue;
                    string rest = muscle.Substring(pre.Length);
                    return side + "Hand." + f + "." + rest;
                }
            return muscle;
        }

        static void SetPose(Probe pr, System.Func<string, float> val)
        {
            var m = pr.pose.muscles;
            foreach (var kv in pr.muscle) m[kv.Value] = val(kv.Key);
            pr.pose.bodyPosition = new Vector3(val("RootT.x"), val("RootT.y"), val("RootT.z"));
            var q = new Quaternion(val("RootQ.x"), val("RootQ.y"), val("RootQ.z"), val("RootQ.w"));
            float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            pr.pose.bodyRotation = n > 1e-6f ? new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n) : Quaternion.identity;
            pr.hph.SetHumanPose(ref pr.pose);
        }

        /// <summary>
        /// The blade in the body or the shield as the game measures it (the film's bladeInBodyCm): the hero's own blade
        /// solver against its body colliders and the shield plate. <paramref name="solve"/>: the solver corrects first, as
        /// it does in play outside a transition; otherwise the clip's blade as it is (the transition, solver standing
        /// down). <paramref name="inflate"/> widens the blade (a clearance bar). Depth in m (0 = clear).
        /// </summary>
        static float Pen(Probe pr, bool solve, float inflate, out string into, out Vector3 push)
        {
            into = ""; push = Vector3.up;
            var s = pr.solver;
            if (s == null) return float.NaN;
            if (solve) s.Solve(1f / Fps);
            else if (s.weapon != null) { s.weapon.localPosition = pr.wPos; s.weapon.localRotation = pr.wRot; }   // the clip's grip, no correction
            float r0 = s.bladeRadius; s.bladeRadius = r0 + inflate;
            float d = s.MeasureNow(out into, out push);
            s.bladeRadius = r0;
            return d;
        }

        static void ResetSolver(Probe pr)
        {
            if (pr.solver == null) return;
            var m = typeof(WeaponSelfCollision).GetMethod("OnEnable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (m != null) m.Invoke(pr.solver, null);
        }

        /// <summary>Two-bone IK: the sword hand to <paramref name="target"/>, its rotation kept, the elbow on the side the
        /// pose bends it.</summary>
        static void Reach(Probe pr, Vector3 target)
        {
            Quaternion rot = pr.hand.rotation;
            Vector3 a = pr.upper.position, b = pr.lower.position, c = pr.hand.position;
            float la = Vector3.Distance(a, b), lb = Vector3.Distance(b, c);
            Vector3 at = target - a;
            float d = Mathf.Clamp(at.magnitude, Mathf.Abs(la - lb) + 1e-3f, la + lb - 1e-3f);
            Vector3 dir = at.sqrMagnitude > 1e-8f ? at.normalized : (c - a).normalized;
            Vector3 pole = b - a - Vector3.Project(b - a, (c - a).normalized);
            if (pole.sqrMagnitude < 1e-6f) pole = Vector3.Cross(dir, Vector3.up);
            float cosA = Mathf.Clamp((la * la + d * d - lb * lb) / (2f * la * d), -1f, 1f);
            Vector3 bendAxis = Vector3.Cross(dir, pole).normalized;
            Vector3 elbow = a + Quaternion.AngleAxis(-Mathf.Acos(cosA) * Mathf.Rad2Deg, bendAxis) * dir * la;
            pr.upper.rotation = Quaternion.FromToRotation(b - a, elbow - a) * pr.upper.rotation;
            b = pr.lower.position; c = pr.hand.position;
            pr.lower.rotation = Quaternion.FromToRotation(c - b, (a + dir * d) - b) * pr.lower.rotation;
            pr.hand.rotation = rot;
        }

        /// <summary>
        /// (c2): every frame of the outgoing attack from its window's end to its end, as the clip has it (no solver): its
        /// blade within 2 cm of the body or the shield or not, whether the cut is complete there (past its combo window,
        /// which the builder holds at the follow-through or later), and the sword hand's and blade's distance to the next
        /// attack's first frame (hips frame). The runs of clear frames are what a chain could leave from.
        /// </summary>
        static void Scan(AttackData fromAtk, AttackData toAtk, float u0 = -1f, float u1 = -1f)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var pr = MakeProbe();
            if (pr == null || pr.solver == null) { GKBridge.Log("chainvia scan: no clean hero or blade solver"); if (pr != null) Object.DestroyImmediate(pr.go); return; }
            try
            {
                AnimationClip A = fromAtk.clip, B = toAtk.clip;
                System.Func<AnimationClip, float, System.Func<string, float>> pose = (c, tt) => prop =>
                {
                    var cv = AnimationUtility.GetEditorCurve(c, EditorCurveBinding.FloatCurve("", typeof(Animator), prop));
                    return cv != null ? cv.Evaluate(tt) : (prop == "RootQ.w" ? 1f : 0f);
                };
                Transform tip = pr.solver.bladeTip != null ? pr.solver.bladeTip : pr.hand;
                System.Func<Vector3[]> local = () =>
                {
                    Vector3 f = Vector3.ProjectOnPlane(pr.hips.forward, Vector3.up);
                    Quaternion iq = Quaternion.Inverse(Quaternion.LookRotation(f.sqrMagnitude > 1e-4f ? f : Vector3.forward, Vector3.up));
                    return new[] { iq * (pr.hand.position - pr.hips.position), (iq * (tip.position - pr.hand.position)).normalized };
                };
                SetPose(pr, pose(B, 0f)); var b0 = local();
                int n0 = Mathf.FloorToInt((u0 >= 0f ? u0 : fromAtk.hitEnd) * A.length * Fps), n1 = Mathf.CeilToInt((u1 >= 0f ? u1 : Mathf.Max(fromAtk.end, fromAtk.comboWindow)) * A.length * Fps);
                var sb = new StringBuilder(); var runs = new StringBuilder();
                int runStart = -1, clearN = 0, completeClearN = 0;
                for (int f = n0; f <= n1 + 1; f++)
                {
                    bool last = f > n1;
                    float tt = Mathf.Min(A.length, f / Fps), u = tt / A.length;
                    bool clear = false; string into = ""; float d = 0f, dh = 0f, db = 0f;
                    if (!last)
                    {
                        SetPose(pr, pose(A, tt));
                        Vector3 push; d = Pen(pr, false, Clearance, out into, out push);
                        clear = d <= 0f;
                        var h = local(); dh = Vector3.Distance(h[0], b0[0]) * 100f; db = Vector3.Angle(h[1], b0[1]);
                        if (clear) { clearN++; if (u >= fromAtk.comboWindow) completeClearN++; }
                        if ((f - n0) % 2 == 0)
                            sb.Append(" | ").Append(u.ToString("F2", inv)).Append(u >= fromAtk.comboWindow ? "" : "*").Append(clear ? " clear" : " " + (d * 100f).ToString("F0") + "cm " + into)
                              .Append(" hand ").Append(dh.ToString("F0")).Append(" blade ").Append(db.ToString("F0"));
                    }
                    if (clear && runStart < 0) runStart = f;
                    if ((!clear || last) && runStart >= 0)
                    {
                        runs.Append(" ").Append((runStart / Fps / A.length).ToString("F2", inv)).Append("-").Append(((f - 1) / Fps / A.length).ToString("F2", inv));
                        runStart = -1;
                    }
                }
                GKBridge.Log("chainvia scan " + fromAtk.name + " (" + A.name + ", window ends " + fromAtk.hitEnd.ToString("F2", inv) + ", combo window " + fromAtk.comboWindow.ToString("F2", inv) +
                             ", end " + fromAtk.end.ToString("F2", inv) + ") into " + toAtk.name + " f0: clear of body and shield by 2 cm (no solver) " + clearN + " frames, " + completeClearN +
                             " with the cut complete; clear runs:" + (runs.Length > 0 ? runs.ToString() : " none") + " (* = before the combo window)" + sb);
            }
            finally { Object.DestroyImmediate(pr.go); }
        }

        public const string FtName = "GK_SS_Downward_Slash_FT";

        static bool IsArmOrUpper(string p)
        {
            foreach (var s in new[] { "Right Shoulder", "Right Arm", "Right Forearm", "Right Hand ", "Left Shoulder", "Left Arm", "Left Forearm", "Left Hand ", "Chest ", "UpperChest " })
                if (p.StartsWith(s)) return true;
            return false;
        }

        /// <summary>A local muscle change: zero outside tc +- h, a raised cosine inside (C1 at its ends).</summary>
        class Bump { public float tc, h; public readonly Dictionary<string, float> d = new Dictionary<string, float>(); }
        static float K(Bump b, float u) { float x = (u - b.tc) / b.h; return Mathf.Abs(x) >= 1f ? 0f : 0.5f * (1f + Mathf.Cos(Mathf.PI * x)); }

        static void FollowThrough(AnimationClip A, AttackData fromAtk, AttackData toAtk)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            const float W0 = 0.20f, W1 = 0.27f, F0 = 0.54f, In = 0.62f, Hold = 0.80f, Out = 0.95f;
            float len = A.length;
            AnimationClip B = toAtk.clip;
            var props = new List<string>();
            foreach (var b in AnimationUtility.GetCurveBindings(A))
                if (b.type == typeof(Animator) && !b.propertyName.StartsWith("Motion")) props.Add(b.propertyName);
            System.Func<AnimationClip, string, AnimationCurve> curve = (c, prop) => AnimationUtility.GetEditorCurve(c, EditorCurveBinding.FloatCurve("", typeof(Animator), prop));
            var orig = new Dictionary<string, AnimationCurve>(); foreach (var p in props) orig[p] = curve(A, p);
            // the follow-through's aim: L3's first frame, arms and upper spine only
            var aim = new Dictionary<string, float>();
            foreach (var p in props) { if (!IsArmOrUpper(p)) continue; var cb = curve(B, p); if (cb != null) aim[p] = cb.Evaluate(0f); }
            System.Func<float, float> wFollow = u => u <= F0 ? 0f : u < In ? Mathf.SmoothStep(0f, 1f, (u - F0) / (In - F0)) : u <= Hold ? 1f : u < Out ? Mathf.SmoothStep(1f, 0f, (u - Hold) / (Out - Hold)) : 0f;
            var bumps = new List<Bump>();
            System.Func<string, float, float> V = (prop, u) =>
            {
                float v = orig[prop].Evaluate(u * len);
                if (!IsArmOrUpper(prop)) return v;
                float a; if (aim.TryGetValue(prop, out a)) v += wFollow(u) * (a - v);
                foreach (var b in bumps) { float dv; if (b.d.TryGetValue(prop, out dv)) v += dv * K(b, u); }
                return Mathf.Clamp(v, -1f, 1f);   // a muscle's own range
            };
            var pr = MakeProbe();
            if (pr == null || pr.solver == null) { GKBridge.Log("chainvia ft: no clean hero or blade solver"); if (pr != null) Object.DestroyImmediate(pr.go); return; }
            var report = new StringBuilder();
            try
            {
                int n = Mathf.RoundToInt(len * Fps);
                System.Func<int, float> U = i => Mathf.Min(1f, i / (len * Fps));
                // the frames to clear: the two spans (the source is clear elsewhere but for the ends, which are the idle)
                System.Func<float, bool> inSpan = u => (u >= W0 && u <= W1) || (u >= F0 && u <= 0.99f);
                string[] what = new string[n + 1]; float[] depth = new float[n + 1];
                System.Func<int> worst = () =>
                {
                    int wi = -1;
                    for (int i = 0; i <= n; i++)
                    {
                        float u = U(i); depth[i] = 0f; what[i] = "";
                        if (!inSpan(u)) continue;
                        SetPose(pr, prop => V(prop, u));
                        Vector3 pu; depth[i] = Pen(pr, false, Clearance, out what[i], out pu);
                        if (depth[i] > 0f && (wi < 0 || depth[i] > depth[wi])) wi = i;
                    }
                    return wi;
                };
                int w0 = worst();
                report.Append("aimed at " + toAtk.name + " f0: within 2 cm " + System.Array.FindAll(depth, x => x > 0f).Length + " frames" + (w0 >= 0 ? ", deepest " + (depth[w0] * 100f).ToString("F1", inv) + " cm at " + U(w0).ToString("F2", inv) + " " + what[w0] : "") + "; ");
                for (int it = 0; it < 6 && w0 >= 0; it++)
                {
                    float uw = U(w0);
                    bool wind = uw <= W1 + 0.005f;
                    var b = new Bump();
                    // centred on the frame it fixes (full weight there), inside its span, zero at the span's edges
                    if (wind) { b.tc = Mathf.Clamp(uw, W0 + 0.01f, W1 - 0.01f); b.h = Mathf.Min(b.tc - W0, W1 - b.tc); }
                    else { b.tc = Mathf.Clamp(uw, F0 + 0.01f, 0.98f); b.h = Mathf.Min(0.03f, Mathf.Min(b.tc - F0, 0.99f - b.tc)); }
                    SetPose(pr, prop => V(prop, uw));
                    var before = new HumanPose(); pr.hph.GetHumanPose(ref before);
                    for (int s = 0; s < 20; s++)
                    {
                        string into; Vector3 push;
                        float dd = Pen(pr, false, Margin, out into, out push);
                        if (dd <= 0f) break;
                        Reach(pr, pr.hand.position + push.normalized * (dd + 0.005f));
                    }
                    var after = new HumanPose(); pr.hph.GetHumanPose(ref after);
                    foreach (var kv in pr.muscle)
                        if (IsSwordArm(kv.Key) && orig.ContainsKey(kv.Key))
                            b.d[kv.Key] = Mathf.Clamp(after.muscles[kv.Value] - before.muscles[kv.Value], -0.3f, 0.3f);
                    bumps.Add(b);
                    w0 = worst();
                    if (it == 5 || w0 < 0)
                        report.Append(bumps.Count + " key(s) out of the head and the shield: within 2 cm " + System.Array.FindAll(depth, x => x > 0f).Length + " frames" +
                                      (w0 >= 0 ? ", deepest " + (depth[w0] * 100f).ToString("F1", inv) + " cm at " + U(w0).ToString("F2", inv) + " " + what[w0] : "") + "; ");
                }

                // the exit band (the combo window to the chain's latest exit): the sword hand and blade against L3 f0
                Transform tip = pr.solver.bladeTip != null ? pr.solver.bladeTip : pr.hand;
                System.Func<Vector3[]> local = () =>
                {
                    Vector3 fw = Vector3.ProjectOnPlane(pr.hips.forward, Vector3.up);
                    Quaternion iq = Quaternion.Inverse(Quaternion.LookRotation(fw.sqrMagnitude > 1e-4f ? fw : Vector3.forward, Vector3.up));
                    return new[] { iq * (pr.hand.position - pr.hips.position), (iq * (tip.position - pr.hand.position)).normalized };
                };
                SetPose(pr, prop => { var cb = curve(B, prop); return cb != null ? cb.Evaluate(0f) : (prop == "RootQ.w" ? 1f : 0f); });
                var l3 = local();
                float hMax = 0f, bMax = 0f, hMax0 = 0f, bMax0 = 0f;
                for (float u = 0.60f; u <= 0.7801f; u += 1f / (len * Fps))
                {
                    float uu = u;
                    SetPose(pr, prop => V(prop, uu)); var h = local();
                    hMax = Mathf.Max(hMax, Vector3.Distance(h[0], l3[0]) * 100f); bMax = Mathf.Max(bMax, Vector3.Angle(h[1], l3[1]));
                    SetPose(pr, prop => orig[prop].Evaluate(uu * len)); var h0 = local();
                    hMax0 = Mathf.Max(hMax0, Vector3.Distance(h0[0], l3[0]) * 100f); bMax0 = Mathf.Max(bMax0, Vector3.Angle(h0[1], l3[1]));
                }
                report.Append("exit band 0.60-0.78 to L3 f0: hand <= " + hMax.ToString("F0") + " cm, blade <= " + bMax.ToString("F0") + " deg (bar 20 / 20; the source " + hMax0.ToString("F0") + " cm / " + bMax0.ToString("F0") + " deg); ");

                // the clip: untouched curves are the source curves; re-keyed ones are sampled at 60 fps with their slopes
                var clip = new AnimationClip { name = FtName, frameRate = Fps };
                var outB = new List<EditorCurveBinding>(); var outC = new List<AnimationCurve>();
                int rekeyed = 0;
                foreach (var prop in props)
                {
                    bool touch = IsArmOrUpper(prop) && (aim.ContainsKey(prop) || bumps.Exists(b => b.d.ContainsKey(prop)));
                    AnimationCurve c;
                    if (!touch) c = orig[prop];
                    else
                    {
                        rekeyed++;
                        var keys = new Keyframe[n + 1];
                        for (int i = 0; i <= n; i++)
                        {
                            float u = U(i), h = 1f / (len * Fps * 8f);
                            float slope = (V(prop, Mathf.Min(1f, u + h)) - V(prop, Mathf.Max(0f, u - h))) / ((Mathf.Min(1f, u + h) - Mathf.Max(0f, u - h)) * len);
                            keys[i] = new Keyframe(u * len, V(prop, u), slope, slope);
                        }
                        c = new AnimationCurve(keys);
                    }
                    outB.Add(EditorCurveBinding.FloatCurve("", typeof(Animator), prop)); outC.Add(c);
                }
                AnimationUtility.SetEditorCurves(clip, outB.ToArray(), outC.ToArray());
                var set = AnimationUtility.GetAnimationClipSettings(A);
                set.startTime = 0f; set.stopTime = len;
                AnimationUtility.SetAnimationClipSettings(clip, set);
                string path = PathOf(FtName);
                var old = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (old != null) { EditorUtility.CopySerialized(clip, old); clip = old; EditorUtility.SetDirty(old); }
                else AssetDatabase.CreateAsset(clip, path);
                AssetDatabase.SaveAssets();
                var imp = AssetImporter.GetAtPath(path);
                if (imp != null)
                {
                    imp.userData = "source:Adobe Mixamo " + A.name + " (Pro Sword and Shield Pack, licensed), arm and upper-spine muscles re-keyed by chainvia ft: wind-up " +
                                   W0.ToString("F2", inv) + "-" + W1.ToString("F2", inv) + " off the head, follow-through from " + F0.ToString("F2", inv) + " out of the shield into " + toAtk.name + " f0; " + bumps.Count + " keys";
                    imp.SaveAndReimport();
                }

                var wc = new Dictionary<string, AnimationCurve>();
                foreach (var prop in props) wc[prop] = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), prop));
                // the curve diff: 240 Hz, the written clip against the source
                float dSpan = 0f, dRest = 0f, dWind = 0f; string dWindProp = "";
                foreach (var prop in props)
                {
                    var c = wc[prop];
                    for (int i = 0; i <= Mathf.RoundToInt(len * 240f); i++)
                    {
                        float tt = i / 240f, u = tt / len, dd = Mathf.Abs(c.Evaluate(tt) - orig[prop].Evaluate(tt));
                        if (u >= W1 + 1f / (len * Fps) && u <= F0) dSpan = Mathf.Max(dSpan, dd);
                        if (!IsArmOrUpper(prop)) dRest = Mathf.Max(dRest, dd);
                        if (u >= W0 && u <= W1 && dd > dWind) { dWind = dd; dWindProp = prop; }
                    }
                }
                report.Append("curve diff (240 Hz): 0.27-0.54 (contact 0.527 inside) max " + dSpan.ToString("0.00000", inv) + ", root/legs/feet/other muscles max " + dRest.ToString("0.00000", inv) +
                              ", wind-up max " + dWind.ToString("F3", inv) + " (" + dWindProp + "); " + rekeyed + " curves re-keyed of " + props.Count + "; ");

                // the sanity bar: the wrist (forearm to hand to the middle finger's root) and the sword in the fist (the grip
                // against the hand, as it sits at the source's first frame)
                Transform mid = pr.anim.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
                Transform grip = pr.solver.grip != null ? pr.solver.grip : pr.solver.weapon;
                SetPose(pr, prop => orig[prop].Evaluate(0f));
                Vector3 grip0 = pr.hand.InverseTransformPoint(grip.position);
                System.Func<float[]> sane = () =>
                {
                    float wristBend = mid != null ? Vector3.Angle(pr.hand.position - pr.lower.position, mid.position - pr.hand.position) : 0f;
                    float gripOff = Vector3.Distance(pr.hand.InverseTransformPoint(grip.position), grip0) * 100f * pr.hand.lossyScale.x;
                    return new[] { wristBend, gripOff };
                };
                // the written clip, every frame, as the game measures it (no solver), and the sanity bar
                int clipIn = 0; float clipMax = 0f; string clipAt = "";
                int insane = 0; float wristMax = 0f, gripMax = 0f; string insaneAt = "";
                for (int i = 0; i <= n; i++)
                {
                    float tt = Mathf.Min(len, i / Fps), u = tt / len;
                    if (u < 0.01f || u > 0.99f) continue;   // the idle's ends
                    SetPose(pr, prop => { AnimationCurve c; return wc.TryGetValue(prop, out c) && c != null ? c.Evaluate(tt) : (prop == "RootQ.w" ? 1f : 0f); });
                    string into; Vector3 pu; float dd = Pen(pr, false, Clearance, out into, out pu);
                    if (dd > 0f) { clipIn++; if (dd > clipMax) { clipMax = dd; clipAt = u.ToString("F2", inv) + " " + into; } }
                    var sn = sane();
                    wristMax = Mathf.Max(wristMax, sn[0]); gripMax = Mathf.Max(gripMax, sn[1]);
                    if (sn[0] > 70f || sn[1] > 2f) { insane++; if (insaneAt.Length == 0) insaneAt = u.ToString("F2", inv); }
                }
                // the window's tail (0.54-0.577): the cut's plane (blade and tip velocity) and the tip's speed against the source
                float planeMax = 0f, speedDev = 0f;
                {
                    Transform tp = pr.solver.bladeTip != null ? pr.solver.bladeTip : pr.hand;
                    System.Func<System.Func<string, float, float>, float, Vector3[]> at = (src, tt) =>
                    {
                        SetPose(pr, prop => src(prop, tt));
                        Vector3 fw = Vector3.ProjectOnPlane(pr.hips.forward, Vector3.up);
                        Quaternion iq = Quaternion.Inverse(Quaternion.LookRotation(fw.sqrMagnitude > 1e-4f ? fw : Vector3.forward, Vector3.up));
                        return new[] { iq * (tp.position - pr.hips.position), iq * (tp.position - pr.hand.position) };
                    };
                    System.Func<string, float, float> so = (prop, tt) => orig[prop].Evaluate(tt);
                    System.Func<string, float, float> sw = (prop, tt) => { AnimationCurve c; return wc.TryGetValue(prop, out c) && c != null ? c.Evaluate(tt) : (prop == "RootQ.w" ? 1f : 0f); };
                    for (float u = F0; u <= 0.5771f; u += 1f / (len * Fps))
                    {
                        float t0 = u * len, t1 = t0 + 1f / Fps;
                        var a0 = at(so, t0); var a1 = at(so, t1); var b0 = at(sw, t0); var b1 = at(sw, t1);
                        Vector3 va = (a1[0] - a0[0]) * Fps, vb = (b1[0] - b0[0]) * Fps;
                        Vector3 na = Vector3.Cross(a0[1], va), nb = Vector3.Cross(b0[1], vb);
                        float ang = Vector3.Angle(na, nb); ang = Mathf.Min(ang, 180f - ang);
                        planeMax = Mathf.Max(planeMax, ang);
                        if (va.magnitude > 0.1f) speedDev = Mathf.Max(speedDev, Mathf.Abs(vb.magnitude / va.magnitude - 1f) * 100f);
                    }
                }

                // before / after at 0.23: the hero's head and sword hand
                var head = pr.anim.GetBoneTransform(HumanBodyBones.Head);
                foreach (var at in new[] { 0.23f, 0.56f, 0.70f })
                foreach (var pass in new[] { "before", "after" })
                {
                    float tt = at * len;
                    SetPose(pr, prop => { AnimationCurve c; var src = pass == "before" ? orig : wc; return src.TryGetValue(prop, out c) && c != null ? c.Evaluate(tt) : (prop == "RootQ.w" ? 1f : 0f); });
                    pr.solver.weapon.localPosition = pr.wPos; pr.solver.weapon.localRotation = pr.wRot;
                    var go = new GameObject("~ftshot");
                    try
                    {
                        var cam = go.AddComponent<Camera>(); cam.nearClipPlane = 0.02f; cam.fieldOfView = 34f;
                        Vector3 look = (head.position + pr.hand.position) * 0.5f;
                        Vector3 side = pr.go.transform.right, fwd = pr.go.transform.forward;
                        go.transform.position = look + (side * 0.9f + fwd * 0.9f + Vector3.up * 0.15f);
                        go.transform.rotation = Quaternion.LookRotation(look - go.transform.position, Vector3.up);
                        GKPreview.Capture(cam, "ft_" + Mathf.RoundToInt(at * 100f).ToString("000") + "_" + pass, 1280, 720);
                    }
                    finally { Object.DestroyImmediate(go); }
                }
                bool tailOk = planeMax <= 10f && speedDev <= 10f;
                bool ftPass = clipIn == 0 && insane == 0 && tailOk && dSpan < 1e-4f && dRest < 1e-4f;
                GKBridge.Log("chainvia ft: " + path + ": " + report + "written clip: within 2 cm of body or shield " + clipIn + " frames" + (clipIn > 0 ? " (deepest " + (clipMax * 100f).ToString("F1", inv) + " cm at " + clipAt + ")" : "") +
                             "; sanity: wrist bend max " + wristMax.ToString("F0") + " deg (bar 70), grip off the fist max " + gripMax.ToString("F1", inv) + " cm (bar 2), " + insane + " frames out" + (insane > 0 ? " from " + insaneAt : "") +
                             "; window tail 0.54-0.577: cut plane within " + planeMax.ToString("F0") + " deg (bar 10), tip speed within " + speedDev.ToString("F0") + " % (bar 10)" +
                             (ftPass ? "; PASS" : "; FAIL") + "; stills GKBridge/ft_023|056|070_before|after.png");
            }
            finally { Object.DestroyImmediate(pr.go); }
        }

        static void Build(AttackData fromAtk, AttackData toAtk, float exit, float entry, float T, float w, bool allowMid, string name)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            AnimationClip A = fromAtk.clip, B = toAtk.clip;
            float te = Mathf.Clamp01(exit) * A.length, tb = Mathf.Clamp01(entry) * B.length;
            var props = new List<string>();
            foreach (var b in AnimationUtility.GetCurveBindings(B))
                if (b.type == typeof(Animator) && !b.propertyName.StartsWith("Motion")) props.Add(b.propertyName);
            if (props.Count == 0) { GKBridge.Log("chainvia: " + B.name + " has no humanoid curves"); return; }
            System.Func<AnimationClip, string, AnimationCurve> curve = (c, prop) => AnimationUtility.GetEditorCurve(c, EditorCurveBinding.FloatCurve("", typeof(Animator), prop));

            int nVia = Mathf.Max(2, Mathf.RoundToInt(T * Fps)), nTail = Mathf.Max(1, Mathf.CeilToInt((B.length - tb) * Fps));
            var times = new float[nVia + nTail + 1];
            for (int i = 0; i <= nVia; i++) times[i] = T * i / nVia;
            for (int i = 1; i <= nTail; i++) times[nVia + i] = T + Mathf.Min(B.length - tb, i / Fps);

            // the body's turn: one quaternion, on the same hemisphere as the exit's
            string[] q = { "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" };
            float dot = 0f;
            foreach (var k in q) { var ca = curve(A, k); var cb = curve(B, k); if (ca != null && cb != null) dot += ca.Evaluate(te) * cb.Evaluate(tb); }
            float sign = dot < 0f ? -1f : 1f;

            var segs = new Dictionary<string, Seg>(); var tails = new Dictionary<string, System.Func<float, float>>();
            var exitVal = new Dictionary<string, AnimationCurve>();
            foreach (var prop in props)
            {
                var cb = curve(B, prop); var ca = curve(A, prop);
                if (cb == null) continue;
                exitVal[prop] = ca;
                float s = System.Array.IndexOf(q, prop) >= 0 ? sign : 1f;
                var g = new Seg { p0 = ca != null ? ca.Evaluate(te) : cb.Evaluate(tb), v0 = ca != null ? D(ca, te, A.length) * w : 0f, p1 = cb.Evaluate(tb) * s, v1 = D(cb, tb, B.length) * s };
                float shift = 0f;
                // the body's travel: none of its own beyond carrying the two clips' momentum; the next clip's travel
                // continues from where the transition leaves it
                if (prop == "RootT.x" || prop == "RootT.z") { float end = g.p0 + 0.5f * (g.v0 + g.v1) * T; shift = end - g.p1; g.p1 = end; }
                segs[prop] = g;
                float sh = shift, sg = s; var cbb = cb;
                tails[prop] = t => cbb.Evaluate(tb + t) * sg + sh;
            }
            System.Func<string, float, float> at = (prop, t) =>
            {
                Seg g;
                if (!segs.TryGetValue(prop, out g)) return prop == "RootQ.w" ? 1f : 0f;
                return t <= T ? Eval(g, T, t) : tails[prop](t - T);
            };

            var pr = MakeProbe();
            var report = new StringBuilder();
            try
            {
                if (pr == null || pr.solver == null) report.Append("NO BLADE CHECK (clean hero or its blade solver missing); ");
                // the old chain for comparison (and for agreement with play, lease K: 1 frame, 3.7 cm): a 0.18 s
                // crossfade from the outgoing clip running on into the next one, the solver correcting as in play
                if (pr != null && pr.solver != null)
                {
                    var cbs = new Dictionary<string, AnimationCurve>();
                    foreach (var prop in segs.Keys) cbs[prop] = curve(B, prop);
                    ResetSolver(pr);
                    int inN = 0; float inMax = 0f; string inAt = "";
                    for (int i = -6; i <= Mathf.RoundToInt(0.4f * Fps); i++)
                    {
                        float tt = i / Fps, k = Mathf.Clamp01(tt / 0.18f);
                        SetPose(pr, prop =>
                        {
                            AnimationCurve ca, cb;
                            if (!cbs.TryGetValue(prop, out cb) || cb == null) return prop == "RootQ.w" ? 1f : 0f;
                            float vb = cb.Evaluate(Mathf.Max(0f, tt)) * (System.Array.IndexOf(q, prop) >= 0 ? sign : 1f);
                            float va = exitVal.TryGetValue(prop, out ca) && ca != null ? ca.Evaluate(Mathf.Clamp(te + tt, 0f, A.length)) : vb;
                            return Mathf.Lerp(va, vb, k);
                        });
                        string into; Vector3 push;
                        float d = Pen(pr, true, 0f, out into, out push);
                        if (i >= 0 && d > 0f) { inN++; if (d > inMax) { inMax = d; inAt = "f" + i + " " + into; } }
                    }
                    report.Append("old crossfade (solver on): blade in body/shield " + inN + " frames" + (inN > 0 ? ", max " + (inMax * 100f).ToString("F1", inv) + " cm at " + inAt : "") + "; ");
                }

                // the transition, the solver standing down: no blade within 2 cm of the body or the shield. Where it
                // comes closer, a key with the sword hand pushed out along the solver's own contact normal
                float[] depth = new float[nVia + 1]; string[] what = new string[nVia + 1];
                System.Func<int> worstVia = () =>
                {
                    int wi = 0;
                    for (int i = 0; i <= nVia; i++)
                    {
                        SetPose(pr, prop => at(prop, times[i]));
                        Vector3 pu; depth[i] = Pen(pr, false, Clearance, out what[i], out pu);
                        if (depth[i] > depth[wi]) wi = i;
                    }
                    return wi;
                };
                if (pr != null && pr.solver != null)
                {
                    int wi = worstVia();
                    report.Append("cubic: within 2 cm " + System.Array.FindAll(depth, x => x > 0f).Length + " frames, deepest " + (depth[wi] * 100f).ToString("F1", inv) + " cm at f" + wi + " " + what[wi] + "; ");
                    for (int attempt = 0; attempt < 6 && allowMid && depth[wi] > 0f; attempt++)
                    {
                        int ki = Mathf.Clamp(wi, 1, nVia - 1);
                        float tk = times[ki];
                        SetPose(pr, prop => at(prop, tk));
                        for (int it = 0; it < 20; it++)
                        {
                            string into; Vector3 push;
                            float d = Pen(pr, false, Margin, out into, out push);
                            if (d <= 0f) break;
                            Reach(pr, pr.hand.position + push.normalized * (d + 0.005f));
                        }
                        string kin; Vector3 kpu; float keyDepth = Pen(pr, false, Clearance, out kin, out kpu);
                        var outPose = new HumanPose(); pr.hph.GetHumanPose(ref outPose);
                        foreach (var kv in pr.muscle)
                        {
                            Seg g;
                            if (!IsSwordArm(kv.Key) || !segs.TryGetValue(kv.Key, out g)) continue;
                            g.Ends(T); g.Key(tk, outPose.muscles[kv.Value]);
                        }
                        wi = worstVia();
                        report.Append("key " + (attempt + 1) + " at f" + ki + " (" + (keyDepth > 0f ? "still " + (keyDepth * 100f).ToString("F1", inv) + " cm in " + kin : "clear") + "): within 2 cm " +
                                      System.Array.FindAll(depth, x => x > 0f).Length + " frames, deepest " + (depth[wi] * 100f).ToString("F1", inv) + " cm at f" + wi + " " + what[wi] + "; ");
                    }
                }

                // the clip
                var clip = new AnimationClip { name = name, frameRate = Fps };
                var outBinds = new List<EditorCurveBinding>(); var outCurves = new List<AnimationCurve>();
                foreach (var prop in segs.Keys)
                {
                    var vals = new float[times.Length];
                    for (int i = 0; i < times.Length; i++) vals[i] = at(prop, times[i]);
                    outBinds.Add(EditorCurveBinding.FloatCurve("", typeof(Animator), prop));
                    outCurves.Add(null);
                    var keys = new Keyframe[times.Length];
                    for (int i = 0; i < times.Length; i++)
                    {
                        int a = Mathf.Max(0, i - 1), b = Mathf.Min(times.Length - 1, i + 1);
                        float slope = times[b] > times[a] ? (vals[b] - vals[a]) / (times[b] - times[a]) : 0f;
                        keys[i] = new Keyframe(times[i], vals[i], slope, slope);
                    }
                    outCurves[outCurves.Count - 1] = new AnimationCurve(keys);
                }
                // RootQ normalized per key
                int[] qi = new int[4];
                for (int k = 0; k < 4; k++) qi[k] = outBinds.FindIndex(b => b.propertyName == q[k]);
                if (System.Array.TrueForAll(qi, x => x >= 0))
                    for (int i = 0; i < times.Length; i++)
                    {
                        float m = 0f; for (int k = 0; k < 4; k++) m += outCurves[qi[k]].keys[i].value * outCurves[qi[k]].keys[i].value;
                        m = Mathf.Sqrt(m); if (m < 1e-6f) continue;
                        for (int k = 0; k < 4; k++) { var c = outCurves[qi[k]]; var kf = c.keys[i]; kf.value /= m; c.MoveKey(i, kf); }
                    }
                AnimationUtility.SetEditorCurves(clip, outBinds.ToArray(), outCurves.ToArray());
                var set = AnimationUtility.GetAnimationClipSettings(B);
                set.loopTime = false; set.loopBlend = false; set.cycleOffset = 0f; set.mirror = false;
                set.startTime = 0f; set.stopTime = times[times.Length - 1];
                AnimationUtility.SetAnimationClipSettings(clip, set);

                string path = PathOf(name);
                GKPaths.Ensure(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
                var old = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (old != null) { EditorUtility.CopySerialized(clip, old); clip = old; EditorUtility.SetDirty(old); }
                else AssetDatabase.CreateAsset(clip, path);
                AssetDatabase.SaveAssets();
                float len = times[times.Length - 1], viaEnd = T / len;
                var imp = AssetImporter.GetAtPath(path);
                if (imp != null)
                {
                    imp.userData = "viaEnd:" + viaEnd.ToString("0.0000", inv) + ";from:" + fromAtk.name + "@" + exit.ToString("0.000", inv) + ";to:" + toAtk.name + "@" + entry.ToString("0.000", inv) +
                                   ";seconds:" + T.ToString("0.000", inv) + ";velocity:" + w.ToString("0.00", inv) +
                                   ";source:own keys (chainvia) from the Mixamo clips " + A.name + " and " + B.name;
                    imp.SaveAndReimport();
                }

                // the clip as written, frame by frame: the shield through the transition and 0.3 s after it, and the tip's
                // step change in the hips' frame (the arm against the body) from the outgoing clip's last frames on
                int viaIn = 0, afterIn = 0; float viaMax = 0f, afterMax = 0f; string viaAt = "", afterAt = "";
                float worst = 0f; int wf = 0; float speed = 0f;
                if (pr != null && pr.solver != null)
                {
                    var tips = new List<Vector3>();
                    Transform tip = pr.solver.bladeTip != null ? pr.solver.bladeTip : pr.hand;
                    System.Func<Vector3> local = () =>
                    {
                        Vector3 f = Vector3.ProjectOnPlane(pr.hips.forward, Vector3.up);
                        return Quaternion.Inverse(Quaternion.LookRotation(f.sqrMagnitude > 1e-4f ? f : Vector3.forward, Vector3.up)) * (tip.position - pr.hips.position);
                    };
                    for (int i = 3; i >= 1; i--)
                    {
                        float ta = Mathf.Max(0f, te - i / Fps);
                        SetPose(pr, prop => { AnimationCurve ca; return exitVal.TryGetValue(prop, out ca) && ca != null ? ca.Evaluate(ta) : at(prop, 0f); });
                        tips.Add(local());
                    }
                    ResetSolver(pr);
                    int n = Mathf.RoundToInt((T + 0.3f) * Fps);
                    for (int i = 0; i <= n; i++)
                    {
                        float tt = Mathf.Min(len, i / Fps);
                        SetPose(pr, prop => at(prop, tt));
                        tips.Add(local());
                        bool inVia = tt < T - 1e-4f;
                        string into; Vector3 pu;
                        float d = Pen(pr, !inVia, inVia ? Clearance : 0f, out into, out pu);
                        if (d > 0f)
                        {
                            if (inVia) { viaIn++; if (d > viaMax) { viaMax = d; viaAt = "f" + i + " " + into; } }
                            else { afterIn++; if (d > afterMax) { afterMax = d; afterAt = "f" + i + " " + into; } }
                        }
                    }
                    for (int i = 1; i + 1 < tips.Count; i++)
                    {
                        float j = (tips[i + 1] - 2f * tips[i] + tips[i - 1]).magnitude * 1000f;
                        if (j > worst) { worst = j; wf = i - 3; }
                        speed = Mathf.Max(speed, (tips[i + 1] - tips[i]).magnitude * Fps);
                    }
                }
                bool pass = pr != null && pr.solver != null && viaIn == 0 && afterIn == 0;
                GKBridge.Log("chainvia: " + path + " " + len.ToString("F3", inv) + " s (" + times.Length + " keys, " + segs.Count + " curves): transition " + T.ToString("F3", inv) +
                             " s = 0-" + viaEnd.ToString("F3", inv) + ", then " + B.name + " from " + entry.ToString("F2", inv) + "; human " + clip.humanMotion + "; " + report +
                             "written: transition within 2 cm " + viaIn + " frames" + (viaIn > 0 ? " (deepest " + (viaMax * 100f).ToString("F1", inv) + " cm at " + viaAt + ")" : "") +
                             ", after it in body/shield " + afterIn + " frames" + (afterIn > 0 ? " (max " + (afterMax * 100f).ToString("F1", inv) + " cm at " + afterAt + ")" : "") +
                             " (bar 2.0: " + (pass ? "PASS" : "FAIL") + "); tip step change max " +
                             worst.ToString("F1", inv) + " mm at f" + wf + " (bar 30), tip speed max " + speed.ToString("F1", inv) + " m/s (hips frame)");
            }
            finally { if (pr != null) Object.DestroyImmediate(pr.go); }
        }
    }
}
