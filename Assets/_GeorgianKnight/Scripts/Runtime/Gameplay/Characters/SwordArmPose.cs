using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Pose fixes for the arms around a one-handed sword. The Mixamo clips come from a two-handed weapon set, which
    /// shows in three places, all fixed after the animation:
    /// <list type="bullet">
    /// <item>Block: the guard was authored for a two-handed haft (blade upright in front of the face, the left hand
    /// open in the air beside it). The sword arm now takes a real high guard: hilt in front of the right shoulder,
    /// blade rising across the head to the left with the edge toward the enemy, and the left fist comes up in front
    /// of the chest (or, optionally, braces the flat of the blade); two-bone IK on both arms. A blocked blow knocks
    /// the guard back and it recovers.</item>
    /// <item>Carry: idle and running held the blade straight up beside the face like a torch. The right wrist now
    /// turns it (limited to a real wrist's range) to a ready angle forward when standing and back and down when
    /// running.</item>
    /// <item>Off hand: with nothing to hold, the left hand kept the half-closed claw of the two-handed grip. At rest it
    /// relaxes to a natural loose hand.</item>
    /// </list>
    /// </summary>
    [DefaultExecutionOrder(164)]   // after PostureCorrector (160); before HandGrip (165), CCHelperBones (170), cloth (190)
    public sealed class SwordArmPose : MonoBehaviour
    {
        [Header("Guard (block)")]
        public bool brace = true;
        [Tooltip("Hilt position in the high guard from the upper chest: right, up, forward (m, character frame).")]
        public Vector3 guardGrip = new Vector3(0.2f, 0.02f, 0.36f);
        [Tooltip("Blade direction in the high guard: right, up, forward (character frame).")]
        public Vector3 guardBlade = new Vector3(-0.3f, 0.93f, 0.18f);
        [Tooltip("Off hand while blocking: lay the palm on the flat of the blade (true), or keep a fist guard in front of the chest (false).")]
        public bool braceBlade = false;
        [Tooltip("Left fist guard from the upper chest: right, up, forward (m, character frame).")]
        public Vector3 leftGuard = new Vector3(-0.05f, -0.07f, 0.27f);
        [Tooltip("Where the palm meets the blade, as a fraction from the cross-guard to the tip.")]
        [Range(0.15f, 0.85f)] public float braceAlong = 0.3f;
        [Tooltip("How far a blocked blow drives the guard back toward the body (m), and how long the recovery takes (s).")]
        public float recoilDistance = 0.09f, recoilTime = 0.32f;
        [Tooltip("Palm centre from the wrist joint along the fingers, and the palm's skin from the bone line (m).")]
        public float palmReach = 0.045f, palmThickness = 0.022f;
        public float braceRate = 9f;

        [Header("Sword carry (free, sword in hand)")]
        public bool carry = true;
        [Tooltip("Largest wrist turn used to bring the blade to the carry angle (deg).")]
        public float maxWrist = 50f;
        [Tooltip("Standing: blade elevation above the horizon (deg) and outward turn (deg).")]
        public float readyElevation = 28f, readyOutward = 18f;
        [Tooltip("Running: blade below the horizon (deg), trailing back.")]
        public float runDepression = 55f;
        public Vector2 runSpeedRange = new Vector2(2.2f, 4.5f);
        public float carryRate = 4f;

        [Header("Off hand at rest")]
        [Range(0f, 1f)] public float relaxWeight = 0.75f;
        [Tooltip("Curl added to the bind pose per joint (proximal, middle, distal), degrees.")]
        public Vector3 relaxCurl = new Vector3(28f, 34f, 20f);

        Combatant self;
        Animator anim;
        AnatomyLimiter anatomy;
        FingerAxes fingerAxes;
        Transform lUpper, lFore, lHand, rUpper, rFore, rHand, chest, sword, bladeBase, bladeTip, gripPoint;
        float recoil, recoilScale = 1f;
        Transform[][] lFingers;
        Quaternion[][] lFingerBind;
        Transform[] lThumb;
        Quaternion[] lThumbBind;
        Vector3 fingerLocal, palmLocal, flatLocal;
        float braceW, relaxW, carryW, speed;
        Vector3 lastPos;
        bool ready;

        /// <summary>Blend weights last frame (QA).</summary>
        public float BraceWeight { get { return braceW; } }
        public float CarryWeight { get { return carryW; } }
        public float LastCarryTurn { get; private set; }
        public float LastBraceMiss { get; private set; }

        void Awake()
        {
            self = GetComponent<Combatant>();
            anim = GetComponent<Animator>();
            anatomy = GetComponent<AnatomyLimiter>();
            fingerAxes = GetComponent<FingerAxes>();
            if (anim == null || !anim.isHuman) return;
            lUpper = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            lFore = anim.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            lHand = anim.GetBoneTransform(HumanBodyBones.LeftHand);
            rUpper = anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
            rFore = anim.GetBoneTransform(HumanBodyBones.RightLowerArm);
            rHand = anim.GetBoneTransform(HumanBodyBones.RightHand);
            chest = anim.GetBoneTransform(HumanBodyBones.UpperChest) ?? anim.GetBoneTransform(HumanBodyBones.Chest);
            sword = rHand != null ? rHand.Find("Sword") : null;
            if (sword != null) { bladeBase = sword.Find("BladeBase"); bladeTip = sword.Find("BladeTip"); gripPoint = sword.Find("Grip"); }
            if (self != null) self.HitReceived += OnHit;
            var ids = new[]
            {
                new[] { HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal },
                new[] { HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal },
                new[] { HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal },
                new[] { HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal },
            };
            lFingers = new Transform[ids.Length][];
            lFingerBind = new Quaternion[ids.Length][];
            for (int f = 0; f < ids.Length; f++)
            {
                lFingers[f] = new Transform[3]; lFingerBind[f] = new Quaternion[3];
                for (int j = 0; j < 3; j++)
                {
                    var t = anim.GetBoneTransform(ids[f][j]);
                    lFingers[f][j] = t;
                    lFingerBind[f][j] = t != null ? t.localRotation : Quaternion.identity;
                }
            }
            lThumb = new[] { anim.GetBoneTransform(HumanBodyBones.LeftThumbProximal), anim.GetBoneTransform(HumanBodyBones.LeftThumbIntermediate), anim.GetBoneTransform(HumanBodyBones.LeftThumbDistal) };
            lThumbBind = new Quaternion[3];
            for (int j = 0; j < 3; j++) lThumbBind[j] = lThumb[j] != null ? lThumb[j].localRotation : Quaternion.identity;
            // left hand frame from the bind pose: fingers toward the middle knuckle, palm normal out of the palm
            Transform mid = lFingers[1][0], idx = lFingers[0][0], lit = lFingers[3][0];
            if (lHand != null && mid != null && idx != null && lit != null)
            {
                Vector3 fw = (mid.position - lHand.position).normalized;
                Vector3 palmW = Vector3.Cross(idx.position - lit.position, fw).normalized;   // left hand
                fingerLocal = lHand.InverseTransformDirection(fw);
                palmLocal = lHand.InverseTransformDirection(palmW);
            }
            flatLocal = FlatNormal();
            lastPos = transform.position;
            ready = lUpper != null && lFore != null && lHand != null && rUpper != null && rFore != null && rHand != null && chest != null && fingerLocal.sqrMagnitude > 0.5f;
        }

        void OnDestroy() { if (self != null) self.HitReceived -= OnHit; }

        void OnHit(HitData hit, HitResult r)
        {
            if (r == HitResult.Blocked || r == HitResult.GuardBroken) { recoil = 1f; recoilScale = r == HitResult.GuardBroken ? 1.6f : 1f; }
            else if (r == HitResult.Parried) { recoil = 1f; recoilScale = 0.6f; }
        }

        /// <summary>The blade's flat normal in the sword's frame: the thinnest extent of the sword's meshes across the blade.</summary>
        Vector3 FlatNormal()
        {
            if (sword == null || bladeBase == null || bladeTip == null) return Vector3.right;
            Vector3 along = sword.InverseTransformDirection(bladeTip.position - bladeBase.position).normalized;
            Vector3 min = Vector3.one * 1e9f, max = -Vector3.one * 1e9f;
            foreach (var mf in sword.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Bounds b = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 p = sword.InverseTransformPoint(mf.transform.TransformPoint(c));
                    min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                }
            }
            Vector3 size = max - min;
            Vector3[] axes = { Vector3.right, Vector3.up, Vector3.forward };
            Vector3 best = Vector3.right; float bestSize = float.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                if (Mathf.Abs(Vector3.Dot(axes[i], along)) > 0.7f) continue;   // the blade's own axis
                float s = Mathf.Abs(Vector3.Dot(size, axes[i]));
                if (s < bestSize) { bestSize = s; best = axes[i]; }
            }
            return Vector3.ProjectOnPlane(best, along).normalized;
        }

        void LateUpdate()
        {
            if (!ready || self == null) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            Vector3 flat = transform.position - lastPos; flat.y = 0f;
            lastPos = transform.position;
            speed = Mathf.Lerp(speed, flat.magnitude / dt, 1f - Mathf.Exp(-8f * dt));

            bool inHand = sword != null && bladeBase != null && sword.IsChildOf(rHand);
            bool busy = self.Holster != null && self.Holster.IsBusy;
            bool free = self.CurrentState == Combatant.State.Free && !busy;
            bool blocking = self.IsBlocking && inHand;
            braceW = Mathf.MoveTowards(braceW, brace && blocking ? 1f : 0f, dt * braceRate);
            carryW = Mathf.MoveTowards(carryW, carry && free && inHand && !blocking ? 1f : 0f, dt * carryRate);
            relaxW = Mathf.MoveTowards(relaxW, free && !blocking ? relaxWeight : 0f, dt * 4f);

            if (carryW > 0.001f) Carry();
            else LastCarryTurn = 0f;
            recoil = Mathf.MoveTowards(recoil, 0f, dt / Mathf.Max(0.05f, recoilTime));
            if (braceW > 0.001f) { Guard(); if (braceBlade) Brace(); else OffGuard(); }
            else LastBraceMiss = 0f;
            float fingerW = Mathf.Max(relaxW, braceW);
            if (fingerW > 0.001f)
            {
                bool fist = braceW > relaxW && !braceBlade;
                RelaxFingers(fingerW, fist ? 2.3f : braceW > relaxW ? 0.45f : 1f, fist);
            }
        }

        void Carry()
        {
            Vector3 blade = (bladeTip.position - bladeBase.position).normalized;
            Vector3 F = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized, R = Vector3.Cross(Vector3.up, F), U = Vector3.up;
            float run = Mathf.InverseLerp(runSpeedRange.x, runSpeedRange.y, speed);
            Vector3 readyDir = Quaternion.AngleAxis(readyOutward, U) * (F * Mathf.Cos(readyElevation * Mathf.Deg2Rad) + U * Mathf.Sin(readyElevation * Mathf.Deg2Rad));
            Vector3 runDir = (-F * Mathf.Cos(runDepression * Mathf.Deg2Rad) - U * Mathf.Sin(runDepression * Mathf.Deg2Rad) + R * 0.35f).normalized;
            Vector3 want = Vector3.Slerp(readyDir, runDir, run).normalized;
            // standing: only a blade raised like a torch is turned; a running carry is always brought down
            float torch = Mathf.InverseLerp(0.5f, 0.85f, Vector3.Dot(blade, U));
            float k = carryW * Mathf.Max(torch, run);
            if (k <= 0.001f) { LastCarryTurn = 0f; return; }
            Quaternion full = Quaternion.FromToRotation(blade, want);
            float ang; Vector3 ax;
            full.ToAngleAxis(out ang, out ax);
            if (ang > 180f) ang -= 360f;
            float a = Mathf.Clamp(ang, -maxWrist, maxWrist) * k;
            rHand.rotation = Quaternion.AngleAxis(a, ax) * rHand.rotation;
            LastCarryTurn = a;
        }

        /// <summary>Right arm to the high guard: the sword is placed (hilt position, blade direction, edge forward) and
        /// the arm reaches it with two-bone IK; the hand turns rigidly with the sword.</summary>
        void Guard()
        {
            if (gripPoint == null) return;
            Vector3 F = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized, R = Vector3.Cross(Vector3.up, F), U = Vector3.up;
            // recoil: out fast (first quarter), back softly
            float t = 1f - recoil;
            float env = recoil <= 0f ? 0f : (t < 0.25f ? t / 0.25f : 1f - Mathf.SmoothStep(0f, 1f, (t - 0.25f) / 0.75f));
            env *= recoilScale;
            Vector3 gripWant = chest.position + R * guardGrip.x + U * guardGrip.y + F * guardGrip.z + (-F * recoilDistance + U * 0.02f) * env;
            Vector3 dirWant = (R * guardBlade.x + U * guardBlade.y + F * guardBlade.z).normalized;
            dirWant = Quaternion.AngleAxis(-10f * env, R) * dirWant;                       // the blow tips the blade back
            Vector3 bladeNow = (bladeTip.position - bladeBase.position).normalized;
            Vector3 flatNow = Vector3.ProjectOnPlane(sword.TransformDirection(flatLocal), bladeNow).normalized;
            // edge toward the enemy: the flat faces across the body; keep the side the hand already has
            Vector3 flatWant = Vector3.Cross(dirWant, F).normalized;
            if (Vector3.Dot(flatWant, flatNow) < 0f) flatWant = -flatWant;
            Quaternion turn = Quaternion.LookRotation(dirWant, flatWant) * Quaternion.Inverse(Quaternion.LookRotation(bladeNow, flatNow));

            Quaternion u0 = rUpper.rotation, f0 = rFore.rotation, h0 = rHand.rotation;
            Quaternion handWant = turn * h0;
            Vector3 wristWant = gripWant - turn * (gripPoint.position - rHand.position);
            Arm(true, wristWant, rFore.position);   // elbow stays in the plane the clip gives it: no swivel flips
            rHand.rotation = handWant;
            if (braceW < 0.999f)
            {
                Quaternion u1 = rUpper.rotation, f1 = rFore.rotation, h1 = rHand.rotation;
                rUpper.rotation = Quaternion.Slerp(u0, u1, braceW);
                rFore.rotation = Quaternion.Slerp(f0, f1, braceW);
                rHand.rotation = Quaternion.Slerp(h0, h1, braceW);
            }
        }

        /// <summary>Left hand to a fist guard in front of the chest (knuckles up and forward, palm toward the midline).</summary>
        void OffGuard()
        {
            Vector3 F = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized, R = Vector3.Cross(Vector3.up, F), U = Vector3.up;
            Vector3 fingersW = (F * 0.55f + U * 0.65f - R * 0.15f).normalized;
            Vector3 palmW = Vector3.ProjectOnPlane(R, fingersW).normalized;
            Quaternion handRot = Quaternion.LookRotation(fingersW, palmW) * Quaternion.Inverse(Quaternion.LookRotation(fingerLocal, palmLocal));
            Vector3 T = chest.position + R * leftGuard.x + U * leftGuard.y + F * leftGuard.z;
            Quaternion u0 = lUpper.rotation, f0 = lFore.rotation, h0 = lHand.rotation;
            Arm(false, T, lFore.position);
            lHand.rotation = handRot;
            LastBraceMiss = Vector3.Distance(lHand.position, T);
            if (braceW < 0.999f)
            {
                Quaternion u1 = lUpper.rotation, f1 = lFore.rotation, h1 = lHand.rotation;
                lUpper.rotation = Quaternion.Slerp(u0, u1, braceW);
                lFore.rotation = Quaternion.Slerp(f0, f1, braceW);
                lHand.rotation = Quaternion.Slerp(h0, h1, braceW);
            }
        }

        void Brace()
        {
            Vector3 a = bladeBase.position, b = bladeTip.position, along = (b - a).normalized;
            Vector3 P = Vector3.Lerp(a, b, braceAlong);
            Vector3 flat = sword.TransformDirection(flatLocal).normalized;
            if (Vector3.Dot(lHand.position - P, flat) < 0f) flat = -flat;       // the face of the blade the left hand is on
            Vector3 palmW = -flat;                                              // palm against the flat
            Vector3 fingersW = Vector3.ProjectOnPlane(along, palmW).normalized; // fingers along the blade, toward the tip
            Quaternion handRot = Quaternion.LookRotation(fingersW, palmW) * Quaternion.Inverse(Quaternion.LookRotation(fingerLocal, palmLocal));
            Vector3 T = P + flat * 0.004f - handRot * (fingerLocal * palmReach + palmLocal * palmThickness);

            Quaternion u0 = lUpper.rotation, f0 = lFore.rotation, h0 = lHand.rotation;
            Arm(false, T, lFore.position);
            lHand.rotation = handRot;
            LastBraceMiss = Vector3.Distance(lHand.position, T);
            if (braceW < 0.999f)
            {
                Quaternion u1 = lUpper.rotation, f1 = lFore.rotation, h1 = lHand.rotation;
                lUpper.rotation = Quaternion.Slerp(u0, u1, braceW);
                lFore.rotation = Quaternion.Slerp(f0, f1, braceW);
                lHand.rotation = Quaternion.Slerp(h0, h1, braceW);
            }
        }

        /// <summary>Two-bone IK of one arm: through the anatomy limiter (the elbow bends only about its measured hinge)
        /// when there is one, else the plain solver.</summary>
        void Arm(bool isRight, Vector3 wrist, Vector3 pole)
        {
            if (anatomy != null && anatomy.right.measured) { anatomy.SolveArm(isRight, wrist, pole); return; }
            if (isRight) SolveTwoBone(rUpper, rFore, rHand, wrist); else SolveTwoBone(lUpper, lFore, lHand, wrist);
        }

        void SolveTwoBone(Transform up, Transform fo, Transform ha, Vector3 target)
        {
            Vector3 s = up.position, e = fo.position, h = ha.position;
            float la = (e - s).magnitude, lb = (h - e).magnitude;
            Vector3 toT = target - s;
            float d = Mathf.Clamp(toT.magnitude, Mathf.Abs(la - lb) + 1e-3f, la + lb - 1e-3f);
            Vector3 dir = toT.normalized;
            Vector3 bend = Vector3.ProjectOnPlane(e - s, dir);                 // keep the animation's elbow direction
            if (bend.sqrMagnitude < 1e-6f) bend = Vector3.ProjectOnPlane(-Vector3.up - transform.right * 0.5f, dir);
            bend.Normalize();
            float cosA = (la * la + d * d - lb * lb) / (2f * la * d);
            float A = Mathf.Acos(Mathf.Clamp(cosA, -1f, 1f));
            Vector3 eWant = s + (dir * Mathf.Cos(A) + bend * Mathf.Sin(A)) * la;
            up.rotation = Quaternion.FromToRotation(e - s, eWant - s) * up.rotation;
            e = fo.position; h = ha.position;
            fo.rotation = Quaternion.FromToRotation(h - e, s + dir * d - e) * fo.rotation;
        }

        /// <summary>Left hand: from whatever the clip did toward a natural hand. Fingers first come together (the
        /// two-handed clips leave them spread), then curl joint by joint toward the palm, the thumb too; with
        /// <paramref name="fist"/> they close into a loose fist.</summary>
        void RelaxFingers(float w, float curlScale, bool fist)
        {
            Vector3 palmW = lHand.TransformDirection(palmLocal);
            float[] perFinger = fist ? new[] { 1f, 1f, 1f, 1.05f } : new[] { 0.75f, 0.9f, 1f, 1.1f };
            // 1. back to the bind hand (fingers straight and together)
            for (int f = 0; f < lFingers.Length; f++)
                for (int j = 0; j < 3; j++)
                    if (lFingers[f][j] != null) lFingers[f][j].localRotation = Quaternion.Slerp(lFingers[f][j].localRotation, lFingerBind[f][j], w);
            // 2. close the spread: each finger's root turns (in the palm plane) toward the middle finger
            Transform midRoot = lFingers[1][0], midNext = lFingers[1][1];
            if (midRoot != null && midNext != null)
            {
                Vector3 midDir = Vector3.ProjectOnPlane(midNext.position - midRoot.position, palmW);
                for (int f = 0; f < lFingers.Length; f++)
                {
                    if (f == 1 || lFingers[f][0] == null || lFingers[f][1] == null) continue;
                    Vector3 d = Vector3.ProjectOnPlane(lFingers[f][1].position - lFingers[f][0].position, palmW);
                    float ang = Vector3.SignedAngle(d, midDir, palmW);
                    lFingers[f][0].rotation = Quaternion.AngleAxis(ang * 0.7f * w, palmW) * lFingers[f][0].rotation;
                }
            }
            // 3. curl toward the palm, each joint about its own anatomical axis when it is known
            bool anatomical = fingerAxes != null && fingerAxes.Ready;
            for (int f = 0; f < lFingers.Length; f++)
            {
                var ch = lFingers[f];
                if (ch[0] == null || ch[1] == null || ch[2] == null) continue;
                Vector3 palmPoint = lHand.position + (ch[0].position - lHand.position) * 0.55f + palmW * 0.03f;
                for (int j = 0; j < 3; j++)
                {
                    float c = (j == 0 ? relaxCurl.x : j == 1 ? relaxCurl.y : relaxCurl.z) * perFinger[f] * curlScale * w;
                    if (anatomical) fingerAxes.Bend(false, f + 1, j, c);
                    else Curl(ch[j], j < 2 ? ch[j + 1].position - ch[j].position : ch[2].position - ch[1].position, palmW, palmPoint, c);
                }
            }
            // 4. the thumb folds in front of the index finger
            if (lThumb[0] != null && lThumb[1] != null && lThumb[2] != null && lFingers[0][1] != null)
            {
                for (int j = 0; j < 3; j++) lThumb[j].localRotation = Quaternion.Slerp(lThumb[j].localRotation, lThumbBind[j], w * 0.8f);
                Vector3 aim = lFingers[0][1].position + palmW * 0.02f;   // over the index finger's middle joint
                float[] tc = fist ? new[] { 22f, 28f, 22f } : new[] { 6f, 10f, 8f };
                for (int j = 0; j < 3; j++)
                {
                    if (anatomical) fingerAxes.Bend(false, 0, j, tc[j] * w);
                    else Curl(lThumb[j], j < 2 ? lThumb[j + 1].position - lThumb[j].position : lThumb[2].position - lThumb[1].position, palmW, aim, tc[j] * w);
                }
            }
        }

        /// <summary>Rotates a joint by <paramref name="degrees"/> about the axis that bends its segment toward the palm,
        /// choosing the direction by test (never backwards).</summary>
        static void Curl(Transform joint, Vector3 seg, Vector3 palmW, Vector3 toward, float degrees)
        {
            Vector3 axis = Vector3.Cross(seg, palmW);
            if (axis.sqrMagnitude < 1e-10f || Mathf.Abs(degrees) < 0.01f) return;
            axis.Normalize();
            Vector3 end = joint.position + seg;
            Vector3 plus = joint.position + Quaternion.AngleAxis(10f, axis) * seg;
            if ((plus - toward).sqrMagnitude > (end - toward).sqrMagnitude) axis = -axis;
            joint.rotation = Quaternion.AngleAxis(degrees, axis) * joint.rotation;
        }
    }
}
