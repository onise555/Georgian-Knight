using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// The arena's lighting pass, one look at a time without rebuilding the level: `world_light key=value ...` sets the
    /// sun, ambient, reflections, sky, fog and grade of the open Level_Didgori_Arena and saves it; `world_shots` then shows
    /// the result. Values that hold go into the arena's GKEnvironment.Site so a rebuild keeps them.
    /// Keys: sun (intensity) temp (K) az el (degrees) shadow (strength 0-1) amb refl (intensity) skyexp skytint (0-1 grey)
    /// fog (r,g,b) fogstart fogend exp contrast sat (post). `world_light` alone logs the current values.
    /// </summary>
    public static class GKArenaLight
    {
        [GKCommand("world_light", "world_light [key=value ...]: sun temp az el shadow amb refl skyexp skytint fog fogstart fogend exp contrast sat fill fillel shadowlift shadowrange disk[int|edge|fall] halo[int|edge|fall] on the open arena; saves it")]
        static void Command(string a)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("world_light: refused in Play mode"); return; }
            var scene = SceneManager.GetActiveScene();
            if (scene.path != GKArenaGreybox.ScenePath) { GKBridge.Log("world_light: open " + GKArenaGreybox.ScenePath + " first (open scene: " + scene.path + ")"); return; }
            var sunGo = GameObject.Find("Environment/Sun");
            var sun = sunGo != null ? sunGo.GetComponent<Light>() : null;
            var sky = RenderSettings.skybox;
            var volGo = GameObject.Find("Environment/Post Volume");
            var profile = volGo != null ? volGo.GetComponent<Volume>().sharedProfile : null;
            ColorAdjustments ca = null;
            if (profile != null) profile.TryGet(out ca);
            if (sun == null || sky == null || ca == null) { GKBridge.Log("world_light: sun, sky or post profile missing in the scene"); return; }

            var ci = CultureInfo.InvariantCulture;
            float az = Mathf.Repeat(Mathf.Atan2(-sun.transform.forward.x, -sun.transform.forward.z) * Mathf.Rad2Deg, 360f);
            float el = Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            bool changed = false;
            foreach (var part in (a ?? "").Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq < 1) { GKBridge.Log("world_light: not key=value: " + part); return; }
                string k = part.Substring(0, eq).ToLowerInvariant(), v = part.Substring(eq + 1);
                float f;
                if (k == "fog")
                {
                    var c = v.Split(',');
                    if (c.Length != 3) { GKBridge.Log("world_light: fog=r,g,b"); return; }
                    RenderSettings.fogColor = new Color(float.Parse(c[0], ci), float.Parse(c[1], ci), float.Parse(c[2], ci));
                    changed = true; continue;
                }
                if (k == "fillel")   // the Sky Fill's elevation in degrees above the anti-sun horizon (the Lead, 2026-10-31: the sweep)
                {
                    var fg = GameObject.Find("Environment/Sky Fill");
                    if (fg == null) { GKBridge.Log("world_light: no Sky Fill (set fill= first)"); return; }
                    float e = float.Parse(v, ci) * Mathf.Deg2Rad;
                    var sunDir = -sun.transform.forward;
                    var flat = new Vector3(-sunDir.x, 0f, -sunDir.z).normalized;
                    var dir = flat * Mathf.Cos(e) + Vector3.up * Mathf.Sin(e);
                    fg.transform.rotation = Quaternion.LookRotation(-dir, Mathf.Abs(dir.y) > 0.99f ? Vector3.forward : Vector3.up);
                    changed = true; continue;
                }
                if (k == "fill")   // the Sky Fill light's intensity (Site.SkyFill); made if missing
                {
                    var fg = GameObject.Find("Environment/Sky Fill");
                    Light fl = fg != null ? fg.GetComponent<Light>() : null;
                    if (fl == null)
                    {
                        GKEnvironment.Root = GameObject.Find("Environment");
                        GKEnvironment.AddSkyFill(-sun.transform.forward);
                        fg = GameObject.Find("Environment/Sky Fill"); fl = fg != null ? fg.GetComponent<Light>() : null;
                    }
                    if (fl == null) { GKBridge.Log("world_light: no Sky Fill"); return; }
                    fl.intensity = float.Parse(v, ci);
                    changed = true; continue;
                }
                if (k == "shadowrange")   // Shadows/Midtones/Highlights shadows start,end (the Critics, 2026-10-31: a lift in the toe only)
                {
                    var c = v.Split(',');
                    if (c.Length != 2 || !profile.TryGet(out ShadowsMidtonesHighlights smr)) { GKBridge.Log("world_light: shadowrange=start,end (needs Shadows/Midtones/Highlights)"); return; }
                    smr.shadowsStart.Override(float.Parse(c[0], ci)); smr.shadowsEnd.Override(float.Parse(c[1], ci));
                    EditorUtility.SetDirty(profile);
                    changed = true; continue;
                }
                if (k == "shadowlift")   // the Shadows/Midtones/Highlights shadows' offset (Site.ShadowLift)
                {
                    if (!profile.TryGet(out ShadowsMidtonesHighlights smh)) { GKBridge.Log("world_light: no Shadows/Midtones/Highlights in the profile"); return; }
                    var s = smh.shadows.value; s.w = float.Parse(v, ci); smh.shadows.Override(s);
                    EditorUtility.SetDirty(profile);
                    changed = true; continue;
                }
                if (k == "awaytint")
                {
                    var c = v.Split(',');
                    if (c.Length != 3 || !sky.HasProperty("_AwayTint")) { GKBridge.Log("world_light: awaytint=r,g,b on GK/SkyPanoramicGraded"); return; }
                    sky.SetColor("_AwayTint", new Color(float.Parse(c[0], ci), float.Parse(c[1], ci), float.Parse(c[2], ci)));
                    EditorUtility.SetDirty(sky);
                    changed = true; continue;
                }
                if (!float.TryParse(v, NumberStyles.Float, ci, out f)) { GKBridge.Log("world_light: not a number: " + part); return; }
                switch (k)
                {
                    case "sun": sun.intensity = f; break;
                    case "temp": sun.useColorTemperature = true; sun.colorTemperature = f; break;
                    case "az": az = f; break;
                    case "el": el = f; break;
                    case "shadow": sun.shadowStrength = f; break;
                    case "amb": RenderSettings.ambientIntensity = f; break;
                    case "skyvisdebug": Shader.SetGlobalFloat("_GK_SkyVisDebug", f); break;   // 1: GK terrain shows its sky visibility
                    case "refl": RenderSettings.reflectionIntensity = f; break;
                    case "skyexp": sky.SetFloat("_Exposure", f); EditorUtility.SetDirty(sky); break;
                    case "skytint": sky.SetColor("_Tint", new Color(f, f, f, 0.5f)); EditorUtility.SetDirty(sky); break;
                    case "fogstart": RenderSettings.fogStartDistance = f; break;
                    case "fogend": RenderSettings.fogEndDistance = f; break;
                    case "exp": ca.postExposure.Override(f); EditorUtility.SetDirty(profile); break;
                    case "contrast": ca.contrast.Override(f); EditorUtility.SetDirty(profile); break;
                    case "sat": ca.saturation.Override(f); EditorUtility.SetDirty(profile); break;
                    case "skyaway": case "skypow":
                        if (!sky.HasProperty("_AwayGain")) { GKBridge.Log("world_light: the sky is not GK/SkyPanoramicGraded (Site.SkyAwayGain)"); return; }
                        sky.SetFloat(k == "skyaway" ? "_AwayGain" : "_GainPower", f); EditorUtility.SetDirty(sky);
                        break;
                    case "disk": case "diskint": case "diskedge": case "diskfall": case "halo": case "haloint": case "haloedge": case "halofall":
                    {
                        var lf = sun.GetComponent<UnityEngine.Rendering.LensFlareComponentSRP>();
                        if (lf == null || lf.lensFlareData == null || lf.lensFlareData.elements.Length < 2) { GKBridge.Log("world_light: no sun disk on the Sun (Site.SunDisk)"); return; }
                        var e = lf.lensFlareData.elements[k.StartsWith("disk") ? 0 : 1];
                        if (k.EndsWith("int")) e.localIntensity = f;
                        else if (k.EndsWith("edge")) e.edgeOffset = f;
                        else if (k.EndsWith("fall")) e.fallOff = f;
                        else e.uniformScale = f;
                        EditorUtility.SetDirty(lf.lensFlareData);
                        break;
                    }
                    default: GKBridge.Log("world_light: unknown key " + k); return;
                }
                changed = true;
            }
            sun.transform.rotation = Quaternion.LookRotation(-(Quaternion.Euler(-el, az, 0f) * Vector3.forward));
            if (changed)
            {
                DynamicGI.UpdateEnvironment();   // ambient from the sky after a sky change
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
            }
            var sb = new StringBuilder("world_light" + (changed ? " (saved): " : ": "));
            sb.AppendFormat(ci, "sun={0:0.##} temp={1:0} az={2:0.#} el={3:0.#} shadow={4:0.##} amb={5:0.##} refl={6:0.##} skyexp={7:0.##} skytint={8:0.##} ",
                sun.intensity, sun.colorTemperature, az, el, sun.shadowStrength, RenderSettings.ambientIntensity, RenderSettings.reflectionIntensity,
                sky.GetFloat("_Exposure"), sky.GetColor("_Tint").r);
            sb.AppendFormat(ci, "fog={0:0.##},{1:0.##},{2:0.##} fogstart={3:0} fogend={4:0} exp={5:0.##} contrast={6:0.#} sat={7:0.#}",
                RenderSettings.fogColor.r, RenderSettings.fogColor.g, RenderSettings.fogColor.b, RenderSettings.fogStartDistance, RenderSettings.fogEndDistance,
                ca.postExposure.value, ca.contrast.value, ca.saturation.value);
            GKBridge.Log(sb.ToString());
        }
    }
}
