using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// One kind of item (C18). Its id never changes once shipped: saves hold ids, counts, locations and the slot map.
    /// Words are localisation keys (C6); the icon is rendered from <see cref="model"/> by UI's tool. The sell price is
    /// the shop's (GDD §17), so only the base <see cref="value"/> is stored.
    /// </summary>
    [CreateAssetMenu(menuName = "Georgian Knight/Item", fileName = "Item")]
    public class ItemData : ScriptableObject
    {
        [Tooltip("Stable id (BALANCE ids, e.g. w_flanged_mace).")] public string id;
        public string nameKey, descKey, loreKey;
        public LoreSource loreSource = LoreSource.D;
        public ItemCategory category = ItemCategory.Misc;
        public Sprite icon;
        public GameObject model;
        public float weight = 1f;
        public int stackMax = 1;
        [Tooltip("Base value in silver; the shop sets the price from it.")] public int value;
        [Tooltip("The heirloom is not.")] public bool sellable = true;
        [Tooltip("Combat's clip id played when the item is used (eat, drink, read); empty = none.")] public string useAction;
    }
}
