using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>Timing and outcome of one validator in a run (shown in the report so slow or crashing validators stand out).</summary>
    public sealed class QaValidatorRun
    {
        public string Id = "";
        public string Category = "";
        public int Issues;
        public double Milliseconds;
        public string Error;
    }

    /// <summary>
    /// The consolidated result of a QA run and its two renderings: <c>qa_report.md</c> for humans and
    /// <c>qa_report.json</c> for tools (deploy gate, dashboards, the AI reviewer). Pure text generation so the
    /// container-side linter writes byte-identical formats.
    /// </summary>
    public sealed class QaReport
    {
        public string Tool = "gk-qa";
        public string Scope = "all";
        public DateTime GeneratedUtc = DateTime.UtcNow;
        public readonly List<QaIssue> Issues = new List<QaIssue>();
        public readonly List<QaValidatorRun> Validators = new List<QaValidatorRun>();

        public QaSummary Summary() { return QaSummary.From(Issues); }

        /// <summary>Sorts by severity, category, path, line, rule — stable output for diffs.</summary>
        public void Sort()
        {
            Issues.Sort(Compare);
        }

        public static int Compare(QaIssue a, QaIssue b)
        {
            int c = a.Severity.CompareTo(b.Severity);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Category, b.Category);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Path, b.Path);
            if (c != 0) return c;
            c = a.Line.CompareTo(b.Line);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Rule, b.Rule);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Object, b.Object);
            return c != 0 ? c : string.CompareOrdinal(a.Message, b.Message);
        }

        // ------------------------------------------------------------------------------------------------ markdown

        public string ToMarkdown()
        {
            Sort();
            var s = Summary();
            var sb = new StringBuilder();
            double totalMs = 0;
            foreach (var v in Validators) totalMs += v.Milliseconds;
            sb.Append("# QA report — Georgian Knight\n\n");
            sb.Append("Generated ").Append(GeneratedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" UTC · tool ")
              .Append(Tool).Append(" · scope `").Append(Scope).Append("` · ").Append(Validators.Count).Append(" validator(s) · ")
              .Append((totalMs / 1000.0).ToString("F1", CultureInfo.InvariantCulture)).Append(" s\n\n");
            sb.Append("**").Append(s.GateLine()).Append("**\n\n");

            sb.Append("| Severity | Active |\n|---|---:|\n");
            foreach (var sev in QaSeverities.All) sb.Append("| ").Append(sev).Append(" | ").Append(s.Count(sev)).Append(" |\n");
            sb.Append("| *suppressed* | ").Append(s.Suppressed).Append(" |\n\n");

            var cats = new List<string>(s.Categories);
            if (cats.Count > 0)
            {
                sb.Append("## By category\n\n| Category | Blocker | Critical | Major | Minor | Info |\n|---|---:|---:|---:|---:|---:|\n");
                foreach (var c in cats)
                {
                    sb.Append("| ").Append(c);
                    foreach (var sev in QaSeverities.All) sb.Append(" | ").Append(s.CategoryCount(c, sev));
                    sb.Append(" |\n");
                }
                sb.Append('\n');
                sb.Append("## Most frequent rules\n\n");
                foreach (var kv in s.TopRules(12)) sb.Append("- `").Append(kv.Key).Append("` × ").Append(kv.Value).Append('\n');
                sb.Append('\n');
            }

            foreach (var sev in QaSeverities.All)
            {
                int n = s.Count(sev);
                if (n == 0) continue;
                sb.Append("## ").Append(sev).Append(" (").Append(n).Append(")\n\n");
                sb.Append("| Rule | Location | Message | Fix |\n|---|---|---|---|\n");
                foreach (var i in Issues)
                {
                    if (i.IsSuppressed || i.Severity != sev) continue;
                    sb.Append("| `").Append(i.Rule).Append("` | ").Append(Cell(i.Location)).Append(" | ").Append(Cell(i.Message))
                      .Append(" | ").Append(Cell(i.Fix)).Append(" |\n");
                }
                sb.Append('\n');
            }

            if (s.Suppressed > 0)
            {
                sb.Append("## Suppressed (").Append(s.Suppressed).Append(")\n\n| Rule | Location | Message | Reason |\n|---|---|---|---|\n");
                foreach (var i in Issues)
                {
                    if (!i.IsSuppressed) continue;
                    sb.Append("| `").Append(i.Rule).Append("` | ").Append(Cell(i.Location)).Append(" | ").Append(Cell(i.Message))
                      .Append(" | ").Append(Cell(i.SuppressedBy)).Append(" |\n");
                }
                sb.Append('\n');
            }

            sb.Append("## Validators\n\n| Validator | Category | Issues | ms | Error |\n|---|---|---:|---:|---|\n");
            foreach (var v in Validators)
                sb.Append("| ").Append(v.Id).Append(" | ").Append(v.Category).Append(" | ").Append(v.Issues).Append(" | ")
                  .Append(v.Milliseconds.ToString("F0", CultureInfo.InvariantCulture)).Append(" | ").Append(Cell(v.Error ?? "")).Append(" |\n");
            return sb.ToString();
        }

        /// <summary>Escapes text for a Markdown table cell.</summary>
        public static string Cell(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");
        }

        // ------------------------------------------------------------------------------------------------ json

        public string ToJson()
        {
            Sort();
            var s = Summary();
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"tool\": ").Append(QaJson.Quote(Tool)).Append(",\n");
            sb.Append("  \"version\": 1,\n");
            sb.Append("  \"generatedUtc\": ").Append(QaJson.Quote(GeneratedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"scope\": ").Append(QaJson.Quote(Scope)).Append(",\n");
            sb.Append("  \"gate\": ").Append(QaJson.Quote(s.Passed ? "PASS" : "FAIL")).Append(",\n");
            sb.Append("  \"counts\": {");
            for (int k = 0; k < QaSeverities.All.Length; k++)
            {
                var sev = QaSeverities.All[k];
                sb.Append(k == 0 ? " " : ", ").Append(QaJson.Quote(sev.ToString())).Append(": ").Append(s.Count(sev));
            }
            sb.Append(" },\n");
            sb.Append("  \"suppressed\": ").Append(s.Suppressed).Append(",\n");
            sb.Append("  \"validators\": [");
            for (int k = 0; k < Validators.Count; k++)
            {
                var v = Validators[k];
                sb.Append(k == 0 ? "\n" : ",\n").Append("    { \"id\": ").Append(QaJson.Quote(v.Id)).Append(", \"category\": ").Append(QaJson.Quote(v.Category))
                  .Append(", \"issues\": ").Append(v.Issues).Append(", \"ms\": ").Append(v.Milliseconds.ToString("F0", CultureInfo.InvariantCulture))
                  .Append(", \"error\": ").Append(QaJson.Quote(v.Error)).Append(" }");
            }
            sb.Append(Validators.Count > 0 ? "\n  ],\n" : "],\n");
            sb.Append("  \"issues\": [");
            for (int k = 0; k < Issues.Count; k++)
            {
                var i = Issues[k];
                sb.Append(k == 0 ? "\n" : ",\n").Append("    { \"rule\": ").Append(QaJson.Quote(i.Rule))
                  .Append(", \"severity\": ").Append(QaJson.Quote(i.Severity.ToString()))
                  .Append(", \"category\": ").Append(QaJson.Quote(i.Category))
                  .Append(", \"path\": ").Append(QaJson.Quote(i.Path))
                  .Append(", \"line\": ").Append(i.Line)
                  .Append(", \"object\": ").Append(QaJson.Quote(i.Object))
                  .Append(", \"message\": ").Append(QaJson.Quote(i.Message))
                  .Append(", \"fix\": ").Append(QaJson.Quote(i.Fix))
                  .Append(", \"suppressed\": ").Append(QaJson.Quote(i.SuppressedBy)).Append(" }");
            }
            sb.Append(Issues.Count > 0 ? "\n  ]\n" : "]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>The log block: counts per severity, the worst issues, and the gate line last.</summary>
        public string ToLogSummary(int maxIssues)
        {
            Sort();
            var s = Summary();
            var sb = new StringBuilder();
            sb.Append("qa [").Append(Scope).Append("]: ").Append(s.CountsLine()).Append('\n');
            int shown = 0;
            foreach (var i in Issues)
            {
                if (i.IsSuppressed || i.Severity > QaSeverity.Major) continue;
                if (shown++ >= maxIssues) { sb.Append("  …\n"); break; }
                sb.Append("  ").Append(i.Severity).Append(' ').Append(i.Rule).Append(' ').Append(i.Location).Append(" — ").Append(i.Message).Append('\n');
            }
            foreach (var v in Validators)
                if (v.Error != null) sb.Append("  validator ").Append(v.Id).Append(" crashed: ").Append(v.Error).Append('\n');
            sb.Append(s.GateLine());
            return sb.ToString();
        }
    }
}
