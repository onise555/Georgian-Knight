#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.Core
{
    /// <summary>
    /// Editor only. In the editor QualitySettings is the project's QualitySettings.asset, so what SettingsService.Apply
    /// writes in Play (the player's quality level and vSync) would stay in the project after Play and could be committed
    /// by accident. The active level and every level's vSync are saved when Play starts and put back when it ends
    /// (GK-25 follow-up). Edits to those two made by hand during Play are undone the same way.
    /// </summary>
    [InitializeOnLoad]
    public static class EditorQualityGuard
    {
        const string Key = "GK.EditorQualityGuard";

        static EditorQualityGuard()
        {
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static void OnPlayMode(PlayModeStateChange s)
        {
            // SessionState: entering Play reloads the domain between the two calls
            if (s == PlayModeStateChange.ExitingEditMode) SessionState.SetString(Key, Snapshot());
            else if (s == PlayModeStateChange.EnteredEditMode)
            {
                string saved = SessionState.GetString(Key, "");
                SessionState.EraseString(Key);
                if (saved.Length > 0) Restore(saved);
            }
        }

        /// <summary>"level;vSync0,vSync1,..." of the quality settings as they are now.</summary>
        public static string Snapshot()
        {
            int level = QualitySettings.GetQualityLevel();
            var v = new string[QualitySettings.names.Length];
            for (int i = 0; i < v.Length; i++) { QualitySettings.SetQualityLevel(i, false); v[i] = QualitySettings.vSyncCount.ToString(); }
            QualitySettings.SetQualityLevel(level, false);
            return level + ";" + string.Join(",", v);
        }

        /// <summary>Puts back a <see cref="Snapshot"/>; does nothing if the levels changed in between.</summary>
        public static void Restore(string snapshot)
        {
            var parts = snapshot.Split(';');
            if (parts.Length != 2) return;
            var v = parts[1].Split(',');
            int level;
            if (v.Length != QualitySettings.names.Length || !int.TryParse(parts[0], out level)) return;
            for (int i = 0; i < v.Length; i++)
            {
                int n;
                if (!int.TryParse(v[i], out n)) continue;
                QualitySettings.SetQualityLevel(i, false);
                if (QualitySettings.vSyncCount != n) QualitySettings.vSyncCount = n;
            }
            QualitySettings.SetQualityLevel(Math.Max(0, Math.Min(level, v.Length - 1)), false);
        }
    }
}
#endif
