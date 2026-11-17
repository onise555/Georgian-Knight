using UnityEngine;
using UnityEngine.AI;

namespace GeorgianKnight
{
    /// <summary>
    /// E06 Turkmen archer (BALANCE 5.1): keeps a shooting band from the player, draws only with one of the director's
    /// ranged tokens (two archers at most loose at once), and every shot is told before it flies: the nock and draw take
    /// <see cref="MoveSet.bowDraw"/> plus a hold while the aim settles. The arrow is aimed with lead on the target's
    /// motion and the ballistic arc, and misses by a cone that widens with distance. Rushed inside panicRange he kicks
    /// (or dodges back) and gives ground; a hit while drawing spoils the shot. Melee tokens are never his.
    /// </summary>
    [RequireComponent(typeof(Combatant))]
    public class ArcherAI : MonoBehaviour
    {
        public enum Mode { Idle, Approach, Hold, BackOff, Draw, Aim, Loose, Panic }

        [Header("Awareness")]
        public float noticeRange = 40f;
        [Tooltip("Set by the wave manager: engage even beyond noticeRange.")]
        public bool engaged;

        [Header("Range band (m)")]
        [Tooltip("Inside this he backs away while he can.")] public float minRange = 8f;
        [Tooltip("Where he likes to stand.")] public float preferredRange = 14f;
        [Tooltip("Beyond this he closes in.")] public float maxRange = 24f;
        [Tooltip("Rushed this close he kicks or dodges back.")] public float panicRange = 3f;

        [Header("Movement")]
        public float runSpeed = 3.6f;
        public float walkSpeed = 1.3f;
        public float turnSharpness = 9f;

        [Header("Shot (E06)")]
        public GameObject arrowPrefab;
        [Tooltip("Where the arrow leaves the bow (child of the bow at the arrow rest); the left hand when empty.")]
        public Transform arrowRest;
        [Tooltip("Arrow speed (m/s). Slower than the hero's 55: at 14 m the flight takes ~0.4 s, a dodge can answer it.")]
        public float arrowSpeed = 38f;
        public float damage = 22f;
        public float poise = 25f;
        public HitReaction reaction = HitReaction.Light;
        [Tooltip("Seconds at full draw before the loose, while the aim settles (random in range).")]
        public Vector2 holdTime = new Vector2(0.25f, 0.6f);
        [Tooltip("Seconds between his shots (random in range).")]
        public Vector2 shotInterval = new Vector2(1.6f, 3.0f);
        [Tooltip("How much of the target's motion he leads (0 = aims where he stands, 1 = perfect lead).")]
        [Range(0f, 1f)] public float lead = 0.7f;
        [Tooltip("Aim error cone (deg): base + per metre. 0.8 + 0.07/m (1.8 deg at 14 m) hit a standing hero 2 of 5: no threat.")]
        public float aimError = 0.5f, aimErrorPerMetre = 0.035f;
        [Tooltip("Most the chest bends up or down to the aim (deg).")]
        public float maxAimPitch = 35f;

        [Header("Reactions")]
        [Range(0f, 1f)] public float dodgeSkill = 0.3f;
        public float reactionTime = 0.25f;

        public Mode CurrentMode { get; private set; }
        /// <summary>The aim at full draw: QA reads the planned flight.</summary>
        public Vector3 AimDirection { get; private set; }
        /// <summary>A shot was drawn (the tell): Audio / Feel hook.</summary>
        public event System.Action<ArcherAI> DrawStarted;
        public event System.Action<ArcherAI, Arrow> Loosed;

        Combatant self, player;
        NavMeshAgent agent;
        CharacterMotor motor;
        Animator anim;
        Transform spine, chest, bowHand;
        Vector3 velocity;
        float nextShot, holdUntil, panicUntil, strafeDir = 1f, strafeSwitchAt, aimPitch;
        bool hasToken;
        ActionClip drawnBy;

        void Awake()
        {
            self = GetComponent<Combatant>();
            agent = GetComponent<NavMeshAgent>();
            motor = GetComponent<CharacterMotor>();
            if (agent != null) { agent.updatePosition = false; agent.updateRotation = false; agent.autoBraking = true; }
            var cc = GetComponent<CharacterController>();
            int blocker = LayerMask.NameToLayer("PlayerBlocker");
            if (cc != null && blocker >= 0) cc.excludeLayers |= 1 << blocker;     // C14
            strafeDir = Random.value < 0.5f ? -1f : 1f;
        }

