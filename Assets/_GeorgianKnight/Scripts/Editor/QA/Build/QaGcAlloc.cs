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
    /// qa.gcalloc (GK-26): reads the profiler windows GKPerfCapture -gkprofile wrote in a development build (one .raw per
    /// wave), finds every GC.Alloc sample on the main thread and sums its bytes per callsite: the first frame of its call
    /// stack in our code (Assets/_GeorgianKnight), with the enclosing profiler marker. → GKBridge/qa_gcalloc.md, one table per
    /// file, bytes and allocations per frame, so each callsite can go to its owner with a number.
    /// </summary>
    public static class QaGcAlloc
    {
        sealed class Site
        {
            public long Bytes;
            public int Count;
            public string Stack;
        }

        [GKCommand("qa.gcalloc", "qa.gcalloc <folder|file.raw> [top]: GC.Alloc bytes per callsite from GKPerfCapture -gkprofile windows → GKBridge/qa_gcalloc.md")]
        public static void Run(string arg)
        {
            var words = (arg ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) { GKBridge.Log("qa.gcalloc: give a folder or a .raw file"); return; }
            int top = words.Length > 1 && int.TryParse(words[1], out top) ? top : 25;
            string path = words[0].Trim('"');
            var files = new List<string>();
            if (Directory.Exists(path)) files.AddRange(Directory.GetFiles(path, "*.raw"));
            else if (File.Exists(path)) files.Add(path);
            files.Sort(StringComparer.Ordinal);
            if (files.Count == 0) { GKBridge.Log("qa.gcalloc: no .raw file at " + path); return; }

            var md = new StringBuilder("# GC allocations per callsite (qa.gcalloc)\n\nMain thread, GC.Alloc samples with their call stacks; a callsite is the first frame in `Assets/_GeorgianKnight` (else the first frame) plus the enclosing profiler marker.\n");
            foreach (var f in files) Analyse(f, top, md);
            ProfilerDriver.ClearAllFrames();
            string outPath = Path.Combine(GKBridge.Dir, "qa_gcalloc.md");
            File.WriteAllText(outPath, md.ToString(), new UTF8Encoding(false));
            GKBridge.Log("qa.gcalloc: " + files.Count + " file(s) → " + outPath);
        }

        /// <summary>Everything one .raw file adds up to.</summary>
        sealed class Tally
        {
            public readonly Dictionary<string, Site> Sites = new Dictionary<string, Site>();
            public readonly List<ulong> Callstack = new List<ulong>();
            public readonly List<int> Path = new List<int>(), Remaining = new List<int>();
            public long Total;
            public int Frames, Allocs, WithStack;
        }

        static void Analyse(string file, int top, StringBuilder md)
        {
            ProfilerDriver.ClearAllFrames();
            if (!ProfilerDriver.LoadProfile(file, false)) { GKBridge.Log("qa.gcalloc: could not load " + file); return; }
            var t = new Tally();
            for (int f = ProfilerDriver.firstFrameIndex; f >= 0 && f <= ProfilerDriver.lastFrameIndex; f++)
                using (var v = ProfilerDriver.GetRawFrameDataView(f, 0))
                    if (v != null && v.valid) Walk(v, t);
            Table(file, top, t, md);
        }

        /// <summary>Depth-first over one frame's main-thread samples, keeping the chain of parents for each GC.Alloc.</summary>
        static void Walk(RawFrameDataView v, Tally t)
        {
            t.Frames++;
            int gc = v.GetMarkerId("GC.Alloc");
            t.Path.Clear(); t.Remaining.Clear();
            for (int i = 0; i < v.sampleCount; i++)
            {
                while (t.Remaining.Count > 0 && t.Remaining[t.Remaining.Count - 1] == 0) { t.Remaining.RemoveAt(t.Remaining.Count - 1); t.Path.RemoveAt(t.Path.Count - 1); }
                if (t.Remaining.Count > 0) t.Remaining[t.Remaining.Count - 1]--;
                if (v.GetSampleMarkerId(i) == gc) Add(v, i, t);
                t.Path.Add(i);
                t.Remaining.Add(v.GetSampleChildrenCount(i));
            }
        }

        static void Add(RawFrameDataView v, int sample, Tally t)
        {
            long bytes = v.GetSampleMetadataCount(sample) > 0 ? v.GetSampleMetadataAsLong(sample, 0) : 0;
            string parent = t.Path.Count > 0 ? v.GetSampleName(t.Path[t.Path.Count - 1]) : "(root)";
            string stack;
            string key = Key(v, sample, parent, t.Callstack, out stack);
            if (stack != null) t.WithStack++;
            Site s;
            if (!t.Sites.TryGetValue(key, out s)) t.Sites[key] = s = new Site { Stack = stack };
            s.Bytes += bytes; s.Count++;
            t.Total += bytes; t.Allocs++;
        }

        static void Table(string file, int top, Tally t, StringBuilder md)
        {
            var list = new List<KeyValuePair<string, Site>>(t.Sites);
            list.Sort((a, b) => b.Value.Bytes.CompareTo(a.Value.Bytes));
            double frames = Math.Max(1, t.Frames);
            md.AppendFormat(CultureInfo.InvariantCulture,
                "\n## {0}\n\n{1} frames, {2:F0} B and {3:F1} allocations per frame ({4} allocations, {5} with a call stack).\n\n| B / frame | allocs / frame | share | callsite | marker | stack (top frames) |\n|---|---|---|---|---|---|\n",
                Path.GetFileName(file), t.Frames, t.Total / frames, t.Allocs / frames, t.Allocs, t.WithStack);
            for (int i = 0; i < list.Count && i < top; i++)
            {
                var s = list[i].Value;
                string[] parts = list[i].Key.Split('\u0001');
                md.AppendFormat(CultureInfo.InvariantCulture, "| {0:F0} | {1:F2} | {2:P1} | {3} | {4} | {5} |\n",
                    s.Bytes / frames, s.Count / frames, t.Total > 0 ? s.Bytes / (double)t.Total : 0,
                    Cell(parts[0]), Cell(parts.Length > 1 ? parts[1] : ""), Cell(s.Stack ?? ""));
            }
            GKBridge.Log(string.Format(CultureInfo.InvariantCulture, "qa.gcalloc: {0}: {1} frames, {2:F0} B / frame, top: {3}",
                Path.GetFileName(file), t.Frames, t.Total / frames, list.Count > 0 ? list[0].Key.Replace('\u0001', ' ') : "none"));
        }

        /// <summary>callsite + '\u0001' + marker; <paramref name="stack"/> = the first few frames, or null without a call stack.</summary>
        static string Key(RawFrameDataView v, int sample, string parent, List<ulong> callstack, out string stack)
        {
            stack = null;
            callstack.Clear();
            v.GetSampleCallstack(sample, callstack);
            string ours = null, first = null;
            var sb = new StringBuilder();
            for (int k = 0; k < callstack.Count; k++)
            {
                var m = v.ResolveMethodInfo(callstack[k]);
                if (string.IsNullOrEmpty(m.methodName)) continue;
                if (first == null) first = m.methodName;
                if (sb.Length < 300) sb.Append(sb.Length > 0 ? " ← " : "").Append(m.methodName);
                if (ours == null && !string.IsNullOrEmpty(m.sourceFileName) && m.sourceFileName.Replace('\\', '/').Contains("_GeorgianKnight/"))
                    ours = m.methodName + " (" + ShortPath(m.sourceFileName) + ":" + m.sourceFileLine + ")";
            }
            if (sb.Length > 0) stack = sb.ToString();
            return (ours ?? first ?? "(no call stack)") + "\u0001" + parent;
        }

        static string ShortPath(string p)
        {
            p = p.Replace('\\', '/');
            int i = p.IndexOf("_GeorgianKnight/", StringComparison.Ordinal);
            return i >= 0 ? p.Substring(i + "_GeorgianKnight/".Length) : Path.GetFileName(p);
        }

        static string Cell(string s) { return s.Replace("|", "\\|").Replace("\n", " "); }
    }
}
