using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// Text checks for string tables: script detection (Mkhedruli vs Latin), mojibake, placeholder text, format
    /// placeholders and typography. Pure functions, shared by the localization validator, the code linter and tests.
    /// </summary>
    public static class LocText
    {
        /// <summary>Latin tokens that legitimately appear inside Georgian text (input glyphs, abbreviations, brands).</summary>
        public static readonly HashSet<string> AllowedLatin = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LMB", "RMB", "MMB", "Shift", "Space", "Tab", "Ctrl", "Alt", "Esc", "Enter", "Backspace", "Delete", "WASD", "HP", "XP",
            "UI", "FPS", "HDR", "FOV", "VSync", "V-Sync", "DLSS", "FSR", "TAA", "SMAA", "FXAA", "SSAO", "Steam", "Unity", "OK",
            "Xbox", "PlayStation", "DualShock", "DualSense", "LB", "RB", "LT", "RT", "LS", "RS", "D-pad", "Noto", "Mixamo"
        };

        static readonly Regex Markup = new Regex(@"<[^<>]{1,60}>|\{\d+(?:[,:][^}]*)?\}|\{[a-z_]+(?::[^}]*)?\}|\[[A-Za-z0-9 _\-]{1,16}\]", RegexOptions.Compiled);
        static readonly Regex FormatRx = new Regex(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);
        static readonly Regex MojibakeRx = new Regex(@"�|áƒ|Ã[\u0080-¿]|â€|Ð[\u0080-¿]|\\u10[a-fA-F0-9]{2}|\?{3,}", RegexOptions.Compiled);
        static readonly Regex PlaceholderAnywhere = new Regex(@"(?i)\b(?:lorem|ipsum|todo|tbd|fixme|placeholder|asdf|qwerty)\b", RegexOptions.Compiled);
        static readonly Regex PlaceholderWhole = new Regex(@"(?i)^(?:test(?:ing)?\s*\d*|item\s*\d+|text\s*\d*|string\s*\d*|new item|new text|untitled|sample|dummy|x{3,}|\?+|\.\.\.|-+|#|ტესტი?\s*\d*)$", RegexOptions.Compiled);
        static readonly Regex LatinWordRx = new Regex(@"[A-Za-z][A-Za-z\-]*", RegexOptions.Compiled);

        public static bool IsGeorgianLetter(char c)
        {
            return (c >= 'Ⴀ' && c <= 'ჿ') || (c >= 'Ა' && c <= 'Ჿ') || (c >= 'ⴀ' && c <= '⴯');
        }

        public static bool HasGeorgian(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (IsGeorgianLetter(c)) return true;
            return false;
        }

        /// <summary>Removes rich-text tags, {0}/{name} placeholders and [Glyph] tokens before script analysis.</summary>
        public static string StripMarkup(string s) { return string.IsNullOrEmpty(s) ? "" : Markup.Replace(s, " "); }

        public static void CountLetters(string s, out int georgian, out int latin)
        {
            georgian = 0; latin = 0;
            foreach (char c in StripMarkup(s))
            {
                if (IsGeorgianLetter(c)) georgian++;
                else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) latin++;
            }
        }

        /// <summary>Latin words (2+ letters) that are not allowed glyph/brand tokens.</summary>
        public static List<string> ForeignLatinWords(string s)
        {
            var list = new List<string>();
            foreach (Match m in LatinWordRx.Matches(StripMarkup(s)))
                if (m.Value.Length >= 2 && !AllowedLatin.Contains(m.Value) && !list.Contains(m.Value)) list.Add(m.Value);
            return list;
        }

        public static bool IsMojibake(string s) { return !string.IsNullOrEmpty(s) && MojibakeRx.IsMatch(s); }

        public static bool IsPlaceholder(string s)
        {
            if (s == null) return false;
            string t = s.Trim();
            return PlaceholderAnywhere.IsMatch(t) || PlaceholderWhole.IsMatch(t);
        }

        /// <summary>Sorted distinct {n} indices used by a format string.</summary>
        public static List<int> FormatIndices(string s)
        {
            var list = new List<int>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (Match m in FormatRx.Matches(s))
            {
                int n;
                if (int.TryParse(m.Groups[1].Value, out n) && !list.Contains(n)) list.Add(n);
            }
            list.Sort();
            return list;
        }

        public static bool SameFormat(string a, string b)
        {
            var x = FormatIndices(a);
            var y = FormatIndices(b);
            if (x.Count != y.Count) return false;
            for (int i = 0; i < x.Count; i++) if (x[i] != y[i]) return false;
            return true;
        }

        /// <summary>Human-readable typography problems ("double space", "straight quotes" …); empty when clean.</summary>
        public static List<string> Typography(string s, bool georgian)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            if (s != s.Trim()) list.Add("leading/trailing whitespace");
            if (s.IndexOf("  ", StringComparison.Ordinal) >= 0) list.Add("double space");
            if (s.IndexOf("...", StringComparison.Ordinal) >= 0) list.Add("'...' instead of '…'");
            if (Regex.IsMatch(s, @"\s[,.;:!?](?:\s|$)")) list.Add("space before punctuation");
            if (georgian && s.IndexOf('"') >= 0) list.Add("straight quotes (use „…“)");
            return list;
        }
    }
}
