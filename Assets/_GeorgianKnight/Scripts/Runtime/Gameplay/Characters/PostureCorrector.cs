using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Keeps the hero's back straight. The Mixamo guard stances were authored on a crouching axe fighter: retargeted
    /// onto the Character Creator spine they read as a man bent at the waist. After the Animator (and the pose
    /// inertializer) this measures how far the torso leans forward (hips to neck, in the body's forward plane) and
    /// straightens whatever exceeds <see cref="targetLean"/>, spread over the waist and the two spine bones, with the
    /// neck giving most of it back so the gaze stays where the animation put it. Full strength while standing or
    /// moving, a light touch during attacks and dodges (their lean is part of the move), none while down or climbing.
    /// </summary>
    [DefaultExecutionOrder(160)]   // after PoseInertializer (150), before cloth (190), scabbard (200) and holster (205)
    public sealed class PostureCorrector : MonoBehaviour
    {
        public Transform hips, spine, chest, upperChest, neck, head;
        [Tooltip("Forward lean of the torso that is left alone (degrees from vertical).")]
        public float targetLean = 7f;
        [Tooltip("Most the back is ever straightened (degrees).")]
        public float maxCorrection = 20f;
        [Range(0f, 1f)] public float freeStrength = 1f;
        [Range(0f, 1f)] public float actionStrength = 0.35f;
        [Tooltip("Share of the correction the neck gives back (keeps the eyes on the same point).")]
        [Range(0f, 1f)] public float neckGiveBack = 0.6f;

        Combatant self;
        float weight;

        /// <summary>Torso lean the animation produced this frame, and how much was taken out (degrees).</summary>
        public float LastLean { get; private set; }
        public float LastCorrection { get; private set; }

        void Awake() { self = GetComponent<Combatant>(); }

        void LateUpdate()
        {
            if (hips == null || spine == null || neck == null) return;
            float target = freeStrength;
            if (self != null)
            {
                switch (self.CurrentState)
                {
                    case Combatant.State.Free: target = freeStrength; break;
                    case Combatant.State.Dead:
                    case Combatant.State.Knockdown:
                    case Combatant.State.Traversal:
                    case Combatant.State.BeingExecuted:
                    // an attack's lean is its reach: straightening it pulled the blade back past the target
                    case Combatant.State.Attacking:
                    case Combatant.State.Executing: target = 0f; break;
                    default: target = actionStrength; break;
                }
            }
            weight = Mathf.MoveTowards(weight, target, Time.deltaTime * 6f);

            // tilt of the torso line in whatever direction it leans (the guard stance turns the chest sideways, so a
            // lean measured only in the root's forward plane missed most of it)
            Vector3 up = Vector3.up;
            Vector3 torsoLine = neck.position - hips.position;
            LastLean = Vector3.Angle(up, torsoLine);
            Vector3 right = Vector3.Cross(up, torsoLine);
            if (right.sqrMagnitude < 1e-6f) return;
            right.Normalize();
            float corr = Mathf.Clamp(LastLean - targetLean, 0f, maxCorrection) * weight;
            LastCorrection = corr;
            if (corr <= 0.01f) return;

            Rotate(spine, -corr * 0.45f, right);
            Rotate(chest, -corr * 0.35f, right);
            Rotate(upperChest != null ? upperChest : chest, -corr * 0.2f, right);
            // the gaze is given back mostly where a person nods (the skull on the top of the neck); bending it all at
            // the base of the neck pushed the head forward like a vulture's
            float give = corr * neckGiveBack;
            if (neck != null) Rotate(neck, give * 0.25f, right);
            if (head != null) Rotate(head, give * 0.75f, right);
        }

        static void Rotate(Transform t, float degrees, Vector3 axis)
        {
            if (t == null) return;
            t.rotation = Quaternion.AngleAxis(degrees, axis) * t.rotation;
        }
    }
}
