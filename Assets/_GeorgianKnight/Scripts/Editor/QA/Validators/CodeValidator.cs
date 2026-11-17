using System.Collections.Generic;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// CODE.* rules over every script under Assets/_GeorgianKnight/Scripts (engine-free <see cref="CodeLinter"/>; the
    /// container runs the same rules via Tools/qa/code_lint.py). Also hands the localization keys found in code to the
    /// localization validator.
    /// </summary>
    public sealed class CodeValidator : IValidator
    {
        public string Id { get { return "code"; } }
        public string Category { get { return QaCategory.Code; } }

        public IEnumerable<QaIssue> Run(QaContext ctx)
        {
            ctx.Progress("reading scripts", 0f);
            var files = ctx.ReadScripts();
            ctx.Progress("linting " + files.Count + " files", 0.3f);
            var linter = new CodeLinter(ctx.CodeOptions);
            var issues = linter.Lint(files);
            ctx.SetCodeLocKeys(linter.LocKeys);
            return issues;
        }
    }
}
