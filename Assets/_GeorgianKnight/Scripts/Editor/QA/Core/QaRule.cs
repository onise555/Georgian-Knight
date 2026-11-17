namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>Category names used for filtering ("qa &lt;category&gt;") and for grouping the report.</summary>
    public static class QaCategory
    {
        public const string Code = "Code";
        public const string Texture = "Texture";
        public const string Material = "Material";
        public const string Model = "Model";
        public const string Animation = "Animation";
        public const string Audio = "Audio";
        public const string Prefab = "Prefab";
        public const string Scene = "Scene";
        public const string Data = "Data";
        public const string Localization = "Localization";
        public const string Visual = "Visual";
        public const string Meta = "QA";

        public static readonly string[] All = { Code, Texture, Material, Model, Animation, Audio, Prefab, Scene, Data, Localization, Visual, Meta };
    }

    /// <summary>
    /// Static description of one rule: stable id (used by suppressions), default severity, and the rationale / fix text
    /// that ends up in the report and in Docs/QA.md. Validators create issues through <see cref="Issue"/> so every finding
    /// carries a fix hint.
    /// </summary>
    public sealed class QaRule
    {
        public readonly string Id;
        public readonly string Category;
        public readonly QaSeverity Severity;
        public readonly string Title;
        public readonly string Why;
        public readonly string Fix;

        public QaRule(string id, string category, QaSeverity severity, string title, string why, string fix)
        {
            Id = id; Category = category; Severity = severity; Title = title; Why = why; Fix = fix;
        }

        /// <summary>Creates an issue with the rule's default severity.</summary>
        public QaIssue Issue(string path, int line, string message) { return Issue(path, line, "", message, Severity, null); }

        /// <summary>Creates an issue; <paramref name="fix"/> overrides the generic fix hint when the validator knows better.</summary>
        public QaIssue Issue(string path, int line, string obj, string message, QaSeverity severity, string fix)
        {
            return new QaIssue
            {
                Rule = Id,
                Severity = severity,
                Category = Category,
                Path = path ?? "",
                Line = line,
                Object = obj ?? "",
                Message = message ?? "",
                Fix = string.IsNullOrEmpty(fix) ? Fix : fix
            };
        }
    }
}
