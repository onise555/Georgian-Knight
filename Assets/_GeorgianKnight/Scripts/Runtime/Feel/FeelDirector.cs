using GeorgianKnight.Core;
using UnityEngine;

namespace GeorgianKnight.Feel
{
    /// <summary>
    /// Receives every <see cref="FeelEvent"/> and hands it to the layers, each owning one sense: rumble, hit flash,
    /// footstep dust, screen pulse, sound layers. Created on its own at the first scene load (no scene wiring) and kept
    /// across scenes. Honors the player's settings (vibration, reduce flashes, camera shake as the pulse's comfort scale,
    /// sfx volume).
    /// </summary>
    [DefaultExecutionOrder(500)]
    public sealed class FeelDirector : MonoBehaviour, IFeelService
    {
        static FeelDirector instance;
        public static FeelDirector I { get { return instance; } }

        [Tooltip("Log every moment to the console as a warning (tests).")]
        public static bool Trace;

        public FeelSettings settings;

        public FeelRumble Rumble { get; private set; }
        public FeelFlash Flash { get; private set; }
        public FeelDust Dust { get; private set; }
        public FeelPulse Pulse { get; private set; }
        public FeelSound Sound { get; private set; }

        /// <summary>Moments received since start, by kind (tests read it).</summary>
        public readonly int[] Counts = new int[16];

        GameSettings player;
        Subscriptions subs;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            // one per game: a director already registered (this scene or one kept from before) wins
            if (instance != null || Services.Has<IFeelService>()) return;
            var go = new GameObject("[Feel]");
            DontDestroyOnLoad(go);
            go.AddComponent<FeelDirector>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { instance = null; Trace = false; }

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            Services.Register<IFeelService>(this);
            if (settings == null) settings = FeelSettings.Active;
            else FeelSettings.Active = settings;

            ISettingsService s;
            if (Services.TryGet(out s)) player = s.Current;
            else Services.Registered += OnServiceRegistered;   // GameRoot may boot after us

            Rumble = new FeelRumble();
            Flash = new FeelFlash(transform);
            Dust = new FeelDust(transform);
            Pulse = new FeelPulse(transform);
            Sound = new FeelSound(transform);
            Apply();

            subs = new Subscriptions()
                .On<FeelEvent>(OnFeel)
                .On<SettingsChanged>(e => { player = e.Settings; Apply(); })
                .On<TimeScaleChanged>(e => Rumble.SetPaused(e.Paused));
        }

        void OnServiceRegistered(System.Type t)
        {
            if (t != typeof(ISettingsService)) return;
            ISettingsService s;
            if (!Services.TryGet(out s)) return;
            player = s.Current;
            Apply();
            Services.Registered -= OnServiceRegistered;
        }

        FeelSettings IFeelService.Settings { get { return settings; } }

        void Apply()
        {
            Rumble.Enabled = player == null || player.vibration;
            Flash.Calm = Pulse.Calm = player != null && player.reduceFlashes;
            Pulse.Comfort = player == null ? 1f : Mathf.Clamp01(player.cameraShake);
            Sound.Volume = player == null ? 1f : player.sfxVolume;
        }

        void OnDestroy()
        {
            if (instance != this) return;
            if (subs != null) subs.Dispose();
            Services.Registered -= OnServiceRegistered;
            Services.Unregister<IFeelService>(this);
            Rumble.Stop();
            Flash.Dispose();
            Pulse.Dispose();
            instance = null;
        }

        void OnApplicationFocus(bool focus) { if (!focus && Rumble != null) Rumble.Stop(); }
        void OnDisable() { if (Rumble != null) Rumble.Stop(); }

        void OnFeel(FeelEvent e)
        {
            int k = (int)e.Kind;
            if (k >= 0 && k < Counts.Length) Counts[k]++;
            var fs = settings;
            if (Trace) Debug.LogWarning("[GKFeel] " + e.Kind + (e.Heavy ? " heavy" : "") + (e.Critical ? " crit" : "") +
                                        " src=" + (e.Source != null ? e.Source.name : "-") + " tgt=" + (e.Target != null ? e.Target.name : "-"));
            if (e.Kind == FeelKind.Footstep)
            {
                var f = fs.footsteps;
                if (f.playerOnly && !e.SourceIsPlayer) return;
                // dust rises from earth only (Unknown = not told: the ground layers decide); stone and wood stay clean
                if (e.Surface != FeelSurface.Unknown && e.Surface != FeelSurface.Earth) return;
                Dust.Step(f, e.Position, e.Speed);
                if (e.Speed >= f.soundMinSpeed) Sound.Play(f.sounds, e.Position, e.SourceIsPlayer, fs.soundVolume, fs.foleyMixerGroup);
                return;
            }

            var p = fs.Profile(e.Kind, e.Heavy);
            if (p == null) return;
            bool playerInvolved = e.SourceIsPlayer || e.TargetIsPlayer;
            if (playerInvolved)
                Rumble.Add(p.rumbleLow, p.rumbleHigh, p.rumbleSeconds, fs.rumbleScale * (e.TargetIsPlayer ? fs.rumbleWhenHurt : 1f));
            if (e.Target != null) Flash.Play(e.Target, p.flashColor, p.flashStrength * fs.flashScale, p.flashSeconds);
            // the screen pulses only for what the player is part of (a duel across the field stays quiet)
            if (playerInvolved && (!p.pulseOnlyOnCritical || e.Critical)) Pulse.Play(p, fs.pulseScale);
            Sound.Play(p.sounds, e.Position, playerInvolved, fs.soundVolume, fs.combatMixerGroup);
        }

        void LateUpdate()
        {
            float dt = Time.unscaledDeltaTime;
            Rumble.Tick(dt);
            Flash.Tick(dt);
            Pulse.Tick(dt);
            Sound.Tick();
        }
    }
}
