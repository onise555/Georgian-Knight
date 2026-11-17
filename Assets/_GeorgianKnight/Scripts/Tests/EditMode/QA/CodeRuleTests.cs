// Compiled only where GK_QA_TESTS is defined (see QaFrameworkTests.cs).
#if GK_QA_TESTS
using System.Collections.Generic;
using GeorgianKnight.EditorTools.QA;
using NUnit.Framework;

namespace GeorgianKnight.Tests.QA
{
    public sealed class CSharpSourceTests
    {
        [Test]
        public void Masks_Strings_Comments_AndKeepsLineBreaks()
        {
            var src = new CSharpSource("a.cs", "var s = \"Find(\\\"x\\\")\"; // GetComponent<T>()\n/* Update */ int x = 1;\nvar v = @\"multi\nline\";");
            Assert.AreEqual(src.Text.Length, src.Masked.Length);
            StringAssert.DoesNotContain("Find", src.Masked);
            StringAssert.DoesNotContain("GetComponent", src.Masked);
            StringAssert.DoesNotContain("Update", src.Masked);
            Assert.AreEqual(2, src.Literals.Count);
            Assert.AreEqual("Find(\"x\")", src.Literals[0].Value);
            Assert.AreEqual("multi\nline", src.Literals[1].Value);
            Assert.AreEqual(2, src.Comments.Count);
            Assert.AreEqual(4, src.LineOf(src.Text.Length - 1));
        }

        [Test]
        public void Interpolated_Strings_WithNestedQuotes_AreOneLiteral()
        {
            var src = new CSharpSource("a.cs", "var s = $\"{(a ? \"x\" : \"y\")} items\"; int after = 2;");
            Assert.AreEqual(1, src.Literals.Count);
            Assert.IsTrue(src.Literals[0].Interpolated);
            StringAssert.Contains("int after = 2;", src.Masked);
        }

        [Test]
        public void EditorOnly_Regions_AreNonShipping_ButElseBranchIsNot()
        {
            var src = new CSharpSource("a.cs", "a();\n#if UNITY_EDITOR\nb();\n#else\nc();\n#endif\nd();\n#if !UNITY_EDITOR\ne();\n#else\nf();\n#endif");
            Assert.IsFalse(src.IsNonShipping(1));
            Assert.IsTrue(src.IsNonShipping(3));
            Assert.IsFalse(src.IsNonShipping(5));
            Assert.IsFalse(src.IsNonShipping(7));
            Assert.IsFalse(src.IsNonShipping(9));
            Assert.IsTrue(src.IsNonShipping(11));
        }

        [Test]
        public void Parser_Finds_Types_Members_AndDocComments()
        {
            var file = new CSharpFile(new CSharpSource("a.cs",
                "namespace N {\n/// <summary>Doc.</summary>\n[System.Serializable] public class A : B, IC<int> {\n  [SerializeField] float x = 1f, y;\n" +
                "  public int P { get; set; } = 3;\n  void M(int a, string b = \"q\") { if (a > 0) { } }\n  public T G<T>() where T : new() => new T();\n" +
                "  public class Nested { }\n}\n}"));
            Assert.AreEqual(2, file.Types.Count);
            var a = file.Types[0];
            Assert.AreEqual("A", a.Name);
            Assert.AreEqual("N", a.Namespace);
            CollectionAssert.AreEqual(new[] { "B", "IC" }, a.Bases);
            Assert.IsNotNull(a.Doc);
            Assert.IsTrue(a.HasAttribute("Serializable"));
            Assert.AreEqual(2, a.Fields.Count);
            Assert.IsTrue(a.Fields[0].HasAttribute("SerializeField"));
            Assert.AreEqual("P", a.Properties[0].Name);
            Assert.AreEqual(2, a.Methods.Count);
            Assert.AreEqual("M", a.Methods[0].Name);
            Assert.AreEqual(2, a.Methods[0].Parameters.Count);
            Assert.AreEqual("G", a.Methods[1].Name);
            Assert.IsTrue(a.Methods[1].ExpressionBodied);
            Assert.AreSame(a, file.Types[1].Parent);
        }
    }

    public sealed class CodeRuleTests
    {
        const string Runtime = "Assets/_GeorgianKnight/Scripts/Runtime/Gameplay/Combat/T.cs";
        const string EditorPath = "Assets/_GeorgianKnight/Scripts/Editor/T.cs";

        static string Wrap(string members, string bases = " : MonoBehaviour", string usings = "using UnityEngine;")
        {
            return usings + "\nnamespace GeorgianKnight.Combat\n{\n    /// <summary>Test type.</summary>\n    public sealed class T" + bases + "\n    {\n" + members + "\n    }\n}\n";
        }

        static List<string> Rules(string path, string text)
        {
            var list = new List<string>();
            foreach (var i in CodeLinter.LintSnippet(path, text)) list.Add(i.Rule);
            return list;
        }

