#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GeorgianKnight
{
    /// <summary>
    /// Effects review capture (QUALITY 3b): grabs the Game view at the end of each frame into numbered JPGs (read back
    /// asynchronously, encoded off the main thread) and, in real-time mode, taps the listener's mix. In fixed mode game
    /// time advances exactly 1/30 s per captured frame (Time.captureFramerate), so a capture can be stepped frame by
    /// frame. Started and finished by the editor command `feel_record`, which muxes the result with ffmpeg; never part of
    /// a scene or a release build.
    /// </summary>
    public sealed class FeelGrabber : MonoBehaviour
    {
        public float Fps = 30f;
        public string folder;
        public float seconds = 12f;
        public bool fixedStep;

        public bool Done { get; private set; }
        public readonly List<float> stamps = new List<float>();
        public int Frames { get { return frame; } }
        public float Length { get; private set; }
        public string AudioPath { get; private set; }

        readonly List<Task> writes = new List<Task>();
        int frame, pendingReads;
        float started, nextFrame;
        RenderTexture rt;
        Tap tap;
        bool stopping;

        void Start()
        {
            Directory.CreateDirectory(folder);
            if (!fixedStep)
            {
                var cam = Camera.main;
                var listener = cam != null ? cam.GetComponent<AudioListener>() : null;
                if (listener != null) tap = listener.gameObject.AddComponent<Tap>();
            }
            else Time.captureFramerate = (int)Fps;
            started = Time.realtimeSinceStartup;
            nextFrame = 0f;
            StartCoroutine(Grab());
        }

        public void Stop() { if (!stopping) { stopping = true; StartCoroutine(Finish()); } }

        IEnumerator Grab()
        {
            var eof = new WaitForEndOfFrame();
            while (!stopping)
            {
                yield return eof;
                float now = fixedStep ? frame / Fps : Time.realtimeSinceStartup - started;
                if (now >= seconds) { Stop(); yield break; }
                if (!fixedStep)
                {
                    if (now < nextFrame) continue;
                    nextFrame = Mathf.Max(nextFrame + 1f / Fps, now);
                }
                if (rt == null || rt.width != Screen.width || rt.height != Screen.height)
                {
                    if (rt != null) rt.Release();
                    rt = new RenderTexture(Screen.width, Screen.height, 0, GraphicsFormat.R8G8B8A8_UNorm);
                }
                ScreenCapture.CaptureScreenshotIntoRenderTexture(rt);
                int index = frame++;
                stamps.Add(now);
                pendingReads++;
                int rw = rt.width, rh = rt.height;
                string path = Path.Combine(folder, "f" + index.ToString("D5") + ".jpg");
                AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, req =>
                {
                    pendingReads--;
                    if (req.hasError) return;
                    var bytes = req.GetData<byte>().ToArray();
                    writes.Add(Task.Run(() => File.WriteAllBytes(path, ImageConversion.EncodeArrayToJPG(bytes, GraphicsFormat.R8G8B8A8_UNorm, (uint)rw, (uint)rh, 0, 92))));
                });
            }
        }

        IEnumerator Finish()
        {
            if (fixedStep) Time.captureFramerate = 0;
            float wait = Time.realtimeSinceStartup + 5f;
            while (pendingReads > 0 && Time.realtimeSinceStartup < wait) yield return null;
            Task.WaitAll(writes.ToArray());
            Length = fixedStep ? frame / Fps : Time.realtimeSinceStartup - started;
            if (tap != null) { AudioPath = Path.Combine(folder, "audio.wav"); tap.Save(AudioPath); Destroy(tap); }
            if (rt != null) rt.Release();
            Done = true;
        }

        void OnDestroy() { if (fixedStep) Time.captureFramerate = 0; }

        /// <summary>Copies the listener's output into memory (audio thread) for the review clip.</summary>
        sealed class Tap : MonoBehaviour
        {
            readonly List<float> samples = new List<float>(48000 * 2 * 30);
            readonly object gate = new object();
            int channels = 2;

            void OnAudioFilterRead(float[] data, int ch) { lock (gate) { channels = ch; samples.AddRange(data); } }

            public void Save(string path)
            {
                float[] s; int ch;
                lock (gate) { s = samples.ToArray(); ch = channels; }
                int rate = AudioSettings.outputSampleRate;
                using (var w = new BinaryWriter(File.Create(path)))
                {
                    int bytes = s.Length * 2;
                    w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                    w.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)ch); w.Write(rate);
                    w.Write(rate * ch * 2); w.Write((short)(ch * 2)); w.Write((short)16);
                    w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(bytes);
                    for (int i = 0; i < s.Length; i++) w.Write((short)Mathf.Clamp(Mathf.RoundToInt(s[i] * 32767f), -32768, 32767));
                }
            }
        }
    }
}
#endif
