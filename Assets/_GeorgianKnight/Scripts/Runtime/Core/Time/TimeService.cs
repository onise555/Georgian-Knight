using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight.Core
{
    /// <summary>Why time is being changed. Lower layers win nothing: the effective scale is the minimum of all requests.</summary>
    public enum TimeLayer { Pause, Menu, Cutscene, HitStop, SlowMotion, Debug }

    public interface ITimeService
    {
        float Scale { get; }
        bool IsPaused { get; }
        /// <summary>Holds a scale until released. Returns a handle.</summary>
        int Hold(TimeLayer layer, float scale);
        /// <summary>Scale for a duration (unscaled seconds) with optional ease in / out (unscaled seconds).</summary>
        int Pulse(TimeLayer layer, float scale, float duration, float easeIn = 0f, float easeOut = 0f);
        void Release(int handle);
        void ReleaseAll(TimeLayer layer);
        bool IsActive(TimeLayer layer);
        void Tick(float unscaledDeltaTime);
    }

    /// <summary>
    /// The only writer of <see cref="Time.timeScale"/>. Pause menus, hit-stop, slow motion and cutscenes each request
    /// a scale; the effective scale is the minimum of all active requests, so e.g. a hit-stop ending while the game is
    /// paused can never un-pause it. Fixed timestep follows the scale so physics stays smooth in slow motion.
    /// </summary>
    public sealed class TimeService : ITimeService
    {
        sealed class Request
        {
            public int handle;
            public TimeLayer layer;
            public float target;
            public float duration;   // < 0 => held
            public float easeIn, easeOut;
            public float age;
        }

        readonly List<Request> requests = new List<Request>();
        readonly float baseFixedDelta;
        int nextHandle = 1;
        float applied = -1f;

        public float Scale { get; private set; }
        public bool IsPaused { get { return Scale <= 0f; } }

        public TimeService()
        {
            baseFixedDelta = Time.fixedDeltaTime > 0f ? Time.fixedDeltaTime : 0.02f;
            Scale = 1f;
            Apply(1f);
        }

        public int Hold(TimeLayer layer, float scale)
        {
            var r = new Request { handle = nextHandle++, layer = layer, target = Mathf.Max(0f, scale), duration = -1f };
            requests.Add(r);
            Recompute();
            return r.handle;
        }

        public int Pulse(TimeLayer layer, float scale, float duration, float easeIn = 0f, float easeOut = 0f)
        {
            var r = new Request
            {
                handle = nextHandle++, layer = layer, target = Mathf.Max(0f, scale),
                duration = Mathf.Max(0.0001f, duration), easeIn = Mathf.Max(0f, easeIn), easeOut = Mathf.Max(0f, easeOut)
            };
            requests.Add(r);
            Recompute();
            return r.handle;
        }

        public void Release(int handle)
        {
            for (int i = requests.Count - 1; i >= 0; i--)
                if (requests[i].handle == handle) { requests.RemoveAt(i); break; }
            Recompute();
        }

        public void ReleaseAll(TimeLayer layer)
        {
            requests.RemoveAll(r => r.layer == layer);
            Recompute();
        }

        public bool IsActive(TimeLayer layer)
        {
            for (int i = 0; i < requests.Count; i++) if (requests[i].layer == layer) return true;
            return false;
        }

        public void Tick(float dt)
        {
            bool changed = false;
            for (int i = requests.Count - 1; i >= 0; i--)
            {
                var r = requests[i];
                if (r.duration < 0f) continue;
                // pulses do not age while the game is paused by another layer (a hit-stop survives a pause)
                if (Scale <= 0f && r.target > 0f) continue;
                r.age += dt;
                if (r.age >= r.duration + r.easeOut) { requests.RemoveAt(i); }
                changed = true;
            }
            if (changed) Recompute();
        }

        static float Current(Request r)
        {
            if (r.duration < 0f) return r.target;
            if (r.easeIn > 0f && r.age < r.easeIn) return Mathf.Lerp(1f, r.target, Smooth(r.age / r.easeIn));
            if (r.age <= r.duration) return r.target;
            if (r.easeOut > 0f) return Mathf.Lerp(r.target, 1f, Smooth((r.age - r.duration) / r.easeOut));
            return 1f;
        }

        static float Smooth(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }

        void Recompute()
        {
            float s = 1f;
            for (int i = 0; i < requests.Count; i++) s = Mathf.Min(s, Current(requests[i]));
            Scale = s;
            Apply(s);
        }

        void Apply(float s)
        {
            if (Mathf.Approximately(s, applied)) return;
            bool wasPaused = applied <= 0f && applied >= 0f;
            applied = s;
            Time.timeScale = s;
            Time.fixedDeltaTime = baseFixedDelta * Mathf.Clamp(s, 0.05f, 1f);
            if (s <= 0f || wasPaused) AudioListener.pause = s <= 0f;
            EventBus.Publish(new TimeScaleChanged(s, s <= 0f));
        }
    }
}
