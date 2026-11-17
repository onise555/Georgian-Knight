using System;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Draw / sheathe for the sword. The animation plays on the upper-body layer (so the knight can keep walking);
    /// the sword is handed between the scabbard socket and the hand at the frame where the hand actually meets the
    /// hilt (measured per clip by the editor), with a short blend so the hand-over never pops. While sheathed the
    /// locomotion switches to the unarmed set and the weapon-collision solver is off.
    /// </summary>
    [DefaultExecutionOrder(205)]   // after ScabbardMotion (200), before WeaponSelfCollision (210)
    public sealed class WeaponHolster : MonoBehaviour
    {
        public enum HolsterState { Drawn, Sheathed, Drawing, Sheathing }

        public Transform weapon;            // sword root
        public Transform hand;              // right hand bone (the sword's in-hand parent)
        public Transform sheath;            // ScabbardPivot (+Z toward the chape)
        public bool startSheathed = true;
        public float handOverBlend = 0.14f;
        public float armedBlendSpeed = 5f;
        [Tooltip("How far the guard sits above the throat when sheathed (m).")]
        public float throatOffset = 0.012f;

        public HolsterState State { get; private set; }
        public bool IsDrawn { get { return State == HolsterState.Drawn; } }
        public bool IsBusy { get { return State == HolsterState.Drawing || State == HolsterState.Sheathing; } }

        public event Action Drawn, Sheathed, DrawStarted, SheatheStarted;
        /// <summary>While set, a sheathe waits: the left hand puts its own sword away first (two-sword style), then
        /// asks again. <see cref="SheatheRequested"/> tells it that someone wanted the sword sheathed.</summary>
        [NonSerialized] public bool holdSheathe;
        public event Action SheatheRequested;

        Combatant self;
        Animator anim;
        WeaponSelfCollision collision;
        int layer = -1, ubLayer = -1, fbLayer = -1;
        float fbWeight;
        Vector3 handLocalPos; Quaternion handLocalRot;           // authored grip
        Vector3 sheathLocalPos; Quaternion sheathLocalRot;       // pose inside the scabbard
        float actionTime, actionLength, switchAt;
        bool switched;
        float blendT = 1f;                                       // 0..1 hand-over blend progress
        Vector3 blendFromPos; Quaternion blendFromRot;
        float armed, layerWeight;
        bool initialized;

        void Awake()
        {
            self = GetComponent<Combatant>();
            anim = GetComponent<Animator>();
            collision = GetComponent<WeaponSelfCollision>();
        }

        /// <summary>Initializes immediately (tools / edit-mode previews); normally called by Start.</summary>
        public void InitializeNow() { if (!initialized) Start(); }

        void Start()
        {
            if (initialized) return;
            if (self == null) Awake();
            if (weapon == null || hand == null || sheath == null) { enabled = false; return; }
            ubLayer = anim != null ? anim.GetLayerIndex(GK.LayerUpperBody) : -1;
            fbLayer = anim != null ? anim.GetLayerIndex(GK.LayerFullBodyAction) : -1;
            layer = ubLayer;
            if (collision != null) collision.Init();               // capture the grip while the sword is still in the hand
            handLocalPos = weapon.localPosition;
            handLocalRot = weapon.localRotation;
            ComputeSheathPose();
            initialized = true;
            if (startSheathed) SnapSheathed(); else SnapDrawn();
        }

        /// <summary>Sword pose in scabbard space from the sword's SheathRef frame (guard point, +Z blade, +Y blade
        /// width): the blade goes down the scabbard axis, its width matches the scabbard's, guard just above the throat.</summary>
        void ComputeSheathPose()
        {
            Transform sr = weapon.Find("SheathRef");
            if (sr == null) { sheathLocalPos = Vector3.zero; sheathLocalRot = Quaternion.identity; Debug.LogWarning("[Holster] sword has no SheathRef"); return; }
            float s = weapon.lossyScale.x;
            Quaternion rotW = sheath.rotation * Quaternion.Inverse(sr.localRotation);
            Vector3 guardW = sheath.position - sheath.forward * throatOffset;
            Vector3 posW = guardW - rotW * (sr.localPosition * s);
            sheathLocalRot = Quaternion.Inverse(sheath.rotation) * rotW;
            sheathLocalPos = sheath.InverseTransformPoint(posW);
        }

        void SnapSheathed()
        {
            weapon.SetParent(sheath, true);
            weapon.localPosition = sheathLocalPos;
            weapon.localRotation = sheathLocalRot;
            State = HolsterState.Sheathed;
            armed = 0f;
            if (collision != null) collision.enabled = false;
            if (anim != null) anim.SetFloat(GK.P_Armed, 0f);
        }

        void SnapDrawn()
        {
            weapon.SetParent(hand, true);
            weapon.localPosition = handLocalPos;
            weapon.localRotation = handLocalRot;
            State = HolsterState.Drawn;
            armed = 1f;
            if (collision != null) collision.enabled = true;
            if (anim != null) anim.SetFloat(GK.P_Armed, 1f);
        }

        public bool Draw()
        {
            if (!initialized || State != HolsterState.Sheathed || self == null || self.IsDead) return false;
            if (self.CurrentState != Combatant.State.Free) return false;
            var clip = self.moves != null ? self.moves.drawSword : null;
            if (clip == null || !clip.IsValid || layer < 0) { SnapDrawn(); if (Drawn != null) Drawn(); return true; }
            Begin(clip, self.moves.drawGrab, HolsterState.Drawing, GK.S_UBDraw);
            if (DrawStarted != null) DrawStarted();
            return true;
        }

        public bool Sheathe()
        {
            if (!initialized || State != HolsterState.Drawn || self == null || self.IsDead) return false;
            if (self.CurrentState != Combatant.State.Free || self.BlockHeld) return false;
            if (holdSheathe) { if (SheatheRequested != null) SheatheRequested(); return false; }
            var clip = self.moves != null ? self.moves.sheathSword : null;
            if (clip == null || !clip.IsValid || layer < 0) { SnapSheathed(); if (Sheathed != null) Sheathed(); return true; }
            Begin(clip, self.moves.sheatheRelease, HolsterState.Sheathing, GK.S_UBSheathe);
            // the solver keeps the blade out of the body all the way to the scabbard: it is off only once the sword
            // is handed to the scabbard (the sheathing path swept 16 cm through the coat with it off)
            if (SheatheStarted != null) SheatheStarted();
            return true;
        }

        public void Toggle() { if (State == HolsterState.Sheathed) Draw(); else if (State == HolsterState.Drawn) Sheathe(); }

        void Begin(ActionClip clip, float switchNormalized, HolsterState st, string animState)
        {
            State = st;
            actionTime = 0f;
            actionLength = clip.Duration;
            switchAt = Mathf.Clamp01(switchNormalized) * actionLength;
            switched = false;
            // standing still: full-body version (hips and feet exactly as authored, so the hand meets the hilt);
            // on the move: upper body only, the legs keep walking
            bool still = self.Motor == null || GK.Flat(self.Motor.Velocity).magnitude < 0.45f;
            useFullBody = still && fbLayer >= 0;
            if (useFullBody)
            {
                layer = fbLayer;
                anim.CrossFadeInFixedTime(st == HolsterState.Drawing ? GK.S_FBDraw : GK.S_FBSheathe, 0.12f, fbLayer, 0f);
            }
            else
            {
                layer = ubLayer;
                anim.CrossFadeInFixedTime(animState, 0.12f, ubLayer, 0f);
            }
        }

        bool useFullBody;

        /// <summary>Instant hand-over without animation (cutscenes, deaths, scripted moments).</summary>
        public void ForceState(bool drawn) { if (!initialized) return; if (drawn) SnapDrawn(); else SnapSheathed(); }

        void LateUpdate()
        {
            if (!initialized) return;
            float dt = Time.deltaTime;

            if (State == HolsterState.Drawing || State == HolsterState.Sheathing)
            {
                actionTime += dt;
                // an interrupting hit / dodge / jump cancels the move: before the hand reaches the hilt (or lets go of
                // it) the sword simply stays where it was; after that the hand-over finishes where the hand already is
                bool interrupted = self.CurrentState != Combatant.State.Free || self.IsDead;
                if (interrupted && !switched)
                {
                    State = State == HolsterState.Drawing ? HolsterState.Sheathed : HolsterState.Drawn;
                    if (anim != null && ubLayer >= 0) anim.CrossFadeInFixedTime(GK.S_UBEmpty, 0.15f, ubLayer, 0f);
                    if (anim != null && fbLayer >= 0) anim.CrossFadeInFixedTime(GK.S_FBEmpty, 0.15f, fbLayer, 0f);
                    if (State == HolsterState.Drawn && collision != null) collision.enabled = true;
                }
                else if (!switched && actionTime >= switchAt)
                {
                    switched = true;
                    if (State == HolsterState.Drawing)
                    {
                        weapon.SetParent(hand, true);
                        // from the hand-over on the blade is kept out of the body, on top of the blend's pose
                        if (collision != null) { collision.followCurrentPose = true; collision.enabled = true; }
                    }
                    else
                    {
                        // start from where the sword was last seen (the solver ran after us last frame and bent the
                        // wrist; this frame's pose is not corrected yet: a 32 cm jump of the tip, measured)
                        if (collision != null && collision.enabled && collision.HasLastWorld) weapon.SetPositionAndRotation(collision.LastWorldPos, collision.LastWorldRot);
                        weapon.SetParent(sheath, true);
                        // the wrist the solver bent eases back over 0.12 s (switching it off at once snapped the hand)
                        if (collision != null) collision.ReleaseWrist(0.12f);
                    }
                    // the blend runs in the new holder's space: the sword travels with the hand (or the scabbard) at once
                    // and only eases its small offset from the switch frame away; blended in world space it stayed
                    // where the switch happened while the drawing hand swept on, 23 cm out of the palm
                    blendFromPos = weapon.localPosition; blendFromRot = weapon.localRotation;
                    blendT = 0f;
                }
                if ((State == HolsterState.Drawing || State == HolsterState.Sheathing) && (actionTime >= actionLength * 0.92f || interrupted))
                {
                    bool drew = State == HolsterState.Drawing;
                    State = drew ? HolsterState.Drawn : HolsterState.Sheathed;
                    if (anim != null && ubLayer >= 0) anim.CrossFadeInFixedTime(GK.S_UBEmpty, 0.2f, ubLayer, 0f);
                    if (anim != null && fbLayer >= 0) anim.CrossFadeInFixedTime(GK.S_FBEmpty, 0.2f, fbLayer, 0f);
                    if (drew && collision != null) collision.enabled = true;
                    if (drew) { if (Drawn != null) Drawn(); } else { if (Sheathed != null) Sheathed(); }
                }
            }

            // hand-over blend: from the pose at the switch frame to the target socket pose (in the holder's space)
            if (blendT < 1f)
            {
                blendT = Mathf.Min(1f, blendT + dt / Mathf.Max(0.01f, handOverBlend));
                float k = blendT * blendT * (3f - 2f * blendT);
                bool inHand = weapon.parent == hand;
                Vector3 targetPos = inHand ? handLocalPos : sheathLocalPos;
                Quaternion targetRot = inHand ? handLocalRot : sheathLocalRot;
                // the grip seats in the palm in the first third; the blade's angle eases in over the whole blend
                float kp = Mathf.Min(1f, blendT * 3f); kp = kp * kp * (3f - 2f * kp);
                weapon.localRotation = Quaternion.Slerp(blendFromRot, targetRot, k);
                if (inHand && collision != null && collision.grip != null)
                {
                    // hold the grip point itself where it belongs, whatever the blade's angle on the way
                    Vector3 gripInWeapon = weapon.InverseTransformPoint(collision.grip.position);
                    Vector3 gripTarget = Vector3.Lerp(blendFromPos + blendFromRot * Vector3.Scale(gripInWeapon, weapon.localScale),
                                                      targetPos + targetRot * Vector3.Scale(gripInWeapon, weapon.localScale), kp);
                    weapon.localPosition = gripTarget - weapon.localRotation * Vector3.Scale(gripInWeapon, weapon.localScale);
                }
                else weapon.localPosition = Vector3.Lerp(blendFromPos, targetPos, kp);
                if (blendT >= 1f)
                {
                    // seated in the hand: from here the solver keeps the blade out of the body, the draw included
                    if (inHand && collision != null) { collision.enabled = true; collision.followCurrentPose = false; }
                    if (inHand) { weapon.localPosition = handLocalPos; weapon.localRotation = handLocalRot; }
                    else { weapon.localPosition = sheathLocalPos; weapon.localRotation = sheathLocalRot; }
                }
            }

            // upper-body layer weight and armed/unarmed locomotion
            if (anim != null)
            {
                bool acting = State == HolsterState.Drawing || State == HolsterState.Sheathing;
                // the player starting to move mid-action hands the legs back (full body -> upper body weight blend)
                if (acting && useFullBody && self.Motor != null && GK.Flat(self.Motor.Velocity).magnitude > 0.8f) useFullBody = false;
                layerWeight = Mathf.MoveTowards(layerWeight, acting ? 1f : 0f, dt * 6f);
                fbWeight = Mathf.MoveTowards(fbWeight, acting && useFullBody ? 1f : 0f, dt * 6f);
                if (ubLayer >= 0) anim.SetLayerWeight(ubLayer, layerWeight);
                if (fbLayer >= 0) anim.SetLayerWeight(fbLayer, fbWeight);
                float armedTarget = (State == HolsterState.Drawn || (State == HolsterState.Drawing && switched)) ? 1f :
                                    (State == HolsterState.Sheathing && !switched) ? 1f : 0f;
                armed = Mathf.MoveTowards(armed, armedTarget, dt * armedBlendSpeed);
                anim.SetFloat(GK.P_Armed, armed);
            }
        }
    }
}
