using System.Collections.Generic;
using System.IO;
using GeorgianKnight.Feel;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Wires the generated sound layers in VFX/Audio (ElevenLabs SFX v2, trimmed and peak-normalised to -1 dBFS mono)
    /// into the settings asset. A moment whose layers a designer already set is left alone.
    /// </summary>
    public static class GKFeelSounds
    {
        const string Dir = GKFeelBuilder.Root + "/Audio";

        public static void Assign(FeelSettings s)
        {
            Import();
            int filled = 0;
            filled += Fill(s.heavyHit, Layer("SFX_HeavyHit_Thump", 0.75f, 0.92f, 1.05f, 0f, false, false));
            filled += Fill(s.kill, Layer("SFX_HeavyHit_Thump", 0.8f, 0.85f, 0.95f, 0f, false, false),
                                   Layer("SFX_Kill_Boom", 0.55f, 0.95f, 1.05f, 0.02f, true, true));
            filled += Fill(s.execution, Layer("SFX_HeavyHit_Thump", 0.9f, 0.8f, 0.9f, 0f, false, false),
                                        Layer("SFX_Kill_Boom", 0.8f, 0.9f, 1f, 0.03f, true, true));
            filled += Fill(s.parry, Layer("SFX_Parry_Ring", 0.6f, 0.97f, 1.06f, 0.025f, false, false));
            filled += Fill(s.guardBreak, Layer("SFX_GuardBreak_Crunch", 0.85f, 0.94f, 1.04f, 0f, false, false));
            filled += Fill(s.block, Layer("SFX_Block_Scrape", 0.4f, 0.95f, 1.08f, 0.035f, false, false));
            filled += Fill(s.heavySwing, Layer("SFX_HeavySwing_Air", 0.5f, 0.92f, 1.06f, 0f, false, false));
            if (s.footsteps.sounds == null || s.footsteps.sounds.Length == 0)
            {
                var grit = Layer("SFX_Step_Grit", 0.3f, 0.9f, 1.1f, 0f, false, false);
                if (grit != null) { s.footsteps.sounds = new[] { grit }; filled++; }
            }
            GKBridge.Log("feel_build: sound layers filled for " + filled + " moments");
        }

        static int Fill(ImpactProfile p, params SoundLayer[] layers)
        {
            if (p.sounds != null && p.sounds.Length > 0) return 0;
            var ok = new List<SoundLayer>();
            foreach (var l in layers) if (l != null) ok.Add(l);
            if (ok.Count == 0) return 0;
            p.sounds = ok.ToArray();
            return 1;
        }

        static SoundLayer Layer(string prefix, float volume, float pitchMin, float pitchMax, float delay, bool flat, bool playerOnly)
        {
            var clips = new List<AudioClip>();
            for (int i = 1; i <= 8; i++)
            {
                var c = AssetDatabase.LoadAssetAtPath<AudioClip>(Dir + "/" + prefix + "_" + i + ".wav");
                if (c != null) clips.Add(c);
            }
            if (clips.Count == 0) { GKBridge.Log("feel_build: no clips for " + prefix); return null; }
            return new SoundLayer { clips = clips.ToArray(), volume = volume, pitch = new Vector2(pitchMin, pitchMax), delay = delay, flat = flat, playerOnly = playerOnly };
        }

        /// <summary>Short one-shots: decompressed on load (no first-play hitch), mono, PCM (they are tiny).</summary>
        static void Import()
        {
            string abs = Path.Combine(Path.GetDirectoryName(Application.dataPath), Dir);
            if (!Directory.Exists(abs)) return;
            foreach (var f in Directory.GetFiles(abs, "*.wav"))
            {
                string path = Dir + "/" + Path.GetFileName(f);
                var imp = AssetImporter.GetAtPath(path) as AudioImporter;
                if (imp == null) continue;
                var st = imp.defaultSampleSettings;
                if (st.loadType == AudioClipLoadType.DecompressOnLoad && st.compressionFormat == AudioCompressionFormat.PCM && imp.forceToMono) continue;
                st.loadType = AudioClipLoadType.DecompressOnLoad;
                st.compressionFormat = AudioCompressionFormat.PCM;
                st.preloadAudioData = true;
                imp.defaultSampleSettings = st;
                imp.forceToMono = true;
                imp.SaveAndReimport();
            }
        }
    }
}
