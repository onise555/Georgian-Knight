using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Aims a cut at a target above or below (a slope, a foe on a rise): while an attack runs, the spine bends up or down
    /// toward the target's chest by the elevation angle, spread over spine, chest and upper chest, at most
    /// <see cref="maxPitch"/> and eased in and out. The clips were captured on flat ground: uphill their blade crossed the
    /// foe's legs or passed below him, downhill over his head (13 misses in reach on Black Banner Rise).
    /// </summary>
    [DefaultExecutionOrder(155)]   // after the animator, before the blade solver (160) and GuardAim (161)
    public class SwingAim : MonoBehaviour
    {
        [Tooltip("Most the spine bends toward a target above or below (deg).")]
        public float maxPitch = 20f;
        [Tooltip("How fast the bend comes in and goes out (1/s).")]
        public float sharpness = 12f;
        [Tooltip("Elevation below which nothing is done (deg): flat ground stays as captured.")]
        public float deadZone = 3f;

        /// <summary>On for every swinger; the bridge's 'swingaim off' measures without it.</summary>
        public static bool Enabled = true;

        Combatant self;
        Transform spine, chest, upperChest;
        float pitch;

        void Awake()
        {
            self = GetComponent<Combatant>();
            var anim = GetComponent<Animator>();
            if (anim != null && anim.isHuman)
            {
                spine = anim.GetBoneTransform(HumanBodyBones.Spine);
                chest = anim.GetBoneTransform(HumanBodyBones.Chest);
                upperChest = anim.GetBoneTransform(HumanBodyBones.UpperChest);
            }
        }

        void LateUpdate()
        {
            if (self == null || spine == null) return;
            if (!Enabled) { pitch = 0f; return; }
            float want = 0f;
            var target = self.SwingTarget != null ? self.SwingTarget : self.Target;
            if (self.CurrentState == Combatant.State.Attacking && target != null && !target.IsDead)
            {
                Vector3 from = (chest != null ? chest : spine).position;
                var ta = target.GetComponent<Animator>();
                Transform tc = ta != null && ta.isHuman ? (ta.GetBoneTransform(HumanBodyBones.Chest) ?? ta.GetBoneTransform(HumanBodyBones.Spine)) : null;
                Vector3 to = tc != null ? tc.position : target.transform.position + Vector3.up * 1.3f;
                Vector3 d = to - from;
                float flat = new Vector2(d.x, d.z).magnitude;
                float elev = Mathf.Atan2(d.y, Mathf.Max(0.3f, flat)) * Mathf.Rad2Deg;   // + = above
                if (Mathf.Abs(elev) > deadZone) want = Mathf.Clamp(elev - Mathf.Sign(elev) * deadZone, -maxPitch, maxPitch);
            }
            pitch = Mathf.Lerp(pitch, want, 1f - Mathf.Exp(-sharpness * Time.deltaTime));
            if (Mathf.Abs(pitch) < 0.05f) return;
            // bend about the body's right axis: a positive elevation tips the torso back (cut rises), negative forward
            Vector3 right = transform.right;
            Rotate(spine, -pitch * 0.3f, right);
            if (chest != null) Rotate(chest, -pitch * 0.35f, right);
            if (upperChest != null) Rotate(upperChest, -pitch * 0.35f, right);
        }

        static void Rotate(Transform bone, float deg, Vector3 axis) { bone.rotation = Quaternion.AngleAxis(deg, axis) * bone.rotation; }
    }
}
