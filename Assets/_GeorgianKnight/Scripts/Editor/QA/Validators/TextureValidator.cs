using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// TEX.* import rules for every Texture2D under the asset root. A texture's role comes from the material/terrain slot
    /// that uses it (most reliable) and falls back to its file-name suffix; data-map rules are Critical only for textures
    /// that something actually renders with (unreferenced packing sources are reported as Minor). Normal-map bias and
    /// grey metal maps are measured on the source image (PNG / JPG, read from disk as the Asset Gate's tools do).
    /// </summary>
    public sealed class TextureValidator : IValidator
    {
        public string Id { get { return "texture"; } }
        public string Category { get { return QaCategory.Texture; } }

        /// <summary>What the rules need to know about one texture.</summary>
        sealed class Tex
        {
            public string Path, Size;
            public TextureImporter Importer;
            public int Width, Height, Effective, MaxSize;   // MaxSize: the PC (Standalone) import limit
            public TextureRole Role;
            public bool Referenced, Is3D, Hero;
        }

        public IEnumerable<QaIssue> Run(QaContext ctx)
        {
            var issues = new List<QaIssue>();
            var paths = ctx.FindAssets("t:Texture2D");
            bool forcedAniso = QualitySettings.anisotropicFiltering == AnisotropicFiltering.ForceEnable;
            for (int i = 0; i < paths.Count; i++)
            {
                if (i % 20 == 0) ctx.Progress(paths[i], i / (float)paths.Count);
                var t = Describe(ctx, paths[i]);
                if (t == null) continue;
                Size(t, ctx.Settings.texture, issues);
                Roles(ctx, t, issues);
                Import(t, ctx.Settings.texture, forcedAniso, issues);
                Content(ctx, t, issues);
                HeroCap(t, ctx.Settings.model.heroNames, issues);
            }
            return issues;
        }

        /// <summary>TEX.NORMAL_BIAS on normal maps and TEX.METAL_GREYS on metal maps, from the source pixels.</summary>
        static void Content(QaContext ctx, Tex t, List<QaIssue> issues)
        {
            var lim = ctx.Settings.texture;
            var sev = t.Referenced ? QaSeverity.Major : QaSeverity.Minor;
            bool normal = t.Role == TextureRole.Normal && !t.Importer.convertToNormalmap;
            string lower = Path.GetFileNameWithoutExtension(t.Path).ToLowerInvariant();
            bool packed = lower.EndsWith("_arm") || lower.EndsWith("_orm") || lower.Contains("_arm_") || lower.Contains("_orm_");
            bool metal = !packed && (ctx.MetalMaps.Contains(t.Path) || (t.Role == TextureRole.Data && lower.Contains("metal")));
            if (!normal && !metal) return;
            var px = SourceSample(t.Path, lim.analysisSamples);
            if (px == null) return;   // not a PNG / JPG source (TGA, PSD, EXR): not measured
            if (normal)
            {
                float r, g, b;
                if (AssetMath.ChannelMeans(px, out r, out g, out b) && AssetMath.NormalBiased(r, g, b, lim.normalMeanTolerance, lim.normalBlueMin))
                    issues.Add(QaRules.TexNormalBias.Issue(t.Path, 0, "", "mean R " + r.ToString("0.000") + ", G " + g.ToString("0.000") + ", B " + b.ToString("0.000") +
                        " (R, G 0.5 ± " + lim.normalMeanTolerance.ToString("0.00") + ", B ≥ " + lim.normalBlueMin.ToString("0.00") + ")" + Users(ctx, t.Path), sev, null));
            }
            if (metal)
            {
                float share = AssetMath.BandShare(px, 0, lim.metalGreyLow, lim.metalGreyHigh);
                if (share > lim.metalGreyShareMax)
                    issues.Add(QaRules.TexMetalGreys.Issue(t.Path, 0, "", (share * 100f).ToString("0") + " % of metal texels between " + lim.metalGreyLow.ToString("0.00") + " and " +
                        lim.metalGreyHigh.ToString("0.00") + " (max " + (lim.metalGreyShareMax * 100f).ToString("0") + " %)" + Users(ctx, t.Path), sev, null));
            }
        }

        /// <summary>Evenly strided RGBA32 samples of a PNG / JPG source file (raw stored values), or null for other formats.</summary>
        static byte[] SourceSample(string path, int maxSamples)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if ((ext != ".png" && ext != ".jpg" && ext != ".jpeg") || !File.Exists(path)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!tex.LoadImage(File.ReadAllBytes(path), false)) return null;
                var px = tex.GetPixels32();
                int step = Mathf.Max(1, px.Length / Mathf.Max(1, maxSamples));
                var buf = new byte[(px.Length / step) * 4];
                for (int i = 0, o = 0; o + 3 < buf.Length; i += step, o += 4)
                {
                    buf[o] = px[i].r; buf[o + 1] = px[i].g; buf[o + 2] = px[i].b; buf[o + 3] = px[i].a;
                }
                return buf;
            }
            finally { Object.DestroyImmediate(tex); }
        }

        /// <summary>TEX.HERO_CAPPED: a hero texture whose source is larger than its import size (below 4K).</summary>
        static void HeroCap(Tex t, IList<string> heroNames, List<QaIssue> issues)
        {
            int source = Mathf.Max(t.Width, t.Height), max = t.MaxSize;
            if (source <= max || max >= 4096 || !t.Is3D) return;
            foreach (var h in heroNames)
                if (!string.IsNullOrEmpty(h) && t.Path.Contains("/" + h))
                {
                    issues.Add(QaRules.TexHeroCapped.Issue(t.Path, 0, "hero texture with a " + t.Size + " source imports at max " + max + " px"));
                    return;
                }
        }

        static Tex Describe(QaContext ctx, string path)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null || path.Contains("/Fonts/")) return null;
            var t = new Tex { Path = path, Importer = ti };
            ti.GetSourceTextureWidthAndHeight(out t.Width, out t.Height);
            t.Size = t.Width + "×" + t.Height;
            var pc = ti.GetPlatformTextureSettings("Standalone");
            t.MaxSize = pc != null && pc.overridden ? pc.maxTextureSize : ti.maxTextureSize;
            t.Effective = Mathf.Min(Mathf.Max(t.Width, t.Height), t.MaxSize);
            t.Referenced = ctx.TextureUsage.TryGetValue(path, out t.Role);
            if (!t.Referenced || t.Role == TextureRole.Unknown) t.Role = TextureNaming.Classify(Path.GetFileName(path));
            bool ui = ti.textureType == TextureImporterType.Sprite || ti.textureType == TextureImporterType.GUI || path.Contains("/UI/");
            t.Is3D = !ui && (ti.textureType == TextureImporterType.Default || ti.textureType == TextureImporterType.NormalMap);
            t.Hero = path.Contains("/Art/Characters/") || path.Contains("/Art/Weapons/");
            return t;
        }

        static void Size(Tex t, QaSettings.TextureLimits lim, List<QaIssue> issues)
        {
            var ti = t.Importer;
            if (t.Is3D && ti.textureCompression != TextureImporterCompression.Uncompressed && ti.npotScale == TextureImporterNPOTScale.None &&
                (!TextureNaming.IsPowerOfTwo(t.Width) || !TextureNaming.IsPowerOfTwo(t.Height)))
                issues.Add(QaRules.TexNpot.Issue(t.Path, 0, "non-power-of-two " + t.Size + " with compression and NPOT scaling off"));
            if (t.Effective > lim.maxSize)
                issues.Add(QaRules.TexTooLarge.Issue(t.Path, 0, "", "imports at " + t.Effective + " px (" + t.Size + " source, max size " + t.MaxSize + ")",
                    t.Effective > lim.maxSize * 2 ? QaSeverity.Critical : QaSeverity.Major, null));
            if (t.Hero && t.Is3D && (t.Role == TextureRole.Albedo || t.Role == TextureRole.Normal) && t.Effective < lim.heroMinSize)
                issues.Add(QaRules.TexHeroLowRes.Issue(t.Path, 0, t.Role + " map of a hero asset imports at " + t.Effective + " px (min " + lim.heroMinSize + ")"));
            if (ti.textureCompression == TextureImporterCompression.Uncompressed && t.Effective >= lim.uncompressedMinSize)
                issues.Add(QaRules.TexUncompressed.Issue(t.Path, 0, "uncompressed " + t.Effective + " px texture (~" + (t.Effective * t.Effective * 4 / (1024 * 1024)) + " MB)"));
        }

        /// <summary>Colour-space and texture-type expectations of the texture's role (Critical only when something renders with it).</summary>
        static void Roles(QaContext ctx, Tex t, List<QaIssue> issues)
        {
            var ti = t.Importer;
            var sev = t.Referenced ? QaSeverity.Critical : QaSeverity.Minor;
            if (t.Role == TextureRole.Normal && ti.textureType != TextureImporterType.NormalMap)
                issues.Add(QaRules.TexNormalNotMarked.Issue(t.Path, 0, "", "normal map imported as " + ti.textureType + Users(ctx, t.Path), sev, null));
            if (t.Role == TextureRole.Data && ti.sRGBTexture)
                issues.Add(QaRules.TexSrgbData.Issue(t.Path, 0, "", "data map (mask/roughness/AO/height) imported as sRGB" + Users(ctx, t.Path), sev, null));
            if (t.Role == TextureRole.Albedo && ti.textureType == TextureImporterType.Default && !ti.sRGBTexture)
                issues.Add(QaRules.TexSrgbAlbedo.Issue(t.Path, 0, "albedo imported as linear (sRGB off)" + Users(ctx, t.Path)));
        }

        static void Import(Tex t, QaSettings.TextureLimits lim, bool forcedAniso, List<QaIssue> issues)
        {
            var ti = t.Importer;
            if (t.Is3D && !ti.mipmapEnabled)
                issues.Add(QaRules.TexNoMipmaps.Issue(t.Path, 0, "3D texture without mipmaps"));
            bool surface = t.Role == TextureRole.Albedo || t.Role == TextureRole.Normal;
            if (!forcedAniso && t.Is3D && surface && ti.mipmapEnabled && ti.anisoLevel < lim.minAniso &&
                QaUnity.NameHasToken(Path.GetFileNameWithoutExtension(t.Path), lim.groundTokens))
                issues.Add(QaRules.TexAniso.Issue(t.Path, 0, "ground texture with aniso level " + ti.anisoLevel + " (min " + lim.minAniso + ")"));
            if (ti.isReadable && !CodeLintOptions.AnyGlob(lim.readWriteAllowed, t.Path))
                issues.Add(QaRules.TexReadWrite.Issue(t.Path, 0, "Read/Write enabled (" + t.Size + ")"));
        }

        static string Users(QaContext ctx, string path)
        {
            List<string> users;
            if (!ctx.TextureUsers.TryGetValue(path, out users) || users.Count == 0) return " (not used by any material)";
            return " (used by " + Path.GetFileNameWithoutExtension(users[0]) + (users.Count > 1 ? " +" + (users.Count - 1) : "") + ")";
        }
    }
}
