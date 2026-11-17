using GeorgianKnight.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GeorgianKnight
{
    /// <summary>
    /// Per-scene entry for gameplay scenes: puts the state machine into <see cref="GameMode.Exploring"/> when the scene
    /// is played directly, offers restart after an encounter ends, and provides a minimal pause when no UI layer
    /// (<see cref="IMenuController"/>) is installed (bare test scenes).
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public class GameBootstrap : MonoBehaviour
    {
        public GKInput input;
        public WaveManager waves;

        IGameStateMachine states;

        void Start()
        {
            if (input == null) input = FindAnyObjectByType<GKInput>();
            if (waves == null) waves = FindAnyObjectByType<WaveManager>();
            if (Services.TryGet(out states) && (states.Base == GameMode.Boot || states.Base == GameMode.Loading || states.Base == GameMode.MainMenu))
                states.SetBase(GameMode.Exploring);
        }

        void Update()
        {
            if (states == null || input == null) return;
            bool ended = waves != null && (waves.CurrentPhase == WaveManager.Phase.Victory || waves.CurrentPhase == WaveManager.Phase.Defeat);

            if (!Services.Has<IMenuController>() && input.PausePressed && !ended)
            {
                if (states.Is(GameMode.Paused)) states.Pop(GameMode.Paused);
                else if (states.InGameplay) states.Push(GameMode.Paused);
            }

            // encounter restart (prototype scene): only from gameplay, never under a menu; through SceneFlow so the
            // loading screen, UI and every SceneLoadStarted listener see a normal scene change
            // the UI end card owns restart (keyboard and pad) when a menu layer is installed (C4)
            if (ended && !Services.Has<IMenuController>() && states.InGameplay && UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.rKey.wasPressedThisFrame)
            {
                ISceneFlow flow;
                if (Services.TryGet(out flow)) { if (!flow.IsLoading) flow.Load(SceneManager.GetActiveScene().name); }
                else { states.ClearOverlays(); SceneManager.LoadScene(SceneManager.GetActiveScene().name); }
            }

            // clicking back into the game window re-captures the mouse
            if (states.InGameplay && Cursor.lockState != CursorLockMode.Locked &&
                UnityEngine.InputSystem.Mouse.current != null && UnityEngine.InputSystem.Mouse.current.leftButton.wasPressedThisFrame)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }
    }
}
