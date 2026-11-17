using System.Collections.Generic;
using System.Globalization;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>The numbers of one attack needed for balance checks (decoupled from AttackData for tests).</summary>
    public struct AttackNumbers
    {
        public string Kind;            // AttackKind name
        public float HitStart, HitEnd, ComboWindow, DodgeCancel, End, SteerUntil;
        public float Damage, PoiseDamage, StaminaCost;
        public bool Unblockable;
        public float ClipSeconds;      // clip.length / speed (0 when no clip)
    }

    /// <summary>One balance/timing finding: which rule and what is wrong.</summary>
    public struct CombatFinding
    {
        public QaRule Rule;
        public QaSeverity Severity;
        public string Message;
    }

    /// <summary>
    /// Attack and action-clip rules (ordering of normalized timings, damage/stamina envelopes per kind, unblockable
    /// telegraph, active-window length, dodge i-frames). Pure functions over plain numbers.
    /// </summary>
    public static class CombatRules
    {
        static string F(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        public static List<CombatFinding> CheckAttack(AttackNumbers a, QaSettings.CombatLimits lim)
        {
            var list = new List<CombatFinding>();
            Timing(a, list);
            Window(a, lim, list);
            if (a.Kind != "Execution") Values(a, lim, list);
            return list;
        }

        static void Timing(AttackNumbers a, List<CombatFinding> list)
        {
            if (!(a.HitStart >= 0f && a.HitStart < a.HitEnd && a.HitEnd < a.ComboWindow && a.ComboWindow <= a.End && a.End <= 1f))
                list.Add(Make(QaRules.DataAttackTiming, "timings out of order: hitStart " + F(a.HitStart) + ", hitEnd " + F(a.HitEnd) +
                    ", comboWindow " + F(a.ComboWindow) + ", end " + F(a.End) + " (need 0 <= hitStart < hitEnd < comboWindow <= end <= 1)"));
            if (a.DodgeCancel < a.HitEnd)
                list.Add(Make(QaRules.DataAttackTiming, "dodgeCancel " + F(a.DodgeCancel) + " before hitEnd " + F(a.HitEnd) + " lets the attacker cancel the swing before it lands", QaSeverity.Major));
            if (a.SteerUntil > a.HitStart)
                list.Add(Make(QaRules.DataAttackValues, "steerUntil " + F(a.SteerUntil) + " after hitStart " + F(a.HitStart) + " (tracking during the active frames feels like homing)", QaSeverity.Minor));
        }

        /// <summary>Active-window length and unblockable wind-up, in seconds of the (speed-scaled) clip.</summary>
        static void Window(AttackNumbers a, QaSettings.CombatLimits lim, List<CombatFinding> list)
        {
            if (a.ClipSeconds <= 0f)
            {
                if (a.Unblockable) list.Add(Make(QaRules.DataUnblockableTelegraph, "unblockable attack without a clip: wind-up cannot be verified"));
                return;
            }
            float active = (a.HitEnd - a.HitStart) * a.ClipSeconds;
            if (active < lim.activeWindowMinSeconds)
                list.Add(Make(QaRules.DataAttackValues, "active window " + F(active * 1000f) + " ms is shorter than two frames (hits can be missed)"));
            else if (active > lim.activeWindowMaxSeconds)
                list.Add(Make(QaRules.DataAttackValues, "active window " + F(active * 1000f) + " ms is unusually long (lingering hitbox)", QaSeverity.Minor));
            if (a.Unblockable && a.HitStart * a.ClipSeconds < lim.unblockableWindupMin)
                list.Add(Make(QaRules.DataUnblockableTelegraph, "unblockable wind-up " + F(a.HitStart * a.ClipSeconds * 1000f) + " ms (< " + F(lim.unblockableWindupMin * 1000f) + " ms telegraph)"));
        }

        /// <summary>Damage / stamina / poise envelope per attack kind (executions are exempt).</summary>
        static void Values(AttackNumbers a, QaSettings.CombatLimits lim, List<CombatFinding> list)
        {
            bool light = a.Kind == "Light" || a.Kind == "Kick";
            float min = a.Kind == "Kick" ? 0f : light ? lim.lightDamageMin : lim.heavyDamageMin;
            float max = light ? lim.lightDamageMax : lim.heavyDamageMax;
            if (a.Damage < min || a.Damage > max)
                list.Add(Make(QaRules.DataAttackValues, a.Kind + " damage " + F(a.Damage) + " outside " + F(min) + ".." + F(max)));
            if (a.StaminaCost <= 0f || a.StaminaCost > lim.staminaMax)
                list.Add(Make(QaRules.DataAttackValues, "stamina cost " + F(a.StaminaCost) + " outside (0.." + F(lim.staminaMax) + "]"));
            if (a.PoiseDamage < 0f || a.PoiseDamage > lim.poiseMax)
                list.Add(Make(QaRules.DataAttackValues, "poise damage " + F(a.PoiseDamage) + " outside 0.." + F(lim.poiseMax)));
        }

        /// <summary>Checks an ActionClip's normalized timings; <paramref name="isDodge"/> requires an i-frame window.</summary>
        public static List<CombatFinding> CheckAction(string label, float end, float cancelFrom, float iStart, float iEnd, bool isDodge)
        {
            var list = new List<CombatFinding>();
            if (cancelFrom > end) list.Add(Make(QaRules.DataActionClip, label + ": cancelFrom " + F(cancelFrom) + " after end " + F(end)));
            if (end <= 0f || end > 1f) list.Add(Make(QaRules.DataActionClip, label + ": end " + F(end) + " outside (0..1]"));
            if ((iStart > 0f || iEnd > 0f) && iStart >= iEnd) list.Add(Make(QaRules.DataActionClip, label + ": i-frames " + F(iStart) + ".." + F(iEnd) + " are empty or inverted"));
            if (isDodge && iEnd <= iStart) list.Add(Make(QaRules.DataDodgeIFrames, label + ": dodge has no invulnerability window"));
            return list;
        }

        static CombatFinding Make(QaRule rule, string message) { return Make(rule, message, rule.Severity); }

        static CombatFinding Make(QaRule rule, string message, QaSeverity severity)
        {
            return new CombatFinding { Rule = rule, Severity = severity, Message = message };
        }
    }
}
