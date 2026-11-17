using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Takes the tremor out of the captured motion. Motion-capture keys carry a little noise: at 60 fps the sword
    /// tip, at the end of a 1 m lever, zigzagged up to 2-5 cm from frame to frame (measured in the raw clips with no game
    /// code), which reads as a shaking hand. Each arm and torso bone's local rotation goes through a one-euro filter
    /// (Casiez et al. 2012): its cutoff rises with the bone's own angular speed, so a slow or still bone is smoothed
    /// hard (the tremor is gone) while a fast swing passes almost untouched (no lag in a cut). Runs right after the
    /// Animator and before every procedural layer (grips, guard, solvers), which then work on the clean pose.
    /// </summary>
    [DefaultExecutionOrder(150)]
    public class TremorFilter : MonoBehaviour
    {
        [Tooltip("Cutoff at rest (Hz): lower = smoother when still.")]
        public float minCutoff = 2.2f;
        [Tooltip("How fast the cutoff rises with angular speed (per deg/s): higher = less lag in fast motion.")]
        public float beta = 0.02f;
        [Tooltip("Cutoff for the speed estimate itself (Hz).")]
        public float speedCutoff = 1.2f;

        static readonly HumanBodyBones[] Bones =
        {
            HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head,
            HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        };

        Transform[] bones;
        Quaternion[] filtered;
        float[] speed;
        bool primed;

        void Awake()
        {
            var anim = GetComponent<Animator>();
            if (anim == null || !anim.isHuman) { enabled = false; return; }
            bones = new Transform[Bones.Length];
            for (int i = 0; i < Bones.Length; i++) bones[i] = anim.GetBoneTransform(Bones[i]);
            filtered = new Quaternion[Bones.Length];
            speed = new float[Bones.Length];
        }

        void OnEnable() { primed = false; }

        static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (bones == null || dt <= 0f) return;          // frozen by hit-stop or paused: leave the pose alone
            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones[i];
                if (b == null) continue;
                Quaternion raw = b.localRotation;
                if (!primed) { filtered[i] = raw; speed[i] = 0f; continue; }
                // the bone's speed (deg/s), itself smoothed, sets how much smoothing this frame gets
                float v = Quaternion.Angle(filtered[i], raw) / dt;
                speed[i] = Mathf.Lerp(speed[i], v, Alpha(speedCutoff, dt));
                float cutoff = minCutoff + beta * speed[i];
                filtered[i] = Quaternion.Slerp(filtered[i], raw, Alpha(cutoff, dt));
                b.localRotation = filtered[i];
            }
            primed = true;
        }
    }
}
