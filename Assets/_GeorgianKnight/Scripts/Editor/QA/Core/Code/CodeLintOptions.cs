using System;
using System.Collections.Generic;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>Which part of the code base a file belongs to; rules are stricter for shipping runtime code.</summary>
    public enum CodeScope { Runtime, Editor, Tests }

    /// <summary>
    /// Thresholds and allow-lists of the code linter. Defaults encode ARCHITECTURE.md / TEAM_BRIEF.md; the Python
    /// linter (Tools/qa/code_lint.py) carries the same defaults — change both together.
    /// </summary>
    public sealed class CodeLintOptions
    {
        /// <summary>Project-relative root of all scripts; paths are reported below it.</summary>
        public string ScriptsRoot = "Assets/_GeorgianKnight/Scripts";

        public int MaxMethodLines = 80;
        public int MaxComplexity = 20;
        /// <summary>How many intra-class calls away from Update/Tick a method still counts as hot.</summary>
        public int HotCallDepth = 3;

        public readonly HashSet<string> HotRoots = new HashSet<string>(StringComparer.Ordinal)
        {
            "Update", "LateUpdate", "FixedUpdate", "OnGUI", "OnAnimatorMove", "OnAnimatorIK", "OnTriggerStay",
            "OnCollisionStay", "OnRenderObject", "OnWillRenderObject", "Tick", "LateTick", "FixedTick"
        };

        /// <summary>Files allowed to search the scene at runtime (bootstrap, debug tools).</summary>
        public readonly List<string> FindAllowList = new List<string> { "**/Runtime/Core/GameRoot.cs", "**/Runtime/Gameplay/Debug/**" };
        public readonly List<string> TimeScaleAllowList = new List<string> { "**/Runtime/Core/Time/TimeService.cs" };
        public readonly List<string> SingletonAllowList = new List<string> { "**/Runtime/Core/GameRoot.cs" };
        /// <summary>Development-only scripts: perf/hot-path rules are relaxed (they must not be referenced by shipping scenes).</summary>
        public readonly List<string> DevOnlyPaths = new List<string> { "**/Runtime/Gameplay/Debug/**" };
        public readonly List<string> ResourcesRoots = new List<string> { "Localization", "GameConfig" };

        /// <summary>Folders whose numbers are balance data (CODE.MAGIC_NUMBER).</summary>
        public readonly List<string> BalanceDirs = new List<string>
        {
            "**/Runtime/Gameplay/Combat/**", "**/Runtime/Gameplay/AI/**", "**/Runtime/Gameplay/Stats/**",
            "**/Runtime/Gameplay/Economy/**", "**/Runtime/Gameplay/Encounters/**", "**/Runtime/Gameplay/Player/**"
        };

        /// <summary>Numbers that are units/identities rather than tuning (0, 1, 2, ½, percentages, degrees, time units).</summary>
        public readonly HashSet<double> NeutralNumbers = new HashSet<double> { 0, 1, 2, 0.5, 10, 100, 1000, 60, 90, 180, 360, 3600, 24, 255 };

        public CodeScope ScopeOf(string path)
        {
            string rel = Relative(path);
            if (rel.StartsWith("Editor/", StringComparison.Ordinal)) return CodeScope.Editor;
            if (rel.StartsWith("Tests/", StringComparison.Ordinal)) return CodeScope.Tests;
            return CodeScope.Runtime;
        }

        /// <summary>Path below <see cref="ScriptsRoot"/> ("Runtime/Core/Loc.cs").</summary>
        public string Relative(string path)
        {
            string p = (path ?? "").Replace('\\', '/');
            string root = ScriptsRoot.TrimEnd('/') + "/";
            int i = p.IndexOf(root, StringComparison.Ordinal);
            return i >= 0 ? p.Substring(i + root.Length) : p;
        }

        public static bool AnyGlob(List<string> globs, string path)
        {
            foreach (var g in globs) if (QaGlob.IsMatch(g, path)) return true;
            return false;
        }
    }
}
