using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>Test driver: lets the knight fight on his own (used for automated play-tests and captures). He draws,
    /// closes in, chains lights and heavies, kicks now and then, raises the guard when the nearest enemy winds up and
    /// sometimes dodges instead. Counts every swing and whether it touched anyone, so a play-test shows the swings
    /// that cut the air with an enemy in reach.</summary>
    public class GKAutoPilot : MonoBehaviour
    {
        Combatant self;
        CharacterMotor motor;
        PlayerController pc;
        Animator anim;
        float nextAction, blockUntil;
        int comboLeft;

        // swing ledger
        public int swings, connected, whiffsInReach, whiffsOutOfReach, interrupted, blocksRaised, kicks;
        public readonly System.Text.StringBuilder whiffLog = new System.Text.StringBuilder();
        AttackData openSwing; bool openHit; float openDist, hitDist; int hitState; Combatant openTarget;   // hitState: -1 dead
        // the log's words, made once (the GC bar is measured with the autopilot running: GK-26)
        static string[] stateNames;
        static readonly System.Collections.Generic.Dictionary<AttackData, string> attackNames = new System.Collections.Generic.Dictionary<AttackData, string>();

        void Start()
        {
            self = GetComponent<Combatant>();
            motor = GetComponent<CharacterMotor>();
            pc = GetComponent<PlayerController>();
            anim = GetComponent<Animator>();
            if (stateNames == null)
            {
                var values = (Combatant.State[])System.Enum.GetValues(typeof(Combatant.State));
                int max = 0; foreach (var v in values) max = Mathf.Max(max, (int)v);
                stateNames = new string[max + 1];
                foreach (var v in values) stateNames[(int)v] = v.ToString();
            }
            if (pc != null) pc.enabled = false;
            self.AttackStarted += OnAttack;
            self.HitDealt += OnDealt;
        }

        void OnDestroy()
        {
            if (pc != null) pc.enabled = true;
            if (self != null) { self.AttackStarted -= OnAttack; self.HitDealt -= OnDealt; self.SetBlock(false); }
        }

        void OnAttack(AttackData a)
        {
            CloseSwing();
            openSwing = a; openHit = false; openTarget = self.Target; hitDist = -1f; hitState = -1;
            openDist = openTarget != null ? GK.Flat(openTarget.transform.position - transform.position).magnitude : -1f;
            swings++;
            if (a.kind == AttackKind.Kick) kicks++;
        }

        void OnDealt(Combatant t, HitData h, HitResult r) { if (openSwing != null) openHit = true; }

        void CloseSwing()
        {
            if (openSwing == null) return;
            if (openHit) connected++;
            // cut short before its blade opened (a blow taken, a stagger, a dodge): not a miss, it never swung
            else if (hitDist < 0f) interrupted++;
            else
            {
                // in reach: the enemy stood within the move's strike range when it began
                float reach = openSwing.idealRange + (openTarget != null ? openTarget.bodyRadius : 0.4f) + 0.6f;
                bool inReach = openTarget != null && !openTarget.IsDead && openDist >= 0f && openDist <= reach;
                if (inReach)
                {
                    whiffsInReach++;
                    if (whiffLog.Length < 3000)
                    {
                        string n;
                        if (!attackNames.TryGetValue(openSwing, out n)) { n = openSwing.name; attackNames[openSwing] = n; }
                        whiffLog.Append(n).Append(' ');
                        AppendF2(whiffLog, openDist); whiffLog.Append('>'); AppendF2(whiffLog, hitDist);
                        whiffLog.Append("m ").Append(hitState < 0 ? "dead" : stateNames[hitState]).Append("; ");
                    }
                }
                else whiffsOutOfReach++;
            }
            openSwing = null;
        }

        /// <summary>v with two decimals ("1.23"), digit by digit: float.ToString allocates.</summary>
        static void AppendF2(System.Text.StringBuilder sb, float v)
        {
            int c = Mathf.RoundToInt(v * 100f);
            if (c < 0) { sb.Append('-'); c = -c; }
            int whole = c / 100, div = 1;
            while (div * 10 <= whole) div *= 10;
            for (; div > 0; div /= 10) sb.Append((char)('0' + whole / div % 10));
            sb.Append('.').Append((char)('0' + c / 10 % 10)).Append((char)('0' + c % 10));
        }

        public string Report()
        {
            CloseSwing();
            return "autopilot: " + swings + " swings (" + kicks + " kicks), " + connected + " connected, " + whiffsInReach +
                   " missed with the enemy in reach, " + interrupted + " cut short before the blade opened (hit / staggered), " + whiffsOutOfReach + " missed out of reach (target moved / died), " +
                   blocksRaised + " guards raised | in-reach misses: " + whiffLog;
        }

        void Update()
        {
            if (self == null || self.IsDead) return;
            if (openSwing != null && self.CurrentState != Combatant.State.Attacking) CloseSwing();
            // where the enemy is, and what he does, when the blade opens
            if (openSwing != null && hitDist < 0f && openTarget != null && self.ActionProgress >= openSwing.hitStart)
            {
                hitDist = GK.Flat(openTarget.transform.position - transform.position).magnitude;
                hitState = openTarget.IsDead ? -1 : (int)openTarget.CurrentState;
            }
            Combatant target = null; float best = 1e9f;
            foreach (var c in CombatDirector.I.all)
            {
                if (c == null || c.IsDead || c.team == self.team) continue;
                float d = (c.transform.position - transform.position).sqrMagnitude;
                if (d < best) { best = d; target = c; }
            }
            self.Target = target;
            if (target == null) { self.SetBlock(false); Move(Vector3.zero); return; }
            if (!self.WeaponReady && self.Holster != null && !self.Holster.IsBusy) self.Holster.Draw();
            Vector3 to = GK.Flat(target.transform.position - transform.position);
            float dist = to.magnitude;
            self.DesiredFacing = to;

            // guard: any enemy close and winding up raises it until its swing is spent
            bool threat = false;
            foreach (var c in CombatDirector.I.all)
                if (c != null && !c.IsDead && c.team != self.team && c.IsWindingUp &&
                    GK.Flat(c.transform.position - transform.position).magnitude < 3.2f) { threat = true; break; }
            if (threat && self.CurrentState == Combatant.State.Free && Time.time >= blockUntil)
            {
                if (Random.value < 0.15f) self.RequestDodge(-to + transform.right * (Random.value < 0.5f ? 1f : -1f));
                else { if (!self.BlockHeld) blocksRaised++; blockUntil = Time.time + 0.9f; }
            }
            bool guarding = Time.time < blockUntil;
            self.SetBlock(guarding);

            // a wave that holds ground (Didgori W3: Black Banner Rise): take it first, fight what comes there
            Transform hold = HoldPoint();
            if (hold != null && dist > 8f && self.CurrentState == Combatant.State.Free)
            {
                Vector3 toHold = GK.Flat(hold.position - transform.position);
                if (toHold.magnitude > 4f)
                {
                    // along the NavMesh (through the opened gate, up the climb): straight at it stalled on the rise's flank
                    Vector3 step = PathStep(hold.position);
                    Move(step.normalized * 3.9f); motor.FaceDirection(step, 12f, Time.deltaTime); return;
                }
            }

            if (self.CurrentState == Combatant.State.Free)
            {
                if (dist > 2.4f) Move(to.normalized * (dist > 6f ? 3.9f : 2.4f));
                else Move(Vector3.zero);
                motor.FaceDirection(to, 12f, Time.deltaTime);
            }
            if (guarding || Time.time < nextAction) return;
            if (dist < 3.2f && self.CanAttackNow)
            {
                bool ok;
                if (comboLeft <= 0 && Random.value < 0.12f) ok = self.RequestKick();
                else
                {
                    bool heavy = comboLeft == 1 && Random.value < 0.5f;
                    ok = self.RequestAttack(heavy ? AttackKind.Heavy : AttackKind.Light, false);
                }
                if (ok)
                {
                    comboLeft = comboLeft <= 0 ? Random.Range(2, 6) : comboLeft - 1;
                    nextAction = Time.time + 0.15f;
                    if (comboLeft == 0) nextAction = Time.time + 0.9f;
                }
            }
        }

        UnityEngine.AI.NavMeshPath path; float pathAt = -1f; int corner, cornerCount;
        readonly Vector3[] corners = new Vector3[64];   // path.corners would make a new array every call
        Vector3 PathStep(Vector3 goal)
        {
            if (path == null) path = new UnityEngine.AI.NavMeshPath();
            if (Time.time - pathAt > 0.5f)
            {
                pathAt = Time.time; corner = 1;
                UnityEngine.AI.NavMeshHit a, b;
                if (!UnityEngine.AI.NavMesh.SamplePosition(transform.position, out a, 3f, UnityEngine.AI.NavMesh.AllAreas) ||
                    !UnityEngine.AI.NavMesh.SamplePosition(goal, out b, 6f, UnityEngine.AI.NavMesh.AllAreas) ||
                    !UnityEngine.AI.NavMesh.CalculatePath(a.position, b.position, UnityEngine.AI.NavMesh.AllAreas, path)) path.ClearCorners();
                cornerCount = path.GetCornersNonAlloc(corners);
            }
            while (corner < cornerCount && GK.Flat(corners[corner] - transform.position).magnitude < 1.2f) corner++;
            return corner < cornerCount ? GK.Flat(corners[corner] - transform.position) : GK.Flat(goal - transform.position);
        }

        WaveManager waves; int holdWave = -2; Transform holdT; float nextWaveSearch;
        Transform HoldPoint()
        {
            // a scene without waves: looked for once a second, not every frame
            if (waves == null && Time.time >= nextWaveSearch) { nextWaveSearch = Time.time + 1f; waves = FindAnyObjectByType<WaveManager>(); }
            if (waves == null || waves.WaveIndex < 0 || waves.WaveIndex >= waves.waves.Length) return null;
            if (holdWave != waves.WaveIndex)
            {
                holdWave = waves.WaveIndex;
                string n = waves.waves[holdWave].holdPoint;
                var go = string.IsNullOrEmpty(n) ? null : GameObject.Find(n);
                holdT = go != null ? go.transform : null;
            }
            return holdT;
        }

        void Move(Vector3 v)
        {
            motor.SetLocomotionVelocity(v);
            if (anim != null)
            {
                anim.SetFloat(GK.P_Speed, v.magnitude, 0.1f, Time.deltaTime);
                anim.SetFloat(GK.P_Strafe, 0f);
            }
        }
    }
}
