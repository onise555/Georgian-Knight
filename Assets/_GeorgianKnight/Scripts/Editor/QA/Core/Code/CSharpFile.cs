using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>Kind of a declared type.</summary>
    public enum CsTypeKind { Class, Struct, Interface, Enum, Delegate }

    /// <summary>Common data of declared types and members: name, modifiers, attributes, location, doc comment.</summary>
    public abstract class CsDecl
    {
        public string Name = "";
        public readonly List<string> Modifiers = new List<string>();
        public readonly List<string> Attributes = new List<string>();
        public int Line;
        public int DeclStart;          // offset of the first attribute/modifier/token
        public string Doc;             // text of the preceding /// comment block, or null

        public bool Has(string modifier) { return Modifiers.Contains(modifier); }

        public bool HasAttribute(string name)
        {
            foreach (var a in Attributes)
                if (a == name || a == name + "Attribute" || a.EndsWith("." + name, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Declared without public/protected/internal.</summary>
        public bool IsPrivate { get { return !Has("public") && !Has("protected") && !Has("internal"); } }
    }

    /// <summary>A class/struct/interface/enum/delegate declaration with its members.</summary>
    public sealed class CsType : CsDecl
    {
        public CsTypeKind Kind;
        public string Namespace = "";
        public CsType Parent;
        public bool IsGeneric;
        public int BodyStart = -1, BodyEnd = -1;   // offsets of '{' and '}'
        public readonly List<string> Bases = new List<string>();
        public readonly List<CsMethod> Methods = new List<CsMethod>();
        public readonly List<CsField> Fields = new List<CsField>();
        public readonly List<CsProperty> Properties = new List<CsProperty>();
        public readonly List<CsType> Nested = new List<CsType>();

        public bool IsEffectivelyPublic
        {
            get
            {
                for (var t = this; t != null; t = t.Parent) if (!t.Has("public")) return false;
                return true;
            }
        }

        public bool Contains(int offset) { return offset > BodyStart && offset < BodyEnd; }
    }

    /// <summary>A method, constructor, operator or local function-free member with a body (or an abstract signature).</summary>
    public sealed class CsMethod : CsDecl
    {
        public CsType Owner;
        public string ReturnType = "";
        public bool IsConstructor, ExpressionBodied, HasBody;
        public int BodyStart = -1, BodyEnd = -1;   // '{'..'}' or '=>'..';'
        public int EndLine;
        public readonly List<KeyValuePair<string, string>> Parameters = new List<KeyValuePair<string, string>>(); // type, name
    }

    /// <summary>One field declarator (<c>int a, b;</c> yields two).</summary>
    public sealed class CsField : CsDecl
    {
        public CsType Owner;
        public string Type = "";
        public bool IsEvent;
        public int NameOffset;
    }

    /// <summary>A property, indexer or event with accessors.</summary>
    public sealed class CsProperty : CsDecl
    {
        public CsType Owner;
        public string Type = "";
        public int BodyStart = -1, BodyEnd = -1;
    }

    /// <summary>
    /// Lightweight structural parse of one C# file (no semantic model): namespaces, types, members and body ranges
    /// found by brace matching on <see cref="CSharpSource.Masked"/>. Good enough for lint rules; never used to compile.
    /// </summary>
    public sealed class CSharpFile
    {
        public readonly CSharpSource Src;
        public readonly List<CsType> Types = new List<CsType>();          // all, outer before nested
        public readonly List<string> Usings = new List<string>();
        public readonly List<KeyValuePair<string, int>> Namespaces = new List<KeyValuePair<string, int>>(); // name, line
        public readonly List<KeyValuePair<int, int>> AttributeSpans = new List<KeyValuePair<int, int>>();
        int[] match;

        static readonly HashSet<string> ModifierWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "public", "private", "protected", "internal", "static", "readonly", "const", "volatile", "new", "event", "unsafe",
            "fixed", "extern", "abstract", "sealed", "partial", "virtual", "override", "async", "ref", "in", "out", "params", "this"
        };

        static readonly Regex TypeDecl = new Regex(@"^(?:(?:public|private|protected|internal|static|abstract|sealed|partial|new|unsafe|readonly|ref)\s+)*(class|struct|interface|enum)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
        static readonly Regex DelegateDecl = new Regex(@"\bdelegate\b[^(]*?\b([A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*\($", RegexOptions.Compiled);

        public CSharpFile(CSharpSource src)
        {
            Src = src;
            BuildMatch();
            ScanBlock(0, src.Masked.Length, "", null);
        }

        public int Match(int offset) { return offset >= 0 && offset < match.Length ? match[offset] : -1; }

        /// <summary>True when the offset lies inside an attribute section ([...] before a declaration).</summary>
        public bool InAttribute(int offset)
        {
            foreach (var s in AttributeSpans) if (offset >= s.Key && offset < s.Value) return true;
            return false;
        }

        /// <summary>The innermost type whose body contains the offset.</summary>
        public CsType TypeAt(int offset)
        {
            CsType best = null;
            foreach (var t in Types)
                if (t.Contains(offset) && (best == null || t.BodyStart > best.BodyStart)) best = t;
            return best;
        }

        /// <summary>The method whose body contains the offset.</summary>
        public CsMethod MethodAt(int offset)
        {
            CsMethod best = null;
            foreach (var t in Types)
                foreach (var m in t.Methods)
                    if (m.HasBody && offset >= m.BodyStart && offset <= m.BodyEnd && (best == null || m.BodyStart > best.BodyStart)) best = m;
            return best;
        }

        // ------------------------------------------------------------------------------------------------ scanning

        void BuildMatch()
        {
            string m = Src.Masked;
            match = new int[m.Length];
            for (int i = 0; i < match.Length; i++) match[i] = -1;
            var stack = new List<int>();
            for (int i = 0; i < m.Length; i++)
            {
                char c = m[i];
                if (c == '(' || c == '[' || c == '{') stack.Add(i);
                else if (c == ')' || c == ']' || c == '}')
                {
                    char open = c == ')' ? '(' : c == ']' ? '[' : '{';
                    // pop to the nearest matching opener (tolerates stray brackets)
                    for (int k = stack.Count - 1; k >= 0; k--)
                    {
                        if (m[stack[k]] != open) continue;
                        match[stack[k]] = i;
                        match[i] = stack[k];
                        stack.RemoveRange(k, stack.Count - k);
                        break;
                    }
                }
            }
        }

        int SkipWs(int pos, int end)
        {
            string m = Src.Masked;
            while (pos < end && char.IsWhiteSpace(m[pos])) pos++;
            return pos;
        }

        /// <summary>Offset of the ';' ending the statement that starts at <paramref name="pos"/> (brackets skipped).</summary>
        int FindSemicolon(int pos, int end)
        {
            string m = Src.Masked;
            while (pos < end)
            {
                char c = m[pos];
                if ((c == '(' || c == '[' || c == '{') && match[pos] > pos) { pos = match[pos] + 1; continue; }
                if (c == ';') return pos;
                if (c == '}') return pos;   // malformed: stop at the enclosing block end
                pos++;
            }
            return end;
        }

        /// <summary>True when the header has an assignment '=' outside brackets (field initializer).</summary>
        bool HasTopLevelAssign(int from, int to)
        {
            string m = Src.Masked;
            for (int i = from; i < to; i++)
            {
                char c = m[i];
                if ((c == '(' || c == '[') && match[i] > i) { i = match[i]; continue; }
                if (c != '=') continue;
                char prev = i > 0 ? m[i - 1] : ' ';
                char next = i + 1 < m.Length ? m[i + 1] : ' ';
                if (next == '=' || next == '>' || prev == '=' || prev == '!' || prev == '<' || prev == '>') continue;
                return true;
            }
            return false;
        }

        void ScanBlock(int start, int end, string ns, CsType owner)
        {
            string m = Src.Masked;
            int pos = start, seg = start;
            while (pos < end)
            {
                char c = m[pos];
                if ((c == '(' || c == '[') && match[pos] > pos) { pos = match[pos] + 1; continue; }
                if (c == '{')
                {
                    int close = match[pos] > pos ? match[pos] : end;
                    if (owner != null && HasTopLevelAssign(seg, pos)) { pos = close + 1; continue; }   // initializer braces
                    HandleBody(seg, pos, close, ns, owner);
                    pos = seg = AfterBody(close + 1, end);
                    continue;
                }
                if (c == ';')
                {
                    HandleStatement(seg, pos, ns, owner);
                    pos = seg = pos + 1;
                    continue;
                }
                if (c == '=' && owner != null && pos + 1 < end && m[pos + 1] == '>' && !HasTopLevelAssign(seg, pos))
                {
                    int semi = FindSemicolon(pos + 2, end);
                    HandleExpressionBodied(seg, pos, semi, owner);
                    pos = seg = semi + 1;
                    continue;
                }
                pos++;
                if (c == '}') seg = pos;   // stray closer
            }
        }

        /// <summary>Position after a member body, skipping a property initializer "= value;" or a trailing ';'.</summary>
        int AfterBody(int pos, int end)
        {
            string m = Src.Masked;
            int q = SkipWs(pos, end);
            if (q < end && m[q] == '=' && !(q + 1 < end && m[q + 1] == '>')) return FindSemicolon(q, end) + 1;
            if (q < end && m[q] == ';') return q + 1;
            return pos;
        }

        /// <summary>Skips attribute sections, records their names; returns the offset of the first real token.</summary>
        int StripAttributes(int pos, int end, List<string> names)
        {
            string m = Src.Masked;
            pos = SkipWs(pos, end);
            while (pos < end && m[pos] == '[' && match[pos] > pos && match[pos] < end)
            {
                int close = match[pos];
                AttributeSpans.Add(new KeyValuePair<int, int>(pos, close + 1));
                string inner = m.Substring(pos + 1, close - pos - 1);
                int colon = inner.IndexOf(':');
                if (colon >= 0 && Regex.IsMatch(inner.Substring(0, colon), @"^\s*(field|return|assembly|module|type|method|param|property)\s*$"))
                    inner = inner.Substring(colon + 1);
                foreach (var part in SplitTopLevel(inner, ','))
                {
                    var mm = Regex.Match(part, @"^\s*([A-Za-z_][\w.]*)");
                    if (mm.Success) names.Add(mm.Groups[1].Value);
                }
                pos = SkipWs(close + 1, end);
            }
            return pos;
        }

        string DocBefore(int segStart, int declStart)
        {
            string doc = null;
            foreach (var c in Src.Comments)
            {
                if (!c.Doc || c.Start < segStart || c.Start >= declStart) continue;
                doc = doc == null ? c.Text : doc + "\n" + c.Text;
            }
            return doc;
        }

        void Fill(CsDecl d, int seg, int declStart, int tokenStart, List<string> attrs)
        {
            d.DeclStart = declStart;
            d.Line = Src.LineOf(tokenStart);
            d.Attributes.AddRange(attrs);
            d.Doc = DocBefore(seg, declStart);
        }

        void HandleBody(int seg, int open, int close, string ns, CsType owner)
        {
            string m = Src.Masked;
            int declStart = SkipWs(seg, open);
            var attrs = new List<string>();
            int p = StripAttributes(seg, open, attrs);
            string text = m.Substring(p, open - p).Trim();
            if (text.Length == 0) return;
            if (text.StartsWith("namespace", StringComparison.Ordinal) && (text.Length == 9 || char.IsWhiteSpace(text[9])))
            {
                string name = text.Substring(9).Trim();
                Namespaces.Add(new KeyValuePair<string, int>(name, Src.LineOf(p)));
                ScanBlock(open + 1, close, ns.Length > 0 ? ns + "." + name : name, null);
                return;
            }
            var td = TypeDecl.Match(text);
            if (td.Success)
            {
                var t = MakeType(td, text, ns, owner, open, close);
                Fill(t, seg, declStart, p, attrs);
                Types.Add(t);
                if (owner != null) owner.Nested.Add(t);
                if (t.Kind != CsTypeKind.Enum) ScanBlock(open + 1, close, ns, t);
                return;
            }
            if (owner == null) return;
            int paren = TopLevelIndexOf(text, '(');
            if (paren >= 0)
            {
                var md = MakeMethod(text, paren, p, owner);
                Fill(md, seg, declStart, p, attrs);
                md.HasBody = true;
                md.BodyStart = open;
                md.BodyEnd = close;
                md.EndLine = Src.LineOf(close);
                owner.Methods.Add(md);
                return;
            }
            var prop = new CsProperty { Owner = owner, BodyStart = open, BodyEnd = close };
            Fill(prop, seg, declStart, p, attrs);
            SplitMemberHead(text, prop.Modifiers, out prop.Type, out prop.Name);
            owner.Properties.Add(prop);
        }

        static CsType MakeType(Match td, string text, string ns, CsType owner, int open, int close)
        {
            string kind = td.Groups[1].Value;
            var t = new CsType
            {
                Kind = kind == "class" ? CsTypeKind.Class : kind == "struct" ? CsTypeKind.Struct : kind == "interface" ? CsTypeKind.Interface : CsTypeKind.Enum,
                Name = td.Groups[2].Value, Namespace = ns, Parent = owner, BodyStart = open, BodyEnd = close
            };
            foreach (var w in Tokens(text.Substring(0, td.Groups[1].Index))) t.Modifiers.Add(w);
            string after = text.Substring(td.Index + td.Length);
            int a = 0;
            while (a < after.Length && char.IsWhiteSpace(after[a])) a++;
            if (a < after.Length && after[a] == '<') { t.IsGeneric = true; a = SkipAngle(after, a); }
            ParseBases(after.Substring(Math.Min(a, after.Length)).Trim(), t.Bases);
            return t;
        }

        /// <summary>": Base, IFoo&lt;T&gt; where T : …" → simple base names.</summary>
        static void ParseBases(string rest, List<string> bases)
        {
            if (!rest.StartsWith(":", StringComparison.Ordinal)) return;
            rest = rest.Substring(1);
            int where = Regex.Match(rest, @"\bwhere\b").Index;
            if (where > 0) rest = rest.Substring(0, where);
            foreach (var b in SplitTopLevel(rest, ','))
            {
                string bn = SimpleTypeName(b);
                if (bn.Length > 0) bases.Add(bn);
            }
        }

        void HandleStatement(int seg, int semi, string ns, CsType owner)
        {
            string m = Src.Masked;
            int declStart = SkipWs(seg, semi);
            var attrs = new List<string>();
            int p = StripAttributes(seg, semi, attrs);
            string text = m.Substring(p, semi - p).Trim();
            if (text.Length == 0) return;
            if (owner == null)
            {
                if (text.StartsWith("using", StringComparison.Ordinal) && text.Length > 5 && char.IsWhiteSpace(text[5]))
                {
                    string u = text.Substring(5).Trim();
                    int eq = u.IndexOf('=');
                    Usings.Add(eq >= 0 ? u.Substring(eq + 1).Trim() : (u.StartsWith("static ", StringComparison.Ordinal) ? u.Substring(7).Trim() : u));
                    return;
                }
            }
            if (Regex.IsMatch(text, @"\bdelegate\b") && text.EndsWith(")", StringComparison.Ordinal))
            {
                int paren = TopLevelIndexOf(text, '(');
                var dm = DelegateDecl.Match(text.Substring(0, paren + 1));
                if (dm.Success)
                {
                    var d = new CsType { Kind = CsTypeKind.Delegate, Name = dm.Groups[1].Value, Namespace = ns, Parent = owner };
                    Fill(d, seg, declStart, p, attrs);
                    foreach (var w in Tokens(text.Substring(0, text.IndexOf("delegate", StringComparison.Ordinal)))) d.Modifiers.Add(w);
                    Types.Add(d);
                    if (owner != null) owner.Nested.Add(d);
                    return;
                }
            }
            if (owner == null || owner.Kind == CsTypeKind.Enum) return;
            int eqi = TopLevelAssignIndex(text);
            int par = TopLevelIndexOf(text, '(');
            if (par >= 0 && (eqi < 0 || par < eqi))
            {
                var md = MakeMethod(text, par, p, owner);
                Fill(md, seg, declStart, p, attrs);
                owner.Methods.Add(md);
                return;
            }
            AddFields(text, p, seg, declStart, attrs, owner);
        }

        void HandleExpressionBodied(int seg, int arrow, int semi, CsType owner)
        {
            string m = Src.Masked;
            int declStart = SkipWs(seg, arrow);
            var attrs = new List<string>();
            int p = StripAttributes(seg, arrow, attrs);
            string text = m.Substring(p, arrow - p).Trim();
            if (text.Length == 0) return;
            int paren = TopLevelIndexOf(text, '(');
            if (paren >= 0)
            {
                var md = MakeMethod(text, paren, p, owner);
                Fill(md, seg, declStart, p, attrs);
                md.HasBody = true;
                md.ExpressionBodied = true;
                md.BodyStart = arrow;
                md.BodyEnd = semi;
                md.EndLine = Src.LineOf(semi);
                owner.Methods.Add(md);
                return;
            }
            var prop = new CsProperty { Owner = owner, BodyStart = arrow, BodyEnd = semi };
            Fill(prop, seg, declStart, p, attrs);
            SplitMemberHead(text, prop.Modifiers, out prop.Type, out prop.Name);
            owner.Properties.Add(prop);
        }

        CsMethod MakeMethod(string text, int paren, int textOffset, CsType owner)
        {
            var md = new CsMethod { Owner = owner };
            string head = text.Substring(0, paren).Trim();
            var toks = Tokens(head);
            int i = 0;
            while (i < toks.Count - 1 && ModifierWords.Contains(toks[i]) && toks[i] != "this") md.Modifiers.Add(toks[i++]);
            int op = toks.IndexOf("operator");
            if (op >= 0)
            {
                md.Name = "operator" + (op + 1 < toks.Count ? toks[op + 1] : "");
                md.ReturnType = string.Join(" ", toks.GetRange(i, Math.Max(0, op - i)).ToArray());
            }
            else
            {
                string name = toks.Count > 0 ? toks[toks.Count - 1] : "";
                int lt = name.IndexOf('<');
                if (lt > 0) name = name.Substring(0, lt);
                int dot = name.LastIndexOf('.');
                if (dot >= 0) name = name.Substring(dot + 1);
                md.Name = name;
                md.ReturnType = string.Join(" ", toks.GetRange(i, Math.Max(0, toks.Count - 1 - i)).ToArray());
                md.IsConstructor = md.ReturnType.Length == 0 && (name == owner.Name || name.StartsWith("~", StringComparison.Ordinal));
            }
            int openAbs = textOffset + paren;   // text starts exactly at textOffset (leading whitespace already skipped)
            int close = Match(openAbs);
            if (close > openAbs)
            {
                string pars = Src.Masked.Substring(openAbs + 1, close - openAbs - 1);
                foreach (var part in SplitTopLevel(pars, ','))
                {
                    string ptext = part;
                    int eq = TopLevelAssignIndex(ptext);
                    if (eq >= 0) ptext = ptext.Substring(0, eq);
                    var pt = Tokens(StripLeadingAttributes(ptext));
                    if (pt.Count < 2) continue;
                    int k = 0;
                    while (k < pt.Count - 2 && ModifierWords.Contains(pt[k])) k++;
                    md.Parameters.Add(new KeyValuePair<string, string>(string.Join(" ", pt.GetRange(k, pt.Count - 1 - k).ToArray()), pt[pt.Count - 1]));
                }
            }
            return md;
        }

        void AddFields(string text, int p, int seg, int declStart, List<string> attrs, CsType owner)
        {
            // declarators are split on top-level commas of the whole statement: "float x = 1f, y;" yields x and y
            var chunks = SplitTopLevel(text, ',');
            if (chunks.Count == 0) return;
            int eq0 = TopLevelAssignIndex(chunks[0]);
            var first = Tokens(eq0 >= 0 ? chunks[0].Substring(0, eq0) : chunks[0]);
            if (first.Count < 2) return;
            var mods = new List<string>();
            int i = 0;
            while (i < first.Count - 1 && ModifierWords.Contains(first[i])) mods.Add(first[i++]);
            string type = string.Join(" ", first.GetRange(i, Math.Max(0, first.Count - 1 - i)).ToArray());
            if (type.Length == 0) return;
            var names = new List<string> { first[first.Count - 1] };
            for (int k = 1; k < chunks.Count; k++)
            {
                int eq = TopLevelAssignIndex(chunks[k]);
                var t = Tokens(eq >= 0 ? chunks[k].Substring(0, eq) : chunks[k]);
                if (t.Count == 1) names.Add(t[0]);
            }
            int searchFrom = p;
            foreach (var name in names)
            {
                if (!Regex.IsMatch(name, @"^[A-Za-z_]\w*$")) continue;
                var f = new CsField { Owner = owner, Name = name, Type = type, IsEvent = mods.Contains("event") };
                f.Modifiers.AddRange(mods);
                Fill(f, seg, declStart, p, attrs);
                var nm = new Regex(@"\b" + Regex.Escape(name) + @"\b").Match(Src.Masked, searchFrom);
                f.NameOffset = nm.Success ? nm.Index : p;
                if (nm.Success) searchFrom = nm.Index + name.Length;
                owner.Fields.Add(f);
            }
        }

        void SplitMemberHead(string text, List<string> modifiers, out string type, out string name)
        {
            var toks = Tokens(text);
            int i = 0;
            while (i < toks.Count - 1 && ModifierWords.Contains(toks[i])) modifiers.Add(toks[i++]);
            name = toks.Count > 0 ? toks[toks.Count - 1] : "";
            int dot = name.LastIndexOf('.');
            if (dot >= 0) name = name.Substring(dot + 1);
            type = string.Join(" ", toks.GetRange(i, Math.Max(0, toks.Count - 1 - i)).ToArray());
        }

        // ------------------------------------------------------------------------------------------------ helpers

        static string StripLeadingAttributes(string s)
        {
            s = s.Trim();
            while (s.StartsWith("[", StringComparison.Ordinal))
            {
                int depth = 0, k = 0;
                for (; k < s.Length; k++)
                {
                    if (s[k] == '[') depth++;
                    else if (s[k] == ']' && --depth == 0) break;
                }
                s = k + 1 < s.Length ? s.Substring(k + 1).Trim() : "";
            }
            return s;
        }

        static int SkipAngle(string s, int i)
        {
            int depth = 0;
            for (; i < s.Length; i++)
            {
                if (s[i] == '<') depth++;
                else if (s[i] == '>' && --depth == 0) return i + 1;
            }
            return s.Length;
        }

        /// <summary>Whitespace-separated tokens, keeping generic argument lists and array ranks attached.</summary>
        public static List<string> Tokens(string s)
        {
            var list = new List<string>();
            var cur = new System.Text.StringBuilder();
            int depth = 0;   // inside <>, [] or ()
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                depth = Math.Max(0, depth + Nesting(c));
                if (!char.IsWhiteSpace(c) || depth > 0) { cur.Append(c); continue; }
                int j = i;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                if (Glues(s, j, cur)) { i = j - 1; continue; }     // "List <int>", "int []", "Foo . Bar", "a, b"
                if (cur.Length > 0) { list.Add(cur.ToString()); cur.Length = 0; }
            }
            if (cur.Length > 0) list.Add(cur.ToString());
            return list;
        }

        static int Nesting(char c)
        {
            if (c == '<' || c == '[' || c == '(') return 1;
            if (c == '>' || c == ']' || c == ')') return -1;
            return 0;
        }

        /// <summary>Whitespace before <paramref name="next"/> is not a token break when it only separates parts of one type.</summary>
        static bool Glues(string s, int next, System.Text.StringBuilder cur)
        {
            if (cur.Length == 0) return false;
            if (next < s.Length && "<[.?,".IndexOf(s[next]) >= 0) return true;
            char last = cur[cur.Length - 1];
            return last == '.' || last == ',';
        }

        /// <summary>Splits on a separator outside (), [], {} and &lt;&gt;.</summary>
        public static List<string> SplitTopLevel(string s, char sep)
        {
            var list = new List<string>();
            int depth = 0, angle = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth = Math.Max(0, depth - 1);
                else if (c == '<') angle++;
                else if (c == '>') angle = Math.Max(0, angle - 1);
                else if (c == sep && depth == 0 && angle == 0)
                {
                    list.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            string last = s.Substring(start);
            if (last.Trim().Length > 0 || list.Count > 0) list.Add(last);
            return list;
        }

        static int TopLevelIndexOf(string s, char target)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == target && depth == 0) return i;
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth = Math.Max(0, depth - 1);
            }
            return -1;
        }

        public static int TopLevelAssignIndex(string s)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(' || c == '[' || c == '{') { depth++; continue; }
                if (c == ')' || c == ']' || c == '}') { depth = Math.Max(0, depth - 1); continue; }
                if (c != '=' || depth > 0) continue;
                char prev = i > 0 ? s[i - 1] : ' ';
                char next = i + 1 < s.Length ? s[i + 1] : ' ';
                if (next == '=' || next == '>' || prev == '=' || prev == '!' || prev == '<' || prev == '>') continue;
                return i;
            }
            return -1;
        }

        /// <summary>"global::UnityEngine.MonoBehaviour" / "IList&lt;int&gt;" -> "MonoBehaviour" / "IList".</summary>
        public static string SimpleTypeName(string t)
        {
            t = t.Trim();
            int lt = t.IndexOf('<');
            if (lt >= 0) t = t.Substring(0, lt);
            int br = t.IndexOf('[');
            if (br >= 0) t = t.Substring(0, br);
            t = t.TrimEnd('?').Trim();
            int dot = t.LastIndexOf('.');
            if (dot >= 0) t = t.Substring(dot + 1);
            int colon = t.LastIndexOf(':');
            if (colon >= 0) t = t.Substring(colon + 1);
            return t.Trim();
        }
    }
}
