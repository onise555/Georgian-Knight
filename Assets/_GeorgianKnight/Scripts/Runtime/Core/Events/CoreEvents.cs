namespace GeorgianKnight.Core
{
    public struct GameModeChanged : IGameEvent
    {
        public GameMode Previous, Current;
        public GameModeChanged(GameMode previous, GameMode current) { Previous = previous; Current = current; }
    }

    public struct SettingsChanged : IGameEvent
    {
        public GameSettings Settings;
        public SettingsChanged(GameSettings s) { Settings = s; }
    }

    public struct LanguageChanged : IGameEvent
    {
        public string Language;
        public LanguageChanged(string lang) { Language = lang; }
    }

    public struct FactChanged : IGameEvent
    {
        public string Key;
        public FactChanged(string key) { Key = key; }
    }

    /// <summary>
    /// The whole fact table was replaced (save applied, new game). Systems that cached facts (doors, chests, pickups,
    /// map discoveries) re-read their state. Individual <see cref="FactChanged"/> events are NOT raised for a restore.
    /// </summary>
    public struct FactsRestored : IGameEvent { }

    /// <summary>A new game started: every save participant was reset to its new-game state.</summary>
    public struct NewGameStarted : IGameEvent { }

    public struct TimeScaleChanged : IGameEvent
    {
        public float Scale;
        public bool Paused;
        public TimeScaleChanged(float scale, bool paused) { Scale = scale; Paused = paused; }
    }

    public struct SceneLoadStarted : IGameEvent
    {
        public string Scene;
        public SceneLoadStarted(string scene) { Scene = scene; }
    }

    /// <summary>A requested scene could not be loaded (not in build settings); the previous scene stays active.</summary>
    public struct SceneLoadFailed : IGameEvent
    {
        public string Scene;
        public SceneLoadFailed(string scene) { Scene = scene; }
    }

    public struct SceneLoaded : IGameEvent
    {
        public string Scene, SpawnId;
        public SceneLoaded(string scene, string spawnId) { Scene = scene; SpawnId = spawnId; }
    }

    public struct GameSaved : IGameEvent
    {
        public int Slot; public bool Success;
        public GameSaved(int slot, bool ok) { Slot = slot; Success = ok; }
    }

    public struct GameLoaded : IGameEvent
    {
        public int Slot; public bool Success;
        public GameLoaded(int slot, bool ok) { Slot = slot; Success = ok; }
    }

    /// <summary>Shown by the HUD as a toast ("Saved", "New skill learned", "+120 drahkani" …). Text is already localized.</summary>
    public struct Notification : IGameEvent
    {
        public string Text; public string Icon; public float Seconds;
        public Notification(string text, string icon = null, float seconds = 3f) { Text = text; Icon = icon; Seconds = seconds; }
    }
}
