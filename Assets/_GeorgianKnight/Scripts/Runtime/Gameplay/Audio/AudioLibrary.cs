using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>All combat / foley clips, filled by the editor builder from Audio/SFX.</summary>
    [CreateAssetMenu(menuName = "Georgian Knight/Audio Library", fileName = "AudioLibrary")]
    public class AudioLibrary : ScriptableObject
    {
        public AudioClip[] swingLight, swingHeavy;
        public AudioClip[] flesh, fleshHeavy;
        public AudioClip[] block, parry;
        public AudioClip[] kick, bodyfall;
        public AudioClip[] stepWalk, stepRun, stepSprint, chainmail;
        public AudioClip[] gruntAttack, gruntPain, groanDeath, battleCry;
        public AudioClip[] unsheathe, boom, whoosh;
        public AudioClip ambienceValley, ambienceWind;

        public static AudioClip Pick(AudioClip[] set)
        {
            if (set == null || set.Length == 0) return null;
            return set[Random.Range(0, set.Length)];
        }
    }
}
