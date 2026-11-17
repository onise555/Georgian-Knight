using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// The battle intro's charge and clash: the knight and the Seljuk run at each other and meet blade on shield. Built
    /// in Intro_Battle.unity, a copy of the finished intro scene (same field, light, grade and characters). Clips are laid
    /// end to end with their root motion, each placed where the previous one left the character, then a measuring pass
    /// moves both runs so the knight's blade lands on the Seljuk's guard at the strike frame.
    /// </summary>
    public static partial class GKIntro
    {
        const string BattleScene = "Assets/_GeorgianKnight/Scenes/Intro_Battle.unity";

        /// <summary>Lays one character's clips end to end along <paramref name="dir"/>, root motion included.</summary>
        sealed class Lane
        {
            public AnimationTrack track; public Animator anim; public Vector3 dir; public Vector3 pos; public float t;
            public readonly List<TimelineClip> clips = new List<TimelineClip>();

            public TimelineClip Add(AnimationClip c, float dur, float overlap = 0.15f, bool moves = true)
            {
                if (c == null) { GKBridge.Log("intro: missing clip"); return null; }
                float start = clips.Count == 0 ? t : t - overlap;
                var tc = track.CreateClip(c);
                tc.start = start; tc.duration = dur;
                var a = (AnimationPlayableAsset)tc.asset;
                a.removeStartOffset = true;
                a.position = pos; a.rotation = Quaternion.LookRotation(dir);
                clips.Add(tc);
                t = start + dur;
                // where the root ends up: the clip's own forward speed, at this character's size
                if (moves) pos += dir * Speed(c, anim) * dur;
                return tc;
            }
        }

        static float Speed(AnimationClip c, Animator a)
        {
            float v = new Vector3(c.averageSpeed.x, 0f, c.averageSpeed.z).magnitude;
            return c.humanMotion && a != null && a.isHuman ? v * a.humanScale : v;
        }

        /// <summary>When in an attack the sword hand comes down hardest: the frame of the hit.</summary>
        static float StrikeTime(GameObject who, AnimationClip c)
        {
            var a = who.GetComponent<Animator>();
            var hand = a.GetBoneTransform(HumanBodyBones.RightHand);
            var pos = who.transform.position; var rot = who.transform.rotation;
            float best = c.length * 0.5f, bestV = 0f; Vector3 prev = Vector3.zero;
            int n = 60;
            for (int i = 0; i <= n; i++)
            {
                float u = i / (float)n * c.length;
                c.SampleAnimation(who, u);
                who.transform.SetPositionAndRotation(pos, rot);
                Vector3 h = who.transform.InverseTransformPoint(hand.position);
                if (i > 0)
                {
                    float down = (prev.y - h.y) * n / c.length;   // downward speed of the hand, m/s
                    if (down > bestV) { bestV = down; best = u; }
                }
                prev = h;
            }
            c.SampleAnimation(who, 0f);
            who.transform.SetPositionAndRotation(pos, rot);
            GKBridge.Log("intro: " + c.name + " strikes at " + best.ToString("F2") + " s of " + c.length.ToString("F2") + " (hand down " + bestV.ToString("F1") + " m/s)");
            return best;
        }

        [GKCommand("intro_clash", "intro_clash [contact=5]: build Intro_Battle.unity, the charge and the clash (edit mode)")]
        static void Clash(string arg)
        {
            if (Application.isPlaying) { GKBridge.Log("intro: edit mode only"); return; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            float tC = string.IsNullOrEmpty(arg) ? 5f : float.Parse(arg.Trim(), inv);

            var open = SceneManager.GetSceneByPath(BattleScene);
            if (open.IsValid() && open.isLoaded) EditorSceneManager.CloseScene(open, true);
            AssetDatabase.DeleteAsset(BattleScene);
            if (!AssetDatabase.CopyAsset(ScenePath, BattleScene)) { GKBridge.Log("intro: copy failed"); return; }
            var s = EditorSceneManager.OpenScene(BattleScene, OpenSceneMode.Additive);
            var prevActive = SceneManager.GetActiveScene();
            SceneManager.SetActiveScene(s);

            var knight = Find(s, "Knight"); var seljuk = Find(s, "Seljuk");
            var root = Find(s, "Intro_Cutscene").transform;
            root.name = "Intro_Battle";
            foreach (var t in root.Cast<Transform>().ToArray())
                if (t.name.StartsWith("Rig_") || t.name == "Intro Director" || t.name == "Intro Overlay" || t.name.StartsWith("Look")) Object.DestroyImmediate(t.gameObject);
            var ho = knight.GetComponent<GeorgianKnight.Cinematics.IntroSwordHandOff>();
            if (ho != null) ho.grabTime = -1f;   // armed from the first frame: the draw belongs to the earlier shot
            var brain = root.GetComponentInChildren<CinemachineBrain>(true);
            var kAnim = knight.GetComponent<Animator>(); var sAnim = seljuk.GetComponent<Animator>();

            Vector3 f = Vector3.ProjectOnPlane(knight.transform.forward, Vector3.up).normalized;
            Vector3 rt = Vector3.Cross(Vector3.up, f);
            var terrain = s.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Terrain>(true)).FirstOrDefault();
            System.Func<Vector3, float> ground = v => terrain != null ? terrain.SampleHeight(v) + terrain.transform.position.y : v.y;

            // the meeting point: a little ahead of where the knight stood in the first intro
            Vector3 M = knight.transform.position + f * 6f; M.y = ground(M);

            string tlPath = Dir + "/Intro_Battle.playable";
            AssetDatabase.DeleteAsset(tlPath);
            var tl = ScriptableObject.CreateInstance<TimelineAsset>();
            AssetDatabase.CreateAsset(tl, tlPath);
            tl.editorSettings.frameRate = 30;
            var dirGo = new GameObject("Battle Director"); dirGo.transform.SetParent(root);
            var director = dirGo.AddComponent<PlayableDirector>();
            director.playableAsset = tl; director.playOnAwake = true; director.extrapolationMode = DirectorWrapMode.Hold;
            if (ho != null) ho.director = director;

            AnimationTrack NewTrack(string name, Animator a)
            {
                var tr = tl.CreateTrack<AnimationTrack>(null, name);
                tr.trackOffset = TrackOffset.ApplyTransformOffsets; tr.position = Vector3.zero; tr.eulerAngles = Vector3.zero;
                director.SetGenericBinding(tr, a);
                return tr;
            }

            // knight: guard, a run, the leaping attack whose blow lands at tC
            var run = Clip("SS_Run"); var leap = Clip("SS_Jump_Attack"); var guard = Clip("SS_Idle"); var block = Clip("SS_Block_Idle");
            var blocked = Clip("SS_Blocked_Impact"); var raise = Clip("SS_Idle_To_Block");
            float strike = StrikeTime(knight, leap);
            float leapAt = tC - strike;
            var k = new Lane { track = NewTrack("Knight", kAnim), anim = kAnim, dir = f, pos = Vector3.zero, t = 0f };
            k.Add(guard, 0.7f, 0f, false);
            while (k.t < leapAt - 0.05f) k.Add(run, Mathf.Min(run.length, leapAt - k.t + 0.15f));
            k.Add(leap, leap.length);
            k.Add(block, 3f, 0.25f, false);

            // Seljuk: waits, charges, plants and raises his shield, takes the blow on it
            float blockAt = tC - 0.55f;
            var sl = new Lane { track = NewTrack("Seljuk", sAnim), anim = sAnim, dir = -f, pos = Vector3.zero, t = 0f };
            sl.Add(guard, 1.1f, 0f, false);
            while (sl.t < blockAt - 0.05f) sl.Add(run, Mathf.Min(run.length, blockAt - sl.t + 0.15f));
            sl.Add(raise, Mathf.Max(0.4f, raise.length), 0.2f, false);
            sl.Add(blocked, blocked.length, 0.08f, false);
            sl.Add(block, 3f, 0.25f, false);
            float end = Mathf.Max(k.t, sl.t) - 1.5f;

            // first pass laid both lanes from the origin; now measure where they really are at the blow and move each
            // whole lane so the knight's blade meets the Seljuk's shield at M
            director.RebuildGraph();
            director.time = tC; director.Evaluate();
            Vector3 kAt = knight.transform.position, sAt = seljuk.transform.position;
            Vector3 kWant = M - f * 0.55f, sWant = M + f * 0.55f;
            foreach (var c in k.clips) ((AnimationPlayableAsset)c.asset).position += Flat(kWant - kAt);
            foreach (var c in sl.clips) ((AnimationPlayableAsset)c.asset).position += Flat(sWant - sAt);
            director.RebuildGraph();
            director.time = tC; director.Evaluate();
            var blade = ho != null && ho.sword != null ? ho.sword.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "BladeTip") : null;
            var shield = seljuk.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.Contains("Shield"));
            Vector3 hit = blade != null ? blade.transform.position : M + Vector3.up * 1.3f;
            GKBridge.Log("intro: at the blow knight " + (Vector3.Distance(knight.transform.position, kWant) * 100f).ToString("F0") + " cm off, Seljuk " +
                         (Vector3.Distance(seljuk.transform.position, sWant) * 100f).ToString("F0") + " cm off, gap " + (Vector3.Distance(knight.transform.position, seljuk.transform.position)).ToString("F2") + " m" +
                         (shield != null ? ", blade tip to shield " + (Vector3.Distance(hit, shield.position) * 100f).ToString("F0") + " cm" : ""));
            // root motion checked frame by frame: a jump between two frames means two clips were laid apart
            foreach (var who in new[] { knight, seljuk })
            {
                float worst = 0f, worstAt = 0f; Vector3 last = Vector3.zero;
                for (int i = 0; i <= Mathf.CeilToInt(end * 30f); i++)
                {
                    director.time = i / 30f; director.Evaluate();
                    Vector3 p = who.transform.position;
                    if (i > 0) { float step = Flat(p - last).magnitude; if (step > worst) { worst = step; worstAt = i / 30f; } }
                    last = p;
                }
                GKBridge.Log("intro: " + who.name + " largest step between frames " + (worst * 100f).ToString("F0") + " cm at " + worstAt.ToString("F2") + " s (a run is ~20 cm)");
            }
            director.time = 0; director.Evaluate();
            Vector3 kStart = k.clips[0] != null ? ((AnimationPlayableAsset)k.clips[0].asset).position : knight.transform.position;
            Vector3 sStart = ((AnimationPlayableAsset)sl.clips[0].asset).position;

            // sparks and a shake on the blow
            Sparks(root, hit, tC);
            new GameObject("Shake@" + tC.ToString("F2", inv)).transform.SetParent(root);

            // cameras: track the knight's charge, the Seljuk coming at us, then the clash side-on, close and low
            var cm = tl.CreateTrack<CinemachineTrack>(null, "Cameras");
            director.SetGenericBinding(cm, brain);
            var kChest = kAnim.GetBoneTransform(HumanBodyBones.Chest); var sHead = sAnim.GetBoneTransform(HumanBodyBones.Head);
            var mid = new GameObject("LookClash").transform; mid.SetParent(root); mid.position = hit;
            float c1 = Mathf.Max(1.2f, tC - 2.2f), c2 = tC - 0.9f;
            Vector3 kAtC1 = Vector3.Lerp(kStart, kWant, Mathf.InverseLerp(0.7f, tC, c1));
            BattleShot(tl, director, cm, root, "B1_Charge", 0f, c1, kChest, 38,
                kStart + rt * 3.2f + Vector3.up * 1.0f - f * 1.0f, kAtC1 + rt * 3.0f + Vector3.up * 1.1f + f * 0.6f, ground);
            Vector3 sAtC1 = Vector3.Lerp(sStart, sWant, Mathf.InverseLerp(1.1f, blockAt, c1));
            BattleShot(tl, director, cm, root, "B2_Seljuk", c1, c2, sHead, 30,
                sAtC1 - f * 3.5f - rt * 0.8f + Vector3.up * 0.5f, sWant - f * 2.2f - rt * 0.6f + Vector3.up * 0.55f, ground);
            BattleShot(tl, director, cm, root, "B3_Clash", c2, end, mid, 34,
                M + rt * 3.4f + Vector3.up * 0.9f - f * 0.3f, M + rt * 2.6f + Vector3.up * 1.05f + f * 0.2f, ground);

            director.RebuildGraph();
            director.time = 0; director.Evaluate();
            EditorSceneManager.MarkSceneDirty(s);
            EditorSceneManager.SaveScene(s);
            if (prevActive.IsValid()) SceneManager.SetActiveScene(prevActive);
            EditorSceneManager.CloseScene(s, true);
            AssetDatabase.SaveAssets();
            GKBridge.Log("intro: built " + BattleScene + " (" + end.ToString("F1") + " s, blow at " + tC.ToString("F2") + " s)");
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        static void BattleShot(TimelineAsset tl, PlayableDirector director, CinemachineTrack cm, Transform root, string name, float start, float end, Transform look, float fov,
                               Vector3 from, Vector3 to, System.Func<Vector3, float> ground)
        {
            from.y = Mathf.Max(from.y, ground(from) + 0.3f); to.y = Mathf.Max(to.y, ground(to) + 0.3f);
            var rig = new GameObject("Rig_" + name); rig.transform.SetParent(root);
            rig.AddComponent<Animator>();
            var vgo = new GameObject("Cam"); vgo.transform.SetParent(rig.transform);
            vgo.transform.position = from;
            var vc = vgo.AddComponent<CinemachineCamera>();
            var lens = LensSettings.Default; lens.FieldOfView = fov; lens.NearClipPlane = 0.05f; lens.FarClipPlane = 3000f; vc.Lens = lens;
            vc.LookAt = look;
            vgo.AddComponent<CinemachineHardLookAt>();
            float d = end - start;
            var clip = new AnimationClip { name = "Cam_" + name, frameRate = 30 };
            clip.SetCurve("Cam", typeof(Transform), "m_LocalPosition.x", AnimationCurve.EaseInOut(0, from.x, d, to.x));
            clip.SetCurve("Cam", typeof(Transform), "m_LocalPosition.y", AnimationCurve.EaseInOut(0, from.y, d, to.y));
            clip.SetCurve("Cam", typeof(Transform), "m_LocalPosition.z", AnimationCurve.EaseInOut(0, from.z, d, to.z));
            AssetDatabase.AddObjectToAsset(clip, tl);
            var at = tl.CreateTrack<AnimationTrack>(null, "Move " + name);
            var tc = at.CreateClip(clip); tc.start = start; tc.duration = d;
            director.SetGenericBinding(at, rig.GetComponent<Animator>());
            var sc = cm.CreateClip<CinemachineShot>();
            sc.start = start; sc.duration = d; sc.displayName = name;
            var shot = (CinemachineShot)sc.asset;
            shot.VirtualCamera.exposedName = GUID.Generate().ToString();
            director.SetReferenceValue(shot.VirtualCamera.exposedName, vc);
        }

        /// <summary>Steel on steel: a short burst of hot sparks thrown off the blade, falling under gravity.</summary>
        static void Sparks(Transform root, Vector3 at, float when)
        {
            var go = new GameObject("FX@" + when.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            go.transform.SetParent(root); go.transform.position = at;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.duration = 1f; main.loop = false; main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(3f, 9f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.03f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.85f, 0.45f), new Color(1f, 0.55f, 0.15f));
            main.gravityModifier = 1.2f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.useUnscaledTime = false;
            var em = ps.emission; em.rateOverTime = 0f; em.SetBursts(new[] { new ParticleSystem.Burst(0f, 70) });
            var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Sphere; sh.radius = 0.04f;
            var col = ps.colorOverLifetime; col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(new Color(1f, 0.95f, 0.8f), 0f), new GradientColorKey(new Color(1f, 0.45f, 0.1f), 1f) },
                      new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.06f; r.lengthScale = 1.5f;
            string matPath = Dir + "/Intro_Sparks.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                mat = new Material(shader);
                mat.SetFloat("_Surface", 1f); mat.SetFloat("_Blend", 2f);   // transparent, additive
                mat.SetColor("_BaseColor", new Color(2.5f, 1.6f, 0.7f, 1f));
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); mat.EnableKeyword("_BLENDMODE_ADD");
                mat.renderQueue = 3000;
                AssetDatabase.CreateAsset(mat, matPath);
            }
            r.sharedMaterial = mat;
            go.SetActive(false);
        }

        [GKCommand("intro_clash_render", "intro_clash_render [width=1920] [slow=0.25]: render Intro_Battle, slowed around the blow, to GKBridge/intro/battle")]
        static void ClashRender(string arg)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var parts = arg.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            int w = parts.Length > 0 ? int.Parse(parts[0]) : 1920;
            float slow = parts.Length > 1 ? float.Parse(parts[1], inv) : 0.25f;
            var tl = AssetDatabase.LoadAssetAtPath<TimelineAsset>(Dir + "/Intro_Battle.playable");
            if (tl == null) { GKBridge.Log("intro: run intro_clash first"); return; }
            float blow = 5f;
            var s = OpenOwn(out bool was, BattleScene);
            foreach (var t in s.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)))
                if (t.name.StartsWith("Shake@")) float.TryParse(t.name.Substring(6), System.Globalization.NumberStyles.Float, inv, out blow);
            float end = (float)s.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<PlayableDirector>(true)).First().duration;
            if (!was) EditorSceneManager.CloseScene(s, true);
            // 30 fps of picture; the timeline clock runs at `slow` speed from just before the blow to just after it
            var times = new List<float>();
            for (float t = 0f; t < end; )
            {
                times.Add(t);
                bool inSlow = t > blow - 0.3f && t < blow + 0.45f;
                t += (inSlow ? slow : 1f) / 30f;
            }
            string outDir = Path.Combine(GKBridge.Dir, "intro", "battle"); Directory.CreateDirectory(outDir);
            foreach (var f in Directory.GetFiles(outDir, "*.png")) File.Delete(f);
            Render(times.ToArray(), w, w * 9 / 16, i => Path.Combine(outDir, i.ToString("0000") + ".png"), BattleScene);
        }

        [GKCommand("intro_clash_still", "intro_clash_still t1,t2,...: stills of Intro_Battle into GKBridge/intro/battle_still_*.png")]
        static void ClashStill(string arg)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var times = arg.Split(new[] { ',', ' ' }, System.StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, inv)).ToArray();
            string outDir = Path.Combine(GKBridge.Dir, "intro"); Directory.CreateDirectory(outDir);
            Render(times, 960, 540, i => Path.Combine(outDir, "battle_still_" + times[i].ToString("00.00", inv) + ".png"), BattleScene);
        }
    }
}
