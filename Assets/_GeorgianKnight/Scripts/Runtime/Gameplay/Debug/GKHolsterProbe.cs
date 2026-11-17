using System.Text;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Play-mode QA probe for draw / sheathe: logs, every frame, the palm-to-grip distance, how far the sword jumps
    /// between frames (pops), the scabbard swing and clearance, and the holster state. Writes a summary on demand.
    /// Attached by the bridge command "holsterqa".
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public sealed class GKHolsterProbe : MonoBehaviour
    {
        WeaponHolster holster;
        ScabbardMotion scab;
        Transform hand, mid, grip, sword;
        Vector3 lastSwordPos; bool hasLast;
        readonly StringBuilder log = new StringBuilder();
        public float maxJump, maxPalmGripInHand, minPalmGripBeforeSwitch = 99f, maxSwing, minClearance = 99f;
        WeaponHolster.HolsterState lastState;
        int frames;

        void Start()
        {
            holster = GetComponent<WeaponHolster>();
            scab = GetComponent<ScabbardMotion>();
            var anim = GetComponent<Animator>();
            hand = anim.GetBoneTransform(HumanBodyBones.RightHand);
            mid = anim.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
            sword = holster != null ? holster.weapon : null;
            grip = sword != null ? sword.Find("Grip") : null;
            if (holster != null) lastState = holster.State;
        }

        public void ResetStats()
        {
            maxJump = 0f; maxPalmGripInHand = 0f; minPalmGripBeforeSwitch = 99f; maxSwing = 0f; minClearance = 99f; frames = 0; hasLast = false;
            log.Length = 0;
        }

        void LateUpdate()
        {
            if (holster == null || grip == null) return;
            frames++;
            Vector3 palm = mid != null ? Vector3.Lerp(hand.position, mid.position, 0.5f) : hand.position;
            float pg = Vector3.Distance(palm, grip.position);
            bool inHand = sword.parent == hand;
            if (inHand) maxPalmGripInHand = Mathf.Max(maxPalmGripInHand, pg);
            else if (holster.State == WeaponHolster.HolsterState.Drawing) minPalmGripBeforeSwitch = Mathf.Min(minPalmGripBeforeSwitch, pg);
            // pops: how far the sword moved relative to the hips this frame (body motion removed)
            var hips = scab != null ? scab.hips : transform;
            Vector3 local = hips.InverseTransformPoint(sword.position);
            if (hasLast)
            {
                float jump = (local - lastSwordPos).magnitude;
                if (jump > maxJump) maxJump = jump;
                if (jump > 0.12f) log.AppendLine(string.Format("frame {0}: sword jumped {1:F2} m (state {2}, parent {3})", frames, jump, holster.State, sword.parent.name));
            }
            lastSwordPos = local; hasLast = true;
            if (scab != null)
            {
                maxSwing = Mathf.Max(maxSwing, scab.LastSwing);
                minClearance = Mathf.Min(minClearance, scab.LastClearance);
            }
            if (holster.State != lastState)
            {
                log.AppendLine(string.Format("frame {0}: {1} -> {2} (palm-grip {3:F3} m, parent {4})", frames, lastState, holster.State, pg, sword.parent.name));
                lastState = holster.State;
            }
        }

        public string Summary()
        {
            return string.Format("holsterqa: frames {0}, max sword jump/frame {1:F3} m, palm-grip in hand max {2:F3} m, closest palm-grip before switch {3:F3} m, scabbard max swing {4:F0}°, min clearance {5:F3} m\n{6}",
                frames, maxJump, maxPalmGripInHand, minPalmGripBeforeSwitch, maxSwing, minClearance, log);
        }
    }
}
