using UnityEngine;
using UnityEngine.AI;

namespace GeorgianKnight
{
    /// <summary>
    /// Enemy brain. Paths with a NavMeshAgent (position driven by our motor), keeps a fighting ring
    /// around the player, circles while waiting for an attack token, blocks/dodges reactively and
    /// mixes combos with telegraphed unblockable strikes.
    /// </summary>
    [RequireComponent(typeof(Combatant), typeof(NavMeshAgent))]
    public class EnemyAI : MonoBehaviour
    {
        public enum Mode { Idle, Approach, Circle, Engage, Recover }

        [Header("Ranges")]
        public float noticeRange = 30f;
        [Tooltip("Sent in by a wave: marches on the player from any distance (a lane head can be 120 m out, far past the "
                 + "notice range: wave 3 at Didgori stood idle there for minutes).")]
        public bool engaged;
        [Tooltip("Where the wave fights for (a scene hold point, the crest of Black Banner Rise): marched to until the player "
                 + "comes within holdRadius of it, then the player. Empty = the player.")]
        public Transform holdPoint;
        public float holdRadius = 15f;
        public float circleRange = 3.6f;
        public float attackRange = 2.1f;

        [Header("Speeds")]
        public float runSpeed = 3.6f;
        public float walkSpeed = 1.4f;
        public float turnSharpness = 9f;

        [Header("Personality (0..1)")]
        [Range(0f, 1f)] public float aggression = 0.55f;
        [Range(0f, 1f)] public float blockSkill = 0.45f;
        [Range(0f, 1f)] public float dodgeSkill = 0.15f;
        [Range(0f, 1f)] public float comboChance = 0.55f;
        [Range(0f, 1f)] public float unblockableChance = 0.15f;
        public float reactionTime = 0.22f;
        [Tooltip("Chance to answer a raised guard with the kick (the move set's guard breaker) instead of a cut.")]
        [Range(0f, 1f)] public float guardBreakChance = 0.12f;   // 0.35 broke a held guard 8 of 18 blows (duelqa)
        [Tooltip("An attacker who cannot close in this long gives the token back, so the others are not left circling.")]
        public float engageTimeout = 2.5f;

        public Mode CurrentMode { get; private set; }

        Combatant self;
        NavMeshAgent agent;
        CharacterMotor motor;
        Animator anim;
        Combatant player;

        Vector3 velocity;
        float circleDir = 1f;
        float circleSwitchAt;
        float nextAttackThink;
        float recoverUntil;
        float blockUntil;
        bool comboQueued;
        float reactAt = -1f;
        bool reactBlock;
        Combatant.State lastPlayerState;
        float engagedSince, lastSpacingFlip = -10f;

        // every live enemy brain, for spacing in the ring (no scene search per frame)
        static readonly System.Collections.Generic.List<EnemyAI> live = new System.Collections.Generic.List<EnemyAI>();
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetLive() { live.Clear(); }
        void OnEnable() { if (!live.Contains(this)) live.Add(this); }
        void OnDisable() { live.Remove(this); }

        void Awake()
        {
            self = GetComponent<Combatant>();
            agent = GetComponent<NavMeshAgent>();
            motor = GetComponent<CharacterMotor>();
            agent.updatePosition = false;
            agent.updateRotation = false;
            agent.autoBraking = true;
            circleDir = Random.value < 0.5f ? -1f : 1f;
            // C14: the level's invisible walls are for the player only; enemies walk in through the lane mouths
            var cc = GetComponent<CharacterController>();
            int blocker = LayerMask.NameToLayer("PlayerBlocker");
            if (cc != null && blocker >= 0) cc.excludeLayers |= 1 << blocker;
        }

        void Start()
        {
            anim = self.Anim;
            self.Died += OnDied;
        }

