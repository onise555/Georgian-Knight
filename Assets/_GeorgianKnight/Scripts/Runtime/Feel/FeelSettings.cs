using System;
using UnityEngine;

namespace GeorgianKnight.Feel
{
    /// <summary>A sound played on top of the combat mix when a moment fires (a sub-bass thump, a ring-out).</summary>
    [Serializable]
    public class SoundLayer
    {
        public AudioClip[] clips;
        [Range(0f, 1.5f)] public float volume = 0.8f;
        public Vector2 pitch = new Vector2(0.95f, 1.05f);
        [Tooltip("Seconds after the moment (unscaled), for a tail that follows the crack.")]
        [Range(0f, 0.5f)] public float delay;
        [Tooltip("Played flat in the listener's ears instead of at the contact point (sub-bass, stingers).")]
        public bool flat;
        [Tooltip("Only when the player deals or takes it.")]
        public bool playerOnly;
    }

    /// <summary>Everything one moment does, from freezing time to the sound on top.</summary>
    [Serializable]
    public class ImpactProfile
    {
        [Header("Hit-stop and camera (read by CombatFeel)")]
        [Tooltip("Seconds both fighters freeze. For Hit and Kill a multiplier of the attack's own hit-stop.")]
        public float hitStop;
        [Tooltip("Cinemachine impulse strength. For Hit and Kill a multiplier of the attack's own shake.")]
        public float shake;

        [Header("Slow motion (read by CombatFeel)")]
        [Range(0.05f, 1f), Tooltip("1 = none.")] public float slowScale = 1f;
        [Tooltip("Real seconds the world stays slowed.")] public float slowSeconds;
        [Tooltip("At most one per this many real seconds (0 = every time).")] public float slowCooldown;
        public bool slowOnlyOnCritical;

        [Header("Controller rumble (player involved only)")]
        [Range(0f, 1f)] public float rumbleLow;
        [Range(0f, 1f)] public float rumbleHigh;
        public float rumbleSeconds;

        [Header("Hit flash on the struck body (off by default, D24: it read as a grey ghost; the Director decides)")]
        [ColorUsage(false, true)] public Color flashColor = new Color(1.25f, 1.12f, 1f);
        [Range(0f, 2f)] public float flashStrength;
        public float flashSeconds = 0.1f;

        [Header("Screen pulse")]
        [Range(0f, 1f)] public float chromatic;
        [Range(0f, 0.6f)] public float vignette;
        public Color vignetteColor = Color.black;
        [Tooltip("Exposure lift in stops (a white pop on a parry).")]
        [Range(0f, 2f)] public float exposure;
        public float pulseSeconds = 0.2f;
        [Tooltip("The screen accent only for a critical (a finishing critical kill), not for every one of these.")]
        public bool pulseOnlyOnCritical;

        [Header("Sound layers")]
        public SoundLayer[] sounds = new SoundLayer[0];
    }

    [Serializable]
    public class FootstepProfile
    {
        [Tooltip("Slower than this (m/s) leaves no dust.")] public float minSpeed = 1.2f;
        [Tooltip("Puffs per step, walking .. sprinting.")] public Vector2 puffs = new Vector2(2f, 7f);
        [Tooltip("Speed (m/s) at which the puff count reaches its maximum.")] public float fullSpeed = 6f;
        [Tooltip("Size multiplier, walking .. sprinting.")] public Vector2 size = new Vector2(0.7f, 1.3f);
        public Color dustColor = new Color(0.62f, 0.55f, 0.45f, 0.35f);
        [Tooltip("What a foot may stand on for dust to rise (ground and terrain).")]
        public LayerMask ground = (1 << 0) | (1 << 10);
        public bool playerOnly;
        [Tooltip("The sound layers play only at this speed and above (m/s): grit under a run, not a stroll.")]
        public float soundMinSpeed = 3f;
        public SoundLayer[] sounds = new SoundLayer[0];
    }

    /// <summary>How a blade's swing ribbon looks, by the kind of swing (read by WeaponTrail).</summary>
    [Serializable]
    public class TrailProfile
    {
        [Tooltip("Ribbon material (shader GK/Feel/BladeTrail). Empty = WeaponTrail's own built-in look.")]
        public Material material;
        [ColorUsage(true, true)] public Color light = new Color(0.86f, 0.92f, 1f, 0.38f);
        [ColorUsage(true, true)] public Color heavy = new Color(1.1f, 1.05f, 1f, 0.55f);
        [ColorUsage(true, true), Tooltip("A charged / critical / execution swing.")] public Color special = new Color(1.6f, 1.15f, 0.7f, 0.7f);
        [ColorUsage(true, true), Tooltip("Enemies' blades, so the player reads whose steel is coming.")] public Color enemy = new Color(1f, 0.78f, 0.6f, 0.4f);
        [Tooltip("Seconds a sample lives: light, heavy.")] public Vector2 lifetime = new Vector2(0.13f, 0.2f);
        [Tooltip("Where along the blade the ribbon begins (0 = hilt, 1 = tip): light, heavy.")] public Vector2 from = new Vector2(0.55f, 0.35f);