        static QaIssue First(string path, string text, string rule)
        {
            foreach (var i in CodeLinter.LintSnippet(path, text)) if (i.Rule == rule) return i;
            return null;
        }

        [Test]
        public void FindInUpdate_IsCritical_AndInStartIsMajor()
        {
            var text = Wrap("        void Update() { var c = FindObjectOfType<Camera>(); }\n        void Start() { var l = FindObjectOfType<Light>(); }");
            Assert.AreEqual(QaSeverity.Critical, First(Runtime, text, "CODE.FIND_IN_HOT_PATH").Severity);
            Assert.AreEqual(QaSeverity.Major, First(Runtime, text, "CODE.FIND_RUNTIME").Severity);
        }

        [Test]
        public void HotPath_FollowsSameClassCalls_WithMilderSeverity()
        {
            var text = Wrap("        void Update() { Step(); }\n        void Step() { var r = GetComponent<Rigidbody>(); }");
            var i = First(Runtime, text, "CODE.GETCOMPONENT_HOT");
            Assert.IsNotNull(i);
            Assert.AreEqual(QaSeverity.Minor, i.Severity);
            StringAssert.Contains("via Update → Step", i.Message);
        }

        [Test]
        public void TimeScale_Write_IsFlagged_ButComparisonIsNot()
        {
            CollectionAssert.Contains(Rules(Runtime, Wrap("        void Pause() { Time.timeScale = 0f; }")), "CODE.TIMESCALE_WRITE");
            CollectionAssert.DoesNotContain(Rules(Runtime, Wrap("        bool Paused() { return Time.timeScale == 0f; }")), "CODE.TIMESCALE_WRITE");
        }

        [Test]
        public void NullPropagation_OnUnityObject_IsFlagged_OnPlainObject_IsNot()
        {
            var unity = Wrap("        [SerializeField] Transform target;\n        void Use() { var p = target?.position; }");
            var plain = Wrap("        System.Action cb;\n        void Use() { cb?.Invoke(); }");
            Assert.AreEqual(QaSeverity.Critical, First(Runtime, unity, "CODE.UNITY_NULL_OP").Severity);
            CollectionAssert.DoesNotContain(Rules(Runtime, plain), "CODE.UNITY_NULL_OP");
            Assert.AreEqual(QaSeverity.Major, First(EditorPath, unity.Replace("GeorgianKnight.Combat", "GeorgianKnight.EditorTools"), "CODE.UNITY_NULL_OP").Severity);
        }

        [Test]
        public void Lambdas_OnlyCapturingOnesAllocate()
        {
            var nonCapturing = Wrap("        System.Collections.Generic.List<Transform> l;\n        void Update() { l.RemoveAll(t => t == null); }");
            var capturing = Wrap("        System.Collections.Generic.List<Transform> l;\n        float r;\n        void Update() { l.RemoveAll(t => t.position.y > r); }");
            CollectionAssert.DoesNotContain(Rules(Runtime, nonCapturing), "CODE.ALLOC_HOT");
            StringAssert.Contains("capturing lambda", First(Runtime, capturing, "CODE.ALLOC_HOT").Message);
        }

        [Test]
        public void ChangeGuarded_StringBuilding_IsAccepted()
        {
            var text = Wrap("        int last; UnityEngine.UI.Text label;\n        void Update() { int n = Time.frameCount; if (n != last) { last = n; label.text = n.ToString(); } }");
            CollectionAssert.DoesNotContain(Rules(Runtime, text), "CODE.ALLOC_HOT");
        }

        [Test]
        public void EmptyCatch_IsMajor_DocumentedIsMinor_AndLoggedIsClean()
        {
            Assert.AreEqual(QaSeverity.Major, First(Runtime, Wrap("        void A() { try { } catch { } }"), "CODE.EMPTY_CATCH").Severity);
            Assert.AreEqual(QaSeverity.Minor, First(Runtime, Wrap("        void A() { try { } catch { /* file may be locked */ } }"), "CODE.EMPTY_CATCH").Severity);
            var logged = Rules(Runtime, Wrap("        void A() { try { } catch (System.Exception e) { Debug.LogException(e); } }"));
            CollectionAssert.DoesNotContain(logged, "CODE.EMPTY_CATCH");
            CollectionAssert.DoesNotContain(logged, "CODE.CATCH_NO_LOG");
        }

        [Test]
        public void TwoComponents_InOneFile_AreCritical()
        {
            var text = Wrap("") + "namespace GeorgianKnight.Combat\n{\n    /// <summary>Second.</summary>\n    public sealed class U : MonoBehaviour { }\n}\n";
            CollectionAssert.Contains(Rules(Runtime, text), "CODE.MULTIPLE_UNITY_OBJECTS");
            CollectionAssert.Contains(Rules(Runtime, text), "CODE.FILENAME_MISMATCH");
        }

