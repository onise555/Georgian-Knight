using System;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Measured motion data of the ladder clips, sampled on the real player avatar by the editor (GKTraversalBuilder).
    /// The clips have all motion baked into the pose, so every position is simply relative to the (unmoving) transform
    /// in the clip's own space: x right, y up, z forward, metres. In that space the ladder the clip was captured on is
    /// fixed — a gripping hand does not move — which is what the climber uses to place the clip space onto a real
    /// ladder, step it cycle by cycle, land transitions exactly and hold hands and feet on the rungs.
    /// </summary>
    public class ClimbProfile : ScriptableObject
    {
        [Serializable]
        public class ClipData
        {
            public string state;
            public float length;
            public float sampleRate = 60f;
            /// <summary>Body facing at t = 0 in the clip's raw space (degrees about up). The stored positions are rotated
            /// into a canonical frame facing +z; the transform turns by this yaw while the clip plays.</summary>
            public float yaw;
            /// <summary>Unused for baked clips (kept for clips with root motion): root displacement since t = 0.</summary>
            public Vector3[] root = new Vector3[0];
            /// <summary>Hips position relative to the transform.</summary>
            public Vector3[] hips = new Vector3[0];
            /// <summary>IK goal positions (wrists, ankles) relative to the transform.</summary>
            public Vector3[] leftHand = new Vector3[0], rightHand = new Vector3[0], leftFoot = new Vector3[0], rightFoot = new Vector3[0];

            public bool IsValid { get { return leftHand != null && leftHand.Length >= 2 && length > 0f; } }
            public Vector3 RootEnd { get { return IsValid ? root[root.Length - 1] : Vector3.zero; } }

            public Vector3 RootAt(float normalized) { return Sample(root, normalized); }
            public Vector3 LeftHandAt(float n) { return Sample(leftHand, n); }
            public Vector3 RightHandAt(float n) { return Sample(rightHand, n); }
            public Vector3 LeftFootAt(float n) { return Sample(leftFoot, n); }
            public Vector3 RightFootAt(float n) { return Sample(rightFoot, n); }

            static Vector3 Sample(Vector3[] a, float n)
            {
                if (a == null || a.Length == 0) return Vector3.zero;
                if (a.Length == 1) return a[0];
                float f = Mathf.Clamp01(n) * (a.Length - 1);
                int i = Mathf.Min(a.Length - 2, (int)f);
                return Vector3.LerpUnclamped(a[i], a[i + 1], f - i);
            }
        }

        [Tooltip("Animator.humanScale of the avatar the data was measured on; other avatars rescale by their own.")]
        public float humanScale = 1f;

        public ClipData mount = new ClipData(), climb = new ClipData(), exitTop = new ClipData(), exitBottom = new ClipData();

        [Header("Derived from the climb loop (clip space)")]
        [Tooltip("Body rise per loop cycle (metres): the transform steps by this at every wrap.")]
        public float climbRise;
        [Tooltip("Rung spacing the loop was captured on: each hand moves up two rungs per cycle.")]
        public float rungPitch;
        [Tooltip("Height of a clip-space rung above the transform, modulo the pitch (the rung lattice's phase).")]
        public float rungOffset;
        [Tooltip("Forward distance of the clip's rung plane from the transform.")]
        public float planeZ;
        [Tooltip("Sideways centre of the hands and feet (the ladder's centre line) in clip space.")]
        public float centreX;
        [Tooltip("Ankle height above the ground when standing (from the mount clip's first frame).")]
        public float ankleHeight = 0.09f;
        [Tooltip("Wrist above / behind the rung centre while gripping.")]
        public float gripUp = 0.055f, gripBack = 0.035f;
        [Tooltip("Ankle above / behind the rung centre while standing on it (ball of the foot on the rung).")]
        public float footUp = 0.10f, footBack = 0.075f;
        [Tooltip("Loop phases (normalized) at which both hands grip: where a climber may rest.")]
        public float[] restPhases = { 0.45f, 0.95f };
        [Tooltip("Loop phase whose pose is closest to the first frame of the top exit / the last frame of the mount.")]
        public float exitTopPhase, mountEndPhase, exitBottomPhase;
    }
}
