using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// VIS.* rules: folds the exposure metrics of the last "qa.shots" run (GKBridge/qa_shots/metrics.json) into the gate,
    /// so "qa" never re-renders but still fails on over/under-exposed review frames.
    /// </summary>
    public sealed class VisualValidator : IValidator
    {
        public string Id { get { return "visual"; } }
        public string Category { get { return QaCategory.Visual; } }

        public IEnumerable<QaIssue> Run(QaContext ctx)
        {
            string file = Path.Combine(QaShots.OutputDir, QaShots.MetricsFile);
            if (!File.Exists(file))
            {
                yield return QaRules.VisNotCaptured.Issue("GKBridge/qa_shots/" + QaShots.MetricsFile, 0, "no screenshot metrics yet");
                yield break;
            }
            var doc = QaJson.Parse(File.ReadAllText(file, Encoding.UTF8));
            var scenes = doc["scenes"];
            if (scenes != null)
                foreach (var s in scenes.Items)
                    if (s.Num("views", 0) == 0)
                        yield return QaRules.VisNoViews.Issue(s.Str("scene") ?? "", 0, "no spawn points or QAView markers to shoot from");
            var shots = doc["shots"];
            if (shots == null) yield break;
            foreach (var shot in shots.Items)
            {
                var flags = shot["flags"];
                if (flags == null) continue;
                foreach (var f in flags.Items)
                {
                    var rule = QaRules.Find(f.Text);
                    if (rule == null) continue;
                    yield return rule.Issue(shot.Str("scene") ?? "", 0, shot.Str("view") ?? "", rule.Title + " in " + shot.Str("file") + " (" + QaShots.Describe(shot) + ")", rule.Severity, null);
                }
            }
        }
    }
}
