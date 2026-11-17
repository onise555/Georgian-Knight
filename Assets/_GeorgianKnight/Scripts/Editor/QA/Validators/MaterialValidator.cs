using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// MAT.* rules: broken/legacy shaders, empty slots next to matching textures, physically implausible PBR values
    /// (metallic band, dark metals, smoothness by material class), unjustified emission and mismatched map resolutions.
    /// Albedo brightness and metal-map means are measured on the GPU (blit + readback), so they work on compressed,
    /// non-readable textures.
    /// </summary>
    public sealed class MaterialValidator : IValidator
    {
        static readonly string[] LegacyPrefixes =
        { "Standard", "Legacy Shaders/", "Mobile/", "Nature/", "Autodesk Interactive", "Particles/Standard", "VR/", "Bumped", "Diffuse", "Specular" };

        readonly Dictionary<string, Vector3> averages = new Dictionary<string, Vector3>();

        public string Id { get { return "material"; } }
        public string Category { get { return QaCategory.Material; } }

        public IEnumerable<QaIssue> Run(QaContext ctx)
        {
            var paths = ctx.FindAssets("t:Material");
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (i % 10 == 0) ctx.Progress(path, i / (float)paths.Count);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) continue;
                var shader = mat.shader;
                if (shader == null || shader.name == "Hidden/InternalErrorShader" || ShaderUtil.ShaderHasError(shader))
                {
                    yield return QaRules.MatShaderError.Issue(path, 0, "shader " + (shader == null ? "missing" : "'" + shader.name + "' is broken"));
                    continue;
                }
                if (IsLegacy(shader.name))
                {
                    yield return QaRules.MatLegacyShader.Issue(path, 0, "uses built-in pipeline shader '" + shader.name + "'");
                    continue;
                }
                if (!shader.name.StartsWith("Universal Render Pipeline/", System.StringComparison.Ordinal) || shader.name.Contains("/Terrain/")) continue;
                foreach (var issue in CheckLit(ctx, path, mat)) yield return issue;
            }
        }

        static bool IsLegacy(string shaderName)
        {
            foreach (var p in LegacyPrefixes)
                if (shaderName == p || shaderName.StartsWith(p, System.StringComparison.Ordinal)) return true;
            return false;
        }

        IEnumerable<QaIssue> CheckLit(QaContext ctx, string path, Material mat)
        {
            var lim = ctx.Settings.material;
            var issues = new List<QaIssue>();
            Slots(path, mat, issues);
            Pbr(path, mat, lim, issues);
            float lum, metal;
            if (Albedo(path, mat, lim, issues, out lum) && Metalness(mat, out metal) && metal >= 0.5f && lum < lim.metalAlbedoMin)
                issues.Add(QaRules.MatMetalAlbedo.Issue(path, 0, "metal (metallic " + metal.ToString("0.00") + ") with albedo luminance " + lum.ToString("0") +
                    " sRGB (metals ≥ " + lim.metalAlbedoMin.ToString("0") + ")"));
            SmoothnessClass(path, mat, lim, issues);
            if (mat.IsKeywordEnabled("_EMISSION") && mat.HasProperty("_EmissionColor") && mat.GetColor("_EmissionColor").maxColorComponent > 0.01f &&
                !QaUnity.NameHasToken(Path.GetFileNameWithoutExtension(path), lim.emitterTokens))
                issues.Add(QaRules.MatEmissionUnjustified.Issue(path, 0, "emission enabled (colour " + mat.GetColor("_EmissionColor") + ") on a non-emitter"));
            Texels(path, mat, lim, issues);
            return issues;
        }

        /// <summary>Empty Base/Normal slots although a matching texture sits next to the material.</summary>
        static void Slots(string path, Material mat, List<QaIssue> issues)
        {
            if (mat.HasProperty("_BaseMap") && mat.GetTexture("_BaseMap") == null)
            {
                string candidate = FindNearbyTexture(path, TextureRole.Albedo);
                if (candidate != null) issues.Add(QaRules.MatMissingBaseMap.Issue(path, 0, "Base Map is empty but " + Path.GetFileName(candidate) + " sits next to it"));
            }
            if (mat.HasProperty("_BumpMap") && mat.GetTexture("_BumpMap") == null)
            {
                string candidate = FindNearbyTexture(path, TextureRole.Normal);
                if (candidate != null) issues.Add(QaRules.MatMissingNormalMap.Issue(path, 0, "Normal Map is empty but " + Path.GetFileName(candidate) + " exists"));
            }
        }

        static void Pbr(string path, Material mat, QaSettings.MaterialLimits lim, List<QaIssue> issues)
        {
            bool metalMap = mat.HasProperty("_MetallicGlossMap") && mat.GetTexture("_MetallicGlossMap") != null;
            if (metalMap || !mat.HasProperty("_Smoothness") || !mat.HasProperty("_Metallic")) return;
            float smooth = mat.GetFloat("_Smoothness"), metal = mat.GetFloat("_Metallic");
            if (smooth > lim.smoothnessMax && metal < 0.5f)
                issues.Add(QaRules.MatSmoothnessRange.Issue(path, 0, "dielectric with smoothness " + smooth.ToString("0.00") + " (looks wet/plastic)"));
            if (metal > lim.metallicLow && metal < lim.metallicHigh)
                issues.Add(QaRules.MatMetallicRange.Issue(path, 0, "constant metallic " + metal.ToString("0.00") + " (neither metal nor dielectric)"));
        }

        /// <summary>Average albedo (texture × base colour) inside the PBR luminance range; opaque, non-exempt materials only.
        /// Returns false when nothing was measured.</summary>
        bool Albedo(string path, Material mat, QaSettings.MaterialLimits lim, List<QaIssue> issues, out float lum)
        {
            lum = 0f;
            if (QaUnity.NameHasToken(Path.GetFileNameWithoutExtension(path), lim.albedoExempt) || !mat.HasProperty("_BaseColor") || mat.renderQueue >= 2450) return false;
            if (!QaUnity.HasGpu) return false;   // no GPU readback in -nographics runs
            Texture baseMap = mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : null;
            Color tint = mat.GetColor("_BaseColor");
            Vector3 avg;
            bool measured = baseMap == null ? Fill(out avg, 255f) : Average(baseMap, false, out avg);
            if (!measured) return false;
            lum = PixelMath.AlbedoLuminance(avg.x, avg.y, avg.z, tint.r, tint.g, tint.b);
            if (lum < lim.albedoMin || lum > lim.albedoMax)
                issues.Add(QaRules.MatAlbedoRange.Issue(path, 0, "average albedo luminance " + lum.ToString("0") + " sRGB (PBR range " +
                    lim.albedoMin.ToString("0") + "–" + lim.albedoMax.ToString("0") + ")" + (baseMap == null ? " from Base Color only" : "")));
            return true;
        }

        /// <summary>Metalness the shader uses: the metal map's mean red channel when there is one, else the _Metallic scalar.</summary>
        bool Metalness(Material mat, out float metal)
        {
            metal = 0f;
            var map = mat.HasProperty("_MetallicGlossMap") ? mat.GetTexture("_MetallicGlossMap") : null;
            if (map != null)
            {
                Vector3 avg;
                if (!Average(map, true, out avg)) return false;
                metal = avg.x / 255f;
                return true;
            }
            if (!mat.HasProperty("_Metallic")) return false;
            metal = mat.GetFloat("_Metallic");
            return true;
        }

        /// <summary>Scalar smoothness against the class range named by the material (wool, leather, iron …); maps are not judged here.</summary>
        static void SmoothnessClass(string path, Material mat, QaSettings.MaterialLimits lim, List<QaIssue> issues)
        {
            if (!mat.HasProperty("_Smoothness") || (mat.HasProperty("_MetallicGlossMap") && mat.GetTexture("_MetallicGlossMap") != null)) return;
            var band = AssetMath.TokenClass(Path.GetFileNameWithoutExtension(path), lim.smoothnessClasses);
            if (band == null) return;
            float smooth = mat.GetFloat("_Smoothness");
            if (smooth < band.min || smooth > band.max)
                issues.Add(QaRules.MatSmoothnessByClass.Issue(path, 0, "smoothness " + smooth.ToString("0.00") + " for " + string.Join("/", band.tokens.ToArray()) +
                    " (" + band.min.ToString("0.00") + "–" + band.max.ToString("0.00") + ")"));
        }

        static void Texels(string path, Material mat, QaSettings.MaterialLimits lim, List<QaIssue> issues)
        {
            int min = int.MaxValue, max = 0;
            string minName = "", maxName = "";
            foreach (var prop in new[] { "_BaseMap", "_BumpMap", "_MetallicGlossMap", "_OcclusionMap", "_MaskMap" })
            {
                var t = mat.HasProperty(prop) ? mat.GetTexture(prop) : null;
                if (t == null) continue;
                int s = Mathf.Max(t.width, t.height);
                if (s < min) { min = s; minName = prop; }
                if (s > max) { max = s; maxName = prop; }
            }
            if (max > 0 && max >= min * lim.texelRatioMax)
                issues.Add(QaRules.MatTexelMismatch.Issue(path, 0, maxName + " is " + max + " px but " + minName + " only " + min + " px"));
        }

        static bool Fill(out Vector3 v, float value) { v = new Vector3(value, value, value); return true; }

        /// <summary>Alpha-weighted average colour (0..255) of a texture via GPU downsample + readback, sRGB-encoded for colour
        /// maps or raw for data maps (<paramref name="linear"/>); cached per asset.</summary>
        bool Average(Texture tex, bool linear, out Vector3 avg)
        {
            string key = AssetDatabase.GetAssetPath(tex) + "#" + tex.name + (linear ? "#lin" : "");
            if (averages.TryGetValue(key, out avg)) return true;
            const int size = 128;
            var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            var read = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                read.Apply();
                float r, g, b;
                if (linear)
                {
                    // data maps: plain mean (alpha holds smoothness, it must not weight the metal channel)
                    if (!AssetMath.ChannelMeans(read.GetRawTextureData(), out r, out g, out b)) return false;
                    r *= 255f; g *= 255f; b *= 255f;
                }
                else if (!PixelMath.AverageColor(read.GetRawTextureData(), out r, out g, out b)) return false;
                avg = new Vector3(r, g, b);
                averages[key] = avg;
                return true;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                Object.DestroyImmediate(read);
            }
        }

        /// <summary>A texture with the given role whose name stem matches the material, in its folder or a Textures folder nearby.</summary>
        static string FindNearbyTexture(string materialPath, TextureRole role)
        {
            string stem = TextureNaming.Stem(materialPath);
            if (stem.Length == 0) return null;
            string dir = Path.GetDirectoryName(materialPath).Replace('\\', '/');
            string parent = Path.GetDirectoryName(dir);
            var folders = new List<string> { dir, dir + "/Textures" };
            if (!string.IsNullOrEmpty(parent)) { folders.Add(parent.Replace('\\', '/')); folders.Add(parent.Replace('\\', '/') + "/Textures"); }
            foreach (var f in folders)
            {
                if (!AssetDatabase.IsValidFolder(f)) continue;
                foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { f }))
                {
                    string p = AssetDatabase.GUIDToAssetPath(guid);
                    if (Path.GetDirectoryName(p).Replace('\\', '/') != f) continue;
                    string ts = TextureNaming.Stem(p);
                    if (TextureNaming.Classify(p) != role || ts.Length == 0) continue;
                    if (ts == stem || ts.StartsWith(stem + "_", System.StringComparison.Ordinal) || stem.StartsWith(ts + "_", System.StringComparison.Ordinal)) return p;
                }
            }
            return null;
        }
    }
}
