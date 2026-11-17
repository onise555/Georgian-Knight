using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Keeps the arms inside what a human arm can do, after every procedural pose fix (guard IK, sword carry, grip).
    /// Ranges are measured, not guessed: <c>jointaudit</c> samples every clip of the animator on this skeleton and
    /// stores, per arm, the elbow's hinge axis and the range of every joint angle the animations themselves use.
    /// Each frame this then
    /// <list type="bullet">
    /// <item>makes the elbow a hinge again (removes the sideways bend an IK can introduce, within what the clips do),
    /// clamps its bend and the forearm's roll (pronation / supination);</item>
    /// <item>clamps the wrist: its two bend directions (flexion / extension, radial / ulnar deviation) and its roll.</item>
    /// </list>
    /// It also measures (for QA) every joint angle it knows, so a twisted wrist shows up as a number.
    /// The hand keeps its world rotation through the elbow fix, so only a truly impossible wrist is changed.
    /// </summary>
    [DefaultExecutionOrder(168)]   // after SwordArmPose (164) and HandGrip (165); before CCHelperBones (170) and cloth
    public sealed class AnatomyLimiter : MonoBehaviour
    {
        [System.Serializable]
        public class ArmRanges
        {
            [Tooltip("Elbow hinge axis in the upper arm's frame (measured from the clips).")]
            public Vector3 hinge = Vector3.right;
            public Vector2 flex = new Vector2(-5f, 150f);
            [Tooltip("Largest sideways (off-hinge) bend the clips themselves show (deg).")]
            public float offHinge = 12f;
            public Vector2 forearmRoll = new Vector2(-90f, 90f);
            public Vector2 wristA = new Vector2(-70f, 70f);
            public Vector2 wristB = new Vector2(-35f, 35f);
            public Vector2 wristRoll = new Vector2(-40f, 40f);
            [Tooltip("Whole forearm turn (forearm roll + wrist roll): pronation / supination.")]
            public Vector2 pronation = new Vector2(-120f, 80f);
            [Tooltip("Upper arm turning about its own axis at the shoulder.")]
            public Vector2 humerusRoll = new Vector2(-70f, 55f);
            public bool measured;
        }

        public bool limit = true;
        [Tooltip("Limit only while the character is free (idle, moving, guarding): that is where the procedural pose fixes act. Attacks and reactions keep their clips untouched (their swings are calibrated on them).")]
        public bool onlyWhenFree = true;
        [Tooltip("Extra room beyond what the clips use (deg).")]
        public float margin = 6f;
        public ArmRanges left = new ArmRanges(), right = new ArmRanges();

        /// <summary>Metric names, in the order of <see cref="Measure"/>.</summary>
        public static readonly string[] Names =
        {
            "R.elbowFlex", "R.elbowOffHinge", "R.forearmRoll", "R.wristA", "R.wristB", "R.wristRoll", "R.pronation", "R.humerusRoll", "R.shoulderSwing",
            "L.elbowFlex", "L.elbowOffHinge", "L.forearmRoll", "L.wristA", "L.wristB", "L.wristRoll", "L.pronation", "L.humerusRoll", "L.shoulderSwing",
            "neckTwist", "neckBendA", "neckBendB", "spineTwist", "spineBend",
            "L.fingerCurl", "L.fingerSpread", "R.fingerCurl",
            "R.kneeFlex", "R.kneeOffHinge", "R.calfRoll", "R.ankleA", "R.ankleB", "R.ankleRoll", "R.hipRoll", "R.hipSwing", "R.toeHeight", "R.heelHeight",
            "L.kneeFlex", "L.kneeOffHinge", "L.calfRoll", "L.ankleA", "L.ankleB", "L.ankleRoll", "L.hipRoll", "L.hipSwing", "L.toeHeight", "L.heelHeight"
        };

        [Tooltip("Knee hinge axes in the thigh's frame (measured from the clips).")]
        public Vector3 kneeHingeR = Vector3.right, kneeHingeL = Vector3.right;

        sealed class Arm
        {
            public Transform clav, upper, fore, hand;
            public Quaternion shBind, elBind, wrBind;        // child rotation in the parent's frame at bind
            public Vector3 upAxis, foAxis, haAxis;            // bone axes (toward the child) in the parent's frame at bind
            public Transform[][] fingers;
        }

        Combatant self;
        Arm L, R, LL, RL;   // arms; legs (clav = pelvis, upper = thigh, fore = calf, hand = foot)
        Transform toesL, toesR;
        Transform hips, chest, head;
        Quaternion neckBind, spineBind;
        Vector3 neckAxis, spineAxis;
        bool ready;

        /// <summary>Largest correction applied last frame (deg), for QA.</summary>
        public float LastCorrection { get; private set; }

        void Awake() { Init(); }

        /// <summary>Captures the bind pose (call on a freshly instantiated prefab, before any animation).</summary>
        public void Init()
        {
            var anim = GetComponent<Animator>();
            if (anim == null || !anim.isHuman) return;
            self = GetComponent<Combatant>();
            System.Func<HumanBodyBones, Transform> B = anim.GetBoneTransform;
            L = MakeArm(B(HumanBodyBones.LeftShoulder), B(HumanBodyBones.LeftUpperArm), B(HumanBodyBones.LeftLowerArm), B(HumanBodyBones.LeftHand), anim, true);
            R = MakeArm(B(HumanBodyBones.RightShoulder), B(HumanBodyBones.RightUpperArm), B(HumanBodyBones.RightLowerArm), B(HumanBodyBones.RightHand), anim, false);
            hips = B(HumanBodyBones.Hips);
            LL = MakeLeg(hips, B(HumanBodyBones.LeftUpperLeg), B(HumanBodyBones.LeftLowerLeg), B(HumanBodyBones.LeftFoot), B(HumanBodyBones.LeftToes));
            RL = MakeLeg(hips, B(HumanBodyBones.RightUpperLeg), B(HumanBodyBones.RightLowerLeg), B(HumanBodyBones.RightFoot), B(HumanBodyBones.RightToes));
            toesL = B(HumanBodyBones.LeftToes); toesR = B(HumanBodyBones.RightToes);
            chest = B(HumanBodyBones.UpperChest) ?? B(HumanBodyBones.Chest);
            head = B(HumanBodyBones.Head);
            if (chest != null && head != null)
            {
                neckBind = Quaternion.Inverse(chest.rotation) * head.rotation;
                neckAxis = Quaternion.Inverse(chest.rotation) * Vector3.up;
            }
            if (hips != null && chest != null)
            {
                spineBind = Quaternion.Inverse(hips.rotation) * chest.rotation;
                spineAxis = Quaternion.Inverse(hips.rotation) * (chest.position - hips.position).normalized;
            }
            ready = L != null && R != null && hips != null && chest != null && head != null;
        }

        static Arm MakeLeg(Transform pelvis, Transform thigh, Transform calf, Transform foot, Transform toes)
        {
            if (pelvis == null || thigh == null || calf == null || foot == null) return null;
            var a = new Arm { clav = pelvis, upper = thigh, fore = calf, hand = foot, fingers = new Transform[0][] };
            a.shBind = Quaternion.Inverse(pelvis.rotation) * thigh.rotation;
            a.elBind = Quaternion.Inverse(thigh.rotation) * calf.rotation;
            a.wrBind = Quaternion.Inverse(calf.rotation) * foot.rotation;
            a.upAxis = Quaternion.Inverse(pelvis.rotation) * (calf.position - thigh.position).normalized;
            a.foAxis = Quaternion.Inverse(thigh.rotation) * (foot.position - calf.position).normalized;
            // the ankle's own axis is the shin (the foot points forward, not along the leg)
            a.haAxis = Quaternion.Inverse(calf.rotation) * (foot.position - calf.position).normalized;
            return a;
        }

        static Arm MakeArm(Transform clav, Transform upper, Transform fore, Transform hand, Animator anim, bool isLeft)
        {
            if (clav == null || upper == null || fore == null || hand == null) return null;
            var a = new Arm { clav = clav, upper = upper, fore = fore, hand = hand };
            a.shBind = Quaternion.Inverse(clav.rotation) * upper.rotation;
            a.elBind = Quaternion.Inverse(upper.rotation) * fore.rotation;
            a.wrBind = Quaternion.Inverse(fore.rotation) * hand.rotation;
            a.upAxis = Quaternion.Inverse(clav.rotation) * (fore.position - upper.position).normalized;
            a.foAxis = Quaternion.Inverse(upper.rotation) * (hand.position - fore.position).normalized;
            Transform mid = anim.GetBoneTransform(isLeft ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            a.haAxis = Quaternion.Inverse(fore.rotation) * ((mid != null ? mid.position : hand.position + hand.up * 0.08f) - hand.position).normalized;
            HumanBodyBones[][] ids = isLeft
                ? new[] { new[] { HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal },
                          new[] { HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal },
                          new[] { HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal },
                          new[] { HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal } }
                : new[] { new[] { HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal },
                          new[] { HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal },
                          new[] { HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal },
                          new[] { HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal } };
            a.fingers = new Transform[ids.Length][];
            for (int f = 0; f < ids.Length; f++)
            {
                a.fingers[f] = new Transform[3];
                for (int j = 0; j < 3; j++) a.fingers[f][j] = anim.GetBoneTransform(ids[f][j]);
            }
            return a;
        }

        // ------------------------------------------------------------------ decomposition

        /// <summary>q = swing * twist, twist about <paramref name="axis"/>; twist in degrees (-180, 180].</summary>
        public static void SwingTwist(Quaternion q, Vector3 axis, out Quaternion swing, out float twistDeg)
        {
            Vector3 r = new Vector3(q.x, q.y, q.z);
            Vector3 p = Vector3.Dot(r, axis) * axis;
            float m = Mathf.Sqrt(p.sqrMagnitude + q.w * q.w);
            Quaternion t = m < 1e-8f ? Quaternion.identity : new Quaternion(p.x / m, p.y / m, p.z / m, q.w / m);
            swing = q * Quaternion.Inverse(t);
            twistDeg = 2f * Mathf.Atan2(Vector3.Dot(new Vector3(t.x, t.y, t.z), axis), t.w) * Mathf.Rad2Deg;
            if (twistDeg > 180f) twistDeg -= 360f; else if (twistDeg <= -180f) twistDeg += 360f;
        }

        /// <summary>Rotation vector (axis * degrees), shortest way.</summary>
        public static Vector3 RotVec(Quaternion q)
        {
            if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            Vector3 v = new Vector3(q.x, q.y, q.z);
            float s = v.magnitude;
            if (s < 1e-7f) return Vector3.zero;
            float ang = 2f * Mathf.Atan2(s, q.w) * Mathf.Rad2Deg;
            return v / s * ang;
        }

        static Quaternion FromRotVec(Vector3 v)
        {
            float a = v.magnitude;
            return a < 1e-5f ? Quaternion.identity : Quaternion.AngleAxis(a, v / a);
        }

        static void Basis(Vector3 axis, out Vector3 e1, out Vector3 e2)
        {
            e1 = Vector3.ProjectOnPlane(Mathf.Abs(Vector3.Dot(axis, Vector3.right)) < 0.9f ? Vector3.right : Vector3.forward, axis).normalized;
            e2 = Vector3.Cross(axis, e1);
        }

        // elbow: forearm relative to upper arm, against the bind
        static void Elbow(Arm a, out Quaternion q, out Vector3 swingVec, out float roll)
        {
            q = (Quaternion.Inverse(a.upper.rotation) * a.fore.rotation) * Quaternion.Inverse(a.elBind);
            Quaternion swing;
            SwingTwist(q, a.foAxis, out swing, out roll);
            swingVec = RotVec(swing);
        }

        static void Wrist(Arm a, out Vector3 swingVec, out float roll)
        {
            Quaternion q = (Quaternion.Inverse(a.fore.rotation) * a.hand.rotation) * Quaternion.Inverse(a.wrBind);
            Quaternion swing;
            SwingTwist(q, a.haAxis, out swing, out roll);
            swingVec = RotVec(swing);
        }

        // ------------------------------------------------------------------ measure (QA and the clip audit)

        /// <summary>All joint angles of the current pose, in the order of <see cref="Names"/> (degrees).</summary>
        public float[] Measure()
        {
            var m = new float[Names.Length];
            if (!ready) return m;
            int k = 0;
            foreach (var arm in new[] { R, L })
            {
                var rg = arm == R ? right : left;
                Quaternion q; Vector3 sv; float roll;
                Elbow(arm, out q, out sv, out roll);
                Vector3 h = rg.hinge.sqrMagnitude > 0.5f ? rg.hinge.normalized : Vector3.right;
                float flex = Vector3.Dot(sv, h);
                m[k++] = flex;
                m[k++] = (sv - flex * h).magnitude;
                m[k++] = roll;
                Vector3 wv; float wroll;
                Wrist(arm, out wv, out wroll);
                Vector3 e1, e2; Basis(arm.haAxis, out e1, out e2);
                m[k++] = Vector3.Dot(wv, e1);
                m[k++] = Vector3.Dot(wv, e2);
                m[k++] = wroll;
                m[k++] = roll + wroll;
                Quaternion sq = (Quaternion.Inverse(arm.clav.rotation) * arm.upper.rotation) * Quaternion.Inverse(arm.shBind);
                Quaternion ssw; float hroll;
                SwingTwist(sq, arm.upAxis, out ssw, out hroll);
                m[k++] = hroll;
                m[k++] = RotVec(ssw).magnitude;
            }
            {
                Quaternion nq = (Quaternion.Inverse(chest.rotation) * head.rotation) * Quaternion.Inverse(neckBind);
                Quaternion nsw; float ntw;
                SwingTwist(nq, neckAxis, out nsw, out ntw);
                Vector3 nv = RotVec(nsw); Vector3 e1, e2; Basis(neckAxis, out e1, out e2);
                m[k++] = ntw; m[k++] = Vector3.Dot(nv, e1); m[k++] = Vector3.Dot(nv, e2);
                Quaternion cq = (Quaternion.Inverse(hips.rotation) * chest.rotation) * Quaternion.Inverse(spineBind);
                Quaternion csw; float ctw;
                SwingTwist(cq, spineAxis, out csw, out ctw);
                m[k++] = ctw; m[k++] = RotVec(csw).magnitude;
            }
            float spread;
            m[k++] = Curl(L, out spread);
            m[k++] = spread;
            m[k++] = Curl(R, out spread);
            foreach (var leg in new[] { RL, LL })
            {
                if (leg == null) { k += 10; continue; }
                Vector3 h = (leg == RL ? kneeHingeR : kneeHingeL).normalized;
                Quaternion q; Vector3 sv; float roll;
                Elbow(leg, out q, out sv, out roll);
                float flex = Vector3.Dot(sv, h);
                m[k++] = flex; m[k++] = (sv - flex * h).magnitude; m[k++] = roll;
                Vector3 wv; float wroll;
                Wrist(leg, out wv, out wroll);
                Vector3 e1, e2; Basis(leg.haAxis, out e1, out e2);
                m[k++] = Vector3.Dot(wv, e1); m[k++] = Vector3.Dot(wv, e2); m[k++] = wroll;
                Quaternion sq = (Quaternion.Inverse(leg.clav.rotation) * leg.upper.rotation) * Quaternion.Inverse(leg.shBind);
                Quaternion ssw; float hroll;
                SwingTwist(sq, leg.upAxis, out ssw, out hroll);
                m[k++] = hroll; m[k++] = RotVec(ssw).magnitude;
                // the feet against the ground the character stands on (its root): negative = through the floor
                Transform toes = leg == RL ? toesR : toesL;
                float ground = transform.position.y;
                m[k++] = (toes != null ? toes.position.y : leg.hand.position.y) - ground;
                m[k++] = leg.hand.position.y - ground;
            }
            return m;
        }

        /// <summary>Mean curl of the four fingers (sum of the three joint bends, deg) and the index-to-little spread.</summary>
        static float Curl(Arm a, out float spread)
        {
            float sum = 0f; int n = 0; spread = 0f;
            Vector3 palmFwd = Vector3.zero;
            foreach (var f in a.fingers)
            {
                if (f[0] == null || f[1] == null || f[2] == null) continue;
                Vector3 meta = (f[0].position - a.hand.position).normalized;
                Vector3 s0 = f[1].position - f[0].position, s1 = f[2].position - f[1].position;
                Vector3 s2 = f[2].childCount > 0 ? f[2].GetChild(0).position - f[2].position : s1;
                sum += Vector3.Angle(meta, s0) + Vector3.Angle(s0, s1) + Vector3.Angle(s1, s2);
                n++;
            }
            if (a.fingers[0][0] != null && a.fingers[0][1] != null && a.fingers[3][0] != null && a.fingers[3][1] != null)
                spread = Vector3.Angle(a.fingers[0][1].position - a.fingers[0][0].position, a.fingers[3][1].position - a.fingers[3][0].position);
            return n > 0 ? sum / n : 0f;
        }

        // ------------------------------------------------------------------ clip audit support

        /// <summary>Hinge axis estimate for one pose: the elbow's swing direction (upper-arm frame), weighted by its angle.</summary>
        public void HingeSample(bool isRight, ref Vector3 acc) { HingeSample(isRight ? R : L, ref acc); }
        public void KneeSample(bool isRight, ref Vector3 acc) { HingeSample(isRight ? RL : LL, ref acc); }

        void HingeSample(Arm arm, ref Vector3 acc)
        {
            if (!ready || arm == null) return;
            Quaternion q; Vector3 sv; float roll;
            Elbow(arm, out q, out sv, out roll);
            if (sv.magnitude < 25f) return;
            if (acc.sqrMagnitude > 1e-6f && Vector3.Dot(sv, acc) < 0f) sv = -sv;
            acc += sv;
        }

        // ------------------------------------------------------------------ limit

        void LateUpdate()
        {
            if (!ready || !limit) { LastCorrection = 0f; return; }
            if (onlyWhenFree && self != null && self.CurrentState != Combatant.State.Free) { LastCorrection = 0f; return; }
            float worst = 0f;
            if (right.measured) worst = Mathf.Max(worst, LimitArm(R, right));
            if (left.measured) worst = Mathf.Max(worst, LimitArm(L, left));
            LastCorrection = worst;
        }

        float LimitArm(Arm a, ArmRanges rg)
        {
            float worst = 0f;
            Vector3 h = rg.hinge.normalized;
            // shoulder: the upper arm may not spin about its own axis further than any clip does (an IK swivel flipped
            // it ~170 degrees and the whole arm read as twisted)
            {
                Quaternion sq = (Quaternion.Inverse(a.clav.rotation) * a.upper.rotation) * Quaternion.Inverse(a.shBind);
                Quaternion ssw; float hroll;
                SwingTwist(sq, a.upAxis, out ssw, out hroll);
                float hrollC = Mathf.Clamp(hroll, rg.humerusRoll.x - margin, rg.humerusRoll.y + margin);
                if (Mathf.Abs(hrollC - hroll) > 0.2f)
                {
                    Quaternion handWorld = a.hand.rotation, foreWorld = a.fore.rotation;
                    a.upper.rotation = a.clav.rotation * (ssw * Quaternion.AngleAxis(hrollC, a.upAxis)) * a.shBind;
                    a.fore.rotation = foreWorld;     // the elbow keeps its bend; the elbow fix below re-hinges it
                    a.hand.rotation = handWorld;
                    worst = Mathf.Max(worst, Mathf.Abs(hrollC - hroll));
                }
            }
            // elbow: a hinge with the clips' own sideways slack, bend and roll in range
            Quaternion q; Vector3 sv; float roll;
            Elbow(a, out q, out sv, out roll);
            float flex = Vector3.Dot(sv, h);
            Vector3 side = sv - flex * h;
            float sideMax = rg.offHinge + margin * 0.5f;
            float flexC = Mathf.Clamp(flex, rg.flex.x - margin, rg.flex.y + margin);
            float rollC = Mathf.Clamp(roll, rg.forearmRoll.x - margin, rg.forearmRoll.y + margin);
            Vector3 sideC = side.magnitude > sideMax ? side.normalized * sideMax : side;
            float elbowFix = Mathf.Max(Mathf.Abs(flexC - flex), Mathf.Max(Mathf.Abs(rollC - roll), side.magnitude - sideC.magnitude));
            if (elbowFix > 0.2f)
            {
                Quaternion handWorld = a.hand.rotation;
                Quaternion qc = FromRotVec(flexC * h + sideC) * Quaternion.AngleAxis(rollC, a.foAxis);
                a.fore.rotation = a.upper.rotation * qc * a.elBind;
                a.hand.rotation = handWorld;                    // the wrist absorbs it, then gets its own limits
                worst = Mathf.Max(worst, elbowFix);
            }
            // wrist: two bends and the roll
            Vector3 wv; float wroll;
            Wrist(a, out wv, out wroll);
            Vector3 e1, e2; Basis(a.haAxis, out e1, out e2);
            float A = Vector3.Dot(wv, e1), Bv = Vector3.Dot(wv, e2);
            float Ac = Mathf.Clamp(A, rg.wristA.x - margin, rg.wristA.y + margin);
            float Bc = Mathf.Clamp(Bv, rg.wristB.x - margin, rg.wristB.y + margin);
            float wrollC = Mathf.Clamp(wroll, rg.wristRoll.x - margin, rg.wristRoll.y + margin);
            // the whole pronation (forearm + wrist) within what the clips do: the wrist gives up the excess
            {
                Quaternion q2; Vector3 sv2; float froll;
                Elbow(a, out q2, out sv2, out froll);
                float total = froll + wrollC;
                float totalC = Mathf.Clamp(total, rg.pronation.x - margin, rg.pronation.y + margin);
                wrollC += totalC - total;
            }
            float wristFix = Mathf.Max(Mathf.Abs(Ac - A), Mathf.Max(Mathf.Abs(Bc - Bv), Mathf.Abs(wrollC - wroll)));
            if (wristFix > 0.2f)
            {
                Quaternion wq = FromRotVec(Ac * e1 + Bc * e2) * Quaternion.AngleAxis(wrollC, a.haAxis);
                a.hand.rotation = a.fore.rotation * wq * a.wrBind;
                worst = Mathf.Max(worst, wristFix);
            }
            return worst;
        }

        // ------------------------------------------------------------------ hinge-correct two-bone IK

        /// <summary>Puts the wrist at <paramref name="target"/>: the elbow bends only about its measured hinge (the
        /// forearm keeps its roll), the shoulder aims the arm, and the arm swivels so the elbow points as close to
        /// <paramref name="pole"/> as it can. Returns how far the wrist ended from the target (m).</summary>
        public float SolveArm(bool isRight, Vector3 target, Vector3 pole)
        {
            if (!ready) return float.MaxValue;
            var a = isRight ? R : L;
            var rg = isRight ? right : left;
            Vector3 h = rg.hinge.normalized;
            Vector3 s = a.upper.position;
            float la = (a.fore.position - s).magnitude, lb = (a.hand.position - a.fore.position).magnitude;
            float d = Mathf.Clamp((target - s).magnitude, Mathf.Abs(la - lb) + 0.01f, la + lb - 0.002f);
            // 1. elbow bend for that reach: interior angle -> bend from straight
            float cosE = (la * la + lb * lb - d * d) / (2f * la * lb);
            float bend = 180f - Mathf.Acos(Mathf.Clamp(cosE, -1f, 1f)) * Mathf.Rad2Deg;
            Quaternion q; Vector3 sv; float roll;
            Elbow(a, out q, out sv, out roll);
            float sign = Vector3.Dot(sv, h) >= 0f ? 1f : -1f;
            if (Mathf.Abs(Vector3.Dot(sv, h)) < 5f) sign = rg.flex.y >= Mathf.Abs(rg.flex.x) ? 1f : -1f;
            Quaternion handWorld = a.hand.rotation;
            a.fore.rotation = a.upper.rotation * (Quaternion.AngleAxis(sign * bend, h) * Quaternion.AngleAxis(roll, a.foAxis)) * a.elBind;
            // 2. aim the shoulder at the target
            Vector3 toWrist = a.hand.position - s, toTarget = target - s;
            a.upper.rotation = Quaternion.FromToRotation(toWrist, toTarget) * a.upper.rotation;
            // 3. swivel about the shoulder-target line toward the pole
            Vector3 axis = toTarget.normalized;
            Vector3 elbowDir = Vector3.ProjectOnPlane(a.fore.position - s, axis);
            Vector3 poleDir = Vector3.ProjectOnPlane(pole - s, axis);
            if (elbowDir.sqrMagnitude > 1e-6f && poleDir.sqrMagnitude > 1e-6f)
                a.upper.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(elbowDir, poleDir, axis), axis) * a.upper.rotation;
            a.hand.rotation = handWorld;
            return Vector3.Distance(a.hand.position, target);
        }

        public Transform Elbow(bool isRight) { var a = isRight ? R : L; return a != null ? a.fore : null; }
    }
}
