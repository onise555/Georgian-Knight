using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GeorgianKnight.EditorTools
{
    /// <summary>gfxstate: everything that decides how the frame looks right now (quality level, URP asset, game view
    /// zoom, cameras and their anti-aliasing, active post effects with their values, sun, ambient, fog).</summary>
    public static class GKGfxDiag
    {
        /// <summary>gfxtune key=value ...: live look-dev on the running game (sun, ambient, reflections, exposure,
        /// contrast, sat, bloom, bthresh, bscatter, grain, vig, temp, rim, fill). Runtime only; nothing is saved.</summary>
        [GKCommand("gfxtune", "gfxtune sun=2.8 ambient=0.85 exposure=0 contrast=12 sat=6 bloom=0.22 bthresh=1.25 bscatter=0.5 grain=0 vig=0.16 temp=5500 rim=1.6 fill=0.5 (play mode)")]
        static void Tune(string arg)
        {
            var kv = new System.Collections.Generic.Dictionary<string, float>();
            foreach (var part in (arg ?? "").Split(' '))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                float f;
                if (float.TryParse(part.Substring(eq + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) kv[part.Substring(0, eq)] = f;
            }
            float x;
            var sun = RenderSettings.sun;
            if (sun != null && kv.TryGetValue("sun", out x)) sun.intensity = x;
            if (sun != null && kv.TryGetValue("temp", out x)) { sun.useColorTemperature = true; sun.colorTemperature = x; }
            if (kv.TryGetValue("ambient", out x)) RenderSettings.ambientIntensity = x;
            if (kv.TryGetValue("reflections", out x)) RenderSettings.reflectionIntensity = x;
            foreach (var hl in Object.FindObjectsByType<HeroLight>(FindObjectsSortMode.None))
            {
                if (kv.TryGetValue("rim", out x)) hl.rimIntensity = x;
                if (kv.TryGetValue("fill", out x)) hl.fillIntensity = x;
            }
            foreach (var v in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
            {
                if (!v.isGlobal || v.sharedProfile == null) continue;
                var p = v.profile;
                ColorAdjustments ca; Bloom bl; FilmGrain fg; Vignette vg;
                if (p.TryGet(out ca))
                {
                    if (kv.TryGetValue("exposure", out x)) ca.postExposure.Override(x);
                    if (kv.TryGetValue("contrast", out x)) ca.contrast.Override(x);
                    if (kv.TryGetValue("sat", out x)) ca.saturation.Override(x);
                }
                if (p.TryGet(out bl))
                {
                    if (kv.TryGetValue("bloom", out x)) bl.intensity.Override(x);
                    if (kv.TryGetValue("bthresh", out x)) bl.threshold.Override(x);
                    if (kv.TryGetValue("bscatter", out x)) bl.scatter.Override(x);
                }
                if (p.TryGet(out fg) && kv.TryGetValue("grain", out x)) { fg.intensity.Override(x); fg.active = x > 0f; }
                if (p.TryGet(out vg) && kv.TryGetValue("vig", out x)) vg.intensity.Override(x);
            }
            GKBridge.Log("gfxtune: " + arg);
        }

        /// <summary>gameview [zoom]: the Game view at 1:1 pixels (a zoomed Game view magnifies the frame and blurs it).</summary>
        [GKCommand("gameview", "gameview [zoom]: set the Game view zoom (default 1 = one game pixel per screen pixel)")]
        static void GameViewZoom(string arg)
        {
            float z = 1f;
            if (!string.IsNullOrEmpty(arg)) float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z);
            var gvType = typeof(Editor).Assembly.GetType("UnityEditor.GameView");
            if (gvType == null) { GKBridge.Log("gameview: no GameView type"); return; }
            foreach (var w in Resources.FindObjectsOfTypeAll(gvType))
            {
                var snap = gvType.GetMethod("SnapZoom", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(float) }, null);
                if (snap != null) snap.Invoke(w, new object[] { z });
                else GKBridge.Log("gameview: SnapZoom not found");
                ((EditorWindow)w).Repaint();
                var zaF = gvType.GetField("m_ZoomArea", BindingFlags.NonPublic | BindingFlags.Instance);
                var za = zaF != null ? zaF.GetValue(w) : null;
                var scP = za != null ? za.GetType().GetProperty("scale") : null;
                GKBridge.Log("gameview: zoom now " + (scP != null ? scP.GetValue(za).ToString() : "?"));
            }
        }

        [GKCommand("gfxstate", "gfxstate: quality, URP asset, game view zoom, camera AA, post effects, sun / ambient (edit or play mode)")]
        static void Run(string arg)
        {
            var sb = new StringBuilder("gfxstate:");
            sb.Append(" quality '" + QualitySettings.names[QualitySettings.GetQualityLevel()] + "' skin " + QualitySettings.skinWeights +
                      " aniso " + QualitySettings.anisotropicFiltering + " mipLimit " + QualitySettings.globalTextureMipmapLimit + " vsync " + QualitySettings.vSyncCount);
            var a = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (a != null)
                sb.Append("\n  URP " + a.name + ": scale " + a.renderScale + " msaa " + a.msaaSampleCount + " hdr " + a.supportsHDR + " upscale " + a.upscalingFilter +
                          " shadows " + a.shadowDistance + " m x" + a.shadowCascadeCount + " @" + a.mainLightShadowmapResolution + " soft " + a.supportsSoftShadows);
            sb.Append("\n  screen " + Screen.width + "x" + Screen.height + " pixelsPerPoint " + EditorGUIUtility.pixelsPerPoint.ToString("F2"));
            var gvType = typeof(Editor).Assembly.GetType("UnityEditor.GameView");
            if (gvType != null)
                foreach (var w in Resources.FindObjectsOfTypeAll(gvType))
                {
                    var zaF = gvType.GetField("m_ZoomArea", BindingFlags.NonPublic | BindingFlags.Instance);
                    var za = zaF != null ? zaF.GetValue(w) : null;
                    var scP = za != null ? za.GetType().GetProperty("scale") : null;
                    var tsP = gvType.GetProperty("targetSize", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    var lrP = gvType.GetProperty("lowResolutionForAspectRatios", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    sb.Append("\n  game view zoom " + (scP != null ? scP.GetValue(za).ToString() : "?") + " render target " + (tsP != null ? tsP.GetValue(w).ToString() : "?") +
                              " lowRes " + (lrP != null ? lrP.GetValue(w).ToString() : "?"));
                }
            foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                if (!cam.isActiveAndEnabled) continue;
                var cd = cam.GetUniversalAdditionalCameraData();
                sb.Append("\n  camera " + cam.name + ": aa " + cd.antialiasing + "/" + cd.antialiasingQuality + " post " + cd.renderPostProcessing + " hdr " + cam.allowHDR +
                          " msaa " + cam.allowMSAA + " fov " + cam.fieldOfView.ToString("F1") + " near " + cam.nearClipPlane.ToString("F3") + " stopNaN " + cd.stopNaN + " dither " + cd.dithering);
            }
            foreach (var v in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
            {
                var p = v.HasInstantiatedProfile() ? v.profile : v.sharedProfile;
                if (p == null) continue;
                sb.Append("\n  volume " + v.name + (v.isGlobal ? " (global" : " (local") + ", weight " + v.weight + ", priority " + v.priority + "):");
                foreach (var c in p.components)
                {
                    if (!c.active) continue;
                    sb.Append(" " + c.GetType().Name + "{");
                    foreach (var f in c.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!typeof(VolumeParameter).IsAssignableFrom(f.FieldType)) continue;
                        var vp = (VolumeParameter)f.GetValue(c);
                        if (vp == null || !vp.overrideState) continue;
                        var valP = f.FieldType.GetProperty("value");
                        object val = valP != null ? valP.GetValue(vp) : null;
                        string vs = val is float ? ((float)val).ToString("0.###") : val != null ? val.ToString() : "null";
                        sb.Append(f.Name + "=" + vs + " ");
                    }
                    sb.Append("}");
                }
            }
            var sun = RenderSettings.sun;
            if (sun != null)
                sb.Append("\n  sun " + sun.name + ": intensity " + sun.intensity + " temp " + (sun.useColorTemperature ? sun.colorTemperature.ToString("F0") : "off") + " colour " + sun.color +
                          " shadows " + sun.shadows + " strength " + sun.shadowStrength + " elevation " + (Mathf.Asin(-sun.transform.forward.y) * Mathf.Rad2Deg).ToString("F1"));
            sb.Append("\n  ambient " + RenderSettings.ambientMode + " x" + RenderSettings.ambientIntensity + " reflections " + RenderSettings.defaultReflectionMode + " x" + RenderSettings.reflectionIntensity +
                      " fog " + RenderSettings.fog + " " + RenderSettings.fogMode + " " + RenderSettings.fogStartDistance + ".." + RenderSettings.fogEndDistance +
                      " skybox " + (RenderSettings.skybox != null ? RenderSettings.skybox.name : "none"));
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l != sun && l.isActiveAndEnabled) sb.Append("\n  light " + l.name + " " + l.type + " intensity " + l.intensity + " range " + l.range + " shadows " + l.shadows);
            GKBridge.Log(sb.ToString());
        }
    }
}
