// Compiled only where GK_QA_TESTS is defined: by GK.Tests.QA.asmdef in Unity (versionDefines) and by Tools/qa/run_cs.sh
// in the container. The shared container test step (compile.sh) cannot reference GK.Editor.QA, so it skips these files.
#if GK_QA_TESTS
using System;
using System.Collections.Generic;
using GeorgianKnight.EditorTools.QA;
using NUnit.Framework;

namespace GeorgianKnight.Tests.QA
{
    public sealed class QaSummaryTests
    {
        static QaIssue Make(QaSeverity s, string rule = "CODE.TODO", string category = "Code")
        {
            return new QaIssue { Rule = rule, Severity = s, Category = category, Path = "a.cs", Message = "m" };
        }

        [Test]
        public void Gate_Passes_WithOnlyMajorMinorInfo()
        {
            var s = QaSummary.From(new[] { Make(QaSeverity.Major), Make(QaSeverity.Minor), Make(QaSeverity.Info) });
            Assert.IsTrue(s.Passed);
            Assert.AreEqual("QA GATE: PASS", s.GateLine());
        }

        [TestCase(QaSeverity.Blocker)]
        [TestCase(QaSeverity.Critical)]
        public void Gate_Fails_OnBlockerOrCritical(QaSeverity severity)
        {
            var s = QaSummary.From(new[] { Make(severity), Make(QaSeverity.Minor) });
            Assert.IsFalse(s.Passed);
            StringAssert.StartsWith("QA GATE: FAIL", s.GateLine());
        }

        [Test]
        public void Suppressed_Issues_DoNotCount_TowardTheGate()
        {
            var i = Make(QaSeverity.Blocker);
            i.SuppressedBy = "accepted for the vertical slice";
            var s = QaSummary.From(new[] { i });
            Assert.IsTrue(s.Passed);
            Assert.AreEqual(1, s.Suppressed);
            Assert.AreEqual(0, s.Total);
        }

        [Test]
        public void Counts_AreAggregated_PerSeverityAndCategory()
        {
            var s = QaSummary.From(new[] { Make(QaSeverity.Major), Make(QaSeverity.Major, "TEX.NPOT", "Texture"), Make(QaSeverity.Info) });
            Assert.AreEqual(2, s.Count(QaSeverity.Major));
            Assert.AreEqual(1, s.CategoryCount("Texture", QaSeverity.Major));
            Assert.AreEqual(1, s.CategoryCount("Code", QaSeverity.Info));
            Assert.AreEqual("Blocker 0 · Critical 0 · Major 2 · Minor 0 · Info 1 (suppressed 0)", s.CountsLine());
        }

        [Test]
        public void Milder_StepsDown_AndStopsAtInfo()
        {
            Assert.AreEqual(QaSeverity.Major, QaSeverities.Milder(QaSeverity.Critical));
            Assert.AreEqual(QaSeverity.Info, QaSeverities.Milder(QaSeverity.Info));
        }

        [Test]
        public void TopRules_AreOrderedByFrequency()
        {
            var s = QaSummary.From(new[] { Make(QaSeverity.Minor, "B"), Make(QaSeverity.Minor, "A"), Make(QaSeverity.Minor, "A") });
            var top = s.TopRules(5);
            Assert.AreEqual("A", top[0].Key);
            Assert.AreEqual(2, top[0].Value);
        }

        [Test]
        public void RuleCatalog_IdsAreUnique_AndEveryRuleHasFixAndWhy()
        {
            var ids = new HashSet<string>();
            foreach (var r in QaRules.All)
            {
                Assert.IsTrue(ids.Add(r.Id), "duplicate rule id " + r.Id);
                Assert.IsFalse(string.IsNullOrEmpty(r.Fix), r.Id + " has no fix");
                Assert.IsFalse(string.IsNullOrEmpty(r.Why), r.Id + " has no rationale");
                Assert.Contains(r.Category, QaCategory.All, r.Id + " has an unknown category");
            }
            Assert.AreSame(QaRules.CodeTodo, QaRules.Find("CODE.TODO"));
        }

        [Test]
        public void Report_Json_IsValid_AndCarriesTheGate()
        {
            var report = new QaReport { Scope = "test" };
            report.Issues.Add(Make(QaSeverity.Critical, "CODE.TIMESCALE_WRITE"));
            report.Issues[0].Message = "quote \" and | pipe\nnewline";
            var doc = QaJson.Parse(report.ToJson());
            Assert.AreEqual("FAIL", doc.Str("gate"));
            Assert.AreEqual(1, doc["issues"].Items.Count);
            Assert.AreEqual("quote \" and | pipe\nnewline", doc["issues"].Items[0].Str("message"));
            StringAssert.Contains("quote \" and \\| pipe<br>newline", report.ToMarkdown());
        }
    }

    public sealed class QaSuppressionTests
    {
        static readonly DateTime Today = new DateTime(2026, 10, 20);

        [TestCase("CODE.*", "CODE.TODO", true)]
        [TestCase("CODE.TODO", "CODE.TODO_X", false)]
        [TestCase("Assets/**/Debug/**", "Assets/_GeorgianKnight/Scripts/Runtime/Gameplay/Debug/GKAutoPilot.cs", true)]
        [TestCase("Assets/*/Scripts/*.cs", "Assets/_GeorgianKnight/Scripts/Runtime/A.cs", false)]
        [TestCase("**/A.cs", "A.cs", true)]
        [TestCase("Assets/_georgianknight/**", "Assets/_GeorgianKnight/X.cs", true)]
        [TestCase("Root/Props/Banner_??", "Root/Props/Banner_03", true)]
        public void Glob(string pattern, string text, bool expected)
        {
            Assert.AreEqual(expected, QaGlob.IsMatch(pattern, text));
        }

