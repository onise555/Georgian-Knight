using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Hero V2 (Character Creator body, the sewn gambeson and trousers, cloth skirt) as a playable prefab for the combat
    /// sandbox: humanoid import with its materials, a skeleton rebuilt from the model, garment materials, sword and
    /// scabbard, then the game's own character builder (ragdoll, cloth grid, combatant, player components).
    /// The Didgori player prefab is not touched: this builds Prefabs/HeroV2_Player.prefab.
    /// </summary>
    public static class GKHeroV2
    {
        public const string Fbx = GKPaths.Characters + "/Hero_V2/GK_HeroV2_GarmentTest.fbx";
        public const string PrefabPath = GKPaths.Prefabs + "/HeroV2_Player.prefab";
        const string MatDir = GKPaths.Generated + "/HeroV2";

        public static bool Available { get { return AssetDatabase.LoadAssetAtPath<GameObject>(Fbx) != null; } }

        [GKCommand("herov2", "herov2: Hero V2 as a playable prefab (Prefabs/HeroV2_Player) for the sandbox")]
        static void Cmd(string a)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("herov2: stop play mode first"); return; }
            if (!Available) { GKBridge.Log("herov2: no " + Fbx); return; }
            Prepare();
            var visual = InstantiateVisual();
            // his skirt lies straight on the trousers (no mail or arming coat between): it keeps at most 1 cm off the
            // leg cones, and the rows down to the top of the front and back slits are sewn
            GKGameBuilder.ClothClearanceCap = 0.01f;
            GKGameBuilder.ClothJoinedRows = 3;
            try { GKGameBuilder.BuildCharacterPrefab(true, visual, PrefabPath); }
            finally { GKGameBuilder.ClothClearanceCap = 0.045f; GKGameBuilder.ClothJoinedRows = -1; }
            GKBridge.Log("herov2: prefab " + PrefabPath);
        }

        /// <summary>Humanoid, its own avatar and skeleton, the FBX's materials and embedded textures.</summary>
        public static void Prepare()
        {
            var imp = (ModelImporter)AssetImporter.GetAtPath(Fbx);
            imp.animationType = ModelImporterAnimationType.Human;
            imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            imp.importAnimation = false;
            imp.importBlendShapes = true;
            imp.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            imp.materialLocation = ModelImporterMaterialLocation.InPrefab;
            imp.SaveAndReimport();
            // the textures travel embedded in the FBX: pull them out once so the imported materials can find them
            string texDir = GKPaths.Characters + "/Hero_V2/Textures";
            if (!AssetDatabase.IsValidFolder(texDir) || AssetDatabase.FindAssets("t:Texture2D", new[] { texDir }).Length == 0)
            {
                GKPaths.Ensure(texDir);
                bool ok = imp.ExtractTextures(texDir);
                AssetDatabase.Refresh();
                GKBridge.Log("herov2: textures extracted " + ok + " -> " + AssetDatabase.FindAssets("t:Texture2D", new[] { texDir }).Length);
                imp.SaveAndReimport();
            }
            // the avatar's skeleton must be the model as it is now (a re-export keeps the old one in the .meta)
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
            var hd = imp.humanDescription;
            hd.skeleton = model.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone
            {
                name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale
            }).ToArray();
            // the jaw stays out of the humanoid: body clips (Mixamo) carry no jaw curve, and a mapped jaw is posed at its
            // muscle zero, which opens the mouth; the face rig (expressions, visemes) drives it instead
            hd.human = hd.human.Where(h => h.humanName != "Jaw").ToArray();
            imp.humanDescription = hd;
            imp.SaveAndReimport();
            var av = AssetDatabase.LoadAllAssetsAtPath(Fbx).OfType<Avatar>().FirstOrDefault();
            GKBridge.Log("herov2: avatar " + (av != null && av.isValid && av.isHuman ? "valid humanoid" : "INVALID") + ", materials " +
                         AssetDatabase.LoadAllAssetsAtPath(Fbx).OfType<Material>().Count() + ", textures " + AssetDatabase.FindAssets("t:Texture2D", new[] { GKPaths.Characters + "/Hero_V2/Textures" }).Length);
        }

        public static GameObject InstantiateVisual()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
            GKPaths.Ensure(MatDir);
            int ccHits = 0; var ccMiss = new System.Collections.Generic.List<string>();
            foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                string n = r.gameObject.name;
                r.updateWhenOffscreen = false;
                r.skinnedMotionVectors = true;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                if (n == "GK_Gambeson") { r.sharedMaterials = Enumerable.Repeat(Cloth("HeroV2_Gambeson", new Color(0.60f, 0.55f, 0.46f), 0.22f), r.sharedMaterials.Length).ToArray(); continue; }
                if (n == "GK_Trousers") { r.sharedMaterials = Enumerable.Repeat(Cloth("HeroV2_Trousers", new Color(0.026f, 0.025f, 0.023f), 0.18f), r.sharedMaterials.Length).ToArray(); continue; }
                bool hair = n.Contains("Bushy") || n.Contains("Mustache") || n.Contains("Sideburns") || n.Contains("Chinstrap") || n.Contains("Soul_Path") || n.Contains("blowback");
                bool wet = n == "CC_Base_EyeOcclusion" || n == "CC_Base_TearLine" || n == "CC_Base_Tear_Ducts";
                // the character's own materials as Character Creator built them for Unity (CC Unity Tools: skin with
                // subsurface and micro-normals, parallax eyes, hair, the eye's wet layers); ours only where none exists
                var mats = r.sharedMaterials.Select(m =>
                {
                    var cc = m != null ? CCMat(m.name) : null;
                    if (cc != null) { ccHits++; return cc; }
                    ccMiss.Add(n + "/" + (m != null ? m.name : "null"));
                    return Fix(m, hair);
                }).ToArray();
                r.sharedMaterials = mats;
                if (hair) TwoPass(r);
                if (wet) { r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.enabled = mats.All(m => m != null && m.shader.name != "Universal Render Pipeline/Lit"); }
                if (hair) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.TwoSided;
            }
            GKBridge.Log("herov2: CC materials " + ccHits + ", ours " + ccMiss.Count + (ccMiss.Count > 0 ? " (" + string.Join(", ", ccMiss.ToArray()) + ")" : ""));
            var anim = go.GetComponent<Animator>();
            if (anim == null) anim = go.AddComponent<Animator>();
            anim.avatar = GKCharacters.GetAvatar(Fbx);
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            GKCharacters.FitSkinnedBounds(go, anim);
            GKWeapons.Attach(go);
            return go;
        }

        const string CCMatDir = GKPaths.Characters + "/CC_Hero/Materials/CC_Hero";

        /// <summary>
        /// Hair cards drawn the way CC Unity Tools draws them: a first pass that writes depth for the solid strands and a
        /// second, blended pass for the soft ends. A renderer with one more material than submeshes draws its last
        /// submesh again, so each card submesh is moved into its own renderer with both passes; the scalp and the brow
        /// base keep a single material.
        /// </summary>
        static void TwoPass(SkinnedMeshRenderer r)
        {
            var mats = r.sharedMaterials;
            var src = r.sharedMesh;
            if (src == null) return;
            for (int sub = 0; sub < mats.Length && sub < src.subMeshCount; sub++)
            {
                if (mats[sub] == null) continue;
                string baseName = mats[sub].name.Replace("_1st_Pass", "");
                var p1 = AssetDatabase.LoadAssetAtPath<Material>(CCMatDir + "/" + baseName + "_1st_Pass.mat");
                var p2 = AssetDatabase.LoadAssetAtPath<Material>(CCMatDir + "/" + baseName + "_2nd_Pass.mat");
                if (p1 == null || p2 == null) continue;
                if (src.subMeshCount == 1) { r.sharedMaterials = new[] { p1, p2 }; return; }
                // split: the cards to a renderer of their own, the rest stays
                string dir = MatDir + "/Meshes"; GKPaths.Ensure(dir);
                var cards = Object.Instantiate(src); cards.name = src.name + "_cards";
                cards.subMeshCount = 1; cards.SetTriangles(src.GetTriangles(sub), 0);
                var others = Enumerable.Range(0, src.subMeshCount).Where(i => i != sub).ToArray();
                var rest = Object.Instantiate(src); rest.name = src.name + "_rest";
                rest.subMeshCount = others.Length;
                for (int k = 0; k < others.Length; k++) rest.SetTriangles(src.GetTriangles(others[k]), k);
                foreach (var m in new[] { cards, rest })
                {
                    string mp = dir + "/" + m.name + ".asset";
                    if (AssetDatabase.LoadAssetAtPath<Mesh>(mp) != null) AssetDatabase.DeleteAsset(mp);
                    AssetDatabase.CreateAsset(m, mp);
                }
                var go = new GameObject(r.gameObject.name + "_Cards");
                go.transform.SetParent(r.transform.parent, false);
                go.transform.localPosition = r.transform.localPosition; go.transform.localRotation = r.transform.localRotation; go.transform.localScale = r.transform.localScale;
                var r2 = go.AddComponent<SkinnedMeshRenderer>();
                r2.sharedMesh = cards; r2.bones = r.bones; r2.rootBone = r.rootBone; r2.localBounds = r.localBounds;
                r2.sharedMaterials = new[] { p1, p2 };
                r2.updateWhenOffscreen = false; r2.skinnedMotionVectors = true; r2.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.TwoSided;
                r.sharedMesh = rest;
                r.sharedMaterials = others.Select(i => mats[i]).ToArray();
                return;
            }
        }

        /// <summary>The CC Unity Tools material for a Character Creator slot name (hair: its single-pass version).</summary>
        static Material CCMat(string slot)
        {
            string s = slot.Replace(" (Instance)", "");
            int dot = s.LastIndexOf('.');
            if (dot > 0 && s.Length - dot == 4 && char.IsDigit(s[s.Length - 1])) s = s.Substring(0, dot);   // Blender's .001
            foreach (var n in new[] { s, s + "_1st_Pass" })
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(CCMatDir + "/" + n + ".mat");
                if (m != null) return m;
            }
            return null;
        }

        static Material Cloth(string name, Color c, float smooth)
        {
            string p = MatDir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, p); }
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smooth);
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>The imported material, copied to an asset on URP Lit (textures kept); hair cards cut out and two-sided.</summary>
        static Material Fix(Material src, bool hair)
        {
            if (src == null) return null;
            string p = MatDir + "/HeroV2_" + src.name.Replace(" ", "_").Replace(":", "_") + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (m == null) { m = new Material(src); AssetDatabase.CreateAsset(m, p); }
            else m.CopyPropertiesFromMaterial(src);
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            if (m.shader != lit)
            {
                var tex = src.HasProperty("_BaseMap") ? src.GetTexture("_BaseMap") : src.mainTexture;
                var bump = src.HasProperty("_BumpMap") ? src.GetTexture("_BumpMap") : null;
                m.shader = lit;
                if (tex != null) m.SetTexture("_BaseMap", tex);
                if (bump != null) { m.SetTexture("_BumpMap", bump); m.EnableKeyword("_NORMALMAP"); }
            }
            if (hair)
            {
                m.SetFloat("_AlphaClip", 1f); m.SetFloat("_Cutoff", 0.4f); m.EnableKeyword("_ALPHATEST_ON");
                m.SetFloat("_Cull", 0f); m.doubleSidedGI = true;
                m.SetFloat("_Smoothness", 0.3f);
            }
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
