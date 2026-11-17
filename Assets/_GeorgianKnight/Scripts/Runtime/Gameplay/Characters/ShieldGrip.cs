using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Keeps the shield hand on its grip strap. The shield rides on the forearm (enarmes): the forearm loop and the fist
    /// round the grip strap hold the wrist where it is, and the fist stays closed. The Mixamo clips open the left hand
    /// and bend the wrist freely in runs, jumps and slashes (they were made for a small round shield or none), which
    /// pulled the fist off the strap and spread the fingers on the back of the plate. After the animation the wrist and
    /// every finger joint are set back to the guard pose the straps were fitted in.
    /// </summary>
    [DefaultExecutionOrder(160)]   // before HandGrip (165) and CCHelperBones (170), so the twist bones follow the locked wrist
    public sealed class ShieldGrip : MonoBehaviour
    {
        [Tooltip("The left hand first, then its finger joints.")]
        public Transform[] bones = new Transform[0];
        [Tooltip("Their local rotations in the guard pose the straps were fitted to.")]
        public Quaternion[] guard = new Quaternion[0];
        [Range(0f, 1f)] public float weight = 1f;
        [Header("Fit (for checks)")]
        public Transform shield, forearm;
        [Tooltip("Where the hand and the forearm loop's centre sat, in the shield's space, when the straps were fitted.")]
        public Vector3 handInShield, loopInShield;

        /// <summary>How far the hand is from where the straps hold it (m): 0 while it grips.</summary>
        public float HandOff => shield != null && bones.Length > 0 && bones[0] != null ? Vector3.Distance(shield.TransformPoint(handInShield), bones[0].position) : -1f;
        /// <summary>How far the forearm loop's centre is from the forearm bone's axis (m): 0 while the loop is round the arm.</summary>
        public float LoopOff
        {
            get
            {
                if (shield == null || forearm == null || bones.Length == 0 || bones[0] == null) return -1f;
                Vector3 a = forearm.position, b = bones[0].position, c = shield.TransformPoint(loopInShield);
                Vector3 ab = b - a;
                float t = Mathf.Clamp01(Vector3.Dot(c - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
                return Vector3.Distance(c, a + ab * t);
            }
        }

        Combatant self;

        void Awake() { self = GetComponentInParent<Combatant>(); }

        void LateUpdate()
        {
            if (weight <= 0f || bones.Length != guard.Length) return;
            // a ragdoll owns the arm once it is dead
            if (self != null && self.IsDead) return;
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] != null)
                    bones[i].localRotation = weight >= 1f ? guard[i] : Quaternion.Slerp(bones[i].localRotation, guard[i], weight);
        }
    }
}
