using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// The 15 s intro teaser: a copy of Didgori with the knight, a Seljuk, six Cinemachine shots, a graded volume and a
    /// title card, all driven by one Timeline. Works on the scene additively in edit mode so another open scene is left alone.
    /// </summary>
    public static partial class GKIntro
    {
        const string ScenePath = "Assets/_GeorgianKnight/Scenes/Intro_Cutscene.unity";
        const string SourceScene = "Assets/_GeorgianKnight/Scenes/Didgori.unity";
        const string Dir = "Assets/_GeorgianKnight/Cinematics/Intro";
        const string KnightPrefab = "Assets/_GeorgianKnight/Prefabs/Knight_Clean.prefab";
        const string SeljukPrefab = "Assets/_GeorgianKnight/Prefabs/Seljuk_Warrior.prefab";
        const string Anims = "Assets/_GeorgianKnight/Animations/Mixamo/";
        public const float Length = 15f;

        struct Shot { public string name; public float start, end, fov; public Vector3 from, to; public string look; }

        static Scene OpenOwn(out bool wasLoaded, string path = ScenePath)
        {
            var s = SceneManager.GetSceneByPath(path);
            wasLoaded = s.IsValid() && s.isLoaded;
            if (!wasLoaded) s = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            return s;
        }

        static void CloseOwn(Scene s, bool wasLoaded, bool save)
        {
            if (save) EditorSceneManager.SaveScene(s);
            if (!wasLoaded && SceneManager.sceneCount > 1) EditorSceneManager.CloseScene(s, true);
        }

        static GameObject Find(Scene s, string name)
        {
            foreach (var r in s.GetRootGameObjects())
                foreach (var t in r.GetComponentsInChildren<Transform>(true))
                    if (t.name == name) return t.gameObject;
            return null;
        }

        static AnimationClip Clip(string fbx)
        {
            return AssetDatabase.LoadAllAssetsAtPath(Anims + fbx + ".fbx").OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
        }

        [GKCommand("intro_scan", "intro_scan: list Didgori roots and anchor candidates")]
        static void Scan(string arg)
        {
            if (Application.isPlaying) { GKBridge.Log("intro: edit mode only"); return; }
            for (int i = 0; i < SceneManager.sceneCount; i++) GKBridge.Log("intro: loaded scene " + SceneManager.GetSceneAt(i).path + (SceneManager.GetSceneAt(i) == SceneManager.GetActiveScene() ? " (active)" : ""));
            var s = SceneManager.GetSceneByPath(SourceScene);
            bool was = s.IsValid() && s.isLoaded;
            if (!was) s = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Additive);
            foreach (var r in s.GetRootGameObjects())
                GKBridge.Log("intro: root " + r.name + " active=" + r.activeSelf + " pos=" + r.transform.position + " children=" + r.transform.childCount);
            foreach (var n in new[] { "Hero Fill", "Hero Rim", "CM Player Camera", "Banners" })
            {
                var g = Find(s, n);
                if (g != null) GKBridge.Log("intro: " + n + " at " + g.transform.position + " fwd " + g.transform.forward);
            }
            var k = AssetDatabase.LoadAssetAtPath<GameObject>(KnightPrefab);
            foreach (var t in k.GetComponentsInChildren<Transform>(true))
                if (t.name.Contains("Sword") || t.name.Contains("Scabbard") || t.name.Contains("Sheath") || t.name == "Shield")
                    GKBridge.Log("intro: knight " + AnimationUtility.CalculateTransformPath(t, k.transform) + " active=" + t.gameObject.activeSelf);
            foreach (var c in k.GetComponents<Component>()) GKBridge.Log("intro: knight comp " + c.GetType().Name);
            var a = k.GetComponent<Animator>();
            if (a != null) GKBridge.Log("intro: knight avatar " + (a.avatar != null ? a.avatar.name + " human=" + a.avatar.isHuman : "none"));
            var sj = AssetDatabase.LoadAssetAtPath<GameObject>(SeljukPrefab);
            foreach (var c in sj.GetComponents<Component>()) GKBridge.Log("intro: seljuk comp " + c.GetType().Name);
            foreach (var n in new[] { "AX_unarmed_idle_looking_ver_1", "MX_Draw_Sword_1", "MX_Draw_Sword_2", "GK_Sword_Draw", "KI_CombatIdle1H", "AX_standing_taunt_battlecry", "AX_standing_idle", "MO_KBS_Ready_Idle" })
            {
                var c = Clip(n);
                GKBridge.Log("intro: clip " + n + " " + (c != null ? c.length.ToString("F2") + "s human=" + c.humanMotion : "MISSING"));
            }
            if (!was) EditorSceneManager.CloseScene(s, true);
        }

        [GKCommand("intro_show", "intro_show: open the intro scene next to the open one, select its director and show the Timeline window")]
        static void Show(string arg)
        {
            if (Application.isPlaying) { GKBridge.Log("intro: edit mode only"); return; }
            var s = OpenOwn(out _);
            SceneManager.SetActiveScene(s);
            var director = s.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<PlayableDirector>(true)).FirstOrDefault();
            if (director == null) { GKBridge.Log("intro: no director"); return; }
            Selection.activeGameObject = director.gameObject;
            EditorApplication.ExecuteMenuItem("Window/Sequencing/Timeline");
            var cam = s.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Camera>(true)).FirstOrDefault(c => c.gameObject.activeInHierarchy);
            if (SceneView.lastActiveSceneView != null && cam != null) SceneView.lastActiveSceneView.AlignViewToObject(cam.transform);
            GKBridge.Log("intro: showing " + ScenePath + ", director selected, Timeline window open");
        }

        [GKCommand("intro_build", "intro_build: (re)create Intro_Cutscene.unity from Didgori with the teaser timeline")]
        static void Build(string arg)
        {
            if (Application.isPlaying) { GKBridge.Log("intro: edit mode only"); return; }
            Directory.CreateDirectory(Dir);
            var open = SceneManager.GetSceneByPath(ScenePath);
            if (open.IsValid() && open.isLoaded) EditorSceneManager.CloseScene(open, true);
            AssetDatabase.DeleteAsset(ScenePath);
            if (!AssetDatabase.CopyAsset(SourceScene, ScenePath)) { GKBridge.Log("intro: copy failed"); return; }
            var s = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            var prevActive = SceneManager.GetActiveScene();
            SceneManager.SetActiveScene(s);

            // gameplay leftovers of the level would spawn a player and drive their own camera in play mode
            foreach (var r in s.GetRootGameObjects())
            {
                string n = r.name.ToLowerInvariant();
                if (n.Contains("player") || n.Contains("gamemanager") || n.Contains("game manager") || n.Contains("enemy") || n.Contains("enemies") || n.Contains("spawner") || n.Contains("hud") || n.Contains("eventsystem") || n == "ui" || n.Contains("main camera") || n.Contains("cm player") || n.Contains("game systems") || n.Contains("house") || n.Contains("camera rig"))
                { r.SetActive(false); GKBridge.Log("intro: disabled root " + r.name); }
            }
            foreach (var oldCam in s.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Camera>(true))) oldCam.gameObject.SetActive(false);
            foreach (var cc in s.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<CinemachineVirtualCameraBase>(true))) cc.gameObject.SetActive(false);

            // anchor: where the level lights its hero
            Vector3 P = Vector3.zero; Vector3 f = Vector3.forward;
            var hero = Find(s, "Knight_Player");
            if (hero != null) { P = hero.transform.position; f = Vector3.ProjectOnPlane(hero.transform.forward, Vector3.up).normalized; }
            if (arg.StartsWith("at "))
            {
                var p = arg.Substring(3).Split(',');
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                P = new Vector3(float.Parse(p[0], inv), 0, float.Parse(p[1], inv));
                if (p.Length > 2) { float yaw = float.Parse(p[2], inv) * Mathf.Deg2Rad; f = new Vector3(Mathf.Sin(yaw), 0, Mathf.Cos(yaw)); }
            }
            var terrain = s.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Terrain>(true)).FirstOrDefault();
            float Ground(Vector3 v) => terrain != null ? terrain.SampleHeight(v) + terrain.transform.position.y : P.y;
            P.y = Ground(P);
            Vector3 rt = Vector3.Cross(Vector3.up, f);
            Vector3 S = P + f * 6.5f; S.y = Ground(S);
            GKBridge.Log("intro: knight at " + P + " facing " + f + ", seljuk at " + S + (terrain != null ? " on " + terrain.name : " (no terrain)"));

            // golden hour: a low warm sun raking across the field, haze swallowing the hills
            foreach (var l in s.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Light>(true)).Where(l => l.type == LightType.Directional))
            {
                l.transform.rotation = Quaternion.LookRotation((-f * 0.75f + rt * 0.65f).normalized + Vector3.down * 0.3f);   // key on the knight's face, rim on the Seljuk
                l.color = new Color(1f, 0.72f, 0.45f); l.intensity = 2.2f; l.shadows = LightShadows.Soft;
            }
            RenderSettings.fog = true; RenderSettings.fogMode = FogMode.ExponentialSquared; RenderSettings.fogDensity = 0.012f;
            RenderSettings.fogColor = new Color(0.78f, 0.66f, 0.52f);
            RenderSettings.ambientIntensity = 0.8f;
            if (RenderSettings.skybox != null)
            {
                // a warmer, dimmer copy of the level's sky; the level's own material is left as it is
                string skyPath = Dir + "/Intro_Sky.mat";
                AssetDatabase.DeleteAsset(skyPath);
                var sky = new Material(RenderSettings.skybox);
                if (sky.HasProperty("_Tint")) sky.SetColor("_Tint", new Color(0.62f, 0.5f, 0.42f));
                if (sky.HasProperty("_SkyTint")) sky.SetColor("_SkyTint", new Color(0.75f, 0.55f, 0.45f));
                if (sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", sky.GetFloat("_Exposure") * 0.75f);
                AssetDatabase.CreateAsset(sky, skyPath);
                RenderSettings.skybox = sky;
                GKBridge.Log("intro: sky " + sky.shader.name);
            }

            var root = new GameObject("Intro_Cutscene");
            var knight = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(KnightPrefab), s);
            knight.name = "Knight";
            // unpacked: in edit mode a prefab instance's children cannot change parent, so the sword could never leave the hand
            PrefabUtility.UnpackPrefabInstance(knight, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            knight.transform.SetPositionAndRotation(P, Quaternion.LookRotation(f));
            knight.transform.SetParent(root.transform, true);
            var seljuk = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(SeljukPrefab), s);
            seljuk.name = "Seljuk";
            seljuk.transform.SetPositionAndRotation(S, Quaternion.LookRotation(-f));
            seljuk.transform.SetParent(root.transform, true);
            foreach (var go in new[] { knight, seljuk })
            {
                foreach (var mb in go.GetComponents<MonoBehaviour>())   // gameplay brains stay off: the timeline acts; the hand and shield solvers stay on
                    mb.enabled = mb.GetType().Name == "HandGrip" || mb.GetType().Name == "ShieldGrip" || mb.GetType().Name == "FingerAxes";
                foreach (var cc in go.GetComponents<CharacterController>()) cc.enabled = false;
                foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) rb.isKinematic = true;
                var an = go.GetComponent<Animator>(); if (an != null) { an.applyRootMotion = false; an.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) { smr.updateWhenOffscreen = true; smr.forceMatrixRecalculationPerRender = true; }
            }
            var kAnim = knight.GetComponent<Animator>();
            foreach (var go in new[] { knight, seljuk })
            {
                var ctl = go.GetComponent<Animator>().runtimeAnimatorController;
                if (ctl != null) GKBridge.Log("intro: " + go.name + " controller " + ctl.name + ": " + string.Join(", ", ctl.animationClips.Select(c => c.name).Distinct().Where(n => n.Contains("dle") || n.Contains("Draw") || n.Contains("Power") || n.Contains("Cry")).ToArray()));
            }
            var sAnim = seljuk.GetComponent<Animator>();
            Transform kHead = kAnim.GetBoneTransform(HumanBodyBones.Head), kChest = kAnim.GetBoneTransform(HumanBodyBones.UpperChest) ?? kAnim.GetBoneTransform(HumanBodyBones.Chest);
            Transform sHead = sAnim != null && sAnim.isHuman ? sAnim.GetBoneTransform(HumanBodyBones.Head) : seljuk.transform;
            Transform kHand = kAnim.GetBoneTransform(HumanBodyBones.RightHand);
            var mid = new GameObject("LookMid").transform; mid.SetParent(root.transform); mid.position = Vector3.Lerp(P, S, 0.45f) + Vector3.up * 1.4f;
            var far = new GameObject("LookFar").transform; far.SetParent(root.transform); far.position = P + f * 6f + Vector3.up * 1.6f;

            // camera + brain
            var camGo = new GameObject("Intro Camera"); camGo.transform.SetParent(root.transform);
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f; cam.farClipPlane = 3000f;
            var cd = camGo.AddComponent<UniversalAdditionalCameraData>();
            cd.renderPostProcessing = true; cd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing; cd.antialiasingQuality = AntialiasingQuality.High;
            var brain = camGo.AddComponent<CinemachineBrain>();
            brain.DefaultBlend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.Cut, 0f);
            camGo.AddComponent<AudioListener>();

            Volume(root.transform);

            var shots = new[]
            {
                new Shot { name = "S1_Wide",    start = 0f,    end = 3.2f,  fov = 38, look = "far",
                           from = P - f * 24f - rt * 9f + Vector3.up * 8f,  to = P - f * 15f - rt * 5.5f + Vector3.up * 4f },
                new Shot { name = "S2_Face",    start = 3.2f,  end = 6.0f,  fov = 28, look = "khead",
                           from = P + f * 1.7f + rt * 1.3f + Vector3.up * 1.15f, to = P + f * 1.5f + rt * 0.45f + Vector3.up * 1.35f },
                new Shot { name = "S3_Draw",    start = 6.0f,  end = 9.0f,  fov = 34, look = "kchest",
                           from = P + f * 3.0f + rt * 1.6f + Vector3.up * 1.0f,  to = P + f * 2.1f + rt * 1.1f + Vector3.up * 1.25f },   // the sword side: the shield hides nothing
                new Shot { name = "S4_Seljuk",  start = 9.0f,  end = 11.3f, fov = 32, look = "shead",
                           from = S - f * 2.6f + rt * 1.1f + Vector3.up * 0.55f, to = S - f * 2.0f + rt * 0.8f + Vector3.up * 0.6f },
                new Shot { name = "S5_OTS",     start = 11.3f, end = 13.3f, fov = 30, look = "kchest",
                           from = S + f * 1.9f + rt * 0.95f + Vector3.up * 1.75f, to = S + f * 1.3f + rt * 0.75f + Vector3.up * 1.65f },
                new Shot { name = "S6_Title",   start = 13.3f, end = Length, fov = 40, look = "mid",
                           from = P + f * 4.5f + rt * 6f + Vector3.up * 2.2f, to = P + f * 4.5f + rt * 8.5f + Vector3.up * 4.0f },
            };
            for (int i = 0; i < shots.Length; i++) shots[i].from.y = Mathf.Max(shots[i].from.y, Ground(shots[i].from) + 0.35f);

            // timeline
            string tlPath = Dir + "/Intro_Timeline.playable";
            AssetDatabase.DeleteAsset(tlPath);
            var tl = ScriptableObject.CreateInstance<TimelineAsset>();
            AssetDatabase.CreateAsset(tl, tlPath);
            tl.editorSettings.frameRate = 30;
            var dirGo = new GameObject("Intro Director"); dirGo.transform.SetParent(root.transform);
            var director = dirGo.AddComponent<PlayableDirector>();
            director.playableAsset = tl;
            director.playOnAwake = true;
            director.extrapolationMode = DirectorWrapMode.Hold;

            var cmTrack = tl.CreateTrack<CinemachineTrack>(null, "Cameras");
            director.SetGenericBinding(cmTrack, brain);
            foreach (var sh in shots)
            {
                var rig = new GameObject("Rig_" + sh.name); rig.transform.SetParent(root.transform);
                rig.AddComponent<Animator>();
                var vgo = new GameObject("Cam"); vgo.transform.SetParent(rig.transform);
                vgo.transform.position = sh.from;
                var vc = vgo.AddComponent<CinemachineCamera>();
                var lens = LensSettings.Default; lens.FieldOfView = sh.fov; lens.NearClipPlane = 0.05f; lens.FarClipPlane = 3000f; vc.Lens = lens;
                vc.LookAt = LookTarget(sh.look, kHead, kChest, sHead, mid, far);
                vgo.AddComponent<CinemachineHardLookAt>();
                vgo.transform.rotation = Quaternion.LookRotation(vc.LookAt.position - sh.from);

                var clip = new AnimationClip { name = "Cam_" + sh.name, frameRate = 30 };
                float d = sh.end - sh.start;
                clip.SetCurve("Cam", typeof(Transform), "m_LocalPosition.x", AnimationCurve.EaseInOut(0, sh.from.x, d, sh.to.x));
                clip.SetCurve("Cam", typeof(Transform), "m_LocalPosition.y", AnimationCurve.EaseInOut(0, sh.from.y, d, sh.to.y));
                clip.SetCurve("Cam", typeof(Transform), "m_LocalPosition.z", AnimationCurve.EaseInOut(0, sh.from.z, d, sh.to.z));
                AssetDatabase.AddObjectToAsset(clip, tl);
                var at = tl.CreateTrack<AnimationTrack>(null, "Move " + sh.name);
                var tc = at.CreateClip(clip); tc.start = sh.start; tc.duration = d;
                director.SetGenericBinding(at, rig.GetComponent<Animator>());

                var shotClip = cmTrack.CreateClip<CinemachineShot>();
                shotClip.start = sh.start; shotClip.duration = d; shotClip.displayName = sh.name;
                var shot = (CinemachineShot)shotClip.asset;
                shot.VirtualCamera.exposedName = GUID.Generate().ToString();
                director.SetReferenceValue(shot.VirtualCamera.exposedName, vc);
            }

            // performances
            var kt = tl.CreateTrack<AnimationTrack>(null, "Knight");
            kt.trackOffset = TrackOffset.ApplySceneOffsets;
            director.SetGenericBinding(kt, kAnim);
            // the game's own draw: the knight's move set plays GK_Sword_Draw at 1.1x and hands the sword over at drawGrab
            var draw = Clip("GK_Sword_Draw");
            const float drawSpeed = 1.1f, drawGrab = 0.46478873f;
            float drawAt = 6.25f, drawLen = draw.length / drawSpeed;
            Place(kt, Clip("AX_unarmed_idle_looking_ver_1"), 0f, drawAt + 0.3f, true);
            var dc = Place(kt, draw, drawAt, drawLen, false);
            if (dc != null) dc.timeScale = drawSpeed;
            // after the draw the game's armed idle, then its guard (the block idle) as the Seljuk answers
            float armedAt = drawAt + drawLen - 0.35f, guardAt = 10.9f;
            Place(kt, Clip("SS_Idle"), armedAt, guardAt + 0.3f - armedAt, true);          // sword-and-shield idle of GK_Knight
            Place(kt, Clip("SS_Block_Idle"), guardAt, Length - guardAt, true);

            if (sAnim != null)
            {
                var st = tl.CreateTrack<AnimationTrack>(null, "Seljuk");
                st.trackOffset = TrackOffset.ApplySceneOffsets;
                director.SetGenericBinding(st, sAnim);
                var cry = Clip("SS_Powering_Up");   // the move set's warCry, as the enemy plays it in game
                Place(st, Clip("SS_Idle"), 0f, 9.0f, true);
                Place(st, cry, 8.8f, cry.length, false);
                float after = 8.8f + cry.length - 0.3f;
                if (after < Length) Place(st, Clip("SS_Block_Idle"), after, Length - after, true);
            }

            // the sword stays sheathed until the hand reaches it in the draw
            GrabFraction(knight, draw, kHand);   // logged as a check against the move set's drawGrab
            SwordHandOff(director, knight, drawAt + drawLen * drawGrab);

            Overlay(root.transform, cam, tl, director);
            Audio(tl);

            director.RebuildGraph();
            EditorSceneManager.MarkSceneDirty(s);
            EditorSceneManager.SaveScene(s);
            if (prevActive.IsValid()) SceneManager.SetActiveScene(prevActive);
            EditorSceneManager.CloseScene(s, true);
            AssetDatabase.SaveAssets();
            GKBridge.Log("intro: built " + ScenePath + " (" + tl.duration.ToString("F1") + " s, draw " + draw.length.ToString("F2") + " s)");
        }

        /// <summary>When in the draw the right hand comes closest to the hilt in the scabbard: the moment the sword may change hands.</summary>
        static float GrabFraction(GameObject knight, AnimationClip draw, Transform hand)
        {
            Transform scab = null;
            foreach (var t in knight.GetComponentsInChildren<Transform>(true)) if (t.name == "ScabbardPivot") { scab = t; break; }
            if (scab == null || hand == null) return 0.42f;
            float best = 0.42f, bestD = float.MaxValue;
            var pos = knight.transform.position; var rot = knight.transform.rotation;
            for (int i = 0; i <= 40; i++)
            {
                float u = i / 40f * 0.75f;
                draw.SampleAnimation(knight, u * draw.length);
                knight.transform.SetPositionAndRotation(pos, rot);
                float d = Vector3.Distance(hand.position, scab.position);
                if (d < bestD) { bestD = d; best = u; }
            }
            draw.SampleAnimation(knight, 0f);
            knight.transform.SetPositionAndRotation(pos, rot);
            GKBridge.Log("intro: hand reaches the scabbard at " + (best * 100f).ToString("F0") + "% of the draw (" + (bestD * 100f).ToString("F0") + " cm)");
            return best;
        }

        static string GrabArg(string arg)
        {
            foreach (var p in arg.Split(' ')) if (p.StartsWith("grab=")) return p.Substring(5);
            return "";
        }

        static Transform LookTarget(string n, Transform kHead, Transform kChest, Transform sHead, Transform mid, Transform far)
        {
            switch (n) { case "khead": return kHead; case "kchest": return kChest; case "shead": return sHead; case "mid": return mid; default: return far; }
        }

        static TimelineClip Place(AnimationTrack t, AnimationClip c, float start, float dur, bool loop)
        {
            if (c == null) { GKBridge.Log("intro: missing clip at " + start); return null; }
            var tc = t.CreateClip(c);
            tc.start = start; tc.duration = dur;
            ((AnimationPlayableAsset)tc.asset).removeStartOffset = true;   // every clip starts where the character stands, facing as placed
            tc.easeInDuration = start > 0 ? 0.3f : 0f;
            return tc;
        }

        /// <summary>The sword starts in the scabbard and changes to the hand at <paramref name="grab"/>, eased into the
        /// grip as the holster does in play; the pose inside the scabbard is the holster's own (SheathRef on the throat).</summary>
        static void SwordHandOff(PlayableDirector director, GameObject knight, float grab)
        {
            Transform sword = null, sheath = null;
            foreach (var t in knight.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Sword" && sword == null) sword = t;
                if (t.name == "ScabbardPivot" && sheath == null) sheath = t;
                if (t.name == "SwordL") t.gameObject.SetActive(false);
            }
            if (sword == null || sheath == null) { GKBridge.Log("intro: no sword/scabbard"); return; }
            var h = knight.AddComponent<GeorgianKnight.Cinematics.IntroSwordHandOff>();
            h.director = director; h.sword = sword; h.hand = sword.parent; h.sheath = sheath; h.grabTime = grab;
            h.handLocalPos = sword.localPosition; h.handLocalRot = sword.localRotation;
            var sr = sword.Find("SheathRef");
            if (sr != null)
            {
                const float throatOffset = 0.012f;
                float sc = sword.lossyScale.x;
                Quaternion rotW = sheath.rotation * Quaternion.Inverse(sr.localRotation);
                Vector3 guardW = sheath.position - sheath.forward * throatOffset;
                Vector3 posW = guardW - rotW * (sr.localPosition * sc);
                h.sheathLocalRot = Quaternion.Inverse(sheath.rotation) * rotW;
                h.sheathLocalPos = sheath.InverseTransformPoint(posW);
            }
            GKBridge.Log("intro: sword in the scabbard until " + grab.ToString("F2") + " s, then to " + h.hand.name);
        }

        static void Volume(Transform root)
        {
            string path = Dir + "/Intro_Grade.asset";
            AssetDatabase.DeleteAsset(path);
            var p = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(p, path);
            var tm = p.Add<Tonemapping>(true); tm.mode.Override(TonemappingMode.ACES);
            var ca = p.Add<ColorAdjustments>(true); ca.postExposure.Override(0.15f); ca.contrast.Override(22f); ca.saturation.Override(-12f); ca.colorFilter.Override(new Color(1f, 0.96f, 0.9f));
            var st = p.Add<SplitToning>(true); st.shadows.Override(new Color(0.32f, 0.45f, 0.55f)); st.highlights.Override(new Color(0.85f, 0.65f, 0.45f)); st.balance.Override(-15f);
            var bl = p.Add<Bloom>(true); bl.intensity.Override(0.7f); bl.threshold.Override(1.0f); bl.scatter.Override(0.7f);
            var vg = p.Add<Vignette>(true); vg.intensity.Override(0.33f); vg.smoothness.Override(0.45f);
            var fg = p.Add<FilmGrain>(true); fg.type.Override(FilmGrainLookup.Medium1); fg.intensity.Override(0.22f);
            var ch = p.Add<ChromaticAberration>(true); ch.intensity.Override(0.08f);
            foreach (var c in p.components) AssetDatabase.AddObjectToAsset(c, p);
            var go = new GameObject("Intro Grade"); go.transform.SetParent(root);
            var v = go.AddComponent<Volume>(); v.isGlobal = true; v.priority = 100; v.sharedProfile = p;
        }

        /// <summary>Letterbox, fade from/to black and the title card on a screen overlay (the Game view), faded by the timeline.</summary>
        static void Overlay(Transform root, Camera cam, TimelineAsset tl, PlayableDirector director)
        {
            var cgo = new GameObject("Intro Overlay", typeof(RectTransform)); cgo.transform.SetParent(root);
            var canvas = cgo.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 100;
            var scaler = cgo.AddComponent<UnityEngine.UI.CanvasScaler>(); scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 1f;
            cgo.AddComponent<Animator>();
            float bar = (1080f - 1920f / 2.39f) / 2f;
            MakeRect(cgo.transform, "BarTop", new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -bar), Vector2.zero, Color.black);
            MakeRect(cgo.transform, "BarBottom", new Vector2(0, 0), new Vector2(1, 0), Vector2.zero, new Vector2(0, bar), Color.black);

            var fade = MakeRect(cgo.transform, "Fade", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, Color.black);
            fade.AddComponent<CanvasGroup>().alpha = 1f;
            var title = new GameObject("Title", typeof(RectTransform)); title.transform.SetParent(cgo.transform, false);
            var trt = (RectTransform)title.transform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = trt.offsetMax = Vector2.zero;
            title.AddComponent<CanvasGroup>().alpha = 0f;
            var geo = GeorgianFont();
            Text(title.transform, "GEORGIAN KNIGHT", 112, new Vector2(0, 50), new Color(0.93f, 0.85f, 0.63f), 14f, null);
            Text(title.transform, "ქართველი რაინდი", 46, new Vector2(0, -40), new Color(0.88f, 0.88f, 0.88f), 4f, geo);
            Text(title.transform, "დიდგორი · 1121", 30, new Vector2(0, -100), new Color(0.73f, 0.73f, 0.73f), 6f, geo);


            var clip = new AnimationClip { name = "Overlay", frameRate = 30 };
            clip.SetCurve("Fade", typeof(CanvasGroup), "m_Alpha", new AnimationCurve(new Keyframe(0, 1), new Keyframe(1.2f, 0), new Keyframe(13.1f, 0), new Keyframe(13.6f, 0.82f), new Keyframe(Length, 0.82f)));
            clip.SetCurve("Title", typeof(CanvasGroup), "m_Alpha", new AnimationCurve(new Keyframe(0, 0), new Keyframe(13.5f, 0), new Keyframe(14.2f, 1), new Keyframe(Length, 1)));
            AssetDatabase.AddObjectToAsset(clip, tl);
            var t = tl.CreateTrack<AnimationTrack>(null, "Overlay");
            var tc = t.CreateClip(clip); tc.start = 0; tc.duration = Length;
            director.SetGenericBinding(t, cgo.GetComponent<Animator>());
        }

        static GameObject MakeRect(Transform parent, string name, Vector2 min, Vector2 max, Vector2 offMin, Vector2 offMax, Color c)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform; rt.anchorMin = min; rt.anchorMax = max; rt.offsetMin = offMin; rt.offsetMax = offMax;
            go.AddComponent<UnityEngine.UI.Image>().color = c;
            return go;
        }

        /// <summary>A dynamic TextMeshPro font from Noto Serif Georgian (OFL), made once next to the timeline.</summary>
        static TMPro.TMP_FontAsset GeorgianFont()
        {
            string path = Dir + "/Fonts/NotoSerifGeorgian SDF.asset";
            var fa = AssetDatabase.LoadAssetAtPath<TMPro.TMP_FontAsset>(path);
            if (fa != null) return fa;
            var font = AssetDatabase.LoadAssetAtPath<Font>(Dir + "/Fonts/NotoSerifGeorgian.ttf");
            if (font == null) { GKBridge.Log("intro: no Georgian font"); return null; }
            fa = TMPro.TMP_FontAsset.CreateFontAsset(font);
            fa.name = "NotoSerifGeorgian SDF";
            AssetDatabase.CreateAsset(fa, path);
            fa.atlasTexture.name = "NotoSerifGeorgian Atlas"; fa.material.name = "NotoSerifGeorgian Material";
            AssetDatabase.AddObjectToAsset(fa.atlasTexture, fa);
            AssetDatabase.AddObjectToAsset(fa.material, fa);
            fa.TryAddCharacters("ქართველი რაინდი დიდგორი · 1121");
            EditorUtility.SetDirty(fa);
            return fa;
        }

        /// <summary>Music, the narrator's two lines and the spot effects, from Cinematics/Intro/Audio (missing files are skipped).</summary>
        static void Audio(TimelineAsset tl)
        {
            void Put(string track, string file, float start, float clipIn, float dur, float vol)
            {
                var a = AssetDatabase.LoadAssetAtPath<AudioClip>(Dir + "/Audio/" + file);
                if (a == null) { GKBridge.Log("intro: no audio " + file); return; }
                var t = tl.GetOutputTracks().OfType<AudioTrack>().FirstOrDefault(x => x.name == track) ?? tl.CreateTrack<AudioTrack>(null, track);
                var c = t.CreateClip(a);
                c.start = start; c.clipIn = clipIn; c.duration = Mathf.Min(dur, a.length - clipIn, Length - start);
            }
            Put("Music", "music.wav", 0f, 0f, Length, 0.8f);
            Put("Ambience", "sfx_wind.wav", 0f, 0f, Length, 0.35f);
            Put("Narrator", "vo_1.wav", 0.8f, 0f, 3f, 1f);
            Put("Narrator", "vo_2.wav", 3.6f, 0f, 3f, 1f);
            Put("Narrator", "vo_3.wav", 12.35f, 0f, 1.5f, 1f);
            Put("SFX", "sfx_draw.wav", 6.55f, 0f, 3f, 0.9f);
            Put("SFX", "sfx_cry.wav", 9.0f, 0f, 2.2f, 0.7f);
            Put("SFX", "sfx_boom.wav", 13.3f, 0f, 1.7f, 0.9f);
        }

        static void Text(Transform parent, string s, float size, Vector2 pos, Color c, float spacing, TMPro.TMP_FontAsset font)
        {
            var go = new GameObject("Text " + s, typeof(RectTransform)); go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform; rt.sizeDelta = new Vector2(1800, 200); rt.anchoredPosition = pos;
            var t = go.AddComponent<TMPro.TextMeshProUGUI>();
            t.text = s; t.fontSize = size; t.color = c; t.alignment = TMPro.TextAlignmentOptions.Center; t.characterSpacing = spacing;
            if (font != null) t.font = font; else t.fontStyle = TMPro.FontStyles.Bold;
        }

        // ---------------- rendering ----------------

        [GKCommand("intro_still", "intro_still t1,t2,...: render stills of the intro at those seconds into GKBridge/intro (edit mode)")]
        static void Still(string arg)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var times = arg.Split(new[] { ',', ' ' }, System.StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, inv)).ToArray();
            string outDir = Path.Combine(GKBridge.Dir, "intro"); Directory.CreateDirectory(outDir);
            Render(times, 960, 540, i => Path.Combine(outDir, "still_" + times[i].ToString("00.00", inv) + ".png"));
        }

        [GKCommand("intro_render", "intro_render [fps=30] [width=1920]: render every frame of the intro to GKBridge/intro/frames (edit mode)")]
        static void RenderAll(string arg)
        {
            var parts = arg.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            int fps = parts.Length > 0 ? int.Parse(parts[0]) : 30;
            int w = parts.Length > 1 ? int.Parse(parts[1]) : 1920;
            string outDir = Path.Combine(GKBridge.Dir, "intro", "frames"); Directory.CreateDirectory(outDir);
            foreach (var f in Directory.GetFiles(outDir, "*.png")) File.Delete(f);
            int n = Mathf.RoundToInt(Length * fps);
            var times = Enumerable.Range(0, n).Select(i => i / (float)fps).ToArray();
            Render(times, w, w * 9 / 16, i => Path.Combine(outDir, i.ToString("0000") + ".png"));
        }

        static void Call(object o, string m)
        {
            var mi = o.GetType().GetMethod(m, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, System.Type.EmptyTypes, null);
            try { mi?.Invoke(o, null); } catch (System.Exception e) { GKBridge.Log("intro: " + o.GetType().Name + "." + m + " failed: " + (e.InnerException ?? e).Message); }
        }

        static void Set(object o, string field, float v)
        {
            var fi = o.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (fi != null && fi.FieldType == typeof(float)) fi.SetValue(o, v);
        }

        /// <summary>One render in progress. Frames are posed on one editor update and drawn on the next: skinned meshes
        /// are only re-skinned by the player loop in between, so posing and drawing in one go showed the body in a stale
        /// pose (the arm hung still through the whole draw while the sword, on the bone, moved on).</summary>
        sealed class Job
        {
            public float[] times; public int w, h; public System.Func<int, string> file;
            public Scene scene, prevActive; public bool wasLoaded; public List<Scene> hidden = new List<Scene>();
            public PlayableDirector director; public Camera cam; public CinemachineBrain brain;
            public RenderTexture rt; public Texture2D tex; public RenderTexture prevTarget;
            public TimelineClip[] shots; public MonoBehaviour[] grips; public GeorgianKnight.Cinematics.IntroSwordHandOff[] handOffs;
            public int i, bad; public bool posed;
            public List<KeyValuePair<float, ParticleSystem>> fx = new List<KeyValuePair<float, ParticleSystem>>();
            public List<float> shakes = new List<float>();
        }
        static Job job;

        static void Render(float[] times, int w, int h, System.Func<int, string> file, string scenePath = ScenePath)
        {
            if (Application.isPlaying) { GKBridge.Log("intro: edit mode only"); return; }
            if (job != null) { GKBridge.Log("intro: a render is already running"); return; }
            var j = new Job { times = times, w = w, h = h, file = file };
            j.scene = OpenOwn(out j.wasLoaded, scenePath);
            var s = j.scene;
            j.prevActive = SceneManager.GetActiveScene();
            SceneManager.SetActiveScene(s);
            // other open scenes (another session's work) drop out of every camera while we render, untouched otherwise
            for (int k = 0; k < SceneManager.sceneCount; k++) { var o = SceneManager.GetSceneAt(k); if (o != s && o.isLoaded) { j.hidden.Add(o); EditorSceneManager.SetSceneCullingMask(o, 0); } }
            j.director = s.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<PlayableDirector>(true)).First();
            j.cam = s.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<CinemachineBrain>(true)).First().GetComponent<Camera>();
            j.brain = j.cam.GetComponent<CinemachineBrain>();
            j.brain.UpdateMethod = CinemachineBrain.UpdateMethods.ManualUpdate;
            j.rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            j.tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            j.prevTarget = j.cam.targetTexture;
            j.cam.targetTexture = j.rt;
            j.shots = ((TimelineAsset)j.director.playableAsset).GetOutputTracks().OfType<CinemachineTrack>().First().GetClips().ToArray();
            // the game's own grip solvers close the fingers round the hilt and set the shield arm; in edit mode we run them by hand
            j.grips = s.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MonoBehaviour>(true))
                .Where(m => m != null && (m.GetType().Name == "HandGrip" || m.GetType().Name == "ShieldGrip" || m.GetType().Name == "FingerAxes")).ToArray();
            j.handOffs = s.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<GeorgianKnight.Cinematics.IntroSwordHandOff>(true)).ToArray();
            foreach (var smr in s.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<SkinnedMeshRenderer>(true))) { smr.updateWhenOffscreen = true; smr.forceMatrixRecalculationPerRender = true; }
            j.director.RebuildGraph();
            j.director.time = 0; j.director.Evaluate();
            foreach (var ho in j.handOffs) ho.Apply((float)j.director.duration);   // the grip learns the hilt with the sword in the hand
            // timed effects, named by when they fire: "FX@5.00" (particles), "Shake@5.00" (camera shake)
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var t in s.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)))
            {
                int at = t.name.IndexOf('@');
                if (at < 0 || !float.TryParse(t.name.Substring(at + 1), System.Globalization.NumberStyles.Float, inv, out float when)) continue;
                if (t.name.StartsWith("FX")) foreach (var ps in t.GetComponents<ParticleSystem>()) j.fx.Add(new KeyValuePair<float, ParticleSystem>(when, ps));
                if (t.name.StartsWith("Shake")) j.shakes.Add(when);
            }
            foreach (var g in j.grips) { Call(g, "Awake"); Call(g, "Start"); }
            job = j;
            EditorApplication.update -= Step;
            EditorApplication.update += Step;
            GKBridge.Log("intro: rendering " + times.Length + " frame(s) " + w + "x" + h + "...");
        }

        static void Pose(Job j, float t)
        {
            j.director.time = t;
            j.director.Evaluate();
            foreach (var ho in j.handOffs) ho.Apply(t);
            bool inHand = j.handOffs.Length == 0 || j.handOffs.Any(ho => ho.sword != null && ho.sword.parent == ho.hand);
            foreach (var g in j.grips)
            {
                if (g.GetType().Name == "FingerAxes") continue;
                if (g.GetType().Name == "HandGrip") Set(g, "weight", inHand ? 1f : 0f);
                else Set(g, "weight", 1f);
                Call(g, "LateUpdate");
            }
            j.brain.ManualUpdate();
            // the camera the cut says should be live; place it ourselves when the brain did not follow
            var clip = j.shots.LastOrDefault(c => t >= c.start - 1e-4);
            var vc = clip != null ? j.director.GetReferenceValue(((CinemachineShot)clip.asset).VirtualCamera.exposedName, out _) as CinemachineCamera : null;
            if (vc != null && (j.cam.transform.position - vc.transform.position).sqrMagnitude > 0.01f)
            {
                j.bad++;
                j.cam.transform.position = vc.transform.position;
                if (vc.LookAt != null) j.cam.transform.rotation = Quaternion.LookRotation(vc.LookAt.position - vc.transform.position);
                j.cam.fieldOfView = vc.Lens.FieldOfView;
            }
            foreach (var kv in j.fx)
            {
                if (t >= kv.Key) { kv.Value.gameObject.SetActive(true); kv.Value.Simulate(t - kv.Key, true, true); }
                else kv.Value.gameObject.SetActive(false);
            }
            foreach (var when in j.shakes)
            {
                // a hit's shake: a sharp kick that rings out in a few tenths of a second
                float u = t - when;
                if (u < 0f || u > 0.6f) continue;
                float amp = 1.6f * Mathf.Exp(-u * 9f);
                j.cam.transform.rotation *= Quaternion.Euler(amp * (Mathf.PerlinNoise(u * 25f, 0.3f) - 0.5f) * 2f, amp * (Mathf.PerlinNoise(0.7f, u * 25f) - 0.5f) * 2f, amp * 0.6f * (Mathf.PerlinNoise(u * 19f, 4.1f) - 0.5f));
            }
            if (j.times.Length <= 12)
                foreach (var ho in j.handOffs)
                {
                    // where the hand bone is against the hilt and the scabbard: proves the grip by number, not by eye
                    var an = ho.GetComponent<Animator>();
                    var rh = an != null ? an.GetBoneTransform(HumanBodyBones.RightHand) : null;
                    var grip = ho.sword != null ? ho.sword.Find("Grip") : null;
                    if (rh != null && grip != null)
                        GKBridge.Log("intro: t=" + t.ToString("F2") + " sword under " + ho.sword.parent.name + ", hand-grip " + (Vector3.Distance(rh.position, grip.position) * 100f).ToString("F0") +
                                     " cm, hand-scabbard " + (Vector3.Distance(rh.position, ho.sheath.position) * 100f).ToString("F0") + " cm");
                }
        }

        static void Step()
        {
            var j = job;
            if (j == null) { EditorApplication.update -= Step; return; }
            if (Application.isPlaying || !j.scene.isLoaded) { GKBridge.Log("intro: render stopped (play mode or scene closed)"); Finish(j); return; }
            try
            {
                if (!j.posed)
                {
                    if (j.i % 30 == 0) EditorUtility.DisplayProgressBar("Intro", "frame " + j.i + "/" + j.times.Length, j.i / (float)j.times.Length);
                    Pose(j, j.times[j.i]);
                    j.posed = true;
                    EditorApplication.QueuePlayerLoopUpdate();   // the skinned meshes catch up with the bones before the frame is drawn
                    return;
                }
                // the edit-mode player loop also ran the hand-off's own LateUpdate; set the frame's pose once more on top
                Pose(j, j.times[j.i]);
                j.cam.Render();
                var prev = RenderTexture.active; RenderTexture.active = j.rt;
                j.tex.ReadPixels(new Rect(0, 0, j.w, j.h), 0, 0); j.tex.Apply();
                RenderTexture.active = prev;
                File.WriteAllBytes(j.file(j.i), j.tex.EncodeToPNG());
                j.posed = false;
                j.i++;
                if (j.i >= j.times.Length)
                {
                    GKBridge.Log("intro: rendered " + j.times.Length + " frame(s) " + j.w + "x" + j.h + (j.bad > 0 ? ", camera placed by hand in " + j.bad / 2 : ""));
                    Finish(j);
                }
            }
            catch (System.Exception e) { GKBridge.Log("intro: render failed at frame " + j.i + ": " + e.Message); Finish(j); }
        }

        static void Finish(Job j)
        {
            EditorApplication.update -= Step;
            job = null;
            try
            {
                j.cam.targetTexture = j.prevTarget;
                foreach (var o in j.hidden) EditorSceneManager.SetSceneCullingMask(o, EditorSceneManager.DefaultSceneCullingMask);
                j.director.time = 0; j.director.Evaluate();
                Object.DestroyImmediate(j.rt); Object.DestroyImmediate(j.tex);
                EditorUtility.ClearProgressBar();
                if (j.prevActive.IsValid() && j.prevActive != j.scene) SceneManager.SetActiveScene(j.prevActive);
                if (!j.wasLoaded) EditorSceneManager.CloseScene(j.scene, true);
            }
            catch (System.Exception e) { GKBridge.Log("intro: cleanup: " + e.Message); }
        }
    }
}
