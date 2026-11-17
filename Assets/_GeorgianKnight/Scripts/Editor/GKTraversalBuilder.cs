using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Ladder traversal content:
    ///  * animator states (mount, climb loop with a signed speed parameter so it also plays downward, exits) on the
    ///    base layer, with the IK pass the climber needs for hands and feet on the rungs;
    ///  * the <see cref="ClimbProfile"/>: every ladder clip sampled at 60 Hz on the real knight avatar (root curve from the
    ///    humanoid body curves, wrist/ankle positions relative to the root), plus what the climber derives from it
    ///    (rise per cycle, rung pitch, stand-off from the rung plane, rest phases, phase matches for the transitions).
    ///  * "climbcal" writes the profile and a readable report (GKBridge/climbcal.txt) for the ladder geometry.
    /// </summary>
    public static class GKTraversalBuilder
    {
        public const string ProfilePath = GKPaths.Data + "/ClimbProfile.asset";
        public const string ClipMount = "GK_Ladder_Start", ClipClimb = "GK_Ladder_Climb", ClipTop = "GK_Ladder_Top", ClipBottom = "GK_Ladder_DownToStand";

        // ------------------------------------------------------------------ animator

        public static void AddStates(AnimatorController c)
        {
            AnimationClip mount = GKCombatBuilder.Clip(ClipMount), climb = GKCombatBuilder.Clip(ClipClimb), top = GKCombatBuilder.Clip(ClipTop), bottom = GKCombatBuilder.Clip(ClipBottom);
            if (climb == null) { GKBridge.Log("traversal: no " + ClipClimb + " clip; ladder states skipped"); return; }
            c.AddParameter(new AnimatorControllerParameter { name = "ClimbSpeed", type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
            var sm = c.layers[0].stateMachine;
            System.Action<string, AnimationClip, int> add = (name, clip, row) =>
            {
                if (clip == null) { GKBridge.Log("traversal: missing clip for " + name); return; }
                var st = sm.AddState(name, new Vector3(-320f, 60f * row, 0f));
                st.motion = clip;
                st.speed = 1f;
                st.speedParameter = "ClimbSpeed";
                st.speedParameterActive = true;
                st.writeDefaultValues = true;
                st.iKOnFeet = false;
            };
            add(LadderClimber.S_Mount, mount, 0);
            add(LadderClimber.S_Climb, climb, 1);
            add(LadderClimber.S_ExitTop, top, 2);
            add(LadderClimber.S_ExitBottom, bottom, 3);
            var layers = c.layers;
            layers[0].iKPass = true;
            c.layers = layers;
        }

        // ------------------------------------------------------------------ profile

        [GKCommand("climbcal", "climbcal: sample the ladder clips on the knight and write the ClimbProfile + GKBridge/climbcal.txt")]
        static void ClimbCal(string a) { BuildProfile(); }

        public static ClimbProfile BuildProfile()
        {
            var climbClip = GKCombatBuilder.Clip(ClipClimb);
            if (climbClip == null) { GKBridge.Log("climbcal: no climb clip"); return null; }
            GKPaths.Ensure(GKPaths.Data);
            var prof = AssetDatabase.LoadAssetAtPath<ClimbProfile>(ProfilePath);
            if (prof == null) { prof = ScriptableObject.CreateInstance<ClimbProfile>(); AssetDatabase.CreateAsset(prof, ProfilePath); }

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(GKPaths.KnightFbx);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            var report = new StringBuilder();
            try
            {
                inst.transform.SetPositionAndRotation(new Vector3(0f, 6000f, 0f), Quaternion.identity);
                var anim = inst.GetComponent<Animator>();
                if (anim == null) anim = inst.AddComponent<Animator>();
                anim.avatar = GKCharacters.GetAvatar(GKPaths.KnightFbx);
                anim.applyRootMotion = false;
                anim.Rebind();
                Transform hips = anim.GetBoneTransform(HumanBodyBones.Hips);
                float restHips = hips.position.y - inst.transform.position.y;
                float hs = anim.humanScale;
                if (hs <= 0.01f || Mathf.Abs(hs - 1f) < 1e-4f) hs = restHips;      // edit-mode animator not initialised
                prof.humanScale = hs;
                // contact geometry of a hand round a rung / a foot on it (rung radius 22 mm): owned here, not by old assets
                prof.gripUp = 0.055f; prof.gripBack = 0.035f; prof.footUp = 0.10f; prof.footBack = 0.075f;
                report.AppendLine("humanScale " + F(hs) + " (rest hips height " + F(restHips) + ")");

                prof.climb = Sample(anim, inst.transform, climbClip, LadderClimber.S_Climb, hs, report);
                prof.mount = Sample(anim, inst.transform, GKCombatBuilder.Clip(ClipMount), LadderClimber.S_Mount, hs, report);
                prof.exitTop = Sample(anim, inst.transform, GKCombatBuilder.Clip(ClipTop), LadderClimber.S_ExitTop, hs, report);
                prof.exitBottom = Sample(anim, inst.transform, GKCombatBuilder.Clip(ClipBottom), LadderClimber.S_ExitBottom, hs, report);
                Derive(prof, report);
            }
            finally
            {
                Object.DestroyImmediate(inst);
            }
            EditorUtility.SetDirty(prof);
            AssetDatabase.SaveAssets();
            System.IO.File.WriteAllText(System.IO.Path.Combine(GKBridge.Dir, "climbcal.txt"), report.ToString());
            GKBridge.Log("climbcal: rise/cycle " + F(prof.climbRise) + " pitch " + F(prof.rungPitch) + " planeZ " + F(prof.planeZ) + " rungOffset " + F(prof.rungOffset) +
                         " rest " + string.Join(",", prof.restPhases.Select(F)) + " -> GKBridge/climbcal.txt");
            return prof;
        }

        [GKCommand("clipinfo", "clipinfo <clip name>: import settings, root curves and speeds of a Mixamo clip")]
        static void ClipInfo(string name)
        {
            string path = GKPaths.Mixamo + "/" + name + ".fbx";
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            var clip = GKCombatBuilder.Clip(name);
            if (mi == null || clip == null) { GKBridge.Log("clipinfo: no clip " + path); return; }
            var sb = new StringBuilder("clipinfo " + name + ":");
            foreach (var c in mi.clipAnimations.Length > 0 ? mi.clipAnimations : mi.defaultClipAnimations)
                sb.Append(" [" + c.name + " loop " + c.loopTime + " loopPose " + c.loopPose + " lockY " + c.lockRootHeightY + " origY " + c.keepOriginalPositionY +
                          " lockXZ " + c.lockRootPositionXZ + " origXZ " + c.keepOriginalPositionXZ + " lockRot " + c.lockRootRotation + " origRot " + c.keepOriginalOrientation +
                          " frames " + c.firstFrame + "-" + c.lastFrame + "]");
            sb.Append(" avatar " + mi.avatarSetup + " len " + F(clip.length) + " avgSpeed " + V(clip.averageSpeed) + " apparent " + F(clip.apparentSpeed) + " human " + clip.humanMotion);
            foreach (var prop in new[] { "RootT.x", "RootT.y", "RootT.z", "MotionT.y" })
            {
                var c = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), prop));
                sb.Append(" | " + prop + (c == null ? " none" : " keys " + c.length + " " + F(c.Evaluate(0f)) + "->" + F(c.Evaluate(clip.length))));
            }
            GKBridge.Log(sb.ToString());
        }

        [GKCommand("reimportclips", "reimportclips <prefix>: force-reimport the Mixamo clips whose name starts with prefix (applies GKImport rules)")]
        static void ReimportClips(string prefix)
        {
            int n = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { GKPaths.Mixamo }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!System.IO.Path.GetFileName(path).StartsWith(prefix ?? "")) continue;
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                n++;
            }
            GKBridge.Log("reimportclips " + prefix + ": " + n);
        }

        static string F(float v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }
        static string V(Vector3 v) { return "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")"; }

        static float Curve(AnimationClip clip, string prop, float t)
        {
            var c = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), prop));
            return c != null ? c.Evaluate(t) : 0f;
        }

        static ClimbProfile.ClipData Sample(Animator anim, Transform root, AnimationClip clip, string state, float hs, StringBuilder report)
        {
            var d = new ClimbProfile.ClipData { state = state };
            if (clip == null) { report.AppendLine(state + ": no clip"); return d; }
            d.length = clip.length;
            int n = Mathf.Max(2, Mathf.CeilToInt(clip.length * d.sampleRate)) + 1;
            d.hips = new Vector3[n]; d.leftHand = new Vector3[n]; d.rightHand = new Vector3[n]; d.leftFoot = new Vector3[n]; d.rightFoot = new Vector3[n];
            d.root = new Vector3[0];
            Transform hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            Transform lh = anim.GetBoneTransform(HumanBodyBones.LeftHand), rh = anim.GetBoneTransform(HumanBodyBones.RightHand);
            Transform lf = anim.GetBoneTransform(HumanBodyBones.LeftFoot), rf = anim.GetBoneTransform(HumanBodyBones.RightFoot);
            Vector3 home = root.position;
            float rootDrift = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = clip.length * i / (n - 1);
                root.SetPositionAndRotation(home, Quaternion.identity);
                clip.SampleAnimation(root.gameObject, t);
                rootDrift = Mathf.Max(rootDrift, (root.position - home).magnitude);
                // relative to the transform (baked clips leave it in place; if sampling moved it, measure from it anyway)
                Vector3 o = root.position; Quaternion inv = Quaternion.Inverse(root.rotation);
                d.hips[i] = inv * (hips.position - o);
                d.leftHand[i] = inv * (lh.position - o); d.rightHand[i] = inv * (rh.position - o);
                d.leftFoot[i] = inv * (lf.position - o); d.rightFoot[i] = inv * (rf.position - o);
            }
            root.SetPositionAndRotation(home, Quaternion.identity);
            // body facing at t = 0 (from the thighs: right = left->right hip, forward = right x up); rotate everything
            // into a canonical frame facing +z, remember the yaw for the runtime
            clip.SampleAnimation(root.gameObject, 0f);
            Vector3 right = anim.GetBoneTransform(HumanBodyBones.RightUpperLeg).position - anim.GetBoneTransform(HumanBodyBones.LeftUpperLeg).position;
            right.y = 0f;
            Vector3 fwd = Vector3.Cross(right.normalized, Vector3.up);
            d.yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            Quaternion canon = Quaternion.Euler(0f, -d.yaw, 0f);
            for (int i = 0; i < n; i++)
            {
                d.hips[i] = canon * d.hips[i]; d.leftHand[i] = canon * d.leftHand[i]; d.rightHand[i] = canon * d.rightHand[i];
                d.leftFoot[i] = canon * d.leftFoot[i]; d.rightFoot[i] = canon * d.rightFoot[i];
            }
            root.SetPositionAndRotation(home, Quaternion.identity);
            report.AppendLine(state + " (" + clip.name + "): yaw " + F(d.yaw) + " length " + F(clip.length) + " hips " + V(d.hips[0]) + " -> " + V(d.hips[n - 1]) +
                              (rootDrift > 1e-3f ? " [sampling moved the root " + F(rootDrift) + " m: clip not fully baked]" : "") +
                              " | start L " + V(d.leftHand[0]) + " R " + V(d.rightHand[0]) + " LF " + V(d.leftFoot[0]) + " RF " + V(d.rightFoot[0]) +
                              " | end L " + V(d.leftHand[n - 1]) + " R " + V(d.rightHand[n - 1]) + " LF " + V(d.leftFoot[n - 1]) + " RF " + V(d.rightFoot[n - 1]));
            return d;
        }

        /// <summary>Climb-loop quantities (clip space: the captured ladder is fixed, a gripping hand is still) and the
        /// phase matches for the transitions.</summary>
        static void Derive(ClimbProfile p, StringBuilder report)
        {
            var c = p.climb;
            int n = c.hips.Length;
            p.climbRise = c.hips[n - 1].y - c.hips[0].y;
            p.rungPitch = p.climbRise * 0.5f;
            float dt = c.length / (n - 1);
            var still = new float[n];
            var hz = new List<float>(); var fz = new List<float>(); var xs = new List<float>();
            float hsx = 0f, hsy = 0f, fsx = 0f, fsy = 0f; int hn = 0, fn = 0;
            float pitch = Mathf.Max(0.05f, p.rungPitch);
            System.Func<Vector3[], int, float> speed = (a, i) =>
            {
                int j = Mathf.Min(n - 1, i + 1), k = Mathf.Max(0, i - 1);
                return (a[j] - a[k]).magnitude / ((j - k) * dt);
            };
            for (int i = 0; i < n; i++)
            {
                float vl = speed(c.leftHand, i), vr = speed(c.rightHand, i), vlf = speed(c.leftFoot, i), vrf = speed(c.rightFoot, i);
                still[i] = Mathf.Max(vl, vr);
                foreach (var h in new[] { new KeyValuePair<float, Vector3>(vl, c.leftHand[i]), new KeyValuePair<float, Vector3>(vr, c.rightHand[i]) })
                {
                    if (h.Key >= 0.25f) continue;
                    hz.Add(h.Value.z); xs.Add(h.Value.x);
                    float a = (h.Value.y - p.gripUp) / pitch * Mathf.PI * 2f; hsx += Mathf.Cos(a); hsy += Mathf.Sin(a); hn++;
                }
                foreach (var f in new[] { new KeyValuePair<float, Vector3>(vlf, c.leftFoot[i]), new KeyValuePair<float, Vector3>(vrf, c.rightFoot[i]) })
                {
                    if (f.Key >= 0.25f) continue;
                    fz.Add(f.Value.z); xs.Add(f.Value.x);
                    float a = (f.Value.y - p.footUp) / pitch * Mathf.PI * 2f; fsx += Mathf.Cos(a); fsy += Mathf.Sin(a); fn++;
                }
            }
            float handPhase = Mathf.Repeat(Mathf.Atan2(hsy, hsx) / (Mathf.PI * 2f), 1f), footPhase = Mathf.Repeat(Mathf.Atan2(fsy, fsx) / (Mathf.PI * 2f), 1f);
            // the capture's hands and feet disagree slightly about where its rungs were: take the circular mean of the
            // two lattices so IK moves each by half the disagreement (both stay natural)
            float mx = Mathf.Cos(handPhase * Mathf.PI * 2f) + Mathf.Cos(footPhase * Mathf.PI * 2f);
            float my = Mathf.Sin(handPhase * Mathf.PI * 2f) + Mathf.Sin(footPhase * Mathf.PI * 2f);
            p.rungOffset = Mathf.Repeat(Mathf.Atan2(my, mx) / (Mathf.PI * 2f), 1f) * pitch;
            float hzm = hz.Count > 0 ? Median(hz) : 0.4f, fzm = fz.Count > 0 ? Median(fz) : 0.3f;
            p.planeZ = 0.5f * ((hzm + p.gripBack) + (fzm + p.footBack));
            p.centreX = xs.Count > 0 ? xs.Average() : 0f;
            report.AppendLine("climb: rise " + F(p.climbRise) + " pitch " + F(p.rungPitch) + " | lattice used " + F(p.rungOffset) + " | from hands " + F(handPhase * pitch) +
                              " (" + hn + " grip samples), from feet " + F(footPhase * pitch) + " (" + fn + "): feet off by " +
                              F(Mathf.DeltaAngle(handPhase * 360f, footPhase * 360f) / 360f * pitch) + " m | rung plane z: hands " + F(hzm + p.gripBack) +
                              " feet " + F(fzm + p.footBack) + " -> " + F(p.planeZ) + " | centre x " + F(p.centreX));
            // rest phases: the two deepest minima of the faster hand's speed, well apart
            var mins = new List<KeyValuePair<float, float>>();
            for (int i = 0; i < n - 1; i++)
            {
                float a = still[(i - 1 + (n - 1)) % (n - 1)], b = still[i], d2 = still[(i + 1) % (n - 1)];
                if (b <= a && b <= d2) mins.Add(new KeyValuePair<float, float>(i / (float)(n - 1), b));
            }
            mins.Sort((x, y) => x.Value.CompareTo(y.Value));
            var rest = new List<float>();
            foreach (var m in mins)
            {
                if (rest.Count > 0 && m.Value > 0.3f) break;                 // only poses where both hands really hold
                if (rest.All(r => Mathf.Abs(Mathf.DeltaAngle(r * 360f, m.Key * 360f)) > 120f)) rest.Add(m.Key);
                if (rest.Count == 2) break;
            }
            if (rest.Count == 0) rest.Add(0f);
            rest.Sort();
            p.restPhases = rest.ToArray();
            report.AppendLine("rest phases " + string.Join(", ", rest.Select(r => F(r) + " (hand speed " + F(still[Mathf.RoundToInt(r * (n - 1))]) + ")")));
            if (p.mount.IsValid) p.ankleHeight = Mathf.Min(p.mount.leftFoot[0].y, p.mount.rightFoot[0].y);
            report.AppendLine("ankle height (standing, mount first frame) " + F(p.ankleHeight));
            p.mountEndPhase = BestPhase(c, p.mount, 1f, "mount end", report);
            p.exitTopPhase = BestPhase(c, p.exitTop, 0f, "top exit start", report);
            p.exitBottomPhase = BestPhase(c, p.exitBottom, 0f, "bottom exit start", report);
            report.AppendLine("loop samples (phase: hips.y | L R LF RF heights | z)");
            for (int i = 0; i < n; i += 3)
                report.AppendLine("  " + F(i / (float)(n - 1)) + ": " + F(c.hips[i].y) + " | " + F(c.leftHand[i].y) + " " + F(c.rightHand[i].y) + " " + F(c.leftFoot[i].y) + " " + F(c.rightFoot[i].y) +
                                  "  z " + F(c.leftHand[i].z) + " " + F(c.rightHand[i].z) + " " + F(c.leftFoot[i].z) + " " + F(c.rightFoot[i].z));
            WriteJson(p);
        }

        /// <summary>The numbers the Blender ladder builder needs, as JSON beside the report.</summary>
        static void WriteJson(ClimbProfile p)
        {
            var sb = new StringBuilder("{");
            System.Action<string, float> num = (k, v) => sb.Append("\"" + k + "\": " + v.ToString("R", CultureInfo.InvariantCulture) + ", ");
            System.Action<string, Vector3> vec = (k, v) => sb.Append("\"" + k + "\": [" + string.Join(", ", new[] { v.x, v.y, v.z }.Select(f => f.ToString("R", CultureInfo.InvariantCulture))) + "], ");
            num("humanScale", p.humanScale); num("climbRise", p.climbRise); num("rungPitch", p.rungPitch); num("rungOffset", p.rungOffset);
            num("planeZ", p.planeZ); num("centreX", p.centreX); num("ankleHeight", p.ankleHeight); num("gripUp", p.gripUp); num("footUp", p.footUp);
            num("mountEndPhase", p.mountEndPhase); num("exitTopPhase", p.exitTopPhase); num("exitBottomPhase", p.exitBottomPhase);
            System.Func<ClimbProfile.ClipData, float, Vector3> cen = (c, t) => (c.LeftHandAt(t) + c.RightHandAt(t) + c.LeftFootAt(t) + c.RightFootAt(t)) * 0.25f;
            System.Func<ClimbProfile.ClipData, float, Vector3> feet = (c, t) => (c.LeftFootAt(t) + c.RightFootAt(t)) * 0.5f;
            if (p.climb.IsValid)
            {
                vec("loopCentroidAtMountEnd", cen(p.climb, p.mountEndPhase)); vec("loopCentroidAtExitTop", cen(p.climb, p.exitTopPhase));
                vec("loopCentroidAtExitBottom", cen(p.climb, p.exitBottomPhase));
                float maxHand = float.MinValue, minFoot = float.MaxValue;
                for (int i = 0; i < p.climb.leftHand.Length; i++) { maxHand = Mathf.Max(maxHand, Mathf.Max(p.climb.leftHand[i].y, p.climb.rightHand[i].y)); minFoot = Mathf.Min(minFoot, Mathf.Min(p.climb.leftFoot[i].y, p.climb.rightFoot[i].y)); }
                num("loopMaxHandY", maxHand); num("loopMinFootY", minFoot);
            }
            if (p.mount.IsValid) { vec("mountCentroidEnd", cen(p.mount, 1f)); vec("mountFeetStart", feet(p.mount, 0f)); }
            if (p.exitTop.IsValid) { vec("exitTopCentroidStart", cen(p.exitTop, 0f)); vec("exitTopFeetEnd", feet(p.exitTop, 1f)); }
            if (p.exitBottom.IsValid) { vec("exitBottomCentroidStart", cen(p.exitBottom, 0f)); vec("exitBottomFeetEnd", feet(p.exitBottom, 1f)); }
            sb.Append("\"ok\": 1}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(GKBridge.Dir, "climbprofile.json"), sb.ToString());
        }

        static float Median(List<float> v) { v.Sort(); return v[v.Count / 2]; }

        /// <summary>Loop phase whose hand/foot layout (relative to its own centroid) best matches the given frame.</summary>
        static float BestPhase(ClimbProfile.ClipData loop, ClimbProfile.ClipData other, float at, string label, StringBuilder report)
        {
            if (other == null || !other.IsValid) { report.AppendLine(label + ": n/a"); return 0f; }
            Vector3[] q = { other.LeftHandAt(at), other.RightHandAt(at), other.LeftFootAt(at), other.RightFootAt(at) };
            Vector3 qc = (q[0] + q[1] + q[2] + q[3]) * 0.25f;
            int n = loop.root.Length;
            float best = 0f, bestE = float.MaxValue;
            for (int i = 0; i < n - 1; i++)
            {
                Vector3[] l = { loop.leftHand[i], loop.rightHand[i], loop.leftFoot[i], loop.rightFoot[i] };
                Vector3 lc = (l[0] + l[1] + l[2] + l[3]) * 0.25f;
                float e = 0f;
                for (int k = 0; k < 4; k++) e += ((l[k] - lc) - (q[k] - qc)).sqrMagnitude;
                if (e < bestE) { bestE = e; best = i / (float)(n - 1); }
            }
            report.AppendLine(label + ": loop phase " + F(best) + " (rms " + F(Mathf.Sqrt(bestE / 4f)) + " m)");
            return best;
        }
    }
}
