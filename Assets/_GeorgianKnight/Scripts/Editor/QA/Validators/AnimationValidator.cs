using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// ANIM.* import rules: loop flags that contradict the clip's purpose, and root-motion bake settings that differ
    /// from the rest of their Mixamo family (AX_/SS_/GK_, split by looping vs one-shot clips).
    /// </summary>
    public sealed class AnimationValidator : IValidator
    {
        sealed class ClipRef
        {
            public string Path, Name, Signature;
        }

        public string Id { get { return "animation"; } }
        public string Category { get { return QaCategory.Animation; } }

        public IEnumerable<QaIssue> Run(QaContext ctx)
        {
            var issues = new List<QaIssue>();
            var families = new Dictionary<string, List<ClipRef>>();
            var paths = ctx.FindAssets("t:Model");
            for (int i = 0; i < paths.Count; i++)
            {
                if (i % 10 == 0) ctx.Progress(paths[i], i / (float)paths.Count);
                var mi = AssetImporter.GetAtPath(paths[i]) as ModelImporter;
                if (mi != null && mi.importAnimation) Clips(paths[i], mi, families, issues);
            }
            Families(families, issues);
            return issues;
        }

        /// <summary>Loop-flag check per clip; collects family members for the root-motion comparison.</summary>
        static void Clips(string path, ModelImporter mi, Dictionary<string, List<ClipRef>> families, List<QaIssue> issues)
        {
            var clips = mi.clipAnimations;
            if (clips == null || clips.Length == 0) clips = mi.defaultClipAnimations;
            string file = Path.GetFileNameWithoutExtension(path);
            foreach (var clip in clips)
            {
                bool generic = clip.name == "mixamo.com" || clip.name.StartsWith("Take ", System.StringComparison.Ordinal) || clips.Length == 1;
                string label = generic ? file : clip.name;
                var kind = ClipRules.Classify(label);
                if (kind == ClipLoopKind.Loop && !clip.loopTime)
                    issues.Add(QaRules.AnimLoopFlag.Issue(path, 0, clip.name, "'" + label + "' is a locomotion/idle clip but Loop Time is off", QaSeverity.Major, null));
                else if (kind == ClipLoopKind.OneShot && clip.loopTime)
                    issues.Add(QaRules.AnimLoopFlag.Issue(path, 0, clip.name, "'" + label + "' is a one-shot action but Loop Time is on", QaSeverity.Major, null));
                string family = ClipRules.Family(file) ?? ClipRules.Family(clip.name);
                if (family == null || kind == ClipLoopKind.Unknown) continue;
                string key = family + " " + (kind == ClipLoopKind.Loop ? "looping" : "one-shot");
                List<ClipRef> list;
                if (!families.TryGetValue(key, out list)) { list = new List<ClipRef>(); families.Add(key, list); }
                list.Add(new ClipRef
                {
                    Path = path, Name = clip.name,
                    Signature = ClipRules.Signature(clip.lockRootRotation, clip.lockRootHeightY, clip.lockRootPositionXZ,
                        clip.keepOriginalOrientation, clip.keepOriginalPositionY, clip.keepOriginalPositionXZ)
                });
            }
        }

        static void Families(Dictionary<string, List<ClipRef>> families, List<QaIssue> issues)
        {
            var keys = new List<string>(families.Keys);
            keys.Sort(System.StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var list = families[key];
                var items = new List<KeyValuePair<string, string>>();
                for (int i = 0; i < list.Count; i++) items.Add(new KeyValuePair<string, string>(i.ToString(), list[i].Signature));
                string majority;
                foreach (var o in ClipRules.Outliers(items, out majority))
                {
                    var c = list[int.Parse(o.Key)];
                    issues.Add(QaRules.AnimRootMotion.Issue(c.Path, 0, c.Name, key + " clip differs from its family: " + ClipRules.Describe(c.Signature, majority), QaSeverity.Major, null));
                }
            }
        }
    }
}
