using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// Checks on art instances inside prefabs and scenes, shared by the prefab and scene validators: models from Art/
    /// rescaled after import (SCALE.ART_RESCALED), test / placeholder assets (SCENE.TEST_ASSET), texel density of the
    /// materials a prefab assigns to character and weapon models (MODEL.TEXEL_DENSITY) and the spread between its parts
    /// (MODEL.TEXEL_NEIGHBOUR). Tier bands are shared with the model validator.
    /// </summary>
    public static class QaArt
    {
        const string ArtRoot = QaContext.AssetRoot + "/Art/";

        /// <summary>Every model instance from Art/ under <paramref name="root"/> whose instance root is not at scale 1 (± tolerance).</summary>
        public static IEnumerable<QaIssue> Rescaled(string path, GameObject root, float tolerance)
        {
            var reported = new HashSet<Transform>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = QaUnity.MeshOf(r);
                if (mesh == null) continue;
                string meshPath = AssetDatabase.GetAssetPath(mesh);
                if (!meshPath.StartsWith(ArtRoot, System.StringComparison.Ordinal)) continue;
                var top = InstanceRoot(r.transform, meshPath) ?? r.transform;
                if (!reported.Add(top)) continue;
                var s = top.localScale;
                if (Mathf.Abs(s.x - 1f) > tolerance || Mathf.Abs(s.y - 1f) > tolerance || Mathf.Abs(s.z - 1f) > tolerance)
                    yield return QaRules.ScaleArtRescaled.Issue(path, 0, QaUnity.HierarchyPath(top), Path.GetFileName(meshPath) + " is scaled " +
                        s.x.ToString("0.###") + " / " + s.y.ToString("0.###") + " / " + s.z.ToString("0.###") + " here (must be 1: fix the source size)", QaSeverity.Major, null);
            }
        }

        /// <summary>The topmost ancestor (or self) that is part of the same model instance, or null when the mesh is not instanced.</summary>
        static Transform InstanceRoot(Transform t, string modelPath)
        {
            Transform top = null;
            for (var p = t; p != null; p = p.parent)
            {
                var src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(p.gameObject);
                if (src != null && AssetDatabase.GetAssetPath(src) == modelPath) top = p;
                else if (top != null) break;
            }
            return top;
        }

        /// <summary>Rendered objects under <paramref name="root"/> whose name, mesh or source asset marks a test / placeholder.</summary>
        public static IEnumerable<QaIssue> TestAssets(string path, GameObject root)
        {
            var reported = new HashSet<Transform>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || r.CompareTag("EditorOnly")) continue;
                var mesh = QaUnity.MeshOf(r);
                var instance = PrefabUtility.GetNearestPrefabInstanceRoot(r.gameObject);
                var source = instance != null ? PrefabUtility.GetCorrespondingObjectFromOriginalSource(instance) : null;
                string sourceName = source != null ? Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(source)) : "";
                string hit = null;
                for (var p = r.transform; p != null && hit == null; p = p.parent) if (AssetMath.IsTestAssetName(p.name)) hit = "object '" + p.name + "'";
                if (hit == null && mesh != null && AssetMath.IsTestAssetName(mesh.name)) hit = "mesh '" + mesh.name + "'";
                if (hit == null && AssetMath.IsTestAssetName(sourceName)) hit = "asset '" + sourceName + "'";
                if (hit == null) continue;
                var key = instance != null ? instance.transform : r.transform;
                if (!reported.Add(key)) continue;
                yield return QaRules.SceneTestAsset.Issue(path, 0, QaUnity.HierarchyPath(key), "renders a test / placeholder " + hit, QaSeverity.Major, null);
            }
        }

        /// <summary>The allowed px/m band of a class ("Hero", "Npc", "Weapon" …), faces of characters widened; false for an unknown class.</summary>
        public static bool TexelBand(QaSettings settings, string kind, string material, out float low, out float high)
        {
            low = 0f; high = float.MaxValue;
            var lim = settings.texel;
            QaSettings.TexelTier tier = null;
            foreach (var t in lim.tiers) if (t.kind == kind) { tier = t; break; }
            if (tier == null) return false;
            bool face = (kind == "Hero" || kind == "Npc") && QaUnity.NameHasToken(material, lim.faceTokens);
            AssetMath.TexelBand(tier.min, face ? lim.faceMax : tier.max, lim.tolerance, out low, out high);
            return true;
        }

        /// <summary>Class of the model a mesh comes from, for the prefab-level density check: Hero / Npc / Shield / Weapon, or "".</summary>
        static string ModelKind(string meshPath, QaSettings settings)
        {
            if (meshPath.Contains("/Characters/"))
            {
                foreach (var h in settings.model.heroNames)
                    if (!string.IsNullOrEmpty(h) && (meshPath.Contains("/" + h + "/") || Path.GetFileName(meshPath).StartsWith(h, System.StringComparison.OrdinalIgnoreCase))) return "Hero";
                return "Npc";
            }
            if (!meshPath.Contains("/Weapons/")) return "";
            return QaUnity.NameHasToken(Path.GetFileNameWithoutExtension(meshPath), settings.model.shieldTokens) ? "Shield" : "Weapon";
        }

        /// <summary>Texel density of the materials one prefab assigns, against their class, and the spread between its parts.</summary>
        public static IEnumerable<QaIssue> Texels(QaContext ctx, string path, GameObject root)
        {
            TexelSample lo = new TexelSample(), hi = new TexelSample();
            bool any = false;
            var reported = new HashSet<string>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = QaUnity.MeshOf(r);
                if (mesh == null) continue;
                string meshPath = AssetDatabase.GetAssetPath(mesh);
                string kind = ModelKind(meshPath, ctx.Settings);
                var toRoot = root.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
                foreach (var s in ctx.Meshes.Densities(r, toRoot, ctx.Settings))
                {
                    if (!any || s.Density < lo.Density) lo = s;
                    if (!any || s.Density > hi.Density) hi = s;
                    any = true;
                    float low, high;
                    // the model validator already judges the model's own materials; here only the ones the prefab assigns
                    if (kind.Length == 0 || s.MaterialPath == meshPath || !reported.Add(s.Material) || !TexelBand(ctx.Settings, kind, s.Material, out low, out high)) continue;
                    if (s.Density < low || s.Density > high)
                        yield return QaRules.ModelTexelDensity.Issue(path, 0, QaUnity.HierarchyPath(r.transform), s.Material + ": " + s.Density.ToString("N0") + " px/m with " +
                            s.Texture + " (" + kind + " tier " + low.ToString("N0") + "–" + (high >= float.MaxValue ? "∞" : high.ToString("N0")) + " px/m, as placed in this prefab)", QaSeverity.Major, null);
                }
            }
            if (any && lo.Density > 0f && hi.Density / lo.Density > ctx.Settings.texel.neighbourRatio)
                yield return QaRules.ModelTexelNeighbour.Issue(path, 0, hi.Material + " " + hi.Density.ToString("N0") + " px/m vs " + lo.Material + " " +
                    lo.Density.ToString("N0") + " px/m (" + (hi.Density / lo.Density).ToString("0.0") + "×, max " + ctx.Settings.texel.neighbourRatio.ToString("0") + "×)");
        }
    }
}
