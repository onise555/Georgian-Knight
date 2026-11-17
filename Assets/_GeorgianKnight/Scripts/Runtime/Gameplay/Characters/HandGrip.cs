using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Closes the sword hand around the handle. The Mixamo clips were made for other hands and other weapons: on the
    /// Character Creator hand the fingers stayed half open and the thumb stood up through the cross-guard, so the sword
    /// looked glued to the palm. After the animation, while the sword is in the hand, each finger is wrapped around
    /// the handle (a cylinder along the blade axis through the grip point): its knuckles bend in the finger's own
    /// plane until every joint and the fingertip rest on the handle surface. The thumb then closes over the fingers
    /// and is kept below the cross-guard. Blends in and out when the sword changes hands (draw, sheathe).
    /// </summary>
    [DefaultExecutionOrder(165)]   // after PostureCorrector (160), before cloth, scabbard, holster and the blade solver
    public sealed class HandGrip : MonoBehaviour
    {
        public Transform thumbProximal, thumbIntermediate, thumbDistal;
        public Transform[] index = new Transform[3], middle = new Transform[3], ring = new Transform[3], little = new Transform[3];
        [Tooltip("The sword root (child of the hand while drawn) with its Grip, SheathRef (guard), BladeBase and BladeTip points.")]
        public Transform sword;
        [Tooltip("The left hand (the second sword): the finger axes are read from that side.")]
        public bool leftHand;
        [Tooltip("Handle radius (m) and the finger's own half thickness (m): joints rest at their sum from the handle axis.")]
        public float handleRadius = 0.016f, fingerSkin = 0.009f;
        [Tooltip("How far below the cross-guard the thumb tip stays (m).")]
        public float clearance = 0.012f;
        [Tooltip("Thumb length beyond the distal joint (m).")]
        public float tipLength = 0.022f;
        [Tooltip("Where the thumb tip comes to rest: how far around the handle from the thumb's base (deg), its distance from the handle axis (m), and how far toward the pommel (m).")]
        public float thumbAround = 100f, thumbReach = 0.035f, thumbDrop = 0.012f;
        [Tooltip("Thumb distal joint curl over the fingers, degrees about its anatomical axis.")]
        public float thumbTipCurl = 15f;
        public float maxCurl = 40f;
        public float blendRate = 8f;

        [Header("Grip strength")]
        [Tooltip("Tightness by state (0 loose .. 1 clenched): standing, moving. Attacks, blocks and hits clench (1), falls 0.85, dodges and jumps 0.75.")]
        [Range(0f, 1f)] public float relaxedTightness = 0.35f, movingTightness = 0.55f;
        [Tooltip("How far a fully loose finger rests off the handle (m), scaled per finger (index, middle, ring, little): a relaxed hand holds with the middle fingers, index and little finger ease off.")]
        public float looseGap = 0.008f;
        public Vector4 looseByFinger = new Vector4(1.2f, 0.5f, 0.7f, 1.0f);
        [Tooltip("A loose index finger also opens its knuckle by up to this much (deg).")]
        public float indexEase = 12f;
        [Tooltip("Clenching is fast (an impact, a swing), letting go is slow (1/s).")]
        public float tightenRate = 14f, relaxRate = 3f;
        public float Tightness { get; private set; }

        Transform hand, guard, bladeBase, bladeTip, gripPoint;
        Combatant self;
        FingerAxes axes;   // each joint bends only about its anatomical axis (measured from the avatar)
        float weight;
        // the bind hand (fingers straight and together): the grip starts from it, not from the clip's two-handed claw
        Transform[][] chains;
        Quaternion[][] bind;

        /// <summary>How far the thumb tip was above the guard plane before the fix, and the curl applied (m, deg).</summary>
        public float LastExcess { get; private set; }
        public float LastCurl { get; private set; }
        /// <summary>Mean distance of the fingertips from the handle surface after the wrap (m).</summary>
        public float LastTipGap { get; private set; }

        void Start()
        {
            if (sword != null)
            {
                guard = sword.Find("SheathRef");
                bladeBase = sword.Find("BladeBase");
                bladeTip = sword.Find("BladeTip");
                gripPoint = sword.Find("Grip");
            }
            if (thumbProximal != null) hand = thumbProximal.parent;
            axes = GetComponent<FingerAxes>();
            self = GetComponent<Combatant>();
            Tightness = relaxedTightness;
            chains = new[] { new[] { thumbProximal, thumbIntermediate, thumbDistal }, index, middle, ring, little };
            bind = new Quaternion[chains.Length][];
            for (int f = 0; f < chains.Length; f++)
            {
                bind[f] = new Quaternion[3];
                for (int j = 0; j < 3; j++)
                    if (chains[f] != null && chains[f].Length > j && chains[f][j] != null) bind[f][j] = chains[f][j].localRotation;
            }
        }

        void LateUpdate()
        {
            LastExcess = 0f; LastCurl = 0f;
            if (sword == null || guard == null || bladeBase == null || bladeTip == null || gripPoint == null) return;
            bool inHand = hand != null && sword.IsChildOf(hand);
            weight = Mathf.MoveTowards(weight, inHand ? 1f : 0f, Time.deltaTime * blendRate);
            if (!inHand || weight <= 0.001f) return;

            Vector3 axis = (bladeTip.position - bladeBase.position).normalized;   // toward the tip
            Vector3 origin = gripPoint.position;
            float rest = handleRadius + fingerSkin;
            float target = TargetTightness();
            Tightness = Mathf.MoveTowards(Tightness, target, Time.deltaTime * (target > Tightness ? tightenRate : relaxRate));
            float loose = (1f - Tightness) * looseGap;
            Rebase();
            float gap = 0f; int n = 0;
            gap += Wrap(index, 1, origin, axis, rest + loose * looseByFinger.x, ref n, -36f);
            gap += Wrap(middle, 2, origin, axis, rest + loose * looseByFinger.y, ref n, -36f);
            gap += Wrap(ring, 3, origin, axis, rest + loose * looseByFinger.z, ref n, -36f);
            gap += Wrap(little, 4, origin, axis, rest + loose * looseByFinger.w, ref n, -36f);
            LastTipGap = n > 0 ? gap / n : 0f;
            // a relaxed hand lets the index finger ease off the handle
            if (axes != null && axes.Ready) axes.Bend(!leftHand, 1, 0, -indexEase * (1f - Tightness) * weight);
            Thumb(axis, origin, rest);
        }

        /// <summary>How hard the hand holds the sword now: clenched in a swing, a block or a blow taken, easier on the
        /// move, loosest standing.</summary>
        float TargetTightness()
        {
            if (self == null) return 1f;
            switch (self.CurrentState)
            {
                case Combatant.State.Attacking:
                case Combatant.State.Executing:
                case Combatant.State.Skill:
                case Combatant.State.Reacting:
                case Combatant.State.Staggered: return 1f;
                case Combatant.State.Knockdown: return 0.85f;
                case Combatant.State.Dodging:
                case Combatant.State.Jumping: return 0.75f;
            }
            if (self.IsBlocking) return 1f;
            bool moving = self.Motor != null && GK.Flat(self.Motor.Velocity).magnitude > 0.5f;
            return moving ? movingTightness : relaxedTightness;
        }

        /// <summary>Curls one finger (proximal, intermediate, distal) onto the handle; returns its tip gap. A joint whose
        /// end sinks into the handle opens by at most <paramref name="openLimit"/> degrees (negative).</summary>
        float Wrap(Transform[] f, int finger, Vector3 origin, Vector3 axis, float rest, ref int count, float openLimit)
        {
            if (f == null || f.Length < 3 || f[0] == null || f[1] == null || f[2] == null) return 0f;
            // the finger flexes in one plane: fixed by the proximal segment and the direction to the handle
            Vector3 knuckleEnd = f[1].position;
            Vector3 toAxis0 = ClosestOnAxis(knuckleEnd, origin, axis) - knuckleEnd;
            Vector3 bend = Vector3.Cross(f[1].position - f[0].position, toAxis0);
            if (bend.sqrMagnitude < 1e-12f) return 0f;
            bend.Normalize();
            bool anatomical = axes != null && axes.Ready;
            for (int j = 0; j < 3; j++)
            {
                Transform joint = f[j];
                Vector3 end = j < 2 ? f[j + 1].position : Tip(f[1], f[2]);
                float want = rest;
                if (anatomical)
                {
                    Vector3 ab = axes.CurlAxisWorld(!leftHand, finger, j);
                    if (ab != Vector3.zero) bend = ab;
                }
                float d0 = DistToAxis(end, origin, axis);
                if (Mathf.Abs(d0 - want) < 0.0015f) continue;
                Vector3 seg = end - joint.position;
                // positive angles curl toward the handle: scan for the first angle that puts the end on the surface
                // (curling in when it floats above it, opening when it is inside), else take the closest approach
                float step = d0 > want ? 4f : -4f, limit = d0 > want ? 100f : openLimit;
                float best = 0f, bestErr = Mathf.Abs(d0 - want), prev = 0f, prevD = d0;
                bool found = false;
                for (float a = step; Mathf.Abs(a) <= Mathf.Abs(limit) + 0.01f; a += step)
                {
                    float d = DistToAxis(joint.position + Quaternion.AngleAxis(a, bend) * seg, origin, axis);
                    if ((prevD - want) * (d - want) <= 0f)
                    {
                        float t = Mathf.Abs(prevD - d) > 1e-6f ? (prevD - want) / (prevD - d) : 0f;
                        best = Mathf.Lerp(prev, a, t); found = true; break;
                    }
                    float err = Mathf.Abs(d - want);
                    if (err < bestErr) { bestErr = err; best = a; }
                    prev = a; prevD = d;
                }
                if (!found && bestErr >= Mathf.Abs(d0 - want)) continue;
                float angle = best * weight;
                joint.rotation = Quaternion.AngleAxis(angle, bend) * joint.rotation;
            }
            count++;
            return Mathf.Abs(DistToAxis(Tip(f[1], f[2]), origin, axis) - rest);
        }

        /// <summary>Back to the bind hand (fingers straight and together, thumb at rest) before the wrap. The Mixamo
        /// clips leave the two-handed claw: fingers spread and rolled, the thumb pointing along the handle at the
        /// guard. Wrapped from there, the fingers twisted inside the glove and the thumb stood up beside the handle.</summary>
        void Rebase()
        {
            if (chains == null) return;
            for (int f = 0; f < chains.Length; f++)
                for (int j = 0; j < 3; j++)
                {
                    Transform t = chains[f] != null && chains[f].Length > j ? chains[f][j] : null;
                    if (t != null) t.localRotation = Quaternion.Slerp(t.localRotation, bind[f][j], weight);
                }
        }

        void Thumb(Vector3 axis, Vector3 origin, float rest)
        {
            if (thumbProximal == null || thumbIntermediate == null || thumbDistal == null) return;
            // a hammer grip: the thumb wraps around the handle, across the front of the index and middle fingers.
            // It used to be solved by its distance from the handle axis alone, which a thumb pointing straight up
            // along the handle also satisfies, so it stayed standing beside the wood. Its anatomical flexion is no help
            // either: with the blade leaving the hand on the thumb side, that axis swings the thumb along the blade,
            // through the cross-guard. So the thumb is laid around the handle joint by joint: the base joint (on the CC
            // rig the metacarpal) puts the middle knuckle partway round, the middle joint puts the tip on the far side
            // (aiming the whole thumb from the base overshoots and the tip sticks out of the fist), the tip curls over.
            Vector3 p = thumbProximal.position;
            Vector3 radial = Vector3.ProjectOnPlane(p - origin, axis);
            Vector3 around = Vector3.Cross(axis, radial);
            if (around.sqrMagnitude > 1e-10f)
            {
                radial.Normalize(); around.Normalize();
                Transform knuckle = index != null && index.Length > 1 ? index[1] : null;
                if (knuckle != null && Vector3.Dot(around, knuckle.position - p) < 0f) around = -around;
                Vector3 centre = origin + axis * (Vector3.Dot(p - origin, axis) - thumbDrop);
                System.Func<float, float, Vector3> onHandle = (deg, r) =>
                    centre + (radial * Mathf.Cos(deg * Mathf.Deg2Rad) + around * Mathf.Sin(deg * Mathf.Deg2Rad)) * r;
                float reach = thumbReach + (1f - Tightness) * looseGap * 0.75f;   // a loose thumb lies lighter on the fingers
                Aim(thumbProximal, thumbIntermediate.position, onHandle(thumbAround * 0.45f, reach + 0.01f));
                Aim(thumbIntermediate, ThumbTip(), onHandle(thumbAround, reach));
            }
            if (axes != null && axes.Ready) axes.Bend(!leftHand, 0, 2, thumbTipCurl * weight);
            // ...and keep it below the cross-guard
            Vector3 tip = ThumbTip();
            float excess = Vector3.Dot(tip - guard.position, axis) + clearance;
            LastExcess = excess;
            if (excess <= 0f) return;
            Vector3 lever = tip - thumbProximal.position;
            Vector3 rotAxis = Vector3.Cross(lever, -axis);
            if (rotAxis.sqrMagnitude < 1e-8f) return;
            rotAxis.Normalize();
            float r = Vector3.ProjectOnPlane(lever, rotAxis).magnitude;
            if (r < 1e-4f) return;
            float angle = Mathf.Min(maxCurl, Mathf.Asin(Mathf.Clamp01(excess / r)) * Mathf.Rad2Deg * 1.15f) * weight;
            thumbProximal.rotation = Quaternion.AngleAxis(angle, rotAxis) * thumbProximal.rotation;
            LastCurl = angle;
        }

        /// <summary>Turns <paramref name="joint"/> so that <paramref name="end"/> (a point it carries) points at
        /// <paramref name="target"/>, blended by the grip weight.</summary>
        void Aim(Transform joint, Vector3 end, Vector3 target)
        {
            Vector3 from = end - joint.position, to = target - joint.position;
            if (from.sqrMagnitude < 1e-10f || to.sqrMagnitude < 1e-10f) return;
            joint.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(from, to), weight) * joint.rotation;
        }

        Vector3 ThumbTip()
        {
            Vector3 dir = thumbIntermediate != null ? (thumbDistal.position - thumbIntermediate.position).normalized : (thumbDistal.position - thumbProximal.position).normalized;
            return thumbDistal.position + dir * tipLength;
        }

        static Vector3 Tip(Transform mid, Transform distal)
        {
            Vector3 d = distal.position - mid.position;
            return distal.position + d.normalized * Mathf.Clamp(d.magnitude * 0.85f, 0.012f, 0.025f);
        }

        static Vector3 ClosestOnAxis(Vector3 p, Vector3 origin, Vector3 axis) { return origin + axis * Vector3.Dot(p - origin, axis); }
        static float DistToAxis(Vector3 p, Vector3 origin, Vector3 axis) { return Vector3.ProjectOnPlane(p - origin, axis).magnitude; }
    }
}
