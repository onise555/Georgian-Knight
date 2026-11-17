using System;
using System.IO;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>Level statistics of one clip.</summary>
    public struct AudioStats
    {
        public float PeakDb;
        public float RmsDb;
        public int ClippedRuns;           // runs of >= 3 consecutive samples at full scale
        public float LeadingSilenceMs;
        public float TrailingSilenceMs;
        public float DcOffset;
        public float Seconds;
        public int Channels;
        public int SampleRate;
    }

    /// <summary>
    /// Decodes PCM/float WAV files and measures peak, RMS, clipping, leading silence and DC offset. Pure C# so the
    /// audio validator can analyse sources without depending on import settings, and tests can feed synthetic signals.
    /// </summary>
    public static class AudioMetrics
    {
        public const float FullScale = 0.999f;

        public static float ToDb(float linear) { return 20f * (float)Math.Log10(Math.Max(1e-9, linear)); }

        public static AudioStats Analyze(float[] interleaved, int channels, int sampleRate, float silenceDb)
        {
            var s = new AudioStats { Channels = channels, SampleRate = sampleRate, PeakDb = -180f, RmsDb = -180f };
            if (interleaved == null || interleaved.Length == 0 || channels <= 0 || sampleRate <= 0) return s;
            int frames = interleaved.Length / channels;
            s.Seconds = frames / (float)sampleRate;
            float threshold = (float)Math.Pow(10.0, silenceDb / 20.0);
            double sumSq = 0, sum = 0;
            float peak = 0f;
            int run = 0;
            int firstLoud = -1, lastLoud = -1;
            for (int f = 0; f < frames; f++)
            {
                bool loud = false;
                for (int c = 0; c < channels; c++)
                {
                    float x = interleaved[f * channels + c];
                    float a = Math.Abs(x);
                    sum += x;
                    sumSq += x * x;
                    if (a > peak) peak = a;
                    if (a >= threshold) loud = true;
                    if (a >= FullScale)
                    {
                        run++;
                        if (run == 3) s.ClippedRuns++;
                    }
                    else run = 0;
                }
                if (loud) { if (firstLoud < 0) firstLoud = f; lastLoud = f; }
            }
            int n = frames * channels;
            s.PeakDb = ToDb(peak);
            s.RmsDb = ToDb((float)Math.Sqrt(sumSq / n));
            s.DcOffset = (float)(sum / n);
            if (firstLoud < 0) { s.LeadingSilenceMs = s.Seconds * 1000f; s.TrailingSilenceMs = s.Seconds * 1000f; }
            else
            {
                s.LeadingSilenceMs = firstLoud * 1000f / sampleRate;
                s.TrailingSilenceMs = (frames - 1 - lastLoud) * 1000f / sampleRate;
            }
            return s;
        }

        /// <summary>
        /// Reads a RIFF/WAVE file (PCM 8/16/24/32-bit or IEEE float 32-bit, incl. WAVE_FORMAT_EXTENSIBLE) into
        /// interleaved floats. Returns false for anything else (compressed WAV, other containers).
        /// </summary>
        public static bool TryReadWav(byte[] data, out float[] samples, out int channels, out int sampleRate)
        {
            samples = null; channels = 0; sampleRate = 0;
            if (data == null || data.Length < 44 || Ascii(data, 0) != "RIFF" || Ascii(data, 8) != "WAVE") return false;
            int format, bits, dataStart, dataLength;
            ReadChunks(data, out format, out channels, out sampleRate, out bits, out dataStart, out dataLength);
            if (dataStart < 0 || channels <= 0 || sampleRate <= 0 || (format != 1 && format != 3) || (format == 3 && bits != 32)) return false;
            if (bits != 8 && bits != 16 && bits != 24 && bits != 32) return false;
            int bytes = bits / 8;
            samples = new float[dataLength / bytes];
            for (int i = 0; i < samples.Length; i++) samples[i] = Sample(data, dataStart + i * bytes, bits, format == 3);
            return true;
        }

        /// <summary>Walks the RIFF chunks: format (PCM=1, float=3, extensible resolved to its sub-format) and the data range.</summary>
        static void ReadChunks(byte[] data, out int format, out int channels, out int sampleRate, out int bits, out int dataStart, out int dataLength)
        {
            format = channels = sampleRate = bits = dataLength = 0;
            dataStart = -1;
            int pos = 12;
            while (pos + 8 <= data.Length)
            {
                string id = Ascii(data, pos);
                int size = BitConverter.ToInt32(data, pos + 4);
                int body = pos + 8;
                if (size < 0 || body + size > data.Length) size = data.Length - body;
                if (id == "fmt " && size >= 16)
                {
                    format = BitConverter.ToUInt16(data, body);
                    channels = BitConverter.ToUInt16(data, body + 2);
                    sampleRate = BitConverter.ToInt32(data, body + 4);
                    bits = BitConverter.ToUInt16(data, body + 14);
                    if (format == 0xFFFE && size >= 26) format = BitConverter.ToUInt16(data, body + 24);
                }
                else if (id == "data") { dataStart = body; dataLength = size; }
                pos = body + size + (size & 1);
            }
        }

        static float Sample(byte[] d, int o, int bits, bool isFloat)
        {
            switch (bits)
            {
                case 8: return (d[o] - 128) / 128f;
                case 16: return BitConverter.ToInt16(d, o) / 32768f;
                case 24:
                    {
                        int v = d[o] | (d[o + 1] << 8) | (d[o + 2] << 16);
                        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                        return v / 8388608f;
                    }
                default: return isFloat ? BitConverter.ToSingle(d, o) : BitConverter.ToInt32(d, o) / 2147483648f;
            }
        }

        public static bool TryReadWav(string path, out float[] samples, out int channels, out int sampleRate)
        {
            samples = null; channels = 0; sampleRate = 0;
            if (!File.Exists(path) || !path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return false;
            return TryReadWav(File.ReadAllBytes(path), out samples, out channels, out sampleRate);
        }

        static string Ascii(byte[] d, int o)
        {
            return o + 4 <= d.Length ? new string(new[] { (char)d[o], (char)d[o + 1], (char)d[o + 2], (char)d[o + 3] }) : "";
        }
    }
}
