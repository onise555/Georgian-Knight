using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// A loosed arrow: a ballistic flight (gravity, no drag) swept tip-first every frame against hurtboxes and the world.
    /// It meets a man through <see cref="Combatant.ReceiveHit"/> as a ranged blow (guarded by its flight direction): a
    /// raised shield takes it and keeps it, a timed guard knocks it aside, a dodge lets it fly on, flesh keeps it in the
    /// bone it struck; the ground and walls keep it too. The pivot is the arrowhead's tip, the shaft runs along -Z.
    /// </summary>
    public class Arrow : MonoBehaviour
    {
        [Tooltip("Gravity on the flight (m/s²).")] public float gravity = 9.8f;
        [Tooltip("Sweep radius of the head (m).")] public float radius = 0.02f;
        [Tooltip("How deep it sinks into what it strikes (m).")] public float bite = 0.1f;
        [Tooltip("Seconds a spent arrow stays before it goes.")] public float spentLife = 15f;
        [Tooltip("Seconds of flight after which a lost arrow is removed.")] public float maxFlight = 6f;

        /// <summary>Every arrow that lands: the arrow, the man it met (null for the world) and what came of it.</summary>
        public static event Action<Arrow, Combatant, HitResult> Landed;

        public Combatant Shooter { get; private set; }
        public Vector3 Velocity { get { return velocity; } }
        public bool Flying { get { return flying; } }

        Vector3 velocity;
        float damage, poise, knockback, flown, spentAt = -1f;
        HitReaction reaction;
        bool flying;
        readonly HashSet<Combatant> passed = new HashSet<Combatant>();
        static readonly RaycastHit[] hits = new RaycastHit[16];

        /// <summary>Looses <paramref name="prefab"/> from <paramref name="tip"/> with <paramref name="velocity"/>.</summary>
        public static Arrow Loose(GameObject prefab, Vector3 tip, Vector3 velocity, Combatant shooter, float damage, float poise,
                                  HitReaction reaction, float knockback = 0.4f)
        {
            if (prefab == null) return null;
            var go = Instantiate(prefab, tip, Quaternion.LookRotation(velocity.sqrMagnitude > 1e-6f ? velocity : Vector3.forward));
            var a = go.GetComponent<Arrow>();
            if (a == null) a = go.AddComponent<Arrow>();
            a.Shooter = shooter; a.velocity = velocity; a.damage = damage; a.poise = poise; a.reaction = reaction; a.knockback = knockback;
            a.flying = true; a.flown = 0f;
            if (shooter != null) a.passed.Add(shooter);
            return a;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (!flying)
            {
                if (spentAt >= 0f && Time.time - spentAt > spentLife) Destroy(gameObject);
                return;
            }
            if (dt <= 0f) return;
            flown += dt;
            if (flown > maxFlight) { Destroy(gameObject); return; }

            Vector3 from = transform.position;
            Vector3 to = from + velocity * dt + 0.5f * gravity * dt * dt * Vector3.down;
            velocity += Vector3.down * gravity * dt;
            Vector3 step = to - from;
            float len = step.magnitude;
            if (len > 1e-5f)
            {
                int mask = GK.HurtboxMask | (1 << GK.LayerEnvironment) | (1 << GK.LayerTerrain);
                int n = Physics.SphereCastNonAlloc(from, radius, step / len, hits, len, mask, QueryTriggerInteraction.Collide);
                Array.Sort(hits, 0, n, HitOrder.Instance);
                for (int i = 0; i < n; i++)
                {
                    var h = hits[i];
                    if (h.collider == null || (h.distance <= 0f && h.point == Vector3.zero)) continue;
                    if (Struck(h, step / len)) return;
                }
            }
            transform.SetPositionAndRotation(to, Quaternion.LookRotation(velocity));
        }

        /// <summary>Handles one thing on the path; true when the flight ends here.</summary>
        bool Struck(RaycastHit h, Vector3 dir)
        {
            var hb = h.collider.GetComponent<Hurtbox>();
            if (hb == null)
            {
                if (h.collider.isTrigger) return false;
                if (h.collider.gameObject.layer == GK.LayerCharacter) return false;
                StickInto(h.collider.transform, h.point, dir);
                Raise(null, HitResult.Hit);
                return true;
            }
            var man = hb.owner;
            if (man == null || passed.Contains(man) || man.IsDead) return false;
            if (Shooter != null && man.team == Shooter.team) { passed.Add(man); return false; }

            bool head = hb.name.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0;
            var hit = new HitData
            {
                attacker = Shooter, damage = damage * (head ? 1.5f : hb.damageMultiplier), poiseDamage = poise,
                reaction = reaction, knockback = knockback, point = h.point, direction = dir, ranged = true, critical = head
            };
            var r = man.ReceiveHit(hit);
            Raise(man, r);
            switch (r)
            {
                case HitResult.Dodged:
                case HitResult.Ignored:
                    passed.Add(man);
                    return false;
                case HitResult.Blocked:
                case HitResult.GuardBroken:
                    var styles = man.GetComponent<FightingStyles>();
                    Transform shield = styles != null && styles.shield != null && styles.shield.gameObject.activeInHierarchy ? styles.shield : null;
                    if (shield != null)
                    {
                        // into the shield's face, where the flight meets it
                        Vector3 p = shield.position + Vector3.ProjectOnPlane(h.point - shield.position, dir) - dir * 0.02f;
                        StickInto(shield, p, dir);
                    }
                    else Glance(dir);
                    return true;
                case HitResult.Parried:
                    Glance(dir);
                    return true;
                default:
                    StickInto(hb.transform, h.point, dir);
                    return true;
            }
        }

        void StickInto(Transform t, Vector3 point, Vector3 dir)
        {
            flying = false; spentAt = Time.time;
            transform.SetPositionAndRotation(point + dir * bite, Quaternion.LookRotation(dir));
            transform.SetParent(t, true);
        }

        /// <summary>Knocked aside: it spins off and drops, then lies where it falls.</summary>
        void Glance(Vector3 dir)
        {
            Vector3 side = Vector3.Cross(dir, Vector3.up);
            if (side.sqrMagnitude < 1e-4f) side = Vector3.right;
            velocity = (-dir * 0.15f + side.normalized * UnityEngine.Random.Range(-0.6f, 0.6f) + Vector3.up * 0.5f) * 6f;
            passed.Add(Shooter);
            gameObject.AddComponent<SpentArrow>().Init(velocity, gravity);
            flying = false; spentAt = Time.time;
        }

        void Raise(Combatant man, HitResult r) { if (Landed != null) Landed(this, man, r); }

        sealed class HitOrder : IComparer<RaycastHit>
        {
            public static readonly HitOrder Instance = new HitOrder();
            public int Compare(RaycastHit a, RaycastHit b) { return a.distance.CompareTo(b.distance); }
        }
    }
}
