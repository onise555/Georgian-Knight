namespace GeorgianKnight
{
    // C18 (README contracts): the item vocabulary. Every enum is append-only: saves and data store these values.

    public enum ItemCategory { Weapon, Shield, Bow, Arrows, Armour, Outfit, Consumable, Reagent, Book, Quest, Misc }

    public enum WeaponClass { Sword, Sabre, MaceAxe, GreatAxe, Spear, Bow }

    /// <summary>Where a carried weapon hangs when it is not in the hand.</summary>
    public enum HangPoint { HipL, HipR, Back, Belt }

    /// <summary>How sure the lore line is (GDD marks): S source, E estimate, L legend, P plausible, D design.</summary>
    public enum LoreSource { S, E, L, P, D }

    // EquipSlot lives in Common/GameplayEvents.cs (explicit, append-only values; C18 adds MainB, Quiver and the aliases
    // MainA / Bow / Shield there)
}
