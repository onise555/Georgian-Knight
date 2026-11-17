using System.Collections.Generic;
using GeorgianKnight.Core;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Player brain: camera-relative locomotion, lock-on strafing and a short input buffer so
    /// combos chain the way players expect (press early, it still lands on the frame it is allowed).
    /// </summary>
    [RequireComponent(typeof(Combatant), typeof(GKInput))]
    public class PlayerController : MonoBehaviour
    {
        [Header("Speeds (m/s)")]
        public float walkSpeed = 1.6f;
        public float runSpeed = 3.9f;
        public float sprintSpeed = 6.3f;
        public float strafeSpeed = 2.4f;
        public float blockSpeed = 1.3f;
        public float acceleration = 11f;
        public float deceleration = 14f;
        public float turnSharpness = 13f;

        [Header("Input buffer")]
        public float bufferTime = 0.38f;

        public Transform cameraTransform;

        public float CurrentSpeed { get; private set; }
        public bool IsSprinting { get; private set; }
        /// <summary>False while a menu, dialogue, cutscene or loading screen owns the input, and for a short grace
        /// period after returning to gameplay (so the click that closed a menu never becomes an attack).</summary>
        public bool InputEnabled
        {
            get { return inputAllowed && inputLocks.Count == 0 && (states == null || states.InGameplay) && Time.unscaledTime >= inputGraceUntil; }
            set { inputAllowed = value; }
        }

        /// <summary>
        /// Blocks player input until the same owner calls <see cref="RemoveInputLock"/> (cutscenes, scripted moments).
        /// Locks are counted per owner, so independent systems never restore each other's state. Menus, dialogue and
        /// loading need no lock: <see cref="InputEnabled"/> is already false outside gameplay modes.
        /// </summary>
        public void AddInputLock(object owner) { if (owner != null && !inputLocks.Contains(owner)) inputLocks.Add(owner); }
        public void RemoveInputLock(object owner) { if (owner != null && inputLocks.Remove(owner) && inputLocks.Count == 0) inputGraceUntil = Time.unscaledTime + 0.2f; }
        public bool HasInputLock(object owner) { return inputLocks.Contains(owner); }
        readonly List<object> inputLocks = new List<object>(2);

        IGameStateMachine states;
        Subscriptions subs;
        bool inputAllowed = true;
        float inputGraceUntil;

        enum Cmd { None, Light, Heavy, Dodge, Kick, WarCry, Jump }

        [Header("Jump")]
        [Tooltip("How quickly the stick can change the momentum in the air (m/s per second).")]
        public float airControl = 4f;
        [Tooltip("A jump at a locked foe lands this far from him (m): the carry toward him is trimmed in the air. Untrimmed, "
                 + "a running jump from 2.5-4.5 m landed 0.7-1.2 m off, on top of him, and the first cut came out crowded.")]
        public float jumpLandAtFoe = 1.6f;
        [Tooltip("The pad's War Cry chord (RB + X) calls a kick back into the shout within this many seconds of its start.")]
        public float warCryChordWindow = 0.15f;

        Combatant self;
        GKInput input;
        LockOnSystem lockOn;
        SkillSystem skills;
        Animator anim;
        CharacterMotor motor;

        Cmd buffered;
        float bufferedAt;
        Vector3 bufferedDir;
        Vector3 planarVelocity;
        Combatant.State lastState;

        void Awake()
        {
            self = GetComponent<Combatant>();
            input = GetComponent<GKInput>();
            lockOn = GetComponent<LockOnSystem>();
            skills = GetComponent<SkillSystem>();
            motor = GetComponent<CharacterMotor>();
            // the weapon slots (S1, C18): every player has them, built prefabs included (the M1 default loadout when empty)
            if (GetComponent<Equipment>() == null) gameObject.AddComponent<Equipment>();
            anim = self.Anim != null ? self.Anim : GetComponent<Animator>();
            Services.TryGet(out states);
            subs = new Subscriptions().On<GameModeChanged>(OnModeChanged);
        }

        void OnDestroy() { if (subs != null) subs.Dispose(); }

        [Header("Auto holster")]
        public bool autoHolster = true;
        public float drawWhenEnemyWithin = 14f;
        public float sheatheWhenClearOf = 24f;
        public float sheatheAfterCalm = 6f;
        float calmSince = -1f, holsterManualUntil;

        /// <summary>Draws when a living enemy comes close, sheathes after a calm period with no enemy nearby.
        /// A manual holster press suspends the automation for a few seconds so it never fights the player.</summary>
        void UpdateAutoHolster()
        {
            var h = self.Holster;
            if (!autoHolster || h == null || h.IsBusy || Time.time < holsterManualUntil || self.CurrentState != Combatant.State.Free) return;
            float nearest = float.MaxValue;
            var all = CombatDirector.I.all;
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c == null || c == self || c.IsDead || c.team == self.team) continue;
                float d = (c.transform.position - transform.position).sqrMagnitude;
                if (d < nearest) nearest = d;
            }
            nearest = Mathf.Sqrt(nearest);
            if (!h.IsDrawn)
            {
                if (nearest <= drawWhenEnemyWithin) h.Draw();
                calmSince = -1f;
            }
            else
            {
                if (nearest > sheatheWhenClearOf && !self.BlockHeld)
                {
                    if (calmSince < 0f) calmSince = Time.time;
                    else if (Time.time - calmSince >= sheatheAfterCalm) { h.Sheathe(); calmSince = -1f; }
                }
                else calmSince = -1f;
            }
        }

        void OnModeChanged(GameModeChanged e)
        {
            bool nowGameplay = e.Current == GameMode.Exploring || e.Current == GameMode.Combat;
            bool wasGameplay = e.Previous == GameMode.Exploring || e.Previous == GameMode.Combat;
            if (nowGameplay && !wasGameplay) inputGraceUntil = Time.unscaledTime + 0.2f;
            if (!nowGameplay) { buffered = Cmd.None; self.SetBlock(false); self.SetChargeHeld(false); }
        }

        void Start()
        {
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (anim == null) anim = self.Anim;
            if (self.IsDead) { motor.SetLocomotionVelocity(Vector3.zero); return; }

            Vector3 moveDir = InputEnabled ? CameraRelative(input.Move) : Vector3.zero;
            Combatant target = lockOn != null ? lockOn.Target : null;
            self.Target = target;
            self.DesiredFacing = moveDir.sqrMagnitude > 0.01f
                ? moveDir
                : (target != null ? target.transform.position - transform.position : transform.forward);

            self.SetBlock(InputEnabled && input.BlockHeld);
            self.SetChargeHeld(InputEnabled && input.HeavyHeld);

            if (InputEnabled && !self.InTraversal)       // on a ladder the traversal owner reads the input
            {
                // the pad's War Cry chord (CONTROLS §1a): X while RB is held. The kick already fired on RB's press (it never
                // waits for the chord); inside its first 0.15 s, before contact, it is called back into the shout
                bool chord = input.LightPressed && input.KickHeldPad;
                if (chord)
                {
                    bool kicking = self.CurrentState == Combatant.State.Attacking && self.CurrentAttack != null && self.CurrentAttack.kind == AttackKind.Kick;
                    if (skills != null && skills.CanWarCry && (!kicking || self.CancelKickWindup(warCryChordWindow))) Buffer(Cmd.WarCry, moveDir);
                }
                else if (input.LightPressed) Buffer(Cmd.Light, moveDir);
                if (input.HeavyPressed) Buffer(Cmd.Heavy, moveDir);
                if (input.DodgePressed) Buffer(Cmd.Dodge, moveDir);
                if (input.KickPressed) Buffer(Cmd.Kick, moveDir);
                if (input.WarCryPressed) Buffer(Cmd.WarCry, moveDir);
                if (input.JumpPressed) Buffer(Cmd.Jump, moveDir);
                if (input.HolsterPressed && self.Holster != null) { self.Holster.Toggle(); holsterManualUntil = Time.time + 8f; }
                if (input.BlockHeld && !self.WeaponReady && self.Holster != null && !self.Holster.IsBusy) self.Holster.Draw();
            }
            UpdateAutoHolster();
            ConsumeBuffer();

            if (self.CurrentState != lastState)
            {
                if (self.CurrentState == Combatant.State.Free) planarVelocity = GK.Flat(motor.Velocity);
                lastState = self.CurrentState;
            }

            if (self.CurrentState == Combatant.State.Free) Locomotion(moveDir, target, dt);
            else if (self.CurrentState == Combatant.State.Jumping)
            {
                // keep the take-off momentum, steer it a little
                float cap = Mathf.Max(planarVelocity.magnitude, moveDir.sqrMagnitude > 0.01f ? walkSpeed : 0f);
                Vector3 desired = moveDir.sqrMagnitude > 0.01f ? moveDir.normalized * cap : planarVelocity;
                planarVelocity = Vector3.MoveTowards(planarVelocity, desired, airControl * dt);
                TrimJumpCarry(target);
                motor.SetLocomotionVelocity(planarVelocity);
                if (planarVelocity.sqrMagnitude > 0.25f) motor.FaceDirection(planarVelocity, turnSharpness * 0.35f, dt);
            }
            else
            {
                planarVelocity = Vector3.zero;
                motor.SetLocomotionVelocity(Vector3.zero);
                IsSprinting = false;
                CurrentSpeed = GK.Damp(CurrentSpeed, 0f, 8f, dt);
                // a traversal owner (ladder) drives the locomotion parameters itself while it walks into position
                if (anim != null && !self.InTraversal) anim.SetFloat(GK.P_Speed, CurrentSpeed, 0.1f, dt);
            }
        }

        Vector3 CameraRelative(Vector2 v)
        {
            if (v.sqrMagnitude < 0.0025f) return Vector3.zero;
            Transform cam = cameraTransform != null ? cameraTransform : transform;
            Vector3 f = GK.Flat(cam.forward).normalized;
            Vector3 r = GK.Flat(cam.right).normalized;
            return Vector3.ClampMagnitude(f * v.y + r * v.x, 1f);
        }

        void Buffer(Cmd c, Vector3 dir)
        {
            buffered = c;
            bufferedAt = Time.time;
            bufferedDir = dir;
        }

        void ConsumeBuffer()
        {
            if (buffered == Cmd.None) return;
            // a strike pressed in the air is kept until the feet are down (it expired mid-jump: the blow never came)
            bool airStrike = self.CurrentState == Combatant.State.Jumping && (buffered == Cmd.Light || buffered == Cmd.Heavy);
            if (!airStrike && Time.time - bufferedAt > bufferTime) { buffered = Cmd.None; return; }
            bool ok = false;
            switch (buffered)
            {
                case Cmd.Light: ok = self.RequestAttack(AttackKind.Light, IsSprinting); break;
                case Cmd.Heavy: ok = self.RequestAttack(AttackKind.Heavy, false); break;
                case Cmd.Dodge:
                    Vector3 d = bufferedDir.sqrMagnitude > 0.01f ? bufferedDir : -transform.forward;
                    ok = self.RequestDodge(d);
                    break;
                case Cmd.Kick: ok = skills != null ? skills.TryKick() : self.RequestKick(); break;
                case Cmd.Jump: ok = self.RequestJump(IsSprinting || GK.Flat(motor.Velocity).magnitude > walkSpeed * 1.25f); break;
                case Cmd.WarCry: ok = skills != null && skills.TryWarCry(); if (!ok && skills != null && !skills.CanWarCry) { buffered = Cmd.None; return; } break;
            }
            if (ok) buffered = Cmd.None;
        }

        /// <summary>In the air toward a locked foe: the speed toward him is capped so that what is left of the flight
        /// ends <see cref="jumpLandAtFoe"/> from him. Sideways momentum and a jump away from him are left alone.</summary>
        void TrimJumpCarry(Combatant target)
        {
            if (target == null || target.IsDead || motor.IsGrounded) return;
            Vector3 to = GK.Flat(target.transform.position - transform.position);
            float d = to.magnitude;
            if (d < 0.01f || d > 8f) return;
            Vector3 dir = to / d;
            float along = Vector3.Dot(planarVelocity, dir);
            if (along <= 0f) return;
            // time left in the air, landing at his height
            float g = Mathf.Max(1f, -motor.gravity), vy = motor.Velocity.y;
            float h = Mathf.Max(0f, transform.position.y - target.transform.position.y);
            float tLeft = (vy + Mathf.Sqrt(Mathf.Max(0f, vy * vy + 2f * g * h))) / g;
            if (tLeft < 0.02f) return;
            float allowed = Mathf.Max(0f, (d - jumpLandAtFoe) / tLeft);
            if (along > allowed) planarVelocity -= dir * (along - allowed);
        }

        void Locomotion(Vector3 moveDir, Combatant target, float dt)
        {
            float mag = moveDir.magnitude;
            bool blocking = self.IsBlocking;
            IsSprinting = InputEnabled && input.SprintHeld && mag > 0.5f && !blocking && self.Stats.Stamina > 1f;
            bool strafe = (target != null || blocking) && !IsSprinting;

            float maxSpeed;
            if (blocking) maxSpeed = blockSpeed;
            else if (IsSprinting) maxSpeed = sprintSpeed;
            else if (strafe) maxSpeed = strafeSpeed;
            else maxSpeed = mag < 0.55f ? walkSpeed : runSpeed;
            var mods = self.Modifiers;      // encumbrance / armour weight / buffs (the blend tree follows the real speed: no foot sliding)
            if (mods != null) maxSpeed *= Mathf.Clamp(mods.MoveSpeedMultiplier, 0.3f, 1.5f);

            Vector3 desiredVel = moveDir.sqrMagnitude > 0.0001f ? moveDir.normalized * maxSpeed * Mathf.Clamp01(mag * 1.15f) : Vector3.zero;
            float rate = desiredVel.sqrMagnitude > planarVelocity.sqrMagnitude ? acceleration : deceleration;
            planarVelocity = Vector3.MoveTowards(planarVelocity, desiredVel, rate * maxSpeed * dt);
            motor.SetLocomotionVelocity(planarVelocity);
            if (IsSprinting) self.Stats.UseStamina(9f * dt * (self.Modifiers != null ? Mathf.Max(0.1f, self.Modifiers.StaminaCostMultiplier) : 1f));

            if (strafe && target != null)
            {
                motor.FaceDirection(target.transform.position - transform.position, turnSharpness, dt);
            }
            else if (strafe && cameraTransform != null)
            {
                motor.FaceDirection(cameraTransform.forward, turnSharpness * 0.6f, dt);
            }
            else if (moveDir.sqrMagnitude > 0.01f)
            {
                motor.FaceDirection(moveDir, turnSharpness, dt);
            }

            CurrentSpeed = planarVelocity.magnitude;
            if (anim != null)
            {
                anim.SetFloat(GK.P_Strafe, strafe ? 1f : 0f);
                anim.SetFloat(GK.P_Speed, CurrentSpeed, 0.08f, dt);
                Vector3 local = transform.InverseTransformDirection(planarVelocity) / Mathf.Max(0.01f, runSpeed);
                anim.SetFloat(GK.P_MoveX, local.x, 0.1f, dt);
                anim.SetFloat(GK.P_MoveY, local.z, 0.1f, dt);
            }
        }
    }
}
