using System;
using System.Collections.Generic;
using System.Text;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// Severity aggregation and the gate decision. Only active (unsuppressed) issues count toward the gate; suppressed
    /// ones are tallied separately so the report shows how much is being waived.
    /// </summary>
    public sealed class QaSummary
    {
        readonly int[] counts = new int[QaSeverities.All.Length];
        readonly Dictionary<string, int[]> byCategory = new Dictionary<string, int[]>(StringComparer.Ordinal);
        readonly Dictionary<string, int> byRule = new Dictionary<string, int>(StringComparer.Ordinal);

        public int Suppressed { get; private set; }
        public int Total { get; private set; }

        public int Count(QaSeverity s) { return counts[(int)s]; }

        /// <summary>The gate fails on any active Blocker or Critical.</summary>
        public bool Passed { get { return counts[(int)QaSeverity.Blocker] == 0 && counts[(int)QaSeverity.Critical] == 0; } }

        public IEnumerable<string> Categories
        {
            get
            {
                var keys = new List<string>(byCategory.Keys);
                keys.Sort(StringComparer.Ordinal);
                return keys;
            }
        }

        public int CategoryCount(string category, QaSeverity s)
        {
            int[] c;
            return byCategory.TryGetValue(category, out c) ? c[(int)s] : 0;
        }

        /// <summary>Rule ids ordered by number of active issues (most frequent first).</summary>
        public List<KeyValuePair<string, int>> TopRules(int max)
        {
            var list = new List<KeyValuePair<string, int>>(byRule);
            list.Sort((a, b) => a.Value != b.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }

        public static QaSummary From(IEnumerable<QaIssue> issues)
        {
            var s = new QaSummary();
            foreach (var i in issues) s.Add(i);
            return s;
        }

        public void Add(QaIssue issue)
        {
            if (issue == null) return;
            if (issue.IsSuppressed) { Suppressed++; return; }
            Total++;
            counts[(int)issue.Severity]++;
            string cat = string.IsNullOrEmpty(issue.Category) ? "?" : issue.Category;
            int[] c;
            if (!byCategory.TryGetValue(cat, out c)) { c = new int[counts.Length]; byCategory.Add(cat, c); }
            c[(int)issue.Severity]++;
            int n;
            byRule.TryGetValue(issue.Rule ?? "", out n);
            byRule[issue.Rule ?? ""] = n + 1;
        }

        /// <summary>"Blocker 0 · Critical 2 · Major 7 · Minor 12 · Info 3 (suppressed 4)".</summary>
        public string CountsLine()
        {
            var sb = new StringBuilder();
            foreach (var sev in QaSeverities.All)
            {
                if (sb.Length > 0) sb.Append(" · ");
                sb.Append(sev).Append(' ').Append(counts[(int)sev]);
            }
            sb.Append(" (suppressed ").Append(Suppressed).Append(')');
            return sb.ToString();
        }

        /// <summary>The line deploy scripts grep for: "QA GATE: PASS" or "QA GATE: FAIL (1 Blocker, 2 Critical)".</summary>
        public string GateLine()
        {
            if (Passed) return "QA GATE: PASS";
            return "QA GATE: FAIL (" + counts[(int)QaSeverity.Blocker] + " Blocker, " + counts[(int)QaSeverity.Critical] + " Critical)";
        }
    }
}
