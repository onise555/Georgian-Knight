using UnityEngine;
using UnityEngine.Playables;

namespace GeorgianKnight.Cinematics
{
    /// <summary>
    /// The game's sword draw, driven by the intro timeline's clock instead of the holster: the sword rests in the
    /// scabbard until the hand reaches the hilt, then changes to the hand and eases into the authored grip, the same
    /// hand-over <c>WeaponHolster</c> does in play. Runs in edit mode too, so scrubbing the timeline shows it.
    /// </summary>
    [ExecuteAlways, DefaultExecutionOrder(-100)]   // before the grip solvers, which read the sword's parent
    public sealed class IntroSwordHandOff : MonoBehaviour
    {
        public PlayableDirector director;
        public Transform sword, hand, sheath;
        public float grabTime = 6.8f;
        public float handOverBlend = 0.14f;
        public Vector3 handLocalPos; public Quaternion handLocalRot = Quaternion.identity;
        public Vector3 sheathLocalPos; public Quaternion sheathLocalRot = Quaternion.identity;

        void LateUpdate()
        {
            if (director != null) Apply((float)director.time);
        }

        public void Apply(float t)
        {
            if (sword == null || hand == null || sheath == null) return;
            if (t < grabTime)
            {
                if (sword.parent != sheath) sword.SetParent(sheath, false);
                sword.localPosition = sheathLocalPos; sword.localRotation = sheathLocalRot;
                return;
            }
            if (sword.parent != hand) sword.SetParent(hand, false);
            float b = Mathf.Clamp01((t - grabTime) / Mathf.Max(0.01f, handOverBlend));
            if (b >= 1f) { sword.localPosition = handLocalPos; sword.localRotation = handLocalRot; return; }
            // from where the sword still sits in the scabbard, seen from the hand, to the grip; the grip seats in the
            // first third, the blade's angle over the whole hand-over (as in play)
            Vector3 fromPos = hand.InverseTransformPoint(sheath.TransformPoint(sheathLocalPos));
            Quaternion fromRot = Quaternion.Inverse(hand.rotation) * sheath.rotation * sheathLocalRot;
            float k = b * b * (3f - 2f * b);
            float kp = Mathf.Min(1f, b * 3f); kp = kp * kp * (3f - 2f * kp);
            sword.localRotation = Quaternion.Slerp(fromRot, handLocalRot, k);
            sword.localPosition = Vector3.Lerp(fromPos, handLocalPos, kp);
        }
    }
}
