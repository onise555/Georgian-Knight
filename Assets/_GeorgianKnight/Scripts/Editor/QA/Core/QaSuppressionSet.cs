using System;
using System.Collections.Generic;
using System.Globalization;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// One accepted exception from Docs/qa_suppressions.json. <see cref="Id"/>, <see cref="Path"/> and
    /// <see cref="Object"/> are globs (<c>*</c> within a path segment, <c>**</c> across segments, <c>?</c> one character);
    /// <see cref="Match"/> is an optional case-insensitive substring of the message; <see cref="Until"/> (yyyy-MM-dd)
    /// makes the waiver expire.
    /// </summary>
    public sealed class QaSuppression
    {
        public string Id = "";
        public string Path = "";
        public string Object = "";
        public string Match = "";
        public string Reason = "";
        public string Until = "";
        public int Line;
        public int Hits;

        public bool Matches(QaIssue issue)
        {
            if (!QaGlob.IsMatch(Id, issue.Rule)) return false;
            if (!QaGlob.IsMatch(Path, issue.Path)) return false;
            if (!string.IsNullOrEmpty(Object) && !QaGlob.IsMatch(Object, issue.Object)) return false;
            if (!string.IsNullOrEmpty(Match) && (issue.Message ?? "").IndexOf(Match, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }
    }

    /// <summary>
    /// The baseline of accepted exceptions. Parsing validates every entry (missing reason, bad date, expired) and
    /// reports problems as QA.* issues instead of silently ignoring them; <see cref="StaleIssues"/> lists entries that
    /// no longer match anything so the file cannot rot.
    /// </summary>
    public sealed class QaSuppressionSet
    {
        public const string DefaultFile = "Docs/qa_suppressions.json";
        const int MinReasonLength = 10;

        public readonly List<QaSuppression> Entries = new List<QaSuppression>();
        public readonly List<QaIssue> Problems = new List<QaIssue>();
        public string SourcePath = DefaultFile;

        public static QaSuppressionSet Empty() { return new QaSuppressionSet(); }

        /// <summary>
        /// Parses <c>{ "suppressions": [ { "id", "path", "reason", "object"?, "match"?, "until"? } ] }</c>.
        /// </summary>
        public static QaSuppressionSet Parse(string json, string sourcePath, DateTime today)
        {
            var set = new QaSuppressionSet { SourcePath = sourcePath ?? DefaultFile };
            if (string.IsNullOrEmpty(json)) return set;
            QaJsonNode root;
            try { root = QaJson.Parse(json); }
            catch (QaJsonException e)
            {
                set.Problems.Add(QaRules.MetaSuppressionInvalid.Issue(set.SourcePath, e.Line, "", "suppression file does not parse: " + e.Message,
                    QaSeverity.Critical, "Fix the JSON; until then no suppression applies."));
                return set;
            }
            var list = root.Kind == QaJsonKind.Object ? root["suppressions"] : null;
            if (list == null || list.Kind != QaJsonKind.Array)
            {
                set.Problems.Add(QaRules.MetaSuppressionInvalid.Issue(set.SourcePath, root.Line, "missing top-level \"suppressions\" array"));
                return set;
            }
            foreach (var n in list.Items)
            {
                if (n.Kind != QaJsonKind.Object)
                {
                    set.Problems.Add(QaRules.MetaSuppressionInvalid.Issue(set.SourcePath, n.Line, "entry is not an object"));
                    continue;
                }
                var e = new QaSuppression
                {
                    Id = n.Str("id") ?? "",
                    Path = n.Str("path") ?? "",
                    Object = n.Str("object") ?? "",
                    Match = n.Str("match") ?? "",
                    Reason = (n.Str("reason") ?? "").Trim(),
                    Until = n.Str("until") ?? "",
                    Line = n.Line
                };
                string why = Validate(e, today);
                if (why == null) { set.Entries.Add(e); continue; }
                bool expired = why.StartsWith("expired", StringComparison.Ordinal);
                var rule = expired ? QaRules.MetaSuppressionExpired : QaRules.MetaSuppressionInvalid;
                set.Problems.Add(rule.Issue(set.SourcePath, e.Line, why + " [" + e.Id + " @ " + e.Path + "]"));
            }
            return set;
        }

        /// <summary>Null when the entry is usable, otherwise why not.</summary>
        static string Validate(QaSuppression e, DateTime today)
        {
            if (string.IsNullOrEmpty(e.Id)) return "missing \"id\"";
            if (string.IsNullOrEmpty(e.Path)) return "missing \"path\" (use \"**\" only with a strong reason)";
            if (e.Reason.Length < MinReasonLength) return "missing or too short \"reason\" (explain why the exception is acceptable)";
            string r = e.Reason.ToLowerInvariant();
            if (r == "todo" || r == "tbd" || (r.StartsWith("temp", StringComparison.Ordinal) && r.Length < 16)) return "placeholder \"reason\"";
            if (!string.IsNullOrEmpty(e.Until))
            {
                DateTime until;
                if (!DateTime.TryParseExact(e.Until, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out until))
                    return "\"until\" must be yyyy-MM-dd";
                if (today.Date > until.Date) return "expired on " + e.Until;
            }
            return null;
        }

        /// <summary>Marks matching issues as suppressed (first matching entry wins) and counts hits per entry.</summary>
        public void Apply(IList<QaIssue> issues)
        {
            foreach (var issue in issues)
            {
                if (issue.Category == QaCategory.Meta) continue;    // problems with the QA setup itself are never waivable
                foreach (var e in Entries)
                {
                    if (!e.Matches(issue)) continue;
                    issue.SuppressedBy = e.Reason;
                    e.Hits++;
                    break;
                }
            }
        }

        /// <summary>
        /// Entries that matched nothing, limited to rules whose category actually ran (a "qa Code" run must not call
        /// scene suppressions stale).
        /// </summary>
        public List<QaIssue> StaleIssues(Func<string, bool> categoryRan)
        {
            var result = new List<QaIssue>();
            foreach (var e in Entries)
            {
                if (e.Hits > 0) continue;
                bool covered = false;
                foreach (var rule in QaRules.All)
                    if (QaGlob.IsMatch(e.Id, rule.Id) && (categoryRan == null || categoryRan(rule.Category))) { covered = true; break; }
                if (!covered) continue;
                result.Add(QaRules.MetaSuppressionStale.Issue(SourcePath, e.Line, "matches no issue: " + e.Id + " @ " + e.Path +
                    (string.IsNullOrEmpty(e.Object) ? "" : " › " + e.Object)));
            }
            return result;
        }
    }

    /// <summary>Path-style glob matching used by suppressions and allow-lists (case-insensitive, '/' separated).</summary>
    public static class QaGlob
    {
        public static bool IsMatch(string pattern, string text)
        {
            if (string.IsNullOrEmpty(pattern)) return string.IsNullOrEmpty(text);
            if (text == null) text = "";
            return Match(pattern.Replace('\\', '/'), 0, text.Replace('\\', '/'), 0);
        }

        static bool Match(string p, int pi, string t, int ti)
        {
            while (pi < p.Length)
            {
                char c = p[pi];
                if (c == '*')
                {
                    bool deep = pi + 1 < p.Length && p[pi + 1] == '*';
                    int next = pi + (deep ? 2 : 1);
                    if (deep && next < p.Length && p[next] == '/') next++;      // "**/" also matches zero segments
                    for (int k = ti; k <= t.Length; k++)
                    {
                        if (Match(p, next, t, k)) return true;
                        if (k < t.Length && !deep && t[k] == '/') return false;
                    }
                    return false;
                }
                if (ti >= t.Length) return false;
                if (c == '?') { if (t[ti] == '/') return false; }
                else if (char.ToLowerInvariant(c) != char.ToLowerInvariant(t[ti])) return false;
                pi++; ti++;
            }
            return ti == t.Length;
        }
    }
}
