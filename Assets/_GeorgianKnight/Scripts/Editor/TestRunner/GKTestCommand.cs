using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>Bridge command "tests [filter]": runs EditMode tests and writes a summary + failures to the log.</summary>
    public static class GKTestCommand
    {
        static TestRunnerApi api;

        sealed class Callbacks : ICallbacks
        {
            int passed, failed, skipped;
            readonly StringBuilder failures = new StringBuilder();
            public void RunStarted(ITestAdaptor testsToRun) { GKBridge.Log("tests: running " + testsToRun.TestCaseCount + " test(s)"); }
            public void RunFinished(ITestResultAdaptor result)
            {
                GKBridge.Log("tests: passed=" + passed + " failed=" + failed + " skipped=" + skipped + " duration=" + result.Duration.ToString("F2") + "s");
                if (failures.Length > 0) GKBridge.Log("tests: FAILURES\n" + failures);
                GKBridge.Log("tests: done");
            }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor r)
            {
                if (r.Test.IsSuite) return;
                switch (r.TestStatus)
                {
                    case TestStatus.Passed: passed++; break;
                    case TestStatus.Failed:
                        failed++;
                        failures.AppendLine("  " + r.Test.FullName + ": " + r.Message + "\n" + (r.StackTrace ?? "").Split('\n')[0]);
                        break;
                    default: skipped++; break;
                }
            }
        }

        [GKCommand("tests", "tests [name filter]: run EditMode tests")]
        static void Run(string filter)
        {
            if (api == null) api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(new Callbacks());
            var f = new Filter { testMode = TestMode.EditMode };
            if (!string.IsNullOrEmpty(filter)) f.groupNames = new[] { filter };
            api.Execute(new ExecutionSettings(f) { runSynchronously = true });
        }
    }
}
