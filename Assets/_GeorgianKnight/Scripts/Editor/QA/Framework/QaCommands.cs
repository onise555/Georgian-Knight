using System.IO;
using System.Text;
using UnityEditor;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>Bridge commands and menu items of the QA framework (see Docs/QA.md).</summary>
    public static class QaCommands
    {
        [GKCommand("qa", "qa [all|list|<category>[,<category>]|id:<validator>]: validate → GKBridge/qa_report.md/.json, QA GATE: PASS/FAIL")]
        public static void Qa(string arg)
        {
            if (arg.Trim() == "list")
            {
                GKBridge.Log("qa validators: " + QaRunner.ValidatorIds());
                return;
            }
            QaRunner.Run(arg);
        }

        [GKCommand("qa.shots", "qa.shots [scene filter]: review screenshots + exposure metrics → GKBridge/qa_shots/")]
        public static void Shots(string arg) { QaShots.Capture(arg); }

        [GKCommand("qa.rules", "qa.rules: rule catalog → GKBridge/qa_rules.md")]
        public static void Rules(string arg)
        {
            var sb = new StringBuilder("# QA rule catalog\n");
            string cat = null;
            foreach (var r in QaRules.All)
            {
                if (r.Category != cat)
                {
                    cat = r.Category;
                    sb.Append("\n## ").Append(cat).Append("\n\n| Id | Severity | Rule | Why | Fix |\n|---|---|---|---|---|\n");
                }
                sb.Append("| `").Append(r.Id).Append("` | ").Append(r.Severity).Append(" | ").Append(QaReport.Cell(r.Title)).Append(" | ")
                  .Append(QaReport.Cell(r.Why)).Append(" | ").Append(QaReport.Cell(r.Fix)).Append(" |\n");
            }
            string path = Path.Combine(GKBridge.Dir, "qa_rules.md");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            GKBridge.Log("qa.rules: " + QaRules.All.Count + " rules → " + path);
        }

        [MenuItem("Georgian Knight/QA/Run All Validators")]
        static void MenuAll() { QaRunner.Run(""); }

        [MenuItem("Georgian Knight/QA/Run Code Validator")]
        static void MenuCode() { QaRunner.Run(QaCategory.Code); }

        [MenuItem("Georgian Knight/QA/Capture Review Shots")]
        static void MenuShots() { QaShots.Capture(""); }
    }
}
