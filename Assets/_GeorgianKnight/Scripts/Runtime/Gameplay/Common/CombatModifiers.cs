namespace GeorgianKnight
{
    /// <summary>
    /// Stat-driven combat numbers a character exposes to the combat code (implemented by the stats module's
    /// CharacterStats; absent = neutral values). Combat reads these per hit / per action and never writes them, so the
    /// stat system stays the single writer of everything derived from gear, buffs and skills.
    /// </summary>
    public interface ICombatModifiers
    {
        /// <summary>Flat armour rating used by ArmorMath.Mitigate on every incoming weapon hit.</summary>
        float Armor { get; }
        /// <summary>Armour ignored by this character's hits (0..1 fraction of the defender's armour).</summary>
        float ArmorPenetration { get; }
        /// <summary>Outgoing weapon damage multiplier (weapon scaling, buffs, skills; 1 = neutral).</summary>
        float DamageMultiplier { get; }
        /// <summary>Outgoing poise damage multiplier (1 = neutral).</summary>
        float PoiseDamageMultiplier { get; }
        /// <summary>Chance (0..1) that a weapon hit is critical.</summary>
        float CritChance { get; }
        /// <summary>Damage multiplier of a critical hit (e.g. 1.5).</summary>
        float CritDamage { get; }
        /// <summary>Multiplier on stamina spent by attacks, blocks and dodges (1 = neutral).</summary>
        float StaminaCostMultiplier { get; }
        /// <summary>Locomotion speed multiplier (encumbrance, armour weight, buffs; 1 = neutral).</summary>
        float MoveSpeedMultiplier { get; }
    }
}
