using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight.Core
{
    /// <summary>
    /// String tables per language, authored as UTF-8 JSON TextAssets in <c>Resources/Localization/&lt;lang&gt;.json</c>:
    /// <code>{ "entries": [ { "k": "ui.menu.new_game", "v": "ახალი თამაში" } ] }</code>
    /// Lookup falls back to English, then to "#key" (missing keys are reported once).
    /// Formatting uses {0}, {1} placeholders.
    /// </summary>
    public static class Loc
    {
        [Serializable] class Entry { public string k = ""; public string v = ""; }
        [Serializable] class Table { public List<Entry> entries = new List<Entry>(); }

        public const string Fallback = "en";
        public static readonly string[] Languages = { "ka", "en" };

        static Dictionary<string, string> current = new Dictionary<string, string>(StringComparer.Ordinal);
        static Dictionary<string, string> fallback = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly HashSet<string> reported = new HashSet<string>(StringComparer.Ordinal);
        static string language;

        public static string Language { get { return language; } }

        public static void SetLanguage(string lang)
        {
            if (string.IsNullOrEmpty(lang)) lang = "ka";
            language = lang;
            current = LoadTable(lang);
            fallback = lang == Fallback ? current : LoadTable(Fallback);
            reported.Clear();
            EventBus.Publish(new LanguageChanged(lang));
        }

        static Dictionary<string, string> LoadTable(string lang)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            var assets = Resources.LoadAll<TextAsset>("Localization/" + lang);   // folder of several tables
            var single = Resources.Load<TextAsset>("Localization/" + lang);      // or one file
            var list = new List<TextAsset>(assets);
            if (single != null) list.Add(single);
            foreach (var ta in list)
            {
                try
                {
                    var t = JsonUtility.FromJson<Table>(ta.text);
                    if (t == null || t.entries == null) continue;
                    foreach (var e in t.entries)
                        if (!string.IsNullOrEmpty(e.k)) d[e.k] = e.v;
                }
                catch (Exception ex) { Debug.LogError("[Loc] bad table " + ta.name + ": " + ex.Message); }
            }
            return d;
        }

        static void EnsureLoaded() { if (language == null) SetLanguage("ka"); }

        public static bool Has(string key)
        {
            EnsureLoaded();
            return key != null && (current.ContainsKey(key) || fallback.ContainsKey(key));
        }

        public static string Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            EnsureLoaded();
            string v;
            if (current.TryGetValue(key, out v)) return v;
            if (fallback.TryGetValue(key, out v)) return v;
            if (reported.Add(key)) Debug.LogWarning("[Loc] missing '" + key + "' (" + language + ")");
            return "#" + key;
        }

        /// <summary>Returns the translation, or null if the key does not exist (no warning).</summary>
        public static string GetOrNull(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            EnsureLoaded();
            string v;
            if (current.TryGetValue(key, out v)) return v;
            return fallback.TryGetValue(key, out v) ? v : null;
        }

        public static string Format(string key, params object[] args)
        {
            string f = Get(key);
            try { return string.Format(f, args); }
            catch (FormatException) { return f; }
        }

        /// <summary>For tests and tools: install a table directly.</summary>
        public static void InstallForTests(string lang, IDictionary<string, string> entries)
        {
            language = lang;
            current = new Dictionary<string, string>(entries, StringComparer.Ordinal);
            fallback = current;
            reported.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { language = null; current.Clear(); fallback.Clear(); reported.Clear(); }
    }
}
