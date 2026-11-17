using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// TERRAIN.TEXEL and SCALE.TILE_SIZE on every TerrainLayer: the diffuse map's px/m at the layer's tile size (near layers
    /// need QaSettings.terrain.minTexel), and the tile size against the scanned set's real size. The real size comes from
    /// a sidecar "&lt;texture&gt;.size.txt" (one number, metres) or Docs/qa_config.json terrain.tileSources; a layer whose
    /// source size is unknown is reported as Info, so it stays visible.
    /// </summary>
    public sealed class TerrainLayerValidator : IValidator
    {
        public string Id { get { return "terrain"; } }
        public string Category { get { return QaCategory.Texture; } }

        public IEnumerable<QaIssue> Run(QaContext ctx)
        {
            var lim = ctx.Settings.terrain;
            foreach (var path in ctx.FindAssets("t:TerrainLayer"))
            {
                var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
                if (layer == null || layer.diffuseTexture == null) continue;
                var tex = layer.diffuseTexture;
                string texPath = AssetDatabase.GetAssetPath(tex);
                float tile = Mathf.Max(layer.tileSize.x, layer.tileSize.y);
                if (tile <= 0f) continue;
                float density = tex.width / tile;
                bool far = QaUnity.NameHasToken(Path.GetFileNameWithoutExtension(path), lim.farTokens);
                if (!far && density < lim.minTexel)
                    yield return QaRules.TerrainTexel.Issue(path, 0, Path.GetFileName(texPath) + " at " + tex.width + " px over a " + tile.ToString("0.##") + " m tile = " +
                        density.ToString("0") + " px/m (near layers ≥ " + lim.minTexel.ToString("0") + "; name a far / macro layer with a far token)");
                float real;
                if (!RealSize(texPath, lim, out real))
                {
                    yield return QaRules.ScaleTileSize.Issue(path, 0, "", "real size of " + Path.GetFileName(texPath) + " unknown: add " + Path.GetFileName(texPath) +
                        ".size.txt or a terrain.tileSources entry in Docs/qa_config.json", QaSeverity.Info, null);
                    continue;
                }
                float error = tile / real - 1f;
                if (Mathf.Abs(error) > lim.tileTolerance)
                    yield return QaRules.ScaleTileSize.Issue(path, 0, "tile " + tile.ToString("0.##") + " m on a " + real.ToString("0.##") + " m scan (" +
                        (error > 0 ? "+" : "") + (error * 100f).ToString("0") + " %, allowed ±" + (lim.tileTolerance * 100f).ToString("0") + " %)");
            }
        }

        static bool RealSize(string texPath, QaSettings.TerrainLimits lim, out float meters)
        {
            meters = 0f;
            string sidecar = texPath + ".size.txt";
            if (File.Exists(sidecar) &&
                float.TryParse(File.ReadAllText(sidecar).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out meters) && meters > 0f)
                return true;
            return AssetMath.Lookup(Path.GetFileName(texPath), lim.tileSources, out meters) && meters > 0f;
        }
    }
}
