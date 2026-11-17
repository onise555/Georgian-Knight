using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// Runs the selected validators, applies Docs/qa_suppressions.json, writes GKBridge/qa_report.md + qa_report.json and
    /// logs the counts and the gate line ("QA GATE: PASS/FAIL") through the bridge log.
    /// </summary>
    public static class QaRunner
    {
        public const string SuppressionsFile = "qa_suppressions.json";

        public static QaReport Run(string filter)
        {
            if (EditorApplication.isPlaying || EditorApplication.isCompiling)
            {
                GKBridge.Log("qa: not available while playing or compiling");
                return null;
            }
            string scope, error;
            var validators = QaRegistry.Select(filter, out scope, out error);
            if (error != null)
            {
                GKBridge.Log("qa: " + error + " — categories: " + string.Join(", ", QaCategory.All) + "; validators: " + ValidatorIds());
                return null;
            }
            var ctx = new QaContext();
            var report = new QaReport { Tool = "gk-qa (Unity " + Application.unityVersion + ")", Scope = scope, GeneratedUtc = DateTime.UtcNow };
            report.Issues.AddRange(ctx.SetupProblems);
            var categoriesRun = new HashSet<string>(StringComparer.Ordinal);
            var culture = System.Threading.Thread.CurrentThread.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            try
            {
                for (int i = 0; i < validators.Count; i++)
                {
                    var v = validators[i];
                    ctx.CurrentValidator = v.Id;
                    ctx.Progress("starting", i / (float)validators.Count);
                    categoriesRun.Add(v.Category);
                    report.Validators.Add(RunOne(v, ctx, report.Issues));
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                System.Threading.Thread.CurrentThread.CurrentCulture = culture;
            }

            ApplySuppressions(ctx, report, categoriesRun);
            Write(report);
            GKBridge.Log(report.ToLogSummary(20));
            return report;
        }

        static QaValidatorRun RunOne(IValidator v, QaContext ctx, List<QaIssue> sink)
        {
            var run = new QaValidatorRun { Id = v.Id, Category = v.Category };
            var sw = Stopwatch.StartNew();
            int before = sink.Count;
            try
            {
                using (var e = v.Run(ctx).GetEnumerator())
                    while (e.MoveNext())
                        if (e.Current != null) sink.Add(e.Current);
            }
            catch (Exception ex)
            {
                run.Error = ex.GetType().Name + ": " + ex.Message;
                sink.Add(QaRules.MetaValidatorCrash.Issue("", 0, v.Id, "validator '" + v.Id + "' crashed: " + run.Error, QaSeverity.Critical, null));
                Debug.LogException(ex);
            }
            run.Issues = sink.Count - before;
            run.Milliseconds = sw.Elapsed.TotalMilliseconds;
            return run;
        }

        static void ApplySuppressions(QaContext ctx, QaReport report, HashSet<string> categoriesRun)
        {
            string path = ctx.DocsPath(SuppressionsFile);
            string json = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            var set = QaSuppressionSet.Parse(json, QaSuppressionSet.DefaultFile, DateTime.Today);
            set.Apply(report.Issues);
            report.Issues.AddRange(set.Problems);
            report.Issues.AddRange(set.StaleIssues(categoriesRun.Contains));
        }

        static void Write(QaReport report)
        {
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(Path.Combine(GKBridge.Dir, "qa_report.md"), report.ToMarkdown(), utf8);
            File.WriteAllText(Path.Combine(GKBridge.Dir, "qa_report.json"), report.ToJson(), utf8);
        }

        public static string ValidatorIds()
        {
            var ids = new List<string>();
            foreach (var v in QaRegistry.All()) ids.Add(v.Id + " (" + v.Category + ")");
            return string.Join(", ", ids.ToArray());
        }
    }
}
