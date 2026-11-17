using System;
using System.IO;
using UnityEngine;
using UnityEngine.Audio;

namespace GeorgianKnight.Core
{
    public interface ISettingsService
    {
        GameSettings Current { get; }
        /// <summary>Applies and persists a modified copy (UI edits a clone, then commits).</summary>
        void Commit(GameSettings changed);
        void ResetToDefaults();
        void Apply();
        /// <summary>Fresh defaults for this machine (hardware-based quality, system language). Not committed.</summary>
        GameSettings CreateDefaults();
    }

    /// <summary>
    /// Loads / saves <see cref="GameSettings"/> and applies the engine-level part (resolution, vsync, frame cap,
    /// quality level, audio mixer). Scene-level options (post effects, FOV, sensitivity …) are applied by the systems
    /// that own them when they receive <see cref="SettingsChanged"/>.
    /// </summary>
    public sealed class SettingsService : ISettingsService
    {
        readonly string path;
        readonly AudioMixer mixer;

        public GameSettings Current { get; private set; }

        /// <summary>Exposed AudioMixer parameter names (dB).</summary>
        public const string MixMaster = "MasterVolume", MixMusic = "MusicVolume", MixSfx = "SfxVolume",
                            MixVoice = "VoiceVolume", MixAmbience = "AmbienceVolume", MixUi = "UiVolume";

        public SettingsService(AudioMixer mixer, string file = null)
        {
            this.mixer = mixer;
            path = file ?? Path.Combine(Application.persistentDataPath, "settings.json");
            Current = Load() ?? Defaults();
        }

        GameSettings Load()
        {
            try
            {
                if (!File.Exists(path)) return null;
                var s = JsonUtility.FromJson<GameSettings>(File.ReadAllText(path, System.Text.Encoding.UTF8));
                if (s == null) return null;
                s.Sanitize();
                return s;
            }
            catch (Exception e) { Debug.LogWarning("[Settings] unreadable, using defaults: " + e.Message); return null; }
        }

        static GameSettings Defaults()
        {
            var s = new GameSettings();
            var r = Screen.currentResolution;
            s.width = r.width; s.height = r.height;
            // modest hardware gets a lighter default
            int vram = SystemInfo.graphicsMemorySize;
            s.quality = vram >= 7000 ? QualityPreset.Ultra : vram >= 3500 ? QualityPreset.High : vram >= 1800 ? QualityPreset.Medium : QualityPreset.Low;
            if (s.quality <= QualityPreset.Medium) { s.ambientOcclusion = false; s.grassDensity = 0.6f; }
            s.language = Application.systemLanguage == SystemLanguage.Unknown ? "ka" : (Application.systemLanguage == SystemLanguage.English ? "en" : "ka");
            return s;
        }

        public void Commit(GameSettings changed)
        {
            if (changed == null) return;
            changed.Sanitize();
            Current = changed;
            Apply();
            Persist();
        }

        public void ResetToDefaults() { Commit(Defaults()); }

        public GameSettings CreateDefaults() { return Defaults(); }

        void Persist()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(Current, true), System.Text.Encoding.UTF8);
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        public void Apply()
        {
            var s = Current;
            // display
            if (!Application.isEditor && s.width > 0 && s.height > 0)
            {
                var cur = Screen.currentResolution;
                if (Screen.width != s.width || Screen.height != s.height || Screen.fullScreenMode != s.fullScreenMode)
                    Screen.SetResolution(s.width, s.height, s.fullScreenMode, cur.refreshRateRatio);
            }
            // the quality level first: it carries its own vSync (on in every level), so the player's choices go on top (GK-25)
            int levels = QualitySettings.names.Length;
            if (levels > 0) QualitySettings.SetQualityLevel(Mathf.Clamp((int)s.quality, 0, levels - 1), true);
            QualitySettings.vSyncCount = s.vSync ? 1 : 0;
            Application.targetFrameRate = s.vSync ? -1 : (s.frameRateCap > 0 ? s.frameRateCap : -1);

            // audio
            if (mixer != null)
            {
                SetDb(MixMaster, s.masterVolume);
                SetDb(MixMusic, s.musicVolume);
                SetDb(MixSfx, s.sfxVolume);
                SetDb(MixVoice, s.voiceVolume);
                SetDb(MixAmbience, s.ambienceVolume);
                SetDb(MixUi, s.uiVolume);
            }
            else AudioListener.volume = s.masterVolume;

            if (Loc.Language != s.language) Loc.SetLanguage(s.language);
            EventBus.Publish(new SettingsChanged(s));
        }

        void SetDb(string param, float linear)
        {
            float db = linear <= 0.0001f ? -80f : Mathf.Log10(linear) * 20f;
            if (!mixer.SetFloat(param, db)) Debug.LogWarning("[Settings] mixer has no exposed parameter " + param);
        }
    }
}