        public Color ColorFor(bool heavySwing, bool specialSwing, bool isPlayer)
        {
            if (!isPlayer) return heavySwing || specialSwing ? Color.Lerp(enemy, special, 0.35f) : enemy;
            return specialSwing ? special : heavySwing ? heavy : light;
        }
    }

    /// <summary>
    /// The one place to tune how combat feels. Combat code reads hit-stop, shake and slow motion from it; the Feel
    /// layers read the rest. Lives at VFX/Resources/Feel_Default so every scene finds it without wiring.
    /// </summary>
    [CreateAssetMenu(menuName = "Georgian Knight/Feel Settings", fileName = "Feel_Default")]
    public class FeelSettings : ScriptableObject
    {
        public const string ResourcePath = "Feel_Default";
        /// <summary>Bumped when a default changes meaning; GKFeelBuilder migrates older assets (keeps tuned values).</summary>
        public const int CurrentDataVersion = 3;
        // 0 when the field is missing from an older asset (Unity keeps the initializer otherwise, hiding the upgrade)
        [HideInInspector] public int dataVersion;

        [Header("Master scales (on top of the player's own settings)")]
        [Range(0f, 2f)] public float hitStopScale = 1f;
        [Range(0f, 2f)] public float shakeScale = 1f;
        [Range(0f, 2f)] public float rumbleScale = 1f;
        [Range(0f, 2f)] public float flashScale = 1f;
        [Range(0f, 2f)] public float pulseScale = 1f;
        [Range(0f, 2f)] public float soundVolume = 1f;
        [Tooltip("A critical multiplies hit-stop and shake by these.")]
        public float criticalHitStop = 1.15f, criticalShake = 1.25f;
        [Header("Rhythm (QUALITY 3b): a blow never stalls")]
        [Tooltip("Longest hit-stop for a light blow or a block, seconds (2 frames at 60 fps).")]
        public float hitStopMaxLight = 2f / 60f;
        [Tooltip("Longest hit-stop for a heavy blow, a kill, a parry, a guard break or an execution, seconds (4 frames at 60 fps).")]
        public float hitStopMaxHeavy = 4f / 60f;
        [Tooltip("Rumble when the player is the one struck, relative to dealing it.")]
        public float rumbleWhenHurt = 1.3f;

        [Header("Moments")]
        public ImpactProfile hit = new ImpactProfile { hitStop = 1f, shake = 1f, rumbleLow = 0.18f, rumbleHigh = 0.35f, rumbleSeconds = 0.09f, flashStrength = 0f, flashSeconds = 0.07f };
        public ImpactProfile heavyHit = new ImpactProfile { hitStop = 1f, shake = 1f, rumbleLow = 0.45f, rumbleHigh = 0.6f, rumbleSeconds = 0.15f, flashStrength = 0f, flashSeconds = 0.08f, chromatic = 0.35f, vignette = 0.12f, pulseSeconds = 0.18f };
        public ImpactProfile kill = new ImpactProfile { hitStop = 1.6f, shake = 1.5f, slowScale = 0.35f, slowSeconds = 0.18f, slowCooldown = 6f, slowOnlyOnCritical = true, pulseOnlyOnCritical = true, rumbleLow = 0.6f, rumbleHigh = 0.7f, rumbleSeconds = 0.22f, flashStrength = 0f, flashSeconds = 0.1f, chromatic = 0.45f, vignette = 0.18f, pulseSeconds = 0.25f };
        public ImpactProfile block = new ImpactProfile { hitStop = 0.045f, shake = 0.18f, rumbleLow = 0.15f, rumbleHigh = 0.45f, rumbleSeconds = 0.07f };
        public ImpactProfile guardBreak = new ImpactProfile { hitStop = 0.045f, shake = 0.18f, rumbleLow = 0.65f, rumbleHigh = 0.8f, rumbleSeconds = 0.25f, flashStrength = 0f, flashSeconds = 0.07f, chromatic = 0.3f, vignette = 0.1f, pulseSeconds = 0.2f };
        public ImpactProfile parry = new ImpactProfile { hitStop = 0.09f, shake = 0.35f, rumbleLow = 0.25f, rumbleHigh = 1f, rumbleSeconds = 0.12f, chromatic = 0.4f, exposure = 0.55f, pulseSeconds = 0.16f };
        public ImpactProfile execution = new ImpactProfile { hitStop = 0.12f, shake = 0.6f, slowScale = 0.3f, slowSeconds = 0.45f, rumbleLow = 0.8f, rumbleHigh = 0.8f, rumbleSeconds = 0.3f, flashStrength = 0f, flashSeconds = 0.12f, chromatic = 0.5f, vignette = 0.22f, pulseSeconds = 0.35f };
        [Tooltip("The moment an execution begins (before the kill lands).")]
        public ImpactProfile executionStart = new ImpactProfile { slowScale = 0.55f, slowSeconds = 0.6f };
        public ImpactProfile swing = new ImpactProfile();
        public ImpactProfile heavySwing = new ImpactProfile { rumbleLow = 0.1f, rumbleSeconds = 0.08f };
        public ImpactProfile death = new ImpactProfile();
        public ImpactProfile dodge = new ImpactProfile();

