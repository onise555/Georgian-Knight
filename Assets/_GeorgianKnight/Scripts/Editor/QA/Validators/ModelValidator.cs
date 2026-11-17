using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// MODEL.* and SCALE.WEAPON_CLASS rules for every imported model under the asset root: importer scale, real-world size
    /// by class, vertex streams (normals/tangents/UV0), UV overlap, topology (open / non-manifold edges, loose vertices),
    /// texel density per material, triangle budgets, LODs, faceted weapons, generator signatures, Read/Write and the
    /// humanoid avatar. Classes come from the folder and name tokens (QaSettings.model); heroes from model.heroNames.
    /// </summary>
    public sealed class ModelValidator : IValidator
    {
        enum Kind { Hero, Npc, Weapon, Shield, Banner, Rock, Prop, Environment }

        public string Id { get { return "model"; } }
        public string Category { get { return QaCategory.Model; } }

        public IEnumerable<QaIssue> Run(QaContext ctx)
        {
            var lim = ctx.Settings.model;
            var paths = ctx.FindAssets("t:Model");
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (i % 5 == 0) ctx.Progress(path, i / (float)paths.Count);
                var mi = AssetImporter.GetAtPath(path) as ModelImporter;
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (mi == null || go == null) continue;
                foreach (var issue in Avatar(path, mi)) yield return issue;
                var renderers = go.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) continue;                        // animation-only file
                // clip sources (Mixamo and packs) carry a skin mesh the game never renders: avatar only
                if (path.Contains("/Animations/")) continue;
                var kind = Classify(path, mi, go, lim);
                if (mi.isReadable) yield return QaRules.ModelReadWrite.Issue(path, 0, "mesh Read/Write enabled");
                if (path.StartsWith(QaContext.AssetRoot + "/Art/", System.StringComparison.Ordinal) &&
                    (Mathf.Abs(mi.globalScale - 1f) > lim.importScaleTolerance || !mi.useFileScale))
                    yield return QaRules.ModelImportScale.Issue(path, 0, "importer Scale Factor " + mi.globalScale.ToString("0.######") +
                        (mi.useFileScale ? "" : ", Use File Scale off") + " (the source must be 1 unit = 1 m)");
                foreach (var issue in Scale(path, go, kind, lim)) yield return issue;
                foreach (var issue in Meshes(ctx, path, go, renderers, kind)) yield return issue;
                if (Generated(path, renderers)) yield return QaRules.ModelGeneratedSignature.Issue(path, 0, "single mesh 'output' / material 'bodymat' or a Meshy file name: a generator export");
            }
        }

        Kind Classify(string path, ModelImporter mi, GameObject go, QaSettings.ModelLimits lim)
        {
            string file = Path.GetFileNameWithoutExtension(path);
            bool skinned = go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
            if (path.Contains("/Characters/") || (mi.animationType == ModelImporterAnimationType.Human && skinned))
            {
                foreach (var h in lim.heroNames)
                    if (!string.IsNullOrEmpty(h) && (path.Contains("/" + h + "/") || file.StartsWith(h, System.StringComparison.OrdinalIgnoreCase))) return Kind.Hero;
                return Kind.Npc;
            }
            if (QaUnity.NameHasToken(file, lim.shieldTokens)) return Kind.Shield;
            if (path.Contains("/Weapons/")) return Kind.Weapon;
            if (QaUnity.NameHasToken(file, lim.bannerTokens)) return Kind.Banner;
            if (QaUnity.NameHasToken(file, lim.rockTokens)) return Kind.Rock;
            if (path.Contains("/Props/")) return Kind.Prop;
            return Kind.Environment;
        }

        static IEnumerable<QaIssue> Avatar(string path, ModelImporter mi)
        {
            if (mi.animationType != ModelImporterAnimationType.Human) yield break;
            Avatar avatar = null;
            if (mi.avatarSetup == ModelImporterAvatarSetup.CopyFromOther) avatar = mi.sourceAvatar;
            else
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                    if (o is Avatar) { avatar = (Avatar)o; break; }
            if (avatar == null) yield return QaRules.ModelAvatarInvalid.Issue(path, 0, "humanoid import without an avatar");
            else if (!avatar.isValid || !avatar.isHuman) yield return QaRules.ModelAvatarInvalid.Issue(path, 0, "avatar '" + avatar.name + "' is not a valid humanoid");
        }

        static IEnumerable<QaIssue> Scale(string path, GameObject go, Kind kind, QaSettings.ModelLimits lim)
        {
            Bounds b;
            if (!QaUnity.MeshBounds(go, out b)) yield break;
            float largest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            string dims = b.size.x.ToString("0.00") + "×" + b.size.y.ToString("0.00") + "×" + b.size.z.ToString("0.00") + " m";
            if (kind == Kind.Hero || kind == Kind.Npc)
            {
                if (b.size.y < lim.characterMinHeight || b.size.y > lim.characterMaxHeight)
                    yield return QaRules.ModelScale.Issue(path, 0, "", "character is " + b.size.y.ToString("0.00") + " m tall (" + lim.characterMinHeight + "–" + lim.characterMaxHeight + " m)",
                        QaSeverity.Critical, null);
                yield break;
            }
            if (kind == Kind.Weapon || kind == Kind.Shield)
            {
                var cls = AssetMath.WeaponClass(Path.GetFileNameWithoutExtension(path), Path.GetFileName(Path.GetDirectoryName(path)), lim.weaponClasses);
                if (cls != null)
                {
                    if (largest < cls.min || largest > cls.max)
                        yield return QaRules.ScaleWeaponClass.Issue(path, 0, cls.token + " is " + largest.ToString("0.000") + " m along its longest axis (" +
                            cls.min.ToString("0.00") + "–" + cls.max.ToString("0.00") + " m)");
                }
                else if (largest < lim.weaponMinLength || largest > lim.weaponMaxLength)
                    yield return QaRules.ModelScale.Issue(path, 0, "weapon spans " + dims + " (" + lim.weaponMinLength + "–" + lim.weaponMaxLength + " m, no size class in its name)");
                yield break;
            }
            if (largest < lim.propMinSize || (kind == Kind.Prop && largest > lim.propMaxSize) || largest > lim.propMaxSize * 20f)
                yield return QaRules.ModelScale.Issue(path, 0, kind.ToString().ToLowerInvariant() + " spans " + dims + " — likely a unit/scale-factor error");
        }

        static bool IsLodMesh(Renderer r, Mesh mesh, out int level)
        {
            level = 0;
            var m = System.Text.RegularExpressions.Regex.Match(r.name + " " + mesh.name, @"(?i)_LOD([0-9])");
            if (m.Success) level = m.Groups[1].Value[0] - '0';
            return m.Success;
        }

        IEnumerable<QaIssue> Meshes(QaContext ctx, string path, GameObject root, Renderer[] renderers, Kind kind)
        {
            var lim = ctx.Settings.model;
            var seen = new HashSet<Mesh>();
            var perLevel = new List<long> { 0 };
            bool closed = kind == Kind.Weapon || kind == Kind.Shield || kind == Kind.Rock || kind == Kind.Prop;
            var texels = new List<TexelSample>();
            foreach (var r in renderers)
            {
                var mesh = QaUnity.MeshOf(r);
                if (mesh == null || !seen.Add(mesh)) continue;
                int level;
                IsLodMesh(r, mesh, out level);
                while (perLevel.Count <= level) perLevel.Add(0);
                perLevel[level] += QaUnity.Triangles(mesh);
                if (level > 0) continue;
                foreach (var issue in Mesh0(ctx, path, root, r, mesh, kind, closed, texels)) yield return issue;
            }
            foreach (var issue in Texels(ctx.Settings, path, kind, texels)) yield return issue;
            foreach (var issue in Budget(path, kind, lim, perLevel[0])) yield return issue;
            foreach (var issue in Lods(path, root, renderers, kind, lim, perLevel)) yield return issue;
            foreach (var issue in Faceted(path, root, kind, lim, perLevel[0])) yield return issue;
        }

        /// <summary>Vertex streams, UV overlap and topology of one LOD0 mesh; adds its texel samples to <paramref name="texels"/>.</summary>
        IEnumerable<QaIssue> Mesh0(QaContext ctx, string path, GameObject root, Renderer r, Mesh mesh, Kind kind, bool closed, List<TexelSample> texels)
        {
            var lim = ctx.Settings.model;
            string obj = mesh.name;
            if (!mesh.HasVertexAttribute(VertexAttribute.Normal)) yield return QaRules.ModelNoNormals.Issue(path, 0, obj, "mesh has no normals", QaSeverity.Major, null);
            if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0))
            {
                yield return QaRules.ModelNoUv0.Issue(path, 0, obj, "mesh has no UV0", QaSeverity.Major, null);
                yield break;
            }
            if (!mesh.HasVertexAttribute(VertexAttribute.Tangent) && UsesNormalMap(r))
                yield return QaRules.ModelNoTangents.Issue(path, 0, obj, "normal-mapped material but the mesh has no tangents", QaSeverity.Major, null);
            var d = ctx.Meshes.Read(mesh);
            if (d == null) yield break;
            if (kind != Kind.Environment && d.Uv != null)
            {
                float overlap = PixelMath.UvOverlap(UComponent(d.Uv, 0), UComponent(d.Uv, 1), d.AllIndices, 128, 20000);
                if (overlap > lim.uvOverlapMax)
                    yield return QaRules.ModelUvOverlap.Issue(path, 0, obj, (overlap * 100f).ToString("0") + "% of the UV0 area is shared by several triangles", QaSeverity.Minor, null);
            }
            var topo = AssetMath.Topology(QaMesh.Transform(d.Xyz, ToRoot(r, root)), d.AllIndices, lim.weldDistance);
            if (closed && topo.OpenEdges > 0)
                yield return QaRules.ModelOpenEdges.Issue(path, 0, obj, topo.OpenEdges.ToString("N0") + " open edge(s) in " + topo.Triangles.ToString("N0") +
                    " triangles (weld " + (lim.weldDistance * 1000f).ToString("0.###") + " mm)", QaSeverity.Major, null);
            if (topo.NonManifoldEdges > 0)
                yield return QaRules.ModelNonManifold.Issue(path, 0, obj, topo.NonManifoldEdges.ToString("N0") + " edge(s) shared by more than two triangles", QaSeverity.Major, null);
            if (topo.LooseVertices > 0)
                yield return QaRules.ModelLooseVerts.Issue(path, 0, obj, topo.LooseVertices.ToString("N0") + " vertices used by no triangle", QaSeverity.Minor, null);
            texels.AddRange(ctx.Meshes.Densities(r, ToRoot(r, root), ctx.Settings));
        }

        /// <summary>Held weapons with too few triangles per metre (a scabbard hangs at the hip and is never seen edge-on).</summary>
        static IEnumerable<QaIssue> Faceted(string path, GameObject root, Kind kind, QaSettings.ModelLimits lim, long tris)
        {
            if (kind != Kind.Weapon || tris <= 0 || Path.GetFileName(path).IndexOf("scabbard", System.StringComparison.OrdinalIgnoreCase) >= 0) yield break;
            Bounds b;
            if (!QaUnity.MeshBounds(root, out b)) yield break;
            float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            float perMeter = longest > 0.01f ? tris / longest : 0f;
            if (perMeter > 0f && perMeter < lim.facetedTrisPerMeter)
                yield return QaRules.ModelFacetedWeapon.Issue(path, 0, tris.ToString("N0") + " triangles over " + longest.ToString("0.00") + " m = " +
                    perMeter.ToString("N0") + " per metre (min " + lim.facetedTrisPerMeter.ToString("N0") + ")");
        }

        static Matrix4x4 ToRoot(Renderer r, GameObject root) { return root.transform.worldToLocalMatrix * r.transform.localToWorldMatrix; }

        static float[] UComponent(float[] uv, int c)
        {
            var o = new float[uv.Length / 2];
            for (int i = 0; i < o.Length; i++) o[i] = uv[i * 2 + c];
            return o;
        }

        static IEnumerable<QaIssue> Texels(QaSettings settings, string path, Kind kind, List<TexelSample> samples)
        {
            foreach (var s in samples)
            {
                float low, high;
                if (!QaArt.TexelBand(settings, kind.ToString(), s.Material, out low, out high)) yield break;
                if (s.Density >= low && s.Density <= high) continue;
                // rocks and environment pieces may sit far from the camera: a soft one is reported, not counted as Major
                var sev = kind == Kind.Rock || kind == Kind.Environment ? QaSeverity.Minor : QaSeverity.Major;
                yield return QaRules.ModelTexelDensity.Issue(path, 0, s.Material, s.Density.ToString("N0") + " px/m with " + s.Texture + " (" + kind + " tier " +
                    low.ToString("N0") + "–" + (high >= float.MaxValue ? "∞" : high.ToString("N0")) + " px/m)", sev, null);
            }
        }

        static IEnumerable<QaIssue> Budget(string path, Kind kind, QaSettings.ModelLimits lim, long tris)
        {
            int budget, minimum = 0;
            switch (kind)
            {
                case Kind.Hero: budget = lim.heroTriangles; minimum = lim.heroMinTriangles; break;
                case Kind.Npc: budget = lim.npcTriangles; break;
                case Kind.Weapon: budget = lim.weaponTriangles; minimum = lim.weaponMinTriangles; break;
                case Kind.Shield: budget = lim.shieldTriangles; break;
                case Kind.Banner: budget = lim.bannerTriangles; break;
                case Kind.Rock: budget = lim.rockTriangles; break;
                case Kind.Prop: budget = lim.propTriangles; break;
                default: budget = lim.environmentTriangles; break;
            }
            if (tris > budget)
                yield return QaRules.ModelPolyBudget.Issue(path, 0, "", tris.ToString("N0") + " triangles (" + kind + " ceiling " + budget.ToString("N0") + ")",
                    tris > budget * 2L ? QaSeverity.Critical : QaSeverity.Major, null);
            else if (tris < minimum)
                yield return QaRules.ModelPolyTooLow.Issue(path, 0, tris.ToString("N0") + " triangles for a " + kind + " asset (expected ≥ " + minimum.ToString("N0") + ")");
        }

        /// <summary>Characters and heavy meshes need LODs in the file, a LODGroup, or sibling _LOD1 files; present LODs must follow the ratios.</summary>
        static IEnumerable<QaIssue> Lods(string path, GameObject root, Renderer[] renderers, Kind kind, QaSettings.ModelLimits lim, List<long> perLevel)
        {
            bool character = kind == Kind.Hero || kind == Kind.Npc;
            if (!character && perLevel[0] <= lim.lodTriangles) yield break;
            if (perLevel.Count > 1)
            {
                string problem;
                if (!AssetMath.LodRatiosOk(perLevel, lim.lodRatios, lim.lodRatioTolerance, out problem))
                    yield return QaRules.ModelNoLods.Issue(path, 0, "", problem, QaSeverity.Minor, null);
                yield break;
            }
            if (root.GetComponentInChildren<LODGroup>(true) != null) yield break;
            string dir = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path);
            if (Directory.Exists(dir) && Directory.GetFiles(dir, stem + "*_LOD1*").Length > 0) yield break;
            yield return QaRules.ModelNoLods.Issue(path, 0, (character ? "character" : "mesh") + " with " + perLevel[0].ToString("N0") +
                " LOD0 triangles has no LOD1-3 (no _LOD1 meshes, no LODGroup, no " + stem + "_LOD1 file)");
        }

        static bool Generated(string path, Renderer[] renderers)
        {
            if (Path.GetFileName(path).StartsWith("Meshy", System.StringComparison.OrdinalIgnoreCase)) return true;
            if (renderers.Length != 1) return false;
            var mesh = QaUnity.MeshOf(renderers[0]);
            var mats = renderers[0].sharedMaterials;
            return (mesh != null && mesh.name == "output") || (mats.Length == 1 && mats[0] != null && mats[0].name == "bodymat");
        }

        static bool UsesNormalMap(Renderer r)
        {
            foreach (var m in r.sharedMaterials)
                if (m != null && m.HasProperty("_BumpMap") && m.GetTexture("_BumpMap") != null) return true;
            return false;
        }
    }
}
