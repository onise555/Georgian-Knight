using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>Blood drops that reach the ground leave a mark there (a few per burst).</summary>
    [RequireComponent(typeof(ParticleSystem))]
    public class BloodDrops : MonoBehaviour
    {
        public HitFX fx;
        ParticleSystem ps;
        // sized up front: GetCollisionEvents grows a smaller list while drops land (GK-26)
        readonly List<ParticleCollisionEvent> events = new List<ParticleCollisionEvent>(64);

        void Awake() { ps = GetComponent<ParticleSystem>(); }

        void OnParticleCollision(GameObject other)
        {
            if (fx == null) return;
            // a dropped sword or shield, a ragdoll limb: it moves on and the mark would hang in the air where it was
            var rb = other.GetComponentInParent<Rigidbody>();
            if (rb != null && !rb.isKinematic) return;
            int n = ps.GetCollisionEvents(other, events);
            for (int i = 0; i < n; i++)
            {
                if (Random.value > 0.3f) continue;     // most drops soak in unseen; a few leave a mark
                var e = events[i];
                Vector3 v = e.velocity;
                // a real drop leaves a mark of a few centimetres; faster ones splash wider
                float size = Mathf.Lerp(0.03f, 0.16f, Mathf.Clamp01(v.magnitude / 7f)) * (0.7f + Random.value * 0.6f);
                fx.LaySplat(e.intersection, e.normal, v, size);
            }
        }
    }
}
