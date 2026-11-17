using System;
using UnityEngine;

namespace GeorgianKnight.Core
{
    public enum QualityPreset { Low = 0, Medium = 1, High = 2, Ultra = 3 }
    public enum Difficulty { Story = 0, Knight = 1, Hero = 2 }
    /// <summary>Gore presentation (owner default: Full). Reduced = no dismemberment, less blood; Off = no blood.</summary>
    public enum GoreLevel { Full = 0, Reduced = 1, Off = 2 }

    /// <summary>Every user option. Persisted to settings.json, independent of save slots.</summary>
    [Serializable]
    public sealed class GameSettings
    {
        public int version = 1;

        // ---- display
        public int width;
        public int height;
        public FullScreenMode fullScreenMode = FullScreenMode.FullScreenWindow;
        public bool vSync = true;
        public int frameRateCap;                  // 0 = unlimited (when vSync off)
        [Range(0.5f, 2f)] public float brightness = 1f;

        // ---- graphics
        public QualityPreset quality = QualityPreset.High;
        [Range(0.5f, 1f)] public float renderScale = 1f;
        public bool ambientOcclusion = true;
        public bool bloom = true;
        public bool motionBlur;
        public bool depthOfField = true;           // cinematic moments only
        public bool filmGrain = true;
        [Range(0.25f, 1f)] public float grassDensity = 1f;
        [Range(40f, 90f)] public float fieldOfView = 50f;   // vertical; close over-the-shoulder framing (HANDBOOK/03)

        // ---- audio (linear 0..1)
        [Range(0f, 1f)] public float masterVolume = 1f;
        [Range(0f, 1f)] public float musicVolume = 0.75f;
        [Range(0f, 1f)] public float sfxVolume = 1f;
        [Range(0f, 1f)] public float voiceVolume = 1f;
        [Range(0f, 1f)] public float ambienceVolume = 0.8f;
        [Range(0f, 1f)] public float uiVolume = 0.7f;
        public bool muteWhenUnfocused = true;

        // ---- controls
        [Range(0.1f, 3f)] public float mouseSensitivity = 1f;
        [Range(0.1f, 3f)] public float gamepadSensitivity = 1f;
        public bool invertY;
        public bool toggleSprint;
        public bool toggleBlock;
        public bool vibration = true;
        public string bindingOverrides = "";       // InputActionAsset.SaveBindingOverridesAsJson()

        // ---- gameplay
        public Difficulty difficulty = Difficulty.Knight;
        [Range(0f, 1.5f)] public float cameraShake = 1f;
        public bool hitStop = true;
        public bool cinematicFinishers = true;     // slow motion + finisher camera
        public bool lockOnAutoSwitch = true;
        public bool showCompass = true;
        public bool showDamageNumbers;
        public bool autosave = true;

        // ---- interface & accessibility
        public string language = "ka";
        public bool subtitles = true;
        public bool speakerNames = true;
        [Range(0.75f, 2f)] public float subtitleScale = 1f;
        [Tooltip("Opacity of the plate behind subtitles (0 = none).")]
        [Range(0f, 1f)] public float subtitleBackground = 0.6f;
        [Tooltip("Scale of all menu / HUD text (1 = 100 %).")]
        [Range(1f, 2f)] public float textScale = 1f;
        [Range(0.75f, 1.4f)] public float hudScale = 1f;
        [Range(0f, 1f)] public float hudOpacity = 1f;
        [Tooltip("Fraction of the screen used by HUD and menus (TV overscan / safe area).")]
        [Range(0.85f, 1f)] public float safeArea = 0.95f;
        public bool reduceFlashes;
        public GoreLevel gore = GoreLevel.Full;

        public GameSettings Clone() { return JsonUtility.FromJson<GameSettings>(JsonUtility.ToJson(this)); }

        public void Sanitize()
        {
            renderScale = Mathf.Clamp(renderScale, 0.5f, 1f);
            fieldOfView = Mathf.Clamp(fieldOfView, 40f, 90f);
            mouseSensitivity = Mathf.Clamp(mouseSensitivity, 0.1f, 3f);
            gamepadSensitivity = Mathf.Clamp(gamepadSensitivity, 0.1f, 3f);
            masterVolume = Mathf.Clamp01(masterVolume); musicVolume = Mathf.Clamp01(musicVolume);
            sfxVolume = Mathf.Clamp01(sfxVolume); voiceVolume = Mathf.Clamp01(voiceVolume);
            ambienceVolume = Mathf.Clamp01(ambienceVolume); uiVolume = Mathf.Clamp01(uiVolume);
            subtitleScale = Mathf.Clamp(subtitleScale, 0.75f, 2f);
            subtitleBackground = Mathf.Clamp01(subtitleBackground);
            textScale = Mathf.Clamp(textScale, 1f, 2f);
            safeArea = Mathf.Clamp(safeArea, 0.85f, 1f);
            hudScale = Mathf.Clamp(hudScale, 0.75f, 1.4f);
            if (frameRateCap < 0) frameRateCap = 0;
            if (string.IsNullOrEmpty(language)) language = "ka";
        }
    }
}
