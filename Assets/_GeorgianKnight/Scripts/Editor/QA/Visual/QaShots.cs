using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// "qa.shots": renders a standard set of review frames per build scene — from every spawn point (follow-cam and eye
    /// level) and every "QAView*" marker — through URP with post-processing, measures exposure (mean luminance, clipped
    /// highlights, crushed blacks, saturation, contrast), and writes GKBridge/qa_shots/{*.png, metrics.json, review.md}.
    /// review.md is the sheet a human or AI art critic fills in (rubric in Docs/QA.md); the "visual" validator turns
    /// flagged frames into gate issues.
    /// </summary>
    public static class QaShots
    {
        public const string MetricsFile = "metrics.json";
        public const string ReviewFile = "review.md";

        /// <summary>One camera placement to capture.</summary>
        sealed class View
        {
            public string Name;
            public Vector3 Position;
            public Quaternion Rotation;
            public float Fov = 55f;
        }

        /// <summary>Metrics of one captured frame.</summary>
        sealed class Shot
        {
            public string Scene, View, File;
            public ImageStats Stats;
            public List<QaRule> Flags;
        }

        public static string OutputDir { get { return Path.Combine(GKBridge.Dir, "qa_shots"); } }

        static readonly string[] Rubric =
        {
            "Silhouette — readable shapes at gameplay distance; no blobby or noisy outlines",
            "Material realism — plausible albedo/roughness, no plastic sheen, no flat untextured surfaces",
            "Scale — doors, steps, props and characters in believable proportion (1.5–2.2 m people)",
            "Texture resolution consistency — no blurry asset next to a crisp one; no visible tiling",
            "Lighting — a clear key light, grounded shadows, no light leaks, no floating objects",
            "Composition — a focal point, leading lines, uncluttered frame edges",
            "Historical plausibility — 12th-century Georgia (Didgori, Tbilisi, Narikala): no anachronisms",
            "UI legibility — HUD/subtitles readable on this background (in-game captures)"
        };

        public static void Capture(string filter)
        {
            if (EditorApplication.isPlaying) { GKBridge.Log("qa.shots: leave play mode first"); return; }
            if (!QaUnity.HasGpu) { GKBridge.Log("qa.shots: no graphics device (-nographics); cannot render"); return; }
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty) { GKBridge.Log("qa.shots: save or discard changes in '" + SceneManager.GetSceneAt(i).name + "' first"); return; }
            var ctx = new QaContext();
            Directory.CreateDirectory(OutputDir);
            foreach (var old in Directory.GetFiles(OutputDir, "*.png")) File.Delete(old);
            var shots = new List<Shot>();
            var sceneViews = new List<KeyValuePair<string, int>>();
            var setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                foreach (var s in EditorBuildSettings.scenes)
                {
                    if (!s.enabled || string.IsNullOrEmpty(s.path)) continue;
                    if (!string.IsNullOrEmpty(filter) && s.path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    EditorUtility.DisplayProgressBar("qa.shots", s.path, 0.5f);
                    var scene = EditorSceneManager.OpenScene(s.path, OpenSceneMode.Single);
                    var views = FindViews(scene, ctx.Settings);
                    sceneViews.Add(new KeyValuePair<string, int>(s.path, views.Count));
                    if (views.Count > 0) shots.AddRange(Shoot(s.path, scene.name, views, ctx.Settings.visual));
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                QaUnity.RestoreScenes(setup);
            }
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(Path.Combine(OutputDir, MetricsFile), Metrics(shots, sceneViews), utf8);
            File.WriteAllText(Path.Combine(OutputDir, ReviewFile), Review(shots, sceneViews), utf8);
            int flagged = 0;
            foreach (var s in shots) if (s.Flags.Count > 0) flagged++;
            GKBridge.Log("qa.shots: " + shots.Count + " frame(s) from " + sceneViews.Count + " scene(s), " + flagged + " flagged → " + Path.Combine(OutputDir, ReviewFile));
        }

        static List<View> FindViews(Scene scene, QaSettings settings)
        {
            var views = new List<View>();
            var vis = settings.visual;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(false))
                {
                    if (t.name.StartsWith("QAView", StringComparison.OrdinalIgnoreCase))
                    {
                        var cam = t.GetComponent<Camera>();
                        views.Add(new View { Name = t.name, Position = t.position, Rotation = t.rotation, Fov = cam != null ? cam.fieldOfView : 55f });
                        continue;
                    }
                    bool spawn = false;
                    foreach (var c in t.GetComponents<Component>())
                        if (c != null && settings.scene.spawnComponents.Contains(c.GetType().Name)) spawn = true;
                    if (!spawn) continue;
                    var fwd = Vector3.ProjectOnPlane(t.forward, Vector3.up);
                    if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
                    fwd.Normalize();
                    var follow = t.position + Vector3.up * (vis.eyeHeight + 0.6f) - fwd * vis.followDistance;
                    var target = t.position + Vector3.up * 1.4f + fwd * 4f;
                    views.Add(new View { Name = t.name + "_follow", Position = follow, Rotation = Quaternion.LookRotation(target - follow, Vector3.up) });
                    views.Add(new View { Name = t.name + "_eye", Position = t.position + Vector3.up * vis.eyeHeight, Rotation = Quaternion.LookRotation(fwd, Vector3.up), Fov = 65f });
                }
            if (views.Count == 0 && Camera.main != null)
                views.Add(new View { Name = "MainCamera", Position = Camera.main.transform.position, Rotation = Camera.main.transform.rotation, Fov = Camera.main.fieldOfView });
            var used = new HashSet<string>();
            foreach (var v in views)
            {
                string n = Safe(v.Name);
                for (int k = 2; !used.Add(n); k++) n = Safe(v.Name) + "_" + k;
                v.Name = n;
            }
            return views;
        }

        static string Safe(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }

        static List<Shot> Shoot(string scenePath, string sceneName, List<View> views, QaSettings.VisualLimits lim)
        {
            var shots = new List<Shot>();
            var go = new GameObject("~qa_shot_camera") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var cam = go.AddComponent<Camera>();
                var template = Camera.main;
                if (template != null) cam.CopyFrom(template);
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = Mathf.Max(cam.farClipPlane, 3000f);
                var data = cam.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = true;
                data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                var templateData = template != null ? template.GetComponent<UniversalAdditionalCameraData>() : null;
                if (templateData != null) data.volumeLayerMask = templateData.volumeLayerMask;
                foreach (var v in views)
                {
                    cam.transform.SetPositionAndRotation(v.Position, v.Rotation);
                    cam.fieldOfView = v.Fov;
                    byte[] png;
                    var stats = Render(cam, lim.width, lim.height, out png);
                    string file = Safe(sceneName) + "__" + v.Name + ".png";
                    File.WriteAllBytes(Path.Combine(OutputDir, file), png);
                    shots.Add(new Shot { Scene = scenePath, View = v.Name, File = file, Stats = stats, Flags = PixelMath.ExposureFlags(stats, lim) });
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
            return shots;
        }

        static ImageStats Render(Camera cam, int w, int h, out byte[] png)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            var prev = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                var req = new RenderPipeline.StandardRequest { destination = rt };
                if (RenderPipeline.SupportsRenderRequest(cam, req)) RenderPipeline.SubmitRenderRequest(cam, req);
                else cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                png = tex.EncodeToPNG();
                return PixelMath.Analyze(tex.GetRawTextureData());
            }
            finally
            {
                RenderTexture.active = prev;
                cam.targetTexture = null;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        static string F(float v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }

        static string Metrics(List<Shot> shots, List<KeyValuePair<string, int>> scenes)
        {
            var sb = new StringBuilder("{\n  \"generatedUtc\": ").Append(QaJson.Quote(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))).Append(",\n  \"scenes\": [");
            for (int i = 0; i < scenes.Count; i++)
                sb.Append(i == 0 ? "\n" : ",\n").Append("    { \"scene\": ").Append(QaJson.Quote(scenes[i].Key)).Append(", \"views\": ").Append(scenes[i].Value).Append(" }");
            sb.Append(scenes.Count > 0 ? "\n  ],\n  \"shots\": [" : "],\n  \"shots\": [");
            for (int i = 0; i < shots.Count; i++)
            {
                var s = shots[i];
                sb.Append(i == 0 ? "\n" : ",\n").Append("    { \"scene\": ").Append(QaJson.Quote(s.Scene)).Append(", \"view\": ").Append(QaJson.Quote(s.View))
                  .Append(", \"file\": ").Append(QaJson.Quote(s.File)).Append(", \"mean\": ").Append(F(s.Stats.MeanLuminance))
                  .Append(", \"clipped\": ").Append(F(s.Stats.ClippedFraction)).Append(", \"crushed\": ").Append(F(s.Stats.CrushedFraction))
                  .Append(", \"saturation\": ").Append(F(s.Stats.MeanSaturation)).Append(", \"contrast\": ").Append(F(s.Stats.LuminanceStdDev)).Append(", \"flags\": [");
                for (int k = 0; k < s.Flags.Count; k++) sb.Append(k == 0 ? "" : ", ").Append(QaJson.Quote(s.Flags[k].Id));
                sb.Append("] }");
            }
            sb.Append(shots.Count > 0 ? "\n  ]\n}\n" : "]\n}\n");
            return sb.ToString();
        }

        /// <summary>"mean 0.123, clipped 1.2%, crushed 0.0%, sat 0.31, contrast 0.18" for a metrics.json shot entry.</summary>
        public static string Describe(QaJsonNode shot)
        {
            return "mean " + shot.Num("mean", 0).ToString("0.000", CultureInfo.InvariantCulture) + ", clipped " + (shot.Num("clipped", 0) * 100).ToString("0.0", CultureInfo.InvariantCulture) +
                   "%, crushed " + (shot.Num("crushed", 0) * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%, sat " + shot.Num("saturation", 0).ToString("0.00", CultureInfo.InvariantCulture) +
                   ", contrast " + shot.Num("contrast", 0).ToString("0.00", CultureInfo.InvariantCulture);
        }

        static string Review(List<Shot> shots, List<KeyValuePair<string, int>> scenes)
        {
            var sb = new StringBuilder("# Visual review sheet — Georgian Knight\n\n");
            sb.Append("Generated ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" UTC by `qa.shots`. ")
              .Append("Exposure flags are automatic; the rubric scores (1–5) are filled in by the reviewer (human or AI critic, see Docs/QA.md §Visual review).\n\n");
            sb.Append("## Rubric\n\n");
            for (int i = 0; i < Rubric.Length; i++) sb.Append(i + 1).Append(". ").Append(Rubric[i]).Append('\n');
            foreach (var scene in scenes)
            {
                sb.Append("\n## ").Append(Path.GetFileNameWithoutExtension(scene.Key)).Append("\n\n");
                if (scene.Value == 0) { sb.Append("_No views: add QAView_* markers or spawn points (VIS.NO_VIEWS)._\n"); continue; }
                sb.Append("| Shot | Mean | Clipped | Crushed | Sat. | Contrast | Flags | S | M | Sc | T | L | C | H | UI | Notes |\n");
                sb.Append("|---|---:|---:|---:|---:|---:|---|---|---|---|---|---|---|---|---|---|\n");
                foreach (var s in shots)
                {
                    if (s.Scene != scene.Key) continue;
                    var flags = new List<string>();
                    foreach (var f in s.Flags) flags.Add(f.Id);
                    sb.Append("| ![").Append(s.View).Append("](").Append(s.File).Append(") `").Append(s.View).Append("` | ").Append(F(s.Stats.MeanLuminance))
                      .Append(" | ").Append((s.Stats.ClippedFraction * 100f).ToString("0.0", CultureInfo.InvariantCulture)).Append("% | ")
                      .Append((s.Stats.CrushedFraction * 100f).ToString("0.0", CultureInfo.InvariantCulture)).Append("% | ")
                      .Append(s.Stats.MeanSaturation.ToString("0.00", CultureInfo.InvariantCulture)).Append(" | ").Append(s.Stats.LuminanceStdDev.ToString("0.00", CultureInfo.InvariantCulture))
                      .Append(" | ").Append(flags.Count == 0 ? "—" : string.Join(", ", flags.ToArray())).Append(" |  |  |  |  |  |  |  |  |  |\n");
                }
            }
            return sb.ToString();
        }
    }
}
