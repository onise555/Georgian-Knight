using System.Text;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>world_splat x z: the terrain layer weights at a world point on every terrain under it, with each layer's
    /// name, and the control-map count. A check that what the splat code paints is what the terrain holds.</summary>
    static class GKArenaSplat
    {
        [GKCommand("world_splat", "world_splat <x> <z>: terrain layer weights at a world point, every terrain (edit or play)")]
        static void Command(string a)
        {
            var p = (a ?? "").Split(' ');
            float x, z;
            if (p.Length < 2 || !float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x)
                || !float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z))
            { GKBridge.Log("world_splat: world_splat <x> <z>"); return; }
            foreach (var t in Terrain.activeTerrains)
            {
                var td = t.terrainData;
                Vector3 o = t.transform.position;
                float nx = (x - o.x) / td.size.x, nz = (z - o.z) / td.size.z;
                if (nx < 0f || nx > 1f || nz < 0f || nz > 1f) continue;
                int ax = Mathf.Clamp((int)(nx * td.alphamapWidth), 0, td.alphamapWidth - 1), az = Mathf.Clamp((int)(nz * td.alphamapHeight), 0, td.alphamapHeight - 1);
                var w = td.GetAlphamaps(ax, az, 1, 1);
                var sb = new StringBuilder("world_splat " + t.name + " (" + td.alphamapTextureCount + " control maps, " + td.terrainLayers.Length + " layers):");
                for (int i = 0; i < td.terrainLayers.Length; i++)
                    if (w[0, 0, i] > 0.005f) sb.AppendFormat(" {0}={1:F2}", td.terrainLayers[i] != null ? td.terrainLayers[i].name : "?", w[0, 0, i]);
                sb.AppendFormat(" | steepness {0:F1}, height {1:F0} m", td.GetSteepness(nx, nz), td.GetInterpolatedHeight(nx, nz) + o.y);
                GKBridge.Log(sb.ToString());
            }
        }
    }
}