        static QaIssue Issue(string rule, string path, string message)
        {
            return new QaIssue { Rule = rule, Severity = QaSeverity.Critical, Category = "Code", Path = path, Message = message };
        }

        [Test]
        public void Apply_SuppressesMatches_AndCountsHits()
        {
            var set = QaSuppressionSet.Parse(
                "{ \"suppressions\": [ { \"id\": \"CODE.FIND_*\", \"path\": \"**/GameRoot.cs\", \"reason\": \"bootstrap finds the scene root once\" } ] }",
                "s.json", Today);
            var hit = Issue("CODE.FIND_RUNTIME", "Assets/_GeorgianKnight/Scripts/Runtime/Core/GameRoot.cs", "FindAnyObjectByType at runtime");
            var miss = Issue("CODE.FIND_RUNTIME", "Assets/_GeorgianKnight/Scripts/Runtime/Combat/X.cs", "FindAnyObjectByType at runtime");
            set.Apply(new List<QaIssue> { hit, miss });
            Assert.IsTrue(hit.IsSuppressed);
            Assert.IsFalse(miss.IsSuppressed);
            Assert.AreEqual(1, set.Entries[0].Hits);
            Assert.AreEqual(0, set.Problems.Count);
        }

        [Test]
        public void Entries_WithoutReason_AreRejected_AndReported()
        {
            var set = QaSuppressionSet.Parse("{ \"suppressions\": [ { \"id\": \"CODE.TODO\", \"path\": \"**\", \"reason\": \"tbd\" } ] }", "s.json", Today);
            Assert.AreEqual(0, set.Entries.Count);
            Assert.AreEqual("QA.SUPPRESSION_INVALID", set.Problems[0].Rule);
        }

        [Test]
        public void Expired_Entries_StopApplying()
        {
            var set = QaSuppressionSet.Parse(
                "{ \"suppressions\": [ { \"id\": \"CODE.TODO\", \"path\": \"**\", \"reason\": \"legacy HUD is replaced in M2\", \"until\": \"2026-10-01\" } ] }", "s.json", Today);
            Assert.AreEqual(0, set.Entries.Count);
            Assert.AreEqual("QA.SUPPRESSION_EXPIRED", set.Problems[0].Rule);
        }

        [Test]
        public void MessageFilter_And_Stale_Detection()
        {
            var set = QaSuppressionSet.Parse(
                "{ \"suppressions\": [ { \"id\": \"CODE.TODO\", \"path\": \"**\", \"match\": \"tune\", \"reason\": \"tracked in the balance backlog\" }," +
                " { \"id\": \"TEX.NPOT\", \"path\": \"**\", \"reason\": \"UI atlas is intentionally NPOT\" } ] }", "s.json", Today);
            var a = Issue("CODE.TODO", "x.cs", "TODO: tune the curve");
            var b = Issue("CODE.TODO", "x.cs", "TODO: rename");
            set.Apply(new List<QaIssue> { a, b });
            Assert.IsTrue(a.IsSuppressed);
            Assert.IsFalse(b.IsSuppressed);
            Assert.AreEqual(0, set.StaleIssues(cat => cat == "Code").Count, "texture suppressions are not stale when textures did not run");
            Assert.AreEqual(1, set.StaleIssues(cat => true).Count);
        }

        [Test]
        public void MetaIssues_CannotBeSuppressed()
        {
            var set = QaSuppressionSet.Parse("{ \"suppressions\": [ { \"id\": \"**\", \"path\": \"**\", \"reason\": \"silence everything forever\" } ] }", "s.json", Today);
            var meta = new QaIssue { Rule = "QA.VALIDATOR_CRASH", Category = QaCategory.Meta, Severity = QaSeverity.Critical };
            set.Apply(new List<QaIssue> { meta });
            Assert.IsFalse(meta.IsSuppressed);
        }

        [Test]
        public void BrokenJson_IsReported_WithLine()
        {
            var set = QaSuppressionSet.Parse("{\n \"suppressions\": [\n { \"id\": }\n ] }", "s.json", Today);
            Assert.AreEqual(1, set.Problems.Count);
            Assert.AreEqual(3, set.Problems[0].Line);
        }
    }

    public sealed class QaJsonTests
    {
        [Test]
        public void Parses_Nested_Values_WithLines_AndDuplicates()
        {
            var doc = QaJson.Parse("{\n \"entries\": [\n  { \"k\": \"a\", \"v\": \"ახალი \\\"თამაში\\\"\\n\" },\n  { \"k\": \"a\", \"v\": \"x\" }\n ],\n \"n\": -1.5e2, \"b\": true, \"z\": null\n}");
            var entries = doc["entries"].Items;
            Assert.AreEqual(2, entries.Count);
            Assert.AreEqual(3, entries[0].Line);
            Assert.AreEqual("ახალი \"თამაში\"\n", entries[0].Str("v"));
            Assert.AreEqual(-150.0, doc.Num("n", 0), 1e-9);
            Assert.IsTrue(doc["b"].Bool);
            Assert.AreEqual(QaJsonKind.Null, doc["z"].Kind);
        }

        [TestCase("{ \"a\": 1, }")]
        [TestCase("{ \"a\" 1 }")]
        [TestCase("[1, 2")]
        [TestCase("{ \"a\": \"unterminated }")]
        public void Rejects_Malformed(string json)
        {
            Assert.Throws<QaJsonException>(() => QaJson.Parse(json));
        }

        [Test]
        public void Quote_RoundTrips_ControlCharacters()
        {
            string s = "tab\there \"q\" back\\slash \u0001";
            Assert.AreEqual(s, QaJson.Parse(QaJson.Quote(s)).Text);
        }
    }
}
#endif
