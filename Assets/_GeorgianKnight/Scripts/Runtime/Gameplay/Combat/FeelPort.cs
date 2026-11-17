using GeorgianKnight.Feel;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Combat's one door to the game-feel layer (GK.Feel, owned by Game Feel & VFX; contract C1a). The numbers come from
    /// FeelSettings.Active (VFX/Resources/Feel_Default.asset) and every moment is published through FeelHub.
    /// </summary>
    public static class FeelPort
    {
        static FeelSettings S { get { return FeelSettings.Active; } }

        public static float HitStop(FeelKind k, bool heavy, float attackValue, bool crit) { return S.HitStop(k, heavy, attackValue, crit); }
        public static float Shake(FeelKind k, bool heavy, float attackValue, bool crit) { return S.Shake(k, heavy, attackValue, crit); }
        public static bool SlowMotion(FeelKind k, bool heavy, bool crit, out float scale, out float seconds, out float cooldown) { return S.SlowMotion(k, heavy, crit, out scale, out seconds, out cooldown); }
        public static bool ExecutionStartSlow(out float scale, out float seconds) { float cd; return S.SlowMotion(S.executionStart, false, out scale, out seconds, out cd); }
        public static void Impact(FeelKind k, Vector3 point, Vector3 dir, GameObject attacker, GameObject target, bool heavy, bool crit, bool attackerIsPlayer, bool targetIsPlayer)
        { FeelHub.Impact(k, point, dir, attacker, target, heavy, crit, attackerIsPlayer, targetIsPlayer); }
        public static void Swing(GameObject attacker, Vector3 tip, bool heavy, bool isPlayer) { FeelHub.Swing(attacker, tip, heavy, isPlayer); }
        public static void Footstep(GameObject walker, Vector3 foot, float speed, bool isPlayer) { FeelHub.Footstep(walker, foot, speed, isPlayer); }
        public static void Simple(FeelKind k, GameObject who, Vector3 pos, bool isPlayer) { FeelHub.Simple(k, who, pos, isPlayer); }
    }
}