        [Header("Footsteps")]
        public FootstepProfile footsteps = new FootstepProfile();

        [Header("Blade trails")]
        public TrailProfile trail = new TrailProfile();

        [Header("Assets (VFX/)")]
        [Tooltip("Additive fresnel material drawn over a struck body (shader GK/Feel/HitFlash).")]
        public Material flashMaterial;
        [Tooltip("Blood film on a sword (shader GK/Feel/BladeBlood), drawn by HitFX's BladeBloodFilm over the steel.")]
        public Material bladeBloodMaterial;
        [Tooltip("The footstep dust particle system; its emission is driven per step.")]
        public ParticleSystem footDustPrefab;
        [Tooltip("3D sound layers use these distances.")]
        public float soundMinDistance = 2.5f, soundMaxDistance = 50f;
        [Tooltip("Mixer group for impact / swing layers (Audio's SFX/Combat, contract C13). Set = the mixer applies the player's sfx volume.")]
        public UnityEngine.Audio.AudioMixerGroup combatMixerGroup;
        [Tooltip("Mixer group for footstep layers (Audio's SFX/Foley).")]
        public UnityEngine.Audio.AudioMixerGroup foleyMixerGroup;

        static FeelSettings active;

        /// <summary>The project's settings asset, or built-in defaults when there is none (never null).</summary>
        public static FeelSettings Active
        {
            get
            {
                if (active == null)
                {
                    active = Resources.Load<FeelSettings>(ResourcePath);
                    if (active == null)
                    {
                        active = CreateInstance<FeelSettings>();
                        active.hideFlags = HideFlags.DontSave;
                    }
                }
                return active;
            }
            set { active = value; }
        }

        public ImpactProfile Profile(FeelKind kind, bool heavy)
        {
            switch (kind)
            {
                case FeelKind.Hit: return heavy ? heavyHit : hit;
                case FeelKind.Kill: return kill;
                case FeelKind.Block: return block;
                case FeelKind.GuardBreak: return guardBreak;
                case FeelKind.Parry: return parry;
                case FeelKind.Execution: return execution;
                case FeelKind.Swing: return heavy ? heavySwing : swing;
                case FeelKind.Death: return death;
                case FeelKind.Dodge: return dodge;
                default: return null;
            }
        }

        /// <summary>Hit-stop seconds for a moment; <paramref name="attackValue"/> is the attack's own (Hit / Kill).</summary>
        public float HitStop(FeelKind kind, bool heavy, float attackValue, bool critical)
        {
            var p = Profile(kind, heavy);
            if (p == null) return 0f;
            float s = kind == FeelKind.Hit || kind == FeelKind.Kill ? attackValue * p.hitStop : p.hitStop;
            s *= hitStopScale * (critical ? criticalHitStop : 1f);
            // the cap keeps the cadence of a chain: a light cut barely catches, a heavy one bites, neither stalls
            bool big = heavy || kind == FeelKind.Kill || kind == FeelKind.Execution || kind == FeelKind.Parry || kind == FeelKind.GuardBreak;
            return Mathf.Min(s, big ? hitStopMaxHeavy : hitStopMaxLight);
        }

        /// <summary>Camera impulse strength for a moment; <paramref name="attackValue"/> is the attack's own (Hit / Kill).</summary>
        public float Shake(FeelKind kind, bool heavy, float attackValue, bool critical)
        {
            var p = Profile(kind, heavy);
            if (p == null) return 0f;
            float s = kind == FeelKind.Hit || kind == FeelKind.Kill ? attackValue * p.shake : p.shake;
            return s * shakeScale * (critical ? criticalShake : 1f);
        }

        /// <summary>Whether the moment slows the world, and how. A cooldown above zero means a rare flourish.</summary>
        public bool SlowMotion(ImpactProfile p, bool critical, out float scale, out float seconds, out float cooldown)
        {
            scale = 1f; seconds = 0f; cooldown = 0f;
            if (p == null || p.slowScale >= 0.999f || p.slowSeconds <= 0f) return false;
            if (p.slowOnlyOnCritical && !critical) return false;
            scale = p.slowScale; seconds = p.slowSeconds; cooldown = p.slowCooldown;
            return true;
        }

        public bool SlowMotion(FeelKind kind, bool heavy, bool critical, out float scale, out float seconds, out float cooldown)
        {
            return SlowMotion(Profile(kind, heavy), critical, out scale, out seconds, out cooldown);
        }
    }
}