        void OnDied(HitData h)
        {
            if (agent != null && agent.isOnNavMesh) agent.isStopped = true;
            motor.SetLocomotionVelocity(Vector3.zero);
            enabled = false;
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

            if (agent.isOnNavMesh) agent.nextPosition = transform.position;

            Vector3 to = GK.Flat(player.transform.position - transform.position);
            float dist = to.magnitude;
            self.DesiredFacing = to;

            ReactToPlayer(dist, dt);

            if (self.CurrentState != Combatant.State.Free)
            {
                velocity = Vector3.zero;
                motor.SetLocomotionVelocity(Vector3.zero);
                Animate(dt, true);
                if (self.CurrentState == Combatant.State.Attacking) TryChainCombo();
                return;
            }

            self.SetBlock(Time.time < blockUntil);
            comboQueued = false;

            if (dist > noticeRange && !engaged) { Idle(dt); return; }

            if (Time.time < recoverUntil) { CurrentMode = Mode.Recover; Circle(to, dist, dt, -0.6f); return; }

            // a hero on the ground (knocked down, getting up): a standing swing passes over him and a blow on the get-up
            // is a free hit, so hold at the ring until he stands (lease L: every distant miss followed the spin's knockdown)
            if (player.CurrentState == Combatant.State.Knockdown)
            {
                if (CurrentMode == Mode.Engage) CombatDirector.I.ReleaseAttackToken(self);
                CurrentMode = Mode.Circle;
                Circle(to, dist, dt, -0.3f);
                return;
            }

            if (dist > circleRange + 1.5f)
            {
                CurrentMode = Mode.Approach;
                // a wave sent for a hold point takes the ground first; a player there (or close by) is fought at once
                bool forHold = holdPoint != null && GK.Flat(player.transform.position - holdPoint.position).magnitude > holdRadius
                               && dist > holdRadius;
                MoveAlongPath(forHold ? holdPoint.position : player.transform.position, runSpeed, dt);
                return;
            }

            // inside the ring: wait for a token, then commit
            if (Time.time >= nextAttackThink)
            {
                nextAttackThink = Time.time + Random.Range(0.25f, 0.7f) * (1.4f - aggression);
                if (CurrentMode != Mode.Engage && Random.value < aggression && CombatDirector.I.RequestAttackToken(self))
                {
                    CurrentMode = Mode.Engage;
                    engagedSince = Time.time;
                }
            }

            if (CurrentMode == Mode.Engage)
            {
                if (dist <= attackRange) StartAnAttack();
                else if (Time.time - engagedSince > engageTimeout)
                {
                    // a hero who keeps backing off is not chased forever with the only token: let another man try
                    CombatDirector.I.ReleaseAttackToken(self);
                    CurrentMode = Mode.Circle;
                    nextAttackThink = Time.time + 0.8f;
                    Circle(to, dist, dt, 0f);
                }
                else MoveAlongPath(player.transform.position, runSpeed, dt);
            }
            else
            {
                CurrentMode = Mode.Circle;
                Circle(to, dist, dt, 0f);
            }
        }

        void Idle(float dt)
        {
            CurrentMode = Mode.Idle;
            velocity = GK.Damp(velocity, Vector3.zero, 6f, dt);
            motor.SetLocomotionVelocity(velocity);
            self.SetBlock(false);
            Animate(dt, false);
        }

