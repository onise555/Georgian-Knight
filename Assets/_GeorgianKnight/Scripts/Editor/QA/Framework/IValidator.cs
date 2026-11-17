using System.Collections.Generic;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// One automated check of the project. Implementations are discovered with TypeCache (any non-abstract class with
    /// a parameterless constructor), so adding a validator is just adding a class. <see cref="Run"/> may yield issues
    /// lazily; an exception becomes a QA.VALIDATOR_CRASH issue and fails the gate instead of silently skipping work.
    /// </summary>
    public interface IValidator
    {
        /// <summary>Stable lowercase id ("texture", "scene"), used in the report and by "qa id:&lt;id&gt;".</summary>
        string Id { get; }

        /// <summary>One of <see cref="QaCategory"/>; "qa &lt;category&gt;" runs every validator of that category.</summary>
        string Category { get; }

        IEnumerable<QaIssue> Run(QaContext ctx);
    }
}
