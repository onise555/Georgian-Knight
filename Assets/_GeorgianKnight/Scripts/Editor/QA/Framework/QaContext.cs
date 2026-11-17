using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GeorgianKnight.Core;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// Shared state of one QA run: project paths, settings (with Docs/qa_config.json overrides), asset queries and lazily
    /// built caches that several validators need (texture slot usage, all definitions, localization keys used in code).
    /// </summary>
    public sealed class QaContext
    {
        public const string AssetRoot = "Assets/_GeorgianKnight";
        public const string ScriptsRoot = AssetRoot + "/Scripts";

        public readonly string ProjectRoot;
        public readonly QaSettings Settings = new QaSettings();
        public readonly CodeLintOptions CodeOptions = new CodeLintOptions();
        public readonly List<QaIssue> SetupProblems = new List<QaIssue>();
        public string CurrentValidator = "";
        /// <summary>Mesh data read once per run, shared by the model and prefab validators.</summary>
        public readonly QaMesh Meshes = new QaMesh();

        List<LocKeyRef> codeLocKeys;
        Dictionary<string, TextureRole> textureUsage;
        Dictionary<string, List<string>> textureUsers;
        HashSet<string> metalMaps;
        List<KeyValuePair<string, Definition>> definitions;

        public QaContext()
        {
            ProjectRoot = Path.GetDirectoryName(Application.dataPath);
            CodeOptions.ScriptsRoot = ScriptsRoot;
            string cfg = DocsPath("qa_config.json");
            if (!File.Exists(cfg)) return;
            try { JsonUtility.FromJsonOverwrite(File.ReadAllText(cfg, Encoding.UTF8), Settings); }
            catch (Exception e)
            {
                SetupProblems.Add(QaRules.MetaConfigInvalid.Issue("Docs/qa_config.json", 0, "", e.Message, QaRules.MetaConfigInvalid.Severity, null));
            }
        }

        public string DocsPath(string file) { return Path.Combine(Path.Combine(ProjectRoot, "Docs"), file); }

        /// <summary>Project-relative asset paths matching an AssetDatabase filter below <paramref name="folder"/>, sorted.</summary>
        public List<string> FindAssets(string filter, string folder = AssetRoot)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (!AssetDatabase.IsValidFolder(folder)) return new List<string>();
            foreach (var g in AssetDatabase.FindAssets(filter, new[] { folder })) set.Add(AssetDatabase.GUIDToAssetPath(g));
            var list = new List<string>(set);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        public void Progress(string info, float t)
        {
            EditorUtility.DisplayProgressBar("QA · " + CurrentValidator, info, Mathf.Clamp01(t));
        }

        // ------------------------------------------------------------------------------------------------ caches

        /// <summary>Localization keys referenced from code (filled by the code validator, or computed on demand).</summary>
        public IList<LocKeyRef> CodeLocKeys
        {
            get
            {
                if (codeLocKeys == null)
                {
                    var linter = new CodeLinter(CodeOptions);
                    linter.Lint(ReadScripts());
                    codeLocKeys = linter.LocKeys;
                }
                return codeLocKeys;
            }
        }

        public void SetCodeLocKeys(List<LocKeyRef> keys) { codeLocKeys = keys; }

        /// <summary>All C# files below Assets/_GeorgianKnight/Scripts as (path, text).</summary>
        public List<KeyValuePair<string, string>> ReadScripts()
        {
            var files = new List<KeyValuePair<string, string>>();
            string dir = Path.Combine(ProjectRoot, ScriptsRoot);
            if (!Directory.Exists(dir)) return files;
            var paths = new List<string>(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories));
            paths.Sort(StringComparer.Ordinal);
            foreach (var f in paths)
            {
                string rel = f.Substring(ProjectRoot.Length).Replace('\\', '/').TrimStart('/');
                files.Add(new KeyValuePair<string, string>(rel, File.ReadAllText(f, Encoding.UTF8)));
            }
            return files;
        }

        /// <summary>Texture path → role implied by the material/terrain slots that use it (Unknown if only odd slots).</summary>
        public IDictionary<string, TextureRole> TextureUsage
        {
            get
            {
                if (textureUsage == null) BuildTextureUsage();
                return textureUsage;
            }
        }

        /// <summary>Texture path → materials/terrain layers using it.</summary>
        public IDictionary<string, List<string>> TextureUsers
        {
            get
            {
                if (textureUsers == null) BuildTextureUsage();
                return textureUsers;
            }
        }

        /// <summary>Textures some material uses in its _MetallicGlossMap slot (metal in R, smoothness in A).</summary>
        public ICollection<string> MetalMaps
        {
            get
            {
                if (metalMaps == null) BuildTextureUsage();
                return metalMaps;
            }
        }

        void BuildTextureUsage()
        {
            metalMaps = new HashSet<string>(StringComparer.Ordinal);
            textureUsage = new Dictionary<string, TextureRole>(StringComparer.Ordinal);
            textureUsers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var path in FindAssets("t:Material"))
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) continue;
                foreach (var prop in mat.GetTexturePropertyNames())
                {
                    var tex = mat.GetTexture(prop);
                    Use(tex, TextureNaming.FromSlot(prop), path);
                    if (prop == "_MetallicGlossMap" && tex != null) metalMaps.Add(AssetDatabase.GetAssetPath(tex));
                }
            }
            foreach (var path in FindAssets("t:TerrainLayer"))
            {
                var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
                if (layer == null) continue;
                Use(layer.diffuseTexture, TextureRole.Albedo, path);
                Use(layer.normalMapTexture, TextureRole.Normal, path);
                Use(layer.maskMapTexture, TextureRole.Data, path);
            }
        }

        void Use(Texture tex, TextureRole role, string user)
        {
            if (tex == null) return;
            string p = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(p)) return;
            TextureRole known;
            if (!textureUsage.TryGetValue(p, out known) || known == TextureRole.Unknown) textureUsage[p] = role;
            List<string> users;
            if (!textureUsers.TryGetValue(p, out users)) { users = new List<string>(); textureUsers.Add(p, users); }
            if (!users.Contains(user)) users.Add(user);
        }

        /// <summary>Every Definition asset below the asset root (path, asset), sorted by path.</summary>
        public IList<KeyValuePair<string, Definition>> Definitions
        {
            get
            {
                if (definitions != null) return definitions;
                definitions = new List<KeyValuePair<string, Definition>>();
                foreach (var path in FindAssets("t:Definition"))
                    foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                    {
                        var d = o as Definition;
                        if (d != null) definitions.Add(new KeyValuePair<string, Definition>(path, d));
                    }
                return definitions;
            }
        }
    }
}
