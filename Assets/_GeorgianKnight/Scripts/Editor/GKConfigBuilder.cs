using System;
using System.Collections.Generic;
using System.Linq;
using GeorgianKnight.Core;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Builds the boot configuration: one asset per concrete <see cref="GameModule"/> type (Settings/Modules),
    /// the <see cref="GameDatabase"/> index of every <see cref="Definition"/> (with duplicate-id validation), and
    /// Resources/GameConfig that GameRoot loads. Idempotent.
    /// </summary>
    public static class GKConfigBuilder
    {
        const string Root = "Assets/_GeorgianKnight";
        const string ModulesDir = Root + "/Settings/Modules";
        const string ResourcesDir = Root + "/Resources";
        const string DatabasePath = Root + "/Content/GameDatabase.asset";
        const string ConfigPath = ResourcesDir + "/GameConfig.asset";

        /// <summary>Install order: base services first, then features, UI last.</summary>
        static int Order(Type t)
        {
            string n = t.Name;
            if (n.StartsWith("GameplayBase")) return 0;
            if (n.StartsWith("Audio")) return 10;
            if (n.StartsWith("Items")) return 20;
            if (n.StartsWith("Skill")) return 30;
            if (n.StartsWith("World")) return 40;
            if (n.StartsWith("Narrative")) return 50;
            if (n.StartsWith("Ui") || n.StartsWith("UI")) return 100;
            return 60;
        }

        [GKCommand("config", "GameConfig + module assets + database")]
        public static void Build(string arg)
        {
            GKPaths.Ensure(ModulesDir);
            GKPaths.Ensure(ResourcesDir);
            GKPaths.Ensure(Root + "/Content");

            // ---- modules
            var modules = new List<GameModule>();
            foreach (var t in TypeCache.GetTypesDerivedFrom<GameModule>().Where(t => !t.IsAbstract).OrderBy(Order).ThenBy(t => t.Name))
            {
                string p = ModulesDir + "/" + t.Name + ".asset";
                var m = AssetDatabase.LoadAssetAtPath<GameModule>(p);
                if (m == null)
                {
                    m = (GameModule)ScriptableObject.CreateInstance(t);
                    AssetDatabase.CreateAsset(m, p);
                }
                modules.Add(m);
            }

            // ---- database
            var db = BuildDatabase();

            // ---- config
            var cfg = AssetDatabase.LoadAssetAtPath<GameConfig>(ConfigPath);
            if (cfg == null) { cfg = ScriptableObject.CreateInstance<GameConfig>(); AssetDatabase.CreateAsset(cfg, ConfigPath); }
            cfg.database = db;
            cfg.modules = modules;
            EditorUtility.SetDirty(cfg);
            AssetDatabase.SaveAssets();
            GKBridge.Log("config: " + modules.Count + " modules (" + string.Join(", ", modules.Select(m => m.GetType().Name).ToArray()) + "), database " + db.Count + " definitions");
        }

        [GKCommand("database", "rebuild the GameDatabase index and validate ids")]
        public static void Database(string arg) { BuildDatabase(); AssetDatabase.SaveAssets(); }

        public static GameDatabase BuildDatabase()
        {
            var db = AssetDatabase.LoadAssetAtPath<GameDatabase>(DatabasePath);
            if (db == null) { db = ScriptableObject.CreateInstance<GameDatabase>(); AssetDatabase.CreateAsset(db, DatabasePath); }
            var defs = new List<Definition>();
            var seen = new Dictionary<string, Definition>(StringComparer.Ordinal);
            int problems = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Definition", new[] { Root }))
            {
                var d = AssetDatabase.LoadAssetAtPath<Definition>(AssetDatabase.GUIDToAssetPath(guid));
                if (d == null) continue;
                if (string.IsNullOrEmpty(d.Id)) { GKBridge.Log("database: missing id on " + AssetDatabase.GetAssetPath(d)); problems++; continue; }
                Definition other;
                if (seen.TryGetValue(d.Id, out other))
                {
                    // ids are global across every content kind (an NPC and a dialogue may not share one)
                    string msg = "database: DUPLICATE id '" + d.Id + "': " + AssetDatabase.GetAssetPath(other) + " / " + AssetDatabase.GetAssetPath(d);
                    GKBridge.Log(msg);
                    Debug.LogError("[GameDatabase] " + msg);
                    problems++;
                    continue;
                }
                seen.Add(d.Id, d);
                defs.Add(d);
            }
            defs.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            db.EditorSet(defs);
            EditorUtility.SetDirty(db);
            GKBridge.Log("database: " + defs.Count + " definitions, " + problems + " problem(s)");
            return db;
        }
    }
}
