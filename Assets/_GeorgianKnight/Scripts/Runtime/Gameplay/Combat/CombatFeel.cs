using GeorgianKnight.Core;
using Unity.Cinemachine;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Game-feel front end for combat: per-character hit-stop, global slow motion (through <see cref="ITimeService"/>,
    /// never by writing Time.timeScale), and camera impulses. Honors the player's settings (hit stop, camera shake,
    /// cinematic finishers, reduced flashes).
    /// </summary>
    public class CombatFeel : MonoBehaviour
    {
        static CombatFeel instance;
        public static CombatFeel I
        {
            get
            {
                if (instance == null)
                {
                    instance = FindAnyObjectByType<CombatFeel>();
                    if (instance == null) instance = new GameObject("[CombatFeel]").AddComponent<CombatFeel>();
                }
                return instance;
            }
        }

        public CinemachineImpulseSource impulse;

        ITimeService time;
        GameSettings settings;
        Subscriptions subs;
        int slowHandle;
        float slowScale = 1f;
        float slowUntil;

        public bool SlowMotionActive { get { return slowHandle != 0 && Time.unscaledTime < slowUntil; } }

        // The shake's impulses without garbage (GK-26): CinemachineImpulseSource makes a new signal object on every
        // impulse and builds Cinemachine's standard shapes on first use. These signals are made once and taken in turn
        // (one is reused only after 7 newer shakes; a shake lasts 0.18 s), on Cinemachine's Bump curve, key for key.
        sealed class ShakeSignal : ISignalSource6D
        {
            public Vector3 velocity;
            public float duration = 0.18f;
            public float SignalDuration { get { return duration; } }
            public void GetSignal(float timeSinceSignalStart, out Vector3 pos, out Quaternion rot)
            {
                pos = velocity * Bump.Evaluate(timeSinceSignalStart / duration);
                rot = Quaternion.identity;
            }
        }
        static AnimationCurve bump;
        static AnimationCurve Bump
        {
            get
            {
                if (bump == null)
                    bump = new AnimationCurve(new Keyframe(0f, 0f, -4.9f, -4.9f), new Keyframe(0.2f, 0f, 8.25f, 8.25f),
                                              new Keyframe(1f, 0f, -0.25f, -0.25f));
                return bump;
            }
        }
        readonly ShakeSignal[] signals = new ShakeSignal[8];
        int nextSignal;

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            Services.TryGet(out time);
            ISettingsService s;
            if (Services.TryGet(out s)) settings = s.Current;
            subs = new Subscriptions().On<SettingsChanged>(e => settings = e.Settings);
            if (impulse == null) impulse = GetComponent<CinemachineImpulseSource>();
            if (impulse == null)
            {
                impulse = gameObject.AddComponent<CinemachineImpulseSource>();
                impulse.ImpulseDefinition.ImpulseShape = CinemachineImpulseDefinition.ImpulseShapes.Bump;
                impulse.ImpulseDefinition.ImpulseDuration = 0.18f;
                impulse.ImpulseDefinition.ImpulseType = CinemachineImpulseDefinition.ImpulseTypes.Uniform;
            }
            for (int i = 0; i < signals.Length; i++) signals[i] = new ShakeSignal();
            Bump.Evaluate(0f);
        }

        void Start()
        {
            // Cinemachine pools its impulse events once they expire: a few silent ones now fill the pool before the first
            // blow (a uniform event lives its duration plus a second)
            var m = CinemachineImpulseManager.Instance;
            for (int i = 0; i < 6; i++)
            {
                var e = m.NewImpulseEvent();
                e.Envelope = new CinemachineImpulseManager.EnvelopeDefinition { SustainTime = 0.01f };
                e.SignalSource = signals[i];
                e.Radius = 9999999f; e.PropagationSpeed = 9999999f; e.DissipationDistance = 0f;
                m.AddImpulseEvent(e);
            }
        }

        void OnDestroy()
        {
            if (subs != null) subs.Dispose();
            if (time != null && slowHandle != 0) time.Release(slowHandle);
            if (instance == this) instance = null;
        }

        /// <summary>Freezes both animators for a few frames so a blow lands with weight (local, not global time).</summary>
        public void HitStop(Combatant a, Combatant b, float duration)
        {
            if (duration <= 0f || (settings != null && !settings.hitStop)) return;
            if (a != null) a.FreezeFor(duration);
            if (b != null) b.FreezeFor(duration * 1.1f);
        }

        /// <summary>
        /// Slows the world to <paramref name="scale"/> for <paramref name="realSeconds"/> (unscaled) with smooth ease in/out.
        /// A deeper or longer request replaces a weaker running one; weaker requests during a stronger one are ignored.
        /// </summary>
        public bool SlowMotion(float scale, float realSeconds, float easeIn = 0.06f, float easeOut = 0.35f)
        {
            if (settings != null && !settings.cinematicFinishers) return false;
            if (time == null) return false;
            scale = Mathf.Clamp(scale, 0.05f, 1f);
            if (SlowMotionActive && scale >= slowScale && Time.unscaledTime + realSeconds <= slowUntil) return false;
            if (slowHandle != 0) time.Release(slowHandle);
            slowHandle = time.Pulse(TimeLayer.SlowMotion, scale, realSeconds, easeIn, easeOut);
            slowScale = scale;
            slowUntil = Time.unscaledTime + easeIn + realSeconds;
            return true;
        }

        float lastFlourish = -100f;

        /// <summary>
        /// Slow motion as a reward for a rare moment (a finishing critical, a parry): at most one every
        /// <paramref name="cooldown"/> seconds (unscaled), so a flurry of them never drags the whole fight down.
        /// </summary>
        public bool Flourish(float scale, float realSeconds, float cooldown = 6f)
        {
            if (Time.unscaledTime - lastFlourish < cooldown) return false;
            if (!SlowMotion(scale, realSeconds, 0.02f, 0.25f)) return false;
            lastFlourish = Time.unscaledTime;
            return true;
        }

        public void Shake(Vector3 position, float strength)
        {
            if (impulse == null) return;
            if (settings != null) strength *= settings.cameraShake;
            if (strength <= 0.001f) return;
            Vector3 v = Random.insideUnitSphere;
            v.y = Mathf.Abs(v.y) + 0.5f;
            var def = impulse.ImpulseDefinition;
            if (def.ImpulseShape != CinemachineImpulseDefinition.ImpulseShapes.Bump
                || def.ImpulseType != CinemachineImpulseDefinition.ImpulseTypes.Uniform || def.ImpulseDuration <= 0f)
            {
                impulse.GenerateImpulseAtPositionWithVelocity(position, v.normalized * strength);
                return;
            }
            // what CinemachineImpulseDefinition.CreateAndReturnEvent does for a uniform impulse, on a reused signal
            var s = signals[nextSignal];
            nextSignal = (nextSignal + 1) % signals.Length;
            s.velocity = v.normalized * strength;
            s.duration = def.ImpulseDuration;
            var m = CinemachineImpulseManager.Instance;
            var e = m.NewImpulseEvent();
            e.Envelope = new CinemachineImpulseManager.EnvelopeDefinition { SustainTime = def.ImpulseDuration };
            e.SignalSource = s;
            e.Position = position;
            e.Radius = 9999999f;
            e.Channel = def.ImpulseChannel;
            e.DirectionMode = CinemachineImpulseManager.ImpulseEvent.DirectionModes.Fixed;
            e.DissipationDistance = 0f;
            e.PropagationSpeed = 9999999f;
            e.CustomDissipation = def.DissipationRate;
            m.AddImpulseEvent(e);
        }
    }
}
