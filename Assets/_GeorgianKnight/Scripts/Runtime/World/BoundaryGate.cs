using System;
using GeorgianKnight.Core;
using UnityEngine;

namespace GeorgianKnight.World
{
    /// <summary>
    /// A boundary segment that opens when a given wave starts (contract C17; Director 2026-10-30): in the Didgori arena the
    /// edge in front of Black Banner Rise opens for wave 3 (index 2), when the line advances. Its children (the blocking
    /// colliders and their greybox blocks) are switched off once and stay off: a boundary only opens, never closes,
    /// mid-fight; a restart reloads the scene closed. Listens to GameplayEvents.WaveStarted on the EventBus (C17,
    /// 0-based, raised by WaveManager at every wave's start) and logs each wave it sees, so a run proves when it opened.
    /// </summary>
    public class BoundaryGate : MonoBehaviour
    {
        [Tooltip("0-based wave index whose start opens this segment (Didgori arena: 2 = wave 3)")]
        public int opensAtWave = 2;
        [Tooltip("Not used since C17 (the gate listens to WaveStarted on the EventBus); kept for the scenes built before it")]
        public WaveManager waves;

        public bool IsOpen { get; private set; }

        IDisposable sub;

        void OnEnable()
        {
            sub = EventBus.Subscribe<WaveStarted>(OnWave);
        }

        void OnDisable()
        {
            if (sub != null) { sub.Dispose(); sub = null; }
        }

        void OnWave(WaveStarted e)
        {
            bool opening = !IsOpen && e.waveIndex >= opensAtWave;
            Debug.Log("[BoundaryGate] " + name + ": wave index " + e.waveIndex + (opening ? " -> OPEN" : IsOpen ? " (already open)" : " (stays closed)"));
            if (opening) Open();
        }

        public void Open()
        {
            IsOpen = true;
            for (int i = 0; i < transform.childCount; i++) transform.GetChild(i).gameObject.SetActive(false);
        }
    }
}
