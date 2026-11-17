using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Bakur, the hero built in Blender from MPFB2 (CC0) with his full kit (CHARACTERS.md §3): import as a Humanoid with
    /// his face shapes, texture settings, URP materials per part, and the player's visual instance. The helmet set
    /// (coif, spangenhelm, brow band, aventail) is worn by default and the hair hidden; the dialogue scenes swap them.
    /// </summary>
    public static class GKHero
    {
        public const string Dir = GKPaths.Characters + "/Bakur";
        public const string Fbx = Dir + "/Bakur.fbx";
        public const string Tex = Dir + "/Textures";
        public const string MatDir = GKPaths.Materials + "/Bakur";
        public const float Height = 1.78f;

        public static bool Available { get { return AssetDatabase.LoadAssetAtPath<GameObject>(Fbx) != null; } }

        [GKCommand("hero", "Bakur: humanoid import with face shapes, textures, materials")]
        static void Cmd(string a) { Prepare(); }

        public static void Prepare()
        {
            if (!Available) { GKBridge.Log("hero: no " + Fbx); return; }
            var mi = AssetImporter.GetAtPath(Fbx) as ModelImporter;
            bool dirty = false;
            if (!mi.importBlendShapes) { mi.importBlendShapes = true; dirty = true; }
            if (mi.importBlendShapeNormals != ModelImporterNormals.None) { mi.importBlendShapeNormals = ModelImporterNormals.None; dirty = true; }
            if (mi.materialImportMode != ModelImporterMaterialImportMode.ImportViaMaterialDescription)
            { mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription; dirty = true; }
            if (mi.importTangents != ModelImporterTangents.CalculateMikk) { mi.importTangents = ModelImporterTangents.CalculateMikk; dirty = true; }
            // Bakur is authored at true metric scale in Blender (the body is 1.78 m): no bounds-based rescale, whose
            // skinned bounds and helmet would shrink him
            if (Mathf.Abs(mi.globalScale - 1f) > 1e-5f || !mi.useFileScale) { mi.globalScale = 1f; mi.useFileScale = true; dirty = true; }
            if (dirty) mi.SaveAndReimport();
            TextureSettings();
            GKCharacters.PrepareHumanoid(Fbx, true, 0f);
            GKBridge.Log("hero: body height " + BodyHeight().ToString("F3") + " m (target " + Height.ToString("F2") + ")");
            BuildMaterials();
            AssetDatabase.SaveAssets();
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
            int shapes = model.GetComponentsInChildren<SkinnedMeshRenderer>().Sum(r => r.sharedMesh != null ? r.sharedMesh.blendShapeCount : 0);
            int tris = model.GetComponentsInChildren<SkinnedMeshRenderer>().Sum(r => r.sharedMesh != null ? r.sharedMesh.triangles.Length / 3 : 0);
            GKBridge.Log("hero: " + model.GetComponentsInChildren<SkinnedMeshRenderer>().Length + " meshes, " + tris + " tris, " + shapes + " blend shapes");
        }

        /// <summary>Height of the skinned body mesh in the bind pose (baked vertices, world space).</summary>
        static float BodyHeight()
        {
            var inst = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Fbx));
            try
            {
                var smr = inst.GetComponentsInChildren<SkinnedMeshRenderer>().FirstOrDefault(r => r.name == "Bakur_Body");
                if (smr == null) return 0f;
                var m = new Mesh();
                smr.BakeMesh(m);                        // local, unscaled: TransformPoint applies the full TRS
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var v in m.vertices) { float y = smr.transform.TransformPoint(v).y; lo = Mathf.Min(lo, y); hi = Mathf.Max(hi, y); }
                Object.DestroyImmediate(m);
                return hi - lo;
            }
            finally { Object.DestroyImmediate(inst); }
        }

        // ------------------------------------------------------------------ textures

        const float BeardCutoff = 0.4f;

        static void TextureSettings()
        {
            if (!AssetDatabase.IsValidFolder(Tex)) return;
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { Tex }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                var ti = AssetImporter.GetAtPath(p) as TextureImporter;
                if (ti == null) continue;
                string n = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                bool changed = false;
                bool data = n.Contains("metallic") || n.Contains("occlusion") || n.EndsWith("_mr") || n.Contains("mask") || n.Contains("strands")
                            || n.EndsWith("_rough") || n.StartsWith("bakur_beardcards");
                bool normal = n.Contains("normal");
                if (normal && ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; changed = true; }
                if (!normal && ti.sRGBTexture == data) { ti.sRGBTexture = !data; changed = true; }
                if (n.StartsWith("border"))
                {
                    if (ti.wrapModeU != TextureWrapMode.Repeat || ti.wrapModeV != TextureWrapMode.Clamp)
                    { ti.wrapModeU = TextureWrapMode.Repeat; ti.wrapModeV = TextureWrapMode.Clamp; changed = true; }
                }
                else if (n.StartsWith("emblem") || n.Contains("mask"))
                {
                    if (ti.wrapMode != TextureWrapMode.Clamp) { ti.wrapMode = TextureWrapMode.Clamp; changed = true; }
                }
                else if (n.StartsWith("gk_skinlut"))
                {
                    if (ti.wrapMode != TextureWrapMode.Clamp || ti.mipmapEnabled) { ti.wrapMode = TextureWrapMode.Clamp; ti.mipmapEnabled = false; changed = true; }
                    if (ti.textureCompression != TextureImporterCompression.Uncompressed) { ti.textureCompression = TextureImporterCompression.Uncompressed; changed = true; }
                }
                else if (n.StartsWith("bakur_mail") || n.Contains("strands") || n.StartsWith("bakur_weave") || n.Contains("pores"))
                {
                    if (ti.wrapMode != TextureWrapMode.Repeat) { ti.wrapMode = TextureWrapMode.Repeat; changed = true; }
                }
                if (n.Contains("eyebrow") || n.Contains("eyelash") || n.Contains("short") || n.Contains("albedo"))
                {
                    if (!ti.mipMapsPreserveCoverage) { ti.mipMapsPreserveCoverage = true; ti.alphaTestReferenceValue = 0.5f; changed = true; }
                    if (!ti.alphaIsTransparency) { ti.alphaIsTransparency = true; changed = true; }
                }
                // the strand tile's mips must hold plain averages (coverage fraction, mean length): the shell shader
                // reads them as densities at a distance
                if (n.Contains("strands"))
                {
                    if (ti.mipMapsPreserveCoverage || ti.alphaIsTransparency) { ti.mipMapsPreserveCoverage = false; ti.alphaIsTransparency = false; changed = true; }
                    if (ti.filterMode != FilterMode.Trilinear) { ti.filterMode = FilterMode.Trilinear; changed = true; }
                    if (ti.textureCompression != TextureImporterCompression.Uncompressed) { ti.textureCompression = TextureImporterCompression.Uncompressed; changed = true; }
                }
                // the card atlas: data (linear), clamped, and mips that keep each clump's alpha-tested coverage so the beard
                // does not thin out with distance
                if (n.StartsWith("bakur_beardcards"))
                {
                    if (ti.wrapMode != TextureWrapMode.Clamp) { ti.wrapMode = TextureWrapMode.Clamp; changed = true; }
                    if (!ti.mipMapsPreserveCoverage || Mathf.Abs(ti.alphaTestReferenceValue - BeardCutoff) > 1e-3f)
                    { ti.mipMapsPreserveCoverage = true; ti.alphaTestReferenceValue = BeardCutoff; changed = true; }
                    if (ti.filterMode != FilterMode.Trilinear) { ti.filterMode = FilterMode.Trilinear; changed = true; }
                    if (ti.textureCompression != TextureImporterCompression.CompressedHQ) { ti.textureCompression = TextureImporterCompression.CompressedHQ; changed = true; }
                    if (ti.anisoLevel != 4) { ti.anisoLevel = 4; changed = true; }
                }
                int max = n.StartsWith("bakur_cloth") ? 4096 : 2048;
                if (ti.maxTextureSize != max) { ti.maxTextureSize = max; changed = true; }
                if (changed) ti.SaveAndReimport();
            }
        }

        static Texture2D T(string file)
        {
            return AssetDatabase.LoadAssetAtPath<Texture2D>(Tex + "/" + file);
        }

        // ------------------------------------------------------------------ materials

        static Material LitMat(string name, Texture2D baseMap, Texture2D metalGloss, Texture2D normal, Texture2D occlusion,
                               float smoothness, float metallic, bool alphaClip = false, float cutoff = 0.5f, bool twoSided = false)
        {
            GKPaths.Ensure(MatDir);
            string path = MatDir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            if (m == null) { m = new Material(lit); AssetDatabase.CreateAsset(m, path); }
            m.shader = lit;
            m.SetTexture("_BaseMap", baseMap);
            m.SetColor("_BaseColor", Color.white);
            if (metalGloss != null)
            {
                m.SetTexture("_MetallicGlossMap", metalGloss); m.EnableKeyword("_METALLICSPECGLOSSMAP");
                m.SetFloat("_Smoothness", 1f); m.SetFloat("_Metallic", 1f);
            }
            else
            {
                m.SetTexture("_MetallicGlossMap", null); m.DisableKeyword("_METALLICSPECGLOSSMAP");
                m.SetFloat("_Smoothness", smoothness); m.SetFloat("_Metallic", metallic);
            }
            m.SetFloat("_SmoothnessTextureChannel", 0f);
            if (normal != null) { m.SetTexture("_BumpMap", normal); m.SetFloat("_BumpScale", 1f); m.EnableKeyword("_NORMALMAP"); }
            else { m.SetTexture("_BumpMap", null); m.DisableKeyword("_NORMALMAP"); }
            if (occlusion != null) { m.SetTexture("_OcclusionMap", occlusion); m.SetFloat("_OcclusionStrength", 1f); m.EnableKeyword("_OCCLUSIONMAP"); }
            else { m.SetTexture("_OcclusionMap", null); m.DisableKeyword("_OCCLUSIONMAP"); }
            m.SetFloat("_AlphaClip", alphaClip ? 1f : 0f);
            m.SetFloat("_Cutoff", cutoff);
            if (alphaClip) { m.EnableKeyword("_ALPHATEST_ON"); m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest; m.SetOverrideTag("RenderType", "TransparentCutout"); }
            else { m.DisableKeyword("_ALPHATEST_ON"); m.renderQueue = -1; m.SetOverrideTag("RenderType", "Opaque"); }
            m.SetFloat("_Cull", twoSided ? 0f : 2f);
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>Skin on GK/Skin: pre-integrated scattering, blurred diffuse normals, dual-lobe specular, pores.</summary>
        static Material SkinMat(string name)
        {
            var sh = Shader.Find("GK/Skin");
            if (sh == null) { GKBridge.Log("hero: shader GK/Skin missing, skin on URP Lit"); return LitMat(name, T("Bakur_Skin_BaseMap.png"), null, null, null, 0.5f, 0f); }
            GKPaths.Ensure(MatDir);
            string path = MatDir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(sh); AssetDatabase.CreateAsset(m, path); }
            m.shader = sh;
            m.SetTexture("_BaseMap", T("Bakur_Skin_BaseMap.png"));
            m.SetColor("_BaseColor", Color.white);
            m.SetTexture("_BumpMap", T("Bakur_Skin_Normal.png"));
            m.SetFloat("_BumpScale", 1f);
            m.SetTexture("_RoughMap", T("Bakur_Skin_Rough.png"));
            m.SetFloat("_RoughnessScale", 1f);
            m.SetTexture("_PoreMap", T("Bakur_Skin_Pores.png"));
            m.SetFloat("_PoreTiling", 48f);
            m.SetFloat("_PoreStrength", 0.45f);
            m.SetTexture("_SkinLUT", T("GK_SkinLUT.png"));
            m.SetFloat("_CurvatureScale", 1f);
            m.SetFloat("_BlurBias", 3f);
            m.SetFloat("_LobeMix", 0.85f);
            m.SetFloat("_SpecularOcclusion", 0.6f);
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>The fabric atlas on GK/Cloth: sheen, wrapped diffuse and the woven detail (UV1, 8 mm tiles).</summary>
        static Material ClothMat(string name)
        {
            var sh = Shader.Find("GK/Cloth");
            if (sh == null) { GKBridge.Log("hero: shader GK/Cloth missing, cloth on URP Lit"); return Atlas(name, 0.25f); }
            GKPaths.Ensure(MatDir);
            string path = MatDir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(sh); AssetDatabase.CreateAsset(m, path); }
            m.shader = sh;
            m.SetTexture("_BaseMap", T(name + "_BaseMap.png"));
            m.SetColor("_BaseColor", Color.white);
            m.SetTexture("_MetallicGlossMap", T(name + "_MetallicGloss.png"));
            m.SetTexture("_BumpMap", T(name + "_Normal.png"));
            m.SetFloat("_BumpScale", 1f);
            m.SetTexture("_OcclusionMap", T(name + "_Occlusion.png"));
            m.SetFloat("_OcclusionStrength", 1f);
            m.SetTexture("_WeaveMap", T("Bakur_Weave_Normal.png"));
            m.SetFloat("_WeaveScale", 1f);
            m.SetFloat("_WeaveStrength", 0.8f);
            m.SetFloat("_SheenTint", 0.9f);              // sheen ~ sqrt(albedo) (Filament)
            m.SetFloat("_SheenRoughness", 0.55f);
            m.SetFloat("_Wrap", 0.4f);                    // thick wool: light wraps round the folds
            m.SetFloat("_ClothSmoothnessMax", 0.36f);
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material Atlas(string name, float fallbackSmooth)
        {
            return LitMat(name, T(name + "_BaseMap.png"), T(name + "_MetallicGloss.png"), T(name + "_Normal.png"), T(name + "_Occlusion.png"), fallbackSmooth, 0f);
        }

        public static void BuildMaterials()
        {
            ClothMat("Bakur_Cloth");
            Atlas("Bakur_Armour", 0.5f);
            Atlas("Bakur_Helmet", 0.5f);
            Atlas("Bakur_Mail", 0.5f);
            LitMat("Bakur_Border", T("border_albedo.png"), T("border_mr.png"), T("border_normal.png"), null, 0.5f, 0.85f, true, 0.5f);
            LitMat("Bakur_Emblem", T("emblem_albedo.png"), T("emblem_mr.png"), T("emblem_normal.png"), null, 0.5f, 0.85f, true, 0.5f);
            // worn gilded copper, not new gold: a darker, redder tint and a duller polish
            var band = LitMat("Bakur_BrowBand", T("border_albedo.png"), null, T("border_normal.png"), null, 0.48f, 1f);
            band.SetColor("_BaseColor", new Color(0.80f, 0.66f, 0.54f));
            SkinMat("Bakur_Skin");
            LitMat("Bakur_Eyes", T("Bakur_Eyes_BaseMap.png"), null, null, null, 0.92f, 0f);
            LitMat("Bakur_Brows", T("eyebrow001.png"), null, null, null, 0.2f, 0f, true, 0.35f, true);
            LitMat("Bakur_Lashes", T("eyelashes01.png"), null, null, null, 0.2f, 0f, true, 0.5f, true);
            LitMat("Bakur_Teeth", T("teeth.png"), null, null, null, 0.6f, 0f);
            LitMat("Bakur_Tongue", T("tongue01_diffuse.png"), null, null, null, 0.55f, 0f);
            LitMat("Bakur_Hair", T("short02_diffuse.png"), null, T("short02_normal.png"), null, 0.35f, 0f, true, 0.4f, true);
            // the beard: hair cards (strand-clump atlas, alpha test with coverage-preserving mips, Kajiya-Kay highlights)
            string bp = MatDir + "/Bakur_Beard.mat";
            var sh = Shader.Find("GK/HairCard");
            if (sh != null)
            {
                var b = AssetDatabase.LoadAssetAtPath<Material>(bp);
                if (b == null) { b = new Material(sh); AssetDatabase.CreateAsset(b, bp); }
                b.shader = sh;
                b.SetTexture("_BaseMap", T("Bakur_BeardCards.png"));
                b.SetFloat("_Cutoff", BeardCutoff);
                // dark chestnut (bible), lighter toward the tips; sRGB values (Unity linearises material colours)
                b.SetColor("_RootColor", new Color(0.18f, 0.125f, 0.09f));
                b.SetColor("_TipColor", new Color(0.33f, 0.235f, 0.165f));
                b.SetFloat("_TintVariation", 0.3f);
                b.SetFloat("_LayerShadow", 0.4f);
                b.SetFloat("_Spec1", 0.22f);
                b.SetFloat("_Spec2", 0.45f);
                b.SetFloat("_Gloss1", 120f);
                b.SetFloat("_Gloss2", 28f);
                b.SetFloat("_Wrap", 0.5f);
                b.SetFloat("_Transmission", 0.3f);
                b.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                EditorUtility.SetDirty(b);
            }
            else GKBridge.Log("hero: shader GK/HairCard missing");
        }

        static Material M(string name) { return AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/" + name + ".mat"); }

        /// <summary>The material for one submesh, from the renderer and the Blender slot name.</summary>
        public static Material MaterialFor(string renderer, string slot)
        {
            string s = (slot ?? "").ToLowerInvariant();
            if (s.Contains("coatborder")) return M("Bakur_Border");
            if (s.Contains("coatemblem")) return M("Bakur_Emblem");
            if (s.Contains("browband")) return M("Bakur_BrowBand");
            if (s.Contains("beard")) return M("Bakur_Beard");
            if (s.Contains("eyebrow")) return M("Bakur_Brows");
            if (s.Contains("eyelash")) return M("Bakur_Lashes");
            if (s.Contains("high-poly") || s.Contains("highpoly") || s.Contains("eye")) return M("Bakur_Eyes");
            if (s.Contains("teeth")) return M("Bakur_Teeth");
            if (s.Contains("tongue")) return M("Bakur_Tongue");
            if (s.Contains("short") || s.Contains("hair")) return M("Bakur_Hair");
            if (s.StartsWith("human.")) return M("Bakur_Skin");
            switch (renderer)
            {
                case "Bakur_Cloth": return M("Bakur_Cloth");
                case "Bakur_Armour": return M("Bakur_Armour");
                case "Bakur_Helmet": return M("Bakur_Helmet");
                case "Bakur_Mail":
                case "Bakur_Aventail": return M("Bakur_Mail");
                case "Bakur_Body": return M("Bakur_Skin");
            }
            return M("Bakur_Cloth");
        }

        // ------------------------------------------------------------------ the player's visual

        public static GameObject InstantiateVisual()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
            foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mats = r.sharedMaterials;
                var outMats = new Material[Mathf.Max(1, mats.Length)];
                for (int i = 0; i < outMats.Length; i++)
                    outMats[i] = MaterialFor(r.gameObject.name, i < mats.Length && mats[i] != null ? mats[i].name : "");
                r.sharedMaterials = outMats;
                r.updateWhenOffscreen = false;
                r.skinnedMotionVectors = true;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }
            var look = go.AddComponent<HeroAppearance>();
            var all = go.GetComponentsInChildren<Transform>(true);
            look.helmetSet = all.Where(t => t.name == "Bakur_Helmet" || t.name == "Bakur_HelmetBand" || t.name == "Bakur_Aventail").Select(t => t.gameObject).ToArray();
            look.hairSet = all.Where(t => t.name == "Bakur_Hair").Select(t => t.gameObject).ToArray();
            look.Helmet = true;
            var anim = go.GetComponent<Animator>();
            if (anim == null) anim = go.AddComponent<Animator>();
            anim.avatar = GKCharacters.GetAvatar(Fbx);
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            GKCharacters.FitSkinnedBounds(go, anim);
            GKWeapons.Attach(go);
            return go;
        }
    }
}
