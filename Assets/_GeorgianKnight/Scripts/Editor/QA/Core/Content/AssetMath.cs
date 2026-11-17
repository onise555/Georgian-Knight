using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>Edge statistics of one triangle mesh after welding coincident positions.</summary>
    public struct TopologyStats
    {
        public int Triangles;
        public int OpenEdges;          // used by one triangle
        public int NonManifoldEdges;   // used by three or more triangles
        public int LooseVertices;      // used by no triangle
    }

    /// <summary>
    /// Engine-free geometry and naming rules behind the asset-intake checks (Asset Quality Gate, Docs/AssetQA/QA_RULES.md):
    /// mesh topology, texel density, weapon size classes, test-asset names, channel statistics of source images.
    /// Plain arrays in, numbers out, so the rules are unit-tested without Unity.
    /// </summary>
    public static class AssetMath
    {
        // ------------------------------------------------------------------------------------------------ topology

        /// <summary>
        /// Open, non-manifold and loose counts of a triangle list. Positions are welded on a grid of <paramref name="weld"/>
        /// metres first, so UV and normal seams (split vertices at one position) do not count as holes.
        /// </summary>
        public static TopologyStats Topology(float[] xyz, int[] indices, float weld)
        {
            var stats = new TopologyStats();
            int vertexCount = xyz == null ? 0 : xyz.Length / 3;
            if (vertexCount == 0 || indices == null) return stats;
            float cell = weld > 0f ? weld : 1e-4f;
            var ids = new int[vertexCount];
            var grid = new Dictionary<CellKey, int>(vertexCount);
            for (int v = 0; v < vertexCount; v++)
            {
                var key = new CellKey(Cell(xyz[v * 3], cell), Cell(xyz[v * 3 + 1], cell), Cell(xyz[v * 3 + 2], cell));
                int id;
                if (!grid.TryGetValue(key, out id)) { id = grid.Count; grid.Add(key, id); }
                ids[v] = id;
            }
            var used = new bool[vertexCount];
            var edges = new Dictionary<long, int>(indices.Length);
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                if (!Valid(a, b, c, vertexCount)) continue;
                used[a] = used[b] = used[c] = true;
                int wa = ids[a], wb = ids[b], wc = ids[c];
                if (wa == wb || wb == wc || wa == wc) continue;    // degenerate after the weld
                stats.Triangles++;
                Count(edges, wa, wb);
                Count(edges, wb, wc);
                Count(edges, wc, wa);
            }
            foreach (var kv in edges)
            {
                if (kv.Value == 1) stats.OpenEdges++;
                else if (kv.Value > 2) stats.NonManifoldEdges++;
            }
            for (int v = 0; v < vertexCount; v++) if (!used[v]) stats.LooseVertices++;
            return stats;
        }

        static long Cell(float x, float cell) { return (long)Math.Round(x / cell); }

        static bool Valid(int a, int b, int c, int n) { return a >= 0 && b >= 0 && c >= 0 && a < n && b < n && c < n; }

        /// <summary>Exact weld-grid cell (no hash merging of distinct cells).</summary>
        struct CellKey : IEquatable<CellKey>
        {
            readonly long x, y, z;
            public CellKey(long x, long y, long z) { this.x = x; this.y = y; this.z = z; }
            public bool Equals(CellKey o) { return x == o.x && y == o.y && z == o.z; }
            public override bool Equals(object o) { return o is CellKey && Equals((CellKey)o); }
            public override int GetHashCode() { return unchecked((int)(x * 73856093L ^ y * 19349663L ^ z * 83492791L)); }
        }

        static void Count(Dictionary<long, int> edges, int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            int n;
            edges.TryGetValue(key, out n);
            edges[key] = n + 1;
        }

        // ------------------------------------------------------------------------------------------------ texel density

        /// <summary>
        /// Texels per metre of a texture <paramref name="textureWidth"/> pixels wide, repeated <paramref name="tiling"/> times
        /// over UV 0..1, on the given triangles: width × tiling × √(UV area / world area). Positions are already in metres
        /// (transformed). Returns 0 when the surface has no area.
        /// </summary>
        public static float TexelDensity(float[] xyz, float[] uv, int[] indices, float textureWidth, float tiling)
        {
            double world = 0, tex = 0;
            if (xyz == null || uv == null || indices == null) return 0f;
            int n = Math.Min(xyz.Length / 3, uv.Length / 2);
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                if (!Valid(a, b, c, n)) continue;
                world += TriangleArea3(xyz, a, b, c);
                tex += Math.Abs((uv[b * 2] - uv[a * 2]) * (uv[c * 2 + 1] - uv[a * 2 + 1]) - (uv[c * 2] - uv[a * 2]) * (uv[b * 2 + 1] - uv[a * 2 + 1])) * 0.5;
            }
            if (world <= 1e-12 || tex <= 0) return 0f;
            return (float)(textureWidth * tiling * Math.Sqrt(tex / world));
        }

        static double TriangleArea3(float[] p, int a, int b, int c)
        {
            double ux = p[b * 3] - p[a * 3], uy = p[b * 3 + 1] - p[a * 3 + 1], uz = p[b * 3 + 2] - p[a * 3 + 2];
            double vx = p[c * 3] - p[a * 3], vy = p[c * 3 + 1] - p[a * 3 + 1], vz = p[c * 3 + 2] - p[a * 3 + 2];
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            return Math.Sqrt(cx * cx + cy * cy + cz * cz) * 0.5;
        }

        /// <summary>True when every UV the triangles use lies inside 0..1 (± <paramref name="slack"/>); tiled and trim-sheet
        /// materials leave it on purpose and are not held to a density tier.</summary>
        public static bool UvInsideUnit(float[] uv, int[] indices, float slack)
        {
            if (uv == null || indices == null) return true;
            int n = uv.Length / 2;
            foreach (int i in indices)
            {
                if (i < 0 || i >= n) continue;
                float u = uv[i * 2], v = uv[i * 2 + 1];
                if (u < -slack || u > 1f + slack || v < -slack || v > 1f + slack) return false;
            }
            return true;
        }

        /// <summary>The allowed density band of a tier: from (1 - tolerance) × min to (1 + tolerance) × max; max 0 = no ceiling.</summary>
        public static void TexelBand(float min, float max, float tolerance, out float low, out float high)
        {
            low = min * (1f - tolerance);
            high = max > 0f ? max * (1f + tolerance) : float.MaxValue;
        }

        // ------------------------------------------------------------------------------------------------ names

        /// <summary>Lower-case name without separators ("Shield_Round-02" → "shieldround02") for class tokens.</summary>
        public static string Squash(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var chars = new List<char>(name.Length);
            foreach (char ch in name) if (ch != '_' && ch != '-' && ch != ' ' && ch != '.') chars.Add(char.ToLowerInvariant(ch));
            return new string(chars.ToArray());
        }

        /// <summary>The first size band whose token occurs in the squashed file or folder name, or null.</summary>
        public static QaSettings.SizeBand WeaponClass(string fileName, string folderName, IList<QaSettings.SizeBand> classes)
        {
            string f = Squash(fileName), d = Squash(folderName);
            if (classes == null) return null;
            foreach (var c in classes)
            {
                string tok = Squash(c.token);
                if (tok.Length > 0 && (f.Contains(tok) || d.Contains(tok))) return c;
            }
            return null;
        }

        static readonly Regex TestWord = new Regex(@"(?i)(^|[_\-\s.(])(test|placeholder|temp|tmp)(?=$|[_\-\s.)0-9])");
        static readonly Regex TestCamel = new Regex(@"(?<=[a-z0-9])(Test|Placeholder|Temp)(?=$|[A-Z_0-9])");

        /// <summary>True for names that mark a test or placeholder asset ("House_Test01", "TEMP_Rock", "TreePlaceholder"),
        /// not for words that merely contain the letters ("Contest", "Temple", "Attempt").</summary>
        public static bool IsTestAssetName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return TestWord.IsMatch(name) || TestCamel.IsMatch(name);
        }

        /// <summary>The first band one of whose tokens occurs in the (lower-case) name, or null.</summary>
        public static QaSettings.TokenBand TokenClass(string name, IList<QaSettings.TokenBand> bands)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (bands == null) return null;
            foreach (var b in bands)
                foreach (var t in b.tokens)
                    if (!string.IsNullOrEmpty(t) && n.Contains(t.ToLowerInvariant())) return b;
            return null;
        }

        /// <summary>The value of the first entry whose match occurs in the (case-insensitive) name; false when none does.</summary>
        public static bool Lookup(string name, IList<QaSettings.NamedValue> table, out float value)
        {
            value = 0f;
            string n = (name ?? "").ToLowerInvariant();
            if (table == null) return false;
            foreach (var e in table)
                if (!string.IsNullOrEmpty(e.match) && n.Contains(e.match.ToLowerInvariant())) { value = e.value; return true; }
            return false;
        }

        // ------------------------------------------------------------------------------------------------ image channels

        /// <summary>Mean of R, G and B (0..1) of an RGBA32 buffer.</summary>
        public static bool ChannelMeans(byte[] rgba, out float r, out float g, out float b)
        {
            r = g = b = 0f;
            if (rgba == null || rgba.Length < 4) return false;
            double sr = 0, sg = 0, sb = 0;
            int n = rgba.Length / 4;
            for (int i = 0; i < n; i++) { sr += rgba[i * 4]; sg += rgba[i * 4 + 1]; sb += rgba[i * 4 + 2]; }
            r = (float)(sr / n / 255.0); g = (float)(sg / n / 255.0); b = (float)(sb / n / 255.0);
            return true;
        }

        /// <summary>Share (0..1) of pixels whose channel (0 = R … 3 = A) lies strictly between low and high (0..1).</summary>
        public static float BandShare(byte[] rgba, int channel, float low, float high)
        {
            if (rgba == null || rgba.Length < 4 || channel < 0 || channel > 3) return 0f;
            int n = rgba.Length / 4, hits = 0;
            for (int i = 0; i < n; i++)
            {
                float v = rgba[i * 4 + channel] / 255f;
                if (v > low && v < high) hits++;
            }
            return hits / (float)n;
        }

        /// <summary>True when a tangent-space normal map's means are biased: R or G off 0.5 by more than the tolerance, or B too low.</summary>
        public static bool NormalBiased(float meanR, float meanG, float meanB, float tolerance, float blueMin)
        {
            return Math.Abs(meanR - 0.5f) > tolerance || Math.Abs(meanG - 0.5f) > tolerance || meanB < blueMin;
        }

        /// <summary>Whether measured LOD triangle counts follow the target ratios of LOD0 (± tolerance); the first failing level in <paramref name="problem"/>.</summary>
        public static bool LodRatiosOk(IList<long> trianglesPerLevel, IList<float> ratios, float tolerance, out string problem)
        {
            problem = null;
            if (trianglesPerLevel == null || trianglesPerLevel.Count < 2 || trianglesPerLevel[0] <= 0) return true;
            for (int i = 1; i < trianglesPerLevel.Count && i - 1 < ratios.Count; i++)
            {
                float r = trianglesPerLevel[i] / (float)trianglesPerLevel[0];
                if (Math.Abs(r - ratios[i - 1]) > tolerance)
                {
                    problem = "LOD" + i + " has " + (r * 100f).ToString("0") + " % of LOD0 (target " + (ratios[i - 1] * 100f).ToString("0") + " %)";
                    return false;
                }
            }
            return true;
        }
    }
}
