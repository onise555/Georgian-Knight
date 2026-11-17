#if GK_QA || UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using GeorgianKnight.Core;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace GeorgianKnight.QA
{
    /// <summary>
    /// Frame-time capture for player builds (Docs/QA.md §13.3). Compiled only into the editor and into builds with the
    /// GK_QA define (qa.build qa / dev); the shipping build has none of it. Does nothing unless started with -gkperf:
    ///
    ///   GeorgianKnight.exe -gkperf 600 -gkrun R2 -gkquality High -gkscene Level_Didgori_Arena -gkautopilot -gkuntilend -gkquit
    ///
    /// -gkperf [s] measuring time (default 600) from the first frame of the scene · -gkrun label · -gkquality
    /// Low|Medium|High|Ultra (default High) · -gkvsync keep vSync on (the pacing run; off by default) · -gkscene name: load
    /// it through the scene flow and measure only there · -gkautopilot the knight fights on his own (GKAutoPilot) ·
    /// -gkuntilend stop 2 s after victory or defeat · -gkwarmup s (default 5) · -gkout dir (default perf\ next to the exe) ·
    /// -gkquit quit when written · -gkprofile [frames] (development builds): per wave, 5 s after it is announced, writes
    /// that many frames (default 300) of profiler data with allocation call stacks to &lt;name&gt;_W&lt;n&gt;.raw (qa.gcalloc reads
    /// them); those frames are left out of the statistics · -gkprofileat 12,8,4 (with or instead of -gkprofile): one window
    /// the first time that many enemies are alive, timing only, to &lt;name&gt;_A&lt;n&gt;.raw (qa.mainthread reads them) ·
    /// -gkprofilegc [bytes] (default 1024): up to 8 windows of 60 frames with call stacks, each started by a frame that
    /// allocated more than that, to &lt;name&gt;_G&lt;n&gt;.raw (qa.gcalloc reads them) · -gkprofiletime s: one window with call
    /// stacks starting s seconds after the scene's first frame (-gkprofile sets its length), to &lt;name&gt;_T.raw ·
    /// -gkprofilewave s: the per-wave windows start s seconds after the announcement instead of 5.
    ///
    /// Settings go through the game's settings service at 1920 × 1080 fullscreen window, render scale 1 and a fresh
    /// player's defaults for the tier, and are not saved. Per frame into a preallocated buffer (no allocation while
    /// measuring); at the end a CSV and a summary JSON (p50 / p95 / p99 / max, hitches per minute, GC, memory) named
    /// &lt;yyyyMMdd-HHmm&gt;_&lt;commit&gt;_&lt;scene&gt;_&lt;run&gt;_&lt;quality&gt;. The commit comes from build_info.json next to the
    /// exe (qa.build). Counters a release player does not expose are written as -1 and reported as null.
    /// </summary>
    public sealed class GKPerfCapture : MonoBehaviour
    {
        struct Sample
        {
            public float t, frame, cpu, main, mainWait, render, gpu, totalMb, gcMb, gfxMb;
            public int batches, setPass, tris, verts, gcBytes, gcCount, enemies;
            public sbyte wave;
            public byte flags;
        }

        const byte Warm = 1, Drift = 2, Profiled = 4;
        const int Width = 1920, Height = 1080;

        static readonly string[] Counters =
        {
            "Batches Count", "SetPass Calls Count", "Triangles Count", "Vertices Count",                                   // Render
            "GC Allocated In Frame", "GC Allocation In Frame Count", "Total Used Memory", "GC Used Memory", "Gfx Used Memory", // Memory
        };

        float seconds = 600f, warmup = 5f;
        string run = "run", scene, outDir;
        QualityPreset quality = QualityPreset.High;
        bool vSync, autopilot, untilEnd, quit;

        Sample[] samples;
        int count, qualityIndex, loads;
        bool inScene, started, full, done;
        float start, warmUntil, endAt = -1f, sceneAt = -1f;
        string sceneName = "", outcome = "time", settingsBefore;
        readonly FrameTiming[] timing = new FrameTiming[1];
        ProfilerRecorder[] rec;
        WaveManager waves;
        GameObject player;
        DateTime stamp;
        int profileFrames, profileLeft, profileWave, settingsVSync = -1;
        float profileAt = -1f;
        const int GcWindowFrames = 60, MaxGcWindows = 8;
        int profileGcBytes, gcWindows;
        float profileTimeAt;
        float profileWaveDelay = 5f;
        int profileOverhead;   // frames after a window closed whose GC is the window's own   // -gkprofilewave: seconds after a wave is announced   // -gkprofiletime: seconds after the scene's first frame (-1 once used)
        int[] profileAtAlive;
        bool[] profileAtDone;
        string profileTag = "";
        readonly int[] aliveMeta = new int[1];

        /// <summary>Frame metadata id of the enemies-alive count in profiler captures (read by qa.mainthread).</summary>
        public static readonly Guid AliveMetaId = new Guid("8f3c1a52-6b0e-4c55-9d2a-1f4e7b9a0c31");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var args = Environment.GetCommandLineArgs();
            if (Array.IndexOf(args, "-gkperf") < 0) return;
            var go = new GameObject("[GKPerfCapture]");
            DontDestroyOnLoad(go);
            go.AddComponent<GKPerfCapture>().Configure(args);
        }

        void Configure(string[] a)
        {
            seconds = Number(a, "-gkperf", seconds);
            warmup = Number(a, "-gkwarmup", warmup);
            run = Text(a, "-gkrun") ?? run;
            scene = Text(a, "-gkscene");
            outDir = Text(a, "-gkout") ?? Path.Combine(Path.GetDirectoryName(Application.dataPath), "perf");
            QualityPreset q;
            if (Enum.TryParse(Text(a, "-gkquality") ?? "", true, out q)) quality = q;
            vSync = Array.IndexOf(a, "-gkvsync") >= 0;
            autopilot = Array.IndexOf(a, "-gkautopilot") >= 0;
            untilEnd = Array.IndexOf(a, "-gkuntilend") >= 0;
            quit = Array.IndexOf(a, "-gkquit") >= 0;
            if (Array.IndexOf(a, "-gkprofile") >= 0) profileFrames = Mathf.RoundToInt(Number(a, "-gkprofile", 300f));
            if (Array.IndexOf(a, "-gkprofilegc") >= 0)
            {
                profileGcBytes = Mathf.RoundToInt(Number(a, "-gkprofilegc", 1024f));
                if (profileFrames <= 0) profileFrames = 300;
            }
            if (Array.IndexOf(a, "-gkprofilewave") >= 0)
            {
                profileWaveDelay = Number(a, "-gkprofilewave", 5f);
                if (profileFrames <= 0) profileFrames = 300;
            }
            if (Array.IndexOf(a, "-gkprofiletime") >= 0)
            {
                profileTimeAt = Number(a, "-gkprofiletime", 20f);
                if (profileFrames <= 0) profileFrames = 300;
            }
            string at = Text(a, "-gkprofileat");
            if (at == null) return;
            var counts = new List<int>();
            foreach (var p in at.Split(','))
            {
                int n;
                if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) counts.Add(n);
            }
            profileAtAlive = counts.ToArray();
            profileAtDone = new bool[profileAtAlive.Length];
            if (profileFrames <= 0) profileFrames = 300;
        }

        void Start()
        {
            stamp = DateTime.Now;
            if (profileFrames > 0 && !Debug.isDebugBuild && !Application.isEditor)
            {
                Debug.LogWarning("[GKPerf] -gkprofile needs a development build (qa.build dev); ignored");
                profileFrames = 0;
            }
            ApplySettings();
            samples = new Sample[Mathf.Clamp(Mathf.CeilToInt(seconds * 300f), 1000, 2000000)];   // up to 300 fps
            rec = new ProfilerRecorder[Counters.Length];
            for (int i = 0; i < Counters.Length; i++)
                rec[i] = ProfilerRecorder.StartNew(i < 4 ? ProfilerCategory.Render : ProfilerCategory.Memory, Counters[i]);
            SceneManager.sceneLoaded += OnSceneLoaded;
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "[GKPerf] {0}: {1} s at {2}, vSync {3}, scene {4}, autopilot {5}",
                                    run, seconds, quality, vSync ? "on" : "off", scene ?? "(any)", autopilot ? "on" : "off"));
            if (scene != null && SceneManager.GetActiveScene().name != scene)
            {
                ISceneFlow flow;
                if (Services.TryGet(out flow)) flow.Load(scene);
                else SceneManager.LoadScene(scene);
            }
            OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
        }

        void OnDestroy() { SceneManager.sceneLoaded -= OnSceneLoaded; }

        void OnSceneLoaded(Scene s, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single) return;
            float now = Time.realtimeSinceStartup;
            inScene = scene == null || s.name == scene;
            if (inScene && !started) { started = true; start = now; sceneAt = now; }   // the time limit runs from the scene's first frame
            sceneName = s.name;
            warmUntil = now + warmup;
            waves = FindFirstObjectByType<WaveManager>();
            player = null;
            loads++;
        }

        void Update()
        {
            if (done) return;
            float now = Time.realtimeSinceStartup;
            if (inScene) Record(now);
            if (inScene && profileFrames > 0) ProfileTick(now);
            if (autopilot && inScene && player == null) Pilot();
            if (waves != null && outcome == "time" &&
                (waves.CurrentPhase == WaveManager.Phase.Victory || waves.CurrentPhase == WaveManager.Phase.Defeat))
            {
                outcome = waves.CurrentPhase.ToString();
                if (untilEnd && endAt < 0f) endAt = now + 2f;
            }
            if (started && endAt < 0f && now - start >= seconds) endAt = now;
            if (!started && now > seconds + 120f) { outcome = "scene not reached"; endAt = now; }
            if (endAt >= 0f && now >= endAt) Finish();
        }

        void Record(float now)
        {
            if (count >= samples.Length) { full = true; return; }
            FrameTimingManager.CaptureFrameTimings();
            bool ft = FrameTimingManager.GetLatestTimings(1, timing) > 0;
            ref Sample s = ref samples[count++];
            s.t = now - start;
            s.frame = Time.unscaledDeltaTime * 1000f;
            s.cpu = ft ? (float)timing[0].cpuFrameTime : -1f;
            s.main = ft ? (float)timing[0].cpuMainThreadFrameTime : -1f;
            s.mainWait = ft ? (float)timing[0].cpuMainThreadPresentWaitTime : -1f;   // inside main: waiting on the GPU / present
            s.render = ft ? (float)timing[0].cpuRenderThreadFrameTime : -1f;
            s.gpu = ft ? (float)timing[0].gpuFrameTime : -1f;
            s.batches = Value(0); s.setPass = Value(1); s.tris = Value(2); s.verts = Value(3);
            s.gcBytes = Value(4); s.gcCount = Value(5);
            s.totalMb = Mb(6); s.gcMb = Mb(7); s.gfxMb = Mb(8);
            var cd = CombatDirector.Existing;
            s.enemies = cd != null ? cd.AliveEnemies : -1;
            s.wave = (sbyte)(waves != null ? waves.WaveIndex + 1 : -1);
            bool drift = QualitySettings.GetQualityLevel() != qualityIndex || QualitySettings.vSyncCount != (vSync ? 1 : 0);
            // the GC recorder reports the previous frame, so a window's own start / stop cost (file name, log line, profiler
            // switching) lands on the frames after it: those are flagged too, and never start a GC window
            bool overhead = profileLeft > 0 || profileOverhead > 0;
            if (profileOverhead > 0) profileOverhead--;
            s.flags = (byte)((now < warmUntil ? Warm : 0) | (drift ? Drift : 0) | (overhead ? Profiled : 0));
        }

        /// <summary>
        /// Profiler windows of <see cref="profileFrames"/> frames: one per wave, 5 s after it is announced, with allocation
        /// call stacks (GC drill-down); or, with -gkprofileat, one the first time the enemies alive equal each listed count,
        /// without call stacks (timing). Every profiled frame carries the alive count as frame metadata (qa.mainthread).
        /// </summary>
        void ProfileTick(float now)
        {
            var cd = CombatDirector.Existing;
            aliveMeta[0] = cd != null ? cd.AliveEnemies : -1;
            if (profileLeft > 0)
            {
                Profiler.EmitFrameMetaData(AliveMetaId, 0, aliveMeta);
                if (--profileLeft > 0) return;
                Profiler.enabled = false;
                Profiler.enableBinaryLog = false;
                Profiler.logFile = "";
                Debug.Log("[GKPerf] profiler window " + profileTag + " written");
                profileOverhead = 2;
                return;
            }
            string tag = profileGcBytes > 0 ? GcTarget() : null;
            if (tag == null && profileTimeAt > 0f && now - start >= profileTimeAt) { profileTimeAt = -1f; tag = "T"; }
            if (tag == null && profileAtAlive != null) tag = AliveTarget(aliveMeta[0]);
            if (tag == null && profileAtAlive == null && profileGcBytes <= 0 && profileTimeAt == 0f) tag = WaveTarget(now);
            if (tag == null) return;
            profileTag = tag;
            bool timing = tag[0] == 'A';
            Directory.CreateDirectory(outDir);
            Profiler.logFile = Path.Combine(outDir, FileName() + "_" + tag + ".raw");
            Profiler.enableAllocationCallstacks = !timing;   // call stacks cost time: only for the GC drill-down
            Profiler.enableBinaryLog = true;
            Profiler.enabled = true;
            profileLeft = tag[0] == 'G' ? GcWindowFrames : profileFrames;
        }

        /// <summary>A GC burst: the frame just recorded allocated more than -gkprofilegc bytes (outside warm-up); the window
        /// catches the rest of the burst (its first frame is already past).</summary>
        string GcTarget()
        {
            if (gcWindows >= MaxGcWindows || count == 0) return null;
            var s = samples[count - 1];
            if ((s.flags & (Warm | Profiled)) != 0 || s.gcBytes <= profileGcBytes) return null;
            gcWindows++;
            return "G" + gcWindows;
        }

        string WaveTarget(float now)
        {
            int w = waves != null ? waves.WaveIndex + 1 : 0;
            if (w > profileWave) { profileWave = w; profileAt = now + profileWaveDelay; }
            if (profileAt < 0f || now < profileAt) return null;
            profileAt = -1f;
            return "W" + w;
        }

        string AliveTarget(int alive)
        {
            for (int i = 0; i < profileAtAlive.Length; i++)
                if (!profileAtDone[i] && profileAtAlive[i] == alive) { profileAtDone[i] = true; return "A" + alive; }
            return null;
        }

        int Value(int i) { return rec[i].Valid ? (int)Math.Min(rec[i].LastValue, int.MaxValue) : -1; }

        float Mb(int i) { return rec[i].Valid ? rec[i].LastValue / 1048576f : -1f; }

        void Pilot()
        {
            player = GameObject.FindWithTag("Player");
            if (player != null && player.GetComponent<GKAutoPilot>() == null) player.AddComponent<GKAutoPilot>();
        }

        void ApplySettings()
        {
            qualityIndex = Mathf.Clamp((int)quality, 0, QualitySettings.names.Length - 1);
            ISettingsService svc;
            if (Services.TryGet(out svc) && svc.Current != null)
            {
                settingsBefore = JsonUtility.ToJson(svc.Current);
                var s = svc.Current;
                s.quality = quality; s.vSync = vSync; s.frameRateCap = 0;
                s.width = Width; s.height = Height; s.fullScreenMode = FullScreenMode.FullScreenWindow; s.renderScale = 1f;
                bool light = quality <= QualityPreset.Medium;                 // a fresh player's tier defaults (SettingsService.Defaults)
                s.ambientOcclusion = !light; s.grassDensity = light ? 0.6f : 1f;
                svc.Apply();                                                  // not Commit: the player's saved settings stay as they are
                // GK-25 check (fixed in 7e218d7): the settings service must leave the requested vSync in place. The
                // summary records what Apply left (vsync_after_apply); a wrong value is set again so the run still
                // measures what was asked, and the drift counter reports any later change.
                settingsVSync = QualitySettings.vSyncCount;
                if (settingsVSync != (vSync ? 1 : 0))
                    Debug.LogWarning("[GKPerf] GK-25: the settings service left vSyncCount " + settingsVSync + "; set to " + (vSync ? 1 : 0));
                QualitySettings.vSyncCount = vSync ? 1 : 0;
                Application.targetFrameRate = -1;
                return;
            }
            QualitySettings.SetQualityLevel(qualityIndex, true);
            QualitySettings.vSyncCount = vSync ? 1 : 0;
            Application.targetFrameRate = -1;
            if (!Application.isEditor) Screen.SetResolution(Width, Height, FullScreenMode.FullScreenWindow);
        }

        void RestoreSettings()
        {
            ISettingsService svc;
            if (settingsBefore == null || !Services.TryGet(out svc) || svc.Current == null) return;
            JsonUtility.FromJsonOverwrite(settingsBefore, svc.Current);
            svc.Apply();
        }

        void Finish()
        {
            done = true;
            try { Write(); }
            catch (Exception e) { Debug.LogException(e); }
            foreach (var r in rec) r.Dispose();
            RestoreSettings();
            if (quit) Application.Quit();
        }

        // ---------------------------------------------------------------- output

        /// <summary>&lt;yyyyMMdd-HHmm&gt;_&lt;commit&gt;_&lt;scene&gt;_&lt;run&gt;_&lt;quality&gt;, stamped when the capture started.</summary>
        string FileName()
        {
            string commit = Application.isEditor ? "editor" : Match(BuildInfo(), "commit") ?? "nocommit";
            return string.Format(CultureInfo.InvariantCulture, "{0:yyyyMMdd-HHmm}_{1}_{2}_{3}_{4}", stamp, commit, Safe(sceneName), Safe(run), quality);
        }

        void Write()
        {
            if (profileLeft > 0) { Profiler.enabled = false; Profiler.enableBinaryLog = false; Profiler.logFile = ""; }
            string buildInfo = BuildInfo();
            string name = FileName();
            Directory.CreateDirectory(outDir);
            string csv = Path.Combine(outDir, name + ".csv");
            using (var w = new StreamWriter(csv, false, new UTF8Encoding(false)))
            {
                w.WriteLine("t_s,frame_ms,cpu_ms,main_ms,render_ms,gpu_ms,batches,setpass,triangles,vertices,gc_bytes,gc_count,total_mb,gc_mb,gfx_mb,enemies,wave,warmup,drift,profiled,main_wait_ms");
                var line = new StringBuilder(160);
                for (int i = 0; i < count; i++)
                {
                    var s = samples[i];
                    line.Length = 0;
                    line.AppendFormat(CultureInfo.InvariantCulture, "{0:F3},{1:F2},{2:F2},{3:F2},{4:F2},{5:F2},{6},{7},{8},{9},{10},{11},{12:F1},{13:F1},{14:F1},{15},{16},{17},{18},{19},{20:F2}",
                        s.t, s.frame, s.cpu, s.main, s.render, s.gpu, s.batches, s.setPass, s.tris, s.verts, s.gcBytes, s.gcCount,
                        s.totalMb, s.gcMb, s.gfxMb, s.enemies, s.wave, (s.flags & Warm) != 0 ? 1 : 0, (s.flags & Drift) != 0 ? 1 : 0,
                        (s.flags & Profiled) != 0 ? 1 : 0, s.mainWait);
                    w.WriteLine(line.ToString());
                }
            }
            string summary = Summary(buildInfo);
            File.WriteAllText(Path.Combine(outDir, name + ".summary.json"), summary, new UTF8Encoding(false));
            Debug.Log("[GKPerf] " + run + " " + quality + ": " + OneLine() + " → " + csv);
        }

        string Summary(string buildInfo)
        {
            var j = new Json();
            j.Str("run", run); j.Str("scene", sceneName); j.Str("quality", quality.ToString());
            j.Raw("vsync", vSync ? "true" : "false");
            j.Num("vsync_after_apply", settingsVSync);   // GK-25: must equal the request (0 when vsync is false); -1 = no settings service
            j.Str("resolution", Screen.width + "x" + Screen.height); j.Str("fullscreen", Screen.fullScreenMode.ToString());
            j.Str("device", SystemInfo.graphicsDeviceName); j.Str("driver", SystemInfo.graphicsDeviceVersion);
            j.Raw("reference_gpu", SystemInfo.graphicsDeviceName.IndexOf("5700", StringComparison.Ordinal) >= 0 ? "true" : "false");
            j.Str("cpu", SystemInfo.processorType); j.Num("ram_mb", SystemInfo.systemMemorySize);
            j.Str("build_type", Application.isEditor ? "editor" : Debug.isDebugBuild ? "dev" : "qa");
            j.Raw("build", string.IsNullOrEmpty(buildInfo) ? "null" : buildInfo.Trim());
            j.Str("outcome", outcome);
            j.Num("scene_loaded_at_s", sceneAt);
            j.Num("scene_loads", loads);

            var measured = new List<Sample>(count);
            int warm = 0, drift = 0, warmHitches = 0, profiled = 0;
            for (int i = 0; i < count; i++)
            {
                var s = samples[i];
                if ((s.flags & Drift) != 0) drift++;
                if ((s.flags & Warm) != 0) { warm++; if (s.frame > 50f) warmHitches++; continue; }
                if ((s.flags & Profiled) != 0) { profiled++; continue; }   // profiler overhead: in the .raw files, not here
                measured.Add(s);
            }
            double ms = 0; int h33 = 0, h50 = 0, gcZero = 0, gcOver1k = 0;
            foreach (var s in measured)
            {
                ms += s.frame;
                if (s.frame > 33.3f) h33++;
                if (s.frame > 50f) h50++;
                if (s.gcBytes == 0) gcZero++;
                if (s.gcBytes > 1024) gcOver1k++;
            }
            double minutes = ms / 60000.0;
            j.Num("frames", measured.Count); j.Num("warmup_frames", warm); j.Num("profiled_frames", profiled); j.Num("measured_s", ms / 1000.0);
            j.Raw("buffer_full", full ? "true" : "false"); j.Num("settings_drift_frames", drift);
            j.Raw("frame_ms", Stats(measured, s => s.frame));
            j.Raw("cpu_ms", Stats(measured, s => s.cpu));
            j.Raw("main_ms", Stats(measured, s => s.main));
            j.Raw("main_wait_ms", Stats(measured, s => s.mainWait));
            j.Raw("main_work_ms", Stats(measured, s => s.main >= 0f && s.mainWait >= 0f ? s.main - s.mainWait : -1f));   // the §13.4 main-thread budget
            j.Raw("render_ms", Stats(measured, s => s.render));
            j.Raw("gpu_ms", Stats(measured, s => s.gpu));
            j.Num("hitches_33_per_min", minutes > 0 ? h33 / minutes : 0);
            j.Num("hitches_50_per_min", minutes > 0 ? h50 / minutes : 0);
            j.Num("warmup_hitches_50", warmHitches);
            j.Raw("batches", Stats(measured, s => s.batches));
            j.Raw("setpass", Stats(measured, s => s.setPass));
            j.Raw("triangles", Stats(measured, s => s.tris));
            bool gc = measured.Count > 0 && measured[0].gcBytes >= 0;
            j.Raw("gc_zero_share", gc ? (gcZero / (double)measured.Count).ToString("F4", CultureInfo.InvariantCulture) : "null");
            j.Raw("gc_over_1k_frames", gc ? gcOver1k.ToString(CultureInfo.InvariantCulture) : "null");
            j.Raw("gc_top5", gc ? Top5(measured) : "null");
            j.Raw("memory_mb", "{ \"total\": " + Span(s => s.totalMb) + ", \"gc\": " + Span(s => s.gcMb) + ", \"gfx\": " + Span(s => s.gfxMb) + " }");
            j.Num("capture_buffer_mb", samples.Length * System.Runtime.InteropServices.Marshal.SizeOf(typeof(Sample)) / 1048576.0);
            return j.Close();
        }

        /// <summary>p50 / p95 / p99 / max of the values that exist (≥ 0); null when the counter is not available here.</summary>
        static string Stats(List<Sample> list, Func<Sample, float> get)
        {
            var v = new List<float>(list.Count);
            foreach (var s in list) { float x = get(s); if (x >= 0f) v.Add(x); }
            if (v.Count == 0) return "null";
            v.Sort();
            return string.Format(CultureInfo.InvariantCulture, "{{ \"p50\": {0:0.###}, \"p95\": {1:0.###}, \"p99\": {2:0.###}, \"max\": {3:0.###} }}",
                                 Rank(v, 0.50), Rank(v, 0.95), Rank(v, 0.99), v[v.Count - 1]);
        }

        static float Rank(List<float> sorted, double p) { return sorted[Mathf.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)]; }

        static string Top5(List<Sample> list)
        {
            var top = new List<Sample>(list);
            top.Sort((a, b) => b.gcBytes.CompareTo(a.gcBytes));
            var sb = new StringBuilder("[");
            for (int i = 0; i < top.Count && i < 5; i++)
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0}{{ \"t\": {1:F2}, \"bytes\": {2}, \"wave\": {3} }}", i > 0 ? ", " : "", top[i].t, top[i].gcBytes, top[i].wave);
            return sb.Append("]").ToString();
        }

        /// <summary>start / peak / end over the whole capture (warm-up included: memory is a trend, not a percentile).</summary>
        string Span(Func<Sample, float> get)
        {
            if (count == 0 || get(samples[0]) < 0f) return "null";
            float peak = 0f;
            for (int i = 0; i < count; i++) peak = Mathf.Max(peak, get(samples[i]));
            return string.Format(CultureInfo.InvariantCulture, "{{ \"start\": {0:F1}, \"peak\": {1:F1}, \"end\": {2:F1} }}", get(samples[0]), peak, get(samples[count - 1]));
        }

        string OneLine()
        {
            var v = new List<float>(count);
            for (int i = 0; i < count; i++) if ((samples[i].flags & (Warm | Profiled)) == 0) v.Add(samples[i].frame);
            if (v.Count == 0) return "no frames measured (" + outcome + ")";
            v.Sort();
            return string.Format(CultureInfo.InvariantCulture, "{0} frames, frame p50 {1:F2} / p95 {2:F2} / p99 {3:F2} ms, outcome {4}",
                                 v.Count, Rank(v, 0.5), Rank(v, 0.95), Rank(v, 0.99), outcome);
        }

        static string BuildInfo()
        {
            try
            {
                string p = Path.Combine(Path.GetDirectoryName(Application.dataPath), "build_info.json");
                return File.Exists(p) ? File.ReadAllText(p) : null;
            }
            catch (Exception e) { Debug.LogWarning("[GKPerf] build_info.json unreadable: " + e.Message); return null; }
        }

        static string Match(string json, string key)
        {
            if (json == null) return null;
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        static string Safe(string s) { return Regex.Replace(string.IsNullOrEmpty(s) ? "none" : s, "[^A-Za-z0-9_.-]", "_"); }

        /// <summary>The value after <paramref name="key"/>, or null when the key is absent or has no value.</summary>
        static string Text(string[] a, string key)
        {
            int i = Array.IndexOf(a, key);
            return i >= 0 && i + 1 < a.Length && !a[i + 1].StartsWith("-", StringComparison.Ordinal) ? a[i + 1] : null;
        }

        static float Number(string[] a, string key, float fallback)
        {
            float f;
            return float.TryParse(Text(a, key), NumberStyles.Float, CultureInfo.InvariantCulture, out f) && f > 0f ? f : fallback;
        }

        /// <summary>Flat JSON object writer (invariant culture; values from <see cref="Raw"/> are inserted as they are).</summary>
        sealed class Json
        {
            readonly StringBuilder sb = new StringBuilder("{\n");
            bool first = true;

            void Key(string k) { sb.Append(first ? "  \"" : ",\n  \"").Append(k).Append("\": "); first = false; }
            public void Raw(string k, string v) { Key(k); sb.Append(v); }
            public void Num(string k, double v) { Key(k); sb.Append(v.ToString("0.###", CultureInfo.InvariantCulture)); }
            public void Str(string k, string v) { Key(k); sb.Append('"').Append((v ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"'); }
            public string Close() { return sb.Append("\n}\n").ToString(); }
        }
    }
}
#endif
