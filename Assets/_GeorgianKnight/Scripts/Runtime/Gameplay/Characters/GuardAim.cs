using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// The guard meets the blow where it comes from. While the knight holds his block and an enemy close by is swinging
    /// at him, the side of the incoming blade is read from where the attacker's blade is now (a cut wound up over the
    /// right shoulder comes in from the knight's left, high), and the guard turns to it: the shield arm carries the
    /// plate's middle onto that line; the sword arm carries the blade's middle there and turns the blade across it,
    /// upright against a cut from the side, level over the head against one from above. The arm turns from the shoulder
    /// (forearm, wrist and grip keep the guard clip's shape), eases in as the swing begins and settles back after it.
    /// Runs after ShieldGrip (160) and before the shield's own yield (162), the hand grip (165) and the blade solver
    /// (210), which keeps the blade out of the body.
    /// </summary>
    [DefaultExecutionOrder(161)]
    public class GuardAim : MonoBehaviour
    {
        [Tooltip("How far the whole arm may turn from the guard pose toward the blow (deg).")]
        [Range(0f, 60f)] public float maxArmTurn = 35f;
        [Tooltip("How far the sword may turn across the incoming cut on top of that (deg).")]
        [Range(0f, 80f)] public float maxBladeTurn = 50f;
        [Tooltip("Attackers further than this are not read (m).")]
        public float readRange = 4.2f;
        public float riseRate = 9f, settleRate = 3f;

        public float Weight { get; private set; }
        public Vector3 LastTargetLocal { get; private set; }     // where the guard aimed, in the knight's space

        Combatant self;
        FightingStyles styles;
        Animator anim;
        Transform lUpper, rUpper, rHand, chest;
        Vector3 aimLocal;       // smoothed aim point, knight space

        // ---- the blow's weight in the guard: a spring the arm gives along, then returns on
        [Tooltip("How far the guarding arm gives way under the heaviest blow (deg).")]
        public float maxRecoil = 18f;
        float recoil, recoilVel;
        Vector3 recoilDir;

        void OnEnable() { if (self != null) self.HitReceived += OnHit; }
        void OnDisable() { if (self != null) self.HitReceived -= OnHit; }

        void OnHit(HitData hit, HitResult r)
        {
            if (r != HitResult.Blocked && r != HitResult.GuardBroken && r != HitResult.Parried) return;
            // weight of the blow: its poise damage (a light cut ~16, a power slash ~55) and a broken guard on top
            float force = Mathf.Clamp01(hit.poiseDamage / 55f) * (r == HitResult.GuardBroken ? 1.4f : r == HitResult.Parried ? 0.5f : 1f);
            Vector3 d = hit.direction.sqrMagnitude > 1e-4f ? hit.direction : -transform.forward;
            recoilDir = Vector3.ProjectOnPlane(d, Vector3.up).normalized * 0.6f + (hit.attacker != null ? GK.Flat(transform.position - hit.attacker.transform.position).normalized : -transform.forward) * 0.8f;
            recoilVel += Mathf.Lerp(0.35f, 1f, force) * 34f;      // a kick to the spring: the heaviest blow peaks at maxRecoil
        }

        void Awake()
        {
            self = GetComponent<Combatant>();
            styles = GetComponent<FightingStyles>();
            anim = GetComponent<Animator>();
            if (anim != null && anim.isHuman)
            {
                lUpper = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                rUpper = anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
                rHand = anim.GetBoneTransform(HumanBodyBones.RightHand);
                chest = anim.GetBoneTransform(HumanBodyBones.UpperChest) ?? anim.GetBoneTransform(HumanBodyBones.Chest);
            }
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (self == null || chest == null || dt <= 0f) return;
            // the recoil spring (critically damped, ~0.3 s): runs whether or not a threat is being read
            recoilVel += (-recoil * 160f - recoilVel * 2f * Mathf.Sqrt(160f)) * dt;
            recoil = Mathf.Clamp(recoil + recoilVel * dt, -0.2f, 1.2f);
            if (recoil > 0.01f) Recoil(recoil);
            Vector3 incoming = Vector3.zero;
            bool threat = self.IsBlocking && ReadThreat(out incoming);
            if (threat)
            {
                Vector3 l = transform.InverseTransformPoint(incoming);
                aimLocal = Weight <= 0.01f ? l : Vector3.Lerp(aimLocal, l, 1f - Mathf.Exp(-14f * dt));
            }
            Weight = Mathf.MoveTowards(Weight, threat ? 1f : 0f, (threat ? riseRate : settleRate) * dt);
            if (Weight <= 0f) return;
            float w = Weight * Weight * (3f - 2f * Weight);
            LastTargetLocal = aimLocal;
            Vector3 aim = transform.TransformPoint(aimLocal);

            bool shield = styles != null && styles.ShieldOnArm && styles.shield != null;
            if (shield) AimShield(aim, w);
            else AimSword(aim, w);
        }

        /// <summary>The point in front of the knight the blow will cross: the side the attacker's blade is wound up on,
        /// mirrored into the knight's frame, at guard distance and between hip and crown height.</summary>
        bool ReadThreat(out Vector3 point)
        {
            point = Vector3.zero;
            var dir = CombatDirector.I;
            if (dir == null) return false;
            Combatant best = null; float bestD = readRange;
            for (int i = 0; i < dir.all.Count; i++)
            {
                var c = dir.all[i];
                if (c == null || c == self || c.team == self.team || c.IsDead) continue;
                var a = c.CurrentAttack;
                if (a == null || c.ActionProgress > a.hitEnd || c.weapon == null) continue;
                float d = GK.Flat(c.transform.position - transform.position).magnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best == null) return false;
            Vector3 b0, b1;
            if (!best.weapon.GetSegment(0, out b0, out b1)) return false;
            // the blade's middle, seen from the knight's chest; a blade wound up on the attacker's right is on the
            // knight's left, which the knight's own frame already says
            Vector3 mid = (b0 + b1) * 0.5f;
            Vector3 c0 = chest.position;
            Vector3 rel = transform.InverseTransformDirection(mid - c0);
            float side = Mathf.Clamp(rel.x, -0.45f, 0.45f);
            float up = Mathf.Clamp(rel.y, -0.35f, 0.4f);
            Vector3 local = transform.InverseTransformPoint(c0) + new Vector3(side * 0.8f, up * 0.8f + 0.05f, 0.48f);
            point = transform.TransformPoint(local);
            return true;
        }

        void AimShield(Vector3 aim, float w)
        {
            if (lUpper == null) return;
            var plate = styles.shield.GetComponent<Collider>();
            var box = plate as BoxCollider;     // from the transform this frame (the physics bounds lag a frame)
            Vector3 centre = box != null ? box.transform.TransformPoint(box.center) : styles.shield.position;
            TurnArm(lUpper, centre, aim, w);
        }

        void AimSword(Vector3 aim, float w)
        {
            if (rUpper == null || rHand == null || self.weapon == null) return;
            Vector3 b0, b1;
            if (!self.weapon.GetSegment(0, out b0, out b1)) return;
            TurnArm(rUpper, (b0 + b1) * 0.5f, aim, w);
            // then the blade across the cut: upright against a blow from the side, level against one from above
            if (!self.weapon.GetSegment(0, out b0, out b1)) return;
            Vector3 blade = (b1 - b0).normalized;
            Vector3 fromAbove = Vector3.ProjectOnPlane(transform.right, Vector3.up).normalized;
            if (Vector3.Dot(fromAbove, blade) < 0f) fromAbove = -fromAbove;
            Vector3 l = transform.InverseTransformPoint(aim) - transform.InverseTransformPoint(chest.position);
            float high = Mathf.Clamp01((l.y - 0.12f) / 0.22f);              // how much the blow comes from above
            Vector3 upright = (Vector3.up + transform.right * Mathf.Sign(l.x) * 0.25f).normalized;
            Vector3 want = Vector3.Slerp(upright, fromAbove, high);
            Quaternion turn = Quaternion.FromToRotation(blade, want);
            float ang = Quaternion.Angle(Quaternion.identity, turn);
            if (ang > maxBladeTurn) turn = Quaternion.Slerp(Quaternion.identity, turn, maxBladeTurn / ang);
            rHand.rotation = Quaternion.Slerp(Quaternion.identity, turn, w * 0.8f) * rHand.rotation;
        }

        void Recoil(float amount)
        {
            bool shield = styles != null && styles.ShieldOnArm && styles.shield != null;
            Transform upper = shield ? lUpper : rUpper;
            if (upper == null || recoilDir.sqrMagnitude < 1e-4f) return;
            // the arm swings back along the blow, about the shoulder: the guard driven toward the body
            Vector3 hand = (shield ? anim.GetBoneTransform(HumanBodyBones.LeftHand) : rHand).position;
            Vector3 from = hand - upper.position;
            Vector3 to = from + recoilDir.normalized * from.magnitude * Mathf.Tan(maxRecoil * Mathf.Deg2Rad);
            Quaternion full = Quaternion.FromToRotation(from, to);
            upper.rotation = Quaternion.Slerp(Quaternion.identity, full, amount) * upper.rotation;
        }

        void TurnArm(Transform upper, Vector3 carried, Vector3 aim, float w)
        {
            Vector3 s = upper.position;
            Quaternion full = Quaternion.FromToRotation(carried - s, aim - s);
            float ang = Quaternion.Angle(Quaternion.identity, full);
            if (ang > maxArmTurn) full = Quaternion.Slerp(Quaternion.identity, full, maxArmTurn / ang);
            upper.rotation = Quaternion.Slerp(Quaternion.identity, full, w) * upper.rotation;
        }
    }
}
