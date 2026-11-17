using GeorgianKnight.Core;
using UnityEngine;

namespace GeorgianKnight.Feel
{
    /// <summary>What happened. Values are explicit and APPEND-ONLY (settings assets store them).</summary>
    public enum FeelKind
    {
        Hit = 0, Kill = 1, Block = 2, GuardBreak = 3, Parry = 4, Execution = 5,
        Swing = 6, Footstep = 7, Death = 8, Dodge = 9
    }

    /// <summary>
    /// What the blade met (contract C2 / C1b): drives sparks vs blood vs splinters and the contact sound. Values are
    /// explicit and APPEND-ONLY. Unknown = the raiser could not tell; listeners then fall back to the hit result.
    /// </summary>
    public enum FeelSurface
    {
        Unknown = 0, Flesh = 1, Cloth = 2, Mail = 3, Lamellar = 4, Steel = 5, Wood = 6, Leather = 7, Earth = 8, Stone = 9
    }

    /// <summary>What struck (C2). APPEND-ONLY.</summary>
    public enum FeelWeapon { Blade = 0, Kick = 1, ShieldBash = 2, Pommel = 3 }

    /// <summary>
    /// One game-feel moment, published on the <see cref="EventBus"/> by <see cref="FeelHub"/>. The listeners (rumble,
    /// hit flash, dust, screen pulse, sound layers) read only this, never combat types, so GK.Feel depends on Core alone.
    /// </summary>
    public struct FeelEvent : IGameEvent
    {
        public FeelKind Kind;
        public Vector3 Position;        // the contact point, a foot, a blade tip
        public Vector3 Direction;       // the blade's travel (impacts), the facing (others); may be zero
        public GameObject Source;       // attacker / walker
        public GameObject Target;       // struck character (impacts only)
        public bool Heavy;              // a heavy / charged / special attack
        public bool Critical;
        public bool SourceIsPlayer, TargetIsPlayer;
        public float Speed;             // footsteps: ground speed (m/s)
        public FeelSurface Surface;     // the outermost layer the blade met (Unknown if not told)
        public FeelSurface SurfaceUnder;// what lies under it (mail under a coat), Unknown if nothing
        public FeelWeapon Weapon;
        public float Force;             // 0.6 (a light cut) .. 1.8 (an execution); 0 = not told
    }

    /// <summary>The running game-feel layer, resolved through <see cref="Services"/> (registered by FeelDirector).</summary>
    public interface IFeelService
    {
        FeelSettings Settings { get; }
    }

    /// <summary>
    /// The single entry point combat code calls. Tuning lives in <see cref="FeelSettings.Active"/>; the layers in the
    /// scene's <see cref="FeelDirector"/> react. Calling it with nobody listening is free.
    /// </summary>
    public static class FeelHub
    {
        public static void Raise(FeelEvent e) { EventBus.Publish(e); }

        public static void Impact(FeelKind kind, Vector3 point, Vector3 direction, GameObject attacker, GameObject target,
                                  bool heavy, bool critical, bool attackerIsPlayer, bool targetIsPlayer)
        {
            EventBus.Publish(new FeelEvent
            {
                Kind = kind, Position = point, Direction = direction, Source = attacker, Target = target,
                Heavy = heavy, Critical = critical, SourceIsPlayer = attackerIsPlayer, TargetIsPlayer = targetIsPlayer
            });
        }

        /// <summary>An impact with its contact material and force (C1b). Prefer this over the plain overload.</summary>
        public static void Impact(FeelKind kind, Vector3 point, Vector3 direction, GameObject attacker, GameObject target,
                                  bool heavy, bool critical, bool attackerIsPlayer, bool targetIsPlayer,
                                  FeelSurface surface, FeelSurface under, FeelWeapon weapon, float force)
        {
            EventBus.Publish(new FeelEvent
            {
                Kind = kind, Position = point, Direction = direction, Source = attacker, Target = target,
                Heavy = heavy, Critical = critical, SourceIsPlayer = attackerIsPlayer, TargetIsPlayer = targetIsPlayer,
                Surface = surface, SurfaceUnder = under, Weapon = weapon, Force = force
            });
        }

        /// <summary>A footstep on a known ground (dust on earth, none on stone or in grass).</summary>
        public static void Footstep(GameObject walker, Vector3 foot, float speed, bool isPlayer, FeelSurface ground)
        {
            EventBus.Publish(new FeelEvent { Kind = FeelKind.Footstep, Position = foot, Source = walker, Speed = speed, SourceIsPlayer = isPlayer, Surface = ground });
        }

        public static void Footstep(GameObject walker, Vector3 foot, float speed, bool isPlayer)
        {
            EventBus.Publish(new FeelEvent { Kind = FeelKind.Footstep, Position = foot, Source = walker, Speed = speed, SourceIsPlayer = isPlayer });
        }

        public static void Swing(GameObject attacker, Vector3 bladeTip, bool heavy, bool isPlayer)
        {
            EventBus.Publish(new FeelEvent { Kind = FeelKind.Swing, Position = bladeTip, Source = attacker, Heavy = heavy, SourceIsPlayer = isPlayer });
        }

        public static void Simple(FeelKind kind, GameObject who, Vector3 position, bool isPlayer)
        {
            EventBus.Publish(new FeelEvent { Kind = kind, Position = position, Source = who, SourceIsPlayer = isPlayer });
        }
    }
}
