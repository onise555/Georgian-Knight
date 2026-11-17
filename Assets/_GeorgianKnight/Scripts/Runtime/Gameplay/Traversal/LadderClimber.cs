using GeorgianKnight.Core;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Ladder climbing for the player.
    ///
    /// Flow: near a ladder's foot (facing it) or at its top the HUD offers "climb"; the interact button starts it. A
    /// drawn sword is sheathed first (both hands are needed) and the climber walks into the exact start spot. The mount
    /// clip then plays, the climb loop follows the stick forward or backward, and an exit clip lands the climber on the
    /// upper surface or on the ground. Dodge lets go.
    ///
    /// How it stays exact: the ladder clips have all their motion baked into the pose (the body climbs inside the clip,
    /// the transform stays put), and the climber drives every clip's time itself. So it always knows precisely which
    /// frame is on screen and where every hand and foot is (<see cref="ClimbProfile"/>, measured on this avatar):
    ///   * loop: the transform steps up by exactly one cycle's rise in the very frame the clip wraps — the body never
    ///     moves in the world, it only climbs, and nothing slides;
    ///   * transitions: the transform moves only by the small correction that lands the body exactly on the next pose
    ///     (rungs, roof, ground), spread over the clip, while the body tilts with the ladder;
    ///   * hands and feet are held on the rungs by IK while they grip;
    ///   * the pose inertializer is told about every transform step so it never mistakes one for a pose jump.
    /// </summary>
    [RequireComponent(typeof(Combatant))]
    [DefaultExecutionOrder(-60)]
    public class LadderClimber : MonoBehaviour
    {
        public const string S_Mount = "Ladder_Mount", S_Climb = "Ladder_Climb", S_ExitTop = "Ladder_ExitTop", S_ExitBottom = "Ladder_ExitBottom";
        public static readonly int P_ClimbSpeed = Animator.StringToHash("ClimbSpeed");

        public ClimbProfile profile;

        [Header("Mounting")]
        public float mountRange = 0.9f;
        [Tooltip("Max angle between the player's facing and the ladder for a bottom mount.")]
        public float mountAngle = 75f;
        public float walkSpeed = 1.1f;
        public float turnSharpness = 10f;

        [Header("Climbing")]
        [Tooltip("Loop playback rate at full stick.")]
        public float climbRate = 1f;
        public float sprintRate = 1.35f;
        public float rateSharpness = 7f;
        [Tooltip("Hands/feet count as gripping while they move slower than this along the rails (m/s).")]
        public float gripSpeed = 0.32f;
        [Tooltip("An effector further than this from its rung is never pulled onto it (it is reaching).")]
        public float gripReach = 0.10f;

        public enum Phase { None, Sheathing, Aligning, Mounting, Climbing, Exiting }
        public Phase Current { get; private set; }
        public Ladder Ladder { get; private set; }
        public bool IsClimbing { get { return Current != Phase.None; } }
        /// <summary>Context prompt for the HUD ("" when nothing is offered).</summary>
        public string PromptText { get; private set; }
        /// <summary>Rail coordinate of the climbing transform (metres from the ladder's foot along the rails).</summary>
        public float Along { get; private set; }
        public float Rate { get { return rate; } }
        /// <summary>QA: largest IK pull applied this frame (metres), the loop's rung-lattice error, the last warp size.</summary>
        public float LastIkPull { get; private set; }
        public float LastPhaseError { get; private set; }
        public float LastWarp { get; private set; }

        Combatant self;
        Animator anim;
        CharacterMotor motor;
        GKInput input;
        PlayerController player;
        PoseInertializer inertia;
        IGameStateMachine states;
        Transform hips;
        float restHips = 0.9f;

        enum Mode { Bottom, Top }
        Mode mode;
        float phaseTimer;

        // transition (time driven here; the transform carries only the correction)
        ClimbProfile.ClipData tClip;
        string tState;
        float tTime, tFrom, tTo;                 // normalized
        Vector3 tP0, tCorr;
        Quaternion tR0, tR1;
        bool tToLoop;
        float tLoopPhase;                        // loop phase to continue from when the transition ends on the ladder
        int tLoopDir;
        float tLoopAlong;
        Vector3 alignPos; Quaternion alignRot;

        // loop
        float u;                                 // unwrapped loop time (cycles)
        float baseAlong;                         // rail coordinate of the transform at u in [0, 1)
        float rate, restPulse;
        int lastDir = 1;
        bool crossTop, crossBottom;

        // IK
        readonly float[] prevAlong = new float[4];
        readonly float[] ikW = new float[4];
        bool ikPrimed;
        static readonly AvatarIKGoal[] Goals = { AvatarIKGoal.LeftHand, AvatarIKGoal.RightHand, AvatarIKGoal.LeftFoot, AvatarIKGoal.RightFoot };

        float K { get { return profile != null && profile.humanScale > 1e-4f && anim != null ? anim.humanScale / profile.humanScale : 1f; } }
        float Pitch { get { return Ladder != null ? Ladder.Pitch : 0.23f; } }

        void Awake()
        {
            self = GetComponent<Combatant>();
            motor = GetComponent<CharacterMotor>();
            input = GetComponent<GKInput>();
            player = GetComponent<PlayerController>();
            inertia = GetComponent<PoseInertializer>();
            Services.TryGet(out states);
            PromptText = "";
        }

        void Start()
        {
            anim = self.Anim;
            if (anim != null && anim.isHuman)
            {
                hips = anim.GetBoneTransform(HumanBodyBones.Hips);
                if (hips != null) restHips = Mathf.Clamp(hips.position.y - transform.position.y, 0.6f, 1.2f);
            }
        }

        bool Live { get { return states == null || states.InGameplay; } }

        /// <summary>Where the camera should anchor while climbing: the body, not the transform (which steps with the
        /// loop and sits wherever the clip's own space puts it).</summary>
        public Vector3 CameraAnchor(float pivotHeight)
        {
            if (hips == null) return transform.position + Vector3.up * pivotHeight;
            return hips.position + Vector3.up * (pivotHeight - restHips);
        }

        // ------------------------------------------------------------------ frame

        void Update()
        {
            if (anim == null) anim = self.Anim;
            if (profile == null || anim == null) { PromptText = ""; return; }
            float dt = Time.deltaTime * anim.speed;          // hit-stop freezes the clips too

            if (self.IsDead)
            {
                if (Current != Phase.None) Release(false);
                PromptText = "";
                return;
            }

            switch (Current)
            {
                case Phase.None: Detect(); break;
                case Phase.Sheathing: TickSheathing(Time.deltaTime); break;
                case Phase.Aligning: TickAligning(Time.deltaTime); break;
                case Phase.Mounting:
                case Phase.Exiting: TickTransition(dt); break;
                case Phase.Climbing: TickClimbing(dt); break;
            }
        }

        void Detect()
        {
            PromptText = "";
            if (!Live || self.CurrentState != Combatant.State.Free || (motor != null && !motor.IsGrounded)) return;
            Ladder best = null; Mode bestMode = Mode.Bottom;
            float bestD = float.MaxValue;
            Vector3 p = transform.position;
            for (int i = 0; i < Ladder.All.Count; i++)
            {
                var l = Ladder.All[i];
                if (l == null || !l.IsValid) continue;
                Vector3 fwd = GK.Flat(l.Forward).normalized;
                Vector3 b = l.standBottom.position;
                float db = GK.Flat(p - b).magnitude;
                if (db < mountRange && Mathf.Abs(p.y - b.y) < 0.6f && Vector3.Angle(GK.Flat(transform.forward), fwd) < mountAngle && db < bestD)
                { bestD = db; best = l; bestMode = Mode.Bottom; }
                Vector3 t = l.standTop.position;
                float dtp = GK.Flat(p - t).magnitude;
                if (dtp < mountRange * 1.2f && Mathf.Abs(p.y - t.y) < 0.6f && dtp < bestD)
                { bestD = dtp; best = l; bestMode = Mode.Top; }
            }
            if (best == null) return;
            PromptText = bestMode == Mode.Bottom ? "E — კიბეზე ასვლა" : "E — კიბით ჩასვლა";
            if (input.InteractPressed) Begin(best, bestMode);
        }

        /// <summary>Starts climbing <paramref name="l"/> from its foot (or its top). Returns false if the body is busy.</summary>
        public bool BeginFromBottom(Ladder l) { return l != null && l.IsValid && Begin(l, Mode.Bottom); }
        public bool BeginFromTop(Ladder l) { return l != null && l.IsValid && Begin(l, Mode.Top); }

        bool Begin(Ladder l, Mode m)
        {
            if (Current != Phase.None || self.CurrentState != Combatant.State.Free || !profile.climb.IsValid) return false;
            if (m == Mode.Bottom && !profile.mount.IsValid) return false;
            if (m == Mode.Top && !profile.exitTop.IsValid) return false;
            Ladder = l; mode = m; PromptText = "";
            phaseTimer = 0f;
            var h = self.Holster;
            if (h != null && (h.IsDrawn || h.IsBusy))
            {
                if (!h.IsBusy) h.Sheathe();
                if (player != null) player.AddInputLock(this);
                Current = Phase.Sheathing;
                return true;
            }
            StartAligning();
            return true;
        }

        void TickSheathing(float dt)
        {
            phaseTimer += dt;
            var h = self.Holster;
            bool done = h == null || (!h.IsBusy && !h.IsDrawn);
            if (!done && phaseTimer < 3f) return;
            if (player != null) player.RemoveInputLock(this);
            if (!done || self.CurrentState != Combatant.State.Free) { Abort(); return; }
            StartAligning();
        }

        // ------------------------------------------------------------------ ladder frame helpers

        /// <summary>Transform position on the ladder for rail coordinate <paramref name="along"/>: the loop's clip space
        /// is placed so its rung plane is the ladder's and the climber is centred between the rails.</summary>
        Vector3 LoopPos(float along)
        {
            float k = K;
            return Ladder.foot.position + Ladder.Up * along - Ladder.Forward * (profile.planeZ * k) - Ladder.Right * (profile.centreX * k);
        }

        /// <summary>The rail coordinate nearest <paramref name="guess"/> at which the loop's clip-space rungs coincide with
        /// the ladder's rungs (the loop was captured on rungs <see cref="ClimbProfile.rungPitch"/> apart).</summary>
        float AlignAlong(float guess)
        {
            float k = K, p = Pitch;
            float target = Ladder.RungAlong(0) - profile.rungOffset * k;     // transform along that puts a clip rung on rung 0
            float n = Mathf.Round((guess - target) / p);
            return target + n * p;
        }

        Vector3 Centroid(ClimbProfile.ClipData c, float n)
        {
            return (c.LeftHandAt(n) + c.RightHandAt(n) + c.LeftFootAt(n) + c.RightFootAt(n)) * 0.25f * K;
        }

        Vector3 FeetMid(ClimbProfile.ClipData c, float n)
        {
            return (c.LeftFootAt(n) + c.RightFootAt(n)) * 0.5f * K;
        }

        /// <summary>Root rotation that makes a clip whose body faces <see cref="ClimbProfile.ClipData.yaw"/> in its own
        /// space face <paramref name="body"/> in the world.</summary>
        static Quaternion RootRot(Quaternion body, ClimbProfile.ClipData clip)
        {
            return clip != null && Mathf.Abs(clip.yaw) > 0.01f ? body * Quaternion.Euler(0f, -clip.yaw, 0f) : body;
        }

        void MoveRoot(Vector3 pos, Quaternion rot)
        {
            // a step of the transform while the body must stay put: keep the inertializer's memory in the new frame
            if (inertia != null) inertia.BeginRootMove();
            transform.SetPositionAndRotation(pos, rot);
            if (inertia != null) inertia.EndRootMove();
        }

        // ------------------------------------------------------------------ mount

        void StartAligning()
        {
            if (!self.BeginTraversal(this)) { Abort(); return; }
            Current = Phase.Aligning;
            phaseTimer = 0f;
            Quaternion upright = Quaternion.LookRotation(GK.Flat(Ladder.Forward).normalized, Vector3.up);
            if (mode == Mode.Bottom)
            {
                // the mount clip ends in the loop pose at mountEndPhase; stand where that pose lands on the rungs
                Quaternion rl = Ladder.ClimbRotation;
                Vector3 ground = Ladder.standBottom.position;
                Vector3 bodyEnd = ground + upright * Centroid(profile.mount, 1f);
                float guess = Ladder.Along(bodyEnd - rl * Centroid(profile.climb, profile.mountEndPhase) + Ladder.Forward * (profile.planeZ * K));
                tLoopAlong = AlignAlong(guess);
                Vector3 loopT = LoopPos(tLoopAlong);
                Vector3 end = loopT + rl * Centroid(profile.climb, profile.mountEndPhase) - upright * Centroid(profile.mount, 1f);
                alignPos = end; alignPos.y = transform.position.y;
                alignRot = upright;
            }
            else
            {
                // at the top the climber turns its back to the edge (it goes down facing the ladder) and stands where
                // the top exit clip ends; that clip then plays backwards onto the rungs
                alignRot = upright;
                alignPos = TopStandRoot(upright); alignPos.y = transform.position.y;
            }
        }

        /// <summary>Transform position at the end of the top exit clip so that its feet stand on the exit point.</summary>
        Vector3 TopStandRoot(Quaternion upright)
        {
            Vector3 feet = FeetMid(profile.exitTop, 1f);
            Vector3 p = Ladder.standTop.position - upright * new Vector3(feet.x, 0f, feet.z);
            p.y = Ladder.standTop.position.y - (feet.y - profile.ankleHeight * K);
            return p;
        }

        void TickAligning(float dt)
        {
            phaseTimer += dt;
            Vector3 to = GK.Flat(alignPos - transform.position);
            float dist = to.magnitude;
            float step = Mathf.Min(dist, walkSpeed * Mathf.Clamp01(dist / 0.25f + 0.2f) * dt);
            Vector3 move = dist > 1e-4f ? to / dist * step : Vector3.zero;
            if (motor != null && motor.Controller != null && motor.Controller.enabled) motor.Controller.Move(move);
            else transform.position += move;
            Quaternion want = dist > 0.2f ? Quaternion.LookRotation(to.normalized, Vector3.up) : alignRot;
            transform.rotation = GK.Damp(transform.rotation, want, turnSharpness, dt);
            anim.SetFloat(GK.P_Speed, dt > 0f ? step / dt : 0f, 0.08f, dt);
            anim.SetFloat(GK.P_Strafe, 0f);
            bool there = dist < 0.02f && Quaternion.Angle(transform.rotation, alignRot) < 3f;
            if (!there && phaseTimer < 2.5f) return;
            anim.SetFloat(GK.P_Speed, 0f);
            StartMount();
        }

        void StartMount()
        {
            if (motor != null && motor.Controller != null) motor.Controller.enabled = false;   // the path is designed; no capsule snagging
            Quaternion r0 = alignRot;
            Quaternion rl = Ladder.ClimbRotation;
            anim.SetFloat(P_ClimbSpeed, 0f);
            if (mode == Mode.Bottom)
            {
                // the transform stays on the ground while the body climbs inside the clip; it moves only by the error
                Vector3 end = LoopPos(tLoopAlong) + rl * Centroid(profile.climb, profile.mountEndPhase) - rl * Centroid(profile.mount, 1f);
                BeginTransition(profile.mount, 0f, 1f, transform.position, r0, end, rl, true, profile.mountEndPhase, 1);
            }
            else
            {
                // backwards through the top exit: from standing at the top to the loop pose at exitTopPhase on the rungs
                Vector3 standRoot = TopStandRoot(r0);
                MoveRoot(new Vector3(standRoot.x, transform.position.y, standRoot.z), transform.rotation);
                Vector3 bodyStart = transform.position + rl * Centroid(profile.exitTop, 0f);
                float guess = Ladder.Along(bodyStart - rl * Centroid(profile.climb, profile.exitTopPhase) + Ladder.Forward * (profile.planeZ * K));
                tLoopAlong = AlignAlong(guess);
                Vector3 end = LoopPos(tLoopAlong) + rl * Centroid(profile.climb, profile.exitTopPhase) - rl * Centroid(profile.exitTop, 0f);
                BeginTransition(profile.exitTop, 1f, 0f, transform.position, r0, end, rl, true, profile.exitTopPhase, -1);
            }
            Current = Phase.Mounting;
            ikPrimed = false;
        }

        void BeginTransition(ClimbProfile.ClipData clip, float from, float to, Vector3 p0, Quaternion r0, Vector3 p1, Quaternion r1,
                             bool toLoop, float loopPhase, int loopDir)
        {
            tClip = clip; tState = clip.state; tFrom = from; tTo = to; tTime = from;
            tP0 = p0; tR0 = r0; tR1 = r1; tCorr = p1 - p0;
            tToLoop = toLoop; tLoopPhase = loopPhase; tLoopDir = loopDir;
            LastWarp = tCorr.magnitude;
            MoveRoot(p0, RootRot(r0, clip));                        // the clip's own facing (the body stays put)
            self.PlayTraversal(tState, 0.2f, Mathf.Clamp01(from) * clip.length * 0.999f);
        }

        void TickTransition(float dt)
        {
            if (tClip == null || tClip.length <= 0f) { Release(true); return; }
            float dir = Mathf.Sign(tTo - tFrom);
            tTime += dir * dt / tClip.length;
            float q = Mathf.Clamp01((tTime - tFrom) / (tTo - tFrom));
            float tt = Mathf.Clamp01(tTime);
            anim.Play(tState, 0, Mathf.Min(tt, 0.9999f));
            // rotation turns early (the hands find the rails first), the correction is spread over the whole clip
            float qr = Smooth(Mathf.Clamp01(q / 0.6f)), qc = Smooth(q);
            transform.SetPositionAndRotation(tP0 + tCorr * qc, RootRot(Quaternion.Slerp(tR0, tR1, qr), tClip));
            if (Ladder != null) Along = Ladder.Along(transform.position);
            if (q < 1f) return;
            if (tToLoop) EnterLoop(tLoopPhase, tLoopDir, tLoopAlong);
            else Release(true);
        }

        // ------------------------------------------------------------------ climbing

        void EnterLoop(float phase, int dir, float along)
        {
            Current = Phase.Climbing;
            rate = 0f;
            lastDir = dir;
            u = phase;
            baseAlong = along;
            Along = along;
            MoveRoot(LoopPos(along), RootRot(Ladder.ClimbRotation, profile.climb));
            self.PlayTraversal(S_Climb, 0.12f, phase * profile.climb.length);
        }

        void TickClimbing(float dt)
        {
            Vector2 mv = Live ? input.Move : Vector2.zero;
            if (Live && input.DodgePressed) { LetGo(); return; }
            float want = Mathf.Abs(mv.y) > 0.15f ? Mathf.Sign(mv.y) * (input.SprintHeld ? sprintRate : climbRate) * Mathf.Clamp01(Mathf.Abs(mv.y) * 1.2f) : 0f;
            float phase = Frac(u);
            if (want == 0f)
            {
                // never freeze mid-reach: finish the move to the next pose where both hands grip
                float to = NextRest(phase, lastDir);
                float dist = lastDir > 0 ? Frac(to - phase) : Frac(phase - to);
                if (dist > 0.012f && dist < 0.985f) want = lastDir * 0.7f;
            }
            else lastDir = want > 0f ? 1 : -1;

            // ends of the ladder: hands never pass the top rung, feet never the bottom one
            float top, bottom;
            Limits(out top, out bottom);
            float curAlong = baseAlong + Mathf.Floor(u) * profile.climbRise * K;
            if ((curAlong >= top && want > 0f) || (curAlong <= bottom && want < 0f)) want = 0f;

            rate = GK.Damp(rate, want, rateSharpness, dt > 0f ? dt / Mathf.Max(0.01f, anim.speed) : 0f);
            if (want == 0f && Mathf.Abs(rate) < 0.08f) rate = 0f;
            float speed = rate;
            if (rate == 0f) { restPulse += dt; speed = 0.03f * Mathf.Cos(restPulse * 1.9f); }     // breathing on the rungs
            else restPulse = 0f;

            float uPrev = u;
            u += speed * dt / profile.climb.length;
            crossTop = Mathf.Floor(u - profile.exitTopPhase) > Mathf.Floor(uPrev - profile.exitTopPhase);
            crossBottom = Mathf.Floor(uPrev - profile.exitBottomPhase) > Mathf.Floor(u - profile.exitBottomPhase);

            // phase lock: drift so the gripping hands sit on their rungs (removes capture error; never visible)
            baseAlong -= Mathf.Clamp(LastPhaseError * 3f * Time.deltaTime, -0.06f * Time.deltaTime, 0.06f * Time.deltaTime);
            // the transform steps by one cycle's rise exactly when the clip wraps; the pose wraps the same frame
            float along = baseAlong + Mathf.Floor(u) * profile.climbRise * K;
            Along = along;
            anim.Play(S_Climb, 0, Frac(u));
            MoveRoot(LoopPos(along), RootRot(Ladder.ClimbRotation, profile.climb));

            if (rate > 0.2f && crossTop && profile.exitTop.IsValid && along >= ExitTopAlong() - Pitch * 1.01f) { StartExitTop(); return; }
            if (rate < -0.2f && crossBottom && profile.exitBottom.IsValid && along <= ExitBottomAlong() + Pitch * 1.01f) { StartExitBottom(); return; }
        }

        void Limits(out float top, out float bottom)
        {
            float k = K;
            var c = profile.climb;
            float maxHand = float.MinValue, minFoot = float.MaxValue;
            for (int i = 0; i < c.leftHand.Length; i++) maxHand = Mathf.Max(maxHand, Mathf.Max(c.leftHand[i].y, c.rightHand[i].y));
            for (int i = 0; i < c.leftFoot.Length; i++) minFoot = Mathf.Min(minFoot, Mathf.Min(c.leftFoot[i].y, c.rightFoot[i].y));
            top = Ladder.RungAlong(Ladder.rungCount - 1) + profile.gripUp * k - maxHand * k;
            bottom = Ladder.RungAlong(0) + profile.footUp * k - minFoot * k;
        }

        float NextRest(float phase, int dir)
        {
            float best = 0f, bestD = 2f;
            var rp = profile.restPhases;
            for (int i = 0; i < rp.Length; i++)
            {
                float d = dir > 0 ? Frac(rp[i] - phase) : Frac(phase - rp[i]);
                if (d < bestD) { bestD = d; best = rp[i]; }
            }
            return best;
        }

        /// <summary>Loop rail coordinate at which the top exit clip, started at exitTopPhase, lands its feet on the
        /// upper surface with no vertical correction (the ladder is built so a cycle lands there).</summary>
        float ExitTopAlong()
        {
            Quaternion rl = Ladder.ClimbRotation;
            Quaternion up = Quaternion.LookRotation(GK.Flat(Ladder.Forward).normalized, Vector3.up);
            // transform at the exit start (continuity with the loop pose) -> its natural end height
            Vector3 rel = rl * (Centroid(profile.climb, profile.exitTopPhase) - Centroid(profile.exitTop, 0f));
            Vector3 feetEnd = up * FeetMid(profile.exitTop, 1f);
            float upY = Mathf.Max(0.2f, Ladder.Up.y);
            // y(transform at exit end, uncorrected) = LoopPos(a).y + rel.y ; feet = that + feetEnd.y - ankle
            Vector3 l0 = LoopPos(0f);
            return (Ladder.TopHeight + profile.ankleHeight * K - feetEnd.y - rel.y - l0.y) / upY;
        }

        float ExitBottomAlong()
        {
            Quaternion rl = Ladder.ClimbRotation;
            Quaternion up = Quaternion.LookRotation(GK.Flat(Ladder.Forward).normalized, Vector3.up);
            Vector3 rel = rl * (Centroid(profile.climb, profile.exitBottomPhase) - Centroid(profile.exitBottom, 0f));
            Vector3 feetEnd = up * FeetMid(profile.exitBottom, 1f);
            float upY = Mathf.Max(0.2f, Ladder.Up.y);
            Vector3 l0 = LoopPos(0f);
            return (Ladder.standBottom.position.y + profile.ankleHeight * K - feetEnd.y - rel.y - l0.y) / upY;
        }

        void StartExitTop()
        {
            Quaternion rl = Ladder.ClimbRotation;
            Quaternion up = Quaternion.LookRotation(GK.Flat(Ladder.Forward).normalized, Vector3.up);
            // jump the transform so the exit clip's first pose continues the loop pose exactly
            Vector3 start = transform.position + rl * (Centroid(profile.climb, profile.exitTopPhase) - Centroid(profile.exitTop, 0f));
            BeginTransition(profile.exitTop, 0f, 1f, start, rl, TopStandRoot(up), up, false, 0f, 0);
            Current = Phase.Exiting;
        }

        void StartExitBottom()
        {
            Quaternion rl = Ladder.ClimbRotation;
            Quaternion up = Quaternion.LookRotation(GK.Flat(Ladder.Forward).normalized, Vector3.up);
            Vector3 start = transform.position + rl * (Centroid(profile.climb, profile.exitBottomPhase) - Centroid(profile.exitBottom, 0f));
            Vector3 feet = FeetMid(profile.exitBottom, 1f);
            Vector3 end = start;                                  // the clip steps back by itself; only the height is fitted
            end.y = Ladder.standBottom.position.y - (feet.y - profile.ankleHeight * K);
            BeginTransition(profile.exitBottom, 0f, 1f, start, rl, end, up, false, 0f, 0);
            Current = Phase.Exiting;
        }

        void LetGo()
        {
            // push off backward and drop: back on the feet basis, the motor takes over with gravity
            Release(true);
            if (motor != null) motor.AddPush(-GK.Flat(Ladder != null ? Ladder.Forward : transform.forward).normalized * 1.6f);
        }

        // ------------------------------------------------------------------ IK

        void OnAnimatorIK(int layer)
        {
            LastIkPull = 0f;
            if (layer != 0 || anim == null || Ladder == null || profile == null) return;
            bool active = Current == Phase.Mounting || Current == Phase.Climbing || Current == Phase.Exiting;
            if (!active) { ikPrimed = false; return; }
            float dt = Mathf.Max(1e-4f, Time.deltaTime);
            float k = K, p = Pitch;
            float first = Ladder.RungAlong(0);
            Vector3 up = Ladder.Up, fwd = Ladder.Forward, right = Ladder.Right;
            float errSum = 0f; int errN = 0;
            for (int i = 0; i < 4; i++)
            {
                bool hand = i < 2;
                Vector3 pos = anim.GetIKPosition(Goals[i]);
                float a = Ladder.Along(pos);
                float v = ikPrimed ? Mathf.Abs(a - prevAlong[i]) / dt : 99f;
                prevAlong[i] = a;
                float lift = (hand ? profile.gripUp : profile.footUp) * k;
                float back = (hand ? profile.gripBack : profile.footBack) * k;
                int r = Mathf.Clamp(Mathf.RoundToInt((a - lift - first) / p), 0, Ladder.rungCount - 1);
                float err = a - lift - (first + r * p);
                bool grip = v < gripSpeed && Mathf.Abs(err) < gripReach * k;
                ikW[i] = Mathf.MoveTowards(ikW[i], grip ? 1f : 0f, dt * (grip ? 7f : 10f));
                if (hand && grip && Current == Phase.Climbing) { errSum += err; errN++; }
                if (ikW[i] <= 0.001f) { anim.SetIKPositionWeight(Goals[i], 0f); continue; }
                float lateral = Mathf.Clamp(Vector3.Dot(pos - Ladder.RungCenter(r), right), -(Ladder.halfWidth - 0.07f), Ladder.halfWidth - 0.07f);
                Vector3 target = Ladder.RungCenter(r) + up * lift - fwd * back + right * lateral;
                anim.SetIKPosition(Goals[i], target);
                anim.SetIKPositionWeight(Goals[i], ikW[i]);
                LastIkPull = Mathf.Max(LastIkPull, Vector3.Distance(target, pos) * ikW[i]);
            }
            LastPhaseError = errN > 0 ? errSum / errN : 0f;
            ikPrimed = true;
        }

        // ------------------------------------------------------------------ end

        void Release(bool controllerOn)
        {
            if (player != null) player.RemoveInputLock(this);
            if (anim != null)
            {
                anim.SetFloat(P_ClimbSpeed, 1f);
                for (int i = 0; i < 4; i++) { ikW[i] = 0f; anim.SetIKPositionWeight(Goals[i], 0f); }
            }
            if (!self.IsDead)
            {
                // back to the feet basis of every other clip: the transform goes under the feet, upright; the pose
                // inertializer blends the clip change, the transform step is announced so it causes no blend
                Vector3 feet = anim != null ? (anim.GetBoneTransform(HumanBodyBones.LeftFoot).position + anim.GetBoneTransform(HumanBodyBones.RightFoot).position) * 0.5f : transform.position;
                Vector3 fwd = GK.Flat(transform.forward);
                Quaternion upright = Quaternion.LookRotation(fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward, Vector3.up);
                Vector3 ground = new Vector3(feet.x, feet.y - (profile != null ? profile.ankleHeight * K : 0.09f), feet.z);
                MoveRoot(ground, upright);
                if (controllerOn && motor != null && motor.Controller != null) motor.Controller.enabled = true;
                self.EndTraversal(this, true);
            }
            Current = Phase.None;
            Ladder = null;
            ikPrimed = false;
        }

        void Abort()
        {
            if (player != null) player.RemoveInputLock(this);
            if (self.InTraversal) self.EndTraversal(this, true);
            if (motor != null && motor.Controller != null) motor.Controller.enabled = true;
            Current = Phase.None;
            Ladder = null;
        }

        void OnDisable()
        {
            if (Current != Phase.None) Release(true);
        }

        static float Smooth(float x) { return x * x * (3f - 2f * x); }
        static float Frac(float x) { return x - Mathf.Floor(x); }
    }
}
