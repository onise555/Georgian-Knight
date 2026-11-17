using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// The swing-speed A/B for the Director (QUALITY 3c tip speed): A plays every attack as built; B slows each attack
    /// whose sword tip peaked over ~35 m/s by its own factor (Generated/Resources/swing_tempo_b.txt, measured by
    /// swingqa), so the tip tops out near 33 m/s. Switched at run time (F8 in a sandbox, or the bridge's 'tempo a|b').
    /// </summary>
    public static class SwingTempo
    {
        public static bool B { get; private set; }
        static Dictionary<string, float> factors;

        public static void Set(bool b) { B = b; Debug.Log("[SwingTempo] " + (b ? "B (slowed to ~33 m/s at the tip)" : "A (as built)")); }

        /// <summary>The play-rate factor for this attack: 1 in A, the measured factor in B.</summary>
        public static float For(AttackData a)
        {
            if (!B || a == null) return 1f;
            if (factors == null) Load();
            float f;
            return factors.TryGetValue(a.name, out f) ? f : 1f;
        }

        static void Load()
        {
            factors = new Dictionary<string, float>();
            var txt = Resources.Load<TextAsset>("swing_tempo_b");
            if (txt == null) return;
            foreach (var line in txt.text.Split('\n'))
            {
                var l = line.Trim();
                if (l.Length == 0 || l[0] == '#') continue;
                var p = l.Split(' ');
                float f;
                if (p.Length >= 2 && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f))
                    factors[p[0]] = Mathf.Clamp(f, 0.3f, 1f);
            }
        }
    }
}
