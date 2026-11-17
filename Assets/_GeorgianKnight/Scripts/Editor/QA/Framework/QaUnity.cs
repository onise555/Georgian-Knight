using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// Engine helpers shared by the prefab and scene validators: hierarchy paths, broken references, missing scripts,
    /// renderer bounds and triangle counts that work on prefab assets (not only on scene instances).
    /// </summary>
    public static class QaUnity
    {
        /// <summary>Restores the scenes that were open before a validator switched scenes (untitled scenes cannot be reopened).</summary>
        public static void RestoreScenes(UnityEditor.SceneManagement.SceneSetup[] setup)
        {
            bool restorable = setup != null && setup.Length > 0;
            if (restorable) foreach (var s in setup) if (string.IsNullOrEmpty(s.path)) restorable = false;
            if (restorable) UnityEditor.SceneManagement.EditorSceneManager.RestoreSceneManagerSetup(setup);
            else UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.DefaultGameObjects, UnityEditor.SceneManagement.NewSceneMode.Single);
        }

        /// <summary>False in -nographics/batch runs: GPU readback would return black and fake every measurement.</summary>
        public static bool HasGpu { get { return SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null; } }

        public static string HierarchyPath(Transform t)
        {
            if (t == null) return "";
            var names = new List<string>();
            for (var p = t; p != null; p = p.parent) names.Add(p.name);
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        /// <summary>Serialized object fields that point at a deleted asset/object ("Missing" in the inspector).</summary>
        public static List<string> BrokenReferences(Object component)
        {
            var list = new List<string>();
            if (component == null) return list;
            var so = new SerializedObject(component);
            var it = so.GetIterator();
            while (it.NextVisible(true))
            {
                if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                if (it.objectReferenceValue == null && it.objectReferenceInstanceIDValue != 0) list.Add(it.propertyPath);
            }
            return list;
        }

        /// <summary>World-space bounds of all renderers under <paramref name="root"/> computed from mesh bounds, so it also
        /// works for prefab assets that are not in a scene.</summary>
        public static bool MeshBounds(GameObject root, out Bounds bounds)
        {
            bounds = new Bounds();
            bool any = false;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = null;
                var smr = r as SkinnedMeshRenderer;
                if (smr != null) mesh = smr.sharedMesh;
                else
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }
                if (mesh == null) continue;
                var b = Transform(mesh.bounds, r.transform.localToWorldMatrix);
                if (!any) { bounds = b; any = true; }
                else bounds.Encapsulate(b);
            }
            return any;
        }

        public static Bounds Transform(Bounds b, Matrix4x4 m)
        {
            var c = m.MultiplyPoint3x4(b.center);
            var e = b.extents;
            var x = m.MultiplyVector(new Vector3(e.x, 0f, 0f));
            var y = m.MultiplyVector(new Vector3(0f, e.y, 0f));
            var z = m.MultiplyVector(new Vector3(0f, 0f, e.z));
            var size = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x), Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
                                   Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z)) * 2f;
            return new Bounds(c, size);
        }

        /// <summary>Triangle count from index counts (no CPU mesh access needed).</summary>
        public static long Triangles(Mesh mesh)
        {
            if (mesh == null) return 0;
            long n = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
                if (mesh.GetTopology(s) == MeshTopology.Triangles) n += mesh.GetIndexCount(s) / 3;
            return n;
        }

        public static Mesh MeshOf(Renderer r)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr != null) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        /// <summary>True for Unity's built-in primitive meshes (Cube, Sphere, Capsule, Cylinder, Plane, Quad).</summary>
        public static bool IsBuiltinPrimitive(Mesh mesh)
        {
            if (mesh == null) return false;
            string p = AssetDatabase.GetAssetPath(mesh);
            return p == "Library/unity default resources" || p == "Resources/unity_builtin_extra";
        }

        public static string Join(List<string> items, int max)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < items.Count && i < max; i++) sb.Append(i == 0 ? "" : ", ").Append(items[i]);
            if (items.Count > max) sb.Append(", … (+").Append(items.Count - max).Append(')');
            return sb.ToString();
        }

        public static bool NameHasToken(string name, IList<string> tokens)
        {
            string n = (name ?? "").ToLowerInvariant();
            foreach (var t in tokens) if (n.Contains(t.ToLowerInvariant())) return true;
            return false;
        }
    }
}
