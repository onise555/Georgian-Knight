using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Procedural scabbard: hangs from the sword belt (a fixed throat point on the hips), swings with inertia when the
    /// body turns or runs, and is kept outside the left leg (thigh + shin, using the mesh-fitted hurtbox capsules,
    /// which include the coat skirt) so it never cuts through the clothing. Runs after animation in LateUpdate.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public sealed class ScabbardMotion : MonoBehaviour
    {
        public Transform hips;
        [Tooltip("Pivot at the scabbard throat, +Z toward the chape; the model is its child.")]
        public Transform scabbard;
        [Tooltip("Scabbard length from throat to chape (m).")]
        public float length = 0.95f;
        [Tooltip("Extra clearance kept between the scabbard and the leg surface (m).")]
        public float clearance = 0.035f;
        [Tooltip("Scabbard tube radius (m).")]
        public float radius = 0.03f;
        public float stiffness = 60f;
        public float damping = 9f;
        public float maxSwing = 85f;
        public CapsuleCollider[] legs;     // thigh / shin capsules to stay out of
        [Tooltip("The visible leg's radius per capsule (m at the fit's scale, measured from the skinned mesh); 0 = the capsule's. "
                 + "The ragdoll capsules are fatter than the trouser leg: kept off them, the scabbard leaned 35-45 deg out.")]
        public float[] legRadius;
        [SerializeField, HideInInspector] float fitScale = 1f;

        [Header("Thigh drive (a real scabbard is pushed along by the leg it hangs beside)")]
        [Tooltip("Upper leg on the scabbard side. The rest hang follows it partially, so walking, lunging and kicking carry the scabbard with the leg instead of colliding with it.")]
        public Transform thigh;
        [Range(0f, 1f)] public float thighFollow = 0.55f;
        [Tooltip("Largest angle the thigh may carry the rest hang away from the hip rest (deg).")]
        public float maxThighFollow = 38f;
        [Tooltip("Rest hang direction in the thigh's space, baked at the bind pose by the editor builder (zero = capture at Init).")]
        public Vector3 restDirInThigh;
        public LayerMask groundMask = (1 << 0) | (1 << 10);

        [Header("Coat skirt profile (built by the editor from the mesh)")]
        [Tooltip("Outer radius of the clothing around the hips' vertical axis, [height bin * sectors + sector] (m).")]
        public float[] skirtRadius;
        public int skirtSectors = 12;
        public float skirtTop;             // height of bin 0 relative to the hips (m, along the body up axis)
        public float skirtStep = 0.08f;    // bin height (m)
        public Vector3 bodyUpLocal = Vector3.up;      // body up / forward in hips space (bind pose)
        public Vector3 bodyFwdLocal = Vector3.forward;

        [Header("Drawn hang")]
        [Tooltip("How far the throat rides back along the body (m) whenever the sword is not being drawn or sheathed. The hanger carries the scabbard at the side of the hip; the left hand only brings it forward (to where the draw and sheathe clips grip the hilt) for the draw and the sheathe. 0 = fixed throat.")]
        public float drawnBack = 0f;
        [Tooltip("How far the throat rides out to the side at rest (m), with drawnBack: the belt carries it outside the "
                 + "breeches, so it hangs nearly straight down past the thigh.")]
        public float restOut = 0.04f;
        [Tooltip("Seconds the throat takes to come forward to the drawing / sheathing hand (at drawnGlide the draw pulled the "
                 + "sword from a scabbard still on its way: 8 cm through the thigh).")]
        public float reachGlide = 0.12f;
        [Tooltip("Seconds the throat takes to glide between the draw position and the hip.")]
        public float drawnGlide = 0.35f;
        [Tooltip("How much the empty scabbard follows the thigh while the sword is out. In the guard the left leg steps "
                 + "forward: following it at the sheathed 0.55 swung the scabbard out in front, across the shield and past "
                 + "the knee; a belt-hung scabbard stays at the hip while the leg moves past it.")]
        public float drawnThighFollow = 0.15f;
        [Tooltip("How much of the coat skirt's push the scabbard takes (1 = rides outside the flare, 0 = passes through the "
                 + "coat). Taken fully, a long coat held it 30-45 deg out from the leg like a stick.")]
        [Range(0f, 1f)] public float skirtYield = 0.3f;
        [Tooltip("Most the scabbard may hang out from the body's vertical, sideways (deg); 0 = no limit. Clothing may overlap "
                 + "it, the legs may not.")]
        public float maxOutward = 0f;   // off: held to 12 it fought the leg avoidance (45 back / 50 out, 5 cm in the thigh); to be solved together

        WeaponHolster holster;
        float drawnW;                      // 0 = sheathed or moving, 1 = the sword out (the thigh followed less)
        float backW;                       // 0 = throat where the hand meets the hilt (draw / sheathe), 1 = at the hip's side
        bool snapDrawn = true;

        Vector3 restLocalPos;              // throat in hips space
        Quaternion restLocalRot;           // scabbard rotation in hips space
        Vector3 restLocalDir;              // throat → chape in scabbard space
        Vector3 tipPos, tipVel, lastThroat;
        bool init;

        void Start() { Init(); }

        /// <summary>Captures the rest hang from the current (bind) pose. Called on Start; tools call it directly.</summary>
        public void Init()
        {
            if (hips == null || scabbard == null) { enabled = false; return; }
            restLocalPos = hips.InverseTransformPoint(scabbard.position);
            restLocalRot = Quaternion.Inverse(hips.rotation) * scabbard.rotation;
            // the pivot sits at the throat with +Z pointing to the chape (built by GKWeapons.Attach)
            restLocalDir = Vector3.forward;
            holster = GetComponent<WeaponHolster>();
            snapDrawn = true;
            if (thigh != null && restDirInThigh.sqrMagnitude < 1e-6f)
                restDirInThigh = Quaternion.Inverse(thigh.rotation) * (scabbard.rotation * restLocalDir);   // only correct at the bind pose
            init = false;
        }

        /// <summary>Editor bake at the bind pose: rest hang direction in the thigh's space.</summary>
        public void BakeThighRest()
        {
            if (thigh == null || scabbard == null) return;
            restDirInThigh = Quaternion.Inverse(thigh.rotation) * (scabbard.rotation * Vector3.forward);
        }

        void LateUpdate() { Solve(Mathf.Min(Time.deltaTime, 1f / 30f)); }

        /// <summary>Restarts the swing simulation from the rest hang (keeps the captured rest pose).</summary>
        public void ResetDynamics() { init = false; snapDrawn = true; filtDirLocal = Vector3.zero; }

        /// <summary>Smallest distance between the scabbard surface and a leg capsule surface after the last Solve (m).</summary>
        public float LastClearance { get; private set; }
        /// <summary>Angle between the solved hang and the (thigh-driven) rest hang after the last Solve (degrees):
        /// the spring lag plus collision correction. Large values read as the scabbard being knocked about.</summary>
        public float LastSwing { get; private set; }
        /// <summary>How far the thigh carried the rest hang this frame (degrees).</summary>
        public float LastFollow { get; private set; }
        /// <summary>Deepest violation left after the constraint loop (m, 0 = fully resolved).</summary>
        public float LastResidual { get; private set; }

        public void Solve(float dt)
        {
            if (hips == null || scabbard == null) return;
            Vector3 throat = hips.TransformPoint(restLocalPos);
            {
                // the sword in the hand (or no holster at all: tools posing the bind prefab): the empty scabbard settles
                float target = holster == null || holster.State == WeaponHolster.HolsterState.Drawn ? 1f : 0f;
                // at the side of the hip unless the left hand is bringing it forward for a draw or a sheathe
                float back = holster == null || (holster.State != WeaponHolster.HolsterState.Drawing && holster.State != WeaponHolster.HolsterState.Sheathing) ? 1f : 0f;
                // forward fast (the hand reaches for the hilt early in the draw), back to the hip at the gentler glide
                backW = snapDrawn || dt <= 0f ? back : Mathf.MoveTowards(backW, back, dt / Mathf.Max(0.01f, back < backW ? reachGlide : drawnGlide));
                drawnW = snapDrawn || dt <= 0f ? target : Mathf.MoveTowards(drawnW, target, dt / Mathf.Max(0.01f, drawnGlide));
                snapDrawn = false;
            }
            if (drawnBack > 0f)
            {
                Vector3 upW = hips.TransformDirection(bodyUpLocal);
                Vector3 fwdW = Vector3.ProjectOnPlane(hips.TransformDirection(bodyFwdLocal), upW).normalized;
                throat -= fwdW * (drawnBack * Mathf.SmoothStep(0f, 1f, backW));
                // ...and a little out to the side, so it hangs straight down past the breeches instead of leaning out
                Vector3 outW = Vector3.Cross(fwdW, upW).normalized;
                throat += outW * (restOut * Mathf.SmoothStep(0f, 1f, backW));
            }
            Quaternion restRot = hips.rotation * restLocalRot;
            Vector3 restDir = restRot * restLocalDir;
            // the leg carries the hang part of the way (bounded), like the thigh pushing a belt-hung scabbard
            LastFollow = 0f;
            if (thigh != null && thighFollow > 0f && restDirInThigh.sqrMagnitude > 1e-6f)
            {
                Vector3 legDir = thigh.rotation * restDirInThigh;
                float follow = Mathf.Lerp(thighFollow, Mathf.Min(thighFollow, drawnThighFollow), Mathf.SmoothStep(0f, 1f, drawnW));
                Vector3 followed = Vector3.Slerp(restDir, legDir, follow);
                float fa = Vector3.Angle(restDir, followed);
                if (fa > maxThighFollow) followed = Vector3.Slerp(restDir, followed, maxThighFollow / fa);
                followed.Normalize();
                restRot = Quaternion.FromToRotation(restDir, followed) * restRot;
                LastFollow = Vector3.Angle(restDir, followed);
                restDir = followed;
            }
            Vector3 restTip = throat + restDir * length;

            bool simulate = init && dt > 0f;
            Vector3 prevTip = tipPos;
            Vector3 throatVel = simulate ? (throat - lastThroat) / dt : Vector3.zero;
            if (!simulate) { tipPos = restTip; tipVel = Vector3.zero; init = true; }
            else
            {
                // spring-damper toward the rest hang (sway and lag when the body turns / stops). Damping acts on the
                // velocity relative to the belt, so running does not make the scabbard trail behind.
                Vector3 acc = (restTip - tipPos) * stiffness - (tipVel - throatVel) * damping + Physics.gravity * 0.15f;
                tipVel += acc * dt;
                tipPos += tipVel * dt;
            }
            lastThroat = throat;

            // keep the length and limit the swing
            Vector3 dir = tipPos - throat;
            if (dir.sqrMagnitude < 1e-6f) dir = restDir;
            dir.Normalize();
            float ang = Vector3.Angle(restDir, dir);
            if (ang > maxSwing) dir = Vector3.Slerp(restDir, dir, maxSwing / ang).normalized;

            // stay outside the legs (thigh / shin capsules) and the coat skirt: resolve the deepest violation first,
            // rotating the scabbard about the throat, until nothing is violated (or the budget runs out)
            float residual = 0f;
            for (int iter = 0; iter < 16; iter++)
            {
                float worst = 0f; Vector3 worstPoint = Vector3.zero, worstPush = Vector3.zero;
                if (legs != null)
                    for (int i = 0; i < legs.Length; i++)
                    {
                        var cap = legs[i];
                        if (cap == null) continue;
                        Vector3 a, b; float r;
                        CapsuleWorld(cap, out a, out b, out r); r = LegR(i, r);
                        Vector3 pS, pC;
                        ClosestPointsSegments(throat + dir * (length * 0.3f), throat + dir * length, a, b, out pS, out pC);
                        Vector3 sep = pS - pC;
                        float d = sep.magnitude;
                        float need = r + radius + clearance - d;
                        if (need <= worst) continue;
                        worst = need; worstPoint = pS;
                        worstPush = d > 1e-5f ? sep / d : Vector3.Cross(b - a, hips.forward).normalized;
                    }
                if (skirtRadius != null && skirtRadius.Length > 0)
                    for (int k = 2; k <= 8; k++)
                    {
                        Vector3 p = throat + dir * (length * k / 8f);
                        Vector3 push;
                        // the coat may give way under it (it lies against the cloth, not out on its flare): only part
                        // of the coat's push is taken; the legs keep their full clearance
                        float need = SkirtViolation(p, out push) * skirtYield;
                        if (need > worst) { worst = need; worstPoint = p; worstPush = push; }
                    }
                // the chape never digs into the ground (a long scabbard nearly reaches it when the hips tilt)
                {
                    Vector3 tip = throat + dir * length;
                    RaycastHit hit;
                    if (Physics.Raycast(tip + Vector3.up * 0.6f, Vector3.down, out hit, 1.2f, groundMask, QueryTriggerInteraction.Ignore))
                    {
                        float need = hit.point.y + radius + 0.02f - tip.y;
                        if (need > worst) { worst = need; worstPoint = tip; worstPush = Vector3.up; }
                    }
                }
                residual = worst;
                if (worst <= 0f) break;
                Vector3 target = worstPoint + worstPush * (worst + 0.004f);
                dir = Quaternion.FromToRotation(worstPoint - throat, target - throat) * dir;
            }
            LastResidual = Mathf.Max(0f, residual);
            // the swing limit is for the scabbard's own sway (applied above, before the legs): a leg pushing it out may
            // take it further. Clamping again here put it back INTO the thigh (-7 cm, measured) until the two fought
            // and it jumped 30 cm in a frame. Only a wild solve beyond the hard limit is held back
            float swing = Vector3.Angle(restDir, dir);
            if (swing > hardSwing) dir = Vector3.Slerp(restDir, dir, hardSwing / swing).normalized;

            // a belt-hung scabbard hangs by its weight: the coat's flare pushed it 25-30 deg out from the leg like a
            // stick. Held to maxOutward from the body's vertical; the coat may overlap it, a leg may not
            // held in only while the sword is sheathed (the legs together): in the wide guard the cap and the legs took
            // turns frame by frame and the chape buzzed (256 mm zig-zag)
            if (maxOutward > 0f && drawnW < 0.5f)
            {
                Vector3 upB = hips.TransformDirection(bodyUpLocal).normalized;
                Vector3 fwdB = Vector3.ProjectOnPlane(hips.TransformDirection(bodyFwdLocal), upB).normalized;
                Vector3 outB = Vector3.Cross(fwdB, upB).normalized;      // the body's left: the scabbard's side
                float down = -Vector3.Dot(dir, upB), side = Vector3.Dot(dir, outB), along = Vector3.Dot(dir, fwdB);
                float maxSide = Mathf.Tan(maxOutward * Mathf.Deg2Rad) * Mathf.Max(0.05f, down);
                if (side > maxSide)
                {
                    Vector3 capped = (-upB * down + outB * maxSide + fwdB * along).normalized;
                    if (LegGap(throat, capped) < -0.004f)
                    {
                        float lo = 0f, hi = 1f;      // only as far out as the leg needs
                        for (int it = 0; it < 6; it++)
                        {
                            float mid = (lo + hi) * 0.5f;
                            if (LegGap(throat, Vector3.Slerp(capped, dir, mid).normalized) < -0.004f) lo = mid; else hi = mid;
                        }
                        capped = Vector3.Slerp(capped, dir, hi).normalized;
                    }
                    dir = capped;
                }
            }

            // the solved hang, filtered in the hips' frame: when two constraints take turns (the thigh, then the skirt,
            // then the thigh again) the raw answer flips from frame to frame and the scabbard buzzed (35 cm zigzags at
            // the chape, measured). A short critically damped follow (~50 ms) removes the buzz but keeps every real
            // swing; a correction that would leave it in a leg is taken at once
            if (simulate)
            {
                Vector3 dirLocal = Quaternion.Inverse(hips.rotation) * dir;
                if (filtDirLocal == Vector3.zero) filtDirLocal = dirLocal;
                float k = 1f - Mathf.Exp(-filterRate * dt);
                Vector3 f = Vector3.Slerp(filtDirLocal, dirLocal, k).normalized;
                // a smoothed hang that would sit in a leg moves toward the solved one only as far as it has to (a
                // snap straight to the solved hang was itself a 34 cm jump at the chape)
                if (LegGap(throat, hips.rotation * f) < -0.004f)
                {
                    float lo = 0f, hi = 1f;
                    for (int it = 0; it < 6; it++)
                    {
                        float mid = (lo + hi) * 0.5f;
                        Vector3 m = Vector3.Slerp(f, dirLocal, mid).normalized;
                        if (LegGap(throat, hips.rotation * m) < -0.004f) lo = mid; else hi = mid;
                    }
                    f = Vector3.Slerp(f, dirLocal, hi).normalized;
                }
                filtDirLocal = f;
                dir = hips.rotation * filtDirLocal;
            }
            else filtDirLocal = Quaternion.Inverse(hips.rotation) * dir;

            tipPos = throat + dir * length;
            // position-based velocity: the constraint push becomes real motion, so the next frame does not spring back
            // into the leg (no chatter, no pops)
            if (simulate) tipVel = (tipPos - prevTip) / dt;
            LastSwing = Vector3.Angle(restDir, dir);
            LastClearance = float.MaxValue;
            if (legs != null)
                for (int i = 0; i < legs.Length; i++)
                {
                    if (legs[i] == null) continue;
                    Vector3 a, b, pS, pC; float r;
                    CapsuleWorld(legs[i], out a, out b, out r); r = LegR(i, r);
                    ClosestPointsSegments(throat + dir * (length * 0.3f), tipPos, a, b, out pS, out pC);
                    LastClearance = Mathf.Min(LastClearance, (pS - pC).magnitude - r - radius);
                }
            if (skirtRadius != null && skirtRadius.Length > 0)
                for (int k = 3; k <= 8; k++)
                {
                    Vector3 push;
                    float v = SkirtViolation(throat + dir * (length * k / 8f), out push);
                    if (v > 0f) LastClearance = Mathf.Min(LastClearance, -(v - clearance));
                }
            scabbard.position = throat;
            scabbard.rotation = Quaternion.FromToRotation(restDir, dir) * restRot;
        }

        [Tooltip("Largest angle a leg may push the scabbard from its hang (deg); its own sway stays within maxSwing.")]
        public float hardSwing = 70f;
        [Tooltip("How fast the drawn hang follows the solved one (1/s): high enough for every real swing, low enough to drop a frame-to-frame flip.")]
        public float filterRate = 22f;
        Vector3 filtDirLocal;

        // the smallest gap between the scabbard (outer 70 %) and the leg capsules, minus the clearance (m)
        float LegGap(Vector3 throat, Vector3 dir)
        {
            float g = float.MaxValue;
            if (legs == null) return g;
            for (int i = 0; i < legs.Length; i++)
            {
                if (legs[i] == null) continue;
                Vector3 a, b, pS, pC; float r;
                CapsuleWorld(legs[i], out a, out b, out r); r = LegR(i, r);
                ClosestPointsSegments(throat + dir * (length * 0.3f), throat + dir * length, a, b, out pS, out pC);
                g = Mathf.Min(g, (pS - pC).magnitude - r - radius);
            }
            return g;
        }

        /// <summary>How far point p (world) is inside the skirt profile (+ clearance), and the outward direction.</summary>
        float SkirtViolation(Vector3 p, out Vector3 push)
        {
            push = Vector3.zero;
            Vector3 up = hips.TransformDirection(bodyUpLocal).normalized;
            Vector3 fwd = Vector3.ProjectOnPlane(hips.TransformDirection(bodyFwdLocal), up).normalized;
            Vector3 right = Vector3.Cross(up, fwd);
            Vector3 d = p - hips.position;
            float h = Vector3.Dot(d, up);
            int bin = Mathf.FloorToInt((skirtTop - h) / skirtStep);
            int bins = skirtRadius.Length / Mathf.Max(1, skirtSectors);
            if (bin < 0 || bin >= bins) return 0f;
            Vector3 radial = d - up * h;
            float r = radial.magnitude;
            if (r < 1e-4f) return 0f;
            float ang = Mathf.Atan2(Vector3.Dot(radial, right), Vector3.Dot(radial, fwd)) * Mathf.Rad2Deg;
            if (ang < 0f) ang += 360f;
            int sector = Mathf.Clamp(Mathf.FloorToInt(ang / (360f / skirtSectors)), 0, skirtSectors - 1);
            float need = skirtRadius[bin * skirtSectors + sector] + radius + clearance - r;
            if (need <= 0f) return 0f;
            push = radial / r;
            return need;
        }

        float LegR(int i, float capsuleR)
        {
            if (legRadius == null || i >= legRadius.Length || legRadius[i] <= 0f || legs[i] == null) return capsuleR;
            return legRadius[i] * legs[i].transform.lossyScale.x / Mathf.Max(1e-5f, fitScale);
        }

        /// <summary>Stores the visible leg radii (world metres at the fit's scale).</summary>
        public void SetLegRadii(float[] r) { legRadius = r; fitScale = legs != null && legs.Length > 0 && legs[0] != null ? legs[0].transform.lossyScale.x : 1f; }

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

        /// <summary>Closest points between segments p0-p1 and q0-q1.</summary>
        public static void ClosestPointsSegments(Vector3 p0, Vector3 p1, Vector3 q0, Vector3 q1, out Vector3 cp, out Vector3 cq)
        {
            Vector3 d1 = p1 - p0, d2 = q1 - q0, r = p0 - q0;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            float s, t;
            if (a <= 1e-8f && e <= 1e-8f) { cp = p0; cq = q0; return; }
            if (a <= 1e-8f) { s = 0f; t = Mathf.Clamp01(f / e); }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= 1e-8f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float denom = a * e - b * b;
                    s = denom != 0f ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
                }
            }
            cp = p0 + d1 * s;
            cq = q0 + d2 * t;
        }
    }
}
