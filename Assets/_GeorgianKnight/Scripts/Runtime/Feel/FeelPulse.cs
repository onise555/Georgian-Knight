using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GeorgianKnight.Feel
{
    /// <summary>
    /// A short post-processing accent on a heavy blow, a guard break or a parry: chromatic aberration, a vignette
    /// squeeze and an exposure pop. It ADDS to the scene's own grade: the scene's values are sampled while the pulse is
    /// idle and the pulse's volume (global, priority 45, below the critical-kill pulse) writes scene + accent, so a
    /// vignetted scene is never lightened by a hit. "Reduce flashes" drops the exposure pop and halves the rest.
    /// </summary>
    public sealed class FeelPulse
    {
        struct Accent { public float chromatic, vignette, exposure, left, length; public Color color; }

        const int Max = 6;
        readonly Accent[] running = new Accent[Max];
        int count;

        public const int MaxPopsPerSecond = 3;
        // ring of the last pops' times; the oldest is the one a new pop must be a second clear of
        readonly float[] pops = { -10f, -10f, -10f };
        int popIndex;

        readonly Volume volume;
        readonly VolumeProfile profile;
        readonly ChromaticAberration ca;
        readonly Vignette vg;
        readonly ColorAdjustments adj;
        float baseCa, baseVg, baseExp;
        Color baseVgColor = Color.black;

        public bool Calm;
        public float Comfort = 1f;
        /// <summary>The accent applied this frame (tests).</summary>
        public float Chromatic { get; private set; }
        public float VignetteAdd { get; private set; }
        public float Exposure { get; private set; }

        public FeelPulse(Transform owner)
        {
            var go = new GameObject("FeelPulse");
            go.transform.SetParent(owner, false);
            volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 45f;
            volume.weight = 1f;
            volume.enabled = false;
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.hideFlags = HideFlags.DontSave;
            ca = profile.Add<ChromaticAberration>(true);
            vg = profile.Add<Vignette>(true);
            vg.smoothness.overrideState = false; vg.center.overrideState = false; vg.rounded.overrideState = false;
            adj = profile.Add<ColorAdjustments>(true);
            foreach (var p in adj.parameters) p.overrideState = false;
            adj.postExposure.overrideState = true;
            volume.sharedProfile = profile;
        }

        public void Dispose() { if (profile != null) Object.Destroy(profile); }

        public void Play(ImpactProfile p, float scale)
        {
            if (p.pulseSeconds <= 0f || scale <= 0f) return;
            float s = scale * Comfort * (Calm ? 0.5f : 1f);
            var a = new Accent
            {
                chromatic = p.chromatic * s, vignette = p.vignette * s, exposure = Calm ? 0f : p.exposure * scale,
                color = p.vignetteColor, left = p.pulseSeconds, length = p.pulseSeconds
            };
            // photosensitivity (QUALITY.md): at most MaxPopsPerSecond full-screen exposure pops in any rolling second
            if (a.exposure > 0f)
            {
                float now = Time.unscaledTime;
                if (now - pops[popIndex] < 1f) a.exposure = 0f;
                else { pops[popIndex] = now; popIndex = (popIndex + 1) % MaxPopsPerSecond; }
            }
            if (a.chromatic <= 0f && a.vignette <= 0f && a.exposure <= 0f) return;
            if (count < Max) running[count++] = a;
            else running[0] = a;
        }

        public void Tick(float dt)
        {
            if (count == 0)
            {
                if (volume.enabled) volume.enabled = false;
                SampleScene();
                Chromatic = VignetteAdd = Exposure = 0f;
                return;
            }
            float c = 0f, v = 0f, e = 0f, strongest = 0f;
            Color col = baseVgColor;
            for (int i = count - 1; i >= 0; i--)
            {
                running[i].left -= dt;
                if (running[i].left <= 0f) { running[i] = running[--count]; continue; }
                float k = running[i].left / running[i].length;
                // snap in, ease out (an impact, not a breath)
                float env = k * k * (3f - 2f * k);
                c = Mathf.Max(c, running[i].chromatic * env);
                e = Mathf.Max(e, running[i].exposure * env);
                float vi = running[i].vignette * env;
                if (vi > v) { v = vi; }
                if (running[i].vignette > strongest) { strongest = running[i].vignette; col = Color.Lerp(baseVgColor, running[i].color, env); }
            }
            Chromatic = c; VignetteAdd = v; Exposure = e;
            ca.intensity.Override(Mathf.Clamp01(baseCa + c));
            vg.intensity.Override(Mathf.Clamp01(baseVg + v));
            vg.color.Override(col);
            adj.postExposure.Override(baseExp + e);
            if (!volume.enabled) volume.enabled = true;
        }

        void SampleScene()
        {
            var stack = VolumeManager.instance.stack;
            if (stack == null) return;
            var sca = stack.GetComponent<ChromaticAberration>();
            var svg = stack.GetComponent<Vignette>();
            var sadj = stack.GetComponent<ColorAdjustments>();
            if (sca != null) baseCa = sca.intensity.value;
            if (svg != null) { baseVg = svg.intensity.value; baseVgColor = svg.color.value; }
            if (sadj != null) baseExp = sadj.postExposure.value;
        }
    }
}
