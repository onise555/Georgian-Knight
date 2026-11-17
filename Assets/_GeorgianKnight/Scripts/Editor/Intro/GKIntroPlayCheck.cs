using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Plays Intro_Cutscene.unity in Play mode from start to end, the way a player sees it: the scene is set as the Play
    /// mode start scene (the scenes open in the editor are left alone), the timeline runs on its own, a frame is captured
    /// at each beat, every error and warning logged during the run is counted, and Play mode ends with the timeline.
    /// </summary>
    [InitializeOnLoad]
    public static partial class GKIntro
    {
        const string CheckKey = "GKIntro.PlayCheck";
        static readonly float[] Beats = { 1.5f, 4.5f, 6.5f, 6.8f, 7.1f, 8.5f, 10f, 12.3f, 14.5f };
        static int nextBeat, errors, warnings;
        static List<string> firstErrors = new List<string>();
        static double startedAt;

        static GKIntro()
        {
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        [GKCommand("intro_playcheck", "intro_playcheck: play Intro_Cutscene.unity in Play mode end to end, capture each beat to GKBridge/intro/play_*.png, count errors, then stop")]
        static bool PlayCheck(string arg)
        {
            if (Application.isPlaying) { GKBridge.Log("intro: stop Play mode first"); return false; }
            var scene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            if (scene == null) { GKBridge.Log("intro: no " + ScenePath); return false; }
            EditorSceneManager.playModeStartScene = scene;
            SessionState.SetBool(CheckKey, true);
            GKBridge.Log("intro: play check starting in " + ScenePath);
            EditorApplication.isPlaying = true;
            return true;   // play reloads the domain: queued commands wait for it
        }

        static void OnPlayMode(PlayModeStateChange s)
        {
            if (!SessionState.GetBool(CheckKey, false)) return;
            if (s == PlayModeStateChange.EnteredPlayMode)
            {
                nextBeat = 0; errors = 0; warnings = 0; firstErrors.Clear();
                startedAt = EditorApplication.timeSinceStartup;
                Application.logMessageReceived -= OnLog; Application.logMessageReceived += OnLog;
                EditorApplication.update -= Watch; EditorApplication.update += Watch;
            }
            else if (s == PlayModeStateChange.EnteredEditMode)
            {
                Application.logMessageReceived -= OnLog;
                EditorApplication.update -= Watch;
                EditorSceneManager.playModeStartScene = null;
                SessionState.SetBool(CheckKey, false);
            }
        }

        static void OnLog(string msg, string stack, LogType type)
        {
            if (msg.StartsWith("[GK]")) return;
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                errors++;
                if (firstErrors.Count < 5) firstErrors.Add(msg.Length > 200 ? msg.Substring(0, 200) : msg);
            }
            else if (type == LogType.Warning) warnings++;
        }

        static void Watch()
        {
            if (!Application.isPlaying) return;
            var director = Object.FindObjectsByType<PlayableDirector>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault();
            if (director == null)
            {
                if (EditorApplication.timeSinceStartup - startedAt > 20) { GKBridge.Log("intro: play check found no director, stopping"); EditorApplication.isPlaying = false; }
                return;
            }
            float t = (float)director.time;
            while (nextBeat < Beats.Length && t >= Beats[nextBeat])
            {
                Capture(t, Beats[nextBeat]);
                nextBeat++;
            }
            bool over = director.state != PlayState.Playing && t <= 0.001f && nextBeat > 0   // wrapped or stopped after it ran
                        || t >= director.duration - 0.05;
            if (over || EditorApplication.timeSinceStartup - startedAt > 120)
            {
                var ho = Object.FindObjectsByType<GeorgianKnight.Cinematics.IntroSwordHandOff>(FindObjectsSortMode.None).FirstOrDefault();
                string sword = ho != null && ho.sword != null ? ", sword under " + ho.sword.parent.name : "";
                GKBridge.Log("intro: play check done at t=" + t.ToString("F2") + " of " + director.duration.ToString("F1") + " s, " + nextBeat + "/" + Beats.Length + " beats captured, " +
                             errors + " error(s), " + warnings + " warning(s)" + sword + (firstErrors.Count > 0 ? " | first: " + string.Join(" || ", firstErrors.ToArray()) : ""));
                EditorApplication.update -= Watch;
                EditorApplication.isPlaying = false;
            }
        }

        static void Capture(float t, float beat)
        {
            var cam = Camera.main;
            if (cam == null) { GKBridge.Log("intro: no main camera at " + t.ToString("F2")); return; }
            const int w = 960, h = 540;
            var rt = RenderTexture.GetTemporary(w, h, 24);
            var prev = cam.targetTexture;
            cam.targetTexture = rt; cam.Render(); cam.targetTexture = prev;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            var a = RenderTexture.active; RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
            RenderTexture.active = a; RenderTexture.ReleaseTemporary(rt);
            string dir = Path.Combine(GKBridge.Dir, "intro"); Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "play_" + beat.ToString("00.00", System.Globalization.CultureInfo.InvariantCulture) + ".png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            GKBridge.Log("intro: play beat " + beat.ToString("F2") + " captured at t=" + t.ToString("F2"));
        }
    }
}
