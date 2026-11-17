using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace GeorgianKnight.Core
{
    /// <summary>Project-wide configuration, loaded from Resources/GameConfig.</summary>
    [CreateAssetMenu(menuName = "Georgian Knight/Game Config", fileName = "GameConfig")]
    public sealed class GameConfig : ScriptableObject
    {
        public GameDatabase database;
        public AudioMixer mixer;
        public List<GameModule> modules = new List<GameModule>();
        [Header("Scenes")]
        public string mainMenuScene = "MainMenu";
        public string firstScene = "Didgori";
        public string firstSpawn = "start";
        [Header("Rules")]
        public float autosaveMinInterval = 120f;
    }
}
