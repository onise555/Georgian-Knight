#if UNITY_EDITOR || DEVELOPMENT_BUILD
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Effects review (QUALITY 3b): the same blow, again and again, so a before and an after can be compared frame for
    /// frame. The hero and the nearest enemy are set on a fixed spot facing each other at a fixed distance before every
    /// blow; the enemy's AI and the hero's controller are paused, the enemy's health is topped up so it never dies, and
    /// UnityEngine.Random is reseeded so the effect code makes the same choices each time. The blow itself is the
    /// game's own attack (Combatant.StartAttack), so hit-stop, contact and effects are the real ones.
    /// Added and removed by the editor command `feel_rig`; never part of a scene or a release build.
    /// </summary>
    public sealed class FeelBlowRig : MonoBehaviour
    {
        public enum Blow { Light, Heavy, Block }

        public Blow blow = Blow.Light;
        public float interval = 2.5f;
        public float distance = 1.5f;
        public int seed = 1121;

        Combatant hero, foe;
        Vector3 origin;
        Quaternion facing;
        float next;
        Behaviour heroControl, foeAI, autopilot;
        public int Blows { get; private set; }

        void Start()
        {
            var d = CombatDirector.Existing;
            hero = d != null ? d.Player : null;
            if (hero == null) { Debug.LogWarning("[FeelRig] no player"); Destroy(this); return; }
            foe = d.FindTarget(hero, hero.transform.forward, 60f, 180f);
            if (foe == null) { Debug.LogWarning("[FeelRig] no enemy in 60 m"); Destroy(this); return; }
            origin = hero.transform.position;
            Vector3 f = foe.transform.position - origin; f.y = 0f;
            facing = Quaternion.LookRotation(f.sqrMagnitude > 1e-3f ? f.normalized : hero.transform.forward);
            heroControl = Pause(hero, "PlayerController");
            autopilot = Pause(hero, "GKAutoPilot");
            foeAI = Pause(foe, "EnemyAI");
            next = Time.time + 0.5f;
        }

        static Behaviour Pause(Combatant c, string type)
        {
            foreach (var b in c.GetComponents<Behaviour>())
                if (b != null && b.GetType().Name == type && b.enabled) { b.enabled = false; return b; }
            return null;
        }

        void Place()
        {
            hero.Motor.Teleport(origin, facing);
            foe.Motor.Teleport(origin + facing * Vector3.forward * distance, facing * Quaternion.Euler(0f, 180f, 0f));
            foe.Stats.SetHealth(foe.Stats.maxHealth);
            hero.Stats.SetHealth(hero.Stats.maxHealth);
        }

        void Update()
        {
            if (hero == null || foe == null || foe.IsDead) { Destroy(this); return; }
            // hold the pair on their marks between blows (nothing drifts the next take off its frame)
            if (Time.time < next - 0.4f) return;
            if (Time.time < next) { Place(); foe.SetBlock(blow == Blow.Block); return; }
            next = Time.time + interval;
            Random.InitState(seed);
            var moves = hero.moves;
            var a = moves == null ? null : blow == Blow.Heavy ? moves.heavyStart : moves.lightStart;
            if (a != null && hero.StartAttack(a)) Blows++;
        }

        void OnDestroy()
        {
            if (foe != null) foe.SetBlock(false);
            if (heroControl != null) heroControl.enabled = true;
            if (autopilot != null) autopilot.enabled = true;
            if (foeAI != null) foeAI.enabled = true;
        }
    }
}
#endif
