using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GeorgianKnight.Core
{
    /// <summary>Implemented by the UI layer: black fade + loading screen with progress and tips.</summary>
    public interface ILoadingScreen
    {
        /// <summary>Fades to black (unscaled time) and shows the loading view.</summary>
        IEnumerator Show(string sceneName);
        void SetProgress(float progress01);
        /// <summary>Fades back in.</summary>
        IEnumerator Hide();
    }

    public interface ISceneFlow
    {
        bool IsLoading { get; }
        string CurrentScene { get; }
        string PendingSpawn { get; }
        /// <summary>
        /// Loads a scene. <paramref name="beforeActivate"/> runs behind the loading screen, after the new scene finished
        /// loading in the background and before it activates: apply a save there so the new scene's Awake/OnEnable
        /// already see the restored world state, inventory and quests. <paramref name="onLoaded"/> runs after
        /// <see cref="SceneLoaded"/> was published (the spawner has placed the player).
        /// </summary>
        void Load(string sceneName, string spawnId = null, Action onLoaded = null, Action beforeActivate = null);
    }

    /// <summary>
    /// Async scene transitions. Holds <see cref="GameMode.Loading"/> during the load, waits a frame after activation
    /// so scene Awake/Start have run, raises <see cref="SceneLoaded"/> (spawn systems place the player there), then
    /// fades in. Re-entrant calls while loading are queued.
    /// </summary>
    public sealed class SceneFlow : MonoBehaviour, ISceneFlow
    {
        string queuedScene, queuedSpawn;
        Action queuedCallback, queuedBefore;

        public bool IsLoading { get; private set; }
        public string CurrentScene { get { return SceneManager.GetActiveScene().name; } }
        public string PendingSpawn { get; private set; }

        public void Load(string sceneName, string spawnId = null, Action onLoaded = null, Action beforeActivate = null)
        {
            if (IsLoading)
            {
                if (queuedScene != null) Debug.LogWarning("[SceneFlow] queued load of '" + queuedScene + "' replaced by '" + sceneName + "'");
                queuedScene = sceneName; queuedSpawn = spawnId; queuedCallback = onLoaded; queuedBefore = beforeActivate;
                return;
            }
            StartCoroutine(Run(sceneName, spawnId, onLoaded, beforeActivate));
        }

        IEnumerator Run(string sceneName, string spawnId, Action onLoaded, Action beforeActivate)
        {
            IsLoading = true;
            PendingSpawn = spawnId;
            ILoadingScreen screen;
            Services.TryGet(out screen);
            IGameStateMachine states;
            Services.TryGet(out states);

            EventBus.Publish(new SceneLoadStarted(sceneName));
            if (screen != null) yield return screen.Show(sceneName);
            GameMode previousBase = states != null ? states.Base : GameMode.Exploring;
            if (states != null) states.SetBase(GameMode.Loading);

            var op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
            if (op == null)
            {
                Debug.LogError("[SceneFlow] scene not in build settings: " + sceneName);
                PendingSpawn = null;
                IsLoading = false;
                if (states != null) states.SetBase(previousBase == GameMode.Loading ? GameMode.Exploring : previousBase);
                EventBus.Publish(new SceneLoadFailed(sceneName));
                if (screen != null) yield return screen.Hide();
                RunQueued();
                yield break;
            }
            op.allowSceneActivation = false;
            while (op.progress < 0.9f)
            {
                if (screen != null) screen.SetProgress(op.progress / 0.9f * 0.9f);
                yield return null;
            }
            if (beforeActivate != null)
            {
                try { beforeActivate(); } catch (Exception e) { Debug.LogException(e); }
            }
            op.allowSceneActivation = true;
            while (!op.isDone) yield return null;
            yield return null;                         // let Awake/Start/OnEnable of the new scene run
            if (screen != null) screen.SetProgress(0.95f);
            // let heavy first-frame work (shader warmup, streaming) settle before revealing
            yield return new WaitForSecondsRealtime(0.15f);
            Resources.UnloadUnusedAssets();
            GC.Collect();

            EventBus.Publish(new SceneLoaded(sceneName, spawnId));
            if (onLoaded != null)
            {
                try { onLoaded(); } catch (Exception e) { Debug.LogException(e); }
            }
            if (screen != null) { screen.SetProgress(1f); yield return screen.Hide(); }
            PendingSpawn = null;
            IsLoading = false;

            RunQueued();
        }

        void RunQueued()
        {
            if (queuedScene == null) return;
            string s = queuedScene, sp = queuedSpawn; var cb = queuedCallback; var before = queuedBefore;
            queuedScene = null; queuedSpawn = null; queuedCallback = null; queuedBefore = null;
            Load(s, sp, cb, before);
        }
    }
}
