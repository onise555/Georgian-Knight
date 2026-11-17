using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// decals: URP's decal renderer feature on the project's renderer (screen-space technique: it projects onto any
    /// opaque surface, the CC characters' own shaders included) and the wound decals HitFX lays on a struck body -
    /// textures with the blood's colour baked in, materials under Resources/FX so the game finds them in a build.
    /// </summary>
    public static class GKDecals
    {
        const string Dir = "Assets/_GeorgianKnight/Resources/FX";
        public const int WoundVariants = 4;

        [GKCommand("decals", "decals: enable URP decals on the renderer and build the wound decal textures and materials")]
        static void Cmd(string arg) { Build(); }

        [GKCommand("decaltest", "decaltest: a wound decal 1 m wide on the ground in front of the player and one on his chest, then a game-camera shot (play mode)")]
        static void Test(string arg)
        {
            var p = GameObject.FindWithTag("Player");
            if (p == null) { GKBridge.Log("decaltest: no player"); return; }
            var mat = Resources.Load<Material>("FX/WoundDecal0");
            var a = p.GetComponent<Animator>();
            var chest = a != null ? a.GetBoneTransform(HumanBodyBones.Chest) : p.transform;
            var g = new GameObject("~DecalGround");
            g.transform.SetPositionAndRotation(p.transform.position + p.transform.forward * 1.5f + Vector3.up * 0.3f, Quaternion.LookRotation(Vector3.down, p.transform.forward));
            var d = g.AddComponent<DecalProjector>(); d.material = mat; d.size = new Vector3(1f, 0.6f, 1f);
            var c = new GameObject("~DecalChest");
            c.transform.SetParent(chest, false);
            c.transform.SetPositionAndRotation(chest.position + p.transform.forward * 0.3f, Quaternion.LookRotation(-p.transform.forward, Vector3.up));
            var dc = c.AddComponent<DecalProjector>(); dc.material = mat; dc.size = new Vector3(0.3f, 0.2f, 0.6f);
            GKBridge.Log("decaltest: mat " + (mat != null ? mat.shader.name + " supported " + mat.shader.isSupported : "NULL") + ", feature on renderer: " +
                         (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null ? UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.name : "none"));
        }

        [GKCommand("woundtest", "woundtest: a Seljuk 2 m in front, a wound on his chest and his left arm, close shots of each (play mode)")]
        static void WoundTest(string arg)
        {
            var p = GameObject.FindWithTag("Player");
            var fx = Object.FindAnyObjectByType<HitFX>();
            if (p == null || fx == null) { GKBridge.Log("woundtest: no player / HitFX"); return; }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKGameBuilder.EnemyPrefabPath);
            Vector3 at = p.transform.position + p.transform.forward * 2f;
            var go = Object.Instantiate(prefab, at, Quaternion.LookRotation(-p.transform.forward));
            foreach (var ai in go.GetComponents<EnemyAI>()) ai.enabled = false;
            var foe = go.GetComponent<Combatant>();
            var a = go.GetComponent<Animator>();
            a.Update(0f);
            var shots = new[] { HumanBodyBones.Chest, HumanBodyBones.LeftUpperArm, HumanBodyBones.RightLowerArm };
            int k = 0;
            foreach (var b in shots)
            {
                var bone = a.GetBoneTransform(b);
                if (bone == null) continue;
                Vector3 from = bone.position + go.transform.forward * 0.5f + (b == HumanBodyBones.LeftUpperArm ? go.transform.right * -0.3f : b == HumanBodyBones.RightLowerArm ? go.transform.right * 0.3f : Vector3.zero);
                Vector3 hitPt = bone.position;
                Vector3 cut = Quaternion.AngleAxis(30f * k, go.transform.forward) * go.transform.right;
                fx.Wound(foe, hitPt + (from - hitPt).normalized * 0.12f, cut, 1f);
                var camGo = new GameObject("~woundcam");
                try
                {
                    var cam = camGo.AddComponent<Camera>();
                    UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(cam).renderPostProcessing = true;
                    cam.fieldOfView = 30f; cam.nearClipPlane = 0.02f;
                    camGo.transform.position = hitPt + (from - hitPt).normalized * 0.9f + Vector3.up * 0.1f;
                    camGo.transform.LookAt(hitPt);
                    GKPreview.Capture(cam, "wound_" + b, 960, 720);
                }
                finally { Object.DestroyImmediate(camGo); }
                k++;
            }
            GKBridge.Log("woundtest: " + k + " wounds shot (wound_*.png)");
        }

        public static void Build()
        {
            int added = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData"))
            {
                var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(AssetDatabase.GUIDToAssetPath(guid));
                if (data == null || data.rendererFeatures.Any(f => f is DecalRendererFeature)) continue;
                var feature = ScriptableObject.CreateInstance<DecalRendererFeature>();
                feature.name = "Decals";
                AssetDatabase.AddObjectToAsset(feature, data);
                var so = new SerializedObject(feature);
                var tech = so.FindProperty("m_Settings.technique");
                if (tech != null) tech.enumValueIndex = 2;                 // ScreenSpace
                var blend = so.FindProperty("m_Settings.screenSpaceSettings.normalBlend");
                if (blend != null) blend.enumValueIndex = 1;               // Medium
                var dist = so.FindProperty("m_Settings.maxDrawDistance");
                if (dist != null) dist.floatValue = 60f;
                so.ApplyModifiedPropertiesWithoutUndo();
                var dso = new SerializedObject(data);
                var list = dso.FindProperty("m_RendererFeatures");
                var map = dso.FindProperty("m_RendererFeatureMap");
                list.arraySize++;
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = feature;
                if (map != null)
                {
                    long id; string g;
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out g, out id);
                    map.arraySize++;
                    map.GetArrayElementAtIndex(map.arraySize - 1).longValue = id;
                }
                dso.ApplyModifiedPropertiesWithoutUndo();
                data.SetDirty();
                EditorUtility.SetDirty(data);
                added++;
            }

            GKPaths.Ensure(Dir);
            var shader = Shader.Find("Shader Graphs/Decal");
            for (int v = 0; v < WoundVariants; v++)
            {
                string png = Dir + "/Wound" + v + ".png";
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Application.dataPath), png), WoundTexture(v).EncodeToPNG());
                AssetDatabase.ImportAsset(png, ImportAssetOptions.ForceSynchronousImport);
                var ti = AssetImporter.GetAtPath(png) as TextureImporter;
                if (ti != null) { ti.alphaIsTransparency = true; ti.wrapMode = TextureWrapMode.Clamp; ti.mipmapEnabled = true; ti.SaveAndReimport(); }
                string matPath = Dir + "/WoundDecal" + v + ".mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, matPath); }
                mat.shader = shader;
                mat.SetTexture("Base_Map", AssetDatabase.LoadAssetAtPath<Texture2D>(png));
                mat.SetFloat("Normal_Blend", 0f);      // the wound has no normal map: keep the skin's or cloth's own shading
                mat.enableInstancing = true;
                EditorUtility.SetDirty(mat);
            }
            // gouges: a blade's bite in a painted, leather-covered wooden shield
            for (int v = 0; v < 3; v++)
            {
                string png = Dir + "/Gouge" + v + ".png";
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Application.dataPath), png), GougeTexture(v).EncodeToPNG());
                AssetDatabase.ImportAsset(png, ImportAssetOptions.ForceSynchronousImport);
                var ti = AssetImporter.GetAtPath(png) as TextureImporter;
                if (ti != null) { ti.alphaIsTransparency = true; ti.wrapMode = TextureWrapMode.Clamp; ti.SaveAndReimport(); }
                string matPath = Dir + "/GougeDecal" + v + ".mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, matPath); }
                mat.shader = shader;
                mat.SetTexture("Base_Map", AssetDatabase.LoadAssetAtPath<Texture2D>(png));
                mat.enableInstancing = true;
                EditorUtility.SetDirty(mat);
            }
            AssetDatabase.SaveAssets();
            GKBridge.Log("decals: renderer feature added to " + added + " renderer(s); " + WoundVariants + " wound decals in " + Dir + (shader == null ? " (NO decal shader found)" : ""));
        }

        /// <summary>A blade's bite: a narrow V-cut along u, pale raw wood at its bottom, splintered fibres along its lips,
        /// the paint flaked off around it, a dark edge where the paint broke.</summary>
        static Texture2D GougeTexture(int variant)
        {
            const int W = 256, H = 64;
            var rnd = new System.Random(77 + variant * 13);
            float seed = (float)rnd.NextDouble() * 10f, len = 0.75f + (float)rnd.NextDouble() * 0.2f;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, true);
            var px = new Color[W * H];
            Color wood = new Color(0.72f, 0.58f, 0.4f), woodDeep = new Color(0.45f, 0.33f, 0.2f), edge = new Color(0.12f, 0.08f, 0.06f), chip = new Color(0.55f, 0.42f, 0.3f);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W * 2f - 1f, v = (y + 0.5f) / H * 2f - 1f;
                    float n = Mathf.PerlinNoise(u * 12f + seed, v * 4f) - 0.5f;
                    float taper = Mathf.Clamp01(1f - Mathf.Pow(Mathf.Abs(u) / len, 4f));
                    float w = 0.28f * taper;
                    float d = Mathf.Abs(v + n * 0.12f);
                    float groove = Mathf.Clamp01((w - d) * 30f);                       // the cut
                    float chipR = (0.55f + n * 0.6f) * taper;
                    float flake = Mathf.Clamp01((chipR - d) * 12f) * (Mathf.PerlinNoise(u * 20f, v * 20f + seed) > 0.45f ? 1f : 0.3f);
                    float lip = Mathf.Clamp01(1f - Mathf.Abs(d - w) * 25f) * taper;     // broken paint edge
                    float fibres = 0.5f + 0.5f * Mathf.PerlinNoise(u * 60f + seed, v * 3f);
                    Color c = Color.Lerp(chip, Color.Lerp(woodDeep, wood, fibres * (1f - d / Mathf.Max(0.01f, w))), groove);
                    c = Color.Lerp(c, edge, lip * 0.7f);
                    float a = Mathf.Clamp01(Mathf.Max(groove, Mathf.Max(flake * 0.8f, lip * 0.8f)));
                    px[y * W + x] = new Color(c.r, c.g, c.b, a);
                }
            t.SetPixels(px);
            t.Apply(true);
            return t;
        }

        /// <summary>
        /// A sword cut's mark on cloth or skin, seen flat: along u the cut itself - a thin, wet, darker-red line, ragged
        /// and tapering at both ends; around it the blood soaked into the cloth, an irregular stain fading out; from its
        /// lower edge one to three runs of blood trickling down (-v) and ending in a bead. Colour baked in (fresh blood is a
        /// deep red, darkest where thickest), alpha as coverage.
        /// </summary>
        static Texture2D WoundTexture(int variant)
        {
            const int W = 256, H = 128;
            var rnd = new System.Random(911 + variant * 57);
            System.Func<float> R = () => (float)rnd.NextDouble();
            float seed = variant * 3.7f;
            float tilt = (R() - 0.5f) * 0.15f;
            int runs = 1 + variant % 3;
            var runX = new float[runs]; var runLen = new float[runs]; var runW = new float[runs];
            for (int i = 0; i < runs; i++) { runX[i] = -0.5f + R() * 1f; runLen[i] = 0.45f + R() * 0.45f; runW[i] = 0.035f + R() * 0.025f; }
            var t = new Texture2D(W, H, TextureFormat.RGBA32, true);
            var px = new Color[W * H];
            Color fresh = new Color(0.55f, 0.035f, 0.025f), deep = new Color(0.3f, 0.01f, 0.007f), soak = new Color(0.4f, 0.03f, 0.022f);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W * 2f - 1f, v = (y + 0.5f) / H * 2f - 1f;   // u along the cut, v up
                    float n = Mathf.PerlinNoise(u * 6f + seed, v * 6f - seed) - 0.5f;
                    float fine = Mathf.PerlinNoise(u * 30f + seed, v * 30f + seed) - 0.5f;
                    // the cut: a line along u with a wobble, tapering toward both ends
                    float line = v - tilt * u - n * 0.08f;
                    float taper = Mathf.Clamp01(1f - Mathf.Pow(Mathf.Abs(u) / 0.9f, 3f));
                    float cutW = 0.13f * taper;
                    float cut = Mathf.Clamp01((cutW - Mathf.Abs(line)) * 40f);
                    // the stain soaked around it: wider, ragged, fading out
                    float stainR = (0.58f + n * 0.35f + fine * 0.08f) * Mathf.Clamp01(1.2f - Mathf.Abs(u) * 0.85f);
                    float stain = Mathf.Clamp01((stainR - Mathf.Abs(line)) * 4f) * 0.85f;
                    // runs trickling down from below the cut, thinning, a bead at the end
                    float run = 0f;
                    for (int i = 0; i < runs; i++)
                    {
                        float top = -0.05f, bot = top - runLen[i];
                        if (v > top || v < bot - 0.06f) continue;
                        float k = Mathf.InverseLerp(top, bot, v);
                        float cx = runX[i] + Mathf.Sin(v * 9f + i) * 0.015f;
                        float w = runW[i] * (1f - 0.5f * k);
                        float r1 = Mathf.Clamp01((w - Mathf.Abs(u - cx)) * 60f) * (v >= bot ? 1f : 0f);
                        float bead = Mathf.Clamp01((runW[i] * 1.6f - new Vector2(u - cx, (v - bot) * 1.4f).magnitude) * 60f);
                        run = Mathf.Max(run, Mathf.Max(r1, bead));
                    }
                    float a = Mathf.Clamp01(Mathf.Max(Mathf.Max(cut, stain), run * 0.95f));
                    Color c = Color.Lerp(soak, fresh, Mathf.Max(cut, run));
                    c = Color.Lerp(c, deep, cut * 0.5f + fine * 0.3f + 0.1f);
                    px[y * W + x] = new Color(c.r, c.g, c.b, a);
                }
            t.SetPixels(px);
            t.Apply(true);
            return t;
        }
    }
}
