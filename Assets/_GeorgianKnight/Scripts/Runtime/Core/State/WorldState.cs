using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight.Core
{
    /// <summary>
    /// The game's "facts": every persistent yes/no, counter or tag that quests, dialogue conditions and scene objects
    /// need (chest opened, gate destroyed, met the merchant, boss defeated …). Keys are dotted lower-case strings:
    /// <c>container.kala_03.opened</c>, <c>npc.vardan.met</c>, <c>quest.liberation.stage</c>.
    /// </summary>
    public sealed class WorldState : ISaveParticipant
    {
        readonly Dictionary<string, int> ints = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<string, string> strings = new Dictionary<string, string>(StringComparer.Ordinal);

        public string SaveKey { get { return "world"; } }

        public bool Has(string key) { return ints.ContainsKey(key) || strings.ContainsKey(key); }

        public bool GetBool(string key) { int v; return ints.TryGetValue(key, out v) && v != 0; }
        public void SetBool(string key, bool value) { SetInt(key, value ? 1 : 0); }

        public int GetInt(string key, int fallback = 0) { int v; return ints.TryGetValue(key, out v) ? v : fallback; }

        public void SetInt(string key, int value)
        {
            if (string.IsNullOrEmpty(key)) return;
            int old;
            if (ints.TryGetValue(key, out old) && old == value) return;
            ints[key] = value;
            EventBus.Publish(new FactChanged(key));
        }

        public int Add(string key, int delta)
        {
            int v = GetInt(key) + delta;
            SetInt(key, v);
            return v;
        }

        public string GetString(string key, string fallback = null) { string v; return strings.TryGetValue(key, out v) ? v : fallback; }

        public void SetString(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            string old;
            if (strings.TryGetValue(key, out old) && old == value) return;
            if (value == null) strings.Remove(key); else strings[key] = value;
            EventBus.Publish(new FactChanged(key));
        }

        public void Clear() { ints.Clear(); strings.Clear(); }

        // ------------------------------------------------------------------ conditions

        /// <summary>
        /// Evaluates a tiny condition language used by dialogue / quests / triggers:
        /// <c>fact</c>, <c>!fact</c>, <c>fact &gt;= 3</c>, <c>fact == 2</c>, <c>name = 'vardan'</c>, joined by <c>&amp;&amp;</c> or <c>||</c>
        /// (no parentheses; && binds tighter). Empty condition is true.
        /// </summary>
        public bool Evaluate(string condition)
        {
            if (string.IsNullOrWhiteSpace(condition)) return true;
            foreach (var orPart in condition.Split(new[] { "||" }, StringSplitOptions.None))
            {
                bool all = true;
                foreach (var andPart in orPart.Split(new[] { "&&" }, StringSplitOptions.None))
                {
                    if (!EvaluateAtom(andPart.Trim())) { all = false; break; }
                }
                if (all) return true;
            }
            return false;
        }

        static readonly string[] ops = { ">=", "<=", "!=", "==", ">", "<", "=" };

        bool EvaluateAtom(string atom)
        {
            if (atom.Length == 0) return true;
            foreach (var op in ops)
            {
                int i = atom.IndexOf(op, StringComparison.Ordinal);
                if (i <= 0) continue;
                string key = atom.Substring(0, i).Trim();
                string rhs = atom.Substring(i + op.Length).Trim();
                if (rhs.Length >= 2 && (rhs[0] == '\'' || rhs[0] == '"'))
                {
                    string s = rhs.Substring(1, rhs.Length - 2);
                    string cur = GetString(key);
                    return op == "!=" ? cur != s : (op == "==" || op == "=") && cur == s;
                }
                int v;
                if (!int.TryParse(rhs, out v)) { Debug.LogWarning("[WorldState] bad condition '" + atom + "'"); return false; }
                int c = GetInt(key);
                switch (op)
                {
                    case ">=": return c >= v;
                    case "<=": return c <= v;
                    case "!=": return c != v;
                    case ">": return c > v;
                    case "<": return c < v;
                    default: return c == v;
                }
            }
            if (atom[0] == '!') return !GetBool(atom.Substring(1).Trim());
            return GetBool(atom);
        }

        // ------------------------------------------------------------------ save

        [Serializable] class IntEntry { public string k; public int v; }
        [Serializable] class StrEntry { public string k; public string v; }
        [Serializable] class Dto { public List<IntEntry> ints = new List<IntEntry>(); public List<StrEntry> strings = new List<StrEntry>(); }

        public string Capture()
        {
            var dto = new Dto();
            foreach (var kv in ints) dto.ints.Add(new IntEntry { k = kv.Key, v = kv.Value });
            foreach (var kv in strings) dto.strings.Add(new StrEntry { k = kv.Key, v = kv.Value });
            return JsonUtility.ToJson(dto);
        }

        /// <summary>Replaces every fact (null/empty = new game) and publishes <see cref="FactsRestored"/> once.</summary>
        public void Restore(string json)
        {
            Dto dto = null;
            if (!string.IsNullOrEmpty(json))
            {
                // parse before clearing: a corrupt section must not wipe the live facts
                try { dto = JsonUtility.FromJson<Dto>(json); }
                catch (Exception e) { Debug.LogError("[WorldState] corrupt save section, facts left unchanged: " + e.Message); return; }
            }
            ints.Clear(); strings.Clear();
            if (dto != null)
            {
                foreach (var e in dto.ints) if (!string.IsNullOrEmpty(e.k)) ints[e.k] = e.v;
                foreach (var e in dto.strings) if (!string.IsNullOrEmpty(e.k)) strings[e.k] = e.v;
            }
            EventBus.Publish(new FactsRestored());
        }
    }
}
