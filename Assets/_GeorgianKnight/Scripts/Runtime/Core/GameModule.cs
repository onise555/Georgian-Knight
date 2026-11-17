using UnityEngine;

namespace GeorgianKnight.Core
{
    /// <summary>
    /// A feature module installed by <see cref="GameRoot"/> at boot (gameplay systems, UI). Modules live in higher
    /// assemblies and are listed in <see cref="GameConfig"/>, which keeps Core free of upward dependencies.
    /// </summary>
    public abstract class GameModule : ScriptableObject
    {
        /// <summary>Create and register services; may add components to the root object.</summary>
        public abstract void Install(GameRoot root);
        /// <summary>Called after every module is installed (resolve cross-module services here).</summary>
        public virtual void Initialize(GameRoot root) { }
    }
}