        void Start()
        {
            anim = self.Anim;
            if (anim != null && anim.isHuman)
            {
                spine = anim.GetBoneTransform(HumanBodyBones.Spine);
                chest = anim.GetBoneTransform(HumanBodyBones.Chest);
                bowHand = anim.GetBoneTransform(HumanBodyBones.LeftHand);
            }
            self.Died += h => { GiveUpToken(); if (agent != null && agent.isOnNavMesh) agent.isStopped = true; motor.SetLocomotionVelocity(Vector3.zero); enabled = false; };
            self.HitReceived += OnHit;
            nextShot = Time.time + Random.Range(0.6f, 1.6f);
        }

        void OnDisable() { GiveUpToken(); }

        void OnHit(HitData h, HitResult r)
        {
            // struck while drawing: the shot is spoilt, the string let down
            if (r == HitResult.Hit || r == HitResult.GuardBroken)
            {
                if (CurrentMode == Mode.Draw || CurrentMode == Mode.Aim) { GiveUpToken(); nextShot = Time.time + Random.Range(0.8f, 1.6f); }
            }
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (self.IsDead) return;
            if (player == null || player.IsDead)
            {
                player = CombatDirector.I.Player;
                if (player == null || player.IsDead) { Idle(dt); return; }
            }
            self.Target = player;
            if (agent != null && agent.isOnNavMesh) agent.nextPosition = transform.position;

            Vector3 to = GK.Flat(player.transform.position - transform.position);
            float dist = to.magnitude;
            self.DesiredFacing = to;

            // the shot in progress owns him until it is loosed or spoilt
            if (self.CurrentState == Combatant.State.Skill && hasToken) { Shoot(to, dist, dt); return; }
            if (CurrentMode == Mode.Draw || CurrentMode == Mode.Aim || CurrentMode == Mode.Loose)
            {
                // the shot ended (loosed, or a hit / dodge broke it off)
                GiveUpToken();
                CurrentMode = Mode.Hold;
            }

            if (self.CurrentState != Combatant.State.Free)
            {
                velocity = Vector3.zero;
                motor.SetLocomotionVelocity(Vector3.zero);
                Animate(dt, true);
                return;
            }

            if (dist > noticeRange && !engaged) { Idle(dt); return; }

            // rushed: a kick to make room, or a dodge back
            if (dist < panicRange && Time.time >= panicUntil)
            {
                panicUntil = Time.time + 1.2f;
                CurrentMode = Mode.Panic;
                var m = self.moves;
                bool kicked = m != null && m.kick != null && Random.value > dodgeSkill && self.StartAttack(m.kick);
                if (!kicked) self.RequestDodge(-to + transform.right * (Random.value < 0.5f ? -0.6f : 0.6f));
                return;
            }

            bool sees = SeesPlayer();
            if (dist > maxRange || !sees)
            {
                CurrentMode = Mode.Approach;
                MoveAlongPath(player.transform.position, runSpeed, dt, false);
                return;
            }
            if (dist < minRange)
            {
                CurrentMode = Mode.BackOff;
                Vector3 away = transform.position - to.normalized * 4f;
                Strafe(to, dist, dt, -1f);
                if (agent != null && agent.isOnNavMesh) { NavMeshHit nh; if (!NavMesh.SamplePosition(away, out nh, 1f, NavMesh.AllAreas)) strafeDir = -strafeDir; }
            }
            else
            {
                CurrentMode = Mode.Hold;
                Strafe(to, dist, dt, Mathf.Clamp((dist - preferredRange) * 0.25f, -0.6f, 0.6f));
            }

            // a shot: the interval passed, a clear line, a token from the director
            if (Time.time >= nextShot && sees && dist >= panicRange + 1f && self.moves != null && self.moves.bowDraw.IsValid)
            {
                if (CombatDirector.I.RequestRangedToken(self))
                {
                    hasToken = true;
                    if (self.PlaySkill(self.moves.bowDraw))
                    {
                        drawnBy = self.moves.bowDraw;
                        CurrentMode = Mode.Draw;
                        holdUntil = -1f;
                        motor.SetLocomotionVelocity(Vector3.zero);
                        if (DrawStarted != null) DrawStarted(this);
                    }
                    else GiveUpToken();
                }
                else nextShot = Time.time + Random.Range(0.4f, 0.9f);
            }
        }

