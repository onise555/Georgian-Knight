using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Authors the two-sword poses no pack has, as real humanoid clips in Animations/Generated:
    /// makeguard: GK_DualGuard (blades crossed in an X before the chin, the right blade resting on the left, flats to the
    /// front) and GK_DualGuardHit (a blocked blow knocks the cross back toward the chest; it returns in a third of a
    /// second). makedualidle: GK_DualIdle (arms low and forward, blades forward and down, as Edward Kenway carries his
    /// cutlasses). Each is built on the clean knight holding both swords: Kevin Iglesias' one-handed combat idle sampled
    /// frame by frame (so the body keeps breathing), each arm set by an exact two-bone solve, each wrist turned to aim its
    /// blade, the pose stored as Mecanim muscles (every joint inside its anatomical range: a wrist cannot twist past what
    /// a wrist does), then the clip played back on the knight and measured.
    /// </summary>
    public static class GKGuardPose
    {
        public const string ClipPath = "Assets/_GeorgianKnight/Animations/Generated/GK_DualGuard.anim";

        /// <summary>Where the arms go at one moment: fists from the chest (forward, apart, up) and either a point the
        /// blades cross through (forward / up from the chest, the right blade laid that far in front) or, with no
        /// cross, a blade direction (forward, up, outward).</summary>
        struct ArmSpec
        {
            public float fistFwd, apart, fistUp;
            public bool cross; public float crossFwd, crossUp, lay;
            public float dirFwd, dirUp, dirOut;
            /// <summary>How far the forearm is brought onto the hand's line (1 = a straight wrist). A low carry keeps a
            /// natural bend: straightening it fully drove the elbow behind the back, hand on the hip.</summary>
            public float straighten;
        }

        static float Arg(string[] p, int i, float d)
        {
            float v;
            return p.Length > i && float.TryParse(p[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : d;
        }

        [GKCommand("makeguard", "makeguard [fwd apart up crossFwd crossUp lay]: author the crossed two-sword guard and its blocked-hit clip, and measure them (edit mode)")]
        static void Cmd(string arg)
        {
            var p = (arg ?? "").Trim().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            var g = new ArmSpec { fistFwd = Arg(p, 0, 0.32f), apart = Arg(p, 1, 0.24f), fistUp = Arg(p, 2, 0.14f), cross = true,
                                  crossFwd = Arg(p, 3, 0.52f), crossUp = Arg(p, 4, 0.28f), lay = Arg(p, 5, 0.035f), straighten = Arg(p, 6, 0.9f) };
            // the guard holds one moment of the stance: across the idle's breath the wrist limits moved the cross by up to
            // 9 cm (the blades parted); a guard is held still
            Author("GK_DualGuard", 20, true, -1f, t => g, 0.3f);
            Author("GK_DualGuardHit", 12, false, 0.42f, t =>
            {
                float k = t < 0.1f ? Mathf.SmoothStep(0f, 1f, t / 0.1f) : Mathf.SmoothStep(1f, 0f, (t - 0.1f) / 0.32f);
                var h = g; h.fistFwd -= 0.09f * k; h.crossFwd -= 0.1f * k; h.fistUp += 0.03f * k; h.crossUp += 0.02f * k;
                return h;
            }, 0.3f);
        }

        [GKCommand("makedualidle", "makedualidle [fwd apart up dirFwd dirUp dirOut]: author the two-sword idle (arms low and forward, blades forward and down) and measure it (edit mode)")]
        static void IdleCmd(string arg)
        {
            var p = (arg ?? "").Trim().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            var s = new ArmSpec { fistFwd = Arg(p, 0, 0.20f), apart = Arg(p, 1, 0.30f), fistUp = Arg(p, 2, -0.36f),
                                  dirFwd = Arg(p, 3, 0.85f), dirUp = Arg(p, 4, -0.45f), dirOut = Arg(p, 5, 0.2f), straighten = Arg(p, 6, 0.35f) };
            // on the relaxed, upright standing idle: the one-handed combat stance hunched the shoulders and tucked the
            // arms against the hips
            Author("GK_DualIdle", 24, true, -1f, t => s, -1f, "AX_unarmed_idle");
        }

        /// <summary>Writes Animations/Generated/[name].anim (see the class summary). <paramref name="length"/> &lt; 0:
        /// the base idle's own length.</summary>
        static void Author(string name, int frames, bool loop, float length, System.Func<float, ArmSpec> specAt, float fixedBase = -1f, string baseName = "KI_CombatIdle1H")
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log(name + ": stop play mode first"); return; }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKHeroCC.CleanPrefabPath);
            var baseClip = GKPreview.Clip(baseName) ?? GKPreview.Clip("KI_CombatIdle1H") ?? GKPreview.Clip("SS_Idle");
            if (prefab == null || baseClip == null) { GKBridge.Log(name + ": knight or base clip missing"); return; }
            string path = "Assets/_GeorgianKnight/Animations/Generated/" + name + ".anim";
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Vector3 shotAt = new Vector3(0f, -500f, 0f);
            go.transform.SetPositionAndRotation(shotAt, Quaternion.identity);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false;
            var anim = go.GetComponent<Animator>();
            string tmp = "Assets/_GeorgianKnight/Generated/~guard.controller";
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(tmp);
            try
            {
                var st = ctrl.layers[0].stateMachine.AddState("Base"); st.motion = baseClip;
                anim.runtimeAnimatorController = ctrl; anim.applyRootMotion = false;
                var combat = go.GetComponent<Combatant>();
                if (combat == null || combat.weapon == null || combat.offWeapon == null) { GKBridge.Log(name + ": the knight needs both swords (herocc clean)"); return; }
                Transform root = go.transform;
                float dur = length > 0f ? length : baseClip.length;
                var curves = new Dictionary<string, AnimationCurve>();
                var hph = new HumanPoseHandler(anim.avatar, root);
                var pose = new HumanPose();
                anim.Rebind();
                for (int f = 0; f <= frames; f++)
                {
                    float u = f / (float)frames;
                    float baseT = fixedBase >= 0f ? fixedBase : loop ? u : Mathf.Min(0.999f, u * dur / baseClip.length);
                    anim.Play("Base", 0, baseT);
                    anim.Update(0f);
                    root.SetPositionAndRotation(shotAt, Quaternion.identity);
                    Pose(anim, combat, root, specAt(u * dur));
                    if (f == 0) GKBridge.Log(name + " solved (before muscles): wrists R " + WristBend(anim, false).ToString("F0") + " L " + WristBend(anim, true).ToString("F0"));
                    // the body's place relative to the character, not where this model stands for the shots
                    root.position = Vector3.zero;
                    hph.GetHumanPose(ref pose);
                    root.position = shotAt;
                    float time = u * dur;
                    for (int i = 0; i < pose.muscles.Length; i++) Key(curves, CurveName(HumanTrait.MuscleName[i]), time, pose.muscles[i]);
                    Key(curves, "RootT.x", time, 0f); Key(curves, "RootT.y", time, pose.bodyPosition.y); Key(curves, "RootT.z", time, 0f);
                    Key(curves, "RootQ.x", time, pose.bodyRotation.x); Key(curves, "RootQ.y", time, pose.bodyRotation.y);
                    Key(curves, "RootQ.z", time, pose.bodyRotation.z); Key(curves, "RootQ.w", time, pose.bodyRotation.w);
                }
                var clip = new AnimationClip { name = name, frameRate = 30f };
                foreach (var kv in curves)
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), kv.Key), kv.Value);
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = loop;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
                GKPaths.Ensure(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
                var old = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (old != null) { EditorUtility.CopySerialized(clip, old); clip = old; EditorUtility.SetDirty(old); }
                else AssetDatabase.CreateAsset(clip, path);
                AssetDatabase.SaveAssets();

                // play it back (muscles, as the game will) and measure, at the start and half way
                var gs = ctrl.layers[0].stateMachine.AddState("Out"); gs.motion = clip;
                var sb = new StringBuilder(name + ": " + path);
                foreach (float at in new[] { 0f, 0.5f })
                {
                    anim.Rebind(); anim.Play("Out", 0, at); anim.Update(0f);
                    root.SetPositionAndRotation(shotAt, Quaternion.identity);
                    sb.Append(" | t" + at.ToString("F1") + ": " + Measure(anim, combat, root));
                }
                GKBridge.Log(sb.ToString());
            }
            finally
            {
                Object.DestroyImmediate(go);
                AssetDatabase.DeleteAsset(tmp);
            }
        }

        static void Key(Dictionary<string, AnimationCurve> curves, string prop, float t, float v)
        {
            AnimationCurve c;
            if (!curves.TryGetValue(prop, out c)) { c = new AnimationCurve(); curves[prop] = c; }
            c.AddKey(new Keyframe(t, v));
        }

        /// <summary>Sets both arms to the spec (two-bone solve, then the wrist aims the blade), three passes.</summary>
        static void Pose(Animator anim, Combatant combat, Transform root, ArmSpec s)
        {
            Vector3 fwd = root.forward, right = root.right, up = Vector3.up;
            Transform chest = anim.GetBoneTransform(HumanBodyBones.UpperChest) ?? anim.GetBoneTransform(HumanBodyBones.Chest);
            Vector3 c = chest.position;
            for (int side = 0; side < 2; side++)
            {
                bool r = side == 0;
                Vector3 outward = r ? right : -right;
                Transform ua = anim.GetBoneTransform(r ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm);
                Transform la = anim.GetBoneTransform(r ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm);
                Transform hd = anim.GetBoneTransform(r ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
                var blade = r ? combat.weapon : combat.offWeapon;
                Vector3 fist = c + fwd * s.fistFwd + outward * s.apart + up * s.fistUp;
                Vector3 pole = fist + outward * 0.35f - fwd * 0.15f - up * 0.25f;
                Transform mid = anim.GetBoneTransform(r ? HumanBodyBones.RightMiddleProximal : HumanBodyBones.LeftMiddleProximal);
                for (int it = 0; it < 3; it++)
                {
                    TwoBone(ua, la, hd, fist, pole);
                    // the blade runs through a point: the cross, or a point down the wanted direction
                    Vector3 through = s.cross
                        ? c + fwd * s.crossFwd + up * s.crossUp + fwd * (r ? s.lay : -s.lay)
                        : blade.bladeBase.position + (fwd * s.dirFwd + up * s.dirUp + outward * s.dirOut).normalized * 0.6f;
                    AimBlade(hd, blade, through, fwd);
                }
                // a fighter aims the blade with the whole forearm, not by cranking the wrist: put the forearm on the line
                // of the hand (wrist near straight) and slide the fist along its own blade until the upper arm reaches
                // the new elbow, so the blade still runs where it was aimed
                for (int pass = 0; mid != null && s.straighten > 0f && pass < 4; pass++)
                {
                    Quaternion handRot = hd.rotation;
                    Vector3 bdir = (blade.bladeTip.position - blade.bladeBase.position).normalized;
                    Vector3 handAxis = (mid.position - hd.position).normalized;
                    Vector3 foreNow = (hd.position - la.position).normalized;
                    Vector3 d = Vector3.Slerp(foreNow, handAxis, s.straighten).normalized;
                    float a = Vector3.Distance(ua.position, la.position), b = Vector3.Distance(la.position, hd.position);
                    Vector3 S = ua.position;
                    Vector3 q = hd.position - d * b - S;
                    float qb = Vector3.Dot(q, bdir), disc = qb * qb - q.sqrMagnitude + a * a;
                    if (disc >= 0f)
                    {
                        float t1 = -qb + Mathf.Sqrt(disc), t2 = -qb - Mathf.Sqrt(disc);
                        float t = Mathf.Abs(t1) < Mathf.Abs(t2) ? t1 : t2;
                        if (Mathf.Abs(t) < 0.4f)
                        {
                            Vector3 fist2 = hd.position + bdir * t;
                            Vector3 elbow = fist2 - d * b;
                            ua.rotation = Quaternion.FromToRotation(la.position - ua.position, elbow - S) * ua.rotation;
                            la.rotation = Quaternion.FromToRotation(hd.position - la.position, fist2 - la.position) * la.rotation;
                            hd.rotation = handRot;
                            continue;
                        }
                    }
                    // the arm cannot quite reach that line: slide the fist along its blade to the nearest point and
                    // swing the elbow round toward the hand's line from there
                    float tc = Mathf.Clamp(-qb, -0.4f, 0.4f);
                    Vector3 fistC = hd.position + bdir * tc;
                    TwoBone(ua, la, hd, fistC, fistC - d * b);
                    hd.rotation = handRot;
                }
            }
        }

        /// <summary>Upper arm and forearm turned so the wrist reaches <paramref name="target"/>, the elbow bending
        /// toward <paramref name="pole"/>.</summary>
        static void TwoBone(Transform ua, Transform la, Transform hd, Vector3 target, Vector3 pole)
        {
            Vector3 s = ua.position;
            float a = Vector3.Distance(ua.position, la.position), b = Vector3.Distance(la.position, hd.position);
            Vector3 toT = target - s;
            float d = Mathf.Clamp(toT.magnitude, Mathf.Abs(a - b) + 1e-3f, a + b - 1e-3f);
            Vector3 dir = toT.normalized;
            // elbow: on the circle around the shoulder-target line, toward the pole
            float x = (a * a - b * b + d * d) / (2f * d);
            float h = Mathf.Sqrt(Mathf.Max(0f, a * a - x * x));
            Vector3 polePerp = Vector3.ProjectOnPlane(pole - s, dir).normalized;
            Vector3 elbow = s + dir * x + polePerp * h;
            ua.rotation = Quaternion.FromToRotation(la.position - ua.position, elbow - s) * ua.rotation;
            la.rotation = Quaternion.FromToRotation(hd.position - la.position, (s + dir * d) - la.position) * la.rotation;
        }

        /// <summary>Turns the wrist so the blade, from its base in the fist, runs through <paramref name="through"/>,
        /// flats to the front (the width across the body), choosing the turn nearer to the wrist as it is.</summary>
        static void AimBlade(Transform hd, WeaponHitbox blade, Vector3 through, Vector3 fwd)
        {
            Transform sw = blade.transform;
            var sr = sw.Find("SheathRef");
            Vector3 bIn = Quaternion.Inverse(hd.rotation) * (blade.bladeTip.position - blade.bladeBase.position).normalized;
            Vector3 wIn = Quaternion.Inverse(hd.rotation) * (sr != null ? sr.up : sw.up);
            wIn = Vector3.ProjectOnPlane(wIn, bIn).normalized;
            Vector3 baseIn = Quaternion.Inverse(hd.rotation) * (blade.bladeBase.position - hd.position);
            Quaternion now = hd.rotation, bone = now;
            Vector3 bdir = (through - blade.bladeBase.position).normalized;
            for (int it = 0; it < 4; it++)
            {
                Vector3 wd = Vector3.Cross(fwd, bdir).normalized;
                if (wd.sqrMagnitude < 1e-6f) wd = Vector3.up;
                if (Vector3.Dot(now * wIn, wd) < 0f) wd = -wd;
                bone = Quaternion.LookRotation(bdir, wd) * Quaternion.Inverse(Quaternion.LookRotation(bIn, wIn));
                bdir = (through - (hd.position + bone * baseIn)).normalized;
            }
            hd.rotation = bone;
        }

        static string Measure(Animator anim, Combatant c, Transform root)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            Vector3 a0 = c.weapon.bladeBase.position, a1 = c.weapon.bladeTip.position, b0 = c.offWeapon.bladeBase.position, b1 = c.offWeapon.bladeTip.position;
            Vector3 pa, pb;
            ScabbardMotion.ClosestPointsSegments(a0, a1, b0, b1, out pa, out pb);
            Vector3 x = (pa + pb) * 0.5f;
            var head = anim.GetBoneTransform(HumanBodyBones.Head);
            var chest = anim.GetBoneTransform(HumanBodyBones.Chest);
            var rh = anim.GetBoneTransform(HumanBodyBones.RightHand); var lh = anim.GetBoneTransform(HumanBodyBones.LeftHand);
            Vector3 eyes = head.position + Vector3.up * 0.08f;
            float faceToBlade = Mathf.Min(SegDist(eyes, a0, a1), SegDist(eyes, b0, b1));
            float bodyToBlade = Mathf.Min(SegDist(chest.position, a0, a1), SegDist(chest.position, b0, b1));
            var hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            float legToBlade = Mathf.Min(SegDist(hips.position, a0, a1), SegDist(hips.position, b0, b1));
            Vector3 f = root.forward;
            float pitchR = Mathf.Asin(Mathf.Clamp((a1 - a0).normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
            float pitchL = Mathf.Asin(Mathf.Clamp((b1 - b0).normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
            var sb = new StringBuilder();
            sb.Append("blade gap ").Append((Vector3.Distance(pa, pb) * 100f).ToString("F1", ci))
              .Append(" cm, angle ").Append(Vector3.Angle(a1 - a0, b1 - b0).ToString("F0", ci))
              .Append(", blade pitch R ").Append(pitchR.ToString("F0", ci)).Append(" L ").Append(pitchL.ToString("F0", ci))
              .Append(", cross ").Append(((eyes.y - x.y) * 100f).ToString("F0", ci)).Append(" cm below the eyes ")
              .Append((Vector3.Dot(x - chest.position, f) * 100f).ToString("F0", ci)).Append(" cm before the chest")
              .Append("; fists ").Append((Vector3.Distance(rh.position, lh.position) * 100f).ToString("F0", ci)).Append(" cm apart")
              .Append("; blades keep ").Append((faceToBlade * 100f).ToString("F0", ci)).Append(" cm from the eyes, ")
              .Append((bodyToBlade * 100f).ToString("F0", ci)).Append(" from the chest, ")
              .Append((legToBlade * 100f).ToString("F0", ci)).Append(" from the hips")
              .Append("; wrists R ").Append(WristBend(anim, false).ToString("F0", ci)).Append(" L ").Append(WristBend(anim, true).ToString("F0", ci)).Append(" deg")
              .Append("; upper arms rise R ").Append(Rise(anim, false).ToString("F0", ci)).Append(" L ").Append(Rise(anim, true).ToString("F0", ci))
              .Append(", shoulders shrug R ").Append(Shrug(anim, false, root).ToString("F1", ci)).Append(" L ").Append(Shrug(anim, true, root).ToString("F1", ci)).Append(" cm");
            return sb.ToString();
        }

        static float Rise(Animator anim, bool left)
        {
            var ua = anim.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
            var la = anim.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
            return Mathf.Asin(Mathf.Clamp((la.position - ua.position).normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        /// <summary>How high the shoulder joint sits above the neck's base, compared with the bind pose (cm): a shrug.</summary>
        static float Shrug(Animator anim, bool left, Transform root)
        {
            var ua = anim.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
            var neck = anim.GetBoneTransform(HumanBodyBones.Neck);
            return (ua.position.y - neck.position.y) * 100f;
        }

        static float WristBend(Animator anim, bool left)
        {
            var la = anim.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
            var h = anim.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            var m = anim.GetBoneTransform(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            return Vector3.Angle(h.position - la.position, m.position - h.position);
        }

        static float SegDist(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
            return Vector3.Distance(p, a + ab * t);
        }

        /// <summary>HumanTrait's muscle names to the clip curve names (fingers are "LeftHand.Index.1 Stretched").</summary>
        static string CurveName(string muscle)
        {
            foreach (var side in new[] { "Left", "Right" })
                foreach (var f in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
                {
                    string pre = side + " " + f + " ";
                    if (!muscle.StartsWith(pre)) continue;
                    string rest = muscle.Substring(pre.Length);   // "1 Stretched" / "Spread"
                    if (rest == "Spread") return side + "Hand." + f + ".Spread";
                    return side + "Hand." + f + "." + rest;
                }
            return muscle;
        }
    }
}
