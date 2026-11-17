using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GeorgianKnight.EditorTools
{
    /// <summary>First look: the Didgori plateau with the knight standing in it, plus offscreen captures.</summary>
    public static class GKPreview
    {
        public const string ScenePath = GKPaths.Scenes + "/Didgori.unity";

        public static AnimationClip Clip(string name)
        {
            string p = GKPaths.Mixamo + "/" + name + ".fbx";
            var c0 = AssetDatabase.LoadAllAssetsAtPath(p).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview__"));
            // clips authored in the project (GKGuardPose and the like)
            return c0 != null ? c0 : AssetDatabase.LoadAssetAtPath<AnimationClip>(GKPaths.Root + "/Animations/Generated/" + name + ".anim");
        }

        public static void BuildPreviewScene()
        {
            GKSetup.ConfigureProject();
            var av = GKCharacters.GetAvatar(GKPaths.KnightFbx);
            if (av == null || !av.isHuman || GKCharacters.KnightMaterial == null) GKCharacters.PrepareAll();

            GKPaths.Ensure(GKPaths.Scenes);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GKEnvironment.Build();

            // ---- the knight
            var knight = GKCharacters.InstantiateKnightVisual();
            knight.name = "Knight (preview)";
            var map = GKCharacters.MapBones(knight.transform);
            Vector3 right, fwd;
            GKCharacters.CharacterFrame(map, out right, out fwd);
            knight.transform.rotation = Quaternion.FromToRotation(fwd, Vector3.forward) * knight.transform.rotation;
            Vector3 kp = new Vector3(0f, 0f, 0f);
            kp.y = GKEnvironment.SampleHeight(kp);
            knight.transform.position = kp;
            var ctrl = PreviewController();
            knight.GetComponent<Animator>().runtimeAnimatorController = ctrl;
            var idle = Clip("AX_standing_idle") ?? Clip("SS_Idle");
            Quaternion krot = knight.transform.rotation;
            if (idle != null) idle.SampleAnimation(knight, 0.6f);
            knight.transform.position = kp; knight.transform.rotation = krot;

            // ---- camera used for the captures (and as the scene's main camera for now)
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 38f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 32000f;
            var cd = camGo.AddComponent<UniversalAdditionalCameraData>();
            cd.renderPostProcessing = true;
            cd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            cd.antialiasingQuality = AntialiasingQuality.High;
            camGo.AddComponent<AudioListener>();
            Vector3 chest = kp + Vector3.up * 1.18f;
            camGo.transform.position = kp + new Vector3(1.55f, 1.5f, 3.5f);
            camGo.transform.LookAt(chest + new Vector3(-0.35f, 0f, 0f));

            EditorSceneManager.SaveScene(scene, ScenePath);
            BakeEnvironmentLighting();
            EditorSceneManager.SaveScene(scene, ScenePath);

            var sv = SceneView.lastActiveSceneView;
            if (sv != null)
            {
                sv.pivot = chest;
                sv.rotation = Quaternion.LookRotation(chest - camGo.transform.position);
                sv.size = 2.2f;
                sv.Repaint();
            }

            Capture(cam, "preview_hero");
            camGo.transform.position = kp + new Vector3(-16f, 7.5f, 24f);
            camGo.transform.LookAt(kp + new Vector3(6f, -2f, -55f));
            cam.fieldOfView = 55f;
            Capture(cam, "preview_wide");
            string[] names = { "N", "E", "S", "W" };
            for (int i = 0; i < 4; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, i * 90f, 0f) * Vector3.forward;
                Vector3 pos = kp - dir * 25f + Vector3.up * 9f;
                camGo.transform.position = pos;
                camGo.transform.rotation = Quaternion.LookRotation(dir * 100f + Vector3.down * 6f);
                cam.fieldOfView = 60f;
                Capture(cam, "view_" + names[i], 1280, 720);
            }
            camGo.transform.position = kp + new Vector3(1.55f, 1.5f, 3.5f);
            camGo.transform.LookAt(chest + new Vector3(-0.35f, 0f, 0f));
            cam.fieldOfView = 38f;
            EditorSceneManager.SaveScene(scene, ScenePath);
            GKBridge.Log("preview scene ready: " + ScenePath);
        }

        static AnimatorController PreviewController()
        {
            GKPaths.Ensure(GKPaths.Generated);
            string path = GKPaths.Generated + "/KnightPreview.controller";
            var c = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (c != null) return c;
            c = AnimatorController.CreateAnimatorControllerAtPath(path);
            var sm = c.layers[0].stateMachine;
            var idle = Clip("AX_standing_idle") ?? Clip("SS_Idle");
            var st = sm.AddState("Idle");
            st.motion = idle;
            sm.defaultState = st;
            return c;
        }

        public static void BakeEnvironmentLighting()
        {
            var ls = new LightingSettings { name = "DidgoriLighting" };
            ls.bakedGI = false;
            ls.realtimeGI = false;
            string p = GKPaths.Generated + "/Environment/DidgoriLighting.lighting";
            var existing = AssetDatabase.LoadAssetAtPath<LightingSettings>(p);
            if (existing == null) { AssetDatabase.CreateAsset(ls, p); existing = ls; }
            Lightmapping.lightingSettings = existing;
            try { Lightmapping.Bake(); } catch (System.Exception e) { GKBridge.Log("lighting bake: " + e.Message); }
        }

        public static void CaptureFromSceneCamera(string name)
        {
            var cam = Camera.main;
            if (cam == null) cam = Object.FindFirstObjectByType<Camera>();
            if (cam == null) { GKBridge.Log("no camera to capture"); return; }
            Capture(cam, name);
        }

        public static string Capture(Camera cam, string name, int w = 1920, int h = 1080)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.antiAliasing = 1;
            var prevTarget = cam.targetTexture;
            cam.targetTexture = rt;
            var req = new RenderPipeline.StandardRequest { destination = rt };
            bool done = false;
            try
            {
                if (RenderPipeline.SupportsRenderRequest(cam, req)) { RenderPipeline.SubmitRenderRequest(cam, req); done = true; }
            }
            catch (System.Exception e) { GKBridge.Log("render request failed: " + e.Message); }
            if (!done) cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = prevTarget;
            string path = Path.Combine(GKBridge.Dir, name + ".png");
            File.WriteAllBytes(path, tex.EncodeToPNG());   // lossless: QA measures exposure / clipping on these
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);
            GKBridge.Log("captured " + path);
            return path;
        }
    }

    public static class GKEval
    {
        public static void Run(string what)
        {
            switch (what)
            {
                case "avatars":
                    foreach (var p in new[] { GKPaths.KnightFbx, GKPaths.XBotFbx })
                    {
                        var a = GKCharacters.GetAvatar(p);
                        GKBridge.Log(p + " avatar=" + (a != null) + " valid=" + (a != null && a.isValid) + " human=" + (a != null && a.isHuman));
                    }
                    break;
                case "bones":
                    {
                        var m = AssetDatabase.LoadAssetAtPath<GameObject>(GKPaths.KnightFbx);
                        var inst = Object.Instantiate(m);
                        var map = GKCharacters.MapBones(inst.transform);
                        GKBridge.Log(string.Join(", ", map.Select(kv => kv.Key + "=" + kv.Value.name).ToArray()));
                        GKBridge.Log("height=" + GKCharacters.MeasureHeight(m));
                        Object.DestroyImmediate(inst);
                    }
                    break;
                case "attacks":
                    foreach (var g in AssetDatabase.FindAssets("t:AttackData"))
                    {
                        var a = AssetDatabase.LoadAssetAtPath<AttackData>(AssetDatabase.GUIDToAssetPath(g));
                        GKBridge.Log(string.Format("{0,-20} len={1:F2} hit={2:F2}-{3:F2} combo={4:F2} end={5:F2} range={6:F2} windupx{7:F2} -> contact {8:F2}s", a.name, a.clip != null ? a.clip.length : 0f, a.hitStart, a.hitEnd, a.comboWindow, a.end, a.idealRange,
                            a.windupSpeed, a.clip != null ? a.hitStart * a.clip.length / Mathf.Max(0.1f, a.speed) / Mathf.Max(0.5f, a.windupSpeed) : 0f));
                    }
                    break;
                default:
                    GKBridge.Log("eval: unknown " + what);
                    break;
            }
        }
    }

    public static class GKBuildAll
    {
        [MenuItem("Georgian Knight/Build Preview Scene", priority = 1)]
        public static void MenuPreview() { GKPreview.BuildPreviewScene(); }

        [MenuItem("Georgian Knight/Build Everything", priority = 0)]
        public static void BuildEverything()
        {
            GKGameBuilder.BuildGame();
        }
    }
}
