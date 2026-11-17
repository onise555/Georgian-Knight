using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// LOC.* rules over Resources/Localization/{ka,en}: JSON validity, duplicate keys, ka/en parity, empty / placeholder /
    /// untranslated / mojibake / wrong-script values, format placeholder parity and typography; plus keys required by
    /// definitions (name/desc), keys used in code, and hard-coded text in UXML.
    /// </summary>
    public sealed class LocalizationValidator : IValidator
    {
        /// <summary>One string table entry with its origin.</summary>
        sealed class Entry
        {
            public string Value, Path;
            public int Line;
        }

        static readonly Regex UxmlTextRx = new Regex("\\b(text|label|tooltip|title)\\s*=\\s*\"([^\"]*)\"", RegexOptions.Compiled);
        const string Primary = "ka";

        public string Id { get { return "localization"; } }
        public string Category { get { return QaCategory.Localization; } }

        public IEnumerable<QaIssue> Run(QaContext ctx)
        {
            var issues = new List<QaIssue>();
            var tables = new Dictionary<string, Dictionary<string, Entry>>(StringComparer.Ordinal);
            foreach (var lang in ctx.Settings.localization.languages) tables[lang] = Load(ctx, lang, issues);
            ctx.Progress("values", 0.3f);
            Values(tables, issues);
            ctx.Progress("definitions", 0.6f);
            Definitions(ctx, tables, issues);
            ctx.Progress("code and UXML", 0.8f);
            CodeKeys(ctx, tables, issues);
            Uxml(ctx, issues);
            return issues;
        }

        static Dictionary<string, Entry> Load(QaContext ctx, string lang, List<QaIssue> issues)
        {
            var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
            string root = QaContext.AssetRoot + "/Resources/Localization";
            var files = new List<string>();
            string dir = Path.Combine(ctx.ProjectRoot, root + "/" + lang);
            if (Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories));
            string single = Path.Combine(ctx.ProjectRoot, root + "/" + lang + ".json");
            if (File.Exists(single)) files.Add(single);
            files.Sort(StringComparer.Ordinal);
            foreach (var file in files)
            {
                string rel = file.Substring(ctx.ProjectRoot.Length).Replace('\\', '/').TrimStart('/');
                QaJsonNode doc;
                try { doc = QaJson.Parse(File.ReadAllText(file, Encoding.UTF8)); }
                catch (QaJsonException e) { issues.Add(QaRules.LocBadJson.Issue(rel, e.Line, e.Message)); continue; }
                var entries = doc.Kind == QaJsonKind.Object ? doc["entries"] : null;
                if (entries == null || entries.Kind != QaJsonKind.Array) { issues.Add(QaRules.LocBadJson.Issue(rel, 1, "missing \"entries\" array")); continue; }
                foreach (var e in entries.Items)
                {
                    string k = e.Kind == QaJsonKind.Object ? e.Str("k") : null;
                    string v = e.Kind == QaJsonKind.Object ? e.Str("v") : null;
                    if (string.IsNullOrEmpty(k) || v == null) { issues.Add(QaRules.LocBadJson.Issue(rel, e.Line, "entry without string \"k\"/\"v\"")); continue; }
                    Entry other;
                    if (map.TryGetValue(k, out other))
                        issues.Add(QaRules.LocDuplicateKey.Issue(rel, e.Line, "'" + k + "' also defined at " + other.Path + ":" + other.Line + " (the later one wins)"));
                    map[k] = new Entry { Value = v, Path = rel, Line = e.Line };
                }
            }
            return map;
        }

        static void Values(Dictionary<string, Dictionary<string, Entry>> tables, List<QaIssue> issues)
        {
            Dictionary<string, Entry> ka, en;
            tables.TryGetValue(Primary, out ka);
            tables.TryGetValue("en", out en);
            foreach (var lang in tables)
                foreach (var kv in lang.Value)
                {
                    var e = kv.Value;
                    string v = e.Value;
                    if (v.Trim().Length == 0) { issues.Add(QaRules.LocEmpty.Issue(e.Path, e.Line, lang.Key + " '" + kv.Key + "' is empty")); continue; }
                    if (LocText.IsMojibake(v)) issues.Add(QaRules.LocMojibake.Issue(e.Path, e.Line, lang.Key + " '" + kv.Key + "': \"" + CodeRules.Shorten(v, 40) + "\""));
                    if (LocText.IsPlaceholder(v)) issues.Add(QaRules.LocPlaceholder.Issue(e.Path, e.Line, lang.Key + " '" + kv.Key + "': \"" + CodeRules.Shorten(v, 40) + "\""));
                    if (v == kv.Key) issues.Add(QaRules.LocUntranslated.Issue(e.Path, e.Line, lang.Key + " '" + kv.Key + "' value equals its key"));
                    Script(lang.Key, kv.Key, e, issues);
                    var typo = LocText.Typography(v, lang.Key == Primary);
                    if (typo.Count > 0) issues.Add(QaRules.LocTypography.Issue(e.Path, e.Line, lang.Key + " '" + kv.Key + "': " + string.Join(", ", typo.ToArray())));
                }
            if (ka == null || en == null) return;
            foreach (var kv in en)
            {
                Entry k;
                if (!ka.TryGetValue(kv.Key, out k))
                    issues.Add(QaRules.LocKeyParity.Issue(kv.Value.Path, kv.Value.Line, "", "'" + kv.Key + "' has no Georgian (ka) translation", QaSeverity.Critical, null));
                else if (!LocText.SameFormat(k.Value, kv.Value.Value))
                    issues.Add(QaRules.LocFormatMismatch.Issue(k.Path, k.Line, "'" + kv.Key + "': ka uses {" + string.Join(",", Indices(k.Value)) + "}, en uses {" + string.Join(",", Indices(kv.Value.Value)) + "}"));
            }
            foreach (var kv in ka)
                if (!en.ContainsKey(kv.Key))
                    issues.Add(QaRules.LocKeyParity.Issue(kv.Value.Path, kv.Value.Line, "", "'" + kv.Key + "' has no English (en) translation", QaSeverity.Major, null));
        }

        static string[] Indices(string v)
        {
            var l = LocText.FormatIndices(v);
            return l.ConvertAll(i => i.ToString()).ToArray();
        }

        static void Script(string lang, string key, Entry e, List<QaIssue> issues)
        {
            int geo, latin;
            LocText.CountLetters(e.Value, out geo, out latin);
            if (lang == Primary)
            {
                if (geo == 0 && latin >= 3) issues.Add(QaRules.LocNotGeorgian.Issue(e.Path, e.Line, "ka '" + key + "' has no Georgian letters: \"" + CodeRules.Shorten(e.Value, 40) + "\""));
                else if (geo > 0)
                {
                    var foreign = LocText.ForeignLatinWords(e.Value);
                    if (foreign.Count > 0) issues.Add(QaRules.LocMixedScript.Issue(e.Path, e.Line, "ka '" + key + "' contains Latin words: " + string.Join(", ", foreign.ToArray())));
                }
            }
            else if (geo > 0)
                issues.Add(QaRules.LocMixedScript.Issue(e.Path, e.Line, lang + " '" + key + "' contains Georgian text"));
        }

        static void Definitions(QaContext ctx, Dictionary<string, Dictionary<string, Entry>> tables, List<QaIssue> issues)
        {
            var lim = ctx.Settings.localization;
            foreach (var kv in ctx.Definitions)
            {
                var d = kv.Value;
                if (string.IsNullOrEmpty(d.Id) || lim.nonPlayerFacingPrefixes.Contains(d.KeyPrefix)) continue;
                Require(kv.Key, d.NameKey, tables, issues);
                if (lim.descriptionRequired.Contains(d.KeyPrefix)) Require(kv.Key, d.DescriptionKey, tables, issues);
            }
        }

        static void Require(string path, string key, Dictionary<string, Dictionary<string, Entry>> tables, List<QaIssue> issues)
        {
            foreach (var t in tables)
                if (!t.Value.ContainsKey(key))
                    issues.Add(QaRules.LocMissingKey.Issue(path, 0, "", "'" + key + "' missing in " + t.Key, t.Key == Primary ? QaSeverity.Critical : QaSeverity.Major, null));
        }

        static void CodeKeys(QaContext ctx, Dictionary<string, Dictionary<string, Entry>> tables, List<QaIssue> issues)
        {
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in ctx.CodeLocKeys)
                foreach (var t in tables)
                {
                    if (t.Value.ContainsKey(r.Key) || !reported.Add(r.Key + "|" + t.Key)) continue;
                    var sev = r.Direct && t.Key == Primary ? QaSeverity.Critical : QaSeverity.Major;
                    issues.Add(QaRules.LocCodeKeyMissing.Issue(r.Path, r.Line, "", "'" + r.Key + "' is used in code but missing in " + t.Key, sev, null));
                }
        }

        static void Uxml(QaContext ctx, List<QaIssue> issues)
        {
            string dir = Path.Combine(ctx.ProjectRoot, QaContext.AssetRoot + "/UI");
            if (!Directory.Exists(dir)) return;
            var files = new List<string>(Directory.GetFiles(dir, "*.uxml", SearchOption.AllDirectories));
            files.Sort(StringComparer.Ordinal);
            foreach (var f in files)
            {
                string rel = f.Substring(ctx.ProjectRoot.Length).Replace('\\', '/').TrimStart('/');
                var lines = File.ReadAllLines(f, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                    foreach (Match m in UxmlTextRx.Matches(lines[i]))
                    {
                        string v = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value);
                        if (CodeRules.LooksUserFacing(v))
                            issues.Add(QaRules.LocUxmlLiteral.Issue(rel, i + 1, m.Groups[1].Value + "=\"" + CodeRules.Shorten(v, 40) + "\""));
                    }
            }
        }
    }
}
