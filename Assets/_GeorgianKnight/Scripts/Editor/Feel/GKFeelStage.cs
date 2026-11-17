using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Staging for the effects review (QUALITY.md 3b): a review camera at a set distance from the fight, and the arena's
    /// dawn sun or shade on the sandbox. Everything is runtime-only (Play mode) and undone by `off` / `restore`; no scene
    /// is changed or saved.
    /// </summary>
    public static class GKFeelStage
    {
        // ------------------------------------------------------------------ camera

        [GKCommand("feel_cam", "feel_cam <metres> [side|front|back] | off: a review camera that distance from the clash (player and nearest enemy), drawn over the game camera (play mode)")]
        static void Cam(string arg)
        {
            if (!EditorApplication.isPlaying) { GKBridge.Log("feel_cam: play mode only"); return; }
            var parts = (arg ?? "").Trim().ToLowerInvariant().Split(' ');
            var rig = Object.FindAnyObjectByType<FeelReviewCam>();
            if (parts[0] == "off" || parts[0] == "")
            {
                if (rig != null) Object.Destroy(rig.gameObject);
                GKBridge.Log("feel_cam: off");
                return;
            }
            float d;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { GKBridge.Log("feel_cam: give a distance in metres"); return; }
            if (rig == null)
            {
                var go = new GameObject("[FeelReviewCam]");

                rig = go.AddComponent<FeelReviewCam>();
            }
            rig.distance = Mathf.Clamp(d, 0.5f, 60f);
            rig.view = parts.Length > 1 ? parts[1] : "side";
            GKBridge.Log("feel_cam: " + rig.distance.ToString("F1", CultureInfo.InvariantCulture) + " m, " + rig.view);
        }

        // ------------------------------------------------------------------ the same blow, again and again

        [GKCommand("feel_rig", "feel_rig light|heavy|block [interval=2.5] [distance=1.5] | off: the hero strikes the nearest enemy from fixed marks every interval, same seed each time, AI and controller paused (play mode)")]
        static void Rig(string arg)
        {
            if (!EditorApplication.isPlaying) { GKBridge.Log("feel_rig: play mode only"); return; }
            var parts = (arg ?? "").Trim().ToLowerInvariant().Split(' ');
            var rig = Object.FindAnyObjectByType<FeelBlowRig>();
            if (parts[0] == "off" || parts[0] == "")
            {
                if (rig != null) { GKBridge.Log("feel_rig: off after " + rig.Blows + " blows"); Object.Destroy(rig.gameObject); }
                else GKBridge.Log("feel_rig: not running");
                return;
            }
            FeelBlowRig.Blow b;
            switch (parts[0]) { case "light": b = FeelBlowRig.Blow.Light; break; case "heavy": b = FeelBlowRig.Blow.Heavy; break; case "block": b = FeelBlowRig.Blow.Block; break; default: GKBridge.Log("feel_rig: light, heavy, block or off"); return; }
            if (rig != null) Object.Destroy(rig.gameObject);
            var go = new GameObject("[FeelBlowRig]");
            rig = go.AddComponent<FeelBlowRig>();
            rig.blow = b;
            float v;
            if (parts.Length > 1 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) rig.interval = Mathf.Clamp(v, 1f, 10f);
            if (parts.Length > 2 && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) rig.distance = Mathf.Clamp(v, 0.8f, 3f);
            GKBridge.Log("feel_rig: " + b + " every " + rig.interval.ToString("F1", CultureInfo.InvariantCulture) + " s at " + rig.distance.ToString("F1", CultureInfo.InvariantCulture) + " m");
        }

        // ------------------------------------------------------------------ the blade blood stand

        static FeelBladeStand stand;

        [GKCommand("feel_blade", "feel_blade [distance, 0 = whole blade] | off: a copy of the hero's sword on a stand with its own camera, run through the blood-film programme (cuts, wet turn, tip, raised, lowered, time-lapse to dry, dry turn); record it with feel_record <name> 32 fixed60 (play mode)")]
        static void Blade(string arg)
        {
            if (!EditorApplication.isPlaying) { GKBridge.Log("feel_blade: play mode only"); return; }
            // keep our own reference to the running stand
            var old = stand;
            string a = (arg ?? "").Trim().ToLowerInvariant();
            if (a == "off") { if (old != null) Object.Destroy(old.gameObject); stand = null; GKBridge.Log("feel_blade: off"); return; }
            if (a == "status") { GKBridge.Log("feel_blade: " + (old != null ? old.Phase + " | " + old.Film : "not running" + (FeelBladeStand.LastError.Length > 0 ? " (" + FeelBladeStand.LastError + ")" : ""))); return; }
            if (old != null) Object.Destroy(old.gameObject);
            var d = CombatDirector.Existing;
            var hero = d != null ? d.Player : null;
            var fx = Object.FindAnyObjectByType<HitFX>();
            if (hero == null || hero.weapon == null || fx == null) { GKBridge.Log("feel_blade: needs the hero's sword and HitFX in the scene"); return; }
            var mat = Feel.FeelSettings.Active.bladeBloodMaterial;
            if (mat == null) { GKBridge.Log("feel_blade: Feel_Default has no bladeBloodMaterial (run feel_build)"); return; }
            var go = new GameObject("[FeelBladeStand]");
            stand = go.AddComponent<FeelBladeStand>();
            stand.source = hero.weapon; stand.filmMaterial = mat; stand.drop = fx.EmitFallingDrop;
            float v;
            if (float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) stand.camDistance = Mathf.Clamp(v, 0.4f, 3f);
            GKBridge.Log("feel_blade: stand up with " + hero.weapon.name + ", camera " + stand.camDistance.ToString("F1", CultureInfo.InvariantCulture) + " m");
        }

        // ------------------------------------------------------------------ the legacy HUD (captures only)

        [GKCommand("feel_hud", "feel_hud off|on: hide or show the legacy GameHUD (its full-screen red hurt flash spoils effect captures; UI P0 bug) for this Play session only (play mode)")]
        static void Hud(string arg)
        {
            if (!EditorApplication.isPlaying) { GKBridge.Log("feel_hud: play mode only"); return; }
            bool on = (arg ?? "").Trim().ToLowerInvariant() == "on";
            int n = 0;
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb == null || mb.GetType().Name != "GameHUD") continue;
                mb.gameObject.SetActive(on);
                n++;
            }
            GKBridge.Log("feel_hud: " + (on ? "shown" : "hidden") + " (" + n + " GameHUD)");
        }

        // ------------------------------------------------------------------ clean-up

        static readonly string[] ToolObjects = { "[FeelBladeStand]", "[FeelBladeCopy]", "[FeelBladeCam]", "[FeelReviewCam]", "[FeelBlowRig]", "[FeelGrabber]" };

        [GKCommand("feel_clean", "feel_clean: remove every Feel review object (stands, sword copies, review cameras, rigs, grabbers), also ones left over from Play mode")]
        static void Clean(string arg)
        {
            int n = 0;
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || EditorUtility.IsPersistent(go)) continue;      // never a prefab or asset
                if (System.Array.IndexOf(ToolObjects, go.name) < 0) continue;
                Object.DestroyImmediate(go);
                n++;
            }
            stand = null;
            GKBridge.Log("feel_clean: removed " + n + " review objects");
        }

        // ------------------------------------------------------------------ light

        static bool saved;
        static Quaternion sunRot;
        static float sunIntensity, sunTemp, ambient;
        static bool sunUsesTemp;
        static Color sunColor;

        [GKCommand("feel_light", "feel_light dawn|shade|restore: the arena's dawn sun on this scene (az 83, el 12, 2.4 at 3800 K, World's values), shade (direct sun off, sky light only), or the scene's own light back (play mode)")]
        static void LightCmd(string arg)
        {
            if (!EditorApplication.isPlaying) { GKBridge.Log("feel_light: play mode only"); return; }
            var sun = RenderSettings.sun;
            if (sun == null) { GKBridge.Log("feel_light: the scene has no sun (RenderSettings.sun)"); return; }
            if (!saved)
            {
                sunRot = sun.transform.rotation; sunIntensity = sun.intensity; sunTemp = sun.colorTemperature;
                sunUsesTemp = sun.useColorTemperature; sunColor = sun.color; ambient = RenderSettings.ambientIntensity;
                saved = true;
            }
            string mode = (arg ?? "").Trim().ToLowerInvariant();
            switch (mode)
            {
                case "dawn":
                    // World's arena dawn (Docs/World/Didgori_Arena_Brief.md: 06:15 LMT, az 83, el 12), aimed the way
                    // GKArenaLight aims it
                    sun.transform.rotation = Quaternion.LookRotation(-(Quaternion.Euler(-12f, 83f, 0f) * Vector3.forward));
                    sun.intensity = 2.4f; sun.useColorTemperature = true; sun.colorTemperature = 3800f; sun.color = Color.white;
                    RenderSettings.ambientIntensity = ambient;
                    break;
                case "shade":
                    sun.transform.rotation = Quaternion.LookRotation(-(Quaternion.Euler(-12f, 83f, 0f) * Vector3.forward));
                    sun.intensity = 0f;
                    break;
                case "restore":
                    sun.transform.rotation = sunRot; sun.intensity = sunIntensity; sun.useColorTemperature = sunUsesTemp;
                    sun.colorTemperature = sunTemp; sun.color = sunColor; RenderSettings.ambientIntensity = ambient;
                    saved = false;
                    break;
                default: GKBridge.Log("feel_light: dawn, shade or restore"); return;
            }
            GKBridge.Log("feel_light: " + mode + " (sun " + sun.intensity.ToString("F1", CultureInfo.InvariantCulture) + ")");
        }
    }
}
