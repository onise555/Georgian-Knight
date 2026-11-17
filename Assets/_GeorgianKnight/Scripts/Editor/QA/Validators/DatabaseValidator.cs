using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GeorgianKnight.Core;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// DATA.* rules over the content database: ids (empty, duplicate, format), GameDatabase index freshness, dangling id
    /// references in definitions and prefabs, shop stock/pricing, loot tables and price monotonicity by rarity. Module
    /// types are read through SerializedObject by field name so the validator does not depend on in-progress assemblies.
    /// </summary>
    public sealed class DatabaseValidator : IValidator
    {
        static readonly Regex IdFormat = new Regex(@"^[a-z0-9]+(?:[_.][a-z0-9]+)*$", RegexOptions.Compiled);

        public string Id { get { return "database"; } }
        public string Category { get { return QaCategory.Data; } }

        public IEnumerable<QaIssue> Run(QaContext ctx)
        {
            var issues = new List<QaIssue>();
            var defs = ctx.Definitions;
            ctx.Progress("ids", 0f);
            Ids(defs, issues);
            Database(ctx, defs, issues);
            ctx.Progress("references", 0.3f);
            var refs = new DataRefs(defs);
            foreach (var kv in defs) refs.Check(kv.Value, kv.Key, "", issues);
            foreach (var p in ctx.FindAssets("t:Prefab"))
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (go == null) continue;
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                    refs.Check(mb, p, QaUnity.HierarchyPath(mb.transform), issues);
            }
            ctx.Progress("economy", 0.7f);
            foreach (var kv in defs)
            {
                if (DataRefs.IsA(kv.Value, "ShopDef")) Shop(kv.Key, kv.Value, refs, issues);
                if (DataRefs.IsA(kv.Value, "LootTable")) Loot(kv.Key, kv.Value, issues);
            }
            LootCycles(defs, issues);
            Prices(defs, issues);
            return issues;
        }

        static void Ids(IList<KeyValuePair<string, Definition>> defs, List<QaIssue> issues)
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in defs)
            {
                string id = kv.Value.Id;
                if (string.IsNullOrEmpty(id)) { issues.Add(QaRules.DataIdEmpty.Issue(kv.Key, 0, kv.Value.GetType().Name + " has no id")); continue; }
                if (!IdFormat.IsMatch(id)) issues.Add(QaRules.DataIdFormat.Issue(kv.Key, 0, "id '" + id + "' is not lower_snake_case ASCII"));
                string other;
                if (seen.TryGetValue(id, out other)) issues.Add(QaRules.DataIdDuplicate.Issue(kv.Key, 0, "id '" + id + "' is also used by " + other));
                else seen.Add(id, kv.Key);
            }
        }

        static void Database(QaContext ctx, IList<KeyValuePair<string, Definition>> defs, List<QaIssue> issues)
        {
            var dbs = ctx.FindAssets("t:GameDatabase");
            if (dbs.Count == 0)
            {
                if (defs.Count > 0) issues.Add(QaRules.DataDatabaseStale.Issue(QaContext.AssetRoot + "/Content", 0, "no GameDatabase asset but " + defs.Count + " definitions exist"));
                return;
            }
            foreach (var dbPath in dbs)
            {
                var db = AssetDatabase.LoadAssetAtPath<GameDatabase>(dbPath);
                if (db == null) continue;
                var indexed = new HashSet<Definition>();
                int nulls = 0;
                foreach (var d in db.EditorAll) { if (d == null) nulls++; else indexed.Add(d); }
                var missing = new List<string>();
                foreach (var kv in defs) if (!indexed.Contains(kv.Value) && !string.IsNullOrEmpty(kv.Value.Id)) missing.Add(kv.Value.Id);
                if (nulls > 0) issues.Add(QaRules.DataDatabaseStale.Issue(dbPath, 0, nulls + " empty (deleted) entries in the index"));
                if (missing.Count > 0) issues.Add(QaRules.DataDatabaseStale.Issue(dbPath, 0, missing.Count + " definition(s) not indexed: " + QaUnity.Join(missing, 8)));
            }
        }

        static void Shop(string path, Definition shop, DataRefs refs, List<QaIssue> issues)
        {
            var so = new SerializedObject(shop);
            var buy = so.FindProperty("buyMultiplier");
            var sell = so.FindProperty("sellMultiplier");
            if (buy != null && sell != null && sell.floatValue >= buy.floatValue)
                issues.Add(QaRules.DataShopArbitrage.Issue(path, 0, "sellMultiplier " + sell.floatValue + " >= buyMultiplier " + buy.floatValue));
            var stock = so.FindProperty("stock");
            if (stock == null || !stock.isArray) return;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < stock.arraySize; i++)
            {
                var e = stock.GetArrayElementAtIndex(i);
                var idp = e.FindPropertyRelative("itemId");
                var mult = e.FindPropertyRelative("priceMultiplier");
                string id = idp != null ? idp.stringValue : "";
                string at = "stock[" + i + "]";
                if (string.IsNullOrEmpty(id)) { issues.Add(QaRules.DataShopStock.Issue(path, 0, at + " has no itemId")); continue; }
                if (!ids.Add(id)) issues.Add(QaRules.DataShopStock.Issue(path, 0, "", at + " lists '" + id + "' twice", QaSeverity.Major, null));
                if (mult != null && mult.floatValue <= 0f) issues.Add(QaRules.DataShopStock.Issue(path, 0, at + " ('" + id + "') has priceMultiplier " + mult.floatValue));
                Definition item;
                if (refs.TryGet(id, out item) && IsQuestItem(item)) issues.Add(QaRules.DataShopStock.Issue(path, 0, at + " sells quest item '" + id + "'"));
            }
        }

        static bool IsQuestItem(Definition item)
        {
            if (DataRefs.IsA(item, "QuestItemDef")) return true;
            var cat = new SerializedObject(item).FindProperty("category");
            return cat != null && cat.propertyType == SerializedPropertyType.Enum && cat.enumValueIndex >= 0 &&
                   cat.enumValueIndex < cat.enumNames.Length && cat.enumNames[cat.enumValueIndex] == "Quest";
        }

        static void Loot(string path, Definition table, List<QaIssue> issues)
        {
            var so = new SerializedObject(table);
            Range(so, "minRolls", "maxRolls", path, issues);
            Range(so, "minSilver", "maxSilver", path, issues);
            int total = 0;
            foreach (var list in new[] { "guaranteed", "entries" })
            {
                var arr = so.FindProperty(list);
                if (arr == null || !arr.isArray) continue;
                total += arr.arraySize;
                for (int i = 0; i < arr.arraySize; i++)
                {
                    var e = arr.GetArrayElementAtIndex(i);
                    string at = list + "[" + i + "]";
                    string item = Str(e, "itemId"), nested = Str(e, "tableId");
                    if (string.IsNullOrEmpty(item) == string.IsNullOrEmpty(nested))
                        issues.Add(QaRules.DataLootTable.Issue(path, 0, at + (string.IsNullOrEmpty(item) ? " names neither an item nor a table" : " names both an item and a table")));
                    var w = e.FindPropertyRelative("weight");
                    if (list == "entries" && w != null && w.floatValue <= 0f) issues.Add(QaRules.DataLootTable.Issue(path, 0, at + " has weight " + w.floatValue));
                    var mn = e.FindPropertyRelative("minCount");
                    var mx = e.FindPropertyRelative("maxCount");
                    if (mn != null && mx != null && mn.intValue > mx.intValue) issues.Add(QaRules.DataLootTable.Issue(path, 0, at + " minCount " + mn.intValue + " > maxCount " + mx.intValue));
                }
            }
            var maxSilver = so.FindProperty("maxSilver");
            if (total == 0 && (maxSilver == null || maxSilver.intValue <= 0))
                issues.Add(QaRules.DataLootTable.Issue(path, 0, "", "table drops nothing (no entries, no silver)", QaSeverity.Major, null));
        }

        static string Str(SerializedProperty e, string name)
        {
            var p = e.FindPropertyRelative(name);
            return p != null ? p.stringValue : "";
        }

        static void Range(SerializedObject so, string min, string max, string path, List<QaIssue> issues)
        {
            var a = so.FindProperty(min);
            var b = so.FindProperty(max);
            if (a != null && b != null && a.intValue > b.intValue) issues.Add(QaRules.DataLootTable.Issue(path, 0, min + " " + a.intValue + " > " + max + " " + b.intValue));
        }

        static void LootCycles(IList<KeyValuePair<string, Definition>> defs, List<QaIssue> issues)
        {
            var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in defs)
            {
                if (!DataRefs.IsA(kv.Value, "LootTable") || string.IsNullOrEmpty(kv.Value.Id)) continue;
                var so = new SerializedObject(kv.Value);
                var next = new List<string>();
                foreach (var list in new[] { "guaranteed", "entries" })
                {
                    var arr = so.FindProperty(list);
                    if (arr == null || !arr.isArray) continue;
                    for (int i = 0; i < arr.arraySize; i++)
                    {
                        string t = Str(arr.GetArrayElementAtIndex(i), "tableId");
                        if (!string.IsNullOrEmpty(t)) next.Add(t);
                    }
                }
                graph[kv.Value.Id] = next;
                paths[kv.Value.Id] = kv.Key;
            }
            var state = new Dictionary<string, int>(StringComparer.Ordinal);   // 1 = on stack, 2 = done
            foreach (var id in graph.Keys)
            {
                var stack = new List<string>();
                string cycle = Visit(id, graph, state, stack);
                if (cycle == null) continue;
                issues.Add(QaRules.DataLootTable.Issue(paths[id], 0, "nested loot tables form a cycle: " + cycle));
                foreach (var n in stack) state[n] = 2;     // report each cycle once
            }
        }

        static string Visit(string id, Dictionary<string, List<string>> graph, Dictionary<string, int> state, List<string> stack)
        {
            int s;
            if (state.TryGetValue(id, out s))
            {
                if (s == 2) return null;
                int at = stack.IndexOf(id);
                return string.Join(" → ", stack.GetRange(at, stack.Count - at).ToArray()) + " → " + id;
            }
            List<string> next;
            if (!graph.TryGetValue(id, out next)) return null;
            state[id] = 1;
            stack.Add(id);
            foreach (var n in next)
            {
                string c = Visit(n, graph, state, stack);
                if (c != null) return c;
            }
            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
            return null;
        }

        static void Prices(IList<KeyValuePair<string, Definition>> defs, List<QaIssue> issues)
        {
            var groups = new Dictionary<string, Dictionary<int, List<int>>>(StringComparer.Ordinal);
            var groupPath = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in defs)
            {
                if (!DataRefs.IsA(kv.Value, "ItemDef")) continue;
                var so = new SerializedObject(kv.Value);
                var value = so.FindProperty("value");
                var rarity = so.FindProperty("rarity");
                if (value == null || rarity == null) continue;
                if (value.intValue <= 0 && !IsQuestItem(kv.Value))
                    issues.Add(QaRules.DataItemValue.Issue(kv.Key, 0, kv.Value.Id + " is worth " + value.intValue + " silver"));
                var cat = so.FindProperty("category");
                string group = kv.Value.GetType().Name + (kv.Value.GetType().Name == "ItemDef" && cat != null ? "/" + cat.enumValueIndex : "");
                Dictionary<int, List<int>> tiers;
                if (!groups.TryGetValue(group, out tiers)) { tiers = new Dictionary<int, List<int>>(); groups.Add(group, tiers); groupPath[group] = kv.Key; }
                List<int> values;
                if (!tiers.TryGetValue(rarity.enumValueIndex, out values)) { values = new List<int>(); tiers.Add(rarity.enumValueIndex, values); }
                values.Add(value.intValue);
            }
            foreach (var g in groups)
            {
                var tiers = new List<int>(g.Value.Keys);
                tiers.Sort();
                for (int i = 1; i < tiers.Count; i++)
                {
                    int lower = Median(g.Value[tiers[i - 1]]), higher = Median(g.Value[tiers[i]]);
                    if (higher < lower)
                        issues.Add(QaRules.DataPriceTier.Issue(groupPath[g.Key], 0, g.Key + ": rarity tier " + tiers[i] + " median " + higher + " < tier " + tiers[i - 1] + " median " + lower));
                }
            }
        }

        static int Median(List<int> values)
        {
            var v = new List<int>(values);
            v.Sort();
            return v.Count == 0 ? 0 : v[v.Count / 2];
        }
    }
}
