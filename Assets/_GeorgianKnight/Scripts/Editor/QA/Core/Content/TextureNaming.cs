using System;
using System.Collections.Generic;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>What a texture is for; decides sRGB, normal-map and compression expectations.</summary>
    public enum TextureRole { Unknown, Albedo, Normal, Data, Emission }

    /// <summary>
    /// Classifies texture files by naming convention (project: _BaseColor/_Normal/_MetalSmooth/_Mask; Poly Haven:
    /// _diff/_nor_gl/_arm/_rough/_ao/_disp). Material slot usage overrides naming when known (see MaterialSlots).
    /// </summary>
    public static class TextureNaming
    {
        static readonly Dictionary<string, TextureRole> Tokens = new Dictionary<string, TextureRole>(StringComparer.OrdinalIgnoreCase)
        {
            { "basecolor", TextureRole.Albedo }, { "base", TextureRole.Albedo }, { "albedo", TextureRole.Albedo }, { "diffuse", TextureRole.Albedo },
            { "diff", TextureRole.Albedo }, { "color", TextureRole.Albedo }, { "col", TextureRole.Albedo }, { "maintex", TextureRole.Albedo },
            { "d", TextureRole.Albedo }, { "c", TextureRole.Albedo }, { "basemap", TextureRole.Albedo }, { "strands", TextureRole.Albedo },
            { "normal", TextureRole.Normal }, { "normals", TextureRole.Normal }, { "nor", TextureRole.Normal }, { "nrm", TextureRole.Normal },
            { "norm", TextureRole.Normal }, { "normalgl", TextureRole.Normal }, { "normaldx", TextureRole.Normal }, { "n", TextureRole.Normal },
            { "bump", TextureRole.Normal },
            { "metalsmooth", TextureRole.Data }, { "metallicsmoothness", TextureRole.Data }, { "mask", TextureRole.Data }, { "maskmap", TextureRole.Data },
            { "arm", TextureRole.Data }, { "orm", TextureRole.Data }, { "rma", TextureRole.Data }, { "mra", TextureRole.Data },
            { "rough", TextureRole.Data }, { "roughness", TextureRole.Data }, { "ao", TextureRole.Data }, { "occlusion", TextureRole.Data },
            { "metal", TextureRole.Data }, { "metallic", TextureRole.Data }, { "metalness", TextureRole.Data }, { "smoothness", TextureRole.Data },
            { "gloss", TextureRole.Data }, { "spec", TextureRole.Data }, { "specular", TextureRole.Data }, { "height", TextureRole.Data },
            { "disp", TextureRole.Data }, { "displacement", TextureRole.Data }, { "parallax", TextureRole.Data }, { "cavity", TextureRole.Data },
            { "curvature", TextureRole.Data }, { "opacity", TextureRole.Data }, { "alpha", TextureRole.Data }, { "detailmask", TextureRole.Data },
            { "emission", TextureRole.Emission }, { "emissive", TextureRole.Emission }, { "emit", TextureRole.Emission }, { "glow", TextureRole.Emission }
        };

        static readonly HashSet<string> Noise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "1k", "2k", "4k", "8k", "16k", "gl", "dx", "png", "jpg", "tga", "lod0", "lod1", "lod2" };

        /// <summary>Role from the file name (without folder/extension), scanning suffix tokens right to left.</summary>
        public static TextureRole Classify(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return TextureRole.Unknown;
            string name = fileName;
            int slash = name.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0) name = name.Substring(slash + 1);
            int dot = name.LastIndexOf('.');
            if (dot > 0) name = name.Substring(0, dot);
            var parts = name.Split(new[] { '_', '-', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries);
            // "nor_gl" / "Normal_GL": the last meaningful token decides; single-letter tokens only count as suffixes
            for (int i = parts.Length - 1; i >= 1; i--)
            {
                string p = parts[i];
                if (Noise.Contains(p) || IsNumber(p)) continue;
                TextureRole r;
                if (Tokens.TryGetValue(p, out r)) return r;
                if (p.Length > 1) break;
            }
            // CamelCase suffix without separator: "KnightBodyNormal"
            string lower = name.ToLowerInvariant();
            foreach (var kv in Tokens)
                if (kv.Key.Length >= 4 && lower.EndsWith(kv.Key.ToLowerInvariant(), StringComparison.Ordinal)) return kv.Value;
            return TextureRole.Unknown;
        }

        static bool IsNumber(string s)
        {
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return s.Length > 0;
        }

        /// <summary>Role implied by a material slot (URP/Lit property names), or Unknown for other slots.</summary>
        public static TextureRole FromSlot(string property)
        {
            switch (property)
            {
                case "_BaseMap": case "_MainTex": case "_BaseColorMap": case "_Albedo": return TextureRole.Albedo;
                case "_BumpMap": case "_NormalMap": case "_DetailNormalMap": case "_Normal": return TextureRole.Normal;
                case "_MetallicGlossMap": case "_SpecGlossMap": case "_MaskMap": case "_OcclusionMap": case "_ParallaxMap":
                case "_DetailMask": case "_HeightMap": case "_Mask": return TextureRole.Data;
                case "_EmissionMap": case "_EmissiveColorMap": return TextureRole.Emission;
            }
            return TextureRole.Unknown;
        }

        /// <summary>Material/texture name stem used to match textures to materials ("M_Rock_Wall_01" -> "rock_wall_01").</summary>
        public static string Stem(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string n = name;
            int slash = n.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0) n = n.Substring(slash + 1);
            int dot = n.LastIndexOf('.');
            if (dot > 0) n = n.Substring(0, dot);
            var parts = new List<string>(n.ToLowerInvariant().Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            if (parts.Count > 1 && (parts[0] == "m" || parts[0] == "mat" || parts[0] == "t" || parts[0] == "tex")) parts.RemoveAt(0);
            while (parts.Count > 1 && (Tokens.ContainsKey(parts[parts.Count - 1]) || Noise.Contains(parts[parts.Count - 1]) || parts[parts.Count - 1] == "mat")) parts.RemoveAt(parts.Count - 1);
            return string.Join("_", parts.ToArray());
        }

        public static bool IsPowerOfTwo(int v) { return v > 0 && (v & (v - 1)) == 0; }
    }
}
