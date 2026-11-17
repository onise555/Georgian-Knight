using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// Placement checks for an open scene: props floating above / sunk into / buried under the ground, spawn points and
    /// characters over the void, and duplicate meshes stacked on the same transform. Ground is found by ray casts that
    /// ignore the object itself; renderers without colliders get temporary MeshColliders for the duration of the check
    /// so a cup on an un-collided table is measured against the table, not the floor (the scene is never saved).
    /// </summary>
    public static class ScenePlacement
    {
        public static void Check(string path, Scene scene, QaSettings.SceneLimits lim, Dictionary<string, List<Component>> byType, List<QaIssue> issues)
        {
            var temp = AddSupportColliders(scene);
            try
            {
                Physics.SyncTransforms();
                if (temp.Count == 0 && Object.FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length == 0) return;
                Props(path, scene, lim, issues);
                Points(path, lim, byType, issues);
                Duplicates(path, scene, lim, issues);
            }
            finally
            {
                foreach (var c in temp) if (c != null) Object.DestroyImmediate(c);
            }
        }

        static List<Collider> AddSupportColliders(Scene scene)
        {
            var added = new List<Collider>();
            foreach (var root in scene.GetRootGameObjects())
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>(false))
                {
                    if (mf.sharedMesh == null || mf.GetComponent<Collider>() != null || mf.GetComponent<MeshRenderer>() == null) continue;
                    if (mf.GetComponentInParent<Rigidbody>() != null) continue;   // non-convex colliders are not allowed under bodies
                    var mc = mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                    mc.hideFlags = HideFlags.HideAndDontSave;
                    added.Add(mc);
                }
            return added;
        }

        /// <summary>The object a prop "is": its nearest prefab instance root, or the topmost renderer of a renderer chain.</summary>
        static Transform PropRoot(Transform t)
        {
            var prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(t.gameObject);
            if (prefabRoot != null) return prefabRoot.transform;
            while (t.parent != null && t.parent.GetComponent<Renderer>() != null) t = t.parent;
            return t;
        }

        static void Props(string path, Scene scene, QaSettings.SceneLimits lim, List<QaIssue> issues)
        {
            var done = new HashSet<Transform>();
            foreach (var root in scene.GetRootGameObjects())
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(false))
                {
                    if (!mr.enabled) continue;
                    var prop = PropRoot(mr.transform);
                    if (!done.Add(prop) || Skip(prop, lim)) continue;
                    Bounds b;
                    if (!RendererBounds(prop, out b)) continue;
                    if (b.size.x > 30f || b.size.z > 30f || b.size.y > 30f || b.size.y < 0.02f) continue;
                    MeasureProp(path, prop, b, lim, issues);
                }
        }

        static bool Skip(Transform prop, QaSettings.SceneLimits lim)
        {
            var go = prop.gameObject;
            if (go.CompareTag("EditorOnly") || go.layer == 10 || go.GetComponentInChildren<Rigidbody>() != null || go.GetComponentInChildren<Terrain>() != null) return true;
            for (var t = prop; t != null; t = t.parent) if (QaUnity.NameHasToken(t.name, lim.floatingAllowed)) return true;
            return false;
        }

        static bool RendererBounds(Transform prop, out Bounds b)
        {
            b = new Bounds();
            bool any = false;
            foreach (var r in prop.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (!r.enabled) continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return any;
        }

        static void MeasureProp(string path, Transform prop, Bounds b, QaSettings.SceneLimits lim, List<QaIssue> issues)
        {
            string obj = QaUnity.HierarchyPath(prop);
            float top = b.max.y + 0.5f;
            Vector3 c = b.center, e = b.extents * 0.5f;
            var samples = new[] { c, c + new Vector3(e.x, 0, e.z), c + new Vector3(-e.x, 0, e.z), c + new Vector3(e.x, 0, -e.z), c + new Vector3(-e.x, 0, -e.z) };
            float minGap = float.MaxValue, centerGround = float.NaN;
            int hits = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                float y;
                if (!Ground(samples[i].x, samples[i].z, top, prop, out y)) continue;
                hits++;
                minGap = Mathf.Min(minGap, b.min.y - y);
                if (i == 0) centerGround = y;
            }
            if (hits == 0)
            {
                issues.Add(QaRules.SceneVoid.Issue(path, 0, obj, "no ground or support below the object", QaSeverity.Major, null));
                return;
            }
            if (minGap > lim.floatTolerance)
                issues.Add(QaRules.SceneFloating.Issue(path, 0, obj, "bottom floats " + (minGap * 100f).ToString("0") + " cm above the surface below", QaSeverity.Major, null));
            if (float.IsNaN(centerGround)) return;
            if (b.max.y < centerGround - 0.05f)
                issues.Add(QaRules.SceneBuried.Issue(path, 0, obj, "entirely below the surface (" + (centerGround - b.max.y).ToString("0.00") + " m)", QaSeverity.Major, null));
            else if (centerGround - b.min.y > lim.sunkFraction * b.size.y)
                issues.Add(QaRules.SceneSunk.Issue(path, 0, obj, ((centerGround - b.min.y) / b.size.y * 100f).ToString("0") + "% of its height is below the surface", QaSeverity.Major, null));
        }

        /// <summary>Height of the first non-trigger surface below (x, top, z) that does not belong to <paramref name="self"/>.</summary>
        public static bool Ground(float x, float z, float top, Transform self, out float y)
        {
            y = 0f;
            var hits = Physics.RaycastAll(new Vector3(x, top, z), Vector3.down, 2000f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            foreach (var h in hits)
            {
                if (self != null && h.collider.transform.IsChildOf(self)) continue;
                if (h.distance < best) { best = h.distance; y = h.point.y; }
            }
            return best < float.MaxValue;
        }

        static void Points(string path, QaSettings.SceneLimits lim, Dictionary<string, List<Component>> byType, List<QaIssue> issues)
        {
            var points = new List<Transform>();
            foreach (var name in lim.spawnComponents) AddAll(byType, name, points);
            AddAll(byType, "CharacterController", points);
            AddAll(byType, "NavMeshAgent", points);
            foreach (var p in points)
            {
                if (p == null || !p.gameObject.activeInHierarchy) continue;
                string obj = QaUnity.HierarchyPath(p);
                float y;
                if (!Ground(p.position.x, p.position.z, p.position.y + 2f, p, out y))
                {
                    issues.Add(QaRules.SceneVoid.Issue(path, 0, obj, "nothing below this spawn/character: it falls into the void", QaSeverity.Critical, null));
                    continue;
                }
                foreach (var terrain in Terrain.activeTerrains)
                {
                    var tp = terrain.transform.position;
                    var size = terrain.terrainData != null ? terrain.terrainData.size : Vector3.zero;
                    if (p.position.x < tp.x || p.position.z < tp.z || p.position.x > tp.x + size.x || p.position.z > tp.z + size.z) continue;
                    float h = terrain.SampleHeight(p.position) + tp.y;
                    if (p.position.y < h - 0.1f)
                        issues.Add(QaRules.SceneBuried.Issue(path, 0, obj, "spawn/character is " + (h - p.position.y).ToString("0.00") + " m below the terrain", QaSeverity.Major, null));
                }
            }
        }

        static void AddAll(Dictionary<string, List<Component>> byType, string type, List<Transform> into)
        {
            List<Component> list;
            if (!byType.TryGetValue(type, out list)) return;
            foreach (var c in list) if (c != null && !into.Contains(c.transform)) into.Add(c.transform);
        }

        static void Duplicates(string path, Scene scene, QaSettings.SceneLimits lim, List<QaIssue> issues)
        {
            var seen = new Dictionary<string, Transform>();
            float q = Mathf.Max(1e-5f, lim.duplicateEpsilon);
            foreach (var root in scene.GetRootGameObjects())
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>(false))
                {
                    if (mf.sharedMesh == null) continue;
                    var t = mf.transform;
                    var e = t.rotation.eulerAngles;
                    var s = t.lossyScale;
                    string key = mf.sharedMesh.GetInstanceID() + "|" + Mathf.RoundToInt(t.position.x / q) + "," + Mathf.RoundToInt(t.position.y / q) + "," +
                                 Mathf.RoundToInt(t.position.z / q) + "|" + Mathf.RoundToInt(e.x * 10f) % 3600 + "," + Mathf.RoundToInt(e.y * 10f) % 3600 + "," +
                                 Mathf.RoundToInt(e.z * 10f) % 3600 + "|" + Mathf.RoundToInt(s.x * 1000f) + "," + Mathf.RoundToInt(s.y * 1000f) + "," + Mathf.RoundToInt(s.z * 1000f);
                    Transform other;
                    if (seen.TryGetValue(key, out other))
                        issues.Add(QaRules.SceneDuplicateMesh.Issue(path, 0, QaUnity.HierarchyPath(t), "same mesh '" + mf.sharedMesh.name + "' at the same transform as " + QaUnity.HierarchyPath(other), QaSeverity.Major, null));
                    else seen.Add(key, t);
                }
        }
    }
}
