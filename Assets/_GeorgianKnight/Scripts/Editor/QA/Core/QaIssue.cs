namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// One finding. Plain data so validators, the code linter (also run outside Unity) and the report writers share it.
    /// <see cref="Path"/> is project-relative ("Assets/_GeorgianKnight/..."), <see cref="Object"/> is an optional
    /// hierarchy path inside a scene/prefab ("Root/Props/Barrel_02") or a sub-asset name.
    /// </summary>
    public sealed class QaIssue
    {
        public string Rule = "";
        public QaSeverity Severity = QaSeverity.Info;
        public string Category = "";
        public string Path = "";
        /// <summary>1-based source line, 0 when the issue is not about a text line.</summary>
        public int Line;
        public string Object = "";
        public string Message = "";
        public string Fix = "";
        /// <summary>Reason of the suppression that silenced this issue, or null when it is active.</summary>
        public string SuppressedBy;

        public bool IsSuppressed { get { return SuppressedBy != null; } }

        /// <summary>"path:line", "path › object" or just the path.</summary>
        public string Location
        {
            get
            {
                string s = Path ?? "";
                if (Line > 0) s += ":" + Line;
                if (!string.IsNullOrEmpty(Object)) s += (s.Length > 0 ? " › " : "") + Object;
                return s;
            }
        }

        public override string ToString() { return Severity + " " + Rule + " " + Location + " — " + Message; }
    }
}
