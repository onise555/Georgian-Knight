using System.Reflection;
using GeorgianKnight.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace GeorgianKnight
{
    /// <summary>
    /// Applies the scene-level part of <see cref="GameSettings"/>: URP render scale and SSAO, post effects on global
    /// volumes (runtime profile copies — assets are never modified), brightness, field of view, grass density,
    /// mouse / gamepad sensitivity and audio category volumes. Re-applies on every settings change and scene load.
    /// </summary>
    public sealed class SettingsApplier : MonoBehaviour
    {
        GameSettings s;
        Subscriptions subs;
        float originalRenderScale = -1f;
        UniversalRenderPipelineAsset touchedAsset;

        void Awake()
        {
            ISettingsService svc;
            if (Services.TryGet(out svc)) s = svc.Current;
            subs = new Subscriptions()
                .On<SettingsChanged>(e => { s = e.Settings; ApplyAll(); })
                .On<PlayerSpawned>(e => ApplyAll());
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void OnDestroy()
        {
            if (subs != null) subs.Dispose();
            SceneManager.sceneLoaded -= OnSceneLoaded;
#if UNITY_EDITOR
            // never leave the editor's URP asset modified by a play session
            if (touchedAsset != null && originalRenderScale > 0f) touchedAsset.renderScale = originalRenderScale;
            SetSsao(true);
#endif
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode) { ApplyAll(); }

        void Start() { ApplyAll(); }

        public void ApplyAll()
        {
            if (s == null) return;
            AudioMix.Apply(s);
            ApplyPipeline();
            ApplyVolumes();
            ApplyScene();
        }

        void ApplyPipeline()
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset == null) return;
            if (touchedAsset != asset) { touchedAsset = asset; originalRenderScale = asset.renderScale; }
            asset.renderScale = Mathf.Clamp(s.renderScale, 0.5f, 1f);
            SetSsao(s.ambientOcclusion && s.quality >= QualityPreset.Medium);
        }

        void SetSsao(bool on)
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset == null) return;
            var f = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance);
            var list = f != null ? f.GetValue(asset) as ScriptableRendererData[] : null;
            if (list == null) return;
            foreach (var rd in list)
            {
                if (rd == null) continue;
                foreach (var feature in rd.rendererFeatures)
                    if (feature != null && feature.GetType().Name == "ScreenSpaceAmbientOcclusion" && feature.isActive != on) feature.SetActive(on);
            }
        }

        void ApplyVolumes()
        {
            foreach (var v in FindObjectsByType<Volume>(FindObjectsSortMode.None))
            {
                if (!v.isGlobal || v.sharedProfile == null) continue;
                var p = v.profile;   // runtime instance
                Bloom bloom;
                if (p.TryGet(out bloom)) { bloom.active = s.bloom; if (s.reduceFlashes) bloom.intensity.Override(Mathf.Min(bloom.intensity.value, 0.35f)); }
                FilmGrain grain;
                if (p.TryGet(out grain)) grain.active = s.filmGrain;
                MotionBlur mb;
                if (!p.TryGet(out mb) && s.motionBlur) { mb = p.Add<MotionBlur>(true); mb.intensity.Override(0.22f); mb.quality.Override(MotionBlurQuality.Medium); }
                if (mb != null) mb.active = s.motionBlur;
                DepthOfField dof;
                if (p.TryGet(out dof) && !s.depthOfField) dof.active = false;
                ColorAdjustments ca;
                if (!p.TryGet(out ca)) ca = p.Add<ColorAdjustments>(false);
                var bright = v.GetComponent<VolumeBrightnessBase>();
                if (bright == null) { bright = v.gameObject.AddComponent<VolumeBrightnessBase>(); bright.baseExposure = ca.postExposure.overrideState ? ca.postExposure.value : 0f; }
                ca.postExposure.Override(bright.baseExposure + Mathf.Log(Mathf.Max(0.05f, s.brightness), 2f));
            }
        }

        void ApplyScene()
        {
            foreach (var cam in FindObjectsByType<PlayerCamera>(FindObjectsSortMode.None)) cam.baseFov = s.fieldOfView;
            foreach (var input in FindObjectsByType<GKInput>(FindObjectsSortMode.None))
            {
                input.mouseSensitivity = GKInput.DefaultMouseSensitivity * s.mouseSensitivity;
                input.stickSensitivity = GKInput.DefaultStickSensitivity * s.gamepadSensitivity;
                input.invertY = s.invertY;
            }
            foreach (var t in Terrain.activeTerrains)
            {
                if (t == null) continue;
                t.detailObjectDensity = Mathf.Clamp01(s.grassDensity);
            }
        }
    }

}
