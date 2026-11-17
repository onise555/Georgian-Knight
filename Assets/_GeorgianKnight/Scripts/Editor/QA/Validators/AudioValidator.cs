using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// AUDIO.* rules: sample rate, mono for positional SFX, load type vs length, and level analysis (clipping, headroom,
    /// loudness, leading silence, DC offset) measured on the WAV source — or on decoded samples for other formats when
    /// the clip decompresses on load.
    /// </summary>
    public sealed class AudioValidator : IValidator
    {
        /// <summary>
        /// AudioClip.GetData(float[], int), bound once by reflection: the container compiler (mcs) cannot resolve the
        /// overload set because of the Span&lt;float&gt; variant, and QA must compile there too.
        /// </summary>
        static readonly System.Reflection.MethodInfo GetDataArray = typeof(AudioClip).GetMethod("GetData", new[] { typeof(float[]), typeof(int) });

        public string Id { get { return "audio"; } }
        public string Category { get { return QaCategory.Audio; } }

        public IEnumerable<QaIssue> Run(QaContext ctx)
        {
            var lim = ctx.Settings.audio;
            var issues = new List<QaIssue>();
            var paths = ctx.FindAssets("t:AudioClip");
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (i % 10 == 0) ctx.Progress(path, i / (float)paths.Count);
                var ai = AssetImporter.GetAtPath(path) as AudioImporter;
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (ai == null || clip == null) continue;
                bool positional = InAny(path, lim.positionalFolders) && !InAny(path, lim.nonPositionalFolders);
                Import(path, ai, clip, positional, lim, issues);
                AudioStats s;
                if (Analyze(ctx, path, clip, ai.defaultSampleSettings.loadType, lim.silenceDb, out s))
                    Levels(path, s, positional || path.Contains("/Voice/") || path.Contains("/SFX/"), lim, issues);
                else
                    issues.Add(QaRules.AudioUnanalysed.Issue(path, 0, Path.GetExtension(path) + " source with load type " + ai.defaultSampleSettings.loadType));
            }
            return issues;
        }

        static void Import(string path, AudioImporter ai, AudioClip clip, bool positional, QaSettings.AudioLimits lim, List<QaIssue> issues)
        {
            var loadType = ai.defaultSampleSettings.loadType;
            if (clip.frequency < 44100 || clip.frequency > 48000)
                issues.Add(QaRules.AudioSampleRate.Issue(path, 0, clip.frequency + " Hz"));
            if (positional && clip.channels > 1 && !ai.forceToMono)
                issues.Add(QaRules.Audio3dStereo.Issue(path, 0, clip.channels + "-channel positional SFX without Force To Mono"));
            if (clip.length > lim.longClipSeconds && loadType == AudioClipLoadType.DecompressOnLoad)
                issues.Add(QaRules.AudioLoadType.Issue(path, 0, clip.length.ToString("0.0") + " s clip is decompressed on load (use Streaming)"));
            else if (clip.length < lim.shortClipSeconds && loadType == AudioClipLoadType.Streaming)
                issues.Add(QaRules.AudioLoadType.Issue(path, 0, "", clip.length.ToString("0.00") + " s clip is streamed (latency, file handles)", QaSeverity.Minor, null));
        }

        /// <summary>Clipping, headroom, loudness, leading silence (timing-critical clips only) and DC offset.</summary>
        static void Levels(string path, AudioStats s, bool timingCritical, QaSettings.AudioLimits lim, List<QaIssue> issues)
        {
            if (s.ClippedRuns > 0)
                issues.Add(QaRules.AudioClipping.Issue(path, 0, s.ClippedRuns + " run(s) of full-scale samples (peak " + s.PeakDb.ToString("0.0") + " dBFS)"));
            else if (s.PeakDb > lim.headroomDb)
                issues.Add(QaRules.AudioNoHeadroom.Issue(path, 0, "peak " + s.PeakDb.ToString("0.00") + " dBFS"));
            if (s.RmsDb < lim.quietRmsDb)
                issues.Add(QaRules.AudioLoudness.Issue(path, 0, "very quiet: RMS " + s.RmsDb.ToString("0.0") + " dBFS"));
            else if (s.RmsDb > lim.loudRmsDb)
                issues.Add(QaRules.AudioLoudness.Issue(path, 0, "over-compressed/loud: RMS " + s.RmsDb.ToString("0.0") + " dBFS"));
            if (timingCritical && s.LeadingSilenceMs > lim.leadingSilenceMs)
                issues.Add(QaRules.AudioLeadingSilence.Issue(path, 0, s.LeadingSilenceMs.ToString("0") + " ms of silence before the sound starts"));
            if (Mathf.Abs(s.DcOffset) > lim.dcOffsetMax)
                issues.Add(QaRules.AudioDcOffset.Issue(path, 0, "DC offset " + s.DcOffset.ToString("0.000")));
        }

        static bool InAny(string path, List<string> folders)
        {
            foreach (var f in folders) if (path.Contains(f)) return true;
            return false;
        }

        static bool Analyze(QaContext ctx, string path, AudioClip clip, AudioClipLoadType loadType, float silenceDb, out AudioStats stats)
        {
            stats = new AudioStats();
            float[] samples;
            int channels, rate;
            if (AudioMetrics.TryReadWav(Path.Combine(ctx.ProjectRoot, path), out samples, out channels, out rate))
            {
                stats = AudioMetrics.Analyze(samples, channels, rate, silenceDb);
                return true;
            }
            if (loadType != AudioClipLoadType.DecompressOnLoad || clip.samples <= 0) return false;
            if (clip.loadState != AudioDataLoadState.Loaded && !clip.LoadAudioData()) return false;
            var data = new float[clip.samples * clip.channels];
            if (GetDataArray == null || !(bool)GetDataArray.Invoke(clip, new object[] { data, 0 })) return false;
            stats = AudioMetrics.Analyze(data, clip.channels, clip.frequency, silenceDb);
            return true;
        }
    }
}
