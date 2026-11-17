using UnityEngine;
using UnityEngine.InputSystem;

namespace GeorgianKnight.Feel
{
    /// <summary>
    /// Gamepad rumble. Overlapping moments do not add up into a buzz: the motors follow the strongest one still
    /// running, each fading out over its length. Stops on pause, focus loss and when vibration is turned off.
    /// </summary>
    public sealed class FeelRumble
    {
        struct Pulse { public float low, high, left, length; }

        const int Max = 8;
        readonly Pulse[] pulses = new Pulse[Max];
        int count;
        bool paused;
        bool enabled = true;
        Gamepad driven;
        float lastLow = -1f, lastHigh = -1f;

        /// <summary>The motor speeds this frame (tests and the debug HUD read them).</summary>
        public float Low { get; private set; }
        public float High { get; private set; }

        public bool Enabled
        {
            get { return enabled; }
            set { enabled = value; if (!value) Stop(); }
        }

        public void Add(float low, float high, float seconds, float scale)
        {
            if (!enabled || seconds <= 0f || scale <= 0f || (low <= 0f && high <= 0f)) return;
            var p = new Pulse { low = Mathf.Clamp01(low * scale), high = Mathf.Clamp01(high * scale), left = seconds, length = seconds };
            if (count < Max) { pulses[count++] = p; return; }
            // full: replace the weakest
            int w = 0;
            for (int i = 1; i < count; i++) if (pulses[i].low + pulses[i].high < pulses[w].low + pulses[w].high) w = i;
            pulses[w] = p;
        }

        public void SetPaused(bool value)
        {
            paused = value;
            if (paused) Write(0f, 0f);
        }

        public void Tick(float dt)
        {
            if (paused) return;
            float low = 0f, high = 0f;
            for (int i = count - 1; i >= 0; i--)
            {
                pulses[i].left -= dt;
                if (pulses[i].left <= 0f) { pulses[i] = pulses[--count]; continue; }
                // a quick attack then a fade, so a long rumble reads as one blow, not a held buzz
                float k = pulses[i].left / pulses[i].length;
                float env = k * k;
                low = Mathf.Max(low, pulses[i].low * env);
                high = Mathf.Max(high, pulses[i].high * env);
            }
            Low = low; High = high;
            Write(low, high);
        }

        public void Stop()
        {
            count = 0;
            Low = High = 0f;
            Write(0f, 0f);
        }

        void Write(float low, float high)
        {
            var pad = Gamepad.current;
            if (pad != driven && driven != null) { driven.SetMotorSpeeds(0f, 0f); lastLow = lastHigh = -1f; }
            driven = pad;
            if (pad == null) return;
            if (Mathf.Abs(low - lastLow) < 0.01f && Mathf.Abs(high - lastHigh) < 0.01f) return;
            lastLow = low; lastHigh = high;
            pad.SetMotorSpeeds(low, high);
        }
    }
}
