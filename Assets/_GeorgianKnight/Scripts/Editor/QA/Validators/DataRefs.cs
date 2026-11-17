using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GeorgianKnight.Core;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// Resolves string id references in serialized data ("itemId", "lootTableId", "merchantId", "skillIds" …) against the
    /// definitions in the project, by field-name convention, so it also covers modules this assembly does not reference.
    /// Used for definitions, prefabs and scene objects.
    /// </summary>
    public sealed class DataRefs
    {
        static readonly Regex RefField = new Regex(@"^(?:(item|table|npc|merchant|dialogue|quest|cutscene|region|location|skill|shop)|[a-z]\w*(Item|Table|Npc|Merchant|Dialogue|Quest|Cutscene|Region|Location|Skill|Shop))Ids?$", RegexOptions.Compiled);
        static readonly Regex FieldOfPath = new Regex(@"([A-Za-z_]\w*)(?:\.Array\.data\[\d+\])?$", RegexOptions.Compiled);

        static readonly Dictionary<string, string> ExpectedBase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "item", "ItemDef" }, { "table", "LootTable" }, { "npc", "NpcDef" }, { "merchant", "NpcDef" }, { "dialogue", "DialogueDef" },
            { "quest", "QuestDef" }, { "cutscene", "CutsceneDef" }, { "region", "RegionDef" }, { "location", "MapLocationDef" },
            { "skill", "SkillDef" }, { "shop", "ShopDef" }
        };

        readonly Dictionary<string, Definition> byId = new Dictionary<string, Definition>(StringComparer.Ordinal);
        readonly HashSet<string> knownTypes = new HashSet<string>(StringComparer.Ordinal);

        public DataRefs(IList<KeyValuePair<string, Definition>> definitions)
        {
            foreach (var kv in definitions)
                if (kv.Value != null && !string.IsNullOrEmpty(kv.Value.Id) && !byId.ContainsKey(kv.Value.Id)) byId.Add(kv.Value.Id, kv.Value);
            foreach (var t in TypeCache.GetTypesDerivedFrom<Definition>()) knownTypes.Add(t.Name);
        }

        public bool TryGet(string id, out Definition def) { return byId.TryGetValue(id ?? "", out def); }

        public static bool IsA(Definition d, string typeName)
        {
            for (var t = d.GetType(); t != null; t = t.BaseType) if (t.Name == typeName) return true;
            return false;
        }

        /// <summary>Reports every id-reference string field of <paramref name="target"/> that names no definition (or the wrong kind).</summary>
        public void Check(UnityEngine.Object target, string path, string obj, List<QaIssue> issues)
        {
            if (target == null) return;
            var so = new SerializedObject(target);
            var it = so.GetIterator();
            while (it.Next(true))
            {
                if (it.propertyType != SerializedPropertyType.String) continue;
                string value = it.stringValue;
                if (string.IsNullOrEmpty(value)) continue;
                var f = FieldOfPath.Match(it.propertyPath);
                if (!f.Success) continue;
                var m = RefField.Match(f.Groups[1].Value);
                if (!m.Success) continue;
                string kind = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToLowerInvariant();
                string where = (string.IsNullOrEmpty(obj) ? "" : obj + " ") + target.GetType().Name + "." + it.propertyPath.Replace(".Array.data", "");
                Definition def;
                if (!byId.TryGetValue(value, out def))
                {
                    issues.Add(QaRules.DataDanglingId.Issue(path, 0, obj, where + " = '" + value + "' names no definition", QaSeverity.Critical, null));
                    continue;
                }
                string expected;
                if (ExpectedBase.TryGetValue(kind, out expected) && knownTypes.Contains(expected) && !IsA(def, expected))
                    issues.Add(QaRules.DataDanglingId.Issue(path, 0, obj, where + " = '" + value + "' is a " + def.GetType().Name + ", expected " + expected, QaSeverity.Critical, null));
            }
        }
    }
}
