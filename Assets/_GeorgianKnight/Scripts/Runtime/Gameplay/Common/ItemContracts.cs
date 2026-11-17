using System;

namespace GeorgianKnight
{
    // Shared item contracts. Narrative, world and interaction code hand items to the player through these, never
    // through the inventory implementation; the items module registers its inventory as both services. Exactly one
    // definition each: module-local copies are not allowed (TEAM_BRIEF "shared contracts live in Common").

    /// <summary>Gives / takes items to and from the player's real inventory.</summary>
    public interface IItemGiver
    {
        /// <summary>Adds items; false (and nothing added) if they cannot be given (unknown id, over the hard weight limit).</summary>
        bool Give(string itemId, int count);
        /// <summary>Removes items; false (and nothing removed) if the player has fewer than <paramref name="count"/> removable copies.</summary>
        bool Take(string itemId, int count);
    }

    /// <summary>Read side of the player's inventory (collect objectives, key-locked doors, dialogue conditions).</summary>
    public interface IItemCounter
    {
        /// <summary>Copies owned, equipped ones included.</summary>
        int Count(string itemId);
    }

    /// <summary>An item id and a count, as authored on pickups, containers, dialogue actions, rewards and new-game kits.</summary>
    [Serializable]
    public sealed class ItemGrant
    {
        public string itemId;
        public int count = 1;

        public ItemGrant() { }
        public ItemGrant(string itemId, int count) { this.itemId = itemId; this.count = count; }

        public bool IsValid { get { return !string.IsNullOrEmpty(itemId) && count > 0; } }
        public override string ToString() { return itemId + " x" + count; }
    }
}
