using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// world_terraintest: why the arena terrain does not draw what its splat holds (2026-10-31: forest 0.44 on the rim by
    /// world_splat, nothing on screen). Switches every terrain in the open scene between the GK TerrainLit material and the
    /// pipeline's default terrain material, and instanced drawing on or off, without saving; capture with world_shots,
    /// then world_greybox discard (or rebuild) to go back.
    /// </summary>
    static class GKArenaTerrainTest
    {
        /// <summary>The mean sRGB colour of a texture as the GPU samples it at a 4x4 mip (blit, read back).</summary>
        static string MipMean(Texture tex)
        {
            var rt = RenderTexture.GetTemporary(4, 4, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var read = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(0, 0, 4, 4), 0, 0); read.Apply();
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            var px = read.GetPixels32(); Object.DestroyImmediate(read);
            float r = 0, g = 0, b = 0;
            foreach (var p in px) { r += p.r; g += p.g; b += p.b; }
            return string.Format("{0:F0}/{1:F0}/{2:F0}", r / px.Length, g / px.Length, b / px.Length);
        }

        [GKCommand("world_terraintest", "world_terraintest mat=gk|default instanced=on|off hide=<terrain name> magenta=on|<slot> matte=on probe solid=<slot>:r,g,b post=off fog=off smooth=<0..1> flatambient=<grey> heightmap=off details=off forest=off: render tests on the open arena's terrains (scene not saved; magenta edits layer 0's asset in memory, world_greybox restores it)")]
        static void Command(string a)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("world_terraintest: edit mode only"); return; }
            string mat = null, inst = null, hide = null; int magenta = -1, solidSlot = -1; bool matte = false, probe = false, postOff = false, fogOff = false, heightmapOff = false, detailsOff = false, forestOff = false; float smooth = -1f, flatAmbient = -1f; Color solid = Color.black;
            foreach (var part in (a ?? "").Split(' '))
            {
                if (part.StartsWith("mat=")) mat = part.Substring(4);
                if (part.StartsWith("instanced=")) inst = part.Substring(10);
                if (part.StartsWith("hide=")) hide = part.Substring(5).Replace('_', ' ');
                if (part == "matte=on") matte = true;
                if (part == "probe") probe = true;
                if (part == "post=off") postOff = true;
                if (part == "fog=off") fogOff = true;
                // the Critics' shadow-mask ruler (2026-10-31): geometry frames lit by a flat ambient only, the terrain or the woods hidden
                if (part.StartsWith("flatambient=")) flatAmbient = float.Parse(part.Substring(12), System.Globalization.CultureInfo.InvariantCulture);
                if (part == "heightmap=off") heightmapOff = true;
                if (part == "details=off") detailsOff = true;
                if (part == "forest=off") forestOff = true;
                if (part.StartsWith("smooth=")) smooth = float.Parse(part.Substring(7), System.Globalization.CultureInfo.InvariantCulture);
                if (part.StartsWith("solid="))
                {
                    var sp = part.Substring(6).Split(':'); var c = sp[1].Split(',');
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    solidSlot = int.Parse(sp[0]); solid = new Color(float.Parse(c[0], ci), float.Parse(c[1], ci), float.Parse(c[2], ci));
                }
                if (part.StartsWith("magenta=")) magenta = part == "magenta=on" ? 0 : int.Parse(part.Substring(8));
            }
            var gk = AssetDatabase.LoadAssetAtPath<Material>(GKPaths.Generated + "/Environment/TerrainLit.mat");
            foreach (var t in Terrain.activeTerrains)
            {
                if (mat == "default") t.materialTemplate = null;
                else if (mat == "gk" && gk != null) t.materialTemplate = gk;
                if (inst == "on") t.drawInstanced = true;
                else if (inst == "off") t.drawInstanced = false;
                t.drawHeightmap = !heightmapOff && (hide == null || t.name != hide);
                if (detailsOff) t.drawTreesAndFoliage = false;
                if (magenta >= 0 && t.terrainData.terrainLayers.Length > magenta)
                {
                    var mt = new Texture2D(4, 4); var px = new Color[16];
                    for (int i = 0; i < 16; i++) px[i] = Color.magenta;
                    mt.SetPixels(px); mt.Apply();
                    t.terrainData.terrainLayers[magenta].diffuseTexture = mt;   // that slot's layer asset (shared by both terrains)
                    GKBridge.Log("world_terraintest: magenta on slot " + magenta + " = " + t.terrainData.terrainLayers[magenta].name);
                }
                if (solidSlot >= 0 && t.terrainData.terrainLayers.Length > solidSlot)
                {
                    var st = new Texture2D(4, 4); var sp = new Color[16];
                    for (int i = 0; i < 16; i++) sp[i] = solid;
                    st.SetPixels(sp); st.Apply();
                    t.terrainData.terrainLayers[solidSlot].diffuseTexture = st;
                    GKBridge.Log("world_terraintest: solid " + solid + " on slot " + solidSlot + " = " + t.terrainData.terrainLayers[solidSlot].name);
                }
                if (probe)
                    for (int i = 0; i < t.terrainData.terrainLayers.Length; i++)
                    {
                        var l = t.terrainData.terrainLayers[i];
                        var d = l != null ? l.diffuseTexture : null;
                        GKBridge.Log("world_terraintest: " + t.name + " slot " + i + " " + (l != null ? l.name + " #" + l.GetInstanceID() : "null")
                                     + " diffuse " + (d != null ? d.name + " " + d.width + "x" + d.height + " " + d.format + " mips " + d.mipmapCount + " smallest-mip mean " + MipMean(d) : "null")
                                     + " remapMax " + (l != null ? l.diffuseRemapMax.ToString("F2") : "-"));
                    }
                if (smooth >= 0f)   // scales the mask maps' smoothness (alpha), maps kept: is the low sun's forward sheen what brightens Q2?
                    foreach (var l in t.terrainData.terrainLayers) { var m = l.maskMapRemapMax; m.w = smooth; l.maskMapRemapMax = m; l.smoothness = smooth; }
                if (matte)
                    foreach (var l in t.terrainData.terrainLayers) { l.maskMapTexture = null; l.smoothness = 0f; l.metallic = 0f; }
                t.Flush();
                GKBridge.Log("world_terraintest: " + t.name + " material " + (t.materialTemplate != null ? t.materialTemplate.name + " (" + t.materialTemplate.shader.name + ")" : "pipeline default")
                             + ", instanced " + t.drawInstanced + ", drawn " + t.drawHeightmap + ", basemap distance " + t.basemapDistance + ", layers " + t.terrainData.terrainLayers.Length
                             + ", position " + t.transform.position.ToString("F0") + ", size " + t.terrainData.size.ToString("F0"));
            }
            if (fogOff) { RenderSettings.fog = false; GKBridge.Log("world_terraintest: fog off"); }
            if (flatAmbient >= 0f)
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(flatAmbient, flatAmbient, flatAmbient);
                GKBridge.Log("world_terraintest: flat ambient " + flatAmbient);
            }
            if (forestOff) { var f = GameObject.Find("GreyForest"); if (f != null) f.SetActive(false); GKBridge.Log("world_terraintest: forest off"); }
            if (postOff)
                foreach (var v in Object.FindObjectsByType<UnityEngine.Rendering.Volume>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) { v.enabled = false; GKBridge.Log("world_terraintest: volume off " + v.name); }
            SceneView.RepaintAll();
        }
    }
}
