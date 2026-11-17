using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// The bridge for one join of the light chain (Team Lead's option b, the f548 jerk): sword and shield's L2 (the
    /// downward cut) chained into L3 (the slash combo). The two clips do not meet (chainmatch: the sword hand 68-107 cm
    /// apart, no library clip within 20 cm / 20 deg), so the arm crosses ~0.7 m in the blend and the blade went through
    /// the shield (4 cm), where the wrist solver escaped 21 deg in a frame (152 mm at the tip). For <see cref="duration"/>
    /// after that join the sword hand is carried by a two-bone IK from where L2 left it to where L3's animation has it,
    /// eased, and pushed out along the blade solver's own contact normal whenever the blade would enter the body or the
    /// shield, so the solver never has to escape. Every other join is left to the animation.
    /// </summary>
    [DefaultExecutionOrder(158)]   // after the inertializer (150) and SwingAim (155); before the arm pose (164), the hand (165) and the blade solver (210)
    public class ChainBridge : MonoBehaviour
    {
        [Tooltip("Seconds the bridge carries the hand after the join.")] public float duration = 0.28f;
        [Tooltip("Fastest the push out of the shield may grow or fade (m/s).")] public float maxPushSpeed = 3f;
        [Tooltip("Margin kept between the blade and what it must not enter (m).")] public float clearance = 0.02f;
        [Tooltip("How far the hand's path swings out in front of the body at mid-bridge (m): the cross is made in front of "
                 + "the shield, not through it.")] public float arc = 0.22f;
        [Tooltip("Share of the bridge over which the IK hands back to the animation (no hard stop: v1 left a 60 deg wrist spike).")]
        [Range(0.05f, 0.6f)] public float fadeOut = 0.35f;
        [Tooltip("Fastest the elbow may move away from where it was last frame (m/s): v1 raised it 45 deg in 4 frames.")]
        public float maxElbowSpeed = 2.5f;

        /// <summary>The A/B switch (the bridge command 'bridge on|off'). Off until the Director has seen old against new
        /// (Team Lead: the old chain stays live until then).</summary>
        public static bool Enabled = false;

        /// <summary>Seconds since the bridge took the hand (negative when idle): QA reads it.</summary>
        public float Active { get { return t; } }

        Combatant self;
        Animator anim;
        WeaponSelfCollision solver;
        Transform upper, lower, hand, hips;
        AttackData from, to, prevAttack;
        Vector3 lastLocal, startLocal, push;
        Quaternion lastRotLocal, startRotLocal;
        bool haveLast;
        float t = -1f;
        Vector3 lastElbow; bool haveElbow;

        void Start()
        {
            self = GetComponent<Combatant>();
            anim = self != null ? self.Anim : GetComponent<Animator>();
            if (anim == null || !anim.isHuman) { enabled = false; return; }
            upper = anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
            lower = anim.GetBoneTransform(HumanBodyBones.RightLowerArm);
            hand = anim.GetBoneTransform(HumanBodyBones.RightHand);
            hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            foreach (var s in GetComponentsInChildren<WeaponSelfCollision>(true)) if (!s.leftHand) solver = s;
            // the join: the sword-and-shield light chain's second cut into its third
            var styles = GetComponent<FightingStyles>();
            var set = styles != null ? styles.swordShield : self.moves;
            var l1 = set != null ? set.lightStart : null;
            from = l1 != null ? l1.nextLight : null;
            to = from != null ? from.nextLight : null;
            if (from == null || to == null || upper == null || lower == null || hand == null) { enabled = false; return; }
            self.AttackStarted += OnAttack;
        }

        void OnDestroy() { if (self != null) self.AttackStarted -= OnAttack; }

        void OnAttack(AttackData a)
        {
            if (!Enabled || a != to || prevAttack != from || !haveLast) return;
            t = 0f; startLocal = lastLocal; startRotLocal = lastRotLocal; push = Vector3.zero; haveElbow = false;
        }

        Quaternion Frame() { Vector3 f = Vector3.ProjectOnPlane(hips.forward, Vector3.up); return Quaternion.LookRotation(f.sqrMagnitude > 1e-4f ? f : transform.forward, Vector3.up); }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (t >= 0f && self.CurrentAttack == to)
            {
                t += dt;
                float s = Mathf.Clamp01(t / duration);
                float w = s * s * (3f - 2f * s);
                Quaternion fr = Frame();
                Quaternion animUpperRot = upper.rotation, animLowerRot = lower.rotation;
                Vector3 startW = hips.position + fr * startLocal;
                Quaternion startRotW = fr * startRotLocal;
                Vector3 animPos = hand.position; Quaternion animRot = hand.rotation;
                Vector3 animElbow = lower.position;
                // the planned path: eased from L2's exit to L3's hand, bowed out in front of the body (and a little up)
                Vector3 bow = (fr * new Vector3(0f, 0.35f, 1f)).normalized * arc * Mathf.Sin(Mathf.PI * s);
                Vector3 goal = Vector3.Lerp(startW, animPos, w) + bow;
                Quaternion goalRot = Quaternion.Slerp(startRotW, animRot, w);
                // a safety net: if the blade would still enter something, out along the solver's own contact normal
                Vector3 want = push * (1f - w * w);
                for (int i = 0; i < 2; i++)
                {
                    upper.rotation = animUpperRot; lower.rotation = animLowerRot; hand.rotation = animRot;   // each try from the animated arm
                    Reach(goal + want, goalRot);
                    if (solver == null) break;
                    string into; Vector3 n;
                    float d = solver.MeasureNow(out into, out n);
                    if (d <= 0f) break;
                    want += n * (d + clearance);
                }
                push = Vector3.MoveTowards(push, want, maxPushSpeed * dt);
                // hand back to the animation over the last part: the IK's weight falls to 0 by the end
                float k = s < 1f - fadeOut ? 1f : Mathf.SmoothStep(1f, 0f, (s - (1f - fadeOut)) / fadeOut);
                Vector3 target = Vector3.Lerp(animPos, goal + push * (1f - w * w), k);
                Quaternion targetRot = Quaternion.Slerp(animRot, goalRot, k);
                // put the arm back to the animated pose, then reach once for the blended target
                upper.rotation = animUpperRot; lower.rotation = animLowerRot; hand.rotation = animRot;
                Reach(target, targetRot);
                // the elbow may not jump
                if (haveElbow && dt > 0f)
                {
                    Vector3 e = lower.position;
                    Vector3 limited = Vector3.MoveTowards(lastElbow, e, maxElbowSpeed * dt);
                    if ((limited - e).sqrMagnitude > 1e-6f)
                    {
                        upper.rotation = Quaternion.FromToRotation(e - upper.position, limited - upper.position) * upper.rotation;
                        Vector3 hb = hand.position;
                        lower.rotation = Quaternion.FromToRotation(hb - lower.position, target - lower.position) * lower.rotation;
                        hand.rotation = targetRot;
                    }
                }
                lastElbow = lower.position; haveElbow = true;
                if (s >= 1f) t = -1f;
            }
            else t = -1f;
            prevAttack = self.CurrentAttack;
            Quaternion f0 = Frame(), inv = Quaternion.Inverse(f0);
            lastLocal = inv * (hand.position - hips.position);
            lastRotLocal = inv * hand.rotation;
            haveLast = true;
        }

        /// <summary>Two-bone IK: the hand to <paramref name="target"/> with <paramref name="rot"/>, the elbow kept on the
        /// side the animation bends it.</summary>
        void Reach(Vector3 target, Quaternion rot)
        {
            Vector3 a = upper.position, b = lower.position, c = hand.position;
            float la = Vector3.Distance(a, b), lb = Vector3.Distance(b, c);
            Vector3 at = target - a;
            float d = Mathf.Clamp(at.magnitude, Mathf.Abs(la - lb) + 1e-3f, la + lb - 1e-3f);
            Vector3 dir = at.sqrMagnitude > 1e-8f ? at.normalized : (c - a).normalized;
            // the bend plane from the animated elbow
            Vector3 pole = b - a - Vector3.Project(b - a, (c - a).normalized);
            if (pole.sqrMagnitude < 1e-6f) pole = Vector3.Cross(dir, transform.up);
            float cosA = Mathf.Clamp((la * la + d * d - lb * lb) / (2f * la * d), -1f, 1f);
            Vector3 bendAxis = Vector3.Cross(dir, pole).normalized;
            Vector3 elbow = a + Quaternion.AngleAxis(-Mathf.Acos(cosA) * Mathf.Rad2Deg, bendAxis) * dir * la;
            upper.rotation = Quaternion.FromToRotation(b - a, elbow - a) * upper.rotation;
            b = lower.position; c = hand.position;
            lower.rotation = Quaternion.FromToRotation(c - b, (a + dir * d) - b) * lower.rotation;
            hand.rotation = rot;
        }
    }
}
