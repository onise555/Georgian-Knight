using System;
using System.Collections.Generic;
using System.Text;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>A string or char literal found in a C# file.</summary>
    public sealed class SourceLiteral
    {
        public int Start, End;      // [Start, End) in the source, delimiters included
        public int Line;
        public string Value = "";   // decoded content
        public bool Interpolated, Verbatim, IsChar;
    }

    /// <summary>A comment found in a C# file (text without the // or /* */ markers).</summary>
    public sealed class SourceComment
    {
        public int Start, End;
        public int Line, EndLine;
        public string Text = "";
        public bool Doc, Block;
    }

    /// <summary>
    /// Lexical view of one C# file for the linters: the original text, a masked copy of the same length in which
    /// comments, preprocessor lines and literal contents are blanked (line breaks kept), so regex rules never match inside
    /// strings or comments; the literals and comments themselves; and per-line "non-shipping" state for code inside
    /// <c>#if UNITY_EDITOR</c> / <c>DEVELOPMENT_BUILD</c> / <c>DEBUG</c> regions.
    /// </summary>
    public sealed class CSharpSource
    {
        public readonly string Path;
        public readonly string Text;
        public readonly string Masked;
        public readonly List<SourceLiteral> Literals = new List<SourceLiteral>();
        public readonly List<SourceComment> Comments = new List<SourceComment>();
        readonly List<int> lineStarts = new List<int>();
        readonly List<bool> nonShipping = new List<bool>();

        public int LineCount { get { return lineStarts.Count; } }

        public CSharpSource(string path, string text)
        {
            Path = path ?? "";
            Text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            lineStarts.Add(0);
            for (int i = 0; i < Text.Length; i++) if (Text[i] == '\n') lineStarts.Add(i + 1);
            var mask = Text.ToCharArray();
            Lex(mask);
            Masked = new string(mask);
        }

        /// <summary>1-based line of a character offset.</summary>
        public int LineOf(int offset)
        {
            int lo = 0, hi = lineStarts.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (lineStarts[mid] <= offset) lo = mid; else hi = mid - 1;
            }
            return lo + 1;
        }

        public int LineStart(int line) { return lineStarts[Math.Max(0, Math.Min(lineStarts.Count - 1, line - 1))]; }

        /// <summary>Original text of a 1-based line (without the line break).</summary>
        public string LineText(int line)
        {
            int s = LineStart(line);
            int e = line < lineStarts.Count ? lineStarts[line] - 1 : Text.Length;
            return Text.Substring(s, Math.Max(0, e - s));
        }

        /// <summary>True when the line is compiled only into the editor / development builds.</summary>
        public bool IsNonShipping(int line)
        {
            int i = line - 1;
            return i >= 0 && i < nonShipping.Count && nonShipping[i];
        }

        public bool IsNonShippingAt(int offset) { return IsNonShipping(LineOf(offset)); }

        /// <summary>The literal starting at or containing the offset, or null.</summary>
        public SourceLiteral LiteralAt(int offset)
        {
            int lo = 0, hi = Literals.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                var l = Literals[mid];
                if (offset < l.Start) hi = mid - 1;
                else if (offset >= l.End) lo = mid + 1;
                else return l;
            }
            return null;
        }

        // ------------------------------------------------------------------------------------------------ lexer

        // lexer state (construction only)
        char[] mask;
        int pos, line;
        PreprocessorState pp;

        void Lex(char[] buffer)
        {
            mask = buffer;
            pp = new PreprocessorState();
            for (int l = 0; l < lineStarts.Count; l++) nonShipping.Add(false);
            nonShipping[0] = pp.NonShipping;
            bool lineHasCode = false;
            int n = Text.Length;
            while (pos < n)
            {
                char c = Text[pos];
                if (c == '\n') { pos++; NewLine(); lineHasCode = false; continue; }
                if (c == ' ' || c == '\t') { pos++; continue; }
                if (c == '#' && !lineHasCode) { LexDirective(); continue; }
                if (c == '/' && pos + 1 < n && Text[pos + 1] == '/') { LexLineComment(); continue; }
                if (c == '/' && pos + 1 < n && Text[pos + 1] == '*') { LexBlockComment(); continue; }
                lineHasCode = true;
                if (IsStringStart(pos)) LexString();
                else if (c == '\'') LexChar();
                else pos++;
            }
            mask = null;
            pp = null;
        }

        void NewLine()
        {
            line++;
            if (line < nonShipping.Count) nonShipping[line] = pp.NonShipping;
        }

        int LineEnd(int from)
        {
            int e = Text.IndexOf('\n', from);
            return e < 0 ? Text.Length : e;
        }

        void LexDirective()
        {
            int e = LineEnd(pos);
            string directive = Text.Substring(pos + 1, e - pos - 1);
            int cut = directive.IndexOf("//", StringComparison.Ordinal);
            if (cut >= 0) directive = directive.Substring(0, cut);
            bool wasNonShipping = pp.NonShipping;
            pp.Apply(directive.Trim());
            // the directive line itself counts as non-shipping when it opens or closes such a region
            nonShipping[line] = wasNonShipping || pp.NonShipping;
            Blank(mask, pos, e);
            pos = e;
        }

        void LexLineComment()
        {
            int n = Text.Length, e = LineEnd(pos);
            bool doc = pos + 2 < n && Text[pos + 2] == '/' && !(pos + 3 < n && Text[pos + 3] == '/');
            int skip = doc ? 3 : 2;
            Comments.Add(new SourceComment { Start = pos, End = e, Line = line + 1, EndLine = line + 1, Text = Text.Substring(pos + skip, e - pos - skip), Doc = doc });
            Blank(mask, pos, e);
            pos = e;
        }

        void LexBlockComment()
        {
            int n = Text.Length;
            int e = Text.IndexOf("*/", pos + 2, StringComparison.Ordinal);
            e = e < 0 ? n : e + 2;
            int startLine = line + 1;
            for (int k = pos; k < e; k++) if (Text[k] == '\n') NewLine();
            Comments.Add(new SourceComment
            {
                Start = pos, End = e, Line = startLine, EndLine = line + 1, Text = Text.Substring(pos + 2, Math.Max(0, Math.Min(e, n) - pos - 4)), Block = true
            });
            Blank(mask, pos, e);
            pos = e;
        }

        /// <summary>"…", @"…", $"…", $@"…" or @$"…" starts at <paramref name="i"/>.</summary>
        bool IsStringStart(int i)
        {
            string s = Text;
            int n = s.Length;
            if (s[i] == '"') return true;
            if ((s[i] != '@' && s[i] != '$') || i + 1 >= n) return false;
            if (s[i + 1] == '"') return true;
            return (s[i + 1] == '@' || s[i + 1] == '$') && i + 2 < n && s[i + 2] == '"';
        }

        void LexString()
        {
            string s = Text;
            int start = pos;
            bool verbatim = false, interp = false;
            while (s[pos] != '"') { if (s[pos] == '@') verbatim = true; else interp = true; pos++; }
            var lit = new SourceLiteral { Start = start, Line = line + 1, Verbatim = verbatim, Interpolated = interp };
            int contentStart = pos + 1;
            int end = ScanString(s, contentStart, verbatim, interp);   // index after closing quote
            lit.End = end;
            int contentEnd = end > contentStart && end <= s.Length && s[end - 1] == '"' ? end - 1 : end;
            lit.Value = Decode(s.Substring(contentStart, Math.Max(0, contentEnd - contentStart)), verbatim);
            Literals.Add(lit);
            for (int k = contentStart; k < contentEnd; k++)
            {
                if (s[k] == '\n') NewLine();
                else mask[k] = ' ';
            }
            pos = end;
        }

        void LexChar()
        {
            string s = Text;
            int n = s.Length, start = pos, k = pos + 1;
            while (k < n && s[k] != '\'' && s[k] != '\n') { if (s[k] == '\\') k++; k++; }
            int end = Math.Min(n, k + 1);
            Literals.Add(new SourceLiteral
            {
                Start = start, End = end, Line = line + 1, IsChar = true, Value = Decode(s.Substring(start + 1, Math.Max(0, Math.Min(k, n) - start - 1)), false)
            });
            for (int m = start + 1; m < Math.Min(k, n); m++) mask[m] = ' ';
            pos = end;
        }

        static void Blank(char[] mask, int from, int to)
        {
            for (int k = from; k < to && k < mask.Length; k++) if (mask[k] != '\n') mask[k] = ' ';
        }

        /// <summary>Returns the index just after the closing quote (or the end of line/file for broken literals).</summary>
        static int ScanString(string s, int i, bool verbatim, bool interp)
        {
            int n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (interp && c == '{')
                {
                    if (i + 1 < n && s[i + 1] == '{') { i += 2; continue; }
                    i = SkipHole(s, i + 1);
                    continue;
                }
                if (verbatim)
                {
                    if (c != '"') { i++; continue; }
                    if (i + 1 < n && s[i + 1] == '"') { i += 2; continue; }
                    return i + 1;
                }
                if (c == '\\') { i += 2; continue; }
                if (c == '"') return i + 1;
                if (c == '\n') return i;    // unterminated: stop at the line end
                i++;
            }
            return n;
        }

        /// <summary>Skips an interpolation hole (after its '{'), including nested strings and chars; returns the index after '}'.</summary>
        static int SkipHole(string s, int i)
        {
            int n = s.Length, depth = 1;
            while (i < n && depth > 0)
            {
                char c = s[i];
                if (c == '"')
                {
                    i = ScanString(s, i + 1, i > 0 && s[i - 1] == '@', false);   // nested string inside the hole
                    continue;
                }
                if (c == '\'')
                {
                    int k = i + 1;
                    while (k < n && s[k] != '\'') { if (s[k] == '\\') k++; k++; }
                    i = k + 1;
                    continue;
                }
                if (c == '{') depth++;
                else if (c == '}') depth--;
                i++;
            }
            return i;
        }

        static string Decode(string raw, bool verbatim)
        {
            if (verbatim) return raw.Replace("\"\"", "\"");
            if (raw.IndexOf('\\') < 0) return raw;
            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c != '\\' || i + 1 >= raw.Length) { sb.Append(c); continue; }
                char e = raw[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '0': sb.Append('\0'); break;
                    case 'u':
                        if (i + 4 < raw.Length)
                        {
                            int code;
                            if (int.TryParse(raw.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                            { sb.Append((char)code); i += 4; break; }
                        }
                        sb.Append('u');
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Tracks #if/#elif/#else/#endif nesting. A region is non-shipping when its condition is a disjunction of
        /// editor/development symbols (or the #else of a negated one).
        /// </summary>
        sealed class PreprocessorState
        {
            sealed class Frame { public bool ifNs, elseNs, current, taken; }
            readonly List<Frame> stack = new List<Frame>();

            public bool NonShipping
            {
                get
                {
                    foreach (var f in stack) if (f.current) return true;
                    return false;
                }
            }

            public void Apply(string directive)
            {
                string word = directive;
                string rest = "";
                int sp = directive.IndexOfAny(new[] { ' ', '\t', '(' });
                if (sp > 0) { word = directive.Substring(0, sp); rest = directive.Substring(sp).Trim(); }
                switch (word)
                {
                    case "if":
                        {
                            bool neg;
                            bool dev = IsDevCondition(rest, out neg);
                            var f = new Frame { ifNs = dev && !neg, elseNs = dev && neg };
                            f.current = f.ifNs;
                            stack.Add(f);
                            break;
                        }
                    case "elif":
                        if (stack.Count > 0) { var f = stack[stack.Count - 1]; f.current = false; f.taken = true; }
                        break;
                    case "else":
                        if (stack.Count > 0) { var f = stack[stack.Count - 1]; f.current = !f.taken && f.elseNs; }
                        break;
                    case "endif":
                        if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                        break;
                }
            }

            static readonly string[] DevSymbols = { "UNITY_EDITOR", "DEVELOPMENT_BUILD", "DEBUG", "GK_DEV", "UNITY_INCLUDE_TESTS" };

            static bool IsDevCondition(string cond, out bool negated)
            {
                negated = false;
                string c = cond.Replace("(", " ").Replace(")", " ").Trim();
                if (c.StartsWith("!", StringComparison.Ordinal))
                {
                    string sym = c.Substring(1).Trim();
                    if (Array.IndexOf(DevSymbols, sym) >= 0) { negated = true; return true; }
                    return false;
                }
                if (c.IndexOf("&&", StringComparison.Ordinal) >= 0 || c.IndexOf('!') >= 0) return false;
                foreach (var part in c.Split(new[] { "||" }, StringSplitOptions.RemoveEmptyEntries))
                    if (Array.IndexOf(DevSymbols, part.Trim()) < 0) return false;
                return c.Length > 0;
            }
        }
    }
}
