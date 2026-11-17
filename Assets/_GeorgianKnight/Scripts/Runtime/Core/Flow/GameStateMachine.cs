using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight.Core
{
    /// <summary>
    /// Base modes describe what the world is doing; overlay modes sit on top of a base mode and are popped back off.
    /// </summary>
    public enum GameMode
    {
        // base
        Boot, MainMenu, Loading, Exploring, Combat, Death,
        // overlays
        Dialogue, Cutscene, Menu, Paused
    }

    public interface IGameStateMachine
    {
        GameMode Current { get; }
        GameMode Base { get; }
        bool Is(GameMode m);
        bool InGameplay { get; }          // player has control of the character
        bool CursorVisible { get; }
        void SetBase(GameMode mode);
        void Push(GameMode overlay);
        void Pop(GameMode overlay);
        void ClearOverlays();
    }

    /// <summary>
    /// Single source of truth for "what may the player do right now". Owns cursor state and the pause/menu time
    /// requests, and broadcasts <see cref="GameModeChanged"/> so input maps, HUD and audio follow.
    /// </summary>
    public sealed class GameStateMachine : IGameStateMachine
    {
        readonly ITimeService time;
        readonly List<GameMode> overlays = new List<GameMode>();
        readonly Dictionary<GameMode, int> timeHandles = new Dictionary<GameMode, int>();
        GameMode baseMode = GameMode.Boot;

        public GameStateMachine(ITimeService time) { this.time = time; }

        public GameMode Base { get { return baseMode; } }
        public GameMode Current { get { return overlays.Count > 0 ? overlays[overlays.Count - 1] : baseMode; } }
        public bool Is(GameMode m) { return Current == m; }

        public bool InGameplay
        {
            get { var c = Current; return c == GameMode.Exploring || c == GameMode.Combat; }
        }

        public bool CursorVisible
        {
            get
            {
                var c = Current;
                return c == GameMode.MainMenu || c == GameMode.Menu || c == GameMode.Paused || c == GameMode.Death || c == GameMode.Dialogue;
            }
        }

        static bool IsOverlay(GameMode m) { return m == GameMode.Dialogue || m == GameMode.Cutscene || m == GameMode.Menu || m == GameMode.Paused; }

        public void SetBase(GameMode mode)
        {
            if (IsOverlay(mode)) { Debug.LogError("[GameState] " + mode + " is an overlay; use Push"); return; }
            var prev = Current;
            baseMode = mode;
            if (mode == GameMode.MainMenu || mode == GameMode.Loading || mode == GameMode.Boot) ClearOverlaysSilently();
            Changed(prev);
        }

        public void Push(GameMode overlay)
        {
            if (!IsOverlay(overlay)) { Debug.LogError("[GameState] " + overlay + " is a base mode; use SetBase"); return; }
            var prev = Current;
            overlays.Remove(overlay);               // re-pushing moves it to the top instead of stacking duplicates
            overlays.Add(overlay);
            if (!timeHandles.ContainsKey(overlay))
            {
                if (overlay == GameMode.Paused) timeHandles[overlay] = time.Hold(TimeLayer.Pause, 0f);
                else if (overlay == GameMode.Menu) timeHandles[overlay] = time.Hold(TimeLayer.Menu, 0f);
            }
            Changed(prev);
        }

        public void Pop(GameMode overlay)
        {
            if (!overlays.Contains(overlay)) return;
            var prev = Current;
            overlays.Remove(overlay);
            ReleaseTime(overlay);
            Changed(prev);
        }

        public void ClearOverlays()
        {
            var prev = Current;
            ClearOverlaysSilently();
            Changed(prev);
        }

        void ClearOverlaysSilently()
        {
            foreach (var o in overlays.ToArray()) ReleaseTime(o);
            overlays.Clear();
        }

        void ReleaseTime(GameMode o)
        {
            int h;
            if (timeHandles.TryGetValue(o, out h)) { time.Release(h); timeHandles.Remove(o); }
        }

        void Changed(GameMode prev)
        {
            var cur = Current;
            Cursor.lockState = CursorVisible ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = CursorVisible;
            if (prev != cur) EventBus.Publish(new GameModeChanged(prev, cur));
        }
    }
}