        [Test]
        public void EditorComponents_AreExemptFromFileRules_EditorScriptableObjectsAreNot()
        {
            // Unity refuses AddComponent for a MonoBehaviour whose matching script asset sits in an editor assembly
            var recorder = Wrap("") + "namespace GeorgianKnight.Combat\n{\n    /// <summary>Second.</summary>\n    public sealed class GKSwingRecorder : MonoBehaviour { }\n}\n";
            CollectionAssert.DoesNotContain(Rules(EditorPath, recorder), "CODE.FILENAME_MISMATCH");
            CollectionAssert.DoesNotContain(Rules(EditorPath, recorder), "CODE.MULTIPLE_UNITY_OBJECTS");
            var asset = Wrap("", " : ScriptableObject");
            CollectionAssert.Contains(Rules(EditorPath.Replace("/T.cs", "/Other.cs"), asset), "CODE.FILENAME_MISMATCH");
            CollectionAssert.Contains(Rules(Runtime, recorder), "CODE.FILENAME_MISMATCH");
        }

        [Test]
        public void StaticEvent_WithoutUnsubscribe_IsFlagged_PairedIsClean()
        {
            var leak = Wrap("        void OnEnable() { Application.lowMemory += OnLow; }\n        void OnLow() { }");
            var paired = Wrap("        void OnEnable() { Application.lowMemory += OnLow; }\n        void OnDisable() { Application.lowMemory -= OnLow; }\n        void OnLow() { }");
            CollectionAssert.Contains(Rules(Runtime, leak), "CODE.EVENT_NO_UNSUBSCRIBE");
            CollectionAssert.DoesNotContain(Rules(Runtime, paired), "CODE.EVENT_NO_UNSUBSCRIBE");
        }

        [Test]
        public void ScriptableObjectField_WrittenAtRuntime_IsCritical()
        {
            var text = Wrap("        public float damage;", " : ScriptableObject") +
                       "namespace GeorgianKnight.Combat\n{\n    /// <summary>User.</summary>\n    public sealed class User\n    {\n        T data;\n        void Hit() { data.damage = 0f; }\n    }\n}\n";
            Assert.AreEqual(QaSeverity.Critical, First(Runtime, text, "CODE.SO_MUTATION").Severity);
        }

        static string Body(string stmt)
        {
            return Wrap("        UnityEngine.UIElements.Label l;\n        void A() { " + stmt + " }", "", "using GeorgianKnight.Core;").Replace("GeorgianKnight.Combat", "GeorgianKnight.UI");
        }

        [Test]
        public void HardcodedUiText_IsFlagged_LocGetIsNot()
        {
            const string ui = "Assets/_GeorgianKnight/Scripts/Runtime/UI/T.cs";
            CollectionAssert.Contains(Rules(ui, Body("l.text = \"New Game\";")), "CODE.HARDCODED_UI_STRING");
            CollectionAssert.DoesNotContain(Rules(ui, Body("l.text = Loc.Get(\"ui.menu.new_game\");")), "CODE.HARDCODED_UI_STRING");
            CollectionAssert.DoesNotContain(Rules(ui, Body("l.AddToClassList(\"gk-title\");")), "CODE.HARDCODED_UI_STRING");
        }

        [Test]
        public void CSharp8Syntax_IsBlocker_ButNewConstraintIsNot()
        {
            Assert.AreEqual(QaSeverity.Blocker, First(Runtime, Wrap("        int A(int k) => k switch { 0 => 1, _ => 2 };"), "CODE.CSHARP_VERSION").Severity);
            CollectionAssert.DoesNotContain(Rules(Runtime, Wrap("        X Make<X>() where X : new() { return new X(); }")), "CODE.CSHARP_VERSION");
            CollectionAssert.DoesNotContain(Rules(Runtime, Wrap("        bool init;\n        void A() { init = true; }")), "CODE.CSHARP_VERSION");
        }

        [TestCase("var x = target.position;", true)]
        [TestCase("Debug.Log(\"hit\");", true)]
        [TestCase("}", true)]
        [TestCase("e.g. call Foo(bar);", false)]
        [TestCase("ground (character root is at y = 5000)", false)]
        [TestCase("standing still: full-body version (hips and feet as authored);", false)]
        public void CommentedCode_Heuristic(string line, bool expected)
        {
            Assert.AreEqual(expected, CodeRules.IsCodeLike(line));
        }

        [TestCase("ახალი თამაში", true)]
        [TestCase("Continue", true)]
        [TestCase("Press any key", true)]
        [TestCase("ui.menu.continue", false)]
        [TestCase("gk-title--large", false)]
        [TestCase("v", false)]
        [TestCase("{0}/{1}", false)]
        public void UserFacingText_Heuristic(string value, bool expected)
        {
            Assert.AreEqual(expected, CodeRules.LooksUserFacing(value));
        }
    }
}
#endif
