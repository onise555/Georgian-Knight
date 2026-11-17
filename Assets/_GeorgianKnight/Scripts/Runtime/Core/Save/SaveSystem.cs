using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GeorgianKnight.Core
{
    /// <summary>Anything that persists. Each participant owns one JSON section of the save file.</summary>
    public interface ISaveParticipant
    {
        /// <summary>Stable section name, e.g. "player", "inventory", "quests".</summary>
        string SaveKey { get; }
        string Capture();
        void Restore(string json);
    }

    [Serializable]
    public sealed class SaveMeta
    {
        public int slot;
        public string scene;
        public string chapterId;
        public string locationKey;     // localization key of the place name shown in the load menu
        public float playtime;         // seconds
        public string utc;             // ISO 8601
        public string gameVersion;
        public int level;
    }

    [Serializable]
    public sealed class SaveSection
    {
        public string key;
        public string json;
    }

    [Serializable]
    public sealed class SaveGame
    {
        public int version;
        public SaveMeta meta = new SaveMeta();
        public List<SaveSection> sections = new List<SaveSection>();

        public string Find(string key)
        {
            foreach (var s in sections) if (s.key == key) return s.json;
            return null;
        }
    }

    public interface ISaveSystem
    {
        void Register(ISaveParticipant participant);
        void Unregister(ISaveParticipant participant);
        bool Save(int slot, SaveMeta meta);
        SaveGame Read(int slot);
        bool Apply(SaveGame game);
        bool Exists(int slot);
        bool Delete(int slot);
        IList<SaveMeta> List();
        /// <summary>Resets every participant to its new-game state and publishes <see cref="NewGameStarted"/>.</summary>
        void NewGame();
        SaveGame Current { get; }
    }

    /// <summary>
    /// File-based save system. Slots: <see cref="AutoSlot"/>, 1..<see cref="ManualSlots"/>, <see cref="QuickSlot"/>.
    /// Writes are atomic (temp file + replace, previous file kept as .bak and used if the main file is corrupt).
    /// Sections of participants that are not registered at load time are kept and restored when they register,
    /// and sections no participant knows about are carried forward unchanged.
    /// </summary>
    public sealed class SaveSystem : ISaveSystem
    {
        public const int CurrentVersion = 1;
        public const int AutoSlot = 0;
        public const int ManualSlots = 6;
        public const int QuickSlot = 9;

        readonly List<ISaveParticipant> participants = new List<ISaveParticipant>();
        readonly Dictionary<string, string> pending = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<int, Func<SaveGame, SaveGame>> migrations = new Dictionary<int, Func<SaveGame, SaveGame>>();
        readonly string directory;

        public SaveGame Current { get; private set; }

        public SaveSystem(string dir = null)
        {
            directory = dir ?? Path.Combine(Application.persistentDataPath, "saves");
        }

        /// <summary>Registers an upgrade from <paramref name="fromVersion"/> to fromVersion+1.</summary>
        public void AddMigration(int fromVersion, Func<SaveGame, SaveGame> upgrade) { migrations[fromVersion] = upgrade; }

        public void Register(ISaveParticipant p)
        {
            if (p == null || participants.Contains(p)) return;
            for (int i = 0; i < participants.Count; i++)
                if (participants[i].SaveKey == p.SaveKey) Debug.LogWarning("[Save] two participants share key '" + p.SaveKey + "'");
            participants.Add(p);
            string json;
            if (pending.TryGetValue(p.SaveKey, out json))
            {
                pending.Remove(p.SaveKey);
                SafeRestore(p, json);
            }
        }

        public void Unregister(ISaveParticipant p)
        {
            if (p == null) return;
            // keep its latest state so a scene reload restores it
            if (participants.Remove(p)) pending[p.SaveKey] = SafeCapture(p);
        }

        string PathOf(int slot) { return Path.Combine(directory, "slot_" + slot + ".json"); }

        public bool Exists(int slot) { return File.Exists(PathOf(slot)) || File.Exists(PathOf(slot) + ".bak"); }

        public bool Save(int slot, SaveMeta meta)
        {
            try
            {
                var game = new SaveGame { version = CurrentVersion, meta = meta ?? new SaveMeta() };
                game.meta.slot = slot;
                game.meta.utc = DateTime.UtcNow.ToString("o");
                game.meta.gameVersion = Application.version;
                var written = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in participants)
                {
                    if (!written.Add(p.SaveKey)) continue;
                    game.sections.Add(new SaveSection { key = p.SaveKey, json = SafeCapture(p) });
                }
                foreach (var kv in pending)
                    if (written.Add(kv.Key)) game.sections.Add(new SaveSection { key = kv.Key, json = kv.Value });
                if (Current != null)
                    foreach (var s in Current.sections)
                        if (written.Add(s.key)) game.sections.Add(s);   // unknown sections survive

                Directory.CreateDirectory(directory);
                WriteAtomic(PathOf(slot), JsonUtility.ToJson(game, true));
                Current = game;
                EventBus.Publish(new GameSaved(slot, true));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EventBus.Publish(new GameSaved(slot, false));
                return false;
            }
        }

        static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, System.Text.Encoding.UTF8);
            if (File.Exists(path))
            {
                string bak = path + ".bak";
                // File.Replace is atomic where supported; otherwise (some file systems / platforms) fall back to copies
                try { File.Replace(tmp, path, bak); return; }
                catch (PlatformNotSupportedException) { Debug.Log("[Save] atomic replace unsupported, using copy fallback"); }
                catch (IOException e) { Debug.LogWarning("[Save] atomic replace failed (" + e.Message + "), using copy fallback"); }
                File.Copy(path, bak, true);
                File.Copy(tmp, path, true);
                File.Delete(tmp);
            }
            else File.Move(tmp, path);
        }

        public SaveGame Read(int slot)
        {
            foreach (var path in new[] { PathOf(slot), PathOf(slot) + ".bak" })
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var g = JsonUtility.FromJson<SaveGame>(File.ReadAllText(path, System.Text.Encoding.UTF8));
                    if (g == null || g.meta == null) throw new InvalidDataException("empty save");
                    return Migrate(g);
                }
                catch (Exception e) { Debug.LogWarning("[Save] cannot read " + path + ": " + e.Message); }
            }
            return null;
        }

        SaveGame Migrate(SaveGame g)
        {
            while (g.version < CurrentVersion)
            {
                Func<SaveGame, SaveGame> m;
                if (!migrations.TryGetValue(g.version, out m)) { Debug.LogWarning("[Save] no migration from v" + g.version); g.version++; continue; }
                g = m(g);
                g.version++;
            }
            if (g.version > CurrentVersion) Debug.LogWarning("[Save] file from a newer version (" + g.version + ")");
            return g;
        }

        /// <summary>Distributes a loaded game to the participants (late participants receive theirs on Register).</summary>
        public bool Apply(SaveGame game)
        {
            if (game == null) return false;
            Current = game;
            pending.Clear();
            foreach (var s in game.sections) pending[s.key] = s.json;
            foreach (var p in participants.ToArray())
            {
                string json;
                if (pending.TryGetValue(p.SaveKey, out json)) { pending.Remove(p.SaveKey); SafeRestore(p, json); }
                else SafeRestore(p, null);
            }
            EventBus.Publish(new GameLoaded(game.meta != null ? game.meta.slot : -1, true));
            return true;
        }

        /// <summary>Starts a fresh game: every participant resets.</summary>
        public void NewGame()
        {
            Current = null;
            pending.Clear();
            foreach (var p in participants.ToArray()) SafeRestore(p, null);
            EventBus.Publish(new NewGameStarted());
        }

        public bool Delete(int slot)
        {
            bool any = false;
            foreach (var path in new[] { PathOf(slot), PathOf(slot) + ".bak" })
                if (File.Exists(path)) { File.Delete(path); any = true; }
            return any;
        }

        public IList<SaveMeta> List()
        {
            var list = new List<SaveMeta>();
            if (!Directory.Exists(directory)) return list;
            foreach (var slot in new[] { AutoSlot, QuickSlot }) AddMeta(list, slot);
            for (int i = 1; i <= ManualSlots; i++) AddMeta(list, i);
            list.Sort((a, b) => string.CompareOrdinal(b.utc ?? "", a.utc ?? ""));
            return list;
        }

        void AddMeta(List<SaveMeta> list, int slot)
        {
            if (!Exists(slot)) return;
            var g = Read(slot);
            if (g != null) { g.meta.slot = slot; list.Add(g.meta); }
        }

        static string SafeCapture(ISaveParticipant p)
        {
            try { return p.Capture(); }
            catch (Exception e) { Debug.LogException(e); return null; }
        }

        static void SafeRestore(ISaveParticipant p, string json)
        {
            try { p.Restore(json); }
            catch (Exception e) { Debug.LogError("[Save] restore failed for '" + p.SaveKey + "': " + e.Message); Debug.LogException(e); }
        }
    }
}
