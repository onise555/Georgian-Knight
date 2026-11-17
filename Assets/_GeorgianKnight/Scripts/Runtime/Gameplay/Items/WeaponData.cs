using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>A weapon item (C18): its class, the move set it fights with, BALANCE §3.1 multipliers and where it hangs.
    /// Whether a melee slot carries the shield with it is the slot's (<see cref="Equipment"/>), so one sword can be carried
    /// with the shield in one slot and alone in the other.</summary>
    [CreateAssetMenu(menuName = "Georgian Knight/Weapon", fileName = "Weapon")]
    public class WeaponData : ItemData
    {
        public WeaponClass weaponClass = WeaponClass.Sword;
        [Tooltip("The move set it fights with; empty = the style's own (sword: FightingStyles).")] public MoveSet moveSet;
        public float damage = 1f, poise = 1f, speed = 1f, stamina = 1f;
        public HangPoint hang = HangPoint.HipL;
        public bool twoHanded;
        [Tooltip("Can be carried with the shield (one-handed weapons).")] public bool shieldAllowed = true;
        public string perk;
    }
}
