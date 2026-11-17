using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// The two-sword guard: blades crossed in an X before the face. None of the packs has a crossed-sword block, so the
    /// arms are put there by the animator's IK on top of the square two-sword stance: both fists before the chest,
    /// shoulder width apart, each blade rising toward the middle so the two cross a hand's breadth before the chin, the
    /// right blade resting on the left one (a finger's width in front of it, never through it), flats to the opponent.
    /// Elbows are hinted out and down. A blocked blow knocks the cross back toward the chest and it recovers.
    /// The blade direction in each fist is read from the swords themselves, so the grips stay exactly as held.
    /// </summary>
    public sealed class DualGuardIK : MonoBehaviour
    {
        [Header("Where the cross sits (character space, from the chest bone, m)")]
        public float fistsForward = 0.36f;
        public float fistsApart = 0.2f;
        public float fistsUp = 0.14f;
        public float crossForward = 0.5f;
        public float crossUp = 0.40f;
        [Tooltip("The right blade lies this far in front of the left where they cross (m).")]
        public float crossGap = 0.015f;
        [Header("Feel")]
        public float raiseRate = 9f, lowerRate = 6f;
        public float recoil = 0.07f, recoilRecover = 7f;

        Animator anim;
        Combatant self;
        FightingStyles styles;
        Transform chest, rHand, lHand;
        float weight, kick;
        // Mecanim's hand IK goals are not the hand bones' own rotations: a fixed per-hand offset (bone -> goal),
        // measured on frames the guard leaves the arms to the clip
        Quaternion animGoalR = Quaternion.identity, animGoalL = Quaternion.identity;
        Quaternion offR = Quaternion.identity, offL = Quaternion.identity;
        bool haveOff, sampled;

        /// <summary>0..1, how much the cross guard holds the arms now.</summary>
        public float Weight { get { return weight; } }

        void Awake()
        {
            anim = GetComponent<Animator>();
            self = GetComponent<Combatant>();
            styles = GetComponent<FightingStyles>();
            if (anim != null && anim.isHuman)
            {
                chest = anim.GetBoneTransform(HumanBodyBones.UpperChest) ?? anim.GetBoneTransform(HumanBodyBones.Chest);
                rHand = anim.GetBoneTransform(HumanBodyBones.RightHand);
                lHand = anim.GetBoneTransform(HumanBodyBones.LeftHand);
            }
            if (self != null) self.HitReceived += (h, r) => { if (r == HitResult.Blocked && weight > 0.5f) kick = 1f; };
        }

        bool Wanted
        {
            get
            {
                return self != null && styles != null && styles.Style == WeaponStyle.DualSwords && styles.OffInHand &&
                       self.IsBlocking && self.offWeapon != null && self.weapon != null;
            }
        }

        void LateUpdate()
        {
            // the clip alone posed the hands this frame: bone -> goal offsets
            if (!sampled || rHand == null || lHand == null) return;
            offR = Quaternion.Inverse(rHand.rotation) * animGoalR;
            offL = Quaternion.Inverse(lHand.rotation) * animGoalL;
            haveOff = true;
            sampled = false;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            weight = Mathf.MoveTowards(weight, Wanted ? 1f : 0f, dt * (Wanted ? raiseRate : lowerRate));
            kick = Mathf.MoveTowards(kick, 0f, dt * recoilRecover);
        }

        /// <summary>The guard's targets in world space for this frame (also used by the take metrics).</summary>
        public void Targets(out Vector3 fistR, out Vector3 fistL, out Vector3 crossR, out Vector3 crossL)
        {
            Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 c = chest != null ? chest.position : transform.position + Vector3.up * 1.35f;
            float back = Mathf.SmoothStep(0f, 1f, kick) * recoil;
            Vector3 f0 = c + fwd * (fistsForward - back) + Vector3.up * fistsUp;
            fistR = f0 + right * fistsApart;
            fistL = f0 - right * fistsApart;
            Vector3 x = c + fwd * (crossForward - back) + Vector3.up * crossUp;
            crossR = x + fwd * (crossGap * 0.5f);
            crossL = x - fwd * (crossGap * 0.5f);
        }

        void OnAnimatorIK(int layer)
        {
            if (anim == null || rHand == null || lHand == null) return;
            animGoalR = anim.GetIKRotation(AvatarIKGoal.RightHand);
            animGoalL = anim.GetIKRotation(AvatarIKGoal.LeftHand);
            sampled = weight <= 0.001f;
            if (weight <= 0.001f || !haveOff) return;
            Vector3 fistR, fistL, crossR, crossL;
            Targets(out fistR, out fistL, out crossR, out crossL);
            Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            float w = Mathf.SmoothStep(0f, 1f, weight);
            Hand(AvatarIKGoal.RightHand, AvatarIKHint.RightElbow, rHand, self.weapon, fistR, crossR, fwd, right, w, offR);
            Hand(AvatarIKGoal.LeftHand, AvatarIKHint.LeftElbow, lHand, self.offWeapon, fistL, crossL, fwd, -right, w, offL);
        }

        void Hand(AvatarIKGoal goal, AvatarIKHint hint, Transform hand, WeaponHitbox blade, Vector3 fist, Vector3 cross,
                  Vector3 fwd, Vector3 outward, float w, Quaternion boneToGoal)
        {
            if (blade == null || blade.bladeBase == null || blade.bladeTip == null) return;
            // the blade and its width as the fist holds them (hand space), from the grip at rest: the blade solver's
            // last correction fed back into the aim and the cross drifted apart from one guard to the next
            Transform sw = blade.transform;
            Vector3 bW = sw.InverseTransformDirection(blade.bladeTip.position - blade.bladeBase.position).normalized;
            var sr = sw.Find("SheathRef");     // +Y across the blade (the sword's width)
            Vector3 wW = sr != null ? sr.localRotation * Vector3.up : Vector3.up;
            Quaternion restW = sw.rotation;
            foreach (var solver in GetComponents<WeaponSelfCollision>())
                if (solver.weapon == sw && sw.parent != null) { restW = sw.parent.rotation * solver.RestLocalRotation; break; }
            Vector3 bIn = Quaternion.Inverse(hand.rotation) * (restW * bW);
            Vector3 wIn = Quaternion.Inverse(hand.rotation) * (restW * wW);
            wIn = Vector3.ProjectOnPlane(wIn, bIn).normalized;
            // the blade's base sits off the wrist bone (in the fist, past the knuckles): aim the blade line from where
            // the base will be, refined twice
            Vector3 baseIn = Quaternion.Inverse(hand.rotation) * (blade.bladeBase.position - hand.position);
            Quaternion animBone = anim.GetIKRotation(goal) * Quaternion.Inverse(boneToGoal);
            Quaternion bone = animBone;
            Vector3 b = (cross - fist).normalized;
            for (int it = 0; it < 3; it++)
            {
                // flats to the opponent: the width runs across the face of the guard
                Vector3 wd = Vector3.Cross(fwd, b).normalized;
                if (Vector3.Dot(animBone * wIn, wd) < 0f) wd = -wd;   // the turn nearer to the clip's own wrist
                bone = Quaternion.LookRotation(b, wd) * Quaternion.Inverse(Quaternion.LookRotation(bIn, wIn));
                Vector3 baseW = fist + bone * baseIn;
                b = (cross - baseW).normalized;
            }
            Quaternion rot = bone * boneToGoal;
            anim.SetIKPositionWeight(goal, w);
            anim.SetIKRotationWeight(goal, w);
            anim.SetIKPosition(goal, fist);
            anim.SetIKRotation(goal, rot);
            anim.SetIKHintPositionWeight(hint, w);
            anim.SetIKHintPosition(hint, fist + outward * 0.28f - fwd * 0.22f + Vector3.down * 0.18f);
        }
    }
}
