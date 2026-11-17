using System;
using System.Collections;
using System.Linq;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// The knight's three ways to fight, each its own move set, guard and carry of the gear: sword and shield (shield on
    /// the left forearm), sword alone (shield slung on the back, the left arm free) and two swords (shield on the back,
    /// the second sword drawn by the left hand from the right hip). Changing style is done with the left arm on the
    /// OffHand layer, never by teleporting gear: the shield goes over the left shoulder onto the back (and comes back
    /// the same way), the second sword is drawn and sheathed by a mirrored draw. Each object changes parent at the frame
    /// the hand reaches it, with a short blend so nothing pops. Only from a free stance; a blow or a dodge that cuts the
    /// move short before the hand-over leaves the gear where it was.
    /// </summary>
    [DefaultExecutionOrder(206)]   // after WeaponHolster (205), before the blade solvers (210)
    public sealed class FightingStyles : MonoBehaviour
    {
        public MoveSet swordShield, sword, dualSwords;
        public WeaponStyle startStyle = WeaponStyle.SwordShield;

        [Header("Shield")]
        public Transform shield;
        public Transform shieldArmParent, shieldBackParent;
        public Vector3 shieldArmPos, shieldBackPos;
        public Quaternion shieldArmRot = Quaternion.identity, shieldBackRot = Quaternion.identity;
        [Tooltip("Over-shoulder reach (OffHand layer): its length (s) and the normalized time the hand is at the shoulder.")]
        public float shieldReachLength = 1.4f;
        [Range(0f, 1f)] public float shieldSwapAt = 0.42f;

        [Header("Second sword")]
        public Transform offSword;
        public Transform offSocket, offSheath;
        public Vector3 offSheathPos;
        public Quaternion offSheathRot = Quaternion.identity;
        public WeaponSelfCollision offSolver;
        public float offDrawLength = 1.2f, offSheatheLength = 1.4f;

        [Tooltip("Seconds an object takes from where the hand met it to its seat.")]
        public float handOverBlend = 0.18f;
        /// <summary>The two-sword style is parked until its captured idle / guard are cleaned up: the player cannot
        /// switch to it (its moves, clips and gear stay built; tests can still force it).</summary>
        public bool dualSwordsEnabled = false;

        public WeaponStyle Style { get; private set; }
        public bool Busy { get { return routine != null; } }
        public bool ShieldOnArm { get { return shield != null && shield.parent == shieldArmParent; } }
        public bool OffInHand { get { return offSword != null && offSocket != null && offSword.parent == offSocket; } }
        public event Action<WeaponStyle> StyleChanged;

        public const string S_ShieldToBack = "ShieldToBack", S_ShieldFromBack = "ShieldFromBack";
        public const string S_OffDraw = "OffDraw", S_OffSheathe = "OffSheathe";

        Combatant self;
        Animator anim;
        WeaponHolster holster;
        ShieldGrip shieldGrip;
        GKInput input;
        int offLayer = -1, blockLayer = -1;
        Coroutine routine;
        Vector3 offHandPos; Quaternion offHandRot;

        // one hand-over at a time
        Transform blendObj, blendParent;
        Vector3 blendFromPos, blendLocalPos;
        Quaternion blendFromRot, blendLocalRot;
        float blendT = 1f;
        Action blendDone;

        public MoveSet SetFor(WeaponStyle s) { return s == WeaponStyle.Sword ? sword : s == WeaponStyle.DualSwords ? dualSwords : swordShield; }

        void Start()
        {
            self = GetComponent<Combatant>();
            anim = GetComponent<Animator>();
            holster = GetComponent<WeaponHolster>();
            shieldGrip = GetComponent<ShieldGrip>();
            input = GetComponent<GKInput>();
            if (anim != null)
            {
                offLayer = anim.GetLayerIndex(GK.LayerOffHand);
                blockLayer = anim.GetLayerIndex(GK.LayerBlock);
            }
            if (offSword != null && offSocket != null)
            {
                // the prefab holds the second sword in the left hand: that is its grip
                offHandPos = offSword.localPosition; offHandRot = offSword.localRotation;
                if (offSolver != null) { offSolver.Init(); offSolver.enabled = false; }
                if (offSheath != null) Seat(offSword, offSheath, offSheathPos, offSheathRot);
            }
            if (holster != null)
            {
                holster.Drawn += OnMainDrawn;
                holster.SheatheRequested += OnSheatheRequested;
            }
            // the shield arm makes way for its own blade (the kite shield is longer than the captures' round one)
            var anim0 = GetComponent<Animator>();
            if (shield != null && self != null && self.weapon != null && anim0 != null && anim0.isHuman && GetComponent<ShieldYield>() == null)
            {
                var plate = shield.GetComponent<Collider>();
                var arm = anim0.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                if (plate != null && arm != null)
                    gameObject.AddComponent<ShieldYield>().Init(arm, self.weapon.bladeBase, self.weapon.bladeTip, plate);
            }
            // the motion capture's tremor filtered out of the arms and torso
            if (anim0 != null && anim0.isHuman && GetComponent<TremorFilter>() == null) gameObject.AddComponent<TremorFilter>();
            // a cut meets a foe above or below (slopes, a rise): the spine bends toward him during the swing
            if (anim0 != null && anim0.isHuman && GetComponent<SwingAim>() == null) gameObject.AddComponent<SwingAim>();
            // the L2 -> L3 join of the shield chain is bridged (the clips do not meet; Team Lead's option b)
            if (anim0 != null && anim0.isHuman && GetComponent<ChainBridge>() == null && GetComponent<GKInput>() != null) gameObject.AddComponent<ChainBridge>();
            // the guard meets each blow by the side it comes from (shield or sword)
            if (self != null && self.team == Team.Player && anim0 != null && anim0.isHuman && GetComponent<GuardAim>() == null)
                gameObject.AddComponent<GuardAim>();
            // the two-sword style parked: its second sword and the scabbard on the right hip are not carried at all (the
            // prefab stores them switched off; enabling the style brings them back)
            if (offSheath != null) offSheath.gameObject.SetActive(dualSwordsEnabled);
            if (offSword != null) offSword.gameObject.SetActive(dualSwordsEnabled);
            Apply(startStyle, true);
        }

        void OnDestroy()
        {
            if (holster != null) { holster.Drawn -= OnMainDrawn; holster.SheatheRequested -= OnSheatheRequested; }
        }

        static readonly int P_Dual = Animator.StringToHash("Dual");
        float dual;

        void Update()
        {
            if (input != null && input.StylePressed > 0) Select((WeaponStyle)(input.StylePressed - 1));
            // the two-sword stance (square to the front) blends in on the locomotion tree
            if (anim != null)
            {
                dual = Mathf.MoveTowards(dual, Style == WeaponStyle.DualSwords ? 1f : 0f, Time.deltaTime * 2.5f);
                anim.SetFloat(P_Dual, dual);
            }
            if (holster != null) holster.holdSheathe = OffInHand || Busy;
        }

        /// <summary>Changes style (player input, AI, scripts). False when it cannot start now.</summary>
        public bool Select(WeaponStyle target)
        {
            if (target == Style || Busy || self == null || self.IsDead || SetFor(target) == null) return false;
            if (target == WeaponStyle.DualSwords && !dualSwordsEnabled) return false;
            if (self.CurrentState != Combatant.State.Free || (holster != null && holster.IsBusy)) return false;
            routine = StartCoroutine(Switch(target));
            return true;
        }

        /// <summary>How long a change to <paramref name="target"/> takes from here (the arm moves it needs, s); 0 when none.</summary>
        public float SwitchDuration(WeaponStyle target)
        {
            if (target == Style) return 0f;
            float t = 0f;
            if (OffInHand && target != WeaponStyle.DualSwords) t += offSheatheLength;
            if (ShieldOnArm && target != WeaponStyle.SwordShield) t += shieldReachLength;
            if (target == WeaponStyle.SwordShield && !ShieldOnArm) t += shieldReachLength;
            if (target == WeaponStyle.DualSwords && holster != null && holster.IsDrawn && !OffInHand) t += offDrawLength;
            return t;
        }

        /// <summary>Puts the gear straight where the style wants it, without the arm moves (tests, loading a save).</summary>
        public void ForceStyle(WeaponStyle target)
        {
            if (routine != null) { StopCoroutine(routine); routine = null; }
            FinishBlend();
            if (self != null) self.offHandOverride = -1f;
            Apply(target, true);
            bool second = target == WeaponStyle.DualSwords && holster != null && holster.IsDrawn;
            if (offSword != null)
            {
                if (second) Seat(offSword, offSocket, offHandPos, offHandRot);
                else Seat(offSword, offSheath, offSheathPos, offSheathRot);
                if (offSolver != null) offSolver.enabled = second;
            }
        }

        IEnumerator Switch(WeaponStyle target)
        {
            bool ok = true;
            if (OffInHand && target != WeaponStyle.DualSwords) yield return Arm(S_OffSheathe, offSheatheLength, Sheathe01(), SheatheOff, r => ok = r);
            if (ok && ShieldOnArm && target != WeaponStyle.SwordShield) yield return Arm(S_ShieldToBack, shieldReachLength, shieldSwapAt, ShieldToBack, r => ok = r);
            if (ok)
            {
                Apply(target, false);
                if (target == WeaponStyle.SwordShield && !ShieldOnArm) yield return Arm(S_ShieldFromBack, shieldReachLength, shieldSwapAt, ShieldToArm, r => ok = r);
                if (ok && target == WeaponStyle.DualSwords && holster != null && holster.IsDrawn && !OffInHand)
                    yield return Arm(S_OffDraw, offDrawLength, Draw01(), DrawOff, r => ok = r);
            }
            routine = null;
        }

        float Draw01() { return self.moves != null ? self.moves.drawGrab : 0.35f; }
        float Sheathe01() { return self.moves != null ? self.moves.sheatheRelease : 0.65f; }

        void OnMainDrawn()
        {
            if (Style == WeaponStyle.DualSwords && !OffInHand && !Busy && offSword != null)
                routine = StartCoroutine(Single(S_OffDraw, offDrawLength, Draw01(), DrawOff));
        }

        void OnSheatheRequested()
        {
            if (!OffInHand || Busy) return;
            routine = StartCoroutine(SheatheBoth());
        }

        IEnumerator SheatheBoth()
        {
            bool ok = true;
            yield return Arm(S_OffSheathe, offSheatheLength, Sheathe01(), SheatheOff, r => ok = r);
            routine = null;
            if (ok && holster != null) { holster.holdSheathe = false; holster.Sheathe(); }
        }

        IEnumerator Single(string state, float length, float swapAt, Action swap)
        {
            yield return Arm(state, length, swapAt, swap, r => { });
            routine = null;
        }

        /// <summary>Plays a left-arm move on the OffHand layer and hands the object over at <paramref name="swapAt"/>.
        /// Reports false when something cut it short before the hand-over (nothing moved).</summary>
        IEnumerator Arm(string state, float length, float swapAt, Action swap, Action<bool> result)
        {
            if (anim == null || offLayer < 0) { swap(); result(true); yield break; }
            self.offHandOverride = 1f;
            anim.CrossFadeInFixedTime(state, 0.15f, offLayer, 0f);
            float t = 0f;
            bool swapped = false, cut = false;
            while (t < length * 0.95f)
            {
                yield return null;
                // a hit, a dodge or a raised guard cuts the move short (the player chose to fight instead)
                if (self.IsDead || (self.CurrentState != Combatant.State.Free && self.CurrentState != Combatant.State.Traversal) ||
                    (self.BlockHeld && self.team == Team.Player)) { cut = true; break; }
                t += Time.deltaTime;
                if (!swapped && t >= swapAt * length) { swap(); swapped = true; }
            }
            if (cut && swapped) FinishBlend();
            self.offHandOverride = -1f;
            if (self.moves != null) anim.CrossFadeInFixedTime(self.moves.offHandState, 0.25f, offLayer, 0f);
            result(swapped || !cut);
            if (!swapped && !cut) swap();     // never leave the gear half-way
        }

        void ShieldToBack()
        {
            if (shieldGrip != null) shieldGrip.weight = 0f;
            Hand(shield, shieldBackParent, shieldBackPos, shieldBackRot, null);
        }

        void ShieldToArm()
        {
            Hand(shield, shieldArmParent, shieldArmPos, shieldArmRot, () => { if (shieldGrip != null) shieldGrip.weight = 1f; });
        }

        void DrawOff()
        {
            Hand(offSword, offSocket, offHandPos, offHandRot, () => { if (offSolver != null) offSolver.enabled = true; });
        }

        void SheatheOff()
        {
            if (offSolver != null) offSolver.enabled = false;
            Hand(offSword, offSheath, offSheathPos, offSheathRot, null);
        }

        void Apply(WeaponStyle style, bool instant)
        {
            var m = SetFor(style);
            if (m == null || self == null) return;
            Style = style;
            self.moves = m;
            self.offHandWeightFree = m.offHandFree;
            if (anim != null)
            {
                if (offLayer >= 0) anim.CrossFadeInFixedTime(m.offHandState, instant ? 0f : 0.25f, offLayer, 0f);
                if (blockLayer >= 0) anim.CrossFadeInFixedTime(m.blockIdleState, 0f, blockLayer, 0f);
            }
            if (instant && shield != null)
            {
                bool onArm = style == WeaponStyle.SwordShield;
                if (onArm) Seat(shield, shieldArmParent, shieldArmPos, shieldArmRot);
                else Seat(shield, shieldBackParent, shieldBackPos, shieldBackRot);
                if (shieldGrip != null) shieldGrip.weight = onArm ? 1f : 0f;
            }
            if (StyleChanged != null) StyleChanged(style);
        }

        static void Seat(Transform t, Transform parent, Vector3 pos, Quaternion rot)
        {
            if (t == null || parent == null) return;
            t.SetParent(parent, true);     // keeps the world scale: the forearm and the spine scale differently
            t.localPosition = pos; t.localRotation = rot;
        }

        void Hand(Transform obj, Transform parent, Vector3 pos, Quaternion rot, Action done)
        {
            if (obj == null || parent == null) return;
            FinishBlend();
            blendFromPos = obj.position; blendFromRot = obj.rotation;
            obj.SetParent(parent, true);
            blendObj = obj; blendParent = parent; blendLocalPos = pos; blendLocalRot = rot; blendDone = done;
            blendT = 0f;
        }

        void FinishBlend()
        {
            if (blendObj == null) return;
            blendObj.localPosition = blendLocalPos; blendObj.localRotation = blendLocalRot;
            blendObj = null; blendT = 1f;
            var d = blendDone; blendDone = null;
            if (d != null) d();
        }

        // ---- the slung shield gives way to the arms: it hangs on a strap, so an elbow or a hand that swings into it
        // pushes it out, swinging about its top edge, and it settles back
        float swayAngle;
        Vector3 swayAxisLocal = Vector3.right;
        Transform[] limbs;

        void SwayBackShield()
        {
            if (shield == null || ShieldOnArm || shield.parent != shieldBackParent || blendObj == shield) { swayAngle = 0f; return; }
            var box = shield.GetComponent<BoxCollider>();
            var top = shield.Find("BashTop");
            if (box == null || anim == null) return;
            if (limbs == null)
                limbs = new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm,
                                HumanBodyBones.LeftMiddleProximal, HumanBodyBones.RightMiddleProximal }     // the knuckles reach furthest
                        .Select(b => anim.GetBoneTransform(b)).Where(t => t != null).ToArray();
            // the shield's own frame: thinnest axis = its normal, longest = up (toward the top / BashTop)
            Vector3 size = box.size;
            int thin = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
            int tall = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            Vector3 upL = Vector3.zero; upL[tall] = 1f;
            if (top != null && Vector3.Dot(top.localPosition - box.center, upL) < 0f) upL = -upL;
            Vector3 nL = Vector3.zero; nL[thin] = 1f;
            // seat at rest, then measure
            shield.localPosition = shieldBackPos; shield.localRotation = shieldBackRot;
            Vector3 n = shield.TransformDirection(nL).normalized;
            Vector3 body = shieldBackParent.position;
            if (Vector3.Dot(n, shield.position - body) < 0f) n = -n;            // outward, away from the back
            Vector3 pivot = shield.TransformPoint(box.center + upL * (size[tall] * 0.5f));
            Vector3 c = shield.TransformPoint(box.center);
            Vector3 half = Vector3.Scale(size * 0.5f, shield.lossyScale);
            float need = 0f; Vector3 at = c;
            const float Skin = 0.045f;
            foreach (var l in limbs)
            {
                Vector3 p = l.position;
                Vector3 loc = Vector3.Scale(shield.InverseTransformPoint(p) - box.center, shield.lossyScale);
                bool within = true;
                for (int i = 0; i < 3; i++) if (i != thin && Mathf.Abs(loc[i]) > half[i] + Skin) within = false;
                if (!within) continue;
                // how far the limb's skin reaches past the plate's inner face toward the outside
                float inner = Vector3.Dot(p - c, n) + Skin + half[thin];
                if (inner > 0f && Vector3.Dot(p - c, n) < half[thin] + Skin && inner > need) { need = inner; at = p; }
            }
            float lever = Mathf.Max(0.1f, Vector3.Distance(pivot, Vector3.ProjectOnPlane(at - pivot, n) + pivot));
            float want = need > 0f ? Mathf.Rad2Deg * Mathf.Asin(Mathf.Clamp01(need / lever)) : 0f;
            float dt = Time.deltaTime;
            swayAngle = want > swayAngle ? want : Mathf.MoveTowards(swayAngle, want, dt * 40f);
            if (swayAngle <= 0.01f) return;
            Vector3 axis = Vector3.Cross(shield.TransformDirection(upL), n).normalized;   // the top edge
            shield.RotateAround(pivot, axis, -swayAngle);
            if (Vector3.Dot(shield.TransformPoint(box.center) - c, n) < 0f) { shield.localPosition = shieldBackPos; shield.localRotation = shieldBackRot; shield.RotateAround(pivot, axis, swayAngle); }
            // an arm right under the strap (little lever) is not cleared by the swing: the strap gives, the plate is
            // pushed out the rest of the way
            float rest = 0f;
            Vector3 cNow = shield.TransformPoint(box.center), nNow = shield.TransformDirection(nL).normalized;
            if (Vector3.Dot(nNow, n) < 0f) nNow = -nNow;
            foreach (var l in limbs)
            {
                Vector3 loc = Vector3.Scale(shield.InverseTransformPoint(l.position) - box.center, shield.lossyScale);
                bool within = true;
                for (int i = 0; i < 3; i++) if (i != thin && Mathf.Abs(loc[i]) > half[i] + Skin) within = false;
                if (!within) continue;
                float d = Vector3.Dot(l.position - cNow, nNow);
                if (d < half[thin] + Skin) rest = Mathf.Max(rest, d + Skin + half[thin]);
            }
            if (rest > 0f) shield.position += nNow * Mathf.Min(rest, 0.08f);
        }

        void LateUpdate()
        {
            SwayBackShield();
            if (blendObj == null) return;
            blendT = Mathf.Min(1f, blendT + Time.deltaTime / Mathf.Max(0.01f, handOverBlend));
            float k = blendT * blendT * (3f - 2f * blendT);
            blendObj.position = Vector3.Lerp(blendFromPos, blendParent.TransformPoint(blendLocalPos), k);
            blendObj.rotation = Quaternion.Slerp(blendFromRot, blendParent.rotation * blendLocalRot, k);
            if (blendT >= 1f) FinishBlend();
        }
    }
}
