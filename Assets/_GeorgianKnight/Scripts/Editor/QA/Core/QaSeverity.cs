using System;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// How bad a QA finding is. The order is significant (lower value = worse) because reports sort by it and the gate
    /// fails on anything at <see cref="Critical"/> or worse.
    /// </summary>
    public enum QaSeverity
    {
        /// <summary>Breaks the build, the game or saves. Never ship.</summary>
        Blocker = 0,
        /// <summary>Visible defect or runtime bug that a player will hit. Gate fails.</summary>
        Critical = 1,
        /// <summary>Quality or performance problem that must be fixed before the milestone.</summary>
        Major = 2,
        /// <summary>Polish / maintainability issue.</summary>
        Minor = 3,
        /// <summary>Observation for a human to judge.</summary>
        Info = 4
    }

    /// <summary>Severity helpers shared by the runner, the report writers and the code linter.</summary>
    public static class QaSeverities
    {
        public static readonly QaSeverity[] All = { QaSeverity.Blocker, QaSeverity.Critical, QaSeverity.Major, QaSeverity.Minor, QaSeverity.Info };

        /// <summary>True when an unsuppressed issue of this severity fails the QA gate.</summary>
        public static bool FailsGate(QaSeverity s) { return s <= QaSeverity.Critical; }

        /// <summary>One step milder (Info stays Info); used for findings reached only indirectly (e.g. via a call chain).</summary>
        public static QaSeverity Milder(QaSeverity s) { return s >= QaSeverity.Info ? QaSeverity.Info : (QaSeverity)((int)s + 1); }

        public static bool TryParse(string text, out QaSeverity s)
        {
            s = QaSeverity.Info;
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var v in All)
                if (string.Equals(v.ToString(), text.Trim(), StringComparison.OrdinalIgnoreCase)) { s = v; return true; }
            return false;
        }
    }
}