        void MoveAlongPath(Vector3 destination, float speed, float dt)
        {
            Vector3 dir;
            if (agent.isOnNavMesh)
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

        void Circle(Vector3 to, float dist, float dt, float bias)
        {
            if (Time.time > circleSwitchAt)
            {
                circleSwitchAt = Time.time + Random.Range(1.6f, 3.8f);
                if (Random.value < 0.45f) circleDir = -circleDir;
            }
            Vector3 fwd = to / Mathf.Max(dist, 0.01f);
            Vector3 side = Vector3.Cross(Vector3.up, fwd) * circleDir;
            float radial = Mathf.Clamp((dist - circleRange) * 0.8f, -1f, 1f) + bias;
            Vector3 dir = (side + fwd * radial + Spacing(ref side)).normalized;
            if (agent.isOnNavMesh)
            {
                NavMeshHit hit;
                Vector3 probe = transform.position + dir * 1.2f;
                if (!NavMesh.SamplePosition(probe, out hit, 0.6f, NavMesh.AllAreas)) { circleDir = -circleDir; dir = (-side + fwd * radial).normalized; }
            }
            Vector3 desired = dir * walkSpeed;
            velocity = Vector3.MoveTowards(velocity, desired, 8f * dt);
            motor.SetLocomotionVelocity(velocity);
            motor.FaceDirection(fwd, turnSharpness, dt);
            Animate(dt, true);
        }

        /// <summary>Keeps the waiting men apart in the ring: a push away from any brother within 2.4 m, and the one
        /// behind turns round rather than walking into the back of the one ahead (they no longer bunch up or overlap).</summary>
        Vector3 Spacing(ref Vector3 side)
        {
            Vector3 push = Vector3.zero;
            Vector3 me = transform.position;
            for (int i = 0; i < live.Count; i++)
            {
                var o = live[i];
                if (o == this || o == null || o.self == null || o.self.IsDead) continue;
                Vector3 away = GK.Flat(me - o.transform.position);
                float d = away.magnitude;
                if (d >= 2.4f || d < 0.001f) continue;
                push += away / d * (2.4f - d) / 2.4f;
                if (o.circleDir == circleDir && Vector3.Dot(side, -away) > 0f && Time.time - lastSpacingFlip > 1f)
                {
                    circleDir = -circleDir;
                    side = -side;
                    circleSwitchAt = Time.time + Random.Range(1.6f, 3.8f);
                    lastSpacingFlip = Time.time;
                }
            }
            return push * 1.4f;
        }

        AttackData lastThrown;

        void StartAnAttack()
        {
            MoveSet m = self.moves;
            if (m == null) return;
            AttackData a = null;
            // a raised guard is answered with the kick (the guard breaker) instead of a cut into the shield
            if (m.kick != null && player != null && player.IsBlocking && lastThrown != m.kick && Random.value < guardBreakChance)
                a = m.kick;
            else if (m.aiUnblockables != null && m.aiUnblockables.Length > 0 && Random.value < unblockableChance)
                a = m.aiUnblockables[Random.Range(0, m.aiUnblockables.Length)];
            else if (m.aiAttacks != null && m.aiAttacks.Length > 0)
            {
                // never two blows the same way in a row (two downward cuts read as one move repeated)
                a = m.aiAttacks[Random.Range(0, m.aiAttacks.Length)];
                for (int i = 0; i < 6 && m.aiAttacks.Length > 1 && AttackData.SameWay(a, lastThrown); i++) a = m.aiAttacks[Random.Range(0, m.aiAttacks.Length)];
            }
            else a = m.lightStart;
            if (a != null && self.StartAttack(a))
            {
                lastThrown = a;
                comboQueued = Random.value < comboChance;
                recoverUntil = Time.time + Random.Range(0.6f, 1.4f);
                CurrentMode = Mode.Recover;
            }
            else
            {
                // a blow that could not start (out of stamina, mid-reaction) gives the token back and steps out of Engage:
                // left in it, the brain re-rolled every frame until a cheap move went through, and the kick and the
                // unblockables came up far more often than their chances (duelqa: 5 kicks of 15 blows at 12 %)
                CombatDirector.I.ReleaseAttackToken(self);
                CurrentMode = Mode.Circle;
                nextAttackThink = Time.time + 0.5f;
            }
        }

        void TryChainCombo()
        {
            if (!comboQueued) return;
            AttackData cur = self.CurrentAttack;
            if (cur == null || cur.nextLight == null) return;
            if (player != null && player.CurrentState == Combatant.State.Knockdown) { comboQueued = false; return; }
            // the hero stepped out of reach: the next cut is not thrown into the air
            if (player != null && GK.Flat(player.transform.position - transform.position).magnitude > attackRange + 0.8f) { comboQueued = false; return; }
            if (self.CanAttackNow)
            {
                comboQueued = Random.value < comboChance * 0.6f;
                self.StartAttack(cur.nextLight);
            }
        }

        /// <summary>Sees the player's wind-up and decides (after a human-like delay) to block or dodge.</summary>
        void ReactToPlayer(float dist, float dt)
        {
            bool playerWinding = player.IsWindingUp && dist < 3.5f;
            if (playerWinding && lastPlayerState != Combatant.State.Attacking)
            {
                float r = Random.value;
                if (r < blockSkill) { reactAt = Time.time + reactionTime; reactBlock = true; }
                else if (r < blockSkill + dodgeSkill) { reactAt = Time.time + reactionTime; reactBlock = false; }
            }
            lastPlayerState = player.CurrentState;

            if (reactAt > 0f && Time.time >= reactAt)
            {
                reactAt = -1f;
                if (reactBlock) blockUntil = Time.time + Random.Range(0.6f, 1.2f);
                else self.RequestDodge(-(player.transform.position - transform.position) + transform.right * (Random.value < 0.5f ? -1f : 1f));
            }
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
