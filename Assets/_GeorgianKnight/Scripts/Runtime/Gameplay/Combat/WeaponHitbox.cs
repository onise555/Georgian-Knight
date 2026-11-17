using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Swept blade collision: samples points from guard to tip and sphere-casts each point from its
    /// previous position, sub-stepping fast swings so nothing tunnels through at low frame rates.
    /// Kicks swap the blade for the legs (<see cref="UseLimbs"/>): knee to toes of both legs.
    /// </summary>
    [DefaultExecutionOrder(220)]   // after WeaponSelfCollision so swings test the corrected blade
    public class WeaponHitbox : MonoBehaviour
    {
        public Transform bladeBase;
        public Transform bladeTip;
        [Range(2, 12)] public int samples = 6;
        public float radius = 0.06f;
        [Tooltip("Sphere radius while the legs strike (a boot is thicker than a blade).")]
        public float limbRadius = 0.08f;
        public float maxStep = 0.12f;
        [Tooltip("The swing's intended target (set by the attacker when the window opens) also counts as hit when the edge passes this much farther from its body (m): the blade is exact, the body capsules are not.")]
        public float assistRadius = 0.1f;
        [System.NonSerialized] public Combatant assistTarget;
        [Tooltip("A detected contact is delivered (hit, block, parry, hit-stop, effects) on the sub-step the blade's edge comes "
                 + "this close to the body (m); a swing whose closest pass stays farther is a reach miss, not a hit (§3c: a hit looks "
                 + "like contact at the close camera).")]
        public float deliverGap = 0.03f;
        [Tooltip("The longest a detected contact may wait for the blade (s).")]
        public float maxDeliverWait = 0.3f;

        /// <summary>A detected contact whose blade never came within deliverGap: the weapon, the target and its closest pass (m).</summary>
        public static System.Action<WeaponHitbox, Combatant, float> ReachMiss;

        /// <summary>§3c delivery at contact. Off (the old delivery, at detection) until enemycal and the hero's reach are
        /// recalibrated for it (Team Lead: it comes back with the recalibration, in one change).</summary>
        public static bool DeliverAtContact = false;

        /// <summary>A delivery gap for every weapon at once (m; below 0: each weapon's own deliverGap). QA sweeps it at runtime.</summary>
        public static float GapOverride = -1f;
        float DeliverGap { get { return GapOverride >= 0f ? GapOverride : deliverGap; } }

        class Pending { public Hurtbox hb; public Collider[] body; public Vector3 dir, point; public float best = float.MaxValue, since; }
        readonly Dictionary<Combatant, Pending> pending = new Dictionary<Combatant, Pending>();
        // reused, so a detection allocates nothing (GK-26): the waits, and each foe's body colliders (found once per foe)
        readonly Stack<Pending> pendingPool = new Stack<Pending>();
        readonly Dictionary<Combatant, Collider[]> bodyOf = new Dictionary<Combatant, Collider[]>();
        static readonly List<Hurtbox> hurtScratch = new List<Hurtbox>();
        static readonly List<Collider> colScratch = new List<Collider>();
        readonly List<Combatant> pendingKeys = new List<Combatant>();
        Vector3[] segPrev = new Vector3[0];

        public Combatant Owner { get; set; }
        public bool Active { get; private set; }

        Vector3[] prev;
        Transform[] limbs;   // pairs (from, to) while a kick is the strike; null = the blade
        readonly HashSet<Combatant> hitThisSwing = new HashSet<Combatant>(8);   // sized now: the first hit grows nothing
        static readonly RaycastHit[] hits = new RaycastHit[24];
        static readonly Collider[] overlaps = new Collider[24];

        public System.Action<Combatant, Hurtbox, Vector3, Vector3> OnContact; // target, hurtbox, point, direction

        void Awake() { prev = new Vector3[Mathf.Max(2, samples) * 2]; }

        /// <summary>Strike with these segments (pairs: from, to) instead of the blade; null goes back to the blade.</summary>
        public void UseLimbs(Transform[] pairs)
        {
            if (Active) Deactivate();
            limbs = pairs != null && pairs.Length >= 2 ? pairs : null;
        }

        public bool UsingLimbs { get { return limbs != null; } }
        public int SegmentCount { get { return limbs != null ? limbs.Length / 2 : 1; } }
        float R { get { return limbs != null ? limbRadius : radius; } }

        /// <summary>The striking segments right now (blade, or the kicking legs).</summary>
        public bool GetSegment(int s, out Vector3 a, out Vector3 b)
        {
            a = b = Vector3.zero;
            Transform ta = limbs != null ? limbs[s * 2] : bladeBase, tb = limbs != null ? limbs[s * 2 + 1] : bladeTip;
            if (ta == null || tb == null) return false;
            a = ta.position; b = tb.position;
            return true;
        }

        int Count { get { return samples * SegmentCount; } }

        public void Activate()
        {
            if (bladeBase == null || bladeTip == null) return;
            if (prev == null || prev.Length < Count) prev = new Vector3[Count];
            hitThisSwing.Clear();
            foreach (var p in pending.Values) pendingPool.Push(p);
            pending.Clear();
            for (int i = 0; i < Count; i++) prev[i] = Sample(i);
            StoreSegments();
            Active = true;
            // first frame: overlap test so point-blank hits register immediately
            for (int i = 0; i < Count; i++)
            {
                Vector3 axis = SegmentAxis(i);
                int n = Physics.OverlapSphereNonAlloc(prev[i], R, overlaps, GK.HurtboxMask, QueryTriggerInteraction.Collide);
                for (int k = 0; k < n; k++) Report(overlaps[k], prev[i], axis);
                if (assistTarget != null && assistRadius > 0f)
                {
                    n = Physics.OverlapSphereNonAlloc(prev[i], R + assistRadius, overlaps, GK.HurtboxMask, QueryTriggerInteraction.Collide);
                    for (int k = 0; k < n; k++) ReportAssist(overlaps[k], prev[i], axis);
                }
            }
        }

        public void Deactivate()
        {
            // the window closes: what still waits is delivered at its closest pass if that was contact, else a reach miss
            if (Active) Settle(true);
            Active = false;
        }

        public void ClearSwingHits() { hitThisSwing.Clear(); }

        Vector3 Sample(int i)
        {
            int s = i / samples, j = i % samples;
            Vector3 a, b;
            if (!GetSegment(s, out a, out b)) return transform.position;
            float t = samples <= 1 ? 1f : (float)j / (samples - 1);
            return Vector3.Lerp(a, b, t);
        }

        Vector3 SegmentAxis(int i)
        {
            Vector3 a, b;
            return GetSegment(i / samples, out a, out b) ? b - a : Vector3.forward;
        }

        void LateUpdate()
        {
            if (!Active) return;
            float r = R;
            for (int i = 0; i < Count; i++)
            {
                Vector3 cur = Sample(i);
                Vector3 from = prev[i];
                Vector3 delta = cur - from;
                float dist = delta.magnitude;
                if (dist > 1e-4f)
                {
                    int steps = Mathf.Clamp(Mathf.CeilToInt(dist / maxStep), 1, 8);
                    Vector3 dir = delta / dist;
                    float stepLen = dist / steps;
                    for (int s = 0; s < steps; s++)
                    {
                        Vector3 origin = from + dir * (stepLen * s);
                        int n = Physics.SphereCastNonAlloc(origin, r, dir, hits, stepLen, GK.HurtboxMask, QueryTriggerInteraction.Collide);
                        for (int k = 0; k < n; k++)
                        {
                            Vector3 point = hits[k].point;
                            if (hits[k].distance <= 0f && point == Vector3.zero) point = origin;
                            Report(hits[k].collider, point, dir);
                        }
                        if (assistTarget != null && assistRadius > 0f && !hitThisSwing.Contains(assistTarget))
                        {
                            n = Physics.SphereCastNonAlloc(origin, r + assistRadius, dir, hits, stepLen, GK.HurtboxMask, QueryTriggerInteraction.Collide);
                            for (int k = 0; k < n; k++)
                            {
                                Vector3 point = hits[k].point;
                                if (hits[k].distance <= 0f && point == Vector3.zero) point = origin;
                                ReportAssist(hits[k].collider, point, dir);
                            }
                        }
                    }
                }
                prev[i] = cur;
            }
            Settle(false);
            StoreSegments();
        }

        void StoreSegments()
        {
            int n = SegmentCount * 2;
            if (segPrev.Length != n) segPrev = new Vector3[n];
            for (int s = 0; s < SegmentCount; s++) { Vector3 a, b; if (GetSegment(s, out a, out b)) { segPrev[s * 2] = a; segPrev[s * 2 + 1] = b; } }
        }

        /// <summary>The blade's edge (the segments, from last frame's place to this one's at <paramref name="k"/> of the way)
        /// to the target's body capsules: the least gap (m) and the body point.</summary>
        float Gap(Pending p, float k, out Vector3 bodyAt)
        {
            bodyAt = p.point;
            float best = float.MaxValue;
            for (int s = 0; s < SegmentCount; s++)
            {
                Vector3 a, b;
                if (!GetSegment(s, out a, out b)) continue;
                if (segPrev.Length >= s * 2 + 2) { a = Vector3.Lerp(segPrev[s * 2], a, k); b = Vector3.Lerp(segPrev[s * 2 + 1], b, k); }
                foreach (var c in p.body)
                {
                    if (c == null || !c.enabled) continue;
                    for (int i = 0; i <= 8; i++)
                    {
                        Vector3 q = Vector3.Lerp(a, b, i / 8f), cp = c.ClosestPoint(q);
                        float dd = (cp - q).magnitude;
                        if (dd < best) { best = dd; bodyAt = cp; }
                    }
                }
            }
            return best;
        }

        /// <summary>The waiting contacts: delivered on the first sub-step within deliverGap; a reach miss once the blade has
        /// passed its closest without coming that close (or the wait or the window ran out).</summary>
        void Settle(bool closing)
        {
            if (pending.Count == 0) return;
            pendingKeys.Clear(); pendingKeys.AddRange(pending.Keys);
            foreach (var who in pendingKeys)
            {
                var p = pending[who];
                bool done = false, passed = false;
                for (int k = 1; k <= 6 && !done; k++)
                {
                    Vector3 at;
                    float g = Gap(p, k / 6f, out at);
                    if (g <= DeliverGap) { Deliver(who, p, at); done = true; }
                    else if (g < p.best) p.best = g;
                    else if (g > p.best + 0.01f) passed = true;
                }
                if (done) continue;
                if (passed || closing || Time.time - p.since > maxDeliverWait)
                {
                    float best = p.best;
                    Drop(who, p);
                    if (ReachMiss != null) ReachMiss(this, who, best);
                }
            }
        }

        void Deliver(Combatant who, Pending p, Vector3 at)
        {
            Hurtbox hb = p.hb; Vector3 dir = p.dir;
            Drop(who, p);
            if (who == null || who.IsDead) return;
            if (OnContact != null) OnContact(who, hb, at, dir);
        }

        void Drop(Combatant who, Pending p) { pending.Remove(who); p.hb = null; p.body = null; pendingPool.Push(p); }

        /// <summary>The foe's hurtbox colliders, found on his first detection by this blade.</summary>
        Collider[] BodyOf(Combatant who)
        {
            Collider[] body;
            if (bodyOf.TryGetValue(who, out body)) return body;
            if (bodyOf.Count >= 32) bodyOf.Clear();   // the fallen are not kept for ever
            colScratch.Clear();
            who.GetComponentsInChildren(hurtScratch);
            foreach (var h in hurtScratch) { Collider c; if (h.TryGetComponent(out c)) colScratch.Add(c); }
            body = colScratch.ToArray();
            bodyOf[who] = body;
            return body;
        }

        void Report(Collider col, Vector3 point, Vector3 dir)
        {
            if (col == null) return;
            // TryGetComponent: GetComponent of a collider with no hurtbox allocates in the editor (GK-26)
            Hurtbox hb;
            if (!col.TryGetComponent(out hb) || hb.owner == null || hb.owner == Owner) return;
            if (Owner != null && hb.owner.team == Owner.team) return;
            if (hitThisSwing.Contains(hb.owner)) return;
            hitThisSwing.Add(hb.owner);
            if (!DeliverAtContact) { if (OnContact != null) OnContact(hb.owner, hb, point, dir); return; }
            // detected: delivered when the blade reaches the body (Settle), this frame if it already has
            var p = pendingPool.Count > 0 ? pendingPool.Pop() : new Pending();
            p.hb = hb; p.body = BodyOf(hb.owner); p.dir = dir; p.point = point; p.best = float.MaxValue; p.since = Time.time;
            pending[hb.owner] = p;
        }

        /// <summary>Wider test, intended target only; the contact point is moved onto its body.</summary>
        void ReportAssist(Collider col, Vector3 point, Vector3 dir)
        {
            if (col == null) return;
            Hurtbox hb;
            if (!col.TryGetComponent(out hb) || hb.owner != assistTarget) return;
            Report(col, col.ClosestPoint(point), dir);
        }

        void OnDrawGizmosSelected()
        {
            if (bladeBase == null || bladeTip == null) return;
            Gizmos.color = Active ? Color.red : new Color(1f, 0.6f, 0f, 0.6f);
            for (int s = 0; s < SegmentCount; s++)
            {
                Vector3 a, b;
                if (!GetSegment(s, out a, out b)) continue;
                for (int i = 0; i < Mathf.Max(2, samples); i++)
                    Gizmos.DrawWireSphere(Vector3.Lerp(a, b, (float)i / (Mathf.Max(2, samples) - 1)), R);
            }
        }
    }
}
