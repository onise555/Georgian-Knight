using System.Collections.Generic;
using GeorgianKnight.Core;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Registry of everyone in the fight plus the attack-token budget that keeps group fights
    /// readable (only a couple of enemies commit to attacks at the same time, as in AC / Batman Arkham).
    /// </summary>
    public class CombatDirector : MonoBehaviour
    {
        static CombatDirector instance;
        public static CombatDirector I
        {
            get
            {
                if (instance == null)
                {
                    instance = FindAnyObjectByType<CombatDirector>();
                    if (instance == null) instance = new GameObject("[CombatDirector]").AddComponent<CombatDirector>();
                }
                return instance;
            }
        }

        /// <summary>The live instance without creating one (use in OnDestroy / teardown paths).</summary>
        public static CombatDirector Existing { get { return instance; } }

        // one blade at a time, the others circling and waiting their turn: every blow can be seen coming and met
        // (two at once from different sides could not be blocked - the hero fell in wave 3 of 5, measured)
        public int maxSimultaneousAttackers = 1;
        public float globalTokenCooldown = 0.7f;

        [Tooltip("Archers drawing on the player at once (BALANCE 5.3: 1 / 2 / 2 / 3 by difficulty); the rest keep their "
                 + "distance with an arrow on the string. Separate from the melee tokens: a shot and a blade can meet.")]
        public int maxRangedShooters = 2;

        public readonly List<Combatant> all = new List<Combatant>();
        readonly HashSet<Combatant> tokenHolders = new HashSet<Combatant>();
        readonly HashSet<Combatant> rangedHolders = new HashSet<Combatant>();
        float lastTokenTime = -10f;

        public Combatant Player { get; private set; }

        [Header("Combat state (music, HUD, item rules, fast travel)")]
        [Tooltip("An enemy targeting the player within this distance counts as engaged.")]
        public float engageDistance = 32f;
        [Tooltip("Seconds without engaged enemies before combat ends (stops music / HUD flicker between waves).")]
        public float combatExitDelay = 3f;

        /// <summary>True while at least one living enemy is engaged with the player (published as <see cref="CombatStateChanged"/>).</summary>
        public bool InCombat { get; private set; }
        public int EngagedEnemies { get; private set; }
        float calmSince;

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
        }

        void Update()
        {
            int engaged = 0;
            var p = Player;
            if (p != null && !p.IsDead)
            {
                Vector3 pp = p.transform.position;
                float max2 = engageDistance * engageDistance;
                for (int i = 0; i < all.Count; i++)
                {
                    var c = all[i];
                    if (c == null || c.team != Team.Enemy || c.IsDead || c.Target != p) continue;
                    if ((c.transform.position - pp).sqrMagnitude <= max2) engaged++;
                }
            }
            bool was = InCombat;
            int prevCount = EngagedEnemies;
            EngagedEnemies = engaged;
            if (engaged > 0) { InCombat = true; calmSince = Time.time; }
            else if (InCombat && (p == null || p.IsDead || Time.time - calmSince >= combatExitDelay)) InCombat = false;
            if (InCombat != was || (InCombat && engaged != prevCount))
                EventBus.Publish(new CombatStateChanged(InCombat, engaged));
        }

        void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
                if (InCombat) EventBus.Publish(new CombatStateChanged(false, 0));   // scene unload ends any fight
            }
        }

        public void Register(Combatant c)
        {
            if (!all.Contains(c)) all.Add(c);
            if (c.team == Team.Player) Player = c;
        }

        public void Unregister(Combatant c)
        {
            all.Remove(c);
            tokenHolders.Remove(c);
            rangedHolders.Remove(c);
            if (Player == c) Player = null;
        }

        public bool RequestAttackToken(Combatant c)
        {
            if (tokenHolders.Contains(c)) return true;
            tokenHolders.RemoveWhere(x => x == null || x.IsDead);
            if (tokenHolders.Count >= maxSimultaneousAttackers) return false;
            if (Time.time - lastTokenTime < globalTokenCooldown) return false;
            tokenHolders.Add(c);
            lastTokenTime = Time.time;
            return true;
        }

        public void ReleaseAttackToken(Combatant c) { tokenHolders.Remove(c); }

        /// <summary>A shot at the player: at most <see cref="maxRangedShooters"/> archers draw at once.</summary>
        public bool RequestRangedToken(Combatant c)
        {
            if (rangedHolders.Contains(c)) return true;
            rangedHolders.RemoveWhere(x => x == null || x.IsDead);
            if (rangedHolders.Count >= maxRangedShooters) return false;
            rangedHolders.Add(c);
            return true;
        }

        public void ReleaseRangedToken(Combatant c) { rangedHolders.Remove(c); }

        public int AliveEnemies
        {
            get
            {
                int n = 0;
                for (int i = 0; i < all.Count; i++) if (all[i] != null && all[i].team == Team.Enemy && !all[i].IsDead) n++;
                return n;
            }
        }

        /// <summary>Best hostile target in front of <paramref name="from"/> (used for attack assist).</summary>
        public Combatant FindTarget(Combatant from, Vector3 preferredDir, float maxDistance, float maxAngle)
        {
            Combatant best = null;
            float bestScore = float.MaxValue;
            Vector3 origin = from.transform.position;
            if (preferredDir.sqrMagnitude < 1e-4f) preferredDir = from.transform.forward;
            for (int i = 0; i < all.Count; i++)
            {
                Combatant c = all[i];
                if (c == null || c == from || c.team == from.team || c.IsDead) continue;
                Vector3 to = GK.Flat(c.transform.position - origin);
                float d = to.magnitude;
                if (d > maxDistance) continue;
                float ang = Mathf.Abs(GK.FlatAngle(preferredDir, to));
                if (ang > maxAngle) continue;
                float score = d * (1f + ang / 45f);
                if (score < bestScore) { bestScore = score; best = c; }
            }
            return best;
        }
    }
}
