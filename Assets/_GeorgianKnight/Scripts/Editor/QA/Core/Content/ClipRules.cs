using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>Whether an animation clip should loop, judged from its name.</summary>
    public enum ClipLoopKind { Unknown, Loop, OneShot }

    /// <summary>
    /// Animation import conventions: loop expectations from clip names and "family" consistency of root-motion bake
    /// settings for the AX_ (axe/shield), SS_ (sword) and GK_ (great-sword/misc) Mixamo sets. Pure for unit tests.
    /// </summary>
    public static class ClipRules
    {
        static readonly Regex LoopRx = new Regex(@"(?i)(idle|walk|run|jog|sprint|strafe|locomotion|loop|crouch[_ ]?walk|breath|guard[_ ]?stance|stance|block[_ ]?idle)", RegexOptions.Compiled);
        static readonly Regex OneShotRx = new Regex(@"(?i)(attack|slash|stab|thrust|strike|swing|kick|punch|hit|react|impact|death|die|dying|dodge|roll|jump|draw|sheath|equip|disarm|parry|block(?![_ ]?idle)|stagger|knock|getup|get[_ ]?up|stand[_ ]?up|taunt|battlecry|cry|execution|exec|finisher|turn[_ ]?180|land|victory|thump|combo)", RegexOptions.Compiled);
        static readonly Regex FamilyRx = new Regex(@"^(AX|SS|GK)_", RegexOptions.Compiled);

        public static ClipLoopKind Classify(string clipName)
        {
            if (string.IsNullOrEmpty(clipName)) return ClipLoopKind.Unknown;
            if (LoopRx.IsMatch(clipName) && !Regex.IsMatch(clipName, @"(?i)(to[_ ]?idle|_end|_start|_stop|idle[_ ]?to)")) return ClipLoopKind.Loop;
            if (OneShotRx.IsMatch(clipName)) return ClipLoopKind.OneShot;
            return ClipLoopKind.Unknown;
        }

        /// <summary>"AX" / "SS" / "GK" for our Mixamo families, null otherwise.</summary>
        public static string Family(string name)
        {
            var m = FamilyRx.Match(name ?? "");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// Given (name, settings-signature) pairs of one family, returns the names whose signature differs from the
        /// strict majority, with that majority signature; groups without a strict majority report nothing (ambiguous).
        /// </summary>
        public static List<KeyValuePair<string, string>> Outliers(IList<KeyValuePair<string, string>> items, out string majority)
        {
            majority = null;
            var result = new List<KeyValuePair<string, string>>();
            if (items == null || items.Count < 3) return result;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in items)
            {
                int c;
                counts.TryGetValue(kv.Value, out c);
                counts[kv.Value] = c + 1;
            }
            int best = 0;
            foreach (var kv in counts)
                if (kv.Value > best || (kv.Value == best && string.CompareOrdinal(kv.Key, majority) < 0)) { best = kv.Value; majority = kv.Key; }
            if (best * 2 <= items.Count) { majority = null; return result; }
            foreach (var kv in items) if (kv.Value != majority) result.Add(kv);
            return result;
        }

        /// <summary>Human-readable difference of two signatures built by <see cref="Signature"/>.</summary>
        public static string Describe(string signature, string majority)
        {
            var a = signature.Split(';');
            var b = majority.Split(';');
            var parts = new List<string>();
            for (int i = 0; i < a.Length && i < b.Length; i++) if (a[i] != b[i]) parts.Add(a[i] + " (family: " + b[i] + ")");
            return string.Join(", ", parts.ToArray());
        }

        /// <summary>Canonical signature of the root-motion bake settings of one clip.</summary>
        public static string Signature(bool bakeRotation, bool bakeY, bool bakeXZ, bool keepOrientation, bool keepY, bool keepXZ)
        {
            return "bakeRotation=" + bakeRotation + ";bakeY=" + bakeY + ";bakeXZ=" + bakeXZ + ";originalOrientation=" + keepOrientation +
                   ";originalY=" + keepY + ";originalXZ=" + keepXZ;
        }
    }
}
