using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Procedural weapon collision avoidance (runs after animation). Mixamo clips were authored for other weapons and
    /// grips, so the blade can cut through the wielder's own body or sink into the ground. Each frame the sword is reset
    /// to its authored grip, then rotated about the grip point by the smallest rotation (a wrist adjustment, clamped)
    /// that keeps the blade outside the body colliders and above the ground. Hit detection reads the corrected blade.
    /// </summary>
    [DefaultExecutionOrder(210)]
    public sealed class WeaponSelfCollision : MonoBehaviour
    {
        public Transform weapon;          // sword root, child of the hand
        public Transform grip;            // rotation pivot (in the fist)
        public Transform bladeBase, bladeTip;
        public Collider[] avoid;          // own body colliders except the sword arm
        [System.NonSerialized] public bool followCurrentPose;   // set by the holster during its hand-over blend
        [Tooltip("The clothed torso's outer surface (coat, belly) the blade is kept out of.")]
        public BodyShell[] shells;
        public float bladeRadius = 0.022f;
        public float clearance = 0.015f;
        public float maxCorrection = 60f;
        [Tooltip("Fastest the correction may change (deg/s): the wrist eases the blade clear instead of snapping it.")]
        public float maxCorrectionSpeed = 240f;
        [Tooltip("When the eased correction would still leave the blade in the body, it may turn this far in one frame (deg). "
                 + "Held to the ease, fast cuts and the sheathe grazed the thigh, arm and spine for 1-2 frames (3-5 cm, Critics A5); "
                 + "inside a swing a quicker wrist is not seen, a blade through the coat is.")]
        public float maxEscapePerFrame = 15f;
        [Tooltip("How far ahead (s) the solver projects the clip's blade: a cut that will drive it into the body is met "
                 + "by an earlier, gentler wrist turn instead of a one-frame escape (the 148 mm tip jerk). 0 = off.")]
        public float anticipate = 0.1f;
        [Tooltip("Fastest the anticipating turn may go (deg/s); what it cannot reach in time is left to the escape.")]
        public float maxAnticipateSpeed = 480f;
        public float releaseRate = 10f;   // how fast it relaxes back to the authored grip (1/s, exponential)
        public bool avoidGround = true;
        [Tooltip("The blade is in the left hand (the second sword).")]
        public bool leftHand;
        public LayerMask groundMask = (1 << 0) | (1 << 10);

        [Header("Carry pose")]
        [Tooltip("Knockdowns, get-ups and jumps were authored without a weapon: there the blade is laid along the "
                 + "forearm (down, on the ground side) or carried low and back, instead of standing up through the face.")]
        public float carryBlendRate = 6f;
        [Tooltip("Carry the blade low and back in jumps (for jump clips authored without a weapon).")]
        public bool carryInJumps = true;
        [Tooltip("Lay the blade along the forearm in knockdowns: it turns the sword in the fist (a dagger grip), so it is "
                 + "off for the clean knight; the ground and body avoidance keep the blade clear instead.")]
        public bool carryInKnockdown = true;
        [Tooltip("Clear the blade by bending the wrist (the hand turns, sword and fingers with it) instead of turning the "
                 + "sword inside the fist, which no grip allows.")]
        public bool correctWithWrist;
        Combatant self;
        Transform lowerArm, handBone;
        float carryW;

        Vector3 restLocalPos;
        Quaternion restLocalRot;
        Quaternion applied = Quaternion.identity, appliedLocal = Quaternion.identity;
        bool init;
        readonly Vector3[] blade = new Vector3[2];

        /// <summary>The grip the clip gives the sword in its parent (before any correction).</summary>
        public Quaternion RestLocalRotation { get { return restLocalRot; } }

        /// <summary>Remaining penetration depth after the last solve (m), 0 when clean.</summary>
        public float LastPenetration { get; private set; }
        /// <summary>What the blade was deepest inside on the last frame (a body part, the shield plate, the ground).</summary>
        public string LastPenetrationInto { get { return LastPenetration > 0f ? deepestName : ""; } }
        // what the blade was deepest in: the collider (named only when read: .name allocates, GK-26) or a fixed label
        Collider deepestCol; string deepestLabel = "";
        string deepestName { get { return deepestCol != null ? deepestCol.name : deepestLabel; } }
        /// <summary>Angle of the correction applied in the last solve (degrees).</summary>
        public float LastCorrection { get; private set; }

        /// <summary>How deep the blade sits in the body right now (m) and in what, without correcting it: works while the
        /// solver is off too (sheathed, drawing), for the film's measurements.</summary>
        public float MeasureNow(out string into) { Vector3 n; return MeasureNow(out into, out n); }

        /// <summary>As <see cref="MeasureNow(out string)"/>, with the direction that would take the blade out (the
        /// chain bridge pushes the hand along it instead of letting this solver turn the wrist).</summary>
        public float MeasureNow(out string into, out Vector3 push)
        {
            push = Vector3.up;
            into = "";
            if (bladeBase == null || bladeTip == null || avoid == null) return 0f;
            Vector3 c, p;
            float d = Mathf.Max(0f, Deepest(bladeBase.position, bladeTip.position, out c, out p));
            into = d > 0f ? deepestName : "";
            push = p;
            return d;
        }

        // each avoided collider's bounding sphere (local centre, radius), worked out once: a cheap test before the real one
        Vector3[] boundC; float[] boundR;

        /// <summary>The blade within reach of anything it must keep out of: any avoided collider's bounding sphere (the
        /// shield plate included), the coat shells, or the ground (the tip within 0.25 m of the feet).</summary>
        bool NearOwnBody(Vector3 a, Vector3 b)
        {
            if (shells != null && shells.Length > 0) return true;
            if (avoidGround && Mathf.Min(a.y, b.y) - transform.position.y < 0.25f) return true;
            if (avoid == null) return false;
            if (boundC == null || boundC.Length != avoid.Length) CacheBounds();
            for (int i = 0; i < avoid.Length; i++)
            {
                var c = avoid[i];
                if (c == null) continue;
                Vector3 w = c.transform.TransformPoint(boundC[i]), ab = b - a;
                float k = Mathf.Clamp01(Vector3.Dot(w - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude));
                float reach = boundR[i] * Mathf.Max(Mathf.Abs(c.transform.lossyScale.x), Mathf.Max(Mathf.Abs(c.transform.lossyScale.y), Mathf.Abs(c.transform.lossyScale.z))) + bladeRadius + clearance + 0.08f;
                if ((a + ab * k - w).sqrMagnitude < reach * reach) return true;
            }
            return false;
        }

        void CacheBounds()
        {
            boundC = new Vector3[avoid.Length]; boundR = new float[avoid.Length];
            for (int i = 0; i < avoid.Length; i++)
            {
                var c = avoid[i];
                if (c is CapsuleCollider) { var k = (CapsuleCollider)c; boundC[i] = k.center; boundR[i] = Mathf.Max(k.radius, k.height * 0.5f); }
                else if (c is SphereCollider) { var s = (SphereCollider)c; boundC[i] = s.center; boundR[i] = s.radius; }
                else if (c is BoxCollider) { var x = (BoxCollider)c; boundC[i] = x.center; boundR[i] = x.size.magnitude * 0.5f; }
                else { boundC[i] = Vector3.zero; boundR[i] = 10f; }   // unknown shape: always near
            }
        }

        public void Init()
        {
            if (weapon == null) { enabled = false; return; }
            self = GetComponent<Combatant>();
            var anim = GetComponent<Animator>();
            if (anim != null && anim.isHuman)
            {
                lowerArm = anim.GetBoneTransform(leftHand ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
                handBone = anim.GetBoneTransform(leftHand ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);

            }
            restLocalPos = weapon.localPosition;
            restLocalRot = weapon.localRotation;
            applied = Quaternion.identity; appliedLocal = Quaternion.identity;
            init = true;
        }

        void Start() { if (!init) Init(); }

        // switched back on (a draw's hand-over): start from the hand as it is, not from the correction remembered from
        // before it was switched off (that stale turn was applied at once: an 18-32 cm jump of the tip, measured)
        void OnEnable() { applied = Quaternion.identity; appliedLocal = Quaternion.identity; HasLastWorld = false; releaseLeft = 0f; haveRaw = false; }

        /// <summary>Where the sword was last drawn on screen (after this solver's correction): the holster starts a
        /// hand-over from here, not from this frame's uncorrected pose.</summary>
        public Vector3 LastWorldPos { get; private set; }
        public Quaternion LastWorldRot { get; private set; }
        public bool HasLastWorld { get; private set; }

        void LateUpdate()
        {
            if (releaseLeft > 0f && !(weapon != null && handBone != null && weapon.IsChildOf(handBone))) { Release(Time.deltaTime); return; }
            releaseLeft = 0f;
            Solve(Time.deltaTime);
        }

        float releaseLeft, releaseTotal;
        Quaternion releaseFrom = Quaternion.identity;

        /// <summary>The sword has left the hand (sheathed): the wrist bend this solver was holding eases back to the clip's
        /// wrist over <paramref name="seconds"/>, then the solver switches itself off. Switching it off at once dropped
        /// the bend in one frame, a 44 deg snap of the hand on the sheathe (Critics A7).</summary>
        public void ReleaseWrist(float seconds)
        {
            if (!enabled) return;
            if (!correctWithWrist || handBone == null || seconds <= 0f || Quaternion.Angle(Quaternion.identity, appliedLocal) < 0.5f) { enabled = false; return; }
            releaseFrom = appliedLocal; releaseTotal = releaseLeft = seconds;
        }

        void Release(float dt)
        {
            releaseLeft = Mathf.Max(0f, releaseLeft - dt);
            float k = releaseLeft / releaseTotal; k = k * k * (3f - 2f * k);
            Quaternion local = Quaternion.Slerp(Quaternion.identity, releaseFrom, k);
            Transform hb = handBone.parent;
            handBone.rotation = (hb != null ? hb.rotation * local * Quaternion.Inverse(hb.rotation) : local) * handBone.rotation;
            applied = Quaternion.identity; appliedLocal = Quaternion.identity;
            if (releaseLeft <= 0f) enabled = false;
        }

        public void Solve(float dt)
        {
            if (!init || weapon == null || grip == null || bladeBase == null || bladeTip == null) return;
            // the holster's hand-over blend owns the pose while it runs: correct on top of it instead of resetting
            if (!followCurrentPose) { weapon.localPosition = restLocalPos; weapon.localRotation = restLocalRot; }
            Carry(dt);
            bool wrist = correctWithWrist && handBone != null && weapon.IsChildOf(handBone);
            Vector3 pivot = wrist ? handBone.position : grip.position;
            Vector3 a0 = bladeBase.position, b0 = bladeTip.position;
            // nothing near the body and no correction to ease out: nothing to solve (GK-26: this solver was three quarters
            // of an enemy's main-thread cost; most frames the blade is nowhere near its own body)
            if (dt > 0f && Quaternion.Angle(Quaternion.identity, appliedLocal) < 0.5f && !NearOwnBody(a0, b0))
            {
                applied = Quaternion.identity; appliedLocal = Quaternion.identity; haveRaw = false;
                LastCorrection = 0f; LastPenetration = 0f;
                LastWorldPos = weapon.position; LastWorldRot = weapon.rotation; HasLastWorld = true;
                return;
            }

            // 1) warm start from last frame's correction (temporal coherence: no flip-flopping between solutions). It is
            // kept in the hand's frame: a fixed world-space correction turned the sword in the fist as the hand turned
            // (the forearm's frame when the wrist does the turning)
            Transform hb = wrist ? handBone.parent : weapon.parent;
            Quaternion prev = hb != null ? hb.rotation * appliedLocal * Quaternion.Inverse(hb.rotation) : applied;
            Quaternion q = prev;
            // an authored chain transition carries the blade itself (AttackData.viaEnd): the correction eases out at the
            // usual pace and nothing new is solved or escaped, so one system owns the arm (Team Lead, f548 d)
            var cur = self != null ? self.CurrentAttack : null;
            bool via = cur != null && cur.viaEnd > 0f && self.ActionProgress < cur.viaEnd;
            if (via) { q = Quaternion.identity; haveRaw = false; }
            else q = Resolve(q, pivot, a0, b0);

            // 2) relax toward the authored grip as far as stays collision-free
            if (!via && dt > 0f && Quaternion.Angle(Quaternion.identity, q) > 0.01f)
            {
                float k = 1f - Mathf.Exp(-releaseRate * dt);
                for (int i = 0; i < 3; i++)
                {
                    Quaternion relaxed = Quaternion.Slerp(q, Quaternion.identity, k);
                    Vector3 c, n;
                    if (Deepest(pivot + relaxed * (a0 - pivot), pivot + relaxed * (b0 - pivot), out c, out n) <= 0f) { q = relaxed; break; }
                    k *= 0.5f;
                }
            }
            else if (!via && dt <= 0f) q = Resolve(Quaternion.identity, pivot, a0, b0);

            // 3) a wrist can only do so much
            float ang = Quaternion.Angle(Quaternion.identity, q);
            if (ang > maxCorrection) q = Quaternion.Slerp(Quaternion.identity, q, maxCorrection / ang);
            // 3b) ...and sees a cut coming: the turn the blade will need within `anticipate` seconds is started now, at the
            // pace that gets there in time, so a fast cut into the body is met by an even wrist turn over several frames
            Quaternion goal = q; float goalRate = maxCorrectionSpeed;
            // the look-ahead (six extra resolves) only where it can be seen: the hero, and enemies within 8 m of the camera
            var cam = Camera.main;
            bool farFoe = self != null && self.team != Team.Player && cam != null && (cam.transform.position - pivot).sqrMagnitude > 64f;
            if (!via && !farFoe) Anticipate(dt, pivot, a0, b0, prev, ref goal, ref goalRate);
            else if (farFoe) haveRaw = false;
            // 4) ...and only so fast: a correction appearing in one frame twitched the sword in the hand
            if (dt > 0f)
            {
                Quaternion eased = Quaternion.RotateTowards(prev, goal, goalRate * dt);
                if (goal != q)
                {
                    // the early turn must still leave the blade clear now; if not, the present need wins
                    Vector3 cg, ng;
                    if (Deepest(pivot + eased * (a0 - pivot), pivot + eased * (b0 - pivot), out cg, out ng) > 0f)
                        eased = Quaternion.RotateTowards(prev, q, maxCorrectionSpeed * dt);
                }
                else eased = Quaternion.RotateTowards(prev, q, maxCorrectionSpeed * dt);
                Vector3 ce, ne;
                if (!via && Quaternion.Angle(eased, q) > 0.01f && Deepest(pivot + eased * (a0 - pivot), pivot + eased * (b0 - pivot), out ce, out ne) > 0f)
                    eased = Quaternion.RotateTowards(prev, q, Mathf.Max(maxCorrectionSpeed * dt, maxEscapePerFrame));
                q = eased;
            }
            applied = q;
            appliedLocal = hb != null ? Quaternion.Inverse(hb.rotation) * q * hb.rotation : q;

            if (wrist) handBone.rotation = applied * handBone.rotation;      // sword and fingers turn with the hand
            else
            {
                weapon.rotation = applied * weapon.rotation;
                weapon.position = pivot + applied * (weapon.position - pivot);
            }
            LastCorrection = Quaternion.Angle(Quaternion.identity, applied);
            LastWorldPos = weapon.position; LastWorldRot = weapon.rotation; HasLastWorld = true;
            Vector3 c2, p2;
            LastPenetration = Mathf.Max(0f, Deepest(bladeBase.position, bladeTip.position, out c2, out p2));

        }

        // the clip's (uncorrected) blade and pivot last frame, in the character's frame, for the anticipation
        Vector3 rawA, rawB, rawP; bool haveRaw;

        /// <summary>Projects the clip's blade ahead along its present motion (in the character's frame, where the body
        /// colliders roughly stay) and, if within <see cref="anticipate"/> seconds it needs more turn than now, sets
        /// <paramref name="goal"/> to that turn and <paramref name="rate"/> to the pace that reaches it in time.</summary>
        void Anticipate(float dt, Vector3 pivot, Vector3 a0, Vector3 b0, Quaternion prev, ref Quaternion goal, ref float rate)
        {
            Transform root = self != null ? self.transform : transform;
            Vector3 la = root.InverseTransformPoint(a0), lb = root.InverseTransformPoint(b0), lp = root.InverseTransformPoint(pivot);
            bool ok = haveRaw && dt > 1e-4f && anticipate > 0f;
            Vector3 va = ok ? (la - rawA) / dt : Vector3.zero, vb = ok ? (lb - rawB) / dt : Vector3.zero, vp = ok ? (lp - rawP) / dt : Vector3.zero;
            rawA = la; rawB = lb; rawP = lp; haveRaw = true;
            if (!ok || (vb - vp).sqrMagnitude < 0.25f) return;      // the tip barely moving against the hand: nothing coming
            // the most urgent future need: the turn that, to be reached in time, asks the fastest pace from here
            Quaternion present = goal;
            float presentNeed = Quaternion.Angle(prev, present), best = 0f;
            for (int i = 1; i <= 6; i++)
            {
                float h = anticipate * i / 6f;
                Vector3 pp = root.TransformPoint(lp + vp * h);
                Quaternion qh = Resolve(present, pp, root.TransformPoint(la + va * h), root.TransformPoint(lb + vb * h));
                float need = Quaternion.Angle(prev, qh);
                if (need <= presentNeed + 0.5f) continue;
                float r = need / h;
                if (r > best) { best = r; goal = qh; }
            }
            if (best <= 0f) return;
            rate = Mathf.Clamp(best, 30f, maxAnticipateSpeed);
            float ang = Quaternion.Angle(Quaternion.identity, goal);
            if (ang > maxCorrection) goal = Quaternion.Slerp(Quaternion.identity, goal, maxCorrection / ang);
        }

        /// <summary>Lays the blade along the forearm while knocked down / getting up, and low and back in a jump.
        /// The carry pose is chosen once, when the state begins, and kept in the hand's own frame: the sword then
        /// moves with the hand like a held object. Re-aiming it at a world direction every frame flipped it by up to
        /// 60 deg a frame whenever the arm turned past that direction.</summary>
        void Carry(float dt)
        {
            float target = 0f;
            Vector3 dir = Vector3.zero;
            if (self != null && lowerArm != null && handBone != null)
            {
                if (carryInKnockdown && self.CurrentState == Combatant.State.Knockdown)
                {
                    target = 1f;
                    dir = (handBone.position - lowerArm.position).normalized + Vector3.up * 0.25f;
                }
                else if (carryInJumps && self.CurrentState == Combatant.State.Jumping)
                {
                    target = 0.85f;
                    Vector3 f = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
                    dir = Vector3.down * 0.8f - f * 0.45f + transform.right * (leftHand ? -0.15f : 0.15f);
                }
            }
            Vector3 cur = bladeTip.position - bladeBase.position;
            if (target > 0f && !carryLocked && dir.sqrMagnitude > 1e-4f && cur.sqrMagnitude > 1e-6f)
            {
                // the carry turn, as seen from the hand: from here on it rides with the hand
                Quaternion full = Quaternion.FromToRotation(cur.normalized, dir.normalized);
                carryTurnLocal = Quaternion.Inverse(handBone.rotation) * full * handBone.rotation;
                carryLocked = true;
            }
            carryW = dt > 0f ? Mathf.MoveTowards(carryW, target, dt * carryBlendRate) : target;
            if (carryW <= 0.001f) { if (target <= 0f) carryLocked = false; return; }
            if (!carryLocked) return;
            Quaternion turn = handBone.rotation * carryTurnLocal * Quaternion.Inverse(handBone.rotation);
            Quaternion part = Quaternion.Slerp(Quaternion.identity, turn, Mathf.SmoothStep(0f, 1f, carryW));
            Vector3 pv = grip.position;
            weapon.rotation = part * weapon.rotation;
            weapon.position = pv + part * (weapon.position - pv);
        }

        bool carryLocked;
        Quaternion carryTurnLocal = Quaternion.identity;

        Quaternion Resolve(Quaternion q, Vector3 pivot, Vector3 a0, Vector3 b0)
        {
            for (int iter = 0; iter < 5; iter++)
            {
                Vector3 a = pivot + q * (a0 - pivot), b = pivot + q * (b0 - pivot);
                Vector3 contact, push;
                float depth = Deepest(a, b, out contact, out push);
                if (depth <= 0f) break;
                Vector3 from = contact - pivot;
                if (from.sqrMagnitude < 1e-6f) break;
                Vector3 to = contact + push * (depth + clearance) - pivot;
                q = Quaternion.FromToRotation(from, to) * q;
            }
            return q;
        }

        /// <summary>Deepest violation of the blade segment a-b: depth (m), contact point on the blade and push direction.</summary>
        float Deepest(Vector3 a, Vector3 b, out Vector3 contact, out Vector3 push)
        {
            float best = 0f; contact = b; push = Vector3.up; deepestCol = null; deepestLabel = "";
            if (avoid != null)
                for (int i = 0; i < avoid.Length; i++)
                {
                    var c = avoid[i];
                    // the ragdoll's colliders are switched off while alive (the hit-only set takes the hits): this solver's
                    // own geometry still reads them, so the blade keeps out of the body
                    if (c == null) continue;
                    Vector3 cp, n; float d;
                    if (!Penetration(c, a, b, out cp, out n, out d)) continue;
                    if (d > best) { best = d; contact = cp; push = n; deepestCol = c; deepestLabel = ""; }
                }
            // the clothed torso (coat and belly), which the ragdoll's boxes do not cover
            if (shells != null)
                for (int k = 0; k < shells.Length; k++)
                {
                    var sh = shells[k];
                    if (sh == null) continue;
                    for (int i = 1; i <= 12; i++)
                    {
                        Vector3 p = Vector3.Lerp(a, b, i / 12f);
                        float d; Vector3 n;
                        if (sh.Depth(p, bladeRadius, out d, out n) && d > best) { best = d; contact = p; push = n; deepestCol = null; deepestLabel = "coat"; }
                    }
                }
            if (avoidGround)
            {
                // check a few points along the outer half of the blade against the ground below them
                for (int i = 0; i <= 3; i++)
                {
                    Vector3 p = Vector3.Lerp(a, b, 0.4f + 0.2f * i);
                    RaycastHit hit;
                    if (Physics.Raycast(p + Vector3.up * 1.5f, Vector3.down, out hit, 3f, groundMask, QueryTriggerInteraction.Ignore))
                    {
                        float d = hit.point.y + bladeRadius - p.y;
                        if (d > best) { best = d; contact = p; push = hit.normal; deepestCol = null; deepestLabel = "ground"; }
                    }
                }
            }
            return best;
        }

        bool Penetration(Collider c, Vector3 a, Vector3 b, out Vector3 bladePoint, out Vector3 normal, out float depth)
        {
            bladePoint = a; normal = Vector3.up; depth = 0f;
            var cap = c as CapsuleCollider;
            if (cap != null)
            {
                Vector3 ca, cb; float r;
                CapsuleWorld(cap, out ca, out cb, out r);
                Vector3 pS, pC;
                ScabbardMotion.ClosestPointsSegments(a, b, ca, cb, out pS, out pC);
                Vector3 sep = pS - pC; float dist = sep.magnitude;
                depth = r + bladeRadius - dist;
                if (depth <= 0f) return false;
                bladePoint = pS;
                normal = dist > 1e-5f ? sep / dist : Vector3.Cross(cb - ca, b - a).normalized;
                return true;
            }
            var sph = c as SphereCollider;
            if (sph != null)
            {
                Vector3 center = sph.transform.TransformPoint(sph.center);
                float r = sph.radius * sph.transform.lossyScale.x;
                Vector3 ab = b - a;
                float t = Mathf.Clamp01(Vector3.Dot(center - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
                Vector3 p = a + ab * t;
                Vector3 sep = p - center; float dist = sep.magnitude;
                depth = r + bladeRadius - dist;
                if (depth <= 0f) return false;
                bladePoint = p;
                normal = dist > 1e-5f ? sep / dist : Vector3.up;
                return true;
            }
            var box = c as BoxCollider;
            if (box != null)
            {
                // sample the blade; depth = distance to the nearest box face for points inside
                var t = box.transform;
                Vector3 half = Vector3.Scale(box.size * 0.5f, t.lossyScale);
                // a plate (the shield): samples would step right over its thickness, so test the segment against its
                // plane. A blade reaching through it is pushed back to the grip's side: the tip, the far end, comes
                // back behind the plate, as a blade held behind a shield lies along its back
                int thin = half.x <= half.y && half.x <= half.z ? 0 : half.y <= half.z ? 1 : 2;
                int j1 = (thin + 1) % 3, j2 = (thin + 2) % 3;
                float other = Mathf.Min(half[j1], half[j2]);
                bool plate = half[thin] < other * 0.15f;
                // a square plate is a round shield: a disc inscribed in the box, its corners empty air
                bool disc = plate && Mathf.Abs(half[j1] - half[j2]) < 0.05f * other;
                if (plate)
                {
                    Vector3 la = Vector3.Scale(t.InverseTransformPoint(a) - box.center, t.lossyScale);
                    Vector3 lb = Vector3.Scale(t.InverseTransformPoint(b) - box.center, t.lossyScale);
                    float sa = la[thin], sb = lb[thin];
                    if (Mathf.Abs(sa) > half[thin] + bladeRadius && Mathf.Sign(sa) != Mathf.Sign(sb))
                    {
                        Vector3 lx = Vector3.Lerp(la, lb, sa / (sa - sb));
                        int i1 = (thin + 1) % 3, i2 = (thin + 2) % 3;
                        bool within = disc ? new Vector2(lx[i1], lx[i2]).magnitude <= other + bladeRadius
                                           : Mathf.Abs(lx[i1]) <= half[i1] + bladeRadius && Mathf.Abs(lx[i2]) <= half[i2] + bladeRadius;
                        if (within)
                        {
                            Vector3 nl = Vector3.zero; nl[thin] = Mathf.Sign(sa);
                            depth = Mathf.Abs(sb) + half[thin] + bladeRadius;
                            bladePoint = b;
                            normal = t.TransformDirection(nl).normalized;
                            return true;
                        }
                    }
                }
                bool any = false;
                for (int i = 0; i <= 10; i++)
                {
                    Vector3 p = Vector3.Lerp(a, b, i / 10f);
                    Vector3 l = Vector3.Scale(t.InverseTransformPoint(p) - box.center, t.lossyScale);
                    Vector3 h = half + Vector3.one * bladeRadius;
                    if (disc)
                    {
                        // inside the disc's slab: the nearer of its face and its rim
                        Vector2 rad = new Vector2(l[j1], l[j2]);
                        float dFace = h[thin] - Mathf.Abs(l[thin]), dRim = other + bladeRadius - rad.magnitude;
                        if (dFace <= 0f || dRim <= 0f) continue;
                        float dd = Mathf.Min(dFace, dRim);
                        if (dd <= depth) continue;
                        Vector3 nl = Vector3.zero;
                        if (dFace <= dRim) nl[thin] = Mathf.Sign(l[thin]);
                        else { Vector2 rn = rad.sqrMagnitude > 1e-8f ? rad.normalized : Vector2.right; nl[j1] = rn.x; nl[j2] = rn.y; }
                        depth = dd; bladePoint = p; normal = t.TransformDirection(nl).normalized; any = true;
                        continue;
                    }
                    float dx = h.x - Mathf.Abs(l.x), dy = h.y - Mathf.Abs(l.y), dz = h.z - Mathf.Abs(l.z);
                    if (dx <= 0f || dy <= 0f || dz <= 0f) continue;
                    float d = Mathf.Min(dx, Mathf.Min(dy, dz));
                    if (d <= depth) continue;
                    Vector3 nLocal = d == dx ? new Vector3(Mathf.Sign(l.x), 0f, 0f) : d == dy ? new Vector3(0f, Mathf.Sign(l.y), 0f) : new Vector3(0f, 0f, Mathf.Sign(l.z));
                    depth = d; bladePoint = p; normal = t.TransformDirection(nLocal).normalized; any = true;
                }
                return any;
            }
            return false;
        }

        static void CapsuleWorld(CapsuleCollider c, out Vector3 a, out Vector3 b, out float r)
        {
            var t = c.transform;
            Vector3 axis = c.direction == 0 ? Vector3.right : c.direction == 2 ? Vector3.forward : Vector3.up;
            float s = t.lossyScale.x;
            r = c.radius * s;
            float half = Mathf.Max(0f, c.height * 0.5f * s - r);
            Vector3 center = t.TransformPoint(c.center);
            Vector3 w = t.TransformDirection(axis);
            a = center - w * half;
            b = center + w * half;
        }
    }
}
