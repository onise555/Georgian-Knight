using System;
using System.Collections.Generic;
using GeorgianKnight.Core;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// The combat state machine shared by the player and every enemy. Brains (PlayerController,
    /// EnemyAI) only issue requests; this class decides what is legal, drives the Animator with
    /// cross-fades, applies root motion + target warping, opens/closes the blade and resolves hits.
    /// </summary>
    [RequireComponent(typeof(CharacterMotor), typeof(CombatStats))]
    [DefaultExecutionOrder(-10)]
    public class Combatant : MonoBehaviour
    {
        public enum State { Free, Attacking, Dodging, Reacting, Staggered, Knockdown, Executing, BeingExecuted, Skill, Dead, Traversal, Jumping }

        [Header("Identity")]
        public Team team = Team.Enemy;
        [Tooltip("Localization key of the name the HUD shows (C6), e.g. enemy.seljuk_warrior.name; empty = enemy.generic.name.")]
        public string displayNameKey = "";
        [Tooltip("Content id of this character (EnemyDef / NPC id, e.g. \"looter_spear\"); reported in EnemyKilled / PlayerDied.")]
        public string characterId = "";
        [Tooltip("Content tags matched by quest objectives and encounter logic (\"looter\", \"seljuk\", \"boss\" …).")]
        public string[] tags = new string[0];
        public MoveSet moves;
        public WeaponHitbox weapon;
        [Tooltip("The second sword (left hand), for the two-sword style; null without one.")]
        public WeaponHitbox offWeapon;
        [Tooltip("Two points down the shield's face (top, point): what a shield bash hits with.")]
        public Transform[] shieldEdge;
        public float bodyRadius = 0.4f;

        [Header("Defense")]
        public bool canBlock = true;
        public bool canParry = true;
        public float parryWindow = 0.22f;
        public float blockAngle = 80f;
        public float blockStaminaFactor = 0.7f;
        public float perfectDodgeWindow = 0.2f;
        [Tooltip("Seconds an attacker stays open after being parried.")]
        public float parriedVulnerability = 1.4f;
        [Tooltip("Executions are allowed when a staggered target's health is at or below this fraction.")]
        [Range(0f, 1f)] public float executionHealthThreshold = 0.45f;

        [Header("Attack assist")]
        public float assistRadius = 5.5f;
        public float assistAngle = 75f;
        [Tooltip("Free flow (player): a light attack aimed at an enemy farther than the light swing can reach, up to this far, closes the gap with the leap attack (m).")]
        public float freeFlowRange = 6f;
        /// <summary>Seconds after an attack has fully ended in which the next press still continues its chain (a press a
        /// beat late no longer restarts the opening cut).</summary>
        public float comboGrace = 0.7f;
        AttackData lastAttack;
        float lastAttackEnded = -10f;
        Combatant freeFlowTarget;
        public float steerSharpness = 16f;
        [Header("Player: guard out of a flinch, swing tracking")]
        [Tooltip("Player only: a held guard comes up out of a light flinch after this long (s). Without it a combo's cuts "
                 + "each restarted the flinch and the knight could not raise his shield for seconds.")]
        public float guardCancelLight = 0.22f;
        [Tooltip("Player only: the same out of a heavy reaction (s). Staggers and knockdowns are never cut short.")]
        public float guardCancelHeavy = 0.45f;
        [Tooltip("Player only: after the attack's steerUntil the swing keeps turning onto its target this sharply until the "
                 + "edge opens, so a foe who side-steps during the wind-up is still met by the blade.")]
        public float trackSharpness = 7f;

        [Header("Animation")]
        public float returnFade = 0.2f;
        public float blockLayerSharpness = 16f;
        [Tooltip("Seconds the block pose takes to come up. The block itself counts from the press; at 55/s damping 60 % of the "
                 + "pose arrived in one frame, a 47 deg snap of the upper arm (Critics A6). 0.06 s = 3-4 frames at 60 fps.")]
        public float blockPoseRise = 0.06f;
        public float offHandWeightFree = 0f;
        public float offHandWeightAction = 0f;

        public Animator Anim { get; private set; }
        public CharacterMotor Motor { get; private set; }
        public CombatStats Stats { get; private set; }
        public Ragdoll Ragdoll { get; private set; }
        /// <summary>Stat-driven combat numbers (armour, crit, multipliers); null = neutral.</summary>
        public ICombatModifiers Modifiers
        {
            get { var o = modifiers as UnityEngine.Object; return o != null ? modifiers : null; }   // destroyed components read as null
        }
        ICombatModifiers modifiers;
        public WeaponHolster Holster { get; private set; }
        /// <summary>Optional inertialized transitions for action starts (see PoseInertializer).</summary>
        public PoseInertializer Inertializer { get; private set; }
        /// <summary>True when the weapon is in hand and ready (no holster, or drawn).</summary>
        public bool WeaponReady { get { return Holster == null || Holster.IsDrawn; } }

        public State CurrentState { get; private set; }
        public bool IsDead { get { return CurrentState == State.Dead; } }
        public Combatant Target { get; set; }
        /// <summary>World direction the brain wants to face (input / AI intent).</summary>
        public Vector3 DesiredFacing { get; set; }
        public bool BlockHeld { get; private set; }
        public bool IsBlocking { get { return canBlock && BlockHeld && CurrentState == State.Free && WeaponReady; } }
        public AttackData CurrentAttack { get { return CurrentState == State.Attacking ? attack : null; } }
        public float ActionProgress { get; private set; }
        public bool Invulnerable { get; private set; }
        public bool IsCharging { get { return charging; } }
        public float ChargeAmount { get { return attack != null && attack.maxChargeTime > 0f ? Mathf.Clamp01(chargeTime / attack.maxChargeTime) : 0f; } }
        public bool IsExecutable
        {
            get
            {
                return CurrentState == State.Staggered && executableUntil > Time.time &&
                       Stats.HealthNormalized <= executionHealthThreshold + 1e-3f;
            }
        }
        public bool IsOpenToRiposte { get { return CurrentState == State.Staggered && executableUntil > Time.time; } }
        public bool IsInHitWindow { get { return swingOpen; } }
        /// <summary>True while the current attack is past the point where its swing can still be reacted to.</summary>
        public bool IsWindingUp { get { return CurrentState == State.Attacking && ActionProgress < attack.hitStart; } }

        // events (audio, FX, UI and AI subscribe)
        public event Action<HitData, HitResult> HitReceived;
        public event Action<Combatant, HitData, HitResult> HitDealt;
        public event Action<AttackData> AttackStarted;
        public event Action<AttackData> SwingStarted;
        public event Action SwingEnded;
        public event Action<AttackData> Telegraphed;
        public event Action<Combatant> ParrySucceeded;
        public event Action PerfectDodge;
        public event Action<HitData> Died;
        public event Action<State> StateChanged;
        public event Action<float> ChargeChanged;
        public event Action Dodged;

        // runtime
        ActionClip action;
        AttackData attack;
        float actionDuration;
        float actionTime;
        bool actionRoot;
        bool swingOpen, swingAnnounced;
        float freezeUntil;
        float blockPressTime = -10f;
        float blockWeight;
        int blockLayer = -1, offHandLayer = -1;
        float offHandWeight = 1f;
        bool chargeHeld, charging, chargeDone;
        float chargeTime, chargeMul = 1f;
        float dodgeStart = -10f;
        bool perfectDodgeTriggered;
        Combatant warpTarget;
        /// <summary>Who the current swing is aimed at (lock-on, free flow or the soft target), null between swings.</summary>
        public Combatant SwingTarget { get { return CurrentState == State.Attacking ? warpTarget : null; } }
        float executableUntil;
        Combatant executionVictim;
        bool executionApplied;
        string lastState;
        bool toggle;
        readonly Dictionary<int, bool> stateExists = new Dictionary<int, bool>(32);
        // each state's duplicate ("<state>#2"), named once for all fighters: the concat allocated on every re-entry (GK-28)
        static readonly Dictionary<string, string> altState = new Dictionary<string, string>();
        bool registered;

        public float AttackSpeedMultiplier { get; set; }

        void Awake()
        {
            Anim = GetComponent<Animator>();
            if (Anim == null) Anim = GetComponentInChildren<Animator>();
            Motor = GetComponent<CharacterMotor>();
            Stats = GetComponent<CombatStats>();
            Ragdoll = GetComponent<Ragdoll>();
            Holster = GetComponent<WeaponHolster>();
            modifiers = GetComponent<ICombatModifiers>();
            Inertializer = GetComponent<PoseInertializer>();
            if (tags == null) tags = new string[0];
            AttackSpeedMultiplier = 1f;
            if (weapon != null)
            {
                weapon.Owner = this;
                weapon.OnContact = OnWeaponContact;
            }
            if (offWeapon != null)
            {
                offWeapon.Owner = this;
                offWeapon.OnContact = OnWeaponContact;
            }
            foreach (Hurtbox hb in GetComponentsInChildren<Hurtbox>(true)) hb.owner = this;
        }

        void Start()
        {
            if (Anim != null)
            {
                Anim.applyRootMotion = true;
                blockLayer = Anim.GetLayerIndex(GK.LayerBlock);
                offHandLayer = Anim.GetLayerIndex(GK.LayerOffHand);
            }
            CombatDirector.I.Register(this);
            registered = true;
        }

        void OnDestroy()
        {
            var director = CombatDirector.Existing;
            if (registered && director != null) director.Unregister(this);
        }

        // ------------------------------------------------------------------ requests (brains call these)

        float reactedAt = -10f;
        bool reactedHeavy;

        public void SetBlock(bool held)
        {
            // the player's held guard comes up out of a flinch once its first moment has passed (a light 0.22 s, a heavy
            // 0.45 s): the next cut of an enemy combo is met by the shield, not by another flinch
            if (held && team == Team.Player && CurrentState == State.Reacting && canBlock && WeaponReady &&
                Time.time - reactedAt >= (reactedHeavy ? guardCancelHeavy : guardCancelLight))
            {
                ReturnToFree();
                blockPressTime = -10f;                         // coming out of a flinch is not a timed parry
            }
            if (held && !BlockHeld)
            {
                blockPressTime = Time.time;
                // an instant guard: pressed while the knight is still winding up his own swing (before its edge can land)
                // or recovering after it, the swing is let go and the guard comes up at once
                if (team == Team.Player && CurrentState == State.Attacking && attack != null && !attack.hyperArmor &&
                    (ActionProgress < attack.hitStart * 0.85f || ActionProgress >= attack.dodgeCancel))
                    ReturnToFree();
            }
            BlockHeld = held;
        }

        public void SetChargeHeld(bool held) { chargeHeld = held; }

        public bool CanAttackNow
        {
            get
            {
                switch (CurrentState)
                {
                    case State.Free: return true;
                    case State.Attacking: return ActionProgress >= attack.comboWindow && !charging;
                    case State.Dodging: return ActionProgress >= (action != null ? action.cancelFrom : 0.7f);
                    // the feet are down after the take-off: a strike cuts the landing short (it waited for the whole
                    // landing clip, and the blow came late, from where the jump had carried him)
                    case State.Jumping: return jumpLaunched && Motor != null && Motor.IsGrounded && ActionProgress >= jumpTakeoff + 0.1f;
                    default: return false;
                }
            }
        }

        public bool CanDodgeNow
        {
            get
            {
                switch (CurrentState)
                {
                    case State.Free: return true;
                    case State.Attacking: return ActionProgress >= attack.dodgeCancel || ActionProgress < attack.hitStart * 0.5f;
                    case State.Dodging: return ActionProgress >= (action != null ? action.cancelFrom : 0.75f);
                    case State.Reacting: return ActionProgress >= 0.45f;
                    default: return false;
                }
            }
        }

        /// <summary>Light/heavy input from the player. Resolves combos, ripostes and executions.</summary>
        public bool RequestAttack(AttackKind kind, bool sprinting)
        {
            if (moves == null || IsDead || !CanAttackNow) return false;
            if (!WeaponReady) { if (Holster != null && !Holster.IsBusy) Holster.Draw(); return false; }

            AttackData next = null;
            if (CurrentState == State.Attacking && attack != null)
                next = kind == AttackKind.Heavy ? attack.nextHeavy : attack.NextLight;
            else if (CurrentState == State.Free && lastAttack != null && Time.time - lastAttackEnded <= comboGrace)
                next = kind == AttackKind.Heavy ? lastAttack.nextHeavy : lastAttack.NextLight;

            if (next == null && kind == AttackKind.Light)
            {
                Combatant open = FindOpenTarget();
                if (open != null)
                {
                    if (open.IsExecutable && moves.execution != null) return BeginExecution(open);
                    if (moves.riposte != null) next = moves.riposte;
                }
            }

            if (next == null)
            {
                if (kind == AttackKind.Heavy) next = moves.heavyStart;
                else next = sprinting && moves.sprintAttack != null ? moves.sprintAttack : moves.lightStart;
                // free flow: the enemy the stick points at, beyond what the light swing's warp can close, is reached
                // with one leap that lands at the strike range (Arkham / Shadow of Mordor style)
                if (kind == AttackKind.Light && team == Team.Player && next == moves.lightStart && next != null && moves.sprintAttack != null)
                {
                    Combatant far = Target != null && !Target.IsDead ? Target : CombatDirector.I.FindTarget(this, DesiredFacing, freeFlowRange, assistAngle * 0.6f);
                    if (far != null)
                    {
                        float d = GK.Flat(far.transform.position - transform.position).magnitude;
                        float lightReach = next.idealRange + far.bodyRadius * 0.5f + 1.4f;
                        if (d > lightReach && d <= freeFlowRange) { next = moves.sprintAttack; freeFlowTarget = far; }
                    }
                }
            }
            // a foe lying on the ground: a light cut passes over him (measured 21-31 cm above the body), and so do the
            // heavy chain's cross slash and low sweep (8-29 cm, swingqa foe=down), so any blade press becomes the
            // style's heavy blow, which reaches down to the ground
            AttackData ground = moves.groundBlow != null ? moves.groundBlow : moves.heavyStart;
            if (team == Team.Player && next != null && next.kind != AttackKind.Kick && !next.shieldBash && next != ground && ground != null)
            {
                Combatant aim = Target != null && !Target.IsDead ? Target : SoftTarget();
                Combatant down = aim != null && aim.CurrentState == State.Knockdown ? aim : null;
                if (down != null && GK.Flat(down.transform.position - transform.position).magnitude <= ground.idealRange + ground.warpMaxDistance)
                    next = ground;
            }
            if (next == null) return false;
            bool started = StartAttack(next);
            freeFlowTarget = null;
            return started;
        }

        public bool RequestKick()
        {
            if (moves == null || moves.kick == null || IsDead || !CanAttackNow) return false;
            return StartAttack(moves.kick);
        }

        public bool RequestDodge(Vector3 worldDir)
        {
            if (moves == null || IsDead || !CanDodgeNow) return false;
            float dodgeCost = 18f * StaminaCostMul;
            if (!Stats.HasStamina(dodgeCost)) return false;
            worldDir = GK.Flat(worldDir);
            float angle = worldDir.sqrMagnitude < 0.01f ? 180f : GK.FlatAngle(transform.forward, worldDir);
            ActionClip clip = moves.dodge.Pick(angle);
            if (clip == null) return false;
            // dodges are authored facing forward: rotate toward the input for forward rolls
            if (Mathf.Abs(angle) <= 45f && worldDir.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(worldDir.normalized);
            Stats.UseStamina(dodgeCost);
            CloseSwing();
            dodgeStart = Time.time;
            perfectDodgeTriggered = false;
            PlayAction(clip, State.Dodging);
            if (Dodged != null) Dodged();
            return true;
        }

        /// <summary>Jumps from the ground (standing or running take-off). The clip's feet-based root lifts the capsule;
        /// the brain keeps steering the momentum in the air.</summary>
        public bool RequestJump(bool running)
        {
            if (moves == null || IsDead || CurrentState != State.Free || Motor == null || !Motor.IsGrounded) return false;
            ActionClip clip = running && moves.jumpRun != null && moves.jumpRun.IsValid ? moves.jumpRun : moves.jump;
            if (clip == null || !clip.IsValid) return false;
            float cost = 6f * StaminaCostMul;
            if (!Stats.HasStamina(cost)) return false;
            Stats.UseStamina(cost);
            BlockHeld = false;
            PlayAction(clip, State.Jumping);
            jumpLaunched = false;
            jumpTakeoff = running ? runJumpTakeoff : standJumpTakeoff;
            jumpSpeed = running ? runJumpSpeed : standJumpSpeed;
            return true;
        }

        [Header("Jump")]
        [Tooltip("Normalized clip time when the feet leave the ground (standing / running take-off).")]
        public float standJumpTakeoff = 0.30f, runJumpTakeoff = 0.12f;
        [Tooltip("Upward speed at take-off (m/s): with the motor's gravity this sets height and air time.")]
        public float standJumpSpeed = 4.4f, runJumpSpeed = 4.6f;
        bool jumpLaunched; float jumpTakeoff, jumpSpeed;

        public bool PlaySkill(ActionClip clip)
        {
            if (clip == null || !clip.IsValid || IsDead || !CanAttackNow) return false;
            CloseSwing();
            PlayAction(clip, State.Skill);
            return true;
        }

        /// <summary>The next part of a running skill (a bow's draw, hold and loose): played straight from the one
        /// that runs, without a return to the idle in between. Refused when no skill runs (a hit cut it short).</summary>
        public bool ChainSkill(ActionClip clip)
        {
            if (clip == null || !clip.IsValid || IsDead || CurrentState != State.Skill) return false;
            PlayAction(clip, State.Skill);
            return true;
        }

        /// <summary>When the attack that runs now began (Time.time).</summary>
        public float AttackStartedAt { get; private set; }

        /// <summary>Calls back a kick in its first <paramref name="within"/> seconds, before its contact (the pad's War Cry
        /// chord RB + X steps into the shout from it). False when no kick runs, it is older, or it has struck.</summary>
        public bool CancelKickWindup(float within)
        {
            if (CurrentState != State.Attacking || attack == null || attack.kind != AttackKind.Kick) return false;
            if (Time.time - AttackStartedAt > within || ActionProgress >= attack.hitStart) return false;
            ReturnToFree();
            return true;
        }

        /// <summary>The action clip that runs now (skills, reactions, dodges), null in an attack or when free.</summary>
        public ActionClip CurrentAction { get { return attack == null ? action : null; } }

        // ------------------------------------------------------------------ traversal (ladders; later ledges, vaults)

        object traversalOwner;
        public bool InTraversal { get { return CurrentState == State.Traversal; } }

        /// <summary>
        /// Hands the body to a traversal system (a ladder). Only from Free. While it lasts combat requests are refused,
        /// hits still hurt but play no reaction (the hands stay on the rungs), root motion belongs to the owner, the
        /// off-hand layer is released (climbing needs both hands) and a death always ragdolls (the body falls).
        /// </summary>
        public bool BeginTraversal(object owner)
        {
            if (owner == null || IsDead || CurrentState != State.Free) return false;
            traversalOwner = owner;
            CloseSwing();
            BlockHeld = false;
            charging = false;
            action = null; attack = null; lastState = null;
            actionRoot = false;
            SetState(State.Traversal);
            return true;
        }

        /// <summary>Returns the body. <paramref name="toLocomotion"/> cross-fades back to the locomotion tree.</summary>
        public void EndTraversal(object owner, bool toLocomotion)
        {
            if (CurrentState != State.Traversal || owner != traversalOwner) return;
            traversalOwner = null;
            if (toLocomotion) ReturnToFree(); else SetState(State.Free);
        }

        /// <summary>Plays an animator state for the traversal owner (inertialized like every other transition), starting
        /// <paramref name="fixedOffset"/> seconds into it (reverse playback starts at the end).</summary>
        public void PlayTraversal(string state, float fade, float fixedOffset)
        {
            if (CurrentState == State.Traversal) Play(state, fade, fixedOffset);
        }

        Transform[] kickLimbs;
        Transform[] KickLimbs()
        {
            if (kickLimbs != null || Anim == null || !Anim.isHuman) return kickLimbs;
            System.Func<HumanBodyBones, HumanBodyBones, Transform> T = (a, b) => Anim.GetBoneTransform(a) ?? Anim.GetBoneTransform(b);
            var l = new[] { T(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftLowerLeg), T(HumanBodyBones.LeftToes, HumanBodyBones.LeftFoot),
                            T(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightLowerLeg), T(HumanBodyBones.RightToes, HumanBodyBones.RightFoot) };
            foreach (var t in l) if (t == null) return null;
            kickLimbs = l;
            return kickLimbs;
        }

        /// <summary>Starts a specific attack (AI uses this directly).</summary>
        public bool StartAttack(AttackData a)
        {
            if (a == null || string.IsNullOrEmpty(a.state) || IsDead) return false;
            float attackCost = a.staminaCost * StaminaCostMul;
            if (!Stats.HasStamina(attackCost)) return false;
            Stats.UseStamina(attackCost);

            CloseSwing();
            attack = a;
            action = null;
            // a kick strikes with the legs, not with the sword in the hand
            if (weapon != null) weapon.UseLimbs(a.shieldBash && shieldEdge != null && shieldEdge.Length == 2 ? shieldEdge : a.kind == AttackKind.Kick ? KickLimbs() : null);
            chargeTime = 0f; charging = false; chargeDone = false; chargeMul = 1f;
            swingAnnounced = false;

            // pick who the swing should connect with
            warpTarget = Target != null && !Target.IsDead ? Target : null;
            if (warpTarget == null && freeFlowTarget != null && !freeFlowTarget.IsDead) warpTarget = freeFlowTarget;
            if (warpTarget == null) warpTarget = SoftTarget();
            if (warpTarget != null)
            {
                Vector3 to = GK.Flat(warpTarget.transform.position - transform.position);
                if (to.magnitude > a.warpMaxDistance + a.idealRange + 0.5f) warpTarget = null;
                else if (to.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(to.normalized);
            }
            else if (DesiredFacing.sqrMagnitude > 0.01f)
            {
                transform.rotation = Quaternion.LookRotation(GK.Flat(DesiredFacing).normalized);
            }

            actionDuration = a.Duration;          // progress is accumulated in clip time (actionTime += dt * animSpeed)
            actionRoot = true;
            // a swing chained out of another blends over a longer moment: from the last cut's follow-through into the
            // next one's start the arm travels far, and a short crossfade read as a jerk of the arm between the blows
            float fade = a.fadeIn;
            bool chained = CurrentState == State.Attacking || (lastAttack != null && Time.time - lastAttackEnded <= comboGrace);
            if (CurrentState == State.Attacking) fade = Mathf.Max(fade, 0.18f);
            else if (lastAttack != null && Time.time - lastAttackEnded <= comboGrace) fade = Mathf.Max(fade, 0.14f);
            float entry = chained && a.clip != null ? Mathf.Clamp(a.chainEntry, 0f, Mathf.Max(0f, a.hitStart - 0.02f)) : 0f;
            Play(a.state, fade, entry * (a.clip != null ? a.clip.length : 0f));
            // the progress starts where the clip does: the hit and combo windows keep their places in the clip
            if (entry > 0f) { actionTime = entry * actionDuration; ActionProgress = entry; }
            SetState(State.Attacking);
            AttackStartedAt = Time.time;
            if (AttackStarted != null) AttackStarted(a);
            if (a.unblockable && Telegraphed != null) Telegraphed(a);
            return true;
        }

        // ------------------------------------------------------------------ incoming hits

        public HitResult ReceiveHit(HitData hit)
        {
            if (IsDead || CurrentState == State.BeingExecuted) return HitResult.Ignored;
            Combatant attacker = hit.attacker;

            if (Invulnerable)
            {
                if (CurrentState == State.Dodging && !perfectDodgeTriggered && Time.time - dodgeStart <= perfectDodgeWindow)
                {
                    perfectDodgeTriggered = true;
                    if (PerfectDodge != null) PerfectDodge();
                }
                FeelPort.Simple(Feel.FeelKind.Dodge, gameObject, transform.position, team == Team.Player);
                return Notify(hit, HitResult.Dodged);
            }

            // a missile is met where it comes from (its flight), not where its shooter stands
            Vector3 threat = hit.ranged && hit.direction.sqrMagnitude > 1e-4f ? -hit.direction
                           : attacker != null ? attacker.transform.position - transform.position : Vector3.zero;
            bool facing = threat.sqrMagnitude < 1e-6f || Mathf.Abs(GK.FlatAngle(transform.forward, threat)) <= blockAngle;

            if (IsBlocking && facing && !hit.unblockable)
            {
                // a timed guard against an arrow knocks it aside (the archer is out of reach: no stagger for him)
                if (hit.ranged && canParry && Time.time - blockPressTime <= parryWindow)
                {
                    Stats.AddAdrenaline(15f);
                    return Notify(hit, HitResult.Parried);
                }
                if (canParry && Time.time - blockPressTime <= parryWindow && attacker != null && !hit.guardBreak)
                {
                    attacker.GetParried(this);
                    Stats.AddAdrenaline(35f);
                    if (ParrySucceeded != null) ParrySucceeded(attacker);
                    return Notify(hit, HitResult.Parried);
                }
                if (hit.guardBreak)
                {
                    EnterStagger(moves != null ? moves.guardBreak : null, 0f);
                    return Notify(hit, HitResult.GuardBroken);
                }
                Stats.UseStamina((hit.damage * blockStaminaFactor + 6f) * StaminaCostMul);
                if (Stats.Stamina <= 0.01f)
                {
                    EnterStagger(moves != null ? moves.guardBreak : null, 0f);
                    return Notify(hit, HitResult.GuardBroken);
                }
                Vector3 pushFrom = hit.ranged ? hit.point - hit.direction : attacker != null ? attacker.transform.position : hit.point;
                Motor.AddPush(GK.Flat(transform.position - pushFrom).normalized * hit.knockback * 1.5f);
                PlayBlockHit();
                return Notify(hit, HitResult.Blocked);
            }

            // take the hit: armour per hit (with the attacker's penetration); executions ignore armour
            var mods = Modifiers;
            float dealt = hit.execution || mods == null ? hit.damage : ArmorMath.Mitigate(hit.damage, mods.Armor, hit.armorPenetration);
#if GK_QA
            // -gkgod (GK_QA builds): the hero takes no damage and is not executed, so an autopilot run reaches its end
            if (team == Team.Player && GKDevFlags.God) dealt = 0f;
            if (hit.execution && !(team == Team.Player && GKDevFlags.God)) Stats.Kill();
#else
            if (hit.execution) Stats.Kill();
#endif
            else Stats.Damage(dealt);
            if (Stats.IsDead)
            {
                Die(hit);
                return Notify(hit, HitResult.Killed);
            }

            bool poiseBroke = Stats.DamagePoise(hit.poiseDamage);
            Vector3 from = attacker != null && !hit.ranged ? attacker.transform.position : hit.point - hit.direction;
            float angle = GK.FlatAngle(transform.forward, from - transform.position);
            if (CurrentState != State.Traversal) Motor.AddPush(GK.Flat(transform.position - from).normalized * hit.knockback * 2.2f);

            bool armored = (CurrentState == State.Attacking && attack != null && attack.hyperArmor && hit.reaction <= HitReaction.Light && !poiseBroke)
                           || CurrentState == State.Traversal;
            if (!armored)
            {
                if (hit.reaction == HitReaction.Knockdown) EnterKnockdown();
                else if (poiseBroke || hit.reaction == HitReaction.Stagger) EnterStagger(moves != null ? moves.stagger : null, 1.6f);
                else if (hit.reaction == HitReaction.Heavy && moves != null && moves.hitHeavy.IsValid)
                {
                    PlayAction(moves.hitHeavy, State.Reacting);
                    reactedAt = Time.time; reactedHeavy = true;
                }
                else if (hit.reaction != HitReaction.None && moves != null)
                {
                    ActionClip c = moves.hitLight.Pick(angle);
                    if (c != null) { PlayAction(c, State.Reacting); reactedAt = Time.time; reactedHeavy = false; }
                }
            }
            return Notify(hit, HitResult.Hit);
        }

        HitResult Notify(HitData hit, HitResult r)
        {
            if (HitReceived != null) HitReceived(hit, r);
            return r;
        }

        void GetParried(Combatant by)
        {
            CloseSwing();
            Stats.UseStamina(20f);
            EnterStagger(moves != null ? moves.parried : null, parriedVulnerability);
            if (CombatDirector.I != null) CombatDirector.I.ReleaseAttackToken(this);
        }

        void PlayBlockHit()
        {
            if (Anim == null || moves == null || !moves.blockHit.IsValid) return;
            // the guard reaction belongs to the style's guard (shield, high sword guard, crossed blades)
            if (blockLayer >= 0) Anim.CrossFadeInFixedTime(moves.blockHit.state, 0.04f, blockLayer, 0f);
        }

        void OnWeaponContact(Combatant target, Hurtbox hb, Vector3 point, Vector3 dir)
        {
            if (attack == null || target == null) return;
            // one blow per target per swing, whichever blade reached it first (a two-sword cut has two edges)
            if (!swingVictims.Add(target)) return;
            bool fullCharge = attack.chargeable && chargeMul >= attack.chargeDamageMultiplier * 0.95f;
            var mods = Modifiers;
            bool crit = mods != null && mods.CritChance > 0f && UnityEngine.Random.value < mods.CritChance;
            float statDamage = mods != null ? mods.DamageMultiplier * (crit ? Mathf.Max(1f, mods.CritDamage) : 1f) : 1f;
            // the knight's blow on an opening is a critical: an enemy reeling, down, or turned away from him
            // (a boot or a shield's face only shoves: never a critical)
            bool blade = attack.kind != AttackKind.Kick && !attack.shieldBash;
            if (!crit && blade && team == Team.Player && Exposed(target)) { crit = true; statDamage *= ExposedCritDamage; }
            HitData hit = new HitData
            {
                attacker = this,
                attack = attack,
                damage = attack.damage * Stats.damageMultiplier * statDamage * chargeMul * (hb != null ? hb.damageMultiplier : 1f),
                poiseDamage = attack.poiseDamage * Stats.poiseDamageMultiplier * (mods != null ? mods.PoiseDamageMultiplier : 1f) * chargeMul,
                armorPenetration = mods != null ? Mathf.Clamp01(mods.ArmorPenetration) : 0f,
                critical = crit,
                unblockable = attack.unblockable || fullCharge,
                guardBreak = attack.guardBreak || fullCharge,
                reaction = fullCharge ? HitReaction.Knockdown : attack.reaction,
                knockback = attack.knockback * (fullCharge ? 1.8f : 1f),
                point = point,
                direction = dir
            };
            HitResult r = target.ReceiveHit(hit);
            // a parry staggers us inside ReceiveHit, which clears our attack: from here the blow is the hit's own copy
            var blow = hit.attack;
            // the moment's weight (hit-stop, camera, slow motion) from the one tuning asset, GK.Feel's FeelSettings;
            // its defaults are the numbers this switch used to hold. A finishing critical slows the world, rarely
            bool heavyBlow = blow.kind != AttackKind.Light && blow.kind != AttackKind.Riposte;
            Feel.FeelKind fk = r == HitResult.Hit ? Feel.FeelKind.Hit : r == HitResult.Killed ? Feel.FeelKind.Kill :
                               r == HitResult.Blocked ? Feel.FeelKind.Block : r == HitResult.GuardBroken ? Feel.FeelKind.GuardBreak : Feel.FeelKind.Parry;
            if (r == HitResult.Hit || r == HitResult.Killed) Stats.AddAdrenaline(blow.kind == AttackKind.Light ? 8f : 12f);
            if (r == HitResult.Hit || r == HitResult.Killed || r == HitResult.Blocked || r == HitResult.GuardBroken || r == HitResult.Parried)
            {
                CombatFeel.I.HitStop(this, target, FeelPort.HitStop(fk, heavyBlow, blow.hitStop, crit));
                CombatFeel.I.Shake(point, FeelPort.Shake(fk, heavyBlow, blow.shake, crit));
                float slowS, slowSec, slowCd;
                if (FeelPort.SlowMotion(fk, heavyBlow, crit, out slowS, out slowSec, out slowCd))
                {
                    if (slowCd > 0f) CombatFeel.I.Flourish(slowS, slowSec, slowCd);
                    else CombatFeel.I.SlowMotion(slowS, slowSec);
                }
                FeelPort.Impact(fk, point, dir, gameObject, target.gameObject, heavyBlow, crit, team == Team.Player, target.team == Team.Player);
            }
            if (HitDealt != null) HitDealt(target, hit, r);
            if (r == HitResult.Blocked) BlockRebound();
        }

        /// <summary>The swing met a guard: it ends there and the arms rebound off it with the blow's weight, instead of the
        /// blade carrying on through the shield (or stopping dead). The cut is spent; the next one starts from the rebound.</summary>
        void BlockRebound()
        {
            if (CurrentState != State.Attacking || moves == null) return;
            CloseSwing();
            ActionClip c = moves.blockRecoil.IsValid ? moves.blockRecoil : moves.hitLight.Pick(0f);
            if (c != null && c.IsValid) PlayAction(c, State.Reacting);
        }

        /// <summary>Who an unlocked swing goes for: the enemy in the direction asked for (the stick, or the facing), and
        /// failing that the nearest one close by (within 3 m, at any angle; the facing still breaks ties) - a foe at the
        /// knight's shoulder is struck, not the air in front of him.</summary>
        Combatant SoftTarget()
        {
            var t = CombatDirector.I.FindTarget(this, DesiredFacing, assistRadius, assistAngle);
            if (t != null || team != Team.Player) return t;
            return CombatDirector.I.FindTarget(this, DesiredFacing, 3f, 180f);
        }

        const float ExposedCritDamage = 1.5f;

        // reeling, knocked down or getting up, or with his back to the blow
        bool Exposed(Combatant target)
        {
            var st = target.CurrentState;
            if (st == State.Staggered || st == State.Knockdown) return true;
            Vector3 from = GK.Flat(transform.position - target.transform.position);
            return from.sqrMagnitude > 1e-4f && Vector3.Dot(GK.Flat(target.transform.forward).normalized, from.normalized) < -0.35f;
        }

        // ------------------------------------------------------------------ executions

        Combatant FindOpenTarget()
        {
            Combatant best = null;
            float bestDist = 3.2f;
            List<Combatant> all = CombatDirector.I.all;
            for (int i = 0; i < all.Count; i++)
            {
                Combatant c = all[i];
                if (c == null || c.team == team || !c.IsOpenToRiposte) continue;
                Vector3 to = GK.Flat(c.transform.position - transform.position);
                float d = to.magnitude;
                if (d > bestDist) continue;
                Vector3 facing = DesiredFacing.sqrMagnitude > 0.01f ? DesiredFacing : transform.forward;
                if (Mathf.Abs(GK.FlatAngle(facing, to)) > 100f) continue;
                bestDist = d; best = c;
            }
            return best;
        }

        bool BeginExecution(Combatant victim)
        {
            AttackData ex = moves.execution;
            CloseSwing();
            attack = ex;
            action = null;
            chargeMul = 1f; charging = false; swingAnnounced = false;
            executionVictim = victim;
            executionApplied = false;
            warpTarget = null;

            Vector3 to = GK.Flat(victim.transform.position - transform.position);
            if (to.sqrMagnitude < 1e-4f) to = transform.forward;
            // turn to the victim over a few frames (snapping the facing read as a cut)
            execTurnFrom = transform.rotation;
            execTurnTo = Quaternion.LookRotation(to.normalized);
            execTurnT = 0f;
            victim.BeginExecutedBy(this, ex.idealRange, execTurnTo);

            actionDuration = ex.Duration;
            actionRoot = true;
            Play(ex.state, 0.1f);
            SetState(State.Executing);
            Invulnerable = true;
            float es, esec;
            if (FeelPort.ExecutionStartSlow(out es, out esec)) CombatFeel.I.SlowMotion(es, esec);
            if (AttackStarted != null) AttackStarted(ex);
            return true;
        }

        void BeginExecutedBy(Combatant executioner, float distance, Quaternion executionerFacing)
        {
            CloseSwing();
            Vector3 fwd = executionerFacing * Vector3.forward;
            // slide into the executioner's reach instead of teleporting there in one frame
            execSlideFrom = transform.position; execRotFrom = transform.rotation;
            execSlideTo = executioner.transform.position + fwd * Mathf.Max(0.8f, distance);
            execRotTo = Quaternion.LookRotation(-fwd);
            execSlideT = 0f;
            Invulnerable = true;
            bool paired = executioner.attack == null || executioner.attack.pairedVictim;
            ActionClip clip = moves == null ? null : paired ? moves.executionVictim : (moves.stagger.IsValid ? moves.stagger : moves.hitHeavy);
            if (clip != null && clip.IsValid) PlayAction(clip, State.BeingExecuted);
            else SetState(State.BeingExecuted);
            Invulnerable = true;
        }

        const float ExecSlideTime = 0.18f, ExecTurnTime = 0.12f;
        Vector3 execSlideFrom, execSlideTo;
        Quaternion execRotFrom, execRotTo, execTurnFrom, execTurnTo;
        float execSlideT = 1f, execTurnT = 1f;

        void ApplyExecutionKill()
        {
            if (executionVictim == null || executionApplied) return;
            executionApplied = true;
            HitData hit = new HitData
            {
                attacker = this, attack = attack, damage = 99999f, poiseDamage = 999f,
                unblockable = true, guardBreak = true, reaction = HitReaction.Knockdown,
                knockback = 0.5f, point = executionVictim.transform.position + Vector3.up * 1.2f,
                direction = transform.forward, execution = true
            };
            executionVictim.Invulnerable = false;
            executionVictim.Stats.Kill();
            executionVictim.Die(hit);
            Stats.AddAdrenaline(40f);
            CombatFeel.I.HitStop(this, null, FeelPort.HitStop(Feel.FeelKind.Execution, true, 0f, false));
            CombatFeel.I.Shake(hit.point, FeelPort.Shake(Feel.FeelKind.Execution, true, 0f, false));
            float xs, xsec, xcd;
            if (FeelPort.SlowMotion(Feel.FeelKind.Execution, true, false, out xs, out xsec, out xcd))
            {
                if (xcd > 0f) CombatFeel.I.Flourish(xs, xsec, xcd); else CombatFeel.I.SlowMotion(xs, xsec);
            }
            FeelPort.Impact(Feel.FeelKind.Execution, hit.point, hit.direction, gameObject, executionVictim.gameObject, true, false, team == Team.Player, executionVictim.team == Team.Player);
            if (HitDealt != null) HitDealt(executionVictim, hit, HitResult.Killed);
        }

        // ------------------------------------------------------------------ state helpers

        /// <summary>External stagger (war cry, shove, scripted events).</summary>
        public void ApplyStagger(float vulnerableSeconds)
        {
            if (IsDead || CurrentState == State.BeingExecuted || CurrentState == State.Executing) return;
            if (CurrentState == State.Attacking && attack != null && attack.hyperArmor) return;
            EnterStagger(moves != null ? moves.stagger : null, vulnerableSeconds);
        }

        /// <summary>External knockdown (scripted events, the swing tests' foe on the ground).</summary>
        public void ApplyKnockdown()
        {
            if (IsDead || CurrentState == State.BeingExecuted || CurrentState == State.Executing) return;
            EnterKnockdown();
        }

        void EnterStagger(ActionClip clip, float vulnerable)
        {
            CloseSwing();
            if (clip == null || !clip.IsValid) clip = moves != null ? moves.stagger : null;
            if (clip != null && clip.IsValid) PlayAction(clip, State.Staggered);
            else SetState(State.Staggered);
            executableUntil = Time.time + Mathf.Max(vulnerable, clip != null && clip.IsValid ? clip.Duration * clip.end : 1f);
            Stats.ResetPoise();
            if (CombatDirector.I != null) CombatDirector.I.ReleaseAttackToken(this);
        }

        void EnterKnockdown()
        {
            CloseSwing();
            if (moves != null && moves.knockdown.IsValid) PlayAction(moves.knockdown, State.Knockdown);
            else EnterStagger(null, 1f);
            Stats.ResetPoise();
            if (CombatDirector.I != null) CombatDirector.I.ReleaseAttackToken(this);
        }

        public void Die(HitData hit)
        {
            if (CurrentState == State.Dead) return;
            bool fell = CurrentState == State.Traversal;          // killed on a ladder: the body drops
            traversalOwner = null;
            lastHitDir = hit.direction;
            CloseSwing();
            BlockHeld = false;
            Invulnerable = true;
            action = null; attack = null;
            SetState(State.Dead);
            if (CombatDirector.I != null) CombatDirector.I.ReleaseAttackToken(this);

            var wsc = GetComponent<WeaponSelfCollision>();
            if (wsc != null) wsc.enabled = false;

            bool useRagdoll = Ragdoll != null && (fell || hit.reaction >= HitReaction.Heavy || UnityEngine.Random.value < 0.35f);
            if (useRagdoll)
            {
                Motor.Controller.enabled = false;
                Vector3 force = (GK.Flat(hit.direction).normalized * 3.5f + Vector3.up * 1.5f) * Mathf.Max(1f, hit.knockback);
                Ragdoll.Activate(force, hit.point);
            }
            else if (moves != null && moves.deaths != null && moves.deaths.Length > 0)
            {
                ActionClip d = moves.deaths[UnityEngine.Random.Range(0, moves.deaths.Length)];
                if (d != null && d.IsValid) { Play(d.state, 0.12f); actionRoot = d.rootMotion; }
                StartCoroutine(After(0.4f, DisableController));
            }
            // the hand opens as the body falls: the sword drops with physics instead of sinking into the ground
            if (useRagdoll) DropWeapon(hit.direction);
            else StartCoroutine(After(0.35f, DropWeaponDelayed));
            FeelPort.Simple(Feel.FeelKind.Death, gameObject, transform.position, team == Team.Player);
            if (Died != null) Died(hit);
            PublishDeath(hit);
        }

        void PublishDeath(HitData hit)
        {
            Combatant killer = hit.attacker;
            if (team == Team.Player)
                EventBus.Publish(new PlayerDied(killer != null ? killer.characterId : "", transform.position));
            else
                EventBus.Publish(new EnemyKilled(characterId ?? "", tags, transform.position,
                    killer != null && killer.team == Team.Player, hit.execution));
        }

        /// <summary>Multiplier on every stamina cost (stat system; 1 when no modifiers).</summary>
        float StaminaCostMul
        {
            get { var m = Modifiers; return m != null ? Mathf.Max(0.1f, m.StaminaCostMultiplier) : 1f; }
        }

        Vector3 lastHitDir;
        void DropWeaponDelayed() { DropWeapon(lastHitDir); }

        static System.Collections.IEnumerator After(float seconds, Action action)
        {
            yield return new WaitForSeconds(seconds);
            action();
        }

        /// <summary>Detaches the held weapon as a physics prop (capsule along grip→tip).</summary>
        public void DropWeapon(Vector3 push)
        {
            if (weapon == null || weapon.transform.parent == null) return;
            var t = weapon.transform;
            weapon.Deactivate();
            weapon.enabled = false;
            Vector3 handVel = Motor != null ? Motor.Velocity : Vector3.zero;
            t.SetParent(null, true);
            var bb = t.Find("BladeBase"); var bt = t.Find("BladeTip"); var gp = t.Find("Grip");
            var col = t.gameObject.AddComponent<CapsuleCollider>();
            if (bt != null && gp != null)
            {
                Vector3 a = gp.localPosition - (bt.localPosition - gp.localPosition).normalized * (0.09f / Mathf.Max(1e-4f, t.lossyScale.x));
                Vector3 b = bt.localPosition;
                Vector3 d = b - a;
                col.direction = Mathf.Abs(d.x) > Mathf.Abs(d.y) ? (Mathf.Abs(d.x) > Mathf.Abs(d.z) ? 0 : 2) : (Mathf.Abs(d.y) > Mathf.Abs(d.z) ? 1 : 2);
                col.center = (a + b) * 0.5f;
                col.radius = 0.025f / Mathf.Max(1e-4f, t.lossyScale.x);
                col.height = d.magnitude + col.radius * 2f;
            }
            t.gameObject.layer = 0;
            var rb = t.gameObject.AddComponent<Rigidbody>();
            rb.mass = 1.3f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.linearVelocity = handVel + GK.Flat(push).normalized * 1.2f + Vector3.up * 0.6f;
            rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 4f;
            weapon = null;
        }

        void DisableController() { if (Motor != null && Motor.Controller != null) Motor.Controller.enabled = false; }

        void PlayAction(ActionClip clip, State st)
        {
            if (clip == null || !clip.IsValid) { SetState(st == State.Dodging ? State.Free : st); return; }
            if (CurrentState == State.Attacking || CurrentState == State.Executing) CloseSwing();
            action = clip;
            attack = null;
            actionDuration = clip.Duration;
            actionRoot = clip.rootMotion;
            Play(clip.state, clip.fadeIn);
            SetState(st);
        }

        void Play(string state, float fade) { Play(state, fade, 0f); }

        void Play(string state, float fade, float fixedOffset)
        {
            if (Anim == null || string.IsNullOrEmpty(state)) return;
            string s = state;
            // re-entering the same state: use the duplicate so the cross-fade restarts cleanly
            if (s == lastState)
            {
                string alt;
                if (!altState.TryGetValue(state, out alt)) { alt = state + "#2"; altState[state] = alt; }
                if (HasState(alt)) { s = toggle ? state : alt; toggle = !toggle; }
            }
            else toggle = false;
            lastState = state;
            if (Inertializer != null && Inertializer.enabled)
            {
                // inertialized transition: the new clip starts this frame (no input lag) and the pose difference decays
                // smoothly, instead of a short cross-fade that pops when the clips' poses differ (combo chains)
                Anim.CrossFadeInFixedTime(s, 0f, 0, fixedOffset);
                Inertializer.Trigger(Mathf.Max(0.12f, fade * 2.2f));
            }
            else Anim.CrossFadeInFixedTime(s, fade, 0, fixedOffset);
            actionTime = 0f;
            ActionProgress = 0f;
        }

        bool HasState(string s)
        {
            int h = Animator.StringToHash(s);
            bool v;
            if (!stateExists.TryGetValue(h, out v)) { v = Anim.HasState(0, h); stateExists[h] = v; }
            return v;
        }

        void ReturnToFree()
        {
            CloseSwing();
            // remember the chain for a moment: the next press continues it (kicks and executions do not chain)
            if (attack != null && CurrentState == State.Attacking && attack.kind != AttackKind.Kick && attack.kind != AttackKind.Execution)
            { lastAttack = attack; lastAttackEnded = Time.time; }
            else lastAttack = null;
            attack = null;
            action = null;
            executionVictim = null;
            Invulnerable = false;
            charging = false;
            lastState = null;
            if (Anim != null)
            {
                bool strafe = Anim.GetFloat(GK.P_Strafe) > 0.5f;
                Anim.CrossFadeInFixedTime(strafe ? GK.S_Strafe : GK.S_Locomotion, returnFade, 0, 0f);
            }
            if (CombatDirector.I != null) CombatDirector.I.ReleaseAttackToken(this);
            SetState(State.Free);
        }

        void SetState(State s)
        {
            if (CurrentState == s && s != State.Attacking) return;
            CurrentState = s;
            if (s != State.Dodging && s != State.Executing && s != State.BeingExecuted && s != State.Dead) Invulnerable = false;
            if (StateChanged != null) StateChanged(s);
        }

        void CloseSwing()
        {
            if (swingOpen) SwingBlades(false);
            bool was = swingOpen || swingAnnounced;
            swingOpen = false;
            swingAnnounced = false;
            if (was && SwingEnded != null) SwingEnded();
        }

        [Tooltip("Animation speed during hit-stop. 0 stopped the blade dead on contact; a crawl keeps the follow-through "
                 + "moving through the stop, so the cut reads as meeting flesh, not a wall (QUALITY 3b rhythm, Feel D16).")]
        public float hitStopCrawl = 0.08f;

        public void FreezeFor(float seconds)
        {
            freezeUntil = Mathf.Max(freezeUntil, Time.unscaledTime + seconds);
        }

        // ------------------------------------------------------------------ frame update

        void Update()
        {
            float dt = Time.deltaTime;
            bool frozen = Time.unscaledTime < freezeUntil;
            float baseSpeed = charging ? 0.04f : (CurrentState == State.Attacking ? AttackSpeedMultiplier * SwingTempo.For(attack) : 1f);
            float animSpeed = frozen ? baseSpeed * hitStopCrawl : baseSpeed;
            if (!frozen && !charging && CurrentState == State.Attacking && attack != null && ActionProgress < attack.hitStart)
                animSpeed *= Mathf.Max(0.5f, attack.windupSpeed);
            if (Anim != null) Anim.speed = animSpeed;

            UpdateLayers(dt);
            if (IsBlocking && team == Team.Player) GuardTurn(dt);
            if (CurrentState == State.Dead || CurrentState == State.Free || CurrentState == State.Traversal) return;

            actionTime += dt * animSpeed;
            ActionProgress = actionDuration > 0f ? Mathf.Clamp01(actionTime / actionDuration) : 1f;

            switch (CurrentState)
            {
                case State.Attacking:
                case State.Executing:
                    if (CurrentState == State.Executing && execTurnT < 1f)
                    {
                        execTurnT = Mathf.Min(1f, execTurnT + dt / ExecTurnTime);
                        transform.rotation = Quaternion.Slerp(execTurnFrom, execTurnTo, Mathf.SmoothStep(0f, 1f, execTurnT));
                    }
                    TickAttack(dt, frozen);
                    break;
                case State.Dodging:
                    Invulnerable = action != null && ActionProgress >= action.iFrameStart && ActionProgress <= action.iFrameEnd;
                    if (action == null || ActionProgress >= action.end) ReturnToFree();
                    break;
                case State.Knockdown:
                    if (action == null || ActionProgress >= action.end)
                    {
                        if (moves != null && moves.getUp.IsValid && action != moves.getUp)
                        {
                            PlayAction(moves.getUp, State.Knockdown);
                            Invulnerable = true;
                        }
                        else ReturnToFree();
                    }
                    break;
                case State.BeingExecuted:
                    // the executioner kills us; until then we glide into his reach
                    if (execSlideT < 1f)
                    {
                        execSlideT = Mathf.Min(1f, execSlideT + dt / ExecSlideTime);
                        float k = Mathf.SmoothStep(0f, 1f, execSlideT);
                        Motor.Teleport(Vector3.Lerp(execSlideFrom, execSlideTo, k), Quaternion.Slerp(execRotFrom, execRotTo, k));
                    }
                    break;
                case State.Jumping:
                    if (!jumpLaunched && ActionProgress >= jumpTakeoff) { jumpLaunched = true; Motor.Launch(jumpSpeed); }
                    if (action == null || ActionProgress >= action.end) ReturnToFree();
                    break;
                default:
                    if (action == null || ActionProgress >= action.end) ReturnToFree();
                    break;
            }
        }

        void TickAttack(float dt, bool frozen)
        {
            if (attack == null) { ReturnToFree(); return; }
            float p = ActionProgress;

            // steering early in the swing
            if (p < attack.steerUntil && !frozen && CurrentState == State.Attacking)
            {
                Vector3 face = warpTarget != null ? warpTarget.transform.position - transform.position : DesiredFacing;
                Motor.FaceDirection(face, steerSharpness, dt);
            }
            // the player's swing keeps tracking its target, more gently, until the edge opens (a side-stepping foe
            // after steerUntil made the blade cut the air in front of the knight); enemies keep their fixed line so the
            // player can still side-step them
            else if (team == Team.Player && warpTarget != null && !warpTarget.IsDead && !frozen && CurrentState == State.Attacking
                     && p < FirstEdge())
                Motor.FaceDirection(warpTarget.transform.position - transform.position, trackSharpness, dt);

            // charging heavy attacks hold the wind-up pose while the button is held
            if (attack.chargeable && !chargeDone && CurrentState == State.Attacking)
            {
                if (!charging && chargeHeld && p >= attack.hitStart * 0.8f) charging = true;
                if (charging)
                {
                    chargeTime += Time.unscaledDeltaTime;
                    float k = Mathf.Clamp01(chargeTime / Mathf.Max(0.05f, attack.maxChargeTime));
                    chargeMul = Mathf.Lerp(1f, attack.chargeDamageMultiplier, k);
                    if (ChargeChanged != null) ChargeChanged(k);
                    if (!chargeHeld || k >= 1f) { charging = false; chargeDone = true; if (ChargeChanged != null) ChargeChanged(-1f); }
                    return;
                }
            }

            if (!swingAnnounced && p >= attack.hitStart - 0.07f)
            {
                swingAnnounced = true;
                if (SwingStarted != null) SwingStarted(attack);
                if (attack != null && weapon != null && weapon.bladeTip != null && attack.kind != AttackKind.Kick)
                    FeelPort.Swing(gameObject, weapon.bladeTip.position, attack.kind != AttackKind.Light && attack.kind != AttackKind.Riposte, team == Team.Player);
                if (attack == null) return;      // a listener ended the swing as it was announced (a blow taken)
            }

            if (CurrentState == State.Executing)
            {
                if (p >= attack.hitStart) ApplyExecutionKill();
            }
            else
            {
                // frame-rate independent: open one progress step early, so the blade sweep covers the frame that
                // crosses into the window, and stay open at least one frame, so a frame that leaps over a short
                // window (a slow machine, a hitch) still sweeps it. At 60 fps that is a few milliseconds either side
                float step = p >= windowPrevP ? p - windowPrevP : 0f;
                windowPrevP = p;
                bool inWindow;
                int cut = -1;
                if (attack.cuts != null && attack.cuts.Length > 0)
                {
                    // a combo clip: the blade cuts in each of its strikes; a new strike starts with fresh victims
                    for (int c = 0; c < attack.cuts.Length; c++)
                        if (p >= attack.cuts[c].x - step && p <= attack.cuts[c].y) { cut = c; break; }
                    inWindow = cut >= 0 || (swingOpen && Time.frameCount <= swingOpenFrame);
                    if (swingOpen && cut >= 0 && cut != swingCut) { SwingBlades(false); swingOpen = false; }
                }
                else inWindow = p >= attack.hitStart - step && (p <= attack.hitEnd || (swingOpen && Time.frameCount <= swingOpenFrame));
                if (inWindow && !swingOpen && weapon != null)
                {
                    SwingBlades(true); swingOpen = true; swingOpenFrame = Time.frameCount; swingCut = cut;
                    if (attack == null) return;      // the blade's first contact ended the swing (a kill, a parry)
                }
                else if (!inWindow && swingOpen) { SwingBlades(false); swingOpen = false; if (SwingEnded != null) SwingEnded(); }
            }

            // a listener of the swing events may have ended the attack already (a hit taken, a kill)
            if (attack == null) return;
            if (p >= attack.end) ReturnToFree();
        }

        /// <summary>Opens or closes the cut on the blade(s) the current swing uses: a kick and a right-hand swing use the
        /// main hitbox, a mirrored swing the second sword's, a two-blade swing both.</summary>
        float windowPrevP;      // the swing's progress last frame (a new swing starts lower and resets the step)
        int swingOpenFrame;
        int swingCut = -1;      // which of a combo clip's cuts the open blade is in

        void SwingBlades(bool on)
        {
            WeaponHand hand = attack != null && offWeapon != null && attack.kind != AttackKind.Kick ? attack.hand : WeaponHand.Main;
            bool main = hand != WeaponHand.Off, off = hand != WeaponHand.Main;
            if (!on)
            {
                swingVictims.Clear();
                if (weapon != null) weapon.Deactivate();
                if (offWeapon != null) offWeapon.Deactivate();
                return;
            }
            if (main && weapon != null) { weapon.assistTarget = warpTarget; weapon.Activate(); }
            if (off && offWeapon != null) { offWeapon.assistTarget = warpTarget; offWeapon.Activate(); }
        }

        /// <summary>When set (0..1), the OffHand layer weight is held there: the gear changes (shield to the back, the
        /// second sword drawn) animate the left arm on that layer. Negative: the state / style decides.</summary>
        [NonSerialized] public float offHandOverride = -1f;
        readonly HashSet<Combatant> swingVictims = new HashSet<Combatant>(8);   // sized now: the first hit grows nothing (GK-26)

        void UpdateLayers(float dt)
        {
            if (Anim == null) return;
            if (blockLayer >= 0)
            {
                // guard comes up almost instantly (the player must SEE the block the frame after the press), lowers softly
                float target = IsBlocking ? 1f : 0f;
                blockWeight = target > blockWeight ? Mathf.MoveTowards(blockWeight, target, dt / Mathf.Max(0.01f, blockPoseRise))
                                                   : GK.Damp(blockWeight, target, blockLayerSharpness, dt);
                Anim.SetLayerWeight(blockLayer, blockWeight);
            }
            if (offHandLayer >= 0)
            {
                float target;
                if (offHandOverride >= 0f) target = offHandOverride;
                else if (IsDead || CurrentState == State.Traversal) target = 0f;
                else if (IsBlocking && moves != null) target = moves.offHandBlock;
                else if (CurrentState == State.Free) target = offHandWeightFree;
                else if (attack != null) target = attack.offHand;
                else if (action != null) target = action.offHand;
                else target = offHandWeightAction;
                offHandWeight = GK.Damp(offHandWeight, target, 14f, dt);
                Anim.SetLayerWeight(offHandLayer, offHandWeight);
            }
        }

        /// <summary>A held guard turns to meet the nearest enemy who is swinging (from the side or behind as well): the blow
        /// lands on the shield or the blade instead of the back. Fast enough (900 deg/s) to come round within a
        /// wind-up.</summary>
        void GuardTurn(float dt)
        {
            var dir = CombatDirector.I;
            if (dir == null) return;
            Combatant threat = null; float best = 4.2f * 4.2f;
            foreach (var c in dir.all)
            {
                if (c == null || c == this || c.IsDead || c.team == team || c.CurrentState != State.Attacking) continue;
                float d = GK.Flat(c.transform.position - transform.position).sqrMagnitude;
                if (d < best) { best = d; threat = c; }
            }
            if (threat == null) return;
            Vector3 to = GK.Flat(threat.transform.position - transform.position);
            if (to.sqrMagnitude < 1e-4f) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(to.normalized), 900f * dt);
        }

        void OnAnimatorMove()
        {
            if (Anim == null) return;
            // jumps: the clip only poses the body (root height is not baked, so the legs tuck under a level pelvis);
            // the motor's launch and gravity carry it, and the brain steers the momentum
            if (CurrentState == State.Jumping) return;
            bool rootActive;
            if (CurrentState == State.Free || CurrentState == State.Traversal) rootActive = false;   // traversal owners apply their own
            else if (CurrentState == State.Dead) rootActive = actionRoot && Motor.Controller.enabled;
            else rootActive = actionRoot;
            if (!rootActive) return;

            Vector3 delta = Anim.deltaPosition;
            delta.y = 0f;

            if (CurrentState == State.Attacking && warpTarget != null && attack != null && ActionProgress < attack.hitEnd)
                delta += WarpCorrection(delta);

            Motor.AddRootMotion(delta);
            transform.rotation = Anim.deltaRotation * transform.rotation;
        }

        Transform lFootBone, rFootBone;
        float lFootRest = 1f, rFootRest = 1f;

        /// <summary>0 while both feet are planted, 1 while one is clearly off the ground (its ankle 5.5 cm over its rest
        /// height). The rest height is the lowest seen, creeping up slowly so a crouch or a slope is learnt again.</summary>
        float FootLift()
        {
            if (Anim == null || !Anim.isHuman) return 1f;
            if (lFootBone == null) { lFootBone = Anim.GetBoneTransform(HumanBodyBones.LeftFoot); rFootBone = Anim.GetBoneTransform(HumanBodyBones.RightFoot); }
            if (lFootBone == null || rFootBone == null) return 1f;
            float ly = lFootBone.position.y - transform.position.y, ry = rFootBone.position.y - transform.position.y;
            lFootRest = Mathf.Min(lFootRest + Time.deltaTime * 0.05f, ly);
            rFootRest = Mathf.Min(rFootRest + Time.deltaTime * 0.05f, ry);
            return Mathf.Clamp01((Mathf.Max(ly - lFootRest, ry - rFootRest) - 0.015f) / 0.04f);
        }

        // where the swing's (first) edge opens: hitStart, or the first cut of a combo clip
        float FirstEdge()
        {
            if (attack == null) return 0f;
            if (attack.cuts != null && attack.cuts.Length > 0) return attack.cuts[0].x;
            return attack.hitStart;
        }

        /// <summary>Adds the extra displacement needed to land the swing at the attack's ideal range. It is carried by the
        /// steps: mostly on frames where a foot is off the ground, hardly while both are planted (moved under planted
        /// feet, the heavy cuts slid them 19-36 cm, QUALITY 3c: planted feet stay put).</summary>
        Vector3 WarpCorrection(Vector3 rootDelta)
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return Vector3.zero;
            float lift = Mathf.Lerp(0.1f, 1f, FootLift());
            Vector3 to = GK.Flat(warpTarget.transform.position - transform.position);
            float dist = to.magnitude;
            if (dist < 1e-3f || dist > attack.warpMaxDistance + attack.idealRange + 0.5f) return Vector3.zero;
            Vector3 dir = to / dist;
            float desired = attack.idealRange + warpTarget.bodyRadius * 0.5f;
            float remaining = dist - desired;
            float clipStep = Vector3.Dot(rootDelta, dir);
            float step;
            // the strike this wind-up leads into: a combo clip's next cut, so a foe a cut has shoved back is closed on
            // again before the following one (the third cut passed short of a staggered foe at 1.9 m)
            float strikeAt = attack.hitStart;
            bool inCut = false;
            if (attack.cuts != null && attack.cuts.Length > 0)
            {
                strikeAt = -1f;
                foreach (var c in attack.cuts)
                {
                    if (ActionProgress >= c.x && ActionProgress <= c.y) { inCut = true; break; }
                    if (c.x > ActionProgress) { strikeAt = c.x; break; }
                }
            }
            if (!inCut && strikeAt > ActionProgress)
            {
                // wind-up: arrive at the ideal range by the first frame of the strike, closing in OR making space
                // (a target already inside the arc was missed by the blade passing beyond it)
                float playRate = Mathf.Max(0.1f, AttackSpeedMultiplier * Mathf.Max(0.5f, attack.windupSpeed));
                float timeLeft = Mathf.Max(dt, (strikeAt - ActionProgress) * actionDuration / playRate);
                // the clip will still move us by roughly its own forward root motion; correct only the difference
                float clipForward = clipStep / dt * timeLeft;
                // about half the wind-up's frames are steps: they carry twice the even share, the planted ones a tenth
                step = Mathf.Clamp((remaining - clipForward) * dt / timeLeft * 2f * lift, -attack.maxWarpSpeed * 1.3f * dt * lift, attack.maxWarpSpeed * 1.6f * dt * lift);
            }
            else
            {
                // strike: hold the range the swing was calibrated at. The clip's own lunge would carry a close target
                // inside the arc; a target stepping away is followed only gently (sliding mid-swing reads as a glitch)
                // the player's cut follows a foe stepping back a little harder (0.6 vs 0.3): his swings into the air were
                // mostly a foe drifting out of the arc mid-cut; enemies keep 0.3 so the player's back-step still works
                float follow = team == Team.Player ? 0.6f : 0.3f;
                step = Mathf.Clamp(remaining - clipStep, -attack.maxWarpSpeed * 0.8f * dt * lift, attack.maxWarpSpeed * follow * dt * lift);
            }
            return dir * step;
        }

        void OnDisable()
        {
            if (swingOpen && weapon != null) weapon.Deactivate();
            swingOpen = false;
        }
    }
}
