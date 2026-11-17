using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeorgianKnight.World
{
    /// <summary>
    /// The greybox forest on the arena's ranges (World, 2026-10-31): the terrains' tree instances (TerrainData.treeInstances,
    /// their own tree pass off at tree distance 0) baked once, at enable, into static meshes per ChunkSize cell, each a
    /// LODGroup of two: the three-crown clump near, one rounded blob per clump far. Unity culls and switches them; nothing runs
    /// per frame. Budget (Build &amp; QA, QA.md §13): trees at High, worst arena view, &lt;= 1 ms GPU / main / render thread,
    /// &lt;= 150 batches, &lt;= 2 M triangles. History: the terrain tree pass cost +28 ms CPU for 41k clumps (per-tree work), a
    /// per-camera Graphics.RenderMeshInstanced pass +1.8 ms main and GPU (the matrices copied every camera, every frame).
    /// The chunks are hidden, never saved, rebuilt at every enable. Art's real trees replace this.
    /// </summary>
    [ExecuteAlways]
    public class GreyForest : MonoBehaviour
    {
        public float ChunkSize = 2000f;   // 1 km: +193 batches over the QA.md §13 cap of 150
        [Range(0f, 1f)] public float NearLodHeight = 0.9f;   // the clump LOD while a chunk fills more than this of the screen height (~2 km for 2 km chunks)
        public bool CastShadows = false;

        readonly List<GameObject> chunks = new List<GameObject>();
        readonly List<Mesh> meshes = new List<Mesh>();

        void OnEnable() { Build(); }
        void OnDisable() { Clear(); }

        void Clear()
        {
            foreach (var go in chunks) if (go != null) { if (Application.isPlaying) Destroy(go); else DestroyImmediate(go); }
            foreach (var m in meshes) if (m != null) { if (Application.isPlaying) Destroy(m); else DestroyImmediate(m); }
            chunks.Clear(); meshes.Clear();
        }

        /// <summary>A unit icosahedron with shared vertices and smooth (radial) normals: 12 vertices, 20 triangles.</summary>
        static void Icosahedron(out Vector3[] v, out int[] tris)
        {
            float t = (1f + Mathf.Sqrt(5f)) * 0.5f;
            v = new[]
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
            };
            for (int i = 0; i < v.Length; i++) v[i] = v[i].normalized;
            tris = new[]
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            };
            for (int i = 0; i < tris.Length; i += 3)   // outward-facing in Unity's winding, whatever the source list's handedness
            {
                Vector3 a = v[tris[i]], b = v[tris[i + 1]], c = v[tris[i + 2]];
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), a + b + c) < 0f) { int s = tris[i + 1]; tris[i + 1] = tris[i + 2]; tris[i + 2] = s; }
            }
        }

        /// <summary>A template made of ellipsoid crowns (centre, radii) in tree space, pivot at the ground.</summary>
        static void Template(Vector4[] crowns, out Vector3[] pos, out Vector3[] nrm, out int[] idx)
        {
            Icosahedron(out var v, out var t);
            pos = new Vector3[v.Length * crowns.Length]; nrm = new Vector3[pos.Length]; idx = new int[t.Length * crowns.Length];
            for (int c = 0; c < crowns.Length; c++)
            {
                var centre = new Vector3(crowns[c].x, crowns[c].y, crowns[c].z);
                var r = new Vector3(crowns[c].w, crowns[c].w * 1.15f, crowns[c].w);
                for (int i = 0; i < v.Length; i++)
                {
                    pos[c * v.Length + i] = centre + Vector3.Scale(v[i], r);
                    nrm[c * v.Length + i] = new Vector3(v[i].x / r.x, v[i].y / r.y, v[i].z / r.z).normalized;   // the ellipsoid's normal
                }
                for (int i = 0; i < t.Length; i++) idx[c * t.Length + i] = c * v.Length + t[i];
            }
        }

        // the near clump: three crowns ~22 m across, ~12 m high, 60 triangles (five, 100 triangles: +2.6 M over the QA.md §13 cap
        // of 2 M, 2026-10-31); the far blob covers the same clump
        static readonly Vector4[] NearCrowns =
        {
            new Vector4(0f, 7.5f, 0f, 5f), new Vector4(6.5f, 6.5f, 3.5f, 4.6f), new Vector4(-3.5f, 6.8f, -6f, 4.6f),
        };
        static readonly Vector4[] FarCrowns = { new Vector4(0f, 6.5f, 0f, 8.5f) };

        /// <summary>Gathers every terrain's trees by chunk and bakes the chunk meshes and their LODGroups.</summary>
        public void Build()
        {
            Clear();
            Material material = null;
            var cells = new Dictionary<Vector2Int, List<Matrix4x4>>();
            foreach (var t in FindObjectsByType<Terrain>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))   // not Terrain.activeTerrains: they may enable after this
            {
                var td = t.terrainData;
                if (td == null || td.treePrototypes.Length == 0 || td.treePrototypes[0].prefab == null) continue;
                var mr = td.treePrototypes[0].prefab.GetComponentInChildren<MeshRenderer>();
                if (mr == null) continue;
                material = mr.sharedMaterial;
                Vector3 origin = t.transform.position, size = td.size;
                foreach (var ti in td.treeInstances)
                {
                    var p = origin + Vector3.Scale(ti.position, size);
                    var key = new Vector2Int(Mathf.FloorToInt(p.x / ChunkSize), Mathf.FloorToInt(p.z / ChunkSize));
                    if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<Matrix4x4>();
                    list.Add(Matrix4x4.TRS(p, Quaternion.Euler(0f, ti.rotation * Mathf.Rad2Deg, 0f), new Vector3(ti.widthScale, ti.heightScale, ti.widthScale)));
                }
            }
            if (material == null || cells.Count == 0) return;
            Template(NearCrowns, out var nPos, out var nNrm, out var nIdx);
            Template(FarCrowns, out var fPos, out var fNrm, out var fIdx);
            int trees = 0;
            foreach (var cell in cells)
            {
                var origin = new Vector3((cell.Key.x + 0.5f) * ChunkSize, 0f, (cell.Key.y + 0.5f) * ChunkSize);
                var go = new GameObject("GreyForest_" + cell.Key.x + "_" + cell.Key.y) { hideFlags = HideFlags.HideAndDontSave };
                go.transform.SetParent(transform, false);
                go.transform.position = origin;
                go.layer = gameObject.layer;
                var near = Part(go, "Near", Bake(cell.Value, origin, nPos, nNrm, nIdx), material);
                var far = Part(go, "Far", Bake(cell.Value, origin, fPos, fNrm, fIdx), material);
                var lod = go.AddComponent<LODGroup>();
                lod.SetLODs(new[] { new LOD(NearLodHeight, new Renderer[] { near }), new LOD(0.003f, new Renderer[] { far }) });
                lod.RecalculateBounds();
                chunks.Add(go);
                trees += cell.Value.Count;
            }
            Debug.Log("GreyForest: " + trees + " trees in " + chunks.Count + " chunks");
        }

        Mesh Bake(List<Matrix4x4> instances, Vector3 origin, Vector3[] pos, Vector3[] nrm, int[] idx)
        {
            var vp = new Vector3[pos.Length * instances.Count]; var vn = new Vector3[vp.Length]; var ix = new int[idx.Length * instances.Count];
            for (int k = 0; k < instances.Count; k++)
            {
                var m = instances[k];
                m.m03 -= origin.x; m.m13 -= origin.y; m.m23 -= origin.z;   // chunk-local
                int vo = k * pos.Length, io = k * idx.Length;
                for (int i = 0; i < pos.Length; i++)
                {
                    vp[vo + i] = m.MultiplyPoint3x4(pos[i]);
                    vn[vo + i] = m.MultiplyVector(nrm[i]).normalized;   // rotation and a near-uniform scale: no inverse-transpose needed
                }
                for (int i = 0; i < idx.Length; i++) ix[io + i] = vo + idx[i];
            }
            var mesh = new Mesh { name = "GreyForestChunk", indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = vp; mesh.normals = vn; mesh.triangles = ix;
            mesh.RecalculateBounds();
            meshes.Add(mesh);
            return mesh;
        }

        Renderer Part(GameObject chunk, string name, Mesh mesh, Material material)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(chunk.transform, false);
            go.layer = chunk.layer;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = CastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            r.lightProbeUsage = LightProbeUsage.BlendProbes;   // no probes in the arena: the ambient probe (Off would light it black)
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return r;
        }
    }
}
