using System;
using GeorgianKnight.Core;
using UnityEngine;

namespace GeorgianKnight.World
{
    /// <summary>
    /// The Georgian line moves up for assault 3 (P03; C17 note; Team Lead decision, M1 = non-fighting allies): when the
    /// given wave starts it carries the whole formation from one marker to the other (Line_W1 → Line_W3 on Black Banner
    /// Rise) over a few seconds, keeping the ranks, each figure set back on the ground as it goes. The "no going back"
    /// colliders switch off as it starts. Once moved it stays; a restart reloads the scene. Logs start and end in the editor
    /// only, and the figure list is gathered at load: the move itself allocates nothing (GK-26, Build & QA: the end log
    /// was ~2.5 KB in one frame, wave 3 + 8 s, in every player run).
    /// </summary>
    public class LineAdvance : MonoBehaviour
    {
        public Transform from;
        public Transform to;
        [Tooltip("0-based wave index whose start moves the line (Didgori arena: 2 = wave 3)")]
        public int advanceAtWave = 2;
        public float duration = 8f;
        [Tooltip("What the figures stand on (Terrain)")]
        public LayerMask ground = 1 << 10;

        IDisposable sub;
        bool moving, done;
        float t0;
        Vector3 startPos, endPos;
        Quaternion startRot, endRot;
        Transform[] figures;
        float[] lift;
        Collider[] gates;

        void Awake()
        {
            figures = new Transform[transform.childCount];
            for (int i = 0; i < figures.Length; i++) figures[i] = transform.GetChild(i);
            lift = new float[figures.Length];
            gates = GetComponentsInChildren<Collider>();
        }

        void OnEnable() { sub = EventBus.Subscribe<WaveStarted>(OnWave); }

        void OnDisable() { if (sub != null) { sub.Dispose(); sub = null; } }

        void OnWave(WaveStarted e)
        {
            if (moving || done || e.waveIndex < advanceAtWave || from == null || to == null) return;
            // the same offset from 'to' as the formation has from 'from' now
            Quaternion delta = to.rotation * Quaternion.Inverse(from.rotation);
            startPos = transform.position; startRot = transform.rotation;
            endPos = to.position + delta * (transform.position - from.position);
            endRot = delta * transform.rotation;
            for (int i = 0; i < figures.Length; i++) lift[i] = figures[i].position.y - Ground(figures[i].position);
            for (int i = 0; i < gates.Length; i++) gates[i].enabled = false;
            moving = true; t0 = Time.time;
            if (Application.isEditor) Debug.Log("[LineAdvance] " + name + ": wave index " + e.waveIndex + " -> advancing to " + to.name + " over " + duration + " s");
        }

        void Update()
        {
            if (!moving) return;
            float k = Mathf.Clamp01((Time.time - t0) / Mathf.Max(0.01f, duration));
            float s = k * k * (3f - 2f * k);
            transform.SetPositionAndRotation(Vector3.Lerp(startPos, endPos, s), Quaternion.Slerp(startRot, endRot, s));
            for (int i = 0; i < figures.Length; i++)
            {
                var p = figures[i].position;
                p.y = Ground(p) + lift[i];
                figures[i].position = p;
            }
            if (k >= 1f)
            {
                moving = false; done = true;
                if (Application.isEditor) Debug.Log("[LineAdvance] " + name + ": holding at " + to.name + " " + transform.position.ToString("F1"));
            }
        }

        float Ground(Vector3 p)
        {
            RaycastHit hit;
            return Physics.Raycast(p + Vector3.up * 50f, Vector3.down, out hit, 200f, ground, QueryTriggerInteraction.Ignore) ? hit.point.y : p.y;
        }
    }
}
