using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace GeorgianKnight.Core
{
    /// <summary>
    /// Persistent composition root. Boots before the first scene (so any scene can be played directly), creates the
    /// core services in a fixed order, installs feature modules from <see cref="GameConfig"/>, and ticks time.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public sealed class GameRoot : MonoBehaviour
    {
        public static GameRoot Instance { get; private set; }
        public static bool IsBooted { get { return Instance != null && Instance.booted; } }
        /// <summary>Raised once all modules are installed and initialized.</summary>
        public static event Action Booted;

        public GameConfig Config { get; private set; }
        public float Playtime { get; set; }

        TimeService time;
        GameStateMachine states;
        bool booted;
        readonly List<Action<float>> tickers = new List<Action<float>>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            if (Instance != null) return;
            var go = new GameObject("[GameRoot]");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<GameRoot>();
            Instance.Boot();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Instance = null; Booted = null; }

        void Boot()
        {
            if (booted) return;
            Config = Resources.Load<GameConfig>("GameConfig");
            if (Config == null)
            {
                Debug.LogWarning("[GameRoot] Resources/GameConfig missing — running with defaults");
                Config = ScriptableObject.CreateInstance<GameConfig>();
            }

            // core services, fixed order
            time = new TimeService();
            Services.Register<ITimeService>(time);
            var settings = new SettingsService(Config.mixer);
            Services.Register<ISettingsService>(settings);
            Loc.SetLanguage(settings.Current.language);
            if (Config.database != null) Services.Register(Config.database);
            var world = new WorldState();
            Services.Register(world);
            var save = new SaveSystem();
            Services.Register<ISaveSystem>(save);
            save.Register(world);
            states = new GameStateMachine(time);
            Services.Register<IGameStateMachine>(states);
            var flow = gameObject.AddComponent<SceneFlow>();
            Services.Register<ISceneFlow>(flow);

            // feature modules
            foreach (var m in Config.modules)
            {
                if (m == null) continue;
                try { m.Install(this); }
                catch (Exception e) { Debug.LogError("[GameRoot] module " + m.name + " failed to install"); Debug.LogException(e); }
            }
            foreach (var m in Config.modules)
            {
                if (m == null) continue;
                try { m.Initialize(this); }
                catch (Exception e) { Debug.LogError("[GameRoot] module " + m.name + " failed to initialize"); Debug.LogException(e); }
            }

            settings.Apply();
            booted = true;
            var h = Booted;
            if (h != null) h();
        }

        // Script reload while playing (editor "recompile and continue") wipes statics and non-serialized fields:
        // rebuild the services so the session keeps working instead of throwing every frame.
        void OnEnable()
        {
            if (Instance == null) Instance = this;
            if (Instance == this && !booted) Boot();
        }

        /// <summary>Registers a per-frame tick (unscaled delta) owned by a non-MonoBehaviour service.</summary>
        public void AddTicker(Action<float> tick) { if (tick != null) tickers.Add(tick); }
        public void RemoveTicker(Action<float> tick) { tickers.Remove(tick); }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            time.Tick(dt);
            if (states.InGameplay && !time.IsPaused) Playtime += dt;
            for (int i = 0; i < tickers.Count; i++)
            {
                try { tickers[i](dt); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        void OnApplicationFocus(bool focus)
        {
            ISettingsService s;
            if (Services.TryGet(out s) && s.Current.muteWhenUnfocused && !Application.isEditor) AudioListener.volume = focus ? 1f : 0f;
        }

        void OnDestroy()
        {
            if (Instance == this) { Services.Clear(); Instance = null; }
        }
    }
}
