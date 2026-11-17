using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace GeorgianKnight.Feel
{
    /// <summary>
    /// The sound layers of a moment, played on top of the combat mix (which CombatAudio owns): a sub-bass thump under a
    /// heavy hit, a metallic ring-out after a parry, grit under a sprinting step. Pooled sources; a delayed layer waits
    /// in real time so hit-stop and slow motion do not stretch it.
    /// </summary>
    public sealed class FeelSound
    {
        const int Voices = 16;
        readonly List<AudioSource> pool = new List<AudioSource>(Voices);
        int next;

        struct Pending { public SoundLayer layer; public Vector3 at; public float due, volume; public AudioMixerGroup group; }
        readonly List<Pending> pending = new List<Pending>();

        /// <summary>The player's sfx volume (master is applied globally by the settings service); unused for layers routed to a mixer group.</summary>
        public float Volume = 1f;
        /// <summary>Layers started since start (tests).</summary>
        public int Played { get; private set; }

        public FeelSound(Transform owner)
        {
            var fs = FeelSettings.Active;
            var root = new GameObject("FeelSound").transform;
            root.SetParent(owner, false);
            for (int i = 0; i < Voices; i++)
            {
                var s = new GameObject("layer" + i).AddComponent<AudioSource>();
                s.transform.SetParent(root, false);
                s.playOnAwake = false;
                s.rolloffMode = AudioRolloffMode.Logarithmic;
                s.minDistance = fs.soundMinDistance;
                s.maxDistance = fs.soundMaxDistance;
                s.dopplerLevel = 0f;
                pool.Add(s);
            }
        }

        public void Play(SoundLayer[] layers, Vector3 at, bool playerInvolved, float scale, AudioMixerGroup group)
        {
            if (layers == null) return;
            for (int i = 0; i < layers.Length; i++)
            {
                var l = layers[i];
                if (l == null || l.clips == null || l.clips.Length == 0) continue;
                if (l.playerOnly && !playerInvolved) continue;
                if (l.delay > 0f) pending.Add(new Pending { layer = l, at = at, due = Time.unscaledTime + l.delay, volume = scale, group = group });
                else Start(l, at, scale, group);
            }
        }

        public void Tick()
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (Time.unscaledTime < pending[i].due) continue;
                Start(pending[i].layer, pending[i].at, pending[i].volume, pending[i].group);
                pending.RemoveAt(i);
            }
        }

        void Start(SoundLayer l, Vector3 at, float scale, AudioMixerGroup group)
        {
            var clip = l.clips[Random.Range(0, l.clips.Length)];
            if (clip == null) return;
            var s = pool[next];
            next = (next + 1) % pool.Count;
            s.Stop();
            s.clip = clip;
            s.spatialBlend = l.flat ? 0f : 1f;
            s.transform.position = at;
            // routed through Audio's mixer (C13), the group applies the player's sfx volume; unrouted, we do
            s.outputAudioMixerGroup = group;
            s.volume = Mathf.Clamp01(l.volume * scale * (group != null ? 1f : Volume));
            // an array element added in the inspector starts at (0, 0): treat it as unpitched, not silent
            s.pitch = l.pitch.x <= 0f && l.pitch.y <= 0f ? 1f : Random.Range(Mathf.Max(0.1f, Mathf.Min(l.pitch.x, l.pitch.y)), Mathf.Max(l.pitch.x, l.pitch.y));
            s.Play();
            Played++;
        }
    }
}
