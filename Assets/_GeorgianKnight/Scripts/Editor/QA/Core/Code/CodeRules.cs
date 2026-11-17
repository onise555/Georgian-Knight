using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// The CODE.* rules. Each rule works on the masked text (no strings/comments) plus the light structural model, and
    /// reports through <see cref="CodeLintContext.Add(QaRule,int,string)"/>. Keep messages stable: the Python port and
    /// suppressions ("match") depend on them.
    /// </summary>
    public static class CodeRules
    {
        const RegexOptions RX = RegexOptions.Compiled;

        public static void RunAll(CodeLintContext c)
        {
            CSharpVersion(c);
            if (c.IsRuntime)
            {
                TimeScale(c);
                Singleton(c);
                ResourcesLoad(c);
                StringMessaging(c);
                TagCompare(c);
                MagicNumbers(c);
                SoMutation(c);
                UiStrings(c);
                HotPathRules(c);
                OutsideHotPath(c);
                DebugLogRuntime(c);
                CollectLocKeys(c);
            }
            UnityNullOps(c);
            Catches(c);
            TypeRules(c);
            MethodRules(c);
            Comments(c);
            Namespace(c);
        }

        // ------------------------------------------------------------------------------------------------ helpers

        static bool Shipping(CodeLintContext c, int offset) { return !c.DevOnly && !c.Src.IsNonShippingAt(offset); }

        /// <summary>Text of the current statement up to <paramref name="offset"/> (back to the last ; { or }).</summary>
        static string StatementPrefix(string masked, int offset)
        {
            int i = offset - 1;
            while (i >= 0 && masked[i] != ';' && masked[i] != '{' && masked[i] != '}') i--;
            return masked.Substring(i + 1, offset - i - 1);
        }

        static string LinesSuffix(CodeLintContext c, List<int> offsets)
        {
            var lines = new List<int>();
            foreach (var o in offsets)
            {
                int l = c.Src.LineOf(o);
                if (!lines.Contains(l)) lines.Add(l);
            }
            if (lines.Count < 2) return "";
            var sb = new StringBuilder(" (lines ");
            for (int i = 0; i < lines.Count && i < 6; i++) sb.Append(i == 0 ? "" : ", ").Append(lines[i]);
            if (lines.Count > 6) sb.Append(", …");
            return sb.Append(')').ToString();
        }

        static string JoinLimited(List<string> items, int max)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < items.Count && i < max; i++) sb.Append(i == 0 ? "" : ", ").Append(items[i]);
            if (items.Count > max) sb.Append(", …");
            return sb.ToString();
        }

        static int CountWord(string text, string word)
        {
            return Regex.Matches(text, @"(?<![\w])" + Regex.Escape(word) + @"(?![\w])").Count;
        }

        // ------------------------------------------------------------------------------------------------ CSHARP_VERSION

        static readonly Regex[] ModernRx =
        {
            new Regex(@"\?\?=", RX), new Regex(@"\busing\s+var\b", RX), new Regex(@"\bswitch\s*\{", RX), new Regex(@"\bnew\s*\(", RX),
            new Regex(@"\[\s*\^", RX), new Regex(@"\.\.", RX), new Regex(@"\brecord\s+(?:class\s+|struct\s+)?[A-Z]\w*\s*[({:<]", RX),
            new Regex(@"[{;]\s*(init)\s*;", RX), new Regex(@"\bis\s+not\b", RX), new Regex(@"\bis\s*[<>]=?\s*[\w\d]", RX),
            new Regex(@"\bwith\s*\{", RX), new Regex(@"\b(?:string|object)\?(?=[\s>,)\]])", RX)
        };

        static readonly string[] ModernWhat =
        {
            "'??=' (C# 8)", "'using var' declaration (C# 8)", "switch expression (C# 8)", "target-typed 'new()' (C# 9)",
            "index from end '^' (C# 8)", "range '..' (C# 8)", "record type (C# 9)", "'init' accessor (C# 9)", "'is not' pattern (C# 9)",
            "relational pattern (C# 9)", "'with' expression (C# 9)", "nullable reference annotation (C# 8)"
        };

        static void CSharpVersion(CodeLintContext c)
        {
            for (int r = 0; r < ModernRx.Length; r++)
            {
                foreach (Match m in ModernRx[r].Matches(c.Masked))
                {
                    if (r == 3)
                    {
                        // `where T : new()` is the classic constructor constraint
                        int ls = c.Src.LineStart(c.Src.LineOf(m.Index));
                        if (Regex.IsMatch(c.Masked.Substring(ls, m.Index - ls), @"\bwhere\b")) continue;
                    }
                    int at = m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Index : m.Index;
                    c.Add(QaRules.CodeCSharpVersion, at, "C# 8+ syntax: " + ModernWhat[r]);
                }
            }
            foreach (Match m in Regex.Matches(c.Src.Text, @"(?m)^[ \t]*#nullable\b"))
                c.Add(QaRules.CodeCSharpVersion, m.Index, "C# 8+ syntax: '#nullable' directive (C# 8)");
        }

        // ------------------------------------------------------------------------------------------------ runtime-only rules

        static readonly Regex TimeScaleRx = new Regex(@"\bTime\s*\.\s*timeScale\s*[+\-*/]?=(?!=)", RX);

        static void TimeScale(CodeLintContext c)
        {
            if (CodeLintOptions.AnyGlob(c.Options.TimeScaleAllowList, c.Path)) return;
            foreach (Match m in TimeScaleRx.Matches(c.Masked))
                c.Add(QaRules.CodeTimeScaleWrite, m.Index, "writes Time.timeScale directly");
        }

        static readonly Regex SingletonRx = new Regex(@"\bstatic\s+(?:readonly\s+)?([A-Za-z_]\w*)\s+(Instance|instance|Current|current|Singleton|singleton|Main|main)\b", RX);

        static void Singleton(CodeLintContext c)
        {
            if (CodeLintOptions.AnyGlob(c.Options.SingletonAllowList, c.Path)) return;
            foreach (Match m in SingletonRx.Matches(c.Masked))
            {
                var t = c.File.TypeAt(m.Index);
                if (t == null || t.Name != m.Groups[1].Value || !Shipping(c, m.Index)) continue;
                c.Add(QaRules.CodeSingleton, m.Index, "static singleton " + t.Name + "." + m.Groups[2].Value);
            }
        }

        static readonly Regex ResourcesRx = new Regex(@"\bResources\s*\.\s*(Load|LoadAll|LoadAsync)\s*(?:<[^<>()]*>)?\s*\(", RX);

        static void ResourcesLoad(CodeLintContext c)
        {
            foreach (Match m in ResourcesRx.Matches(c.Masked))
            {
                if (!Shipping(c, m.Index)) continue;
                int p = m.Index + m.Length;
                while (p < c.Masked.Length && char.IsWhiteSpace(c.Masked[p])) p++;
                var lit = c.Src.LiteralAt(p);
                if (lit == null || lit.Start != p)
                {
                    c.Add(QaRules.CodeResourcesLoad, m.Index, "Resources." + m.Groups[1].Value + " with a computed path (root cannot be verified)",
                        QaSeverity.Minor, null);
                    continue;
                }
                bool ok = false;
                foreach (var root in c.Options.ResourcesRoots)
                    if (lit.Value == root || lit.Value.StartsWith(root + "/", StringComparison.Ordinal)) ok = true;
                if (!ok)
                    c.Add(QaRules.CodeResourcesLoad, m.Index, "Resources." + m.Groups[1].Value + "(\"" + lit.Value + "\") outside the allowed roots (" +
                        string.Join(", ", c.Options.ResourcesRoots.ToArray()) + ")");
            }
        }

        static readonly Regex SendMessageRx = new Regex(@"(?<!\w)(SendMessage|SendMessageUpwards|BroadcastMessage)\s*\(", RX);
        static readonly Regex InvokeRx = new Regex(@"(?<![\w.])(?:this\s*\.\s*)?(Invoke|InvokeRepeating|CancelInvoke|IsInvoking)\s*\(\s*(?:""|nameof\s*\()", RX);

        static void StringMessaging(CodeLintContext c)
        {
            foreach (Match m in SendMessageRx.Matches(c.Masked))
                if (Shipping(c, m.Index)) c.Add(QaRules.CodeSendMessage, m.Index, m.Groups[1].Value + "(…) dispatches by method name");
            foreach (Match m in InvokeRx.Matches(c.Masked))
                if (Shipping(c, m.Index)) c.Add(QaRules.CodeSendMessage, m.Index, m.Groups[1].Value + "(\"…\") calls a method by name");
        }

        static readonly Regex TagRx = new Regex(@"\.\s*tag\s*(?:==|!=)|(?:==|!=)\s*[\w.]*\.\s*tag\b", RX);

        static void TagCompare(CodeLintContext c)
        {
            foreach (Match m in TagRx.Matches(c.Masked))
                if (Shipping(c, m.Index)) c.Add(QaRules.CodeTagCompare, m.Index, "compares .tag with ==/!=");
        }

        static readonly Regex NumberRx = new Regex(@"(?<![\w.])(\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)([fFdDmMuUlL]*)(?![\w.])", RX);
        static readonly HashSet<string> NoMagicMethods = new HashSet<string>(StringComparer.Ordinal) { "OnDrawGizmos", "OnDrawGizmosSelected", "OnValidate", "Reset" };

        static void MagicNumbers(CodeLintContext c)
        {
            if (!CodeLintOptions.AnyGlob(c.Options.BalanceDirs, c.Path)) return;
            foreach (var t in c.File.Types)
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody || NoMagicMethods.Contains(m.Name) || m.Name.StartsWith("Editor", StringComparison.Ordinal)) continue;
                    var found = new List<string>();
                    int first = MagicInMethod(c, m, found);
                    if (found.Count > 0)
                        c.Add(QaRules.CodeMagicNumber, first, found.Count + " magic number(s) in " + t.Name + "." + m.Name + ": " + JoinLimited(found, 8));
                }
        }

        /// <summary>Collects distinct tuning literals of one method; returns the offset of the first (or -1).</summary>
        static int MagicInMethod(CodeLintContext c, CsMethod m, List<string> found)
        {
            int first = -1;
            string body = c.Masked.Substring(m.BodyStart, m.BodyEnd - m.BodyStart);
            foreach (Match n in NumberRx.Matches(body))
            {
                int abs = m.BodyStart + n.Index;
                double v;
                if (!Shipping(c, abs) || !double.TryParse(n.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                if (c.Options.NeutralNumbers.Contains(v) || (v > 0 && v <= 0.01)) continue;
                int prev = SkipWsBack(body, n.Index - 1);
                if (prev >= 0 && body[prev] == '[') continue;                                     // index
                int lineStart = c.Src.LineStart(c.Src.LineOf(abs));
                if (Regex.IsMatch(c.Masked.Substring(lineStart, abs - lineStart), @"\bconst\b")) continue;    // named local constant
                if (!found.Contains(n.Value)) found.Add(n.Value);
                if (first < 0) first = abs;
            }
            return first;
        }

        static readonly Regex MemberWriteRx = new Regex(@"(?<![\w.])([a-z_]\w*)\s*\.\s*([A-Za-z_]\w*)\s*(?:[+\-*/]?=(?![=>])|\+\+|--)", RX);
        static readonly Regex SelfWriteRx = new Regex(@"(?<![\w.])(?:this\s*\.\s*)?([a-z_]\w*)\s*(?:[+\-*/]?=(?![=>])|\+\+|--)", RX);
        static readonly HashSet<string> SoInitMethods = new HashSet<string>(StringComparer.Ordinal) { "OnValidate", "Reset", "Awake", "OnEnable" };

        static void SoMutation(CodeLintContext c)
        {
            ExternalSoWrites(c);
            foreach (var t in c.File.Types)
                if (t.Kind == CsTypeKind.Class && c.Index.IsScriptableObject(t.Name)) SelfSoWrites(c, t);
        }

        /// <summary>someDefinition.field = … where the variable is a ScriptableObject type and the field is serialized.</summary>
        static void ExternalSoWrites(CodeLintContext c)
        {
            foreach (Match m in MemberWriteRx.Matches(c.Masked))
            {
                if (!Shipping(c, m.Index)) continue;
                var method = c.File.MethodAt(m.Index);
                if (method == null || method.Name.StartsWith("Editor", StringComparison.Ordinal)) continue;
                string type = CodeLinter.TypeOf(c, m.Groups[1].Value, m.Index);
                if (type == null || !c.Index.IsScriptableObject(type)) continue;
                if (method.Owner != null && c.Index.IsScriptableObject(method.Owner.Name) && SoInitMethods.Contains(method.Name)) continue;
                if (!c.Index.SerializedFields(type).Contains(m.Groups[2].Value)) continue;
                c.Add(QaRules.CodeSoMutation, m.Index, "writes " + CSharpFile.SimpleTypeName(type) + "." + m.Groups[2].Value + " (asset data) at runtime");
            }
        }

        /// <summary>A ScriptableObject's own methods writing its serialized fields (outside OnValidate/Reset/Awake/OnEnable/Editor*).</summary>
        static void SelfSoWrites(CodeLintContext c, CsType t)
        {
            var own = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in t.Fields)
                if (!f.Has("static") && !f.Has("const") && !f.Has("readonly") && (f.Has("public") || f.HasAttribute("SerializeField") || f.HasAttribute("SerializeReference")))
                    own.Add(f.Name);
            if (own.Count == 0) return;
            foreach (var m in t.Methods)
            {
                if (!m.HasBody || m.IsConstructor || SoInitMethods.Contains(m.Name) || m.Name.StartsWith("Editor", StringComparison.Ordinal)) continue;
                string body = c.Masked.Substring(m.BodyStart, m.BodyEnd - m.BodyStart);
                foreach (Match w in SelfWriteRx.Matches(body))
                {
                    int abs = m.BodyStart + w.Index;
                    if (!own.Contains(w.Groups[1].Value) || !Shipping(c, abs) || LocalShadows(c, w.Groups[1].Value, m, abs)) continue;
                    c.Add(QaRules.CodeSoMutation, abs, t.Name + "." + m.Name + " writes its own serialized field '" + w.Groups[1].Value + "'", QaSeverity.Major, null);
                }
            }
        }

        static bool LocalShadows(CodeLintContext c, string name, CsMethod m, int offset)
        {
            foreach (var p in m.Parameters) if (p.Value == name) return true;
            string body = c.Masked.Substring(m.BodyStart, offset - m.BodyStart);
            return Regex.IsMatch(body, @"[\w>\]]\s+" + Regex.Escape(name) + @"\s*(?:=|;|,|\bin\b)");
        }

        // ------------------------------------------------------------------------------------------------ UI strings / Georgian / keys

        static readonly Regex UiContextRx = new Regex(
            @"\.\s*(?:text|tooltip|label|title)\s*=|\bnew\s+(?:Label|Button|Toggle|Foldout|TextField|TextElement|GroupBox|Tab|RadioButton|DropdownField)\s*\(|" +
            @"\.\s*(?:SetText|ShowNotification|ShowToast|Notify|ShowMessage|ShowPrompt|SetLabel|SetTitle|SetPrompt|ShowHint|ShowSubtitle|Toast)\s*\(|" +
            @"(?:^|[\s,{(])(?:text|label|title|tooltip|prompt|caption|message|header)\s*=\s*$", RX);
        static readonly Regex LocCallRx = new Regex(@"\bLoc\s*\.\s*(?:Get|Format|GetOrNull|Has)\s*\(\s*$", RX);
        static readonly Regex KeyLikeRx = new Regex(@"^[a-z0-9_]+(?:\.[a-z0-9_\-{}]+)+$", RX);

        /// <summary>True for text a player would read (Georgian, sentences, capitalised words) — not keys, ids or USS classes.</summary>
        public static bool LooksUserFacing(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (LocText.HasGeorgian(value)) return true;
            string v = Regex.Replace(value, @"\{\d+(?:[:,][^}]*)?\}", " ").Trim();
            if (KeyLikeRx.IsMatch(v)) return false;
            int latin = 0;
            foreach (char ch in v) if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')) latin++;
            if (latin < 2) return false;
            if (v.IndexOf(' ') >= 0 && latin >= 3) return true;
            return Regex.IsMatch(v, @"^[A-Z][a-z]{2,}");
        }

        static void UiStrings(CodeLintContext c)
        {
            foreach (var lit in c.Src.Literals)
            {
                if (lit.IsChar || !Shipping(c, lit.Start) || c.File.InAttribute(lit.Start)) continue;
                string prefix = StatementPrefix(c.Masked, lit.Start);
                if (LocCallRx.IsMatch(prefix)) continue;
                bool ui = c.IsUiFile && UiContextRx.IsMatch(prefix) && LooksUserFacing(lit.Value);
                if (ui)
                {
                    c.Add(QaRules.CodeHardcodedUiString, lit.Start, "hard-coded UI text \"" + Shorten(lit.Value, 40) + "\"");
                    continue;
                }
                if (LocText.HasGeorgian(lit.Value))
                    c.Add(QaRules.CodeGeorgianLiteral, lit.Start, "Georgian text in code \"" + Shorten(lit.Value, 40) + "\"");
            }
        }

        static void CollectLocKeys(CodeLintContext c)
        {
            foreach (var lit in c.Src.Literals)
            {
                if (lit.IsChar || lit.Interpolated || lit.Value.Length == 0) continue;
                bool direct = LocCallRx.IsMatch(StatementPrefix(c.Masked, lit.Start)) && KeyLikeRx.IsMatch(lit.Value);
                bool keyLike = Regex.IsMatch(lit.Value, @"^ui\.[a-z0-9_]+(?:\.[a-z0-9_\-]+)+$");
                if (!direct && !keyLike) continue;
                if (lit.Value.EndsWith(".", StringComparison.Ordinal)) continue;     // prefix used for concatenation
                c.LocKeys.Add(new LocKeyRef { Key = lit.Value, Path = c.Path, Line = lit.Line, Direct = direct });
            }
        }

        public static string Shorten(string s, int max)
        {
            s = (s ?? "").Replace("\n", "\\n");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        // ------------------------------------------------------------------------------------------------ hot paths

        static readonly Regex FindRx = new Regex(@"(?<!\w)(FindObjectOfType|FindObjectsOfType|FindAnyObjectByType|FindFirstObjectByType|FindObjectsByType|FindWithTag|FindGameObjectWithTag|FindGameObjectsWithTag)\b|\bGameObject\s*\.\s*(Find)\s*\(", RX);
        static readonly Regex GetCompRx = new Regex(@"(?<!\w)(GetComponent|GetComponents|GetComponentInChildren|GetComponentsInChildren|GetComponentInParent|GetComponentsInParent)\s*(?:<[^<>()]*>)?\s*\(", RX);
        static readonly Regex LinqRx = new Regex(@"\.\s*(Where|Select|SelectMany|Any|All|First|FirstOrDefault|Last|LastOrDefault|Single|SingleOrDefault|OrderBy|OrderByDescending|ThenBy|ThenByDescending|ToList|ToArray|ToDictionary|ToLookup|GroupBy|Sum|Average|Max|Min|Count|Distinct|Concat|Skip|Take|SkipWhile|TakeWhile|Aggregate|Cast|OfType|Zip|Except|Intersect|Union|DefaultIfEmpty|ElementAt|SequenceEqual)\s*(?:<[^<>()]*>)?\s*\(", RX);
        static readonly HashSet<string> NonLinqReceivers = new HashSet<string>(StringComparer.Ordinal)
        { "Mathf", "Math", "Vector2", "Vector3", "Vector4", "Color", "Quaternion", "string", "String", "Physics", "Physics2D", "Path", "Interlocked", "Array" };
        static readonly Regex[] AllocRx =
        {
            new Regex(@"\bnew\s+(List|Dictionary|HashSet|Queue|Stack|LinkedList|SortedList|SortedDictionary|SortedSet|StringBuilder)\b", RX),
            new Regex(@"\bnew\s+(WaitForSeconds|WaitForSecondsRealtime|WaitUntil|WaitWhile|WaitForEndOfFrame|WaitForFixedUpdate)\b", RX),
            new Regex(@"\bnew\s+[A-Za-z_][\w.]*\s*(?:<[^<>()]*>)?\s*\[|\bnew\s*\[\s*\]", RX),
            new Regex(@"\b(?:string|String)\s*\.\s*(Format|Concat|Join)\s*\(", RX),
            new Regex(@"\.\s*(ToString)\s*\(", RX),
            new Regex(@"\.\s*(Split|Substring|Replace|ToLower|ToUpper|ToLowerInvariant|ToUpperInvariant|Trim|TrimStart|TrimEnd|PadLeft|PadRight)\s*\(", RX),
            new Regex(@"=>", RX),
            new Regex(@"\bdelegate\s*[({]", RX)
        };
        static readonly Regex ErrorPathRx = new Regex(@"\bDebug\s*\.\s*(?:Log\w*|Assert\w*)\s*\(|\bthrow\b", RX);
        static readonly Regex AnimStringRx = new Regex(@"\.\s*(SetFloat|SetBool|SetInteger|SetTrigger|ResetTrigger|GetFloat|GetBool|GetInteger|Play|PlayInFixedTime|CrossFade|CrossFadeInFixedTime|IsName|IsTag|GetLayerIndex)\s*\(\s*""", RX);
        static readonly Regex DebugLogRx = new Regex(@"(?<!\w)(?:UnityEngine\s*\.\s*)?Debug\s*\.\s*(Log|LogFormat)\s*\(", RX);
        static readonly Regex CameraMainRx = new Regex(@"\bCamera\s*\.\s*main\b", RX);
        static readonly Regex PhysicsAllocRx = new Regex(@"\b(Physics|Physics2D)\s*\.\s*(RaycastAll|OverlapSphere|OverlapBox|OverlapCapsule|SphereCastAll|CapsuleCastAll|BoxCastAll|OverlapCircleAll|OverlapAreaAll|OverlapPointAll|CircleCastAll|LinecastAll)\s*\(", RX);

        sealed class Hits
        {
            public readonly List<string> LabelAt = new List<string>();
            public readonly List<int> Offsets = new List<int>();
            public void Add(string label, int offset)
            {
                LabelAt.Add(label);
                Offsets.Add(offset);
            }
        }

        static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "null", "true", "false", "new", "return", "if", "else", "var", "int", "float", "double", "bool", "string", "object", "void",
            "default", "typeof", "nameof", "is", "as", "in", "out", "ref", "for", "foreach", "while", "switch", "case", "break",
            "continue", "throw", "try", "catch", "finally", "using", "await", "async", "yield", "checked", "unchecked", "sizeof",
            "lock", "do", "goto", "params", "delegate", "char", "byte", "long", "uint", "short", "decimal", "when", "where"
        };
        static readonly Regex IdentRx = new Regex(@"(?<![\w.])([A-Za-z_]\w*)", RX);
        static readonly Regex InnerParamRx = new Regex(@"(?:\(([^()]*)\)|([A-Za-z_]\w*))\s*=>", RX);

        /// <summary>
        /// True when the lambda whose '=>' is at <paramref name="arrow"/> captures locals, parameters or 'this' (and therefore
        /// allocates a closure per evaluation); non-capturing lambdas are cached by the compiler.
        /// </summary>
        static bool LambdaCaptures(CodeLintContext c, CsMethod m, int arrow)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (!LambdaParameters(c, arrow, names)) return true;
            int start, end;
            LambdaBody(c, m, arrow, out start, out end);
            string body = c.Masked.Substring(start, Math.Max(0, end - start));
            foreach (Match ip in InnerParamRx.Matches(body))
            {
                if (ip.Groups[2].Success) names.Add(ip.Groups[2].Value);
                else AddLastTokens(ip.Groups[1].Value, names);
            }
            foreach (Match d in Regex.Matches(body, @"[\w>\]]\s+([A-Za-z_]\w*)\s*(?:=(?!=)|;|\bin\b)")) names.Add(d.Groups[1].Value);
            foreach (Match id in IdentRx.Matches(body))
                if (Captures(c, m.Owner, id.Groups[1].Value, start + id.Index, names)) return true;
            return false;
        }

        /// <summary>Adds the lambda's parameter names; false when they cannot be determined (treated as capturing).</summary>
        static bool LambdaParameters(CodeLintContext c, int arrow, HashSet<string> names)
        {
            string s = c.Masked;
            int p = SkipWsBack(s, arrow - 1);
            if (p < 0) return false;
            if (s[p] != ')')
            {
                int st = IdentStartBack(s, p);
                names.Add(s.Substring(st + 1, p - st));
                return true;
            }
            int open = c.File.Match(p);
            if (open < 0) return false;
            AddLastTokens(s.Substring(open + 1, p - open - 1), names);
            return true;
        }

        static void AddLastTokens(string list, HashSet<string> names)
        {
            foreach (var part in CSharpFile.SplitTopLevel(list, ','))
            {
                var toks = CSharpFile.Tokens(part.Trim());
                if (toks.Count > 0) names.Add(toks[toks.Count - 1]);
            }
        }

        /// <summary>Range of the lambda body: a { block } or the expression up to the enclosing ',', ')' or ';'.</summary>
        static void LambdaBody(CodeLintContext c, CsMethod m, int arrow, out int start, out int end)
        {
            string s = c.Masked;
            start = arrow + 2;
            while (start < m.BodyEnd && char.IsWhiteSpace(s[start])) start++;
            if (start < s.Length && s[start] == '{') { end = Math.Max(start, c.File.Match(start)); return; }
            int depth = 0;
            for (end = start; end < m.BodyEnd; end++)
            {
                char ch = s[end];
                if (ch == '(' || ch == '[' || ch == '{') depth++;
                else if (ch == ')' || ch == ']' || ch == '}') { if (depth == 0) return; depth--; }
                else if ((ch == ',' || ch == ';') && depth == 0) return;
            }
        }

        /// <summary>Does identifier <paramref name="n"/> inside a lambda body refer to a local, parameter or instance member?</summary>
        static bool Captures(CodeLintContext c, CsType owner, string n, int at, HashSet<string> names)
        {
            if (n == "this" || n == "base") return true;
            if (names.Contains(n) || Keywords.Contains(n)) return false;
            if (char.IsLower(n[0]) || n[0] == '_') return true;
            if (owner == null) return false;
            string s = c.Masked;
            int after = at + n.Length;
            while (after < s.Length && char.IsWhiteSpace(s[after])) after++;
            return after < s.Length && s[after] == '(' ? IsInstanceMethod(owner, n) : IsInstanceMember(owner, n);
        }

        static bool IsInstanceMethod(CsType owner, string n)
        {
            foreach (var m in owner.Methods) if (m.Name == n && !m.Has("static")) return true;
            return false;
        }

        static bool IsInstanceMember(CsType owner, string n)
        {
            foreach (var f in owner.Fields) if (f.Name == n && !f.Has("static") && !f.Has("const")) return true;
            foreach (var p in owner.Properties) if (p.Name == n && !p.Has("static")) return true;
            return false;
        }

        static readonly Regex IfRx = new Regex(@"\bif\s*\(", RX);
        static readonly Regex NeqRx = new Regex(@"([A-Za-z_][\w.]*)\s*!=\s*([A-Za-z_][\w.]*)", RX);
        static readonly Regex RefNeqRx = new Regex(@"!\s*(?:object\s*\.\s*)?(?:ReferenceEquals|Equals)\s*\(\s*([\w.]+)\s*,\s*([\w.]+)\s*\)", RX);
        static readonly Regex NotRx = new Regex(@"!\s*([A-Za-z_][\w.]*)(?![\w.]*\s*\()", RX);

        /// <summary>
        /// True when the offset sits inside an <c>if</c> whose condition compares against a cached value that the method then
        /// updates (<c>if (x != last) { last = x; label.text = x.ToString(); }</c>): the allocation happens on change only.
        /// </summary>
        static bool IsChangeGuarded(CodeLintContext c, CsMethod m, int offset)
        {
            string s = c.Masked;
            foreach (Match x in IfRx.Matches(s.Substring(m.BodyStart, m.BodyEnd - m.BodyStart)))
            {
                int open = m.BodyStart + x.Index + x.Length - 1;
                int close = c.File.Match(open);
                if (close < 0 || close > offset) continue;
                int st = close + 1;
                while (st < m.BodyEnd && char.IsWhiteSpace(s[st])) st++;
                int end = st < s.Length && s[st] == '{' ? c.File.Match(st) : s.IndexOf(';', st);
                if (end < 0 || offset < st || offset > end) continue;
                string cond = s.Substring(open + 1, close - open - 1);
                var vars = new List<string>();
                foreach (Match v in NeqRx.Matches(cond)) { vars.Add(v.Groups[1].Value); vars.Add(v.Groups[2].Value); }
                foreach (Match v in RefNeqRx.Matches(cond)) { vars.Add(v.Groups[1].Value); vars.Add(v.Groups[2].Value); }
                foreach (Match v in NotRx.Matches(cond)) vars.Add(v.Groups[1].Value);
                string rest = s.Substring(st, m.BodyEnd - st);
                foreach (var v in vars)
                    if (Regex.IsMatch(rest, @"(?<![\w.])" + Regex.Escape(v) + @"\s*=(?!=)")) return true;
            }
            return false;
        }

        static string ReceiverBefore(string masked, int dotIndex)
        {
            int i = dotIndex - 1;
            while (i >= 0 && char.IsWhiteSpace(masked[i])) i--;
            int end = i;
            while (i >= 0 && (char.IsLetterOrDigit(masked[i]) || masked[i] == '_')) i--;
            return end > i ? masked.Substring(i + 1, end - i) : "";
        }

        static void HotPathRules(CodeLintContext c)
        {
            var methods = new List<CsMethod>(c.Hot.Keys);
            methods.Sort((a, b) => a.BodyStart.CompareTo(b.BodyStart));
            foreach (var m in methods)
            {
                var h = c.Hot[m];
                string body = c.Masked.Substring(m.BodyStart, m.BodyEnd - m.BodyStart);
                EmitHot(c, QaRules.CodeFindInHotPath, Collect(FindRx, body, m.BodyStart, x => x.Groups[1].Success ? x.Groups[1].Value : "GameObject.Find"), m, h);
                EmitHot(c, QaRules.CodeGetComponentHot, Collect(GetCompRx, body, m.BodyStart, x => x.Groups[1].Value), m, h);
                if (c.UsesLinq)
                    EmitHot(c, QaRules.CodeLinqHot, Collect(LinqRx, body, m.BodyStart,
                        x => NonLinqReceivers.Contains(ReceiverBefore(body, x.Index)) ? null : "." + x.Groups[1].Value + "()"), m, h);
                EmitHot(c, QaRules.CodeAllocHot, Allocations(c, m, body), m, h);
                EmitHot(c, QaRules.CodeStringAnimatorHot, Collect(AnimStringRx, body, m.BodyStart, x => x.Groups[1].Value + "(\"…\")"), m, h);
                EmitHot(c, QaRules.CodeDebugLogHot, Collect(DebugLogRx, body, m.BodyStart, x => "Debug." + x.Groups[1].Value), m, h);
                EmitHot(c, QaRules.CodeCameraMainHot, Collect(CameraMainRx, body, m.BodyStart, x => "Camera.main"), m, h);
                EmitHot(c, QaRules.CodePhysicsAlloc, Collect(PhysicsAllocRx, body, m.BodyStart, x => x.Groups[1].Value + "." + x.Groups[2].Value), m, h);
            }
        }

        /// <summary>Matches of <paramref name="rx"/> in a method body as (label, absolute offset); a null label skips the match.</summary>
        static Hits Collect(Regex rx, string body, int offset, Func<Match, string> label)
        {
            var hits = new Hits();
            foreach (Match x in rx.Matches(body))
            {
                string l = label(x);
                if (l != null) hits.Add(l, offset + x.Index);
            }
            return hits;
        }

        static readonly string[] AllocLabels = { "new ", "new ", "new array", "string.", "ToString()", ".", "capturing lambda", "anonymous delegate" };

        static Hits Allocations(CodeLintContext c, CsMethod m, string body)
        {
            var alloc = new Hits();
            for (int r = 0; r < AllocRx.Length; r++)
            {
                foreach (Match x in AllocRx[r].Matches(body))
                {
                    if (r == 6 && !LambdaCaptures(c, m, m.BodyStart + x.Index)) continue;
                    string label = AllocLabels[r];
                    if (r <= 1 || r == 3) label += x.Groups[1].Value;
                    else if (r == 5) label += x.Groups[1].Value + "()";
                    alloc.Add(label, m.BodyStart + x.Index);
                }
            }
            foreach (var lit in c.Src.Literals)
            {
                if (lit.IsChar || lit.Start < m.BodyStart || lit.Start >= m.BodyEnd) continue;
                if (lit.Interpolated) alloc.Add("interpolated string", lit.Start);
                else if (Concatenated(c.Masked, lit)) alloc.Add("string +", lit.Start);
            }
            return alloc;
        }

        /// <summary>True when a string literal is an operand of '+' (string concatenation).</summary>
        static bool Concatenated(string s, SourceLiteral lit)
        {
            int p = SkipWsBack(s, lit.Start - 1);
            int q = lit.End;
            while (q < s.Length && char.IsWhiteSpace(s[q])) q++;
            return (p >= 0 && s[p] == '+') || (q < s.Length && s[q] == '+' && (q + 1 >= s.Length || s[q + 1] != '+'));
        }

        static void EmitHot(CodeLintContext c, QaRule rule, Hits hits, CsMethod m, HotPath h)
        {
            var offsets = new List<int>();
            var labels = new List<string>();
            for (int i = 0; i < hits.Offsets.Count; i++)
            {
                int o = hits.Offsets[i];
                if (!Shipping(c, o)) continue;
                if (rule == QaRules.CodeAllocHot && (ErrorPathRx.IsMatch(StatementPrefix(c.Masked, o)) || IsChangeGuarded(c, m, o))) continue;
                offsets.Add(o);
                if (!labels.Contains(hits.LabelAt[i])) labels.Add(hits.LabelAt[i]);
            }
            if (offsets.Count == 0) return;
            offsets.Sort();
            var sev = h.Depth == 0 ? rule.Severity : QaSeverities.Milder(rule.Severity);
            c.Add(rule, offsets[0], string.Join(", ", labels.ToArray()) + " in " + CodeLintContext.Where(m, h) + LinesSuffix(c, offsets), sev, null);
        }

        static void OutsideHotPath(CodeLintContext c)
        {
            bool findAllowed = CodeLintOptions.AnyGlob(c.Options.FindAllowList, c.Path);
            foreach (Match x in FindRx.Matches(c.Masked))
            {
                if (findAllowed || !Shipping(c, x.Index) || c.HotAt(x.Index) != null) continue;
                c.Add(QaRules.CodeFindRuntime, x.Index, (x.Groups[1].Success ? x.Groups[1].Value : "GameObject.Find") + " at runtime");
            }
            foreach (Match x in PhysicsAllocRx.Matches(c.Masked))
            {
                if (!Shipping(c, x.Index) || c.HotAt(x.Index) != null) continue;
                c.Add(QaRules.CodePhysicsAlloc, x.Index, x.Groups[1].Value + "." + x.Groups[2].Value + " allocates a new array per call", QaSeverity.Minor, null);
            }
        }

        static void DebugLogRuntime(CodeLintContext c)
        {
            var offsets = new List<int>();
            foreach (Match x in DebugLogRx.Matches(c.Masked))
                if (Shipping(c, x.Index) && c.HotAt(x.Index) == null) offsets.Add(x.Index);
            if (offsets.Count == 0) return;
            c.Add(QaRules.CodeDebugLogRuntime, offsets[0], offsets.Count + " Debug.Log call(s) in shipping code" + LinesSuffix(c, offsets));
        }

        // ------------------------------------------------------------------------------------------------ ?. / ?? on Unity objects

        static readonly Regex NullOpRx = new Regex(@"\?\.|\?\?", RX);
        static readonly HashSet<string> UnityCalls = new HashSet<string>(StringComparer.Ordinal)
        {
            "GetComponent", "GetComponentInChildren", "GetComponentInParent", "AddComponent", "FindObjectOfType", "FindFirstObjectByType",
            "FindAnyObjectByType", "Instantiate", "FindWithTag", "FindGameObjectWithTag", "GetChild"
        };
        /// <summary>Calls that return a Unity object only with a known receiver (Resources.Load, GameObject.Find, transform.Find).</summary>
        static readonly Regex QualifiedUnityCallRx = new Regex(@"(?:^|\.)(?:Resources\.Load\w*|AssetDatabase\.Load\w*|GameObject\.Find|transform\.Find)$", RX);
        static readonly HashSet<string> UnityMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            "main", "transform", "gameObject", "parent", "root", "sharedMaterial", "material", "sharedMesh", "mesh", "clip",
            "runtimeAnimatorController", "attachedRigidbody", "collider", "rigidbody", "targetTexture", "avatar", "sprite"
        };

        static void UnityNullOps(CodeLintContext c)
        {
            string s = c.Masked;
            foreach (Match x in NullOpRx.Matches(s))
            {
                int at = x.Index;
                if (x.Value == "??" && at > 0 && s[at - 1] == '?') continue;
                string what = UnityReceiver(c, at);
                if (what == null) continue;
                var sev = c.IsRuntime && Shipping(c, at) ? QaSeverity.Critical : QaSeverity.Major;
                c.Add(QaRules.CodeUnityNullOp, at, "'" + x.Value + "' on " + what + " (UnityEngine.Object fake-null)", sev, null);
            }
        }

        static bool IsIdent(char ch) { return char.IsLetterOrDigit(ch) || ch == '_'; }

        static int SkipWsBack(string s, int i)
        {
            while (i >= 0 && char.IsWhiteSpace(s[i])) i--;
            return i;
        }

        /// <summary>Index just before the identifier that ends at <paramref name="i"/>.</summary>
        static int IdentStartBack(string s, int i)
        {
            while (i >= 0 && IsIdent(s[i])) i--;
            return i;
        }

        /// <summary>Skips a generic argument list ending at <paramref name="j"/> ("Foo&lt;Bar&gt;") backwards.</summary>
        static int SkipGenericBack(string s, int j)
        {
            if (j < 0 || s[j] != '>') return j;
            int depth = 0;
            for (; j >= 0; j--)
            {
                if (s[j] == '>') depth++;
                else if (s[j] == '<' && --depth == 0) { j--; break; }
            }
            return SkipWsBack(s, j);
        }

        /// <summary>"a.b.Call" for a call name whose identifier starts after <paramref name="back"/>.</summary>
        static string QualifierChain(string s, int back, string chain)
        {
            while (true)
            {
                int q = SkipWsBack(s, back);
                if (q < 0 || s[q] != '.') return chain;
                q = SkipWsBack(s, q - 1);
                int start = IdentStartBack(s, q);
                if (q <= start) return chain;
                chain = s.Substring(start + 1, q - start) + "." + chain;
                back = start;
            }
        }

        /// <summary>Description of the Unity-object receiver before a ?. / ?? operator, or null when it is not (provably) one.</summary>
        static string UnityReceiver(CodeLintContext c, int opIndex)
        {
            string s = c.Masked;
            int i = SkipWsBack(s, opIndex - 1);
            if (i < 0) return null;
            if (s[i] == ')') return CallReceiver(c, i);
            if (s[i] == ']') return IndexReceiver(c, i, opIndex);
            return IsIdent(s[i]) ? IdentReceiver(c, i, opIndex) : null;
        }

        static string CallReceiver(CodeLintContext c, int close)
        {
            string s = c.Masked;
            int open = c.File.Match(close);
            if (open < 0) return null;
            var cast = Regex.Match(s.Substring(open + 1, close - open - 1), @"\bas\s+([\w.]+)\s*$");
            if (cast.Success) return c.Index.IsUnityObject(cast.Groups[1].Value) ? cast.Groups[1].Value : null;
            int end = SkipGenericBack(s, SkipWsBack(s, open - 1));
            int start = IdentStartBack(s, end);
            string call = end > start ? s.Substring(start + 1, end - start) : "";
            string chain = QualifierChain(s, start, call);
            return UnityCalls.Contains(call) || QualifiedUnityCallRx.IsMatch(chain) ? call + "(…)" : null;
        }

        static string IndexReceiver(CodeLintContext c, int close, int opIndex)
        {
            string s = c.Masked;
            int open = c.File.Match(close);
            if (open < 0) return null;
            int end = SkipWsBack(s, open - 1);
            int start = IdentStartBack(s, end);
            if (end <= start) return null;
            string name = s.Substring(start + 1, end - start);
            string elem = CodeLinter.ElementType(CodeLinter.TypeOf(c, name, opIndex));
            return elem != null && c.Index.IsUnityObject(elem) ? name + "[…] (" + elem + ")" : null;
        }

        static string IdentReceiver(CodeLintContext c, int end, int opIndex)
        {
            string s = c.Masked;
            int start = IdentStartBack(s, end);
            string ident = s.Substring(start + 1, end - start);
            int k = SkipWsBack(s, start);
            if (k >= 0 && s[k] == '.')
            {
                int ownerEnd = SkipWsBack(s, k - 1);
                int ownerStart = IdentStartBack(s, ownerEnd);
                string owner = ownerEnd > ownerStart ? s.Substring(ownerStart + 1, ownerEnd - ownerStart) : "";
                if (UnityMembers.Contains(ident)) return owner + "." + ident;
                if (owner != "this") return null;
            }
            if (Regex.IsMatch(ident, @"^\d") || ident == "null" || ident == "this") return null;
            string type = CodeLinter.TypeOf(c, ident, opIndex);
            return type != null && c.Index.IsUnityObject(type) ? "'" + ident + "' (" + CSharpFile.SimpleTypeName(type) + ")" : null;
        }

        // ------------------------------------------------------------------------------------------------ catch blocks

        static readonly Regex CatchRx = new Regex(@"\bcatch\b\s*(?:\([^)]*\))?\s*(?:when\s*\([^)]*\)\s*)?\{", RX);
        static readonly Regex LogsRx = new Regex(@"\bthrow\b|\bDebug\s*\.\s*Log\w*|\bLog\s*\.\s*\w+\s*\(|\bGKBridge\s*\.\s*Log\b|\bLog(?:Error|Warning|Exception)\s*\(|\bReport\w*\s*\(|\bAssert\s*\.", RX);

        static void Catches(CodeLintContext c)
        {
            foreach (Match x in CatchRx.Matches(c.Masked))
            {
                int open = x.Index + x.Length - 1;
                int close = c.File.Match(open);
                if (close < 0) continue;
                string body = c.Masked.Substring(open + 1, close - open - 1);
                bool commented = false;
                foreach (var cm in c.Src.Comments) if (cm.Start > open && cm.Start < close) { commented = true; break; }
                if (body.Trim().Length == 0)
                {
                    if (commented) c.Add(QaRules.CodeEmptyCatch, x.Index, "empty catch block (documented)", QaSeverity.Minor, null);
                    else c.Add(QaRules.CodeEmptyCatch, x.Index, "empty catch block swallows the exception");
                    continue;
                }
                var v = Regex.Match(x.Value, @"\(\s*[\w.]+\s+([A-Za-z_]\w*)\s*\)");
                bool usesException = v.Success && CountWord(body, v.Groups[1].Value) > 0;
                if (!commented && !usesException && !LogsRx.IsMatch(body))
                    c.Add(QaRules.CodeCatchNoLog, x.Index, "catch handles the exception without logging or rethrowing");
            }
        }

        // ------------------------------------------------------------------------------------------------ type-level rules

        static void TypeRules(CodeLintContext c)
        {
            var unityTop = new List<CsType>();
            var topNames = new List<string>();
            foreach (var t in c.File.Types)
            {
                bool script = t.Kind == CsTypeKind.Class && !t.Has("abstract") && !t.IsGeneric && c.Index.IsSerializedScript(t.Name);
                if (t.Parent == null)
                {
                    topNames.Add(t.Name);
                    // editor assemblies: a MonoBehaviour whose file matches gets an editor MonoScript and Unity refuses to
                    // AddComponent it, so editor-only helper components (recorders, probes) live in a file of another name
                    bool editorComponent = c.Scope == CodeScope.Editor && c.Index.IsComponent(t.Name);
                    if (script && !editorComponent) unityTop.Add(t);
                }
                else if (script)
                    c.Add(QaRules.CodeNestedUnityObject, t.DeclStart, "nested " + (c.Index.IsComponent(t.Name) ? "MonoBehaviour" : "ScriptableObject") + " " + t.Parent.Name + "." + t.Name);

                XmlSummary(c, t);
                if (t.Kind == CsTypeKind.Class || t.Kind == CsTypeKind.Struct)
                {
                    PublicFields(c, t);
                    UnusedFields(c, t);
                    Coroutines(c, t);
                    Events(c, t);
                    Subscriptions(c, t);
                }
            }
            string baseName = c.FileName;
            int dot = baseName.IndexOf('.');
            if (dot > 0) baseName = baseName.Substring(0, dot);
            if (unityTop.Count > 1)
            {
                var names = new List<string>();
                foreach (var t in unityTop) names.Add(t.Name);
                c.Add(QaRules.CodeMultipleUnityObjects, unityTop[1].DeclStart, unityTop.Count + " MonoBehaviour/ScriptableObject classes in one file: " + string.Join(", ", names.ToArray()));
            }
            foreach (var t in unityTop)
                if (t.Name != baseName)
                    c.Add(QaRules.CodeFileNameMismatch, t.DeclStart, "class " + t.Name + " is declared in " + c.FileName + ".cs");
        }

        static void XmlSummary(CodeLintContext c, CsType t)
        {
            if (c.Scope == CodeScope.Tests || !t.IsEffectivelyPublic || !Shipping(c, t.DeclStart) && c.IsRuntime) return;
            if (t.Doc == null)
            {
                c.Add(QaRules.CodeMissingXmlSummary, t.DeclStart, "public " + t.Kind.ToString().ToLowerInvariant() + " " + t.Name + " has no /// <summary>");
                return;
            }
            string text = t.Doc;
            var sm = Regex.Match(text, @"<summary>(.*?)</summary>", RegexOptions.Singleline);
            if (sm.Success) text = sm.Groups[1].Value;
            if (Regex.IsMatch(t.Doc, @"<inheritdoc")) return;
            text = Regex.Replace(text, @"<[^>]+>", " ");
            var words = Regex.Matches(text, @"[\p{L}\p{N}_']+");
            if (words.Count == 0 || (words.Count <= 5 && text.IndexOf(t.Name, StringComparison.OrdinalIgnoreCase) >= 0))
                c.Add(QaRules.CodeWeakXmlSummary, t.DeclStart, "summary of " + t.Name + " only restates the name: \"" + Shorten(text.Trim(), 50) + "\"");
        }

        static void PublicFields(CodeLintContext c, CsType t)
        {
            if (!c.IsRuntime || !c.Index.IsComponent(t.Name)) return;
            var names = new List<string>();
            int first = -1;
            foreach (var f in t.Fields)
            {
                if (!f.Has("public") || f.Has("static") || f.Has("const") || f.Has("readonly") || f.IsEvent || !Shipping(c, f.DeclStart)) continue;
                names.Add(f.Name);
                if (first < 0) first = f.DeclStart;
            }
            if (names.Count == 0) return;
            c.Add(QaRules.CodePublicFieldMb, first, names.Count + " public field(s) on " + t.Name + ": " + JoinLimited(names, 6));
        }

        static void UnusedFields(CodeLintContext c, CsType t)
        {
            if (c.Scope == CodeScope.Tests || t.Has("partial")) return;
            foreach (var f in t.Fields)
            {
                if (!f.IsPrivate || f.IsEvent) continue;
                int refs = CountWord(c.Masked, f.Name) - 1;
                if (refs > 0) continue;
                bool named = false;
                foreach (var lit in c.Src.Literals) if (lit.Value == f.Name) { named = true; break; }
                if (named) continue;
                bool serialized = f.HasAttribute("SerializeField") || f.HasAttribute("SerializeReference");
                if (!serialized)
                    c.Add(QaRules.CodeUnusedPrivateField, f.NameOffset, "private field '" + f.Name + "' in " + t.Name + " is never used");
                else if (c.IsRuntime)
                    c.Add(QaRules.CodeSerializedUnused, f.NameOffset, "serialized field '" + f.Name + "' in " + t.Name + " is never read");
            }
        }

        static readonly Regex StartCoRx = new Regex(@"\bStartCoroutine\s*\(", RX);
        static readonly Regex StopCoRx = new Regex(@"\b(?:StopCoroutine|StopAllCoroutines)\s*\(", RX);

        static void Coroutines(CodeLintContext c, CsType t)
        {
            if (!c.Index.IsComponent(t.Name)) return;
            string body = c.Masked.Substring(t.BodyStart, t.BodyEnd - t.BodyStart);
            var start = StartCoRx.Match(body);
            if (!start.Success || StopCoRx.IsMatch(body)) return;
            c.Add(QaRules.CodeCoroutineNoStop, t.BodyStart + start.Index, t.Name + " starts coroutines but never stops them");
        }

        static readonly Regex SubRx = new Regex(@"(?<![\w.\]\)])((?:this\s*\.\s*)?[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*)\s*(\+=|-=)", RX);

        sealed class Sub
        {
            public string Lhs, Handler, Method;
            public bool Lambda, Add;
            public int Offset;
        }

        static void Events(CodeLintContext c, CsType t)
        {
            var subs = EventSubscriptions(c, t);
            bool component = c.Index.IsComponent(t.Name);
            foreach (var s in subs)
            {
                if (!s.Add || !Shipping(c, s.Offset) || s.Lhs.IndexOf('.') < 0) continue;    // no dot: the type's own event
                string first = s.Lhs.Split('.')[0];
                bool member = IsMember(t, first);
                if (!member && CodeLinter.TypeOf(c, first, s.Offset) != null) continue;     // a local object's event
                bool isStatic = !member && char.IsUpper(first[0]);
                if (s.Lambda)
                {
                    if (isStatic) c.Add(QaRules.CodeEventNoUnsubscribe, s.Offset, "lambda subscribed to static event " + s.Lhs + " in " + s.Method + " can never be removed");
                    continue;
                }
                var removers = new List<string>();
                foreach (var r in subs) if (!r.Add && r.Lhs == s.Lhs && r.Handler == s.Handler) removers.Add(r.Method);
                if (removers.Count == 0) Unpaired(c, s, isStatic, component);
                else if (component) Mispaired(c, s, removers);
            }
        }

        /// <summary>Every '+=' / '-=' of a method group or lambda inside the type's methods.</summary>
        static List<Sub> EventSubscriptions(CodeLintContext c, CsType t)
        {
            var methodNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in t.Methods) methodNames.Add(m.Name);
            var subs = new List<Sub>();
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                string body = c.Masked.Substring(m.BodyStart, m.BodyEnd - m.BodyStart);
                foreach (Match x in SubRx.Matches(body))
                {
                    string rhs = TakeRhs(body, x.Index + x.Length).Trim();
                    var s = new Sub
                    {
                        Lhs = Regex.Replace(x.Groups[1].Value, @"\s+", "").Replace("this.", ""), Method = m.Name, Add = x.Groups[2].Value == "+=", Offset = m.BodyStart + x.Index,
                        Lambda = rhs.IndexOf("=>", StringComparison.Ordinal) >= 0 || rhs.StartsWith("delegate", StringComparison.Ordinal)
                    };
                    if (!s.Lambda)
                    {
                        var mg = MethodGroupRx.Match(rhs);
                        if (!mg.Success || !methodNames.Contains(mg.Groups[1].Value)) continue;   // numeric '+=' etc.
                        s.Handler = mg.Groups[1].Value;
                    }
                    subs.Add(s);
                }
            }
            return subs;
        }

        static readonly Regex MethodGroupRx = new Regex(@"^(?:new\s+[\w.<>, ]+\s*\(\s*)?(?:this\s*\.\s*)?(?:[A-Za-z_]\w*\s*\.\s*)*([A-Za-z_]\w*)\s*\)?$", RX);

        static bool IsMember(CsType t, string name)
        {
            foreach (var f in t.Fields) if (f.Name == name) return true;
            foreach (var p in t.Properties) if (p.Name == name) return true;
            return false;
        }

        static void Unpaired(CodeLintContext c, Sub s, bool isStatic, bool component)
        {
            if (isStatic)
                c.Add(QaRules.CodeEventNoUnsubscribe, s.Offset, s.Lhs + " += " + s.Handler + " in " + s.Method + " has no matching -=");
            else if (component && (s.Method == "OnEnable" || s.Method == "Awake" || s.Method == "Start"))
                c.Add(QaRules.CodeEventNoUnsubscribe, s.Offset, s.Lhs + " += " + s.Handler + " in " + s.Method + " has no matching -=", QaSeverity.Minor, null);
        }

        static void Mispaired(CodeLintContext c, Sub s, List<string> removers)
        {
            if (s.Method == "OnEnable" && !removers.Contains("OnDisable"))
                c.Add(QaRules.CodeEventNoUnsubscribe, s.Offset, s.Lhs + " subscribed in OnEnable but removed in " + removers[0] + " (handlers stack up after disable/enable)");
            else if ((s.Method == "Awake" || s.Method == "Start") && removers.Contains("OnDisable") && !removers.Contains("OnDestroy"))
                c.Add(QaRules.CodeEventNoUnsubscribe, s.Offset, s.Lhs + " subscribed in " + s.Method + " but removed in OnDisable (lost after re-enable)");
        }

        static string TakeRhs(string body, int from)
        {
            int depth = 0, i = from;
            for (; i < body.Length; i++)
            {
                char ch = body[i];
                if (ch == '(' || ch == '[' || ch == '{') depth++;
                else if (ch == ')' || ch == ']' || ch == '}') { if (depth == 0) break; depth--; }
                else if (ch == ';' && depth == 0) break;
            }
            return body.Substring(from, i - from);
        }

        static readonly Regex BusSubscribeRx = new Regex(@"\bEventBus\s*\.\s*Subscribe\b", RX);
        static readonly Regex SubscriptionsNewRx = new Regex(@"\bnew\s+Subscriptions\s*\(", RX);
        static readonly Regex DisposeRx = new Regex(@"\.\s*Dispose\s*\(", RX);

        static readonly Regex UsingPrefixRx = new Regex(@"\busing\s*\(\s*(?:var\s+\w+\s*=\s*)?$", RX);

        static void Subscriptions(CodeLintContext c, CsType t)
        {
            string s = c.Masked;
            int needsDispose = -1;
            var hits = new List<int>();
            foreach (Match x in BusSubscribeRx.Matches(s, t.BodyStart)) { if (x.Index >= t.BodyEnd) break; hits.Add(x.Index); }
            foreach (Match x in SubscriptionsNewRx.Matches(s, t.BodyStart)) { if (x.Index >= t.BodyEnd) break; hits.Add(x.Index); }
            hits.Sort();
            foreach (int at in hits)
            {
                if (c.File.TypeAt(at) != t || !Shipping(c, at)) continue;
                string prefix = StatementPrefix(s, at);
                if (UsingPrefixRx.IsMatch(prefix)) continue;
                if (prefix.Trim().Length == 0 && s.Substring(at, 8) == "EventBus")
                {
                    c.Add(QaRules.CodeSubscriptionNotDisposed, at, "EventBus.Subscribe result discarded in " + t.Name);
                    continue;
                }
                if (needsDispose < 0) needsDispose = at;
            }
            if (needsDispose < 0) return;
            if (!DisposeRx.IsMatch(s.Substring(t.BodyStart, t.BodyEnd - t.BodyStart)))
                c.Add(QaRules.CodeSubscriptionNotDisposed, needsDispose, t.Name + " subscribes to the EventBus but never calls Dispose()");
        }

        // ------------------------------------------------------------------------------------------------ method-level rules

        static readonly Regex DecisionRx = new Regex(@"\b(?:if|case|for|foreach|while|catch)\b|&&|\|\||\?\?|\s\?\s", RX);

        static void MethodRules(CodeLintContext c)
        {
            foreach (var t in c.File.Types)
            {
                foreach (var m in t.Methods)
                {
                    if (m.Has("async") && m.ReturnType == "void")
                        c.Add(QaRules.CodeAsyncVoid, m.DeclStart, "async void " + t.Name + "." + m.Name);
                    if (!m.HasBody || c.Scope == CodeScope.Tests) continue;
                    bool strict = c.IsRuntime && Shipping(c, m.DeclStart);
                    var sev = strict ? QaSeverity.Major : QaSeverity.Minor;
                    int lines = m.EndLine - m.Line + 1;
                    if (!m.ExpressionBodied && lines > c.Options.MaxMethodLines)
                        c.Add(QaRules.CodeMethodTooLong, m.DeclStart, t.Name + "." + m.Name + " is " + lines + " lines (limit " + c.Options.MaxMethodLines + ")", sev, null);
                    string body = c.Masked.Substring(m.BodyStart, m.BodyEnd - m.BodyStart);
                    int cc = 1 + DecisionRx.Matches(body).Count;
                    if (cc > c.Options.MaxComplexity)
                        c.Add(QaRules.CodeComplexity, m.DeclStart, t.Name + "." + m.Name + " has cyclomatic complexity " + cc + " (limit " + c.Options.MaxComplexity + ")", sev, null);
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ comments

        static readonly Regex TodoRx = new Regex(@"\b(TODO|FIXME|HACK|XXX)\b", RX);
        static readonly Regex CodeStartRx = new Regex(@"^(?:if|else|for|foreach|while|return|var|using|public|private|protected|internal|static|new|case|break|continue|switch|try|catch|throw|yield)(?:\s|\(|;|$)", RX);
        static readonly Regex StatementRx = new Regex(@"^(?:[\w.\[\]]+\s*[+\-*/]?=\s*[^=\s].*|[\w.]+(?:<[^<>]*>)?\(.*\)|(?:var|int|float|bool|string|[A-Z]\w*(?:<[^<>]*>)?(?:\[\])?)\s+[a-z_]\w*\s*(?:=.*)?)\s*;$", RX);
        static readonly Regex CallLineRx = new Regex(@"^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+(?:<[^<>]*>)?\(.*\)$", RX);
        static readonly Regex ProseRx = new Regex(@"e\.g\.|i\.e\.|\b(?:example|Example|usage|Usage)\b", RX);

        /// <summary>Heuristic: does this comment line look like C# code rather than prose?</summary>
        public static bool IsCodeLike(string line)
        {
            string t = line.Trim();
            if (t == "{" || t == "}" || t == "};" || t == "});") return true;
            if (t.Length < 3 || ProseRx.IsMatch(t)) return false;
            bool endsCode = t.EndsWith(";", StringComparison.Ordinal) || t.EndsWith("{", StringComparison.Ordinal) || t.EndsWith(")", StringComparison.Ordinal);
            if (CodeStartRx.IsMatch(t) && endsCode) return true;
            return StatementRx.IsMatch(t) || CallLineRx.IsMatch(t);
        }

        static void Comments(CodeLintContext c)
        {
            foreach (var cm in c.Src.Comments)
            {
                var m = TodoRx.Match(cm.Text);
                if (!m.Success) continue;
                var sev = m.Value == "TODO" ? QaSeverity.Minor : QaSeverity.Major;
                c.Add(QaRules.CodeTodo, cm.Start, m.Value + ": " + Shorten(cm.Text.Trim(), 90), sev, null);
            }
            // commented-out code: groups of consecutive whole-line // comments, and block comments
            var group = new List<SourceComment>();
            for (int i = 0; i <= c.Src.Comments.Count; i++)
            {
                SourceComment cm = i < c.Src.Comments.Count ? c.Src.Comments[i] : null;
                bool whole = cm != null && !cm.Doc && !cm.Block && c.Src.LineText(cm.Line).Trim().StartsWith("//", StringComparison.Ordinal);
                if (whole && (group.Count == 0 || group[group.Count - 1].Line == cm.Line - 1)) { group.Add(cm); continue; }
                FlushCommentGroup(c, group);
                group.Clear();
                if (whole) group.Add(cm);
                else if (cm != null && cm.Block && !cm.Doc)
                {
                    var lines = new List<string>(cm.Text.Split('\n'));
                    EvaluateCommentLines(c, cm.Start, lines);
                }
            }
        }

        static void FlushCommentGroup(CodeLintContext c, List<SourceComment> group)
        {
            if (group.Count == 0) return;
            var lines = new List<string>();
            foreach (var g in group) lines.Add(g.Text);
            EvaluateCommentLines(c, group[0].Start, lines);
        }

        static void EvaluateCommentLines(CodeLintContext c, int offset, List<string> lines)
        {
            int code = 0, total = 0;
            foreach (var l in lines)
            {
                string t = l.Trim().TrimStart('*').Trim();
                if (t.Length == 0) continue;
                total++;
                if (IsCodeLike(t)) code++;
            }
            if (code == 0 || code * 2 < total) return;
            var sev = code >= 3 ? QaSeverity.Major : QaSeverity.Minor;
            c.Add(QaRules.CodeCommentedCode, offset, code + " line(s) of commented-out code", sev, null);
        }

        // ------------------------------------------------------------------------------------------------ namespace

        static void Namespace(CodeLintContext c)
        {
            if (c.File.Types.Count == 0) return;
            bool legacyOk;
            var allowed = AllowedNamespaces(c.Options.Relative(c.Path), out legacyOk);
            if (c.File.Namespaces.Count == 0)
            {
                c.Add(QaRules.CodeNamespaceFolder, c.File.Types[0].DeclStart, "types are declared in the global namespace", QaSeverity.Major, null);
                return;
            }
            foreach (var ns in c.File.Namespaces)
            {
                bool ok = false;
                foreach (var a in allowed) if (ns.Key == a || ns.Key.StartsWith(a + ".", StringComparison.Ordinal)) ok = true;
                if (ok) continue;
                int off = c.Src.LineStart(ns.Value);
                if (legacyOk && ns.Key == "GeorgianKnight")
                    c.Add(QaRules.CodeNamespaceFolder, off, "legacy namespace GeorgianKnight (expected " + allowed[0] + ")", QaSeverity.Info, null);
                else
                    c.Add(QaRules.CodeNamespaceFolder, off, "namespace " + ns.Key + " does not match folder (expected " + string.Join(" or ", allowed.ToArray()) + ")");
            }
        }

        /// <summary>Namespaces a file below the scripts root may use (ARCHITECTURE §2); legacy "GeorgianKnight" is tolerated in Gameplay.</summary>
        public static List<string> AllowedNamespaces(string relativePath, out bool legacyOk)
        {
            var parts = relativePath.Split('/');
            var allowed = new List<string>();
            legacyOk = false;
            bool runtime = parts.Length >= 2 && parts[0] == "Runtime";
            if (runtime && parts[1] == "Core") allowed.Add("GeorgianKnight.Core");
            else if (runtime && parts[1] == "Gameplay")
            {
                legacyOk = true;
                if (parts.Length >= 4) { allowed.Add("GeorgianKnight." + parts[2]); allowed.Add("GeorgianKnight.Gameplay." + parts[2]); }
                else allowed.Add("GeorgianKnight.Gameplay");
            }
            else if (runtime && parts[1] == "UI") allowed.Add("GeorgianKnight.UI");
            else if (parts[0] == "Editor") allowed.Add("GeorgianKnight.EditorTools");
            else if (parts[0] == "Tests") allowed.Add("GeorgianKnight.Tests");
            else allowed.Add("GeorgianKnight");
            return allowed;
        }
    }
}
