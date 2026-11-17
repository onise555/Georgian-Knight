using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>Procedural dressing: grass clumps, boulders, Georgian banners.</summary>
    public static class GKProps
    {
        static string Abs(string p) { return Path.Combine(Path.GetDirectoryName(Application.dataPath), p); }
        static string Dir { get { return GKPaths.Generated + "/Props"; } }

        static Material LitMaterial(string name, Texture2D baseMap, Color color, float smooth, bool doubleSided, bool alphaClip)
        {
            GKPaths.Ensure(Dir);
            string path = Dir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            m.SetTexture("_BaseMap", baseMap);
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", smooth);
            m.SetFloat("_Metallic", 0f);
            if (doubleSided) { m.SetFloat("_Cull", 0f); m.doubleSidedGI = true; }
            if (alphaClip)
            {
                m.SetFloat("_AlphaClip", 1f);
                m.SetFloat("_Cutoff", 0.42f);
                m.EnableKeyword("_ALPHATEST_ON");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                m.SetOverrideTag("RenderType", "TransparentCutout");
            }
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        static Texture2D SaveTexture(Texture2D t, string name, bool linear, bool alpha)
        {
            string path = Dir + "/" + name + ".png";
            File.WriteAllBytes(Abs(path), t.EncodeToPNG());
            Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti != null)
            {
                ti.sRGBTexture = !linear;
                ti.alphaIsTransparency = alpha;
                ti.mipMapsPreserveCoverage = alpha;
                ti.alphaTestReferenceValue = 0.42f;
                ti.wrapMode = alpha ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // ------------------------------------------------------------------ grass

        /// <summary>The blade atlas; straw = every blade dry and bleached (August stubble and litter).</summary>
        static Texture2D GrassTexture(bool straw = false)
        {
            GKPaths.Ensure(Dir);
            string path = Dir + (straw ? "/grass_blades_straw3_alpha.png" : "/grass_blades_alpha.png");
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;
            const int W = 512, H = 512;
            var px = new Color[W * H];
            for (int i = 0; i < px.Length; i++) px[i] = new Color(0.30f, 0.36f, 0.16f, 0f);
            var rnd = new System.Random(7);
            for (int b = 0; b < 46; b++)
            {
                float x0 = 20 + (float)rnd.NextDouble() * (W - 40);
                float height = H * (0.45f + (float)rnd.NextDouble() * 0.53f);
                float lean = ((float)rnd.NextDouble() - 0.5f) * 140f;
                float width = 5f + (float)rnd.NextDouble() * 7f;
                bool dry = straw || rnd.NextDouble() < 0.28;
                Color baseC = dry ? new Color(0.42f, 0.40f, 0.20f) : new Color(0.17f + (float)rnd.NextDouble() * 0.08f, 0.27f + (float)rnd.NextDouble() * 0.08f, 0.08f);
                Color tipC = dry ? new Color(0.78f, 0.70f, 0.45f) : new Color(0.45f + (float)rnd.NextDouble() * 0.12f, 0.55f + (float)rnd.NextDouble() * 0.08f, 0.22f);
                if (straw)
                {
                    // August upland straw: tan, ochre and grey, not lime (Team Lead 2026-10-31): a muted base and tip, a third
                    // of the colour pulled to grey, some blades still grey-green
                    float k = 0.75f + (float)rnd.NextDouble() * 0.4f;
                    bool greyGreen = rnd.NextDouble() < 0.18;
                    // hue toward the Trialeti references (REFERENCES_meadow.md: low-sun dry field h 84 deg, July meadow 90-99):
                    // less orange than the first muted pass (72-74 deg)
                    baseC = greyGreen ? new Color(0.30f, 0.32f, 0.24f) : new Color(0.38f, 0.36f, 0.27f);
                    tipC = greyGreen ? new Color(0.58f, 0.60f, 0.48f) : new Color(0.70f, 0.67f, 0.50f);
                    baseC *= k; tipC *= k;
                    float lb = baseC.r * 0.3f + baseC.g * 0.59f + baseC.b * 0.11f, lt = tipC.r * 0.3f + tipC.g * 0.59f + tipC.b * 0.11f;
                    baseC = Color.Lerp(baseC, new Color(lb, lb, lb), 0.3f); tipC = Color.Lerp(tipC, new Color(lt, lt, lt), 0.3f);
                }
                for (int y = 0; y < (int)height; y++)
                {
                    float t = y / height;
                    float cx = x0 + lean * t * t;
                    float half = width * (1f - t) * 0.5f + 0.4f;
                    Color c = Color.Lerp(baseC, tipC, Mathf.Pow(t, 0.8f));
                    for (int x = (int)(cx - half - 1); x <= (int)(cx + half + 1); x++)
                    {
                        if (x < 0 || x >= W) continue;
                        float cov = Mathf.Clamp01(half + 0.5f - Mathf.Abs(x - cx));
                        if (cov <= 0f) continue;
                        int i = y * W + x;
                        float shade = 0.85f + 0.15f * (1f - Mathf.Abs(x - cx) / Mathf.Max(0.5f, half));
                        Color cc = c * shade; cc.a = 1f;
                        px[i] = Color.Lerp(px[i], cc, cov);
                        px[i].a = Mathf.Max(px[i].a, cov);
                    }
                }
            }
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, true);
            tex.SetPixels(px); tex.Apply();
            return SaveTexture(tex, straw ? "grass_blades_straw3_alpha" : "grass_blades_alpha", false, true);
        }

        /// <summary>Ankle-high August stubble for the terrain's second detail layer: the grass clump's mesh with straw blades
        /// (the terrain's detail tint does not reach this mesh's material, so the colour is in the texture).</summary>
        public static GameObject StubblePrefab()
        {
            var clump = GrassClumpPrefab();
            var mat = LitMaterial("GrassStubble", GrassTexture(true), StubbleColor, 0.06f, true, true);
            string prefabPath = Dir + "/GrassStubble.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (existing != null || clump == null) return existing;
            var go = new GameObject("GrassStubble");
            go.AddComponent<MeshFilter>().sharedMesh = clump.GetComponent<MeshFilter>().sharedMesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            Object.DestroyImmediate(go);
            return prefab;
        }

        static readonly Color GrassTint = new Color(0.78f, 0.82f, 0.74f);   // grey-green, not saturated lawn green

        /// <summary>A material colour (sRGB, may exceed 1) that brings a texture of the given mean linear albedo to the target
        /// linear albedo, with hue's hue (Docs/World/ALBEDO.md). Grass blades: 0.22 like the meadow they stand in.</summary>
        static Color AlbedoColor(Color hue, float textureAlbedo, float target)
        {
            Color l = hue.linear;
            float lum = 0.2126f * l.r + 0.7152f * l.g + 0.0722f * l.b;
            float k = target / Mathf.Max(1e-5f, textureAlbedo * lum);
            return new Color(l.r * k, l.g * k, l.b * k, 1f).gamma;
        }

        // the blade textures' mean linear albedo over their opaque texels (measured 2026-10-31): green 0.132, straw 0.141. The detail
        // colours (healthy / dry, ~0.8 and ~0.6) multiply after, so the materials aim above 0.22
        static Color GrassColor => GKEnvironment.Current.PhysicalAlbedo ? AlbedoColor(GrassTint, 0.132f, 0.27f) : GrassTint;
        static Color StubbleColor => GKEnvironment.Current.PhysicalAlbedo ? AlbedoColor(new Color(0.86f, 0.84f, 0.8f), 0.141f, 0.37f) : new Color(0.86f, 0.84f, 0.8f);

        public static GameObject GrassClumpPrefab()
        {
            GKPaths.Ensure(Dir);
            string prefabPath = Dir + "/GrassClump.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (existing != null) { LitMaterial("Grass", GrassTexture(), GrassColor, 0.08f, true, true); return existing; }

            var mesh = new Mesh { name = "GrassClump" };
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>(); var col = new List<Color>();
            var rnd = new System.Random(3);
            int blades = 6;
            for (int i = 0; i < blades; i++)
            {
                float ang = i * Mathf.PI / blades + (float)rnd.NextDouble() * 0.4f;
                Vector3 dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                Vector3 off = new Vector3((float)rnd.NextDouble() - 0.5f, 0f, (float)rnd.NextDouble() - 0.5f) * 0.18f;
                float w = 0.32f + (float)rnd.NextDouble() * 0.12f, h = 0.42f + (float)rnd.NextDouble() * 0.2f;
                Vector3 lean = new Vector3((float)rnd.NextDouble() - 0.5f, 0f, (float)rnd.NextDouble() - 0.5f) * 0.12f;
                float u0 = (float)rnd.NextDouble() * 0.5f;
                int b = v.Count;
                v.Add(off - dir * w * 0.5f); v.Add(off + dir * w * 0.5f);
                v.Add(off + dir * w * 0.5f + Vector3.up * h + lean); v.Add(off - dir * w * 0.5f + Vector3.up * h + lean);
                uv.Add(new Vector2(u0, 0f)); uv.Add(new Vector2(u0 + 0.5f, 0f)); uv.Add(new Vector2(u0 + 0.5f, 1f)); uv.Add(new Vector2(u0, 1f));
                for (int k = 0; k < 4; k++) { n.Add(Vector3.up); col.Add(Color.white); }
                tri.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
            }
            mesh.SetVertices(v); mesh.SetNormals(n); mesh.SetUVs(0, uv); mesh.SetColors(col); mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            AssetDatabase.CreateAsset(mesh, Dir + "/GrassClump.mesh");

            var mat = LitMaterial("Grass", GrassTexture(), GrassColor, 0.08f, true, true);
            mat.SetFloat("_ReceiveShadows", 1f);
            var go = new GameObject("GrassClump");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            Object.DestroyImmediate(go);
            return prefab;
        }

        // ------------------------------------------------------------------ greybox trees

        /// <summary>A greybox broadleaf clump (beech / hornbeam) for the terrain's tree system: five low-poly dark-green crowns,
        /// ~22 m across and ~12 m high, pivot at the base, one instanced material. At 1-3 km a forest reads by its broken crest line and dark mass, not by its
        /// texture (the Team Lead, 2026-10-31). Art's CC0 / Meshy trees replace it as the TreePrototype.</summary>
        public static GameObject GreyTreePrefab()
        {
            GKPaths.Ensure(Dir);
            string prefabPath = Dir + "/GreyTree_Clump.prefab";
            // the material first, so a colour change reaches the saved prefab. A beech wood in shade reads L* ~30 at range
            // under the sun of 3.5 (0.11/0.17/0.07 measured L* 48: an orchard, not woods). Instanced: the terrain draws each
            // tree as its own batch otherwise (2026-10-31: 167k single trees = 247k batches, +44 ms GPU, +90 ms CPU)
            // cool grey-green (Critics AR-1: the reference h 145, C* 10; 0.06/0.095/0.04 measured h 101, C* 21, olive)
            // albedo ~4-5 % linear like a broadleaf canopy: 0.065/0.09/0.072 (sRGB, <1 % linear) went black in shade at ambient 1
            // canopy 0.08 linear (the low end of 0.08-0.15: greybox spheres do not self-shadow), a warm green (cool read teal)
            var crownMat = LitMaterial("M_GreyTree_Crown", null, new Color(0.29f, 0.33f, 0.2f), 0f, false, false);
            crownMat.shader = Shader.Find("Universal Render Pipeline/Lit");   // an earlier test left the asset Unlit / emissive
            crownMat.DisableKeyword("_EMISSION");
            // matte: the smooth spheres caught the blue sky at grazing angles, ~10 % of the shaded valley teal (Critics 5a49096,
            // hue ~218); no specular highlight, no environment reflection
            crownMat.SetFloat("_SpecularHighlights", 0f); crownMat.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            crownMat.SetFloat("_EnvironmentReflections", 0f); crownMat.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
            crownMat.enableInstancing = true;
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (existing != null && existing.GetComponent<LODGroup>() == null)
            {
                var contents = PrefabUtility.LoadPrefabContents(prefabPath);
                SingleLod(contents);
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
                PrefabUtility.UnloadPrefabContents(contents);
                existing = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            }
            if (existing != null) return existing;
            // one instance = a clump of five crowns (~22 m across) in one mesh, one material, no trunk (never seen at range):
            // a quarter of the instances for the same cover, 100 triangles each
            var pb = UnityEngine.ProBuilder.ShapeGenerator.GenerateIcosahedron(UnityEngine.ProBuilder.PivotLocation.Center, 4f, 0, true, true);
            pb.ToMesh(); pb.Refresh();
            var unit = Object.Instantiate(pb.GetComponent<MeshFilter>().sharedMesh);
            Object.DestroyImmediate(pb.gameObject);
            var offsets = new[] { new Vector3(0f, 7.5f, 0f), new Vector3(7f, 6.5f, 2.5f), new Vector3(-6f, 7f, 4f), new Vector3(2.5f, 6f, -7f), new Vector3(-4.5f, 6.8f, -5.5f) };
            var parts = new CombineInstance[offsets.Length];
            for (int i = 0; i < offsets.Length; i++)
                parts[i] = new CombineInstance { mesh = unit, transform = Matrix4x4.TRS(offsets[i], Quaternion.Euler(0f, i * 37f, 0f), new Vector3(1f + 0.06f * i, 1.15f - 0.04f * i, 1f)) };
            var mesh = new Mesh { name = "GreyTree_Clump" };
            mesh.CombineMeshes(parts, true, true);
            mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
            Object.DestroyImmediate(unit);
            AssetDatabase.CreateAsset(mesh, Dir + "/GreyTree_Clump.mesh");
            var root = new GameObject("GreyTree_Clump");
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            root.AddComponent<MeshRenderer>().sharedMaterial = crownMat;
            SingleLod(root);
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        /// <summary>One LOD, never culled. A tree prototype without a LODGroup must use Nature/Soft Occlusion, or every terrain
        /// load warns (Player.log, Build &amp; QA 2026-10-31); with one the terrain takes it as a mesh tree. The terrain never draws
        /// the clump (treeDistance 0): GreyForest bakes its own chunks and reads only the material.</summary>
        static void SingleLod(GameObject root)
        {
            var lod = root.GetComponent<LODGroup>();
            if (lod == null) lod = root.AddComponent<LODGroup>();
            lod.SetLODs(new[] { new LOD(0f, root.GetComponentsInChildren<Renderer>()) });
            lod.RecalculateBounds();
        }

        // ------------------------------------------------------------------ scanned rocks (Poly Haven CC0)

        public const string RockModels = "Assets/_GeorgianKnight/Art/Environment/Downloaded/models";

        /// <summary>A rock prefab from a Poly Haven scan in RockModels/&lt;id&gt; (FBX + diffuse, normal, AO, roughness at res),
        /// built to Asset QA's intake (Docs/AssetQA/INTAKE.md, 2026-10-31): base colour pulled toward grey (the scans are warm
        /// South-African stone; Didgori's is grey basalt and andesite), AO as occlusion, roughness as smoothness, LOD levels
        /// over 20k triangles dropped, one convex collider on the lightest LOD. Placed at 0.8-1.3x its real size, never
        /// stretched (a pebble blown up to a boulder has grain 10-20x too big). Null if the model is not there.</summary>
        public static GameObject RockPrefab(string id, string res)
        {
            string dir = RockModels + "/" + id, fbx = dir + "/" + id + "_" + res + ".fbx";
            // a scan shipped as one heavy mesh gets LOD0-3 made in Blender (Asset QA, 2026-10-31): <id>_<res>_lods.fbx, its
            // meshes named _LOD0.._LOD3 so the importer builds the LODGroup
            string lodFbx = dir + "/" + id + "_" + res + "_lods.fbx";
            if (AssetImporter.GetAtPath(lodFbx) != null) fbx = lodFbx;
            var mi = AssetImporter.GetAtPath(fbx) as ModelImporter;
            if (mi == null) { GKBridge.Log("rocks: model missing " + fbx); return null; }
            if (mi.materialImportMode != ModelImporterMaterialImportMode.None || mi.animationType != ModelImporterAnimationType.None)
            {
                mi.materialImportMode = ModelImporterMaterialImportMode.None;
                mi.animationType = ModelImporterAnimationType.None;
                mi.importAnimation = false;
                mi.SaveAndReimport();
            }
            string Map(string tag) { return dir + "/" + id + "_" + tag + "_" + res + ".jpg"; }
            var nor = ImportAs(Map("nor_gl"), TextureImporterType.NormalMap, false, true);
            var ao = ImportAs(Map("ao"), TextureImporterType.Default, false, false);
            var diff = ImportAs(Map("diff"), TextureImporterType.Default, true, true);
            var rough = ImportAs(Map("rough"), TextureImporterType.Default, true, false);

            var mat = LitMaterial("M_Rock2_" + id, GreyBase(id, diff), Color.white, 0.85f, false, false);
            if (nor != null) { mat.SetTexture("_BumpMap", nor); mat.EnableKeyword("_NORMALMAP"); }
            if (ao != null) { mat.SetTexture("_OcclusionMap", ao); mat.SetFloat("_OcclusionStrength", 1f); mat.EnableKeyword("_OCCLUSIONMAP"); }
            var ms = SmoothnessMap(id, rough);
            if (ms != null) { mat.SetTexture("_MetallicGlossMap", ms); mat.SetFloat("_SmoothnessTextureChannel", 0f); mat.EnableKeyword("_METALLICSPECGLOSSMAP"); }
            EditorUtility.SetDirty(mat);

            string prefabPath = Dir + "/Rock2_" + id + ".prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (existing != null) return existing;
            var go = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(fbx));
            go.name = "Rock2_" + id;
            var lg = go.GetComponent<LODGroup>();
            if (lg != null)
            {
                var lods = new List<LOD>(lg.GetLODs());
                while (lods.Count > 1 && Triangles(lods[0].renderers) > 20000)
                {
                    foreach (var r in lods[0].renderers) if (r != null) Object.DestroyImmediate(r.gameObject);
                    var first = lods[0]; lods.RemoveAt(0);
                    var top = lods[0]; top.screenRelativeTransitionHeight = Mathf.Max(top.screenRelativeTransitionHeight, first.screenRelativeTransitionHeight); lods[0] = top;
                }
                lg.SetLODs(lods.ToArray());
                lg.RecalculateBounds();
            }
            Mesh lightest = null; GameObject holder = null; int fewest = int.MaxValue;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                mf.GetComponent<MeshRenderer>().sharedMaterial = mat;
                int t = mf.sharedMesh != null ? mf.sharedMesh.triangles.Length / 3 : int.MaxValue;
                if (t < fewest) { fewest = t; lightest = mf.sharedMesh; holder = mf.gameObject; }
            }
            if (holder != null) { var mc = holder.AddComponent<MeshCollider>(); mc.sharedMesh = lightest; mc.convex = true; }
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            Object.DestroyImmediate(go);
            return prefab;
        }

        /// <summary>The real size of a rock prefab (renderer bounds at scale 1), metres.</summary>
        public static Vector3 RockSize(GameObject prefab)
        {
            var rs = prefab.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return Vector3.one;
            Bounds b = rs[0].bounds; for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b.size;
        }

        static int Triangles(Renderer[] rs)
        {
            int n = 0;
            foreach (var r in rs) { var mf = r != null ? r.GetComponent<MeshFilter>() : null; if (mf != null && mf.sharedMesh != null) n += mf.sharedMesh.triangles.Length / 3; }
            return n;
        }

        static Texture2D ImportAs(string path, TextureImporterType type, bool readable, bool srgb)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return null;
            if (ti.textureType != type || ti.isReadable != readable || ti.sRGBTexture != srgb)
            {
                ti.textureType = type; ti.isReadable = readable; ti.sRGBTexture = srgb;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Texture2D GreyBase(string id, Texture2D diff)
        {
            string name = "T_Rock2_" + id + "_BaseGrey";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + "/" + name + ".png");
            if (existing != null || diff == null) return existing != null ? existing : diff;
            var px = diff.GetPixels();
            for (int i = 0; i < px.Length; i++)
            {
                // keep ~40 % of the colour around the luminance (saturation ~0.2), a touch cool: grey volcanic stone
                Color c = px[i]; float l = c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;
                Color g = new Color(l * 0.98f, l, l * 1.03f);
                px[i] = Color.Lerp(g, c, 0.4f); px[i].a = 1f;
            }
            var t = new Texture2D(diff.width, diff.height, TextureFormat.RGBA32, true);
            t.SetPixels(px); t.Apply();
            return SaveTexture(t, name, false, false);
        }

        static Texture2D SmoothnessMap(string id, Texture2D rough)
        {
            string name = "T_Rock2_" + id + "_MetalSmooth";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + "/" + name + ".png");
            if (existing != null || rough == null) return existing;
            var px = rough.GetPixels();
            for (int i = 0; i < px.Length; i++) px[i] = new Color(0f, 0f, 0f, 1f - px[i].r);   // R metallic 0, A smoothness
            var t = new Texture2D(rough.width, rough.height, TextureFormat.RGBA32, true);
            t.SetPixels(px); t.Apply();
            return SaveTexture(t, name, true, true);
        }

        // ------------------------------------------------------------------ rocks

        public static Material RockMaterial(GKDownloads.PbrSet set)
        {
            var m = LitMaterial("Rock", set != null ? set.diffuse : null, new Color(0.86f, 0.85f, 0.83f), 0.5f, false, false);
            if (set != null && set.normal != null) { m.SetTexture("_BumpMap", set.normal); m.EnableKeyword("_NORMALMAP"); }
            if (set != null && set.mask != null)
            {
                m.SetTexture("_MetallicGlossMap", set.mask); m.EnableKeyword("_METALLICSPECGLOSSMAP");
                m.SetTexture("_OcclusionMap", set.mask); m.EnableKeyword("_OCCLUSIONMAP"); m.SetFloat("_OcclusionStrength", 1f);
                m.SetFloat("_Smoothness", 0.6f);
            }
            m.SetTextureScale("_BaseMap", new Vector2(1.5f, 1.5f));
            EditorUtility.SetDirty(m);
            return m;
        }

        static float Noise3(Vector3 p)
        {
            return (Mathf.PerlinNoise(p.x + 13.1f, p.y - 7.7f) + Mathf.PerlinNoise(p.y + 3.3f, p.z + 19.9f) + Mathf.PerlinNoise(p.z - 5.5f, p.x + 2.2f)) / 1.5f - 1f;
        }

        public static List<Mesh> BoulderMeshes(int count)
        {
            GKPaths.Ensure(Dir);
            var list = new List<Mesh>();
            for (int k = 0; k < count; k++)
            {
                string path = Dir + "/Boulder_" + k + ".mesh";
                var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (existing != null) { list.Add(existing); continue; }
                var mesh = Icosphere(4);
                var rnd = new System.Random(100 + k * 17);
                var verts = mesh.vertices;
                var cuts = new List<Vector4>();
                int nCuts = 5 + rnd.Next(5);
                for (int c = 0; c < nCuts; c++)
                {
                    Vector3 nrm = new Vector3((float)rnd.NextDouble() * 2f - 1f, (float)rnd.NextDouble() * 1.4f - 0.4f, (float)rnd.NextDouble() * 2f - 1f).normalized;
                    cuts.Add(new Vector4(nrm.x, nrm.y, nrm.z, 0.62f + (float)rnd.NextDouble() * 0.28f));
                }
                Vector3 seed = new Vector3(k * 3.7f, k * 1.3f, k * 5.1f);
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 p = verts[i].normalized;
                    float r = 1f + 0.22f * Noise3(p * 1.4f + seed) + 0.07f * Noise3(p * 4.2f + seed * 2f) + 0.025f * Noise3(p * 11f + seed);
                    p *= r;
                    foreach (var c in cuts)
                    {
                        Vector3 cn = new Vector3(c.x, c.y, c.z);
                        float dd = Vector3.Dot(p, cn) - c.w;
                        if (dd > 0f) p -= cn * dd * 0.92f;
                    }
                    if (p.y < -0.38f) p.y = -0.38f + (p.y + 0.38f) * 0.15f;
                    verts[i] = p;
                }
                mesh.vertices = verts;
                var uvs = new Vector2[verts.Length];
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 p = verts[i];
                    float u = Mathf.Abs(Mathf.Atan2(p.z, p.x)) / Mathf.PI * 2.2f;
                    uvs[i] = new Vector2(u, p.y * 1.1f + 0.5f);
                }
                mesh.uv = uvs;
                mesh.RecalculateNormals();
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                mesh.name = "Boulder_" + k;
                AssetDatabase.CreateAsset(mesh, path);
                list.Add(mesh);
            }
            return list;
        }

        static Mesh Icosphere(int subdiv)
        {
            float t = (1f + Mathf.Sqrt(5f)) / 2f;
            var v = new List<Vector3>
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
            };
            for (int i = 0; i < v.Count; i++) v[i] = v[i].normalized;
            var f = new List<int>
            {
                0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11, 1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
                3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9, 4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1
            };
            var cache = new Dictionary<long, int>();
            System.Func<int, int, int> mid = (a, b) =>
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                int idx;
                if (cache.TryGetValue(key, out idx)) return idx;
                v.Add(((v[a] + v[b]) * 0.5f).normalized);
                idx = v.Count - 1;
                cache[key] = idx;
                return idx;
            };
            for (int s = 0; s < subdiv; s++)
            {
                var nf = new List<int>();
                for (int i = 0; i < f.Count; i += 3)
                {
                    int a = f[i], b = f[i + 1], c = f[i + 2];
                    int ab = mid(a, b), bc = mid(b, c), ca = mid(c, a);
                    nf.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
                }
                f = nf;
            }
            var m = new Mesh();
            m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(v);
            m.SetTriangles(f, 0);
            return m;
        }

        // ------------------------------------------------------------------ Georgian banner

        /// <summary>A period Georgian field banner (owner decision 2026-10-20, HISTORY_DIDGORI_REFERENCE §3): one plain
        /// Latin cross in undyed linen on a madder-crimson field, an ochre hem. Never the five-cross flag (first
        /// attested 1367). Icon banners (St George, the Theotokos) come from Art &amp; Assets later.</summary>
        static Texture2D FlagTexture(bool regenerate = false)
        {
            GKPaths.Ensure(Dir);
            const string name = "T_Banner_CrossCrimson_BaseColor";
            string path = Dir + "/" + name + ".png";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null && !regenerate) return existing;
            const int W = 512, H = 768;
            var px = new Color[W * H];
            Color linen = new Color(0.86f, 0.82f, 0.72f), crimson = new Color(0.50f, 0.07f, 0.08f), ochre = new Color(0.62f, 0.45f, 0.18f);
            // a Latin cross with a long foot, as on a processional banner: an equal-armed white cross on red reads as
            // the Swiss flag, a red one on white as the Red Cross, and an edge-to-edge one as a modern national flag
            float cx = W * 0.5f, top = H * 0.13f, foot = H * 0.80f, barY = H * 0.33f;
            float half = W * 0.05f, arm = W * 0.24f, hem = W * 0.035f;
            var rnd = new System.Random(11);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float fromTop = H - 1 - y, dx = Mathf.Abs(x - cx);
                    bool cross = (dx < half && fromTop > top && fromTop < foot) || (Mathf.Abs(fromTop - barY) < half && dx < arm);
                    bool border = x < hem || x >= W - hem || fromTop < hem;
                    Color c = cross ? linen : border ? ochre : crimson;
                    // weave + soiling for a used field banner (dirtier toward the hem that flaps low)
                    float weave = ((x % 3 == 0) ? 0.97f : 1f) * ((y % 3 == 0) ? 0.98f : 1f);
                    float dirt = 1f - 0.12f * Mathf.Pow(1f - y / (float)H, 3f) - 0.05f * Mathf.PerlinNoise(x * 0.02f, y * 0.02f);
                    float n = 1f + ((float)rnd.NextDouble() - 0.5f) * 0.04f;
                    c *= weave * dirt * n; c.a = 1f;
                    px[y * W + x] = c;
                }
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, true);
            tex.SetPixels(px); tex.Apply();
            return SaveTexture(tex, name, false, false);
        }

        /// <summary>Points the shared banner material at the period banner. Didgori.unity and Intro_Cutscene.unity use
        /// this material asset, so both change without opening or saving either scene.</summary>
        [GKCommand("world_banner", "world_banner: (re)draws the banner, a plain Latin cross on crimson (no five-cross flag), into the shared material; no scene is touched")]
        static void RefreshBanner(string a)
        {
            var m = LitMaterial("GeorgianBanner", FlagTexture(true), Color.white, 0.12f, true, false);
            AssetDatabase.SaveAssets();
            GKBridge.Log("world_banner: " + AssetDatabase.GetAssetPath(m) + " -> " + AssetDatabase.GetAssetPath(m.GetTexture("_BaseMap")));
        }

        public static GameObject Banner()
        {
            var root = new GameObject("Georgian Banner");
            var wood = LitMaterial("BannerWood", null, new Color(0.20f, 0.13f, 0.08f), 0.18f, false, false);
            var iron = LitMaterial("BannerIron", null, new Color(0.25f, 0.24f, 0.23f), 0.55f, false, false);
            iron.SetFloat("_Metallic", 0.9f);
            var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.DestroyImmediate(pole.GetComponent<Collider>());
            pole.name = "Pole";
            pole.transform.SetParent(root.transform, false);
            pole.transform.localPosition = new Vector3(0f, 2.3f, 0f);
            pole.transform.localScale = new Vector3(0.075f, 2.3f, 0.075f);
            pole.GetComponent<Renderer>().sharedMaterial = wood;
            var bar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.DestroyImmediate(bar.GetComponent<Collider>());
            bar.name = "Crossbar";
            bar.transform.SetParent(root.transform, false);
            bar.transform.localPosition = new Vector3(0f, 4.15f, 0f);
            bar.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            bar.transform.localScale = new Vector3(0.05f, 0.62f, 0.05f);
            bar.GetComponent<Renderer>().sharedMaterial = wood;
            var finial = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(finial.GetComponent<Collider>());
            finial.name = "Finial";
            finial.transform.SetParent(root.transform, false);
            finial.transform.localPosition = new Vector3(0f, 4.66f, 0f);
            finial.transform.localScale = new Vector3(0.11f, 0.16f, 0.11f);
            finial.GetComponent<Renderer>().sharedMaterial = iron;
            var col = root.AddComponent<CapsuleCollider>();
            col.center = new Vector3(0f, 2.3f, 0f); col.height = 4.6f; col.radius = 0.08f;

            // cloth gonfalon hanging from the crossbar
            var cloth = new GameObject("Cloth");
            cloth.transform.SetParent(root.transform, false);
            cloth.transform.localPosition = new Vector3(0f, 4.12f, 0.04f);
            int nx = 12, ny = 18;
            float w = 1.1f, h = 1.75f;
            var mesh = new Mesh { name = "BannerCloth" };
            var v = new Vector3[nx * ny]; var uv = new Vector2[nx * ny]; var tri = new List<int>();
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    float u = x / (float)(nx - 1), t = y / (float)(ny - 1);
                    float tail = t > 0.82f ? (1f - Mathf.Abs(u - 0.5f) * 2f) * (t - 0.82f) * 0.9f : 0f; // swallow-tail point
                    v[y * nx + x] = new Vector3((u - 0.5f) * w, -t * h - tail, 0f);
                    uv[y * nx + x] = new Vector2(u, 1f - t);
                    if (x < nx - 1 && y < ny - 1)
                    {
                        int i = y * nx + x;
                        tri.AddRange(new[] { i, i + 1, i + nx, i + 1, i + nx + 1, i + nx });
                    }
                }
            mesh.vertices = v; mesh.uv = uv; mesh.SetTriangles(tri, 0);
            mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
            GKPaths.Ensure(Dir);
            string mp = Dir + "/BannerCloth.mesh";
            var saved = AssetDatabase.LoadAssetAtPath<Mesh>(mp);
            if (saved == null) { AssetDatabase.CreateAsset(mesh, mp); saved = mesh; }
            var smr = cloth.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = saved;
            var flagMat = LitMaterial("GeorgianBanner", FlagTexture(), Color.white, 0.12f, true, false);
            smr.sharedMaterial = flagMat;
            var c = cloth.AddComponent<Cloth>();
            var coeffs = c.coefficients;
            for (int i = 0; i < coeffs.Length; i++)
            {
                bool top = saved.vertices[i].y > -0.01f;
                coeffs[i].maxDistance = top ? 0f : 3f;
                coeffs[i].collisionSphereDistance = 0f;
            }
            c.coefficients = coeffs;
            c.stretchingStiffness = 0.95f;
            c.bendingStiffness = 0.15f;
            c.damping = 0.12f;
            c.useGravity = true;
            c.externalAcceleration = new Vector3(1.6f, 0f, 1.0f);
            c.randomAcceleration = new Vector3(3.5f, 0.5f, 3.5f);
            c.worldVelocityScale = 0.4f;
            return root;
        }
    }
}
