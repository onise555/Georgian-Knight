using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// The GeorgianKnight/Fabric inspector: the default property list, then the texture keywords switched on exactly
    /// when their map is assigned (shader_feature: a map that is set but whose keyword is off is never read).
    /// </summary>
    public class GKFabricGUI : ShaderGUI
    {
        public override void OnGUI(MaterialEditor editor, MaterialProperty[] props)
        {
            base.OnGUI(editor, props);
            foreach (var t in editor.targets)
                if (t is Material m) GKFabric.SetKeywords(m);
        }

        public override void AssignNewShaderToMaterial(Material material, Shader oldShader, Shader newShader)
        {
            base.AssignNewShaderToMaterial(material, oldShader, newShader);
            GKFabric.SetKeywords(material);
        }
    }

    /// <summary>
    /// Fabric review tools (Director 2026-10-31: "cloth, not plastic"). `feel_fabric_weave` writes the tileable
    /// thread-scale weave normals (twill, plain) the fabric shader tiles on its own. `feel_fabric` (Play mode only,
    /// nothing saved): swaps the hero's garment materials between their Lit originals and GeorgianKnight/Fabric copies,
    /// and puts a macro review camera on the chest (front 3/4 or grazing), for the A/B the Director asked for.
    /// </summary>
    public static class GKFabric
    {
        public const string ShaderName = "GeorgianKnight/Fabric";
        const string ClothShader = "GeorgianKnight/Lit Two-Sided Cloth";
        const string WeaveDir = "Assets/_GeorgianKnight/VFX/Textures";
        public const string TwillPath = WeaveDir + "/T_Weave_Twill_N.png";
        public const string PlainPath = WeaveDir + "/T_Weave_Plain_N.png";

        public static void SetKeywords(Material m)
        {
            if (m == null || m.shader == null || m.shader.name != ShaderName) return;
            Toggle(m, "_NORMALMAP", m.GetTexture("_BumpMap") != null);
            Toggle(m, "_DETAIL_NORMAL", m.GetTexture("_DetailNormalMap") != null && m.GetFloat("_DetailNormalScale") > 0f);
            Toggle(m, "_MASKMAP", m.GetTexture("_MaskMap") != null);
            Toggle(m, "_FLOWMAP", m.GetTexture("_FlowMap") != null);
            Toggle(m, "_ALPHATEST_ON", m.GetFloat("_AlphaClip") > 0.5f);
        }

        static void Toggle(Material m, string kw, bool on) { if (on) m.EnableKeyword(kw); else m.DisableKeyword(kw); }

        // ------------------------------------------------------------------ weave normals

        [GKCommand("feel_fabric_weave", "feel_fabric_weave: write the tileable thread-scale weave normals (VFX/Textures/T_Weave_Twill_N, T_Weave_Plain_N, 512 px, one repeat = 16 threads) for the fabric shader (edit mode)")]
        static void Weave(string arg)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("feel_fabric_weave: edit mode only"); return; }
            WriteWeave(TwillPath, true);
            WriteWeave(PlainPath, false);
            GKBridge.Log("feel_fabric_weave: wrote " + TwillPath + " and " + PlainPath);
        }

        // A woven height field: warp and weft threads as rounded ridges passing over / under each other (twill steps one
        // thread per row, giving the diagonal rib of wool twill; plain alternates), turned into a tangent-space normal.
        static void WriteWeave(string path, bool twill)
        {
            const int N = 512, threads = 16;
            float cell = (float)N / threads;
            var h = new float[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    int cx = (int)(x / cell), cy = (int)(y / cell);
                    float fx = x / cell - cx, fy = y / cell - cy;
                    bool warpOver = twill ? ((cx + cy) % 4) < 2 : ((cx + cy) & 1) == 0;
                    float warp = Mathf.Sin(fx * Mathf.PI);        // a warp thread across this cell (runs along y)
                    float weft = Mathf.Sin(fy * Mathf.PI);        // a weft thread (runs along x)
                    // the thread on top shows its full round; the one under shows only at the cell's edges
                    float top = warpOver ? warp * (0.65f + 0.35f * Mathf.Sin(fy * Mathf.PI)) : weft * (0.65f + 0.35f * Mathf.Sin(fx * Mathf.PI));
                    float fibre = 0.04f * Mathf.Sin((warpOver ? fx : fy) * Mathf.PI * 7f);   // fibres twisted in the yarn
                    h[y * N + x] = top + fibre;
                }
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false, true);
            var px = new Color32[N * N];
            const float strength = 2.2f;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = h[y * N + (x + 1) % N] - h[y * N + (x + N - 1) % N];
                    float dy = h[((y + 1) % N) * N + x] - h[((y + N - 1) % N) * N + x];
                    var n = new Vector3(-dx * strength, -dy * strength, 1f).normalized;
                    px[y * N + x] = new Color32((byte)(n.x * 127.5f + 127.5f), (byte)(n.y * 127.5f + 127.5f), (byte)(n.z * 127.5f + 127.5f), 255);
                }
            tex.SetPixels32(px);
            Directory.CreateDirectory(WeaveDir);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.textureType = TextureImporterType.NormalMap;
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.mipmapEnabled = true;
            imp.anisoLevel = 4;
            imp.SaveAndReimport();
        }

        // ------------------------------------------------------------------ the A/B in Play

        static readonly Dictionary<Renderer, Material[]> originals = new Dictionary<Renderer, Material[]>();
        static readonly Dictionary<Renderer, Material[]> fabrics = new Dictionary<Renderer, Material[]>();
        static Camera cam;
        static float camDist = 0.9f;
        static string camView = "front";
        // what freeze turned off, to turn back on
        static readonly List<Behaviour> frozen = new List<Behaviour>();
        static Animator frozenAnim;
        static float frozenSpeed = 1f;

        [GKCommand("feel_fabric", "feel_fabric lit|fabric | cam [metres=0.9] [front|grazing|back] | freeze on|off | shot <name> | set <prop> <value> | off: A/B the hero's garments (Lit originals vs GeorgianKnight/Fabric copies) with a macro camera on the chest; shot renders that camera to GKBridge/feel/<name>.png (1600x1200, independent of the Game view); nothing in the project is saved (play mode)")]
        static void Run(string arg)
        {
            if (!EditorApplication.isPlaying) { GKBridge.Log("feel_fabric: play mode only"); return; }
            var parts = (arg ?? "").Trim().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            string mode = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
            var d = CombatDirector.Existing;
            var hero = d != null ? d.Player : null;
            if (hero == null) { GKBridge.Log("feel_fabric: no hero"); return; }

            if (mode == "lit" || mode == "fabric")
            {
                Collect(hero);
                if (originals.Count == 0) { GKBridge.Log("feel_fabric: no garment on the hero uses " + ClothShader); return; }
                foreach (var kv in originals) if (kv.Key != null) kv.Key.sharedMaterials = mode == "lit" ? kv.Value : fabrics[kv.Key];
                GKBridge.Log("feel_fabric: " + mode + " on " + originals.Count + " renderer(s): " + Names());
                return;
            }
            if (mode == "set" && parts.Length >= 3)
            {
                Collect(hero);
                float v; Color c;
                int n = 0;
                foreach (var mats in fabrics.Values)
                    foreach (var m in mats)
                    {
                        if (m == null || m.shader.name != ShaderName || !m.HasProperty(parts[1])) continue;
                        if (parts.Length >= 5 && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out c.r)
                            && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out c.g)
                            && float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out c.b)) { c.a = 1f; m.SetColor(parts[1], c); n++; }
                        else if (float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { m.SetFloat(parts[1], v); SetKeywords(m); n++; }
                    }
                GKBridge.Log("feel_fabric: set " + parts[1] + " on " + n + " fabric material(s)");
                return;
            }
            if (mode == "cam")
            {
                float dist = 0.9f;
                if (parts.Length > 1) float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out dist);
                camDist = Mathf.Clamp(dist, 0.3f, 4f);
                camView = parts.Length > 2 ? parts[2].ToLowerInvariant() : "front";
                Cam(hero, camDist, camView);
                return;
            }
            if (mode == "freeze")
            {
                bool on = parts.Length < 2 || parts[1].ToLowerInvariant() != "off";
                Freeze(hero, on);
                return;
            }
            if (mode == "shot" && parts.Length > 1)
            {
                Cam(hero, camDist, camView);          // the chest may have moved since the camera was placed
                Shot(parts[1]);
                return;
            }
            if (mode == "off")
            {
                Freeze(hero, false);
                foreach (var kv in originals) if (kv.Key != null) kv.Key.sharedMaterials = kv.Value;
                foreach (var mats in fabrics.Values) foreach (var m in mats) if (m != null && m.shader.name == ShaderName) Object.Destroy(m);
                originals.Clear(); fabrics.Clear();
                if (cam != null) Object.Destroy(cam.gameObject);
                GKBridge.Log("feel_fabric: off, originals back");
                return;
            }
            GKBridge.Log("feel_fabric: usage: lit|fabric | cam [metres] [front|grazing|back] | freeze on|off | shot <name> | set <prop> <value|r g b> | off");
        }

        // holds the hero still for a matched A/B: his controller and the autopilot off, the animation paused
        static void Freeze(Combatant hero, bool on)
        {
            if (on)
            {
                if (frozen.Count > 0 || frozenAnim != null) { GKBridge.Log("feel_fabric: already frozen"); return; }
                foreach (var b in hero.GetComponents<Behaviour>())
                {
                    string n = b.GetType().Name;
                    if (b.enabled && (n == "PlayerController" || n == "GKAutoPilot")) { b.enabled = false; frozen.Add(b); }
                }
                frozenAnim = hero.Anim;
                if (frozenAnim != null) { frozenSpeed = frozenAnim.speed; frozenAnim.speed = 0f; }
                GKBridge.Log("feel_fabric: hero frozen (" + frozen.Count + " controller(s) off, animation paused)");
            }
            else
            {
                foreach (var b in frozen) if (b != null) b.enabled = true;
                frozen.Clear();
                if (frozenAnim != null) frozenAnim.speed = frozenSpeed;
                frozenAnim = null;
            }
        }

        // the macro camera rendered on its own into a 1600 x 1200 image, the scene's light and post as the game sees it
        static void Shot(string name)
        {
            if (cam == null) { GKBridge.Log("feel_fabric: place the camera first (feel_fabric cam)"); return; }
            const int W = 1600, H = 1200;
            var rt = RenderTexture.GetTemporary(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0, false);
            tex.Apply(false);
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);
            string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "GKBridge", "feel");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.Destroy(tex);
            GKBridge.Log("feel_fabric: shot " + path + " (" + camView + ", " + camDist.ToString("F2", CultureInfo.InvariantCulture) + " m)");
        }

        static string Names()
        {
            var list = new List<string>();
            foreach (var kv in originals) if (kv.Key != null) list.Add(kv.Key.name);
            return string.Join(", ", list);
        }

        // the hero's renderers wearing the cloth shader, and a fabric copy of each such material (made once per Play)
        static void Collect(Combatant hero)
        {
            // already collected this Play (and the renderers still exist): keep those copies and their tweaks
            foreach (var r in originals.Keys) if (r != null) return;
            originals.Clear(); fabrics.Clear();
            var shader = Shader.Find(ShaderName);
            if (shader == null) { GKBridge.Log("feel_fabric: shader " + ShaderName + " not found"); return; }
            var twill = AssetDatabase.LoadAssetAtPath<Texture2D>(TwillPath);
            foreach (var r in hero.GetComponentsInChildren<Renderer>(true))
            {
                if (r.name.StartsWith("CC_Base_")) continue;          // the body: skin, not cloth
                var mats = r.sharedMaterials;
                bool any = false;
                var copy = new Material[mats.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    var src = mats[i];
                    if (src == null || src.shader == null || src.shader.name != ClothShader || !IsCloth(src.name)) { copy[i] = src; continue; }
                    any = true;
                    copy[i] = ToFabric(src, shader, twill);
                }
                if (!any) continue;
                originals[r] = mats;
                fabrics[r] = copy;
            }
        }

        // the cloth shader also dresses leather (boots, the belt strip) and, on some bodies, skin: those stay Lit
        static bool IsCloth(string name)
        {
            string n = name.ToLowerInvariant();
            foreach (var not in new[] { "skin", "boot", "belt", "strip", "leather", "shoe", "glove" })
                if (n.Contains(not)) return false;
            return true;
        }

        static Material ToFabric(Material src, Shader shader, Texture2D weave)
        {
            var m = new Material(shader) { name = src.name + " (Fabric)" };
            if (src.HasProperty("_BaseMap")) { m.SetTexture("_BaseMap", src.GetTexture("_BaseMap")); m.SetTextureScale("_BaseMap", src.GetTextureScale("_BaseMap")); m.SetTextureOffset("_BaseMap", src.GetTextureOffset("_BaseMap")); }
            if (src.HasProperty("_BaseColor")) m.SetColor("_BaseColor", src.GetColor("_BaseColor"));
            if (src.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", src.GetTexture("_BumpMap"));
            if (src.HasProperty("_BumpScale")) m.SetFloat("_BumpScale", src.GetFloat("_BumpScale"));
            if (src.HasProperty("_Cull")) m.SetFloat("_Cull", src.GetFloat("_Cull"));
            if (src.HasProperty("_AlphaClip")) { m.SetFloat("_AlphaClip", src.GetFloat("_AlphaClip")); m.SetFloat("_Cutoff", src.GetFloat("_Cutoff")); }
            if (weave != null) { m.SetTexture("_DetailNormalMap", weave); m.SetTextureScale("_DetailNormalMap", new Vector2(24f, 24f)); }
            // Art's wool (2026-10-31): sheen linear (0.42, 0.40, 0.40) at roughness 0.42, wrap 0.3, F0 0.02, never glossy.
            // A material colour is authored in sRGB, so the linear values go in through .gamma.
            m.SetColor("_SheenColor", new Color(0.42f, 0.40f, 0.40f, 1f).gamma);
            m.SetFloat("_SheenStrength", 1f);
            m.SetFloat("_SheenRoughness", 0.42f);
            m.SetFloat("_Wrap", 0.3f);
            m.SetFloat("_Roughness", 0.9f);
            m.SetFloat("_ClothF0", 0.02f);
            m.SetFloat("_ClothSpecular", 1f);
            m.SetFloat("_DetailNormalScale", 0.6f);
            m.SetColor("_DustColor", new Color(0.20f, 0.17f, 0.13f, 1f).gamma);
            SetKeywords(m);
            return m;
        }

        // a macro camera on the hero's chest: front 3/4, grazing (nearly side-on, the silhouette and the folds turned
        // away), or back; the scene's own light, drawn over the game camera
        static void Cam(Combatant hero, float dist, string view)
        {
            var anim = hero.Anim;
            Transform chest = anim != null && anim.isHuman ? anim.GetBoneTransform(HumanBodyBones.Chest) : null;
            Vector3 at = chest != null ? chest.position : hero.transform.position + Vector3.up * 1.3f;
            Vector3 fwd = hero.transform.forward, right = hero.transform.right;
            Vector3 dir = view == "grazing" ? (right * 0.97f + fwd * 0.25f).normalized
                        : view == "back" ? (-fwd + right * 0.4f).normalized
                        : (fwd + right * 0.6f).normalized;
            if (cam == null)
            {
                var main = Camera.main;
                cam = new GameObject("[FeelFabricCam]").AddComponent<Camera>();
                if (main != null) { cam.CopyFrom(main); cam.depth = main.depth + 30f; }
                cam.nearClipPlane = 0.02f;
                UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(cam).renderPostProcessing = true;
            }
            cam.fieldOfView = 30f;
            cam.transform.position = at + dir * dist + Vector3.up * 0.05f;
            cam.transform.rotation = Quaternion.LookRotation(at - cam.transform.position, Vector3.up);
            GKBridge.Log("feel_fabric: camera " + view + " at " + dist.ToString("F2", CultureInfo.InvariantCulture) + " m on " + (chest != null ? chest.name : "the hero"));
        }
    }
}
