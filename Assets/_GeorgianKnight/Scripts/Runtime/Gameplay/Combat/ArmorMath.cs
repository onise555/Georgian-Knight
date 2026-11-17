namespace GeorgianKnight
{
    /// <summary>
    /// Physical damage mitigation from the armour rating (StatId.Armor), as pure functions so combat, AI previews
    /// and the item card all agree on one formula.
    /// <para>
    /// <b>Formula (diminishing returns):</b> <c>reduction = A / (A + K)</c> with <c>K = </c><see cref="Constant"/>,
    /// capped at <see cref="MaxReduction"/>. Each extra point of armour is worth less than the previous one:
    /// 25 armour → 20 %, 50 → 33 %, 100 → 50 %, 200 → 67 %, 400 → 80 % (cap). A kaftan (5) barely matters, mail (32)
    /// takes a quarter off and full lamellar with helm (~70) about 40 %, which keeps heavy armour valuable without
    /// ever making the knight immune.
    /// </para>
    /// <para>
    /// Negative armour (sundering debuffs) amplifies damage symmetrically: <c>extra = |A| / (|A| + K)</c>, capped at
    /// <see cref="MaxAmplification"/>.
    /// </para>
    /// <para>
    /// Penetration (0..1) removes that fraction of positive armour before the formula (piercing arrows, maces).
    /// </para>
    /// </summary>
    public static class ArmorMath
    {
        /// <summary>Armour at which damage is halved.</summary>
        public const float Constant = 100f;
        public const float MaxReduction = 0.8f;
        public const float MaxAmplification = 0.5f;

        /// <summary>Fraction of incoming physical damage removed (negative when armour is negative).</summary>
        public static float Reduction(float armor)
        {
            if (armor >= 0f)
            {
                float r = armor / (armor + Constant);
                return r > MaxReduction ? MaxReduction : r;
            }
            float a = -armor;
            float amp = a / (a + Constant);
            return -(amp > MaxAmplification ? MaxAmplification : amp);
        }

        /// <summary>Multiplier for incoming damage: <c>1 - Reduction(armor)</c>.</summary>
        public static float DamageTakenMultiplier(float armor)
        {
            return 1f - Reduction(armor);
        }

        /// <summary>
        /// Damage that gets through. <paramref name="penetration"/> (0..1) ignores that fraction of positive armour.
        /// </summary>
        public static float Mitigate(float rawDamage, float armor, float penetration = 0f)
        {
            if (rawDamage <= 0f) return 0f;
            if (penetration > 0f && armor > 0f)
            {
                if (penetration > 1f) penetration = 1f;
                armor *= 1f - penetration;
            }
            return rawDamage * DamageTakenMultiplier(armor);
        }
    }
}
