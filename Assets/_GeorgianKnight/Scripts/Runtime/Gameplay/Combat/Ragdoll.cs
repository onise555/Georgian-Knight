using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Kinematic bone bodies that become a physical ragdoll on death. With a hit-only set (GKRagdollFit.BuildHitSet) their
    /// colliders are off while alive (no hits, no physics cost) and switch on at death; without one they double as hurtboxes.
    /// Built by the editor (Georgian Knight > Build Everything).
    /// </summary>
    public class Ragdoll : MonoBehaviour
    {
        public List<Rigidbody> bodies = new List<Rigidbody>();
        public List<Collider> colliders = new List<Collider>();
        public Rigidbody hips;
        public bool IsActive { get; private set; }
        [Tooltip("Off while alive: the hit-only set takes the hits; the colliders switch on at death (the blade solver still reads them).")]
        public bool collidersOffWhileAlive;

        void Awake()
        {
            SetKinematic(true);
            if (collidersOffWhileAlive) for (int i = 0; i < colliders.Count; i++) if (colliders[i] != null) colliders[i].enabled = false;
        }

        void SetKinematic(bool kinematic)
        {
            for (int i = 0; i < bodies.Count; i++)
            {
                if (bodies[i] == null) continue;
                bodies[i].isKinematic = kinematic;
                bodies[i].interpolation = kinematic ? RigidbodyInterpolation.None : RigidbodyInterpolation.Interpolate;
                bodies[i].collisionDetectionMode = kinematic ? CollisionDetectionMode.Discrete : CollisionDetectionMode.ContinuousSpeculative;
            }
            for (int i = 0; i < colliders.Count; i++)
                if (colliders[i] != null) colliders[i].isTrigger = kinematic;
        }

        public void Activate(Vector3 impulse, Vector3 atPoint)
        {
            if (IsActive) return;
            IsActive = true;
            Animator a = GetComponent<Animator>();
            if (a != null) a.enabled = false;
            for (int i = 0; i < colliders.Count; i++) if (colliders[i] != null) colliders[i].enabled = true;
            SetKinematic(false);
            Rigidbody closest = hips;
            float best = float.MaxValue;
            for (int i = 0; i < bodies.Count; i++)
            {
                if (bodies[i] == null) continue;
                float d = (bodies[i].worldCenterOfMass - atPoint).sqrMagnitude;
                if (d < best) { best = d; closest = bodies[i]; }
            }
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i] != null) bodies[i].AddForce(impulse * 0.35f, ForceMode.VelocityChange);
            if (closest != null) closest.AddForceAtPosition(impulse * 6f, atPoint, ForceMode.Impulse);
        }
    }
}
