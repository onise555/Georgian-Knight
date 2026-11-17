using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>A localization key referenced from code (checked against the string tables by the localization validator).</summary>
    public sealed class LocKeyRef
    {
        public string Key = "";
        public string Path = "";
        public int Line;
        /// <summary>True when the literal is passed straight to Loc.Get/Format/GetOrNull; false for key-shaped constants.</summary>
        public bool Direct;
    }

    /// <summary>How a method is reached from a per-frame entry point.</summary>
    public sealed class HotPath
    {
        public int Depth;
        public string Chain = "";
    }

    /// <summary>Everything a code rule needs about one file.</summary>
    public sealed class CodeLintContext
    {
        public string Path;
        public string FileName;            // without extension
        public CodeScope Scope;
        public CSharpSource Src;
        public CSharpFile File;
        public CsTypeIndex Index;
        public CodeLintOptions Options;
        public bool UsesLinq, IsUiFile;
        /// <summary>Development tools (Runtime/Gameplay/Debug): compiled into the player but never used by shipping content.</summary>
        public bool DevOnly;
        public readonly Dictionary<CsMethod, HotPath> Hot = new Dictionary<CsMethod, HotPath>();
        public readonly List<QaIssue> Issues = new List<QaIssue>();
        public readonly List<LocKeyRef> LocKeys = new List<LocKeyRef>();

        public string Masked { get { return Src.Masked; } }
        public bool IsRuntime { get { return Scope == CodeScope.Runtime; } }

        public QaIssue Add(QaRule rule, int offset, string message) { return Add(rule, offset, message, rule.Severity, null); }

        public QaIssue Add(QaRule rule, int offset, string message, QaSeverity severity, string fix)
        {
            var i = rule.Issue(Path, Src.LineOf(Math.Max(0, offset)), "", message, severity, fix);
            Issues.Add(i);
            return i;
        }

        public HotPath HotAt(int offset)
        {
            var m = File.MethodAt(offset);
            HotPath h;
            return m != null && Hot.TryGetValue(m, out h) ? h : null;
        }

        /// <summary>"Update" or "HandleInput (via Update → HandleInput)".</summary>
        public static string Where(CsMethod m, HotPath h)
        {
            return h == null || h.Depth == 0 ? m.Name : m.Name + " (via " + h.Chain + ")";
        }
    }

    /// <summary>
    /// Text + light-parse linter for the project's C# (rule ids CODE.*). Two passes: parse every file and build a type
    /// index (inheritance, serialized fields), then run the rules per file. Engine-free so it runs inside Unity
    /// (CodeValidator) and in the container (Tools/qa/harness) — the Python port must produce the same issues.
    /// </summary>
    public sealed class CodeLinter
    {
        public readonly CodeLintOptions Options;
        public readonly List<LocKeyRef> LocKeys = new List<LocKeyRef>();

        public CodeLinter(CodeLintOptions options) { Options = options ?? new CodeLintOptions(); }

        /// <summary>Lints a set of files given as (project-relative path, text).</summary>
        public List<QaIssue> Lint(IList<KeyValuePair<string, string>> files)
        {
            var index = new CsTypeIndex();
            var parsed = new List<CodeLintContext>();
            foreach (var kv in files)
            {
                var src = new CSharpSource(kv.Key, kv.Value);
                var file = new CSharpFile(src);
                index.Add(file);
                parsed.Add(new CodeLintContext
                {
                    Path = kv.Key.Replace('\\', '/'),
                    FileName = FileNameOf(kv.Key),
                    Scope = Options.ScopeOf(kv.Key),
                    Src = src,
                    File = file,
                    Index = index,
                    Options = Options
                });
            }
            var issues = new List<QaIssue>();
            foreach (var ctx in parsed)
            {
                Prepare(ctx);
                CodeRules.RunAll(ctx);
                issues.AddRange(ctx.Issues);
                LocKeys.AddRange(ctx.LocKeys);
            }
            issues.Sort(QaReport.Compare);
            return issues;
        }

        /// <summary>Convenience for tests: lint one snippet as if it lived at <paramref name="path"/>.</summary>
        public static List<QaIssue> LintSnippet(string path, string text)
        {
            return new CodeLinter(null).Lint(new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(path, text) });
        }

        static string FileNameOf(string path)
        {
            string p = path.Replace('\\', '/');
            int slash = p.LastIndexOf('/');
            string name = slash >= 0 ? p.Substring(slash + 1) : p;
            int dot = name.LastIndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        static void Prepare(CodeLintContext ctx)
        {
            foreach (var u in ctx.File.Usings)
            {
                if (u == "System.Linq") ctx.UsesLinq = true;
                if (u == "UnityEngine.UIElements" || u == "UnityEngine.UI" || u == "TMPro") ctx.IsUiFile = true;
            }
            if (ctx.Options.Relative(ctx.Path).StartsWith("Runtime/UI/", StringComparison.Ordinal)) ctx.IsUiFile = true;
            ctx.DevOnly = CodeLintOptions.AnyGlob(ctx.Options.DevOnlyPaths, ctx.Path);
            foreach (var t in ctx.File.Types) ComputeHot(ctx, t);
        }

        static readonly Regex CallRx = new Regex(@"(?<![\w.])(?:this\s*\.\s*)?([A-Za-z_]\w*)\s*(?:<[^<>()]*>)?\s*\(", RegexOptions.Compiled);

        /// <summary>Marks per-frame roots (Update, Tick…) and the same-class methods they call, up to HotCallDepth.</summary>
        static void ComputeHot(CodeLintContext ctx, CsType t)
        {
            var byName = new Dictionary<string, List<CsMethod>>(StringComparer.Ordinal);
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                List<CsMethod> l;
                if (!byName.TryGetValue(m.Name, out l)) { l = new List<CsMethod>(); byName.Add(m.Name, l); }
                l.Add(m);
            }
            var queue = new List<CsMethod>();
            foreach (var m in t.Methods)
            {
                if (!m.HasBody || !ctx.Options.HotRoots.Contains(m.Name) || ctx.Src.IsNonShipping(m.Line) || ctx.DevOnly) continue;
                if (ctx.Hot.ContainsKey(m)) continue;
                ctx.Hot[m] = new HotPath { Depth = 0, Chain = m.Name };
                queue.Add(m);
            }
            for (int qi = 0; qi < queue.Count; qi++)
            {
                var m = queue[qi];
                var h = ctx.Hot[m];
                if (h.Depth >= ctx.Options.HotCallDepth) continue;
                string body = ctx.Masked.Substring(m.BodyStart, m.BodyEnd - m.BodyStart);
                foreach (Match c in CallRx.Matches(body))
                {
                    List<CsMethod> targets;
                    if (!byName.TryGetValue(c.Groups[1].Value, out targets)) continue;
                    foreach (var target in targets)
                    {
                        if (ctx.Hot.ContainsKey(target) || ctx.Src.IsNonShipping(target.Line)) continue;
                        ctx.Hot[target] = new HotPath { Depth = h.Depth + 1, Chain = h.Chain + " → " + target.Name };
                        queue.Add(target);
                    }
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ type resolution

        static readonly Regex LocalDeclRx = new Regex(
            @"(?<![\w.<>])((?:[A-Za-z_]\w*\s*\.\s*)*[A-Za-z_]\w*(?:\s*<[^;(){}=]*?>)?(?:\s*\[\s*\])*\??)\s+([A-Za-z_]\w*)\s*(?=[=;,)]|in\b)",
            RegexOptions.Compiled);

        static readonly HashSet<string> NotTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "return", "new", "else", "case", "throw", "await", "yield", "goto", "is", "as", "in", "out", "ref", "using", "default",
            "typeof", "sizeof", "nameof", "if", "while", "for", "foreach", "switch", "lock", "catch", "const", "static", "readonly",
            "public", "private", "protected", "internal", "params", "this", "base", "checked", "unchecked", "when", "where", "select",
            "from", "let", "orderby", "group", "into", "on", "equals", "by", "ascending", "descending", "operator", "event", "delegate"
        };

        static readonly Regex[] VarInference =
        {
            new Regex(@"\bGet(?:Component|Components|ComponentInChildren|ComponentsInChildren|ComponentInParent|ComponentsInParent)\s*<\s*([\w.]+)\s*>", RegexOptions.Compiled),
            new Regex(@"\b(?:AddComponent|FindObjectOfType|FindFirstObjectByType|FindAnyObjectByType|FindObjectsOfType|FindObjectsByType|Load|LoadAssetAtPath|Instantiate)\s*<\s*([\w.]+)\s*>", RegexOptions.Compiled),
            new Regex(@"^\s*new\s+([\w.]+)", RegexOptions.Compiled),
            new Regex(@"\bas\s+([\w.]+)\s*$", RegexOptions.Compiled),
            new Regex(@"^\s*\(\s*([A-Z][\w.]*)\s*\)", RegexOptions.Compiled)
        };

        /// <summary>Declared type of an identifier visible at <paramref name="offset"/> (locals, parameters, fields, properties).</summary>
        public static string TypeOf(CodeLintContext ctx, string name, int offset)
        {
            string masked = ctx.Masked;
            var m = ctx.File.MethodAt(offset);
            if (m != null)
            {
                string found = null;
                int from = m.BodyStart;
                string body = masked.Substring(from, Math.Max(0, offset - from));
                foreach (Match d in LocalDeclRx.Matches(body))
                {
                    if (d.Groups[2].Value != name) continue;
                    string type = Regex.Replace(d.Groups[1].Value, @"\s+", "");
                    if (NotTypes.Contains(type)) continue;
                    if (type == "var") type = InferVar(masked, from + d.Index + d.Length);
                    found = type;
                }
                if (found != null) return found;
                foreach (var p in m.Parameters) if (p.Value == name) return p.Key;
            }
            for (var t = ctx.File.TypeAt(offset); t != null; t = t.Parent)
            {
                foreach (var f in t.Fields) if (f.Name == name) return f.Type;
                foreach (var p in t.Properties) if (p.Name == name) return p.Type;
            }
            return null;
        }

        static string InferVar(string masked, int afterName)
        {
            int i = afterName;
            while (i < masked.Length && char.IsWhiteSpace(masked[i])) i++;
            string init;
            if (i < masked.Length && masked[i] == '=') init = TakeStatement(masked, i + 1);
            else if (masked.Length - i > 2 && masked.Substring(i, 2) == "in") init = TakeStatement(masked, i + 2);
            else return "var";
            foreach (var rx in VarInference)
            {
                var mm = rx.Match(init);
                if (mm.Success) return mm.Groups[1].Value;
            }
            return "var";
        }

        static string TakeStatement(string masked, int from)
        {
            int depth = 0, i = from;
            for (; i < masked.Length; i++)
            {
                char c = masked[i];
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') { if (depth == 0) break; depth--; }
                else if ((c == ';' || c == ',') && depth == 0) break;
            }
            return masked.Substring(from, i - from);
        }

        /// <summary>Element type for "T[]" / "List&lt;T&gt;" / "IList&lt;T&gt;"; null otherwise.</summary>
        public static string ElementType(string type)
        {
            if (string.IsNullOrEmpty(type)) return null;
            string t = type.Replace(" ", "");
            if (t.EndsWith("[]", StringComparison.Ordinal)) return t.Substring(0, t.Length - 2);
            var mm = Regex.Match(t, @"^(?:[\w.]*\.)?(?:List|IList|IReadOnlyList|HashSet|Queue|Stack)<([\w.]+)>$");
            return mm.Success ? mm.Groups[1].Value : null;
        }
    }
}
