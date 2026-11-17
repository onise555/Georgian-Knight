using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>JSON value kinds produced by <see cref="QaJson.Parse"/>.</summary>
    public enum QaJsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// A parsed JSON value that remembers the line it started on, so validators can point at the exact entry of a
    /// string table or suppression file. Objects keep every member in order, duplicates included (JsonUtility silently
    /// keeps the last one, which is exactly what LOC.DUPLICATE_KEY needs to see).
    /// </summary>
    public sealed class QaJsonNode
    {
        public QaJsonKind Kind;
        public int Line;
        public string Text;          // string value, or the raw number text
        public bool Bool;
        public readonly List<QaJsonNode> Items = new List<QaJsonNode>();
        public readonly List<KeyValuePair<string, QaJsonNode>> Members = new List<KeyValuePair<string, QaJsonNode>>();

        /// <summary>First member with this name, or null.</summary>
        public QaJsonNode this[string key]
        {
            get
            {
                for (int i = 0; i < Members.Count; i++)
                    if (Members[i].Key == key) return Members[i].Value;
                return null;
            }
        }

        public string Str(string key)
        {
            var n = this[key];
            return n != null && n.Kind == QaJsonKind.String ? n.Text : null;
        }

        public double Num(string key, double fallback)
        {
            var n = this[key];
            double d;
            if (n == null || n.Kind != QaJsonKind.Number) return fallback;
            return double.TryParse(n.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : fallback;
        }
    }

    /// <summary>Thrown by <see cref="QaJson.Parse"/> with a 1-based line/column.</summary>
    public sealed class QaJsonException : Exception
    {
        public readonly int Line, Column;
        public QaJsonException(string message, int line, int column) : base(message + " (line " + line + ", col " + column + ")")
        {
            Line = line; Column = column;
        }
    }

    /// <summary>
    /// Minimal, strict JSON reader/writer for QA data (suppressions, string tables, reports). It has no engine
    /// dependency so the same code runs in Unity and in the container harness.
    /// </summary>
    public static class QaJson
    {
        public static QaJsonNode Parse(string text)
        {
            var p = new Parser(text ?? "");
            p.SkipWs();
            if (p.pos < p.s.Length && p.s[p.pos] == '﻿') { p.pos++; p.SkipWs(); }
            var v = p.Value();
            p.SkipWs();
            if (p.pos < p.s.Length) throw p.Error("unexpected trailing content");
            return v;
        }

        sealed class Parser
        {
            public readonly string s;
            public int pos;
            int line = 1, lineStart;

            public Parser(string text) { s = text; }

            public QaJsonException Error(string msg) { return new QaJsonException(msg, line, pos - lineStart + 1); }

            public void SkipWs()
            {
                while (pos < s.Length)
                {
                    char c = s[pos];
                    if (c == '\n') { line++; pos++; lineStart = pos; }
                    else if (c == ' ' || c == '\t' || c == '\r') pos++;
                    else break;
                }
            }

            public QaJsonNode Value()
            {
                SkipWs();
                if (pos >= s.Length) throw Error("unexpected end of input");
                var node = new QaJsonNode { Line = line };
                char c = s[pos];
                if (c == '{') { Obj(node); return node; }
                if (c == '[') { Arr(node); return node; }
                if (c == '"') { node.Kind = QaJsonKind.String; node.Text = Str(); return node; }
                if (Lit("true")) { node.Kind = QaJsonKind.Bool; node.Bool = true; return node; }
                if (Lit("false")) { node.Kind = QaJsonKind.Bool; return node; }
                if (Lit("null")) { node.Kind = QaJsonKind.Null; return node; }
                if (c == '-' || (c >= '0' && c <= '9')) { node.Kind = QaJsonKind.Number; node.Text = Number(); return node; }
                throw Error("unexpected character '" + c + "'");
            }

            bool Lit(string word)
            {
                if (string.CompareOrdinal(s, pos, word, 0, word.Length) != 0) return false;
                pos += word.Length;
                return true;
            }

            string Number()
            {
                int start = pos;
                if (s[pos] == '-') pos++;
                while (pos < s.Length && "0123456789.eE+-".IndexOf(s[pos]) >= 0) pos++;
                string t = s.Substring(start, pos - start);
                double d;
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) throw Error("bad number '" + t + "'");
                return t;
            }

            string Str()
            {
                pos++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (pos >= s.Length) throw Error("unterminated string");
                    char c = s[pos++];
                    if (c == '"') return sb.ToString();
                    if (c == '\n') throw Error("newline inside string");
                    if (c != '\\') { sb.Append(c); continue; }
                    if (pos >= s.Length) throw Error("unterminated escape");
                    char e = s[pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            {
                                if (pos + 4 > s.Length) throw Error("bad \\u escape");
                                int code;
                                if (!int.TryParse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) throw Error("bad \\u escape");
                                sb.Append((char)code);
                                pos += 4;
                                break;
                            }
                        default: throw Error("bad escape '\\" + e + "'");
                    }
                }
            }

            void Arr(QaJsonNode node)
            {
                node.Kind = QaJsonKind.Array;
                pos++;
                SkipWs();
                if (pos < s.Length && s[pos] == ']') { pos++; return; }
                while (true)
                {
                    node.Items.Add(Value());
                    SkipWs();
                    if (pos >= s.Length) throw Error("unterminated array");
                    char c = s[pos++];
                    if (c == ']') return;
                    if (c != ',') throw Error("expected ',' or ']'");
                }
            }

            void Obj(QaJsonNode node)
            {
                node.Kind = QaJsonKind.Object;
                pos++;
                SkipWs();
                if (pos < s.Length && s[pos] == '}') { pos++; return; }
                while (true)
                {
                    SkipWs();
                    if (pos >= s.Length || s[pos] != '"') throw Error("expected member name");
                    string key = Str();
                    SkipWs();
                    if (pos >= s.Length || s[pos] != ':') throw Error("expected ':'");
                    pos++;
                    node.Members.Add(new KeyValuePair<string, QaJsonNode>(key, Value()));
                    SkipWs();
                    if (pos >= s.Length) throw Error("unterminated object");
                    char c = s[pos++];
                    if (c == '}') return;
                    if (c != ',') throw Error("expected ',' or '}'");
                }
            }
        }

        /// <summary>JSON string literal (with quotes) for any text.</summary>
        public static string Quote(string text)
        {
            if (text == null) return "null";
            var sb = new StringBuilder(text.Length + 2);
            sb.Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
