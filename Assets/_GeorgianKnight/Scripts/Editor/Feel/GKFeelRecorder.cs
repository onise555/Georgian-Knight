using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// `feel_record`: the review capture of the Game view. The runtime <see cref="FeelGrabber"/> grabs the frames (a
    /// component must live in a runtime assembly to be added in Play mode); this side waits for it, then ffmpeg muxes
    /// the frames by their timestamps, with the sound in real-time mode, into GKBridge/feel/&lt;name&gt;.mp4. Fixed mode
    /// steps game time 1/30 s per frame for frame-by-frame review (no sound).
    /// </summary>
    public static class GKFeelRecorder
    {
        static FeelGrabber active;
        static string activeName;

        [GKCommand("feel_record", "feel_record <name> [seconds=12] [fixed|fixed60] | stop: capture the Game view (and its sound) to GKBridge/feel/<name>.mp4; fixed = every frame at exactly 30 fps game time, no sound, for frame-by-frame review (play mode)")]
        static void Command(string arg)
        {
            if (!EditorApplication.isPlaying) { GKBridge.Log("feel_record: play mode only"); return; }
            var parts = (arg ?? "").Trim().Split(' ');
            if (parts[0] == "stop") { if (active != null) active.Stop(); else GKBridge.Log("feel_record: not recording"); return; }
            if (active != null) { GKBridge.Log("feel_record: already recording " + activeName); return; }
            string n = string.IsNullOrEmpty(parts[0]) ? "feel" : parts[0];
            float s = 12f;
            if (parts.Length > 1) float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out s);
            string dir = Path.Combine(GKBridge.Dir, "feel", n);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            var go = new GameObject("[FeelGrabber]");

            active = go.AddComponent<FeelGrabber>();
            active.folder = dir;
            active.seconds = Mathf.Clamp(s, 1f, 120f);
            bool f60 = System.Array.IndexOf(parts, "fixed60") > 0;
            active.fixedStep = f60 || System.Array.IndexOf(parts, "fixed") > 0;
            active.Fps = f60 ? 60f : 30f;
            activeName = n;
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
            GKBridge.Log("feel_record: recording " + n + " for " + active.seconds.ToString("F0", CultureInfo.InvariantCulture) + " s (" + Screen.width + "x" + Screen.height + (active.fixedStep ? ", fixed " + active.Fps + " fps" : ", real time with sound") + ")");
        }

        static void Poll()
        {
            if (active == null)
            {
                EditorApplication.update -= Poll;
                if (activeName != null) GKBridge.Log("feel_record: " + activeName + " ended without finishing (Play mode left?)");
                activeName = null;
                return;
            }
            if (!active.Done) return;
            EditorApplication.update -= Poll;
            var g = active; string name = activeName;
            active = null; activeName = null;
            Mux(g, name);
            Object.Destroy(g.gameObject);
        }

        static void Mux(FeelGrabber g, string name)
        {
            var list = new StringBuilder();
            int n = 0;
            for (int i = 0; i < g.stamps.Count; i++)
            {
                string f = Path.Combine(g.folder, "f" + i.ToString("D5") + ".jpg");
                if (!File.Exists(f)) continue;
                float d = (i + 1 < g.stamps.Count ? g.stamps[i + 1] : g.Length) - g.stamps[i];
                list.Append("file '").Append(f.Replace('\\', '/')).Append("'\n");
                list.Append("duration ").Append(Mathf.Max(0.001f, d).ToString("F4", CultureInfo.InvariantCulture)).Append('\n');
                n++;
            }
            string listPath = Path.Combine(g.folder, "frames.txt");
            File.WriteAllText(listPath, list.ToString());
            string mp4 = Path.Combine(GKBridge.Dir, "feel", name + ".mp4");
            string wav = g.AudioPath;
            // D3D readback is bottom-up
            string flip = SystemInfo.graphicsUVStartsAtTop ? "vflip," : "";
            string args = "-y -f concat -safe 0 -i \"" + listPath + "\"" + (wav != null ? " -i \"" + wav + "\"" : "") +
                          " -vf \"" + flip + "fps=" + (g.fixedStep ? g.Fps : 30f) + ",scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p\" -c:v libx264 -crf 16 -preset medium" +
                          (wav != null ? " -c:a aac -b:a 192k -shortest" : "") + " \"" + mp4 + "\"";
            int code = -1; string err = "";
            try
            {
                var p = Process.Start(new ProcessStartInfo("ffmpeg", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
                err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                code = p.ExitCode;
            }
            catch (System.Exception e) { err = e.Message; }
            GKBridge.Log("feel_record: " + name + " " + n + " frames, " + g.Length.ToString("F1", CultureInfo.InvariantCulture) + " s -> " +
                         (code == 0 ? mp4 : "ffmpeg failed (" + code + "): " + (err.Length > 400 ? err.Substring(err.Length - 400) : err)) +
                         (flip.Length > 0 ? " (frames on disk are upside down; the mp4 is flipped)" : ""));
        }
    }
}