        void LateUpdate()
        {
            // bend the chest to the aim's elevation while the bow is up (the clips aim level)
            bool bowUp = CurrentMode == Mode.Draw || CurrentMode == Mode.Aim;
            float want = 0f;
            if (bowUp && AimDirection.sqrMagnitude > 0.5f)
                want = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(AimDirection.y, -1f, 1f)) * Mathf.Rad2Deg, -maxAimPitch, maxAimPitch);
            aimPitch = Mathf.Lerp(aimPitch, want, 1f - Mathf.Exp(-10f * Time.deltaTime));
            if (spine == null || Mathf.Abs(aimPitch) < 0.05f) return;
            Vector3 right = transform.right;
            spine.rotation = Quaternion.AngleAxis(-aimPitch * 0.4f, right) * spine.rotation;
            if (chest != null) chest.rotation = Quaternion.AngleAxis(-aimPitch * 0.6f, right) * chest.rotation;
        }

        // ------------------------------------------------------------------ the shot
        void Shoot(Vector3 to, float dist, float dt)
        {
            motor.SetLocomotionVelocity(Vector3.zero);
            motor.FaceDirection(to, turnSharpness, dt);
            var m = self.moves;
            var cur = self.CurrentAction;
            AimDirection = Aim();
            if (CurrentMode == Mode.Draw)
            {
                // drawn to the cheek: hold while the aim settles
                if (cur == drawnBy && self.ActionProgress >= m.bowFullDraw)
                {
                    CurrentMode = Mode.Aim;
                    holdUntil = Time.time + Random.Range(holdTime.x, holdTime.y);
                    if (m.bowHold.IsValid) self.ChainSkill(m.bowHold);
                }
            }
            else if (CurrentMode == Mode.Aim)
            {
                if (Time.time >= holdUntil)
                {
                    CurrentMode = Mode.Loose;
                    var arrow = LooseArrow();
                    if (m.bowLoose.IsValid) self.ChainSkill(m.bowLoose);
                    nextShot = Time.time + Random.Range(shotInterval.x, shotInterval.y);
                    GiveUpToken();
                    if (Loosed != null) Loosed(this, arrow);
                }
            }
        }

        Vector3 Origin()
        {
            if (arrowRest != null) return arrowRest.position;
            if (bowHand != null) return bowHand.position;
            return transform.position + Vector3.up * 1.45f + transform.forward * 0.5f;
        }

        Vector3 TargetPoint()
        {
            var pa = player.Anim;
            Transform c = pa != null && pa.isHuman ? (pa.GetBoneTransform(HumanBodyBones.Chest) ?? pa.GetBoneTransform(HumanBodyBones.Spine)) : null;
            return c != null ? c.position : player.transform.position + Vector3.up * 1.3f;
        }

        /// <summary>The launch direction: lead on the target's motion, then the arc that drops onto it at arrowSpeed.</summary>
        Vector3 Aim()
        {
            Vector3 o = Origin(), t = TargetPoint();
            var pm = player.Motor;
            Vector3 v = pm != null ? GK.Flat(pm.Velocity) : Vector3.zero;
            float flight = Vector3.Distance(o, t) / Mathf.Max(1f, arrowSpeed);
            t += v * flight * lead;
            return Ballistic(o, t, arrowSpeed, 9.8f);
        }

        static Vector3 Ballistic(Vector3 from, Vector3 to, float speed, float g)
        {
            Vector3 d = to - from;
            Vector3 flat = GK.Flat(d);
            float x = flat.magnitude, y = d.y;
            if (x < 0.01f) return d.normalized;
            float v2 = speed * speed;
            float root = v2 * v2 - g * (g * x * x + 2f * y * v2);
            if (root < 0f) return (flat.normalized + Vector3.up).normalized;      // out of range: 45 deg
            float angle = Mathf.Atan2(v2 - Mathf.Sqrt(root), g * x);               // the low arc
            return (flat / x * Mathf.Cos(angle) + Vector3.up * Mathf.Sin(angle)).normalized;
        }

        Arrow LooseArrow()
        {
            Vector3 o = Origin();
            Vector3 dir = Aim();
            float dist = Vector3.Distance(o, TargetPoint());
            float err = aimError + aimErrorPerMetre * dist;
            // a cone of error: uniform in angle around the aim, gaussian-ish in size
            Vector3 axis = Vector3.Cross(dir, Random.onUnitSphere);
            if (axis.sqrMagnitude < 1e-4f) axis = Vector3.Cross(dir, Vector3.up);
            float a = err * 0.5f * (Random.value + Random.value);
            dir = Quaternion.AngleAxis(a, axis.normalized) * dir;
            return Arrow.Loose(arrowPrefab, o + dir * 0.05f, dir * arrowSpeed, self, damage, poise, reaction);
        }

        bool SeesPlayer()
        {
            Vector3 o = transform.position + Vector3.up * 1.5f;
            Vector3 t = TargetPoint();
            return !Physics.Linecast(o, t, GK.SightBlockMask, QueryTriggerInteraction.Ignore);
        }

        void GiveUpToken()
        {
            if (!hasToken) return;
            hasToken = false;
            if (CombatDirector.Existing != null) CombatDirector.Existing.ReleaseRangedToken(self);
        }

        // ------------------------------------------------------------------ movement
        void Idle(float dt)
        {
            CurrentMode = Mode.Idle;
            velocity = Vector3.MoveTowards(velocity, Vector3.zero, 6f * dt);
            motor.SetLocomotionVelocity(velocity);
            Animate(dt, false);
        }

        void MoveAlongPath(Vector3 destination, float speed, float dt, bool face)
        {
            Vector3 dir;
            if (agent != null && agent.isOnNavMesh)
            {
                agent.speed = speed;
                agent.SetDestination(destination);
                dir = agent.desiredVelocity;
                if (dir.sqrMagnitude < 0.01f) dir = destination - transform.position;
            }
            else dir = destination - transform.position;
            dir = GK.Flat(dir);
            Vector3 desired = dir.sqrMagnitude > 0.0001f ? dir.normalized * speed : Vector3.zero;
            velocity = Vector3.MoveTowards(velocity, desired, 12f * dt);
            motor.SetLocomotionVelocity(velocity);
            motor.FaceDirection(velocity.sqrMagnitude > 0.1f ? velocity : dir, turnSharpness, dt);
            Animate(dt, false);
        }

        /// <summary>Side-steps facing the player, drifting in or out by <paramref name="radial"/> (-1 away .. +1 in).</summary>
        void Strafe(Vector3 to, float dist, float dt, float radial)
        {
            if (Time.time > strafeSwitchAt)
            {
                strafeSwitchAt = Time.time + Random.Range(1.8f, 4f);
                if (Random.value < 0.4f) strafeDir = -strafeDir;
            }
            Vector3 fwd = to / Mathf.Max(dist, 0.01f);
            Vector3 side = Vector3.Cross(Vector3.up, fwd) * strafeDir;
            Vector3 dir = (side * 0.7f + fwd * radial).normalized;
            if (agent != null && agent.isOnNavMesh)
            {
                NavMeshHit hit;
                if (!NavMesh.SamplePosition(transform.position + dir * 1.2f, out hit, 0.6f, NavMesh.AllAreas)) { strafeDir = -strafeDir; dir = (-side * 0.7f + fwd * radial).normalized; }
            }
            velocity = Vector3.MoveTowards(velocity, dir * walkSpeed, 8f * dt);
            motor.SetLocomotionVelocity(velocity);
            motor.FaceDirection(fwd, turnSharpness, dt);
            Animate(dt, true);
        }

        void Animate(float dt, bool strafe)
        {
            if (anim == null) return;
            Vector3 v = GK.Flat(motor.Velocity);
            anim.SetFloat(GK.P_Strafe, strafe ? 1f : 0f);
            anim.SetFloat(GK.P_Speed, v.magnitude, 0.1f, dt);
            Vector3 local = transform.InverseTransformDirection(v) / Mathf.Max(0.01f, runSpeed);
            anim.SetFloat(GK.P_MoveX, local.x, 0.12f, dt);
            anim.SetFloat(GK.P_MoveY, local.z, 0.12f, dt);
        }
    }
}
