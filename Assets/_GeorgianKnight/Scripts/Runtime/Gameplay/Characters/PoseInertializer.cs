using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Inertialized animation transitions (Bollo, "Inertialization: High-Performance Animation Transitions in Gears of
    /// War", GDC 2018). Instead of cross-fading two clips — which still snaps when the clips' poses differ a lot, e.g.
    /// hips 20 cm apart between two combo attacks — the new clip starts at once (no input lag) and the difference
    /// between the last displayed pose and the new pose is stored as a per-bone offset that decays to zero with a
    /// smootherstep curve (zero velocity at both ends: no pop, no hitch). Triggered explicitly by Combatant on every
    /// action start, and automatically when a pose discontinuity is detected (layer weight snaps, state cuts).
    /// Runs after the Animator and before the procedural layers (scabbard, holster, weapon collision, hitboxes), so
    /// everything downstream sees the pose the player sees.
    /// </summary>
    [DefaultExecutionOrder(150)]
    public sealed class PoseInertializer : MonoBehaviour
    {
        [Tooltip("Default blend time for detected discontinuities (s).")]
        public float autoDuration = 0.16f;
        [Tooltip("Hips displacement per 60 fps frame (relative to the rig root) treated as a discontinuity (m).")]
        public float autoHipsThreshold = 0.05f;
        [Tooltip("Core bone rotation per 60 fps frame treated as a discontinuity (deg). Arms are never auto-triggered.")]
        public float autoCoreThreshold = 24f;

        Animator anim;
        Combatant self;
        Transform hips;
        int hipsIndex = -1;
        Transform[] bones;
        bool[] core;
        Quaternion[] prevRot, offRot;
        Vector3 prevHips, offHips;
        float t, duration, pending = -1f;
        bool active, hasPrev;

        public bool Active { get { return active; } }

        void Awake()
        {
            anim = GetComponent<Animator>();
            self = GetComponent<Combatant>();
            if (anim == null || !anim.isHuman) { enabled = false; return; }
            hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            var list = new System.Collections.Generic.List<Transform>();
            var cl = new System.Collections.Generic.List<bool>();
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var b = (HumanBodyBones)i;
                var tr = anim.GetBoneTransform(b);
                if (tr == null) continue;
                list.Add(tr);
                cl.Add(b == HumanBodyBones.Spine || b == HumanBodyBones.Chest || b == HumanBodyBones.UpperChest || b == HumanBodyBones.Neck ||
                       b == HumanBodyBones.LeftUpperLeg || b == HumanBodyBones.RightUpperLeg || b == HumanBodyBones.LeftLowerLeg || b == HumanBodyBones.RightLowerLeg);
            }
            bones = list.ToArray(); core = cl.ToArray();
            hipsIndex = System.Array.IndexOf(bones, hips);
            prevRot = new Quaternion[bones.Length]; offRot = new Quaternion[bones.Length];
        }

        /// <summary>Blend from the currently displayed pose into whatever the Animator produces next, over `seconds`.</summary>
        public void Trigger(float seconds)
        {
            if (!enabled) return;
            pending = Mathf.Max(pending, Mathf.Max(0.05f, seconds));
        }

        /// <summary>
        /// The rig's root transform moved by <paramref name="worldDelta"/> while the body is meant to stay where it is
        /// (traversal clips whose motion is baked into the pose step the root at loop wraps and clip changes): shift the
        /// remembered hips into the new frame so the step is neither detected as a pop nor blended.
        /// </summary>
        public void RootMoved(Vector3 worldDelta)
        {
            if (!hasPrev || hips == null || hips.parent == null) return;
            prevHips -= hips.parent.InverseTransformVector(worldDelta);
        }

        Vector3 movePos; Quaternion moveRot, moveParent; bool moving;

        /// <summary>Call before moving/rotating the root while the body must stay where it is; then move it and call
        /// <see cref="EndRootMove"/>. The remembered hips pose is re-expressed in the new root frame, so the step is
        /// neither detected as a pop nor blended (a root that turns 180° under a still body stays invisible).</summary>
        public void BeginRootMove()
        {
            moving = hasPrev && hips != null && hips.parent != null && hipsIndex >= 0;
            if (!moving) return;
            movePos = hips.parent.TransformPoint(prevHips);
            moveRot = hips.parent.rotation * prevRot[hipsIndex];
            moveParent = hips.parent.rotation;
        }

        public void EndRootMove()
        {
            if (!moving) return;
            moving = false;
            prevHips = hips.parent.InverseTransformPoint(movePos);
            prevRot[hipsIndex] = Quaternion.Inverse(hips.parent.rotation) * moveRot;
            if (active)
            {
                // a running blend keeps its world-space effect: re-express the hips offsets in the turned frame
                Quaternion d = Quaternion.Inverse(hips.parent.rotation) * moveParent;
                offRot[hipsIndex] = d * offRot[hipsIndex] * Quaternion.Inverse(d);
                offHips = d * offHips;
            }
        }

        /// <summary>Drops any running blend and the remembered pose (teleports, ragdoll, respawn).</summary>
        public void ResetPose() { active = false; hasPrev = false; pending = -1f; }

        bool Suspended { get { return self != null && self.Ragdoll != null && self.Ragdoll.IsActive; } }

        void LateUpdate()
        {
            if (bones == null) return;
            if (Suspended || !anim.enabled) { ResetPose(); return; }
            float dt = Time.deltaTime;
            if (dt <= 0f) return;                                            // paused / hit-stop frozen: keep everything as is

            if (hasPrev && pending < 0f && !active)
            {
                // detect discontinuities the code did not announce (60 fps normalised)
                float k = (1f / 60f) / dt;
                if ((prevHips - hips.localPosition).magnitude * k > autoHipsThreshold) pending = autoDuration;
                else
                    for (int i = 0; i < bones.Length; i++)
                        if (core[i] && Quaternion.Angle(prevRot[i], bones[i].localRotation) * k > autoCoreThreshold) { pending = autoDuration; break; }
            }

            if (pending > 0f && hasPrev)
            {
                for (int i = 0; i < bones.Length; i++) offRot[i] = prevRot[i] * Quaternion.Inverse(bones[i].localRotation);
                offHips = prevHips - hips.localPosition;
                duration = pending; t = 0f; active = true;
            }
            pending = -1f;

            if (active)
            {
                float x = Mathf.Clamp01(t / duration);
                float w = 1f - x * x * x * (x * (x * 6f - 15f) + 10f);        // 1 - smootherstep
                for (int i = 0; i < bones.Length; i++)
                    bones[i].localRotation = Quaternion.SlerpUnclamped(Quaternion.identity, offRot[i], w) * bones[i].localRotation;
                hips.localPosition += offHips * w;
                t += dt;
                if (t >= duration) active = false;
            }

            for (int i = 0; i < bones.Length; i++) prevRot[i] = bones[i].localRotation;
            prevHips = hips.localPosition;
            hasPrev = true;
        }
    }
}
