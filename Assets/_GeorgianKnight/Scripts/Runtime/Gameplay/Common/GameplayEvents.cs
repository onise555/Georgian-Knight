using GeorgianKnight.Core;
using UnityEngine;

namespace GeorgianKnight
{
    // Cross-module gameplay events. Module-private events live next to their module; anything another module
    // (quests, achievements, audio, UI) reacts to is declared here so there is exactly one definition.

    public struct EnemyKilled : IGameEvent
    {
        public string EnemyId;          // EnemyDef id ("" for unspecified)
        public Vector3 Position;
        public bool ByPlayer;
        public bool Execution;
        public string[] Tags;           // content tags of the victim ("looter", "seljuk", "boss" …); never null after publish by Combat
        public EnemyKilled(string enemyId, Vector3 pos, bool byPlayer, bool execution) { EnemyId = enemyId; Position = pos; ByPlayer = byPlayer; Execution = execution; Tags = null; }
        public EnemyKilled(string enemyId, string[] tags, Vector3 pos, bool byPlayer, bool execution) { EnemyId = enemyId; Tags = tags; Position = pos; ByPlayer = byPlayer; Execution = execution; }

        /// <summary>True if the id equals <paramref name="idOrTag"/> or the victim carries it as a tag.</summary>
        public bool Matches(string idOrTag)
        {
            if (string.IsNullOrEmpty(idOrTag)) return true;
            if (EnemyId == idOrTag) return true;
            if (Tags != null) for (int i = 0; i < Tags.Length; i++) if (Tags[i] == idOrTag) return true;
            return false;
        }
    }

    /// <summary>The player's health reached zero (death screen, checkpoint respawn, quest failure conditions).</summary>
    public struct PlayerDied : IGameEvent
    {
        public string KillerId;         // character id of the killer ("" for environment / unknown)
        public Vector3 Position;
        public PlayerDied(string killerId, Vector3 pos) { KillerId = killerId; Position = pos; }
    }

    /// <summary>A wave of an encounter begins (contract C17): 0-based, the moment WaveManager announces it. Level
    /// scenes open what the wave needs (Didgori: the boundary in front of Black Banner Rise at index 2).</summary>
    public struct WaveStarted : IGameEvent
    {
        public int waveIndex;
        public WaveStarted(int index) { waveIndex = index; }
    }

    public struct PlayerSpawned : IGameEvent
    {
        public GameObject Player;
        public PlayerSpawned(GameObject p) { Player = p; }
    }

    public struct ItemAdded : IGameEvent
    {
        public string ItemId; public int Count; public int Total;
        public ItemAdded(string id, int count, int total) { ItemId = id; Count = count; Total = total; }
    }

    public struct ItemRemoved : IGameEvent
    {
        public string ItemId; public int Count; public int Total;
        public ItemRemoved(string id, int count, int total) { ItemId = id; Count = count; Total = total; }
    }

    public struct ItemUsed : IGameEvent
    {
        public string ItemId;
        public ItemUsed(string id) { ItemId = id; }
    }

    public struct EquipmentChanged : IGameEvent
    {
        public EquipSlot Slot; public string ItemId; public string PreviousItemId;
        public EquipmentChanged(EquipSlot slot, string id, string previous) { Slot = slot; ItemId = id; PreviousItemId = previous; }
    }

    public struct CurrencyChanged : IGameEvent
    {
        public int Amount; public int Delta;
        public CurrencyChanged(int amount, int delta) { Amount = amount; Delta = delta; }
    }

    public struct SkillUnlocked : IGameEvent
    {
        public string SkillId;
        public SkillUnlocked(string id) { SkillId = id; }
    }

    public struct LocationDiscovered : IGameEvent
    {
        public string LocationId;
        public LocationDiscovered(string id) { LocationId = id; }
    }

    public struct LocationEntered : IGameEvent
    {
        public string LocationId;
        public LocationEntered(string id) { LocationId = id; }
    }

    public struct Interacted : IGameEvent
    {
        public string InteractableId; public string Kind;   // "talk", "open", "pickup", "use", "read" …
        public Interacted(string id, string kind) { InteractableId = id; Kind = kind; }
    }

    public struct DialogueStarted : IGameEvent
    {
        public string DialogueId;
        public DialogueStarted(string id) { DialogueId = id; }
    }

    public struct DialogueEnded : IGameEvent
    {
        public string DialogueId; public string EndNode;
        public DialogueEnded(string id, string endNode) { DialogueId = id; EndNode = endNode; }
    }

    public struct QuestUpdated : IGameEvent
    {
        public string QuestId; public string StageId; public QuestStatus Status;
        public QuestUpdated(string q, string stage, QuestStatus status) { QuestId = q; StageId = stage; Status = status; }
    }

    public struct CombatStateChanged : IGameEvent
    {
        public bool InCombat; public int Enemies;
        public CombatStateChanged(bool inCombat, int enemies) { InCombat = inCombat; Enemies = enemies; }
    }

    public struct BossPhaseChanged : IGameEvent
    {
        public string BossId; public int Phase;
        public BossPhaseChanged(string id, int phase) { BossId = id; Phase = phase; }
    }

    /// <summary>The player was moved instantly (spawn, respawn, fast travel, cutscene restore); cameras snap, trails reset.</summary>
    public struct PlayerTeleported : IGameEvent
    {
        public Vector3 Position; public Quaternion Rotation;
        public PlayerTeleported(Vector3 p, Quaternion r) { Position = p; Rotation = r; }
    }

    /// <summary>The death screen (or any system) asks for a respawn at the last checkpoint.</summary>
    public struct RespawnRequested : IGameEvent { }

    /// <summary>The player is alive again after death (at the last checkpoint of the scene).</summary>
    public struct PlayerRespawned : IGameEvent
    {
        public string CheckpointId;
        public PlayerRespawned(string id) { CheckpointId = id; }
    }

    /// <summary>The player rested (bed, camp fire, bench): stats restore health and stamina, time may pass.</summary>
    public struct PlayerRested : IGameEvent
    {
        public string RestPointId; public float HoursPassed;
        public PlayerRested(string id, float hours) { RestPointId = id; HoursPassed = hours; }
    }

    /// <summary>
    /// A system asks the save flow for an autosave (resting, quest milestone, region change). The owner of the save
    /// slots decides whether to honour it (not in combat, not dead, respecting the minimum interval).
    /// </summary>
    public struct AutosaveRequested : IGameEvent
    {
        public string Reason;
        public AutosaveRequested(string reason) { Reason = reason; }
    }

    public enum QuestStatus { Inactive, Active, Completed, Failed }

    /// <summary>
    /// Equipment slots. Values are explicit and APPEND-ONLY: assets and saves store them as ints, so never reorder,
    /// renumber or reuse a value. The UI orders the paper doll itself (not by value).
    /// </summary>
    public enum EquipSlot
    {
        MainHand = 0, Ranged = 1, Head = 2, Body = 3, Hands = 4, Legs = 5, Feet = 6, Cloak = 7, Amulet = 8,
        Ring1 = 9, Ring2 = 10, Quick1 = 11, Quick2 = 12, Quick3 = 13, Quick4 = 14,
        OffHand = 15,   // shield / dagger / torch
        // C18 (S1, 2026-10-31): the two melee slots and the bow. MainA / Bow / Shield name the existing values
        MainB = 16, Quiver = 17,
        MainA = MainHand, Bow = Ranged, Shield = OffHand
    }
}
