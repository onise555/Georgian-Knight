using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Sound for the whole fight: pooled 3D one-shots driven by combat events (swings, flesh, steel,
    /// parries, kicks, falls, voices), foot-plant footsteps with chainmail, war cries, and the valley
    /// ambience. Every combatant registered with the CombatDirector is hooked automatically.
    /// </summary>
    public class CombatAudio : MonoBehaviour
    {
        public AudioLibrary lib;
        [Range(0f, 1f)] public float master = 0.9f;
        public int voices = 32;

        static CombatAudio instance;
        public static CombatAudio I { get { return instance; } }

        readonly List<AudioSource> pool = new List<AudioSource>();
        int next;
        readonly HashSet<Combatant> hooked = new HashSet<Combatant>();
        readonly List<Feet> feet = new List<Feet>();
        float lastVoice;

        class Feet
        {
            public Combatant c; public Transform l, r; public float lh, rh; public bool lUp, rUp; public float lastStep;
        }

        void Awake()
        {
            instance = this;
            for (int i = 0; i < voices; i++)
            {
                var go = new GameObject("sfx" + i);
                go.transform.SetParent(transform, false);
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false; s.spatialBlend = 1f; s.rolloffMode = AudioRolloffMode.Logarithmic;
                s.minDistance = 2.5f; s.maxDistance = 60f; s.dopplerLevel = 0f;
                pool.Add(s);
            }
        }

        void Start()
        {
            if (lib == null) return;
            Loop(lib.ambienceValley, 0.55f);
            Loop(lib.ambienceWind, 0.32f);
            var waves = FindAnyObjectByType<WaveManager>();
            if (waves != null)
            {
                waves.WaveAnnounced += (i, w) => { var p = CombatDirector.I.Player; if (p != null) Play2D(AudioLibrary.Pick(lib.boom), 0.35f); };
                waves.Victory += () => Play2D(AudioLibrary.Pick(lib.battleCry), 0.9f);
            }
        }

        readonly List<KeyValuePair<AudioSource, float>> loops = new List<KeyValuePair<AudioSource, float>>();

        void OnEnable() { AudioMix.Changed += RefreshLoops; }
        void OnDisable() { AudioMix.Changed -= RefreshLoops; }

        void RefreshLoops()
        {
            for (int i = 0; i < loops.Count; i++)
                if (loops[i].Key != null) loops[i].Key.volume = loops[i].Value * master * AudioMix.Get(AudioCategory.Ambience);
        }

        void Loop(AudioClip clip, float vol)
        {
            if (clip == null) return;
            var s = gameObject.AddComponent<AudioSource>();
            s.clip = clip; s.loop = true; s.volume = vol * master * AudioMix.Get(AudioCategory.Ambience); s.spatialBlend = 0f; s.priority = 0;
            s.Play();
            loops.Add(new KeyValuePair<AudioSource, float>(s, vol));
        }

        public void Play(AudioClip clip, Vector3 pos, float vol = 1f, float pitchJitter = 0.06f, float pitch = 1f, AudioCategory category = AudioCategory.Sfx)
        {
            // a throw here ran inside Combatant.HitDealt and cut off every listener after it (HitFX: no blood on that hit)
            if (clip == null || pool.Count == 0) return;
            if (next >= pool.Count) next = 0;
            var s = pool[next]; next = (next + 1) % pool.Count;
            if (s == null) return;
            s.transform.position = pos;
            s.clip = clip;
            // slow motion drags the sound down with it (subtly), so finishers feel heavy
            float ts = Time.timeScale;
            if (ts > 0f && ts < 0.95f) pitch *= Mathf.Lerp(0.72f, 1f, ts);
            s.volume = vol * master * AudioMix.Get(category);
            s.pitch = pitch * (1f + Random.Range(-pitchJitter, pitchJitter));
            s.spatialBlend = 1f;
            s.Play();
        }

        public void Play2D(AudioClip clip, float vol = 1f)
        {
            if (clip == null || pool.Count == 0) return;
            if (next >= pool.Count) next = 0;
            var s = pool[next]; next = (next + 1) % pool.Count;
            if (s == null) return;
            s.clip = clip; s.volume = vol * master * AudioMix.Get(AudioCategory.Sfx); s.pitch = 1f; s.spatialBlend = 0f;
            s.Play();
        }

        void Update()
        {
            if (lib == null) return;
            var all = CombatDirector.I.all;
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c == null || hooked.Contains(c)) continue;
                Hook(c);
            }
            TickFootsteps();
        }

        void Hook(Combatant c)
        {
            hooked.Add(c);
            bool player = c.team == Team.Player;
            c.SwingStarted += a =>
            {
                bool heavy = a.kind != AttackKind.Light && a.kind != AttackKind.Riposte;
                Vector3 p = c.weapon != null && c.weapon.bladeTip != null ? c.weapon.bladeTip.position : c.transform.position + Vector3.up;
                Play(AudioLibrary.Pick(heavy ? lib.swingHeavy : lib.swingLight), p, heavy ? 0.85f : 0.7f, 0.08f, heavy ? 0.92f : 1.05f);
                if (Random.value < (heavy ? 0.8f : 0.35f)) Voice(c, lib.gruntAttack, player ? 0.55f : 0.5f);
            };
            c.HitDealt += (target, hit, r) =>
            {
                Vector3 p = hit.point;
                switch (r)
                {
                    case HitResult.Hit:
                        Play(AudioLibrary.Pick(hit.damage > 28f ? lib.fleshHeavy : lib.flesh), p, 0.9f);
                        if (target != null && Random.value < 0.75f) Voice(target, lib.gruntPain, 0.7f);
                        break;
                    case HitResult.Killed:
                        Play(AudioLibrary.Pick(lib.fleshHeavy), p, 1f, 0.05f, 0.9f);
                        if (target != null) Voice(target, lib.groanDeath, 0.8f, true);
                        break;
                    case HitResult.Blocked:
                        Play(AudioLibrary.Pick(lib.block), p, 0.85f, 0.1f);
                        break;
                    case HitResult.GuardBroken:
                        Play(AudioLibrary.Pick(lib.block), p, 1f, 0.05f, 0.85f);
                        Play(AudioLibrary.Pick(lib.kick), p, 0.6f);
                        break;
                    case HitResult.Parried:
                        Play(AudioLibrary.Pick(lib.parry), p, 1f, 0.04f);
                        Play2D(AudioLibrary.Pick(lib.whoosh), 0.25f);
                        break;
                }
                if (hit.attack != null && hit.attack.kind == AttackKind.Kick && (r == HitResult.Hit || r == HitResult.GuardBroken))
                    Play(AudioLibrary.Pick(lib.kick), p, 0.9f);
                if (hit.attack != null && hit.attack.kind == AttackKind.Execution)
                    Play2D(AudioLibrary.Pick(lib.boom), 0.7f);
            };
            c.Died += h => { if (c != null) StartCoroutine(Fall(c)); };
            c.Dodged += () => Play(AudioLibrary.Pick(lib.chainmail), c.transform.position + Vector3.up, 0.6f);
            c.StateChanged += s =>
            {
                if (s == Combatant.State.Knockdown) StartCoroutine(Fall(c));
            };
            var skills = c.GetComponent<SkillSystem>();
            if (skills != null) skills.WarCryStarted += () => Voice(c, lib.battleCry, 1f, true);

            var anim = c.GetComponent<Animator>();
            if (anim != null && anim.isHuman)
            {
                var f = new Feet { c = c, l = anim.GetBoneTransform(HumanBodyBones.LeftFoot), r = anim.GetBoneTransform(HumanBodyBones.RightFoot) };
                if (f.l != null && f.r != null) feet.Add(f);
            }
        }

        System.Collections.IEnumerator Fall(Combatant c)
        {
            yield return new WaitForSeconds(0.45f);
            if (c != null) Play(AudioLibrary.Pick(lib.bodyfall), c.transform.position, 0.85f);
        }

        void Voice(Combatant c, AudioClip[] set, float vol, bool force = false)
        {
            if (c == null) return;
            if (!force && Time.time - lastVoice < 0.18f) return;
            lastVoice = Time.time;
            float pitch = c.team == Team.Player ? 1f : (0.92f + (c.GetInstanceID() % 7) * 0.025f);
            Play(AudioLibrary.Pick(set), c.transform.position + Vector3.up * 1.6f, vol, 0.04f, pitch, AudioCategory.Voice);
        }

        void TickFootsteps()
        {
            for (int i = feet.Count - 1; i >= 0; i--)
            {
                var f = feet[i];
                if (f.c == null) { feet.RemoveAt(i); continue; }
                if (f.c.IsDead) continue;
                float speed = f.c.Motor != null ? GK.Flat(f.c.Motor.Velocity).magnitude : 0f;
                Transform root = f.c.transform;
                float lh = f.l.position.y - root.position.y, rh = f.r.position.y - root.position.y;
                Step(f, ref f.lUp, lh, speed, f.l.position);
                Step(f, ref f.rUp, rh, speed, f.r.position);
            }
        }

        void Step(Feet f, ref bool up, float h, float speed, Vector3 pos)
        {
            if (h > 0.16f) up = true;
            else if (up && h < 0.11f)
            {
                up = false;
                if (Time.time - f.lastStep < 0.16f) return;
                f.lastStep = Time.time;
                bool moving = speed > 0.4f || f.c.CurrentState == Combatant.State.Attacking || f.c.CurrentState == Combatant.State.Dodging;
                if (!moving) return;
                FeelPort.Footstep(f.c.gameObject, pos, speed, f.c.team == Team.Player);
                AudioClip[] set = speed > 5f ? lib.stepSprint : (speed > 2.5f ? lib.stepRun : lib.stepWalk);
                float vol = f.c.team == Team.Player ? 0.55f : 0.45f;
                Play(AudioLibrary.Pick(set), pos, vol, 0.08f);
                if (Random.value < 0.6f) Play(AudioLibrary.Pick(lib.chainmail), pos + Vector3.up, vol * 0.45f, 0.1f);
            }
        }
    }
}
