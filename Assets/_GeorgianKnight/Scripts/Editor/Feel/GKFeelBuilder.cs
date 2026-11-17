using System.IO;
using GeorgianKnight.Feel;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Builds the Feel assets under VFX/: the hit flash material, the footstep dust texture / material / prefab, and the
    /// settings asset in VFX/Resources. Re-running keeps every value a designer has tuned: it only creates what is
    /// missing and fills empty references.
    /// </summary>
    public static class GKFeelBuilder
    {
        public const string Root = "Assets/_GeorgianKnight/VFX";
        public const string SettingsPath = Root + "/Resources/Feel_Default.asset";
        const string FlashMatPath = Root + "/Materials/M_HitFlash.mat";
        const string PuffTexPath = Root + "/Textures/T_DustPuff_BaseColor.png";
        const string DustMatPath = Root + "/Materials/M_FootDust.mat";
        const string DustPrefabPath = Root + "/Prefabs/VFX_FootDust.prefab";

        [GKCommand("feel_build", "feel_build [rebuild]: create / fill the Feel assets under VFX/ (rebuild = remake the dust prefab and materials, settings values kept)")]
        static void Build(string arg)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("feel_build: not in play mode"); return; }
            bool rebuild = arg != null && arg.Trim() == "rebuild";
            foreach (var d in new[] { "Resources", "Materials", "Textures", "Prefabs", "Shaders", "Audio" })
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), Root, d));
            AssetDatabase.Refresh();

            var flash = FlashMaterial(rebuild);
            var dust = DustPrefab(rebuild);

            var s = AssetDatabase.LoadAssetAtPath<FeelSettings>(SettingsPath);
            bool created = s == null;
            if (created)
            {
                s = ScriptableObject.CreateInstance<FeelSettings>();
                AssetDatabase.CreateAsset(s, SettingsPath);
            }
            if (s.flashMaterial == null || rebuild) s.flashMaterial = flash;
            if (s.footDustPrefab == null || rebuild) s.footDustPrefab = dust;
            var film = ShaderMaterial("GK/Feel/BladeBlood", Root + "/Materials/M_BladeBlood.mat", rebuild);
            if (s.bladeBloodMaterial == null || rebuild) s.bladeBloodMaterial = film;
            var trail = TrailMaterial(rebuild);
            if (s.trail.material == null || rebuild) s.trail.material = trail;
            GKFeelSounds.Assign(s);
            Migrate(s);
            EditorUtility.SetDirty(s);
            AssetDatabase.SaveAssets();
            GKBridge.Log("feel_build: " + (created ? "created " : "updated ") + SettingsPath + " flash=" + (flash != null) + " dust=" + (dust != null));
        }

        static Material FlashMaterial(bool rebuild)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(FlashMatPath);
            if (m != null && !rebuild) return m;
            var sh = Shader.Find(FeelFlash.ShaderName);
            if (sh == null) { GKBridge.Log("feel_build: shader " + FeelFlash.ShaderName + " not found (compiled?)"); return null; }
            if (m == null) { m = new Material(sh); AssetDatabase.CreateAsset(m, FlashMatPath); }
            else m.shader = sh;
            m.SetColor("_Color", new Color(1.6f, 1.45f, 1.3f, 1f));
            m.SetFloat("_Intensity", 0f);
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>Brings an older Feel_Default up to today's meaning without touching values a designer tuned since.</summary>
        static void Migrate(FeelSettings s)
        {
            if (s.dataVersion >= FeelSettings.CurrentDataVersion) return;
            if (s.dataVersion < 2)
            {
                // effects detail pass 2026-10-30: the kill accent only for a critical (HitFX's own red pulse is gone),
                // and the hit flash made a warm, 2-3 frame touch instead of a white glow (QUALITY 3b, restraint)
                s.kill.pulseOnlyOnCritical = true;
                var warm = new Color(1.25f, 1.12f, 1f);
                Flash(s.hit, warm, 0.35f, 0.07f); Flash(s.heavyHit, warm, 0.5f, 0.08f); Flash(s.kill, warm, 0.6f, 0.1f);
                Flash(s.guardBreak, warm, 0.3f, 0.07f); Flash(s.execution, warm, 0.6f, 0.12f);
            }
            if (s.dataVersion < 3)
            {
                // D24 (2026-10-30, Team Lead): the hit flash washed the whole struck body pale grey; off until the
                // Director has seen it side by side
                foreach (var p in new[] { s.hit, s.heavyHit, s.kill, s.block, s.guardBreak, s.parry, s.execution }) p.flashStrength = 0f;
            }
            s.dataVersion = FeelSettings.CurrentDataVersion;
            GKBridge.Log("feel_build: Feel_Default migrated to data version " + s.dataVersion);
        }

        static void Flash(ImpactProfile p, Color c, float strength, float seconds) { p.flashColor = c; p.flashStrength = strength; p.flashSeconds = seconds; }

        static Material ShaderMaterial(string shader, string path, bool rebuild)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null && !rebuild) return m;
            var sh = Shader.Find(shader);
            if (sh == null) { GKBridge.Log("feel_build: shader " + shader + " not found (compiled?)"); return null; }
            if (m == null) { m = new Material(sh); AssetDatabase.CreateAsset(m, path); }
            else m.shader = sh;
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material TrailMaterial(bool rebuild)
        {
            const string path = Root + "/Materials/M_BladeTrail.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null && !rebuild) return m;
            var sh = Shader.Find("GK/Feel/BladeTrail");
            if (sh == null) { GKBridge.Log("feel_build: shader GK/Feel/BladeTrail not found (compiled?)"); return null; }
            if (m == null) { m = new Material(sh); AssetDatabase.CreateAsset(m, path); }
            else m.shader = sh;
            EditorUtility.SetDirty(m);
            return m;
        }

        // ------------------------------------------------------------------ footstep dust

        static ParticleSystem DustPrefab(bool rebuild)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(DustPrefabPath);
            if (existing != null && !rebuild) return existing.GetComponent<ParticleSystem>();

            var mat = DustMaterial(rebuild);
            var go = new GameObject("VFX_FootDust");
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 1f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.7f, 1.3f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.25f, 0.8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.16f, 0.3f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new Color(0.62f, 0.55f, 0.45f, 0.35f);
            main.gravityModifier = -0.015f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 400;

            var em = ps.emission; em.enabled = false;

            // emitted along the ground normal (+Z): a wide, flat cone so the puff rolls out low around the boot
            var sh = ps.shape;
            sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 78f; sh.radius = 0.07f;

            // air drag: a quick spread that settles, not a fountain
            var lim = ps.limitVelocityOverLifetime;
            lim.enabled = true; lim.limit = 0.05f; lim.dampen = 0.12f; lim.drag = 1.5f;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.12f), new GradientAlphaKey(0.55f, 0.5f), new GradientAlphaKey(0f, 1f) });
            col.color = g;

            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.45f, 0f, 3f), new Keyframe(1f, 1.7f, 0f, 0f)));

            var rol = ps.rotationOverLifetime;
            rol.enabled = true; rol.z = new ParticleSystem.MinMaxCurve(-0.6f, 0.6f);

            // the scene's one wind (World's WindZone, from about 70 deg in the arena) carries the dust away with its gusts
            var wind = ps.externalForces;
            wind.enabled = true; wind.multiplier = 0.8f;

            var noise = ps.noise;
            noise.enabled = true; noise.strength = 0.08f; noise.frequency = 1.2f; noise.scrollSpeed = 0.4f; noise.quality = ParticleSystemNoiseQuality.Low;

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.alignment = ParticleSystemRenderSpace.View;
            r.minParticleSize = 0f;
            r.maxParticleSize = 0.5f;

            var prefab = PrefabUtility.SaveAsPrefabAsset(go, DustPrefabPath);
            Object.DestroyImmediate(go);
            return prefab.GetComponent<ParticleSystem>();
        }

        static Material DustMaterial(bool rebuild)
        {
            var tex = PuffTexture(rebuild);
            var m = AssetDatabase.LoadAssetAtPath<Material>(DustMatPath);
            if (m != null && !rebuild) return m;
            var sh = Shader.Find("Universal Render Pipeline/Particles/Simple Lit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (m == null) { m = new Material(sh); AssetDatabase.CreateAsset(m, DustMatPath); }
            else m.shader = sh;
            m.SetTexture("_BaseMap", tex);
            m.SetColor("_BaseColor", Color.white);
            // transparent, alpha blended, soft against the ground it rises from
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_SoftParticlesEnabled", 1f);
            m.SetVector("_SoftParticleFadeParams", new Vector4(0f, 0.25f, 0f, 0f));
            m.SetFloat("_SoftParticlesNearFadeDistance", 0f);
            m.SetFloat("_SoftParticlesFarFadeDistance", 0.25f);
            m.SetFloat("_CameraFadingEnabled", 1f);
            m.SetFloat("_CameraNearFadeDistance", 0.3f);
            m.SetFloat("_CameraFarFadeDistance", 1f);
            m.SetVector("_CameraFadeParams", new Vector4(0.3f, 1f / 0.7f, 0f, 0f));
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.EnableKeyword("_SOFTPARTICLES_ON");
            m.EnableKeyword("_FADING_ON");
            m.DisableKeyword("_ALPHATEST_ON");
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)RenderQueue.Transparent;
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>A soft, irregular puff (value noise over a radial falloff), white: the particle colour tints it.</summary>
        static Texture2D PuffTexture(bool rebuild)
        {
            var t = AssetDatabase.LoadAssetAtPath<Texture2D>(PuffTexPath);
            if (t != null && !rebuild) return t;
            const int N = 128;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float n = 0f, amp = 0.5f, f = 3f;
                    for (int o = 0; o < 4; o++) { n += amp * Mathf.PerlinNoise(x / (float)N * f + 17.3f * o, y / (float)N * f + 4.1f * o); amp *= 0.5f; f *= 2f; }
                    // a smoothstep falloff from 0.25 to 1 of the (noise-warped) radius
                    float k = Mathf.Clamp01((r + (n - 0.45f) * 0.6f - 0.25f) / 0.75f);
                    float edge = 1f - k * k * (3f - 2f * k);
                    float a = edge * Mathf.Lerp(0.55f, 1f, n);
                    px[y * N + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Application.dataPath), PuffTexPath), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(PuffTexPath);
            var imp = (TextureImporter)AssetImporter.GetAtPath(PuffTexPath);
            imp.alphaIsTransparency = true;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.mipmapEnabled = true;
            imp.sRGBTexture = true;
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(PuffTexPath);
        }
    }
}
