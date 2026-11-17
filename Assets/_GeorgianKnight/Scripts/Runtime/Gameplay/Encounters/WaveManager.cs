using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace GeorgianKnight
{
    /// <summary>
    /// Escalating waves of Seljuk warriors arrive over the rim of the Didgori plateau. With
    /// <see cref="approachDistance"/> set they come in off screen within reach of the player; a wave's
    /// <see cref="Wave.stagger"/> brings its men in one after another. Victory when every wave is cut down.
    /// </summary>
    public class WaveManager : MonoBehaviour
    {
        [Serializable]
        public class Wave
        {
            public string title = "";
            [Tooltip("Localization key the HUD shows (Loc.Get); `title` is only the fallback text (C6).")]
            public string titleKey = "";
            public int warriors = 2;
            public int veterans;
            public bool champion;
            public float delayBefore = 4f;
            [Tooltip("Lanes this wave comes down (SpawnLane.lane); fighters are shared out among them in turn. Empty = the ring around the arena.")]
            public int[] lanes;
            [Tooltip("A scene object (by name) the wave's men march to and hold until the player comes near it (C17 note: "
                     + "Didgori's W3 holds Markers/HoldPoint_BlackBannerRise). Empty or not in the scene: they go for the player.")]
            public string holdPoint;
            [Tooltip("Seconds between this wave's fighters coming in (0 = all at once). The first one opens the wave alone, "
                     + "so a first wave of two with a long stagger plays as a duel the second man joins.")]
            public float stagger;
        }

        public GameObject enemyPrefab;
        [Tooltip("Veterans come as this prefab (their own move set: the mace) when set; else as the warrior, re-tuned.")]
        public GameObject veteranPrefab;
        public Combatant player;
        public Wave[] waves =
        {
            // M1 "Didgori, the first fight": three waves (Director decision #4); words come from the Loc keys
            new Wave { title = "მოწინავე რაზმი", titleKey = "wave.didgori.1.title", warriors = 2, delayBefore = 3f, lanes = new[] { 3 } },
            new Wave { title = "თურქ-სელჩუკთა ქვეითები", titleKey = "wave.didgori.2.title", warriors = 3, delayBefore = 5f, lanes = new[] { 3, 1 } },
            new Wave { title = "ვეტერანები", titleKey = "wave.didgori.3.title", warriors = 2, veterans = 2, delayBefore = 6f, lanes = new[] { 1, 2 }, holdPoint = "HoldPoint_BlackBannerRise" },
        };
        public float spawnRadius = 32f;
        public float spawnSpread = 7f;
        [Tooltip("Lane spawns are pulled in to this distance from the player (along the lane's direction, off screen), so a "
                 + "wave reaches him in seconds, not after a 120 m walk-in (M1 master list row 2). 0 = spawn at the lane head.")]
        public float approachDistance;
        [Tooltip("The level's spawn lanes (filled from the scene when empty).")]
        public SpawnLane[] lanes;

        public enum Phase { Intro, Between, Fighting, Victory, Defeat }
        public Phase CurrentPhase { get; private set; }
        public int WaveIndex { get; private set; }
        public int WaveCount { get { return waves.Length; } }
        public float ElapsedTime { get; private set; }
        public int Kills { get; private set; }

        public event Action<int, Wave> WaveAnnounced;
        public event Action<int> WaveCleared;
        public event Action Victory;
        public event Action Defeat;

        readonly List<Combatant> alive = new List<Combatant>();
        float nextWaveAt;

        // a wave's fighters still to come in (stagger), in order
        struct Pending { public float at; public Vector3 pos; public Vector3 lookAt; public int rank; }
        readonly List<Pending> pending = new List<Pending>();

        void Start()
        {
            if (player == null) player = CombatDirector.I.Player;
            if (player != null) player.Died += h => { if (CurrentPhase != Phase.Victory) { CurrentPhase = Phase.Defeat; if (Defeat != null) Defeat(); } };
            CurrentPhase = Phase.Intro;
            WaveIndex = -1;
            nextWaveAt = Time.time + 2.5f;
        }

        void Update()
        {
            if (CurrentPhase == Phase.Victory || CurrentPhase == Phase.Defeat) return;
            ElapsedTime += Time.deltaTime;
            alive.RemoveAll(c => c == null || c.IsDead);
            SpawnDue();

            if (CurrentPhase == Phase.Fighting && alive.Count == 0 && pending.Count == 0)
            {
                if (WaveCleared != null) WaveCleared(WaveIndex);
                if (WaveIndex >= waves.Length - 1)
                {
                    CurrentPhase = Phase.Victory;
                    if (player != null && player.moves != null && player.moves.victory.IsValid) player.PlaySkill(player.moves.victory);
                    if (Victory != null) Victory();
                    return;
                }
                CurrentPhase = Phase.Between;
                nextWaveAt = Time.time + waves[WaveIndex + 1].delayBefore;
                if (player != null) player.Stats.Heal(player.Stats.maxHealth * 0.25f);
            }

            if ((CurrentPhase == Phase.Intro || CurrentPhase == Phase.Between) && Time.time >= nextWaveAt)
                StartWave(WaveIndex + 1);
        }

        void StartWave(int index)
        {
            WaveIndex = index;
            var w = waves[index];
            CurrentPhase = Phase.Fighting;
            if (WaveAnnounced != null) WaveAnnounced(index, w);
            GeorgianKnight.Core.EventBus.Publish(new WaveStarted(index));   // C17
            Vector3 center = player != null ? player.transform.position : transform.position;
            // arrive from the side the player is NOT looking at, so the camera has to find them
            float baseAngle = player != null ? Mathf.Atan2(-player.transform.forward.x, -player.transform.forward.z) * Mathf.Rad2Deg : 0f;
            int total = w.warriors + w.veterans + (w.champion ? 1 : 0);
            if (lanes == null || lanes.Length == 0) lanes = FindObjectsByType<SpawnLane>(FindObjectsSortMode.None);
            if (w.lanes != null && w.lanes.Length > 0 && lanes.Length > 0)
            {
                // down the level's lanes, the fighters shared out among the listed ones in turn
                for (int i = 0; i < total; i++)
                {
                    int want = w.lanes[i % w.lanes.Length];
                    SpawnLane l = null;
                    foreach (var c in lanes) if (c != null && c.lane == want) { l = c; break; }
                    if (l == null) continue;
                    Vector2 r = UnityEngine.Random.insideUnitCircle * l.radius;
                    Vector3 pos = l.transform.position + new Vector3(r.x, 0f, r.y);
                    int rank = i < w.warriors ? 0 : (i < w.warriors + w.veterans ? 1 : 2);
                    Queue(i * w.stagger, ApproachPoint(pos), l.useForward ? pos + l.transform.forward : center, rank);
                }
                SpawnDue();
                return;
            }
            for (int i = 0; i < total; i++)
            {
                float ang = baseAngle + (i - (total - 1) * 0.5f) * 24f + UnityEngine.Random.Range(-6f, 6f);
                Vector3 dir = Quaternion.Euler(0f, ang, 0f) * Vector3.forward;
                Vector3 pos = transform.position + dir * (spawnRadius + UnityEngine.Random.Range(-spawnSpread, spawnSpread) * 0.4f);
                int rank = i < w.warriors ? 0 : (i < w.warriors + w.veterans ? 1 : 2);
                Queue(i * w.stagger, pos, center, rank);
            }
            SpawnDue();
        }

        void Queue(float delay, Vector3 pos, Vector3 lookAt, int rank)
        {
            pending.Add(new Pending { at = Time.time + Mathf.Max(0f, delay), pos = pos, lookAt = lookAt, rank = rank });
        }

        void SpawnDue()
        {
            for (int i = 0; i < pending.Count; i++)
            {
                if (Time.time < pending[i].at) continue;
                var p = pending[i];
                pending.RemoveAt(i--);
                // a late man faces where the player is now, not where he stood when the wave began
                Spawn(p.pos, player != null ? player.transform.position : p.lookAt, p.rank);
            }
        }

        static readonly float[] ApproachTurns = { 0f, 25f, -25f, 50f, -50f, 75f, -75f, 100f, -100f, 130f, -130f };

        /// <summary>The lane head pulled in to <see cref="approachDistance"/> of the player along the lane's direction,
        /// turned (up to 130°) until the point is walkable, reaches the player and is off screen: nobody pops into view.</summary>
        Vector3 ApproachPoint(Vector3 laneHead)
        {
            if (approachDistance <= 0f || player == null) return laneHead;
            Vector3 p = player.transform.position;
            Vector3 d = GK.Flat(laneHead - p);
            if (d.magnitude <= approachDistance) return laneHead;
            d.Normalize();
            var cam = Camera.main;
            var path = new NavMeshPath();
            foreach (float turn in ApproachTurns)
            {
                Vector3 cand = p + Quaternion.Euler(0f, turn, 0f) * d * approachDistance;
                NavMeshHit hit;
                if (!NavMesh.SamplePosition(cand, out hit, 6f, NavMesh.AllAreas)) continue;
                if (cam != null && InView(cam, hit.position)) continue;
                if (!NavMesh.CalculatePath(hit.position, p, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) continue;
                return hit.position;
            }
            return laneHead;                                     // nothing closer qualifies: the lane head, as before
        }

        static bool InView(Camera cam, Vector3 feet)
        {
            for (int k = 0; k < 2; k++)
            {
                Vector3 v = cam.WorldToViewportPoint(feet + Vector3.up * (k == 0 ? 0.1f : 1.8f));
                if (v.z > 0f && v.x > -0.08f && v.x < 1.08f && v.y > -0.08f && v.y < 1.08f) return true;
            }
            return false;
        }

        void Spawn(Vector3 pos, Vector3 lookAt, int rank)
        {
            GameObject prefab = rank == 1 && veteranPrefab != null ? veteranPrefab : enemyPrefab;
            if (prefab == null) return;
            bool ownKind = prefab != enemyPrefab;                // a veteran prefab keeps its own stats and temper
            NavMeshHit hit;
            if (NavMesh.SamplePosition(pos, out hit, 12f, NavMesh.AllAreas)) pos = hit.position;
            else
            {
                RaycastHit rh;
                if (Physics.Raycast(pos + Vector3.up * 200f, Vector3.down, out rh, 500f, ~(1 << GK.LayerCharacter | 1 << GK.LayerHurtbox))) pos = rh.point;
            }
            Quaternion rot = Quaternion.LookRotation(GK.Flat(lookAt - pos).sqrMagnitude > 0.01f ? GK.Flat(lookAt - pos).normalized : Vector3.forward);
            var go = Instantiate(prefab, pos, rot);
            var c = go.GetComponent<Combatant>();
            var ai = go.GetComponent<EnemyAI>();
            var stats = go.GetComponent<CombatStats>();
            if (ai != null) ai.engaged = true;   // a wave marches on the player, however far its lane head
            if (ai != null && !string.IsNullOrEmpty(waves[WaveIndex].holdPoint))
            {
                var hp = GameObject.Find(waves[WaveIndex].holdPoint);
                if (hp != null) ai.holdPoint = hp.transform;
            }
            if (rank == 1)
            {
                go.name = "Seljuk Veteran";
                if (c != null) c.displayNameKey = "enemy.seljuk_veteran.name";
                if (!ownKind)
                {
                    stats.maxHealth *= 1.5f; stats.maxPoise *= 1.4f;
                    if (ai != null) { ai.aggression = 0.7f; ai.blockSkill = 0.6f; ai.dodgeSkill = 0.25f; ai.unblockableChance = 0.2f; }
                }
            }
            else if (rank == 2)
            {
                go.name = "Seljuk Champion";
                if (c != null) c.displayNameKey = "enemy.seljuk_champion.name";
                go.transform.localScale = Vector3.one * 1.08f;
                stats.maxHealth *= 3.2f; stats.maxPoise *= 2.4f; stats.maxStamina *= 1.6f;
                if (ai != null) { ai.aggression = 0.85f; ai.blockSkill = 0.7f; ai.dodgeSkill = 0.3f; ai.unblockableChance = 0.3f; ai.comboChance = 0.75f; }
            }
            else { go.name = "Seljuk Warrior"; if (c != null) c.displayNameKey = "enemy.seljuk_warrior.name"; }
            stats.ResetAll();
            if (c != null)
            {
                c.Died += h => { Kills++; };
                alive.Add(c);
            }
        }

        /// <summary>Fighters on the field plus those of the current wave still to come in.</summary>
        public int AliveCount { get { return alive.Count + pending.Count; } }
    }
}
