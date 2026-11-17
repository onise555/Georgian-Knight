using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// PREFAB.* rules on every prefab asset: missing scripts/prefabs/references/materials, degenerate colliders, bodies
    /// without colliders, the layer table (Character 8, Hurtbox 9, Terrain 10), CharacterController proportions, LODs on
    /// heavy meshes, shadows on tiny props, art rescaled after import and texel-density mismatches between its parts.
    /// </summary>
    public sealed class PrefabValidator : IValidator
    {
        public string Id { get { return "prefab"; } }
        public string Category { get { return QaCategory.Prefab; } }

        public IEnumerable<QaIssue> Run(QaContext ctx)
        {
            var paths = ctx.FindAssets("t:Prefab");
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (i % 10 == 0) ctx.Progress(path, i / (float)paths.Count);
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (root == null) continue;
                foreach (var issue in Integrity(path, root, QaRules.PrefabMissingScript, QaRules.PrefabMissingPrefab, QaRules.PrefabMissingReference, QaRules.PrefabMissingMaterial))
                    yield return issue;
                foreach (var issue in Physics(path, root, ctx.Settings.prefab)) yield return issue;
                foreach (var issue in Rendering(path, root, ctx.Settings.prefab)) yield return issue;
                foreach (var issue in QaArt.Rescaled(path, root, ctx.Settings.prefab.rescaleTolerance)) yield return issue;
                foreach (var issue in QaArt.Texels(ctx, path, root)) yield return issue;
            }
        }

        /// <summary>Missing scripts / nested prefabs / references / materials; shared with the scene validator.</summary>
        public static IEnumerable<QaIssue> Integrity(string path, GameObject root, QaRule missingScript, QaRule missingPrefab, QaRule brokenRef, QaRule missingMat)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var go = t.gameObject;
                string obj = QaUnity.HierarchyPath(t);
                int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (missing > 0) yield return missingScript.Issue(path, 0, obj, missing + " missing script(s)", missingScript.Severity, null);
                if (PrefabUtility.IsPrefabAssetMissing(go)) yield return missingPrefab.Issue(path, 0, obj, "prefab instance whose asset is missing", missingPrefab.Severity, null);
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var broken = QaUnity.BrokenReferences(c);
                    if (broken.Count > 0)
                        yield return brokenRef.Issue(path, 0, obj, c.GetType().Name + ": missing reference in " + QaUnity.Join(broken, 4), brokenRef.Severity, null);
                }
                var r = go.GetComponent<Renderer>();
                if (r != null && !(r is ParticleSystemRenderer) && !(r is TrailRenderer) && !(r is LineRenderer))
                {
                    var mats = r.sharedMaterials;
                    int empty = 0;
                    foreach (var m in mats) if (m == null) empty++;
                    if (mats.Length == 0 || empty > 0)
                        yield return missingMat.Issue(path, 0, obj, mats.Length == 0 ? "renderer without materials" : empty + " of " + mats.Length + " material slot(s) empty", missingMat.Severity, null);
                }
            }
        }

        static IEnumerable<QaIssue> Physics(string path, GameObject root, QaSettings.PrefabLimits lim)
        {
            foreach (var col in root.GetComponentsInChildren<Collider>(true))
            {
                string why = Degenerate(col);
                if (why != null) yield return QaRules.PrefabZeroCollider.Issue(path, 0, QaUnity.HierarchyPath(col.transform), why, QaSeverity.Major, null);
            }
            foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true))
                if (rb.GetComponentsInChildren<Collider>(true).Length == 0)
                    yield return QaRules.PrefabRigidbodyNoCollider.Issue(path, 0, QaUnity.HierarchyPath(rb.transform), "Rigidbody has no collider on itself or its children", QaSeverity.Major, null);
            foreach (var cc in root.GetComponentsInChildren<CharacterController>(true))
            {
                string obj = QaUnity.HierarchyPath(cc.transform);
                if (cc.gameObject.layer != lim.characterLayer)
                    yield return QaRules.PrefabLayer.Issue(path, 0, obj, "CharacterController on layer " + LayerMask.LayerToName(cc.gameObject.layer) + " (" + cc.gameObject.layer + "), expected Character (" + lim.characterLayer + ")", QaSeverity.Major, null);
                foreach (var msg in ControllerProblems(cc, lim)) yield return QaRules.PrefabCcDimensions.Issue(path, 0, obj, msg, QaSeverity.Major, null);
            }
            foreach (var hb in root.GetComponentsInChildren<Hurtbox>(true))
                if (hb.gameObject.layer != lim.hurtboxLayer)
                    yield return QaRules.PrefabLayer.Issue(path, 0, QaUnity.HierarchyPath(hb.transform), "Hurtbox on layer " + hb.gameObject.layer + ", expected Hurtbox (" + lim.hurtboxLayer + ")", QaSeverity.Major, null);
            foreach (var rag in root.GetComponentsInChildren<Ragdoll>(true))
                foreach (var c in rag.colliders)
                    if (c != null && c.gameObject.layer != lim.hurtboxLayer)
                        yield return QaRules.PrefabLayer.Issue(path, 0, QaUnity.HierarchyPath(c.transform), "ragdoll/hurtbox collider on layer " + c.gameObject.layer + ", expected Hurtbox (" + lim.hurtboxLayer + ")", QaSeverity.Major, null);
            foreach (var terrain in root.GetComponentsInChildren<Terrain>(true))
                if (terrain.gameObject.layer != lim.terrainLayer)
                    yield return QaRules.PrefabLayer.Issue(path, 0, QaUnity.HierarchyPath(terrain.transform), "Terrain on layer " + terrain.gameObject.layer + ", expected Terrain (" + lim.terrainLayer + ")", QaSeverity.Major, null);
        }

        public static string Degenerate(Collider col)
        {
            const float eps = 1e-4f;
            var box = col as BoxCollider;
            if (box != null && (Mathf.Abs(box.size.x) < eps || Mathf.Abs(box.size.y) < eps || Mathf.Abs(box.size.z) < eps)) return "BoxCollider with zero size " + box.size;
            var sphere = col as SphereCollider;
            if (sphere != null && sphere.radius < eps) return "SphereCollider with zero radius";
            var cap = col as CapsuleCollider;
            if (cap != null && (cap.radius < eps || cap.height < eps)) return "CapsuleCollider with zero radius/height";
            var mc = col as MeshCollider;
            if (mc != null && mc.sharedMesh == null) return "MeshCollider without mesh";
            return null;
        }

        public static List<string> ControllerProblems(CharacterController cc, QaSettings.PrefabLimits lim)
        {
            var list = new List<string>();
            var s = cc.transform.lossyScale;
            float h = cc.height * Mathf.Abs(s.y), r = cc.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
            float cy = cc.center.y * Mathf.Abs(s.y);
            if (h < lim.ccMinHeight || h > lim.ccMaxHeight) list.Add("height " + h.ToString("0.00") + " m (" + lim.ccMinHeight + "–" + lim.ccMaxHeight + ")");
            if (r < lim.ccMinRadius || r > lim.ccMaxRadius) list.Add("radius " + r.ToString("0.00") + " m (" + lim.ccMinRadius + "–" + lim.ccMaxRadius + ")");
            if (Mathf.Abs(cy - h * 0.5f) > 0.15f * h) list.Add("center.y " + cy.ToString("0.00") + " m is not ≈ height/2 (" + (h * 0.5f).ToString("0.00") + "): capsule floats or sinks");
            if (cc.skinWidth < 0.05f * cc.radius || cc.skinWidth > 0.25f * cc.radius) list.Add("skin width " + cc.skinWidth.ToString("0.000") + " (≈10% of radius recommended)");
            if (cc.stepOffset > cc.height + cc.radius * 2f) list.Add("step offset " + cc.stepOffset.ToString("0.00") + " larger than the capsule");
            return list;
        }

        static IEnumerable<QaIssue> Rendering(string path, GameObject root, QaSettings.PrefabLimits lim)
        {
            if (root.GetComponentInChildren<LODGroup>(true) == null)
            {
                long tris = 0;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true)) tris += QaUnity.Triangles(QaUnity.MeshOf(r));
                Bounds b;
                bool hasBounds = QaUnity.MeshBounds(root, out b);
                float size = hasBounds ? Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) : 0f;
                if (tris > lim.lodTriangles || size > lim.lodSize)
                    yield return QaRules.PrefabLodMissing.Issue(path, 0, "no LODGroup: " + tris.ToString("N0") + " triangles, " + size.ToString("0.0") + " m");
            }
            var tiny = new List<string>();
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (mr.shadowCastingMode == ShadowCastingMode.Off) continue;
                var mesh = QaUnity.MeshOf(mr);
                if (mesh == null) continue;
                var b = QaUnity.Transform(mesh.bounds, mr.transform.localToWorldMatrix);
                if (Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) < lim.tinyShadowSize) tiny.Add(QaUnity.HierarchyPath(mr.transform));
            }
            if (tiny.Count > 0)
                yield return QaRules.PrefabShadowTiny.Issue(path, 0, tiny.Count + " renderer(s) under " + (lim.tinyShadowSize * 100f).ToString("0") + " cm cast shadows: " + QaUnity.Join(tiny, 5));
        }
    }
}
