using GeorgianKnight.Core;
using UnityEngine;

namespace GeorgianKnight
{
    public enum AudioCategory { Sfx, Voice, Ambience, Music, Ui }

    /// <summary>
    /// Category volumes from the player's settings (linear 0..1). The master volume is applied globally by the
    /// settings service; every AudioSource the game plays multiplies its own volume by <see cref="Get"/>.
    /// Ambience/music loops subscribe to <see cref="Changed"/> to follow slider moves live.
    /// </summary>
    public static class AudioMix
    {
        static float sfx = 1f, voice = 1f, ambience = 0.8f, music = 0.75f, ui = 0.7f;

        public static event System.Action Changed;

        public static float Get(AudioCategory c)
        {
            switch (c)
            {
                case AudioCategory.Voice: return voice;
                case AudioCategory.Ambience: return ambience;
                case AudioCategory.Music: return music;
                case AudioCategory.Ui: return ui;
                default: return sfx;
            }
        }

        public static void Apply(GameSettings s)
        {
            if (s == null) return;
            sfx = s.sfxVolume; voice = s.voiceVolume; ambience = s.ambienceVolume; music = s.musicVolume; ui = s.uiVolume;
            var h = Changed;
            if (h != null) h();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() { Changed = null; sfx = 1f; voice = 1f; ambience = 0.8f; music = 0.75f; ui = 0.7f; }
    }
}
