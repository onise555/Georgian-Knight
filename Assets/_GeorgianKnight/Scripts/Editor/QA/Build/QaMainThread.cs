using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor.Profiling;
using UnityEditorInternal;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// qa.mainthread: main-thread time per profiler marker, by enemies alive, from the windows GKPerfCapture -gkprofileat
    /// wrote in a development build (each frame carries the alive count as frame metadata). Per marker: total time (outermost
    /// occurrence, children included) and self time, ms per frame at each alive count, and the slope per extra enemy
    /// between the lowest and the highest count → GKBridge/qa_mainthread.md. Development-build times run above the release
    /// ones (profiler markers on); read the shares and the slopes, not the absolute frame.
    /// </summary>
    public static class QaMainThread
    {
        /// <summary>The same id as GKPerfCapture.AliveMetaId (GK.QA.Runtime, not referenced from the editor assembly).</summary>
        static readonly Guid AliveMetaId = new Guid("8f3c1a52-6b0e-4c55-9d2a-1f4e7b9a0c31");

        sealed class Bucket
        {
            public int Frames;
            public double Main;
            public readonly Dictionary<string, double[]> Markers = new Dictionary<string, double[]>();   // [total, self]
        }

        sealed class Walker
        {
            public readonly List<int> Index = new List<int>(), Remaining = new List<int>(), Marker = new List<int>();
            public readonly List<double> Self = new List<double>();
            public readonly Dictionary<int, int> OnStack = new Dictionary<int, int>();
            public readonly Dictionary<int, string> Names = new Dictionary<int, string>();
        }

        [GKCommand("qa.mainthread", "qa.mainthread <folder|file.raw> [top]: main-thread ms per marker by enemies alive (GKPerfCapture -gkprofileat windows) → GKBridge/qa_mainthread.md")]
        public static void Run(string arg)
        {
            var words = (arg ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) { GKBridge.Log("qa.mainthread: give a folder or a .raw file"); return; }
            int top = words.Length > 1 && int.TryParse(words[1], out top) ? top : 40;
            string path = words[0].Trim('"');
            var files = new List<string>();
            if (Directory.Exists(path)) files.AddRange(Directory.GetFiles(path, "*_A*.raw"));
            else if (File.Exists(path)) files.Add(path);
            if (files.Count == 0) { GKBridge.Log("qa.mainthread: no *_A*.raw file at " + path); return; }

            var buckets = new SortedDictionary<int, Bucket>();
            foreach (var f in files) Load(f, buckets);
            ProfilerDriver.ClearAllFrames();
            string outPath = Path.Combine(GKBridge.Dir, "qa_mainthread.md");
            File.WriteAllText(outPath, Report(files, buckets, top), new UTF8Encoding(false));
            var sb = new StringBuilder("qa.mainthread: ");
            foreach (var b in buckets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0} alive: {1:F2} ms ({2} frames); ", b.Key, b.Value.Main / Math.Max(1, b.Value.Frames), b.Value.Frames);
            GKBridge.Log(sb.Append("→ ").Append(outPath).ToString());
        }

        static void Load(string file, SortedDictionary<int, Bucket> buckets)
        {
            ProfilerDriver.ClearAllFrames();
            if (!ProfilerDriver.LoadProfile(file, false)) { GKBridge.Log("qa.mainthread: could not load " + file); return; }
            var w = new Walker();
            for (int f = ProfilerDriver.firstFrameIndex; f >= 0 && f <= ProfilerDriver.lastFrameIndex; f++)
                using (var v = ProfilerDriver.GetRawFrameDataView(f, 0))
                {
                    if (v == null || !v.valid) continue;
                    var meta = v.GetFrameMetaData<int>(AliveMetaId, 0);
                    int alive = meta.IsCreated && meta.Length > 0 ? meta[0] : -1;
                    Bucket b;
                    if (!buckets.TryGetValue(alive, out b)) buckets[alive] = b = new Bucket();
                    Walk(v, w, b);
                }
        }

        /// <summary>Depth-first over one frame: total time for the outermost sample of each marker, self = time minus children.</summary>
        static void Walk(RawFrameDataView v, Walker w, Bucket b)
        {
            b.Frames++;
            w.Index.Clear(); w.Remaining.Clear(); w.Marker.Clear(); w.Self.Clear(); w.OnStack.Clear();
            for (int i = 0; i < v.sampleCount; i++)
            {
                PopFinished(v, w, b);
                double t = v.GetSampleTimeMs(i);
                int marker = v.GetSampleMarkerId(i);
                if (w.Remaining.Count > 0) { w.Remaining[w.Remaining.Count - 1]--; w.Self[w.Self.Count - 1] -= t; }
                else b.Main += t;
                int depthOfSame;
                if (!w.OnStack.TryGetValue(marker, out depthOfSame) || depthOfSame == 0) Add(b, Name(v, w, marker), 0, t);
                w.OnStack[marker] = (w.OnStack.TryGetValue(marker, out depthOfSame) ? depthOfSame : 0) + 1;
                w.Index.Add(i); w.Remaining.Add(v.GetSampleChildrenCount(i)); w.Marker.Add(marker); w.Self.Add(t);
            }
            while (w.Index.Count > 0) Pop(v, w, b);
        }

        static void PopFinished(RawFrameDataView v, Walker w, Bucket b)
        {
            while (w.Remaining.Count > 0 && w.Remaining[w.Remaining.Count - 1] == 0) Pop(v, w, b);
        }

        static void Pop(RawFrameDataView v, Walker w, Bucket b)
        {
            int last = w.Index.Count - 1, marker = w.Marker[last];
            Add(b, Name(v, w, marker), 1, w.Self[last]);
            w.OnStack[marker]--;
            w.Index.RemoveAt(last); w.Remaining.RemoveAt(last); w.Marker.RemoveAt(last); w.Self.RemoveAt(last);
        }

        static string Name(RawFrameDataView v, Walker w, int marker)
        {
            string n;
            if (!w.Names.TryGetValue(marker, out n)) w.Names[marker] = n = v.GetMarkerName(marker);
            return n;
        }

        static void Add(Bucket b, string name, int slot, double ms)
        {
            double[] a;
            if (!b.Markers.TryGetValue(name, out a)) b.Markers[name] = a = new double[2];
            a[slot] += ms;
        }

        static string Report(List<string> files, SortedDictionary<int, Bucket> buckets, int top)
        {
            var keys = new List<int>();
            foreach (var k in buckets.Keys) if (k >= 0 && buckets[k].Frames > 0) keys.Add(k);
            var sb = new StringBuilder("# Main thread by enemies alive (qa.mainthread)\n\nFiles: ");
            foreach (var f in files) sb.Append('`').Append(Path.GetFileName(f)).Append("` ");
            sb.Append("\n\nDevelopment build with profiler markers: read the shares and the slopes. Total = the marker's outermost sample with its children; self = without them.\n\n| Alive | frames | main thread ms / frame |\n|---|---|---|\n");
            foreach (var k in keys) sb.AppendFormat(CultureInfo.InvariantCulture, "| {0} | {1} | {2:F2} |\n", k, buckets[k].Frames, buckets[k].Main / buckets[k].Frames);
            if (keys.Count == 0) return sb.ToString();
            int lo = keys[0], hi = keys[keys.Count - 1];
            sb.Append("\n| Marker | ");
            foreach (var k in keys) sb.Append(k).Append(" alive total (self) | ");
            sb.Append("ms per extra enemy |\n|---|");
            foreach (var k in keys) sb.Append("---|");
            sb.Append("---|\n");
            var names = new List<string>(buckets[hi].Markers.Keys);
            names.Sort((a, b) => Ms(buckets[hi], b, 0).CompareTo(Ms(buckets[hi], a, 0)));
            for (int i = 0; i < names.Count && i < top; i++)
            {
                sb.Append("| ").Append(names[i].Replace("|", "\\|")).Append(" | ");
                foreach (var k in keys) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:F3} ({1:F3}) | ", Ms(buckets[k], names[i], 0), Ms(buckets[k], names[i], 1));
                double slope = hi > lo ? (Ms(buckets[hi], names[i], 0) - Ms(buckets[lo], names[i], 0)) / (hi - lo) : 0;
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0:+0.000;-0.000} |\n", slope);
            }
            return sb.ToString();
        }

        /// <summary>ms per frame of a marker in a bucket: slot 0 total, 1 self.</summary>
        static double Ms(Bucket b, string name, int slot)
        {
            double[] a;
            return b.Frames > 0 && b.Markers.TryGetValue(name, out a) ? a[slot] / b.Frames : 0;
        }
    }
}
