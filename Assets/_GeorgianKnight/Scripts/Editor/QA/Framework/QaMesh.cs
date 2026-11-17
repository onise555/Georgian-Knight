using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>Positions (local, metres before the renderer's transform), UV0 and per-submesh triangle indices of one mesh.</summary>
    public sealed class QaMeshData
    {
        public float[] Xyz;
        public float[] Uv;          // null when the mesh has no UV0
        public int[][] SubIndices;  // triangle submeshes only; empty array for other topologies
        public int[] AllIndices;
    }

    /// <summary>One material's measured texel density on one renderer.</summary>
    public struct TexelSample
    {
        public string Material;
        public string MaterialPath;
        public string Texture;
        public float Density;
    }

    /// <summary>
    /// Reads mesh data in the editor through the read-only mesh API (works on non-readable meshes) and measures texel
    /// density per material. Shared by the model and prefab validators; one instance per QA run caches every mesh it read.
    /// </summary>
    public sealed class QaMesh
    {
        static readonly string[] BaseMapSlots = { "_BaseMap", "_MainTex", "_BaseColorMap", "_Albedo" };
        readonly Dictionary<int, QaMeshData> cache = new Dictionary<int, QaMeshData>();

        /// <summary>The mesh's data, or null when the editor cannot read it (streamed / compressed mesh).</summary>
        public QaMeshData Read(Mesh mesh)
        {
            if (mesh == null) return null;
            QaMeshData d;
            if (cache.TryGetValue(mesh.GetInstanceID(), out d)) return d;
            d = ReadUncached(mesh);
            cache[mesh.GetInstanceID()] = d;
            return d;
        }

        static QaMeshData ReadUncached(Mesh mesh)
        {
            try
            {
                using (var data = Mesh.AcquireReadOnlyMeshData(mesh))
                {
                    var md = data[0];
                    var d = new QaMeshData();
                    var pos = new NativeArray<Vector3>(md.vertexCount, Allocator.Temp);
                    md.GetVertices(pos);
                    d.Xyz = new float[pos.Length * 3];
                    for (int i = 0; i < pos.Length; i++) { d.Xyz[i * 3] = pos[i].x; d.Xyz[i * 3 + 1] = pos[i].y; d.Xyz[i * 3 + 2] = pos[i].z; }
                    pos.Dispose();
                    if (md.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0))
                    {
                        var uv = new NativeArray<Vector2>(md.vertexCount, Allocator.Temp);
                        md.GetUVs(0, uv);
                        d.Uv = new float[uv.Length * 2];
                        for (int i = 0; i < uv.Length; i++) { d.Uv[i * 2] = uv[i].x; d.Uv[i * 2 + 1] = uv[i].y; }
                        uv.Dispose();
                    }
                    d.SubIndices = new int[md.subMeshCount][];
                    var all = new List<int>();
                    for (int s = 0; s < md.subMeshCount; s++)
                    {
                        if (md.GetSubMesh(s).topology != MeshTopology.Triangles) { d.SubIndices[s] = new int[0]; continue; }
                        var idx = new NativeArray<int>(md.GetSubMesh(s).indexCount, Allocator.Temp);
                        md.GetIndices(idx, s);
                        d.SubIndices[s] = idx.ToArray();
                        all.AddRange(d.SubIndices[s]);
                        idx.Dispose();
                    }
                    d.AllIndices = all.ToArray();
                    return d;
                }
            }
            catch (System.Exception e)
            {
                // the mesh data is not accessible in the editor: the geometry checks of this mesh are skipped and the reason logged
                Debug.LogWarning("[QA] cannot read mesh '" + mesh.name + "': " + e.Message);
                return null;
            }
        }

        /// <summary>Positions transformed by <paramref name="m"/> (renderer to model root), in metres.</summary>
        public static float[] Transform(float[] xyz, Matrix4x4 m)
        {
            var o = new float[xyz.Length];
            for (int i = 0; i + 2 < xyz.Length; i += 3)
            {
                var p = m.MultiplyPoint3x4(new Vector3(xyz[i], xyz[i + 1], xyz[i + 2]));
                o[i] = p.x; o[i + 1] = p.y; o[i + 2] = p.z;
            }
            return o;
        }

        /// <summary>
        /// Texel density of every measurable material on the renderer: base map present, not an exempt card / tiny part
        /// (settings.texel.exemptTokens), UVs inside 0..1. <paramref name="toRoot"/> brings the mesh into the measured space.
        /// </summary>
        public List<TexelSample> Densities(Renderer r, Matrix4x4 toRoot, QaSettings settings)
        {
            var list = new List<TexelSample>();
            var mesh = QaUnity.MeshOf(r);
            var d = Read(mesh);
            if (d == null || d.Uv == null) return list;
            float[] world = null;
            var mats = r.sharedMaterials;
            for (int s = 0; s < d.SubIndices.Length && s < mats.Length; s++)
            {
                var mat = mats[s];
                if (mat == null || d.SubIndices[s].Length == 0 || QaUnity.NameHasToken(mat.name, settings.texel.exemptTokens)) continue;
                Texture tex = null;
                string slot = null;
                foreach (var p in BaseMapSlots)
                    if (mat.HasProperty(p) && mat.GetTexture(p) != null) { tex = mat.GetTexture(p); slot = p; break; }
                if (tex == null || !AssetMath.UvInsideUnit(d.Uv, d.SubIndices[s], 0.01f)) continue;
                if (world == null) world = Transform(d.Xyz, toRoot);
                float tiling = Mathf.Max(Mathf.Abs(mat.GetTextureScale(slot).x), 1e-4f);
                float density = AssetMath.TexelDensity(world, d.Uv, d.SubIndices[s], tex.width, tiling);
                if (density <= 0f) continue;
                list.Add(new TexelSample { Material = mat.name, MaterialPath = AssetDatabase.GetAssetPath(mat), Texture = Path.GetFileName(AssetDatabase.GetAssetPath(tex)), Density = density });
            }
            return list;
        }
    }
}
