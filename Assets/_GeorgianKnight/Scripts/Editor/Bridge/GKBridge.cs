using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>Marks a static editor method <c>void M(string arg)</c> or <c>bool M(string arg)</c> as a bridge command.
    /// Returning true defers the remaining queued commands until after the next domain reload.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class GKCommandAttribute : Attribute
    {
        public readonly string Name;
        public readonly string Help;
        public GKCommandAttribute(string name, string help = "") { Name = name; Help = help; }
    }

    /// <summary>
    /// File-based remote control for the editor (lives in its own assembly so it keeps working even when game code
    /// does not compile). Several automation sessions share it:
    /// - each owner drops commands, one per line, into &lt;project&gt;/GKBridge/requests/&lt;owner&gt;.txt (or &lt;owner&gt;~&lt;id&gt;.txt);
    ///   the old GKBridge/request.txt still works and runs as owner "legacy". Every file is one batch; batches run in
    ///   arrival order and a batch is never interleaved with another one, even across play-mode domain reloads.
    /// - lock &lt;owner&gt; &lt;minutes&gt; [reason] / unlock &lt;owner&gt; [force] / lockstatus: a lease in GKBridge/lock.json.
    ///   While it is held, other owners' batches wait in queue.txt; it expires on its own (max 120 min). A lock request
    ///   older than 90 s (it sat through a stall and its sender gave up) is refused. A request file that holds only
    ///   "cancel" drops the sender's queued commands.
    /// - log.txt gets every line as "HH:mm:ss [owner] msg"; log-&lt;owner&gt;.txt only that owner's lines.
    /// - heartbeat.txt is rewritten every second while the editor ticks (focus, compiling, play, queue, lock), so a
    ///   caller can tell a busy bridge from a stalled one: commands only run while Unity is not compiling or importing.
    /// Other logs: console.txt (errors), warnings.txt, compile.txt, alive.txt.
    /// Built-in commands: refresh, reimport, menu, play, stop, status, unpause, steps, timescale, save, open, commands,
    /// lock, unlock, lockstatus. Everything else is discovered from [GKCommand] methods in any editor assembly.
    /// </summary>
    [InitializeOnLoad]
    public static class GKBridge
    {
        public static readonly string Dir;
        static readonly string RequestsDir;
        const string LegacyOwner = "legacy";
        const string ActiveKey = "GKBridge.activeBatch";
        const string LastOwnerKey = "GKBridge.lastOwner";
        const string PlayByKey = "GKBridge.playBy";               // owner whose "play" started the current Play session
        const double MaxLeaseMinutes = 120;
        static double nextPoll, nextBeat;
        static bool busy;
        static readonly object fileLock = new object();
        static Dictionary<string, MethodInfo> commands;
        static int stepsRemaining;
        static double lastStep;
        static string stepsOwner;

        /// <summary>One queued command. A batch is one request file; its commands stay together in the queue.</summary>
        struct Entry { public string batch, owner, cmd; }
        static List<Entry> queue = new List<Entry>();
        static int batchCounter;
        static string current;                                   // owner of the running command; Log() goes to it
        static string currentBatch;
        static double lastCommandEnd = -100;
        const double PlayAttributionSeconds = 5;
        const double MaxLockRequestAge = 90;                     // older lock requests were sent while the editor stalled
        static string lastOwner;                                 // late logs (async work, stepping) go to the last owner
        static readonly HashSet<string> told = new HashSet<string>();   // waiting batches already told about the lease

        class Lease { public string owner, reason; public DateTime since, until; }
        static Lease lease;

        static GKBridge()
        {
            Dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "GKBridge");
            RequestsDir = Path.Combine(Dir, "requests");
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(RequestsDir);
            lastOwner = SessionState.GetString(LastOwnerKey, "bridge");
            LoadQueue();
            LoadLease();
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.update -= StepTick;
            EditorApplication.update += StepTick;
            CompilationPipeline.assemblyCompilationFinished -= OnCompiled;
            CompilationPipeline.assemblyCompilationFinished += OnCompiled;
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.playModeStateChanged += OnPlayMode;
            File.WriteAllText(Path.Combine(Dir, "alive.txt"), DateTime.Now.ToString("HH:mm:ss") + " domain loaded, playing=" + EditorApplication.isPlaying);
        }

        /// <summary>Logs to log.txt and to the log of the owner whose command is running.</summary>
        public static void Log(string msg) { LogTo(current ?? lastOwner ?? "bridge", msg); }

        static void LogTo(string owner, string msg)
        {
            string t = DateTime.Now.ToString("HH:mm:ss");
            Append("log.txt", t + " [" + owner + "] " + msg);
            Append("log-" + owner + ".txt", t + " " + msg);
            Debug.Log("[GK] " + msg);
        }

        static void Append(string file, string line)
        {
            try { lock (fileLock) File.AppendAllText(Path.Combine(Dir, file), line + "\n"); } catch { }
        }

        static void OnLog(string msg, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                Append("console.txt", DateTime.Now.ToString("HH:mm:ss") + " [" + type + "] " + msg + "\n" + Trim(stack));
            else if (type == LogType.Warning && msg.Length < 600)
                Append("warnings.txt", DateTime.Now.ToString("HH:mm:ss") + " " + msg);
        }

        static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > 1500 ? s.Substring(0, 1500) : s;
        }

        static void OnCompiled(string assembly, CompilerMessage[] messages)
        {
            var sb = new StringBuilder();
            int errors = 0;
            foreach (var m in messages)
            {
                if (m.type == CompilerMessageType.Error) errors++;
                if (m.type == CompilerMessageType.Error || m.message.Contains("GeorgianKnight"))
                    sb.AppendLine(m.type + " " + m.file + ":" + m.line + " " + m.message);
            }
            Append("compile.txt", DateTime.Now.ToString("HH:mm:ss") + " " + Path.GetFileName(assembly) + " errors=" + errors + "\n" + sb);
        }

        /// <summary>Play that starts during a bridge command, or within a few seconds after one (commands such as
        /// intro_playcheck set isPlaying from a delayCall), belongs to that command's owner; anything else is the user.</summary>
        static void OnPlayMode(PlayModeStateChange s)
        {
            if (s == PlayModeStateChange.EnteredEditMode) SessionState.EraseString(PlayByKey);
            else if (s == PlayModeStateChange.ExitingEditMode && SessionState.GetString(PlayByKey, "").Length == 0)
            {
                string by = current ?? (EditorApplication.timeSinceStartup - lastCommandEnd < PlayAttributionSeconds ? lastOwner : null);
                if (by != null) SessionState.SetString(PlayByKey, by);
            }
        }

        /// <summary>Who started the current Play session: a bridge owner, or "user" (the Play button) — then nobody else may stop it.</summary>
        static string PlayBy()
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode) return "-";
            string by = SessionState.GetString(PlayByKey, "");
            return by.Length > 0 ? by : "user";
        }

        /// <summary>Drives the player loop frame by frame while the editor is unfocused (remote play-tests).</summary>
        static void StepTick()
        {
            if (stepsRemaining <= 0 || !EditorApplication.isPlaying) return;
            if (EditorApplication.timeSinceStartup - lastStep < 0.012) return;
            lastStep = EditorApplication.timeSinceStartup;
            if (!EditorApplication.isPaused) EditorApplication.isPaused = true;
            EditorApplication.Step();
            stepsRemaining--;
            if (stepsRemaining == 0) LogTo(stepsOwner ?? lastOwner, "stepping done, frame=" + Time.frameCount + " t=" + Time.time.ToString("F1"));
        }

        static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now >= nextBeat) { nextBeat = now + 1.0; Beat(null); }
            if (busy || now < nextPoll) return;
            if (stepsRemaining > 0 && EditorApplication.isPlaying) return;     // queued commands wait for stepping to finish
            nextPoll = now + 1.0;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            ExpireLease();
            Ingest();
            if (queue.Count == 0) return;
            busy = true;
            try { Pump(); }
            finally
            {
                busy = false;
                current = null;
                EditorUtility.ClearProgressBar();
            }
        }

        // ---------------------------------------------------------------- queue

        /// <summary>Moves every request file into the queue, oldest first. A file still being written is left for the next tick.</summary>
        static void Ingest()
        {
            var files = new List<FileInfo>();
            try
            {
                var legacy = new FileInfo(Path.Combine(Dir, "request.txt"));
                if (legacy.Exists) files.Add(legacy);
                foreach (var f in new DirectoryInfo(RequestsDir).GetFiles())
                    if (f.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && !f.Name.StartsWith(".")) files.Add(f);
            }
            catch { return; }
            if (files.Count == 0) return;
            files.Sort((a, b) => a.LastWriteTimeUtc != b.LastWriteTimeUtc ? a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc) : string.CompareOrdinal(a.Name, b.Name));
            bool changed = false;
            foreach (var f in files)
            {
                string owner = f.Name == "request.txt" && f.DirectoryName == Dir ? LegacyOwner : OwnerOf(f.Name);
                if (owner == null)
                {
                    LogTo("bridge", "ignored request file with a bad owner name: requests/" + f.Name + " (use a-z 0-9 _ -)");
                    try { File.Move(f.FullName, f.FullName + ".bad"); } catch { }
                    continue;
                }
                if ((DateTime.UtcNow - f.LastWriteTimeUtc).TotalMilliseconds < 150) continue;
                // move first: a writer that comes later creates a fresh file instead of appending to one being read
                string taking = f.FullName + ".taking";
                string[] lines;
                try { File.Move(f.FullName, taking); }
                catch { continue; }
                try { lines = File.ReadAllLines(taking); }
                catch { try { File.Move(taking, f.FullName); } catch { } continue; }
                try { File.Delete(taking); } catch { }
                // the id starts with the time the caller wrote the file, so a request that sat through a stall can be told apart
                string batch = f.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "-" + (batchCounter++);
                if (lines.Length == 1 && lines[0].Trim() == "cancel") { Cancel(owner); changed = true; continue; }
                foreach (var l in lines)
                {
                    string cmd = l.Trim();
                    if (cmd.Length == 0 || cmd.StartsWith("#")) continue;
                    queue.Add(new Entry { batch = batch, owner = owner, cmd = cmd.Replace('\t', ' ') });
                    changed = true;
                }
            }
            if (changed) SaveQueue();
        }

        /// <summary>A request file holding only "cancel" drops that owner's queued commands (not a batch already running),
        /// so a session that gave up waiting leaves nothing behind to run later.</summary>
        static void Cancel(string owner)
        {
            string active = SessionState.GetString(ActiveKey, "");
            int n = queue.RemoveAll(e => e.owner == owner && e.batch != active);
            LogTo(owner, "cancel: dropped " + n + " queued command(s)" + (ActiveOwner() == owner ? "; the batch already running goes on" : ""));
        }

        static double AgeSeconds(string batch)
        {
            long ticks;
            int dash = batch == null ? -1 : batch.IndexOf('-');
            if (dash <= 0 || !long.TryParse(batch.Substring(0, dash), NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) return 0;
            return (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds;
        }

        static string OwnerOf(string fileName)
        {
            string n = fileName.Substring(0, fileName.Length - 4);
            int t = n.IndexOf('~');
            if (t >= 0) n = n.Substring(0, t);
            n = n.ToLowerInvariant();
            return Regex.IsMatch(n, "^[a-z0-9][a-z0-9_-]{0,31}$") ? n : null;
        }

        static void Pump()
        {
            for (int guard = 0; guard < 50; guard++)
            {
                RunControlHeads();
                string batch = PickBatch();
                if (batch == null || !RunBatch(batch)) return;
            }
        }

        static bool IsHead(int i) { return i == 0 || queue[i - 1].batch != queue[i].batch; }
        static bool Allowed(string owner) { return lease == null || lease.owner == owner; }
        static bool IsControl(string cmd)
        {
            string c = cmd.Split(' ')[0];
            return c == "lock" || c == "unlock" || c == "lockstatus";
        }

        /// <summary>lock / unlock / lockstatus at the head of a waiting owner's first batch answer at once, so a refused
        /// lock comes back quickly instead of queueing behind the lease it asks about.</summary>
        static void RunControlHeads()
        {
            string active = SessionState.GetString(ActiveKey, "");
            for (int i = 0; i < queue.Count; i++)
            {
                var e = queue[i];
                // only the owner's first batch: a later "unlock" must not overtake that owner's own earlier work
                if (e.batch == active || Allowed(e.owner) || !IsHead(i) || !IsControl(e.cmd) || FirstBatchOf(e.owner) != e.batch) continue;
                queue.RemoveAt(i);
                SaveQueue();
                current = e.owner;
                currentBatch = e.batch;
                Log("> " + e.cmd);
                try { Run(e.cmd); }
                catch (Exception ex) { Log("EXCEPTION in '" + e.cmd + "': " + ex.Message); }
                current = null;
                i--;
            }
        }

        static string FirstBatchOf(string owner)
        {
            foreach (var e in queue) if (e.owner == owner) return e.batch;
            return null;
        }

        /// <summary>The batch to run: the one already started (it survives play-mode reloads), else the oldest one whose
        /// owner may run under the current lease. Owners left waiting are told once per batch.</summary>
        static string PickBatch()
        {
            string active = SessionState.GetString(ActiveKey, "");
            if (active.Length > 0)
            {
                foreach (var e in queue) if (e.batch == active) return active;
                SessionState.EraseString(ActiveKey);
            }
            for (int i = 0; i < queue.Count; i++)
            {
                if (!IsHead(i)) continue;
                var e = queue[i];
                if (Allowed(e.owner)) return e.batch;
                if (told.Add(e.batch)) LogTo(e.owner, "waiting: lock " + Describe(lease) + "; " + Count(e.batch) + " command(s) queued");
            }
            return null;
        }

        static int Count(string batch)
        {
            int n = 0;
            foreach (var e in queue) if (e.batch == batch) n++;
            return n;
        }

        /// <summary>Runs a batch until it is done (true) or a command defers the rest (false).</summary>
        static bool RunBatch(string batch)
        {
            SessionState.SetString(ActiveKey, batch);
            string owner = null;
            bool deferred = false;
            while (true)
            {
                int i = queue.FindIndex(x => x.batch == batch);
                if (i < 0) break;
                var e = queue[i];
                queue.RemoveAt(i);
                SaveQueue();
                owner = current = lastOwner = e.owner;
                currentBatch = e.batch;
                SessionState.SetString(LastOwnerKey, owner);
                Beat(e);
                Log("> " + e.cmd);
                bool deferRest = false;
                try { deferRest = Run(e.cmd); }
                catch (Exception ex) { Log("EXCEPTION in '" + e.cmd + "': " + (ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex)); }
                lastCommandEnd = EditorApplication.timeSinceStartup;
                if (deferRest || EditorApplication.isCompiling)
                {
                    int rest = Count(batch);
                    if (rest > 0) Log("deferred " + rest + " command(s)");
                    deferred = true;
                    break;
                }
            }
            if (Count(batch) == 0) SessionState.EraseString(ActiveKey);
            if (owner != null) Log("idle");
            current = null;
            return !deferred;
        }

        static string QueuePath { get { return Path.Combine(Dir, "queue.txt"); } }

        static void LoadQueue()
        {
            queue.Clear();
            try
            {
                if (!File.Exists(QueuePath)) return;
                foreach (var l in File.ReadAllLines(QueuePath))
                {
                    var p = l.Split(new[] { '\t' }, 3);
                    if (p.Length == 3) queue.Add(new Entry { batch = p[0], owner = p[1], cmd = p[2] });
                }
            }
            catch (Exception e) { Debug.LogWarning("[GKBridge] could not read queue.txt: " + e.Message); }
        }

        static void SaveQueue()
        {
            try
            {
                if (queue.Count == 0) { File.Delete(QueuePath); return; }
                var lines = new string[queue.Count];
                for (int i = 0; i < queue.Count; i++) lines[i] = queue[i].batch + "\t" + queue[i].owner + "\t" + queue[i].cmd;
                File.WriteAllLines(QueuePath, lines);
            }
            catch (Exception e) { Debug.LogWarning("[GKBridge] could not write queue.txt: " + e.Message); }
        }

        static string Owners()
        {
            var counts = new Dictionary<string, int>();
            var order = new List<string>();
            foreach (var e in queue)
            {
                if (!counts.ContainsKey(e.owner)) { counts[e.owner] = 0; order.Add(e.owner); }
                counts[e.owner]++;
            }
            var parts = new List<string>();
            foreach (var o in order) parts.Add(o + ":" + counts[o]);
            return parts.Count == 0 ? "" : "(" + string.Join(",", parts.ToArray()) + ")";
        }

        static string ActiveOwner()
        {
            string active = SessionState.GetString(ActiveKey, "");
            if (active.Length > 0) foreach (var e in queue) if (e.batch == active) return e.owner;
            return "none";
        }

        /// <summary>heartbeat.txt: one line, rewritten every second while the editor ticks and before each command.</summary>
        static void Beat(Entry? running)
        {
            try
            {
                string line = "time=" + DateTime.Now.ToString("HH:mm:ss") + " epoch=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() +
                    " focused=" + UnityEditorInternal.InternalEditorUtility.isApplicationActive + " compiling=" + EditorApplication.isCompiling +
                    " updating=" + EditorApplication.isUpdating + " playing=" + EditorApplication.isPlaying + " playBy=" + PlayBy() + " paused=" + EditorApplication.isPaused +
                    " stepping=" + stepsRemaining + " queue=" + queue.Count + Owners() + " active=" + ActiveOwner() +
                    " lock=" + (lease == null ? "none" : lease.owner) +
                    (running.HasValue ? " running=" + running.Value.owner + ": " + running.Value.cmd : "");
                File.WriteAllText(Path.Combine(Dir, "heartbeat.txt"), line + "\n");
            }
            catch { }
        }

        // ---------------------------------------------------------------- lease

        static string LockPath { get { return Path.Combine(Dir, "lock.json"); } }

        static void LoadLease()
        {
            lease = null;
            try
            {
                if (!File.Exists(LockPath)) return;
                var f = new Dictionary<string, string>();
                foreach (Match m in Regex.Matches(File.ReadAllText(LockPath), "\"(\\w+)\"\\s*:\\s*\"?([^\",\\n]*)\"?"))
                    f[m.Groups[1].Value] = m.Groups[2].Value.Trim();
                string owner, reason, since, until;
                if (!f.TryGetValue("owner", out owner) || !f.TryGetValue("untilEpoch", out until)) return;
                f.TryGetValue("reason", out reason);
                f.TryGetValue("sinceEpoch", out since);
                lease = new Lease
                {
                    owner = owner, reason = reason ?? "",
                    since = DateTimeOffset.FromUnixTimeSeconds(long.Parse(since ?? until, CultureInfo.InvariantCulture)).UtcDateTime,
                    until = DateTimeOffset.FromUnixTimeSeconds(long.Parse(until, CultureInfo.InvariantCulture)).UtcDateTime,
                };
            }
            catch (Exception e) { Debug.LogWarning("[GKBridge] could not read lock.json: " + e.Message); lease = null; }
        }

        static void SaveLease()
        {
            try
            {
                if (lease == null) { File.Delete(LockPath); return; }
                File.WriteAllText(LockPath,
                    "{\n" +
                    "  \"owner\": \"" + lease.owner + "\",\n" +
                    "  \"reason\": \"" + lease.reason + "\",\n" +
                    "  \"since\": \"" + lease.since.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "\",\n" +
                    "  \"until\": \"" + lease.until.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "\",\n" +
                    "  \"sinceEpoch\": " + new DateTimeOffset(lease.since).ToUnixTimeSeconds() + ",\n" +
                    "  \"untilEpoch\": " + new DateTimeOffset(lease.until).ToUnixTimeSeconds() + "\n" +
                    "}\n");
            }
            catch (Exception e) { Debug.LogWarning("[GKBridge] could not write lock.json: " + e.Message); }
        }

        static void ExpireLease()
        {
            if (lease == null || DateTime.UtcNow < lease.until) return;
            LogTo(lease.owner, "lock: lease of " + lease.owner + " expired");
            lease = null;
            SaveLease();
            told.Clear();
        }

        static string Describe(Lease l)
        {
            if (l == null) return "free";
            var left = l.until - DateTime.UtcNow;
            return "held by " + l.owner + " until " + l.until.ToLocalTime().ToString("HH:mm:ss") +
                   " (" + (int)left.TotalMinutes + "m" + left.Seconds.ToString("00") + "s left)" + (l.reason.Length > 0 ? " - " + l.reason : "");
        }

        static void Lock(string arg)
        {
            var p = arg.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            double minutes;
            if (p.Length < 2 || !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out minutes) || minutes <= 0)
            { Log("lock: refused, usage: lock <owner> <minutes> [reason]"); return; }
            string owner = p[0].ToLowerInvariant();
            if (owner != current) { Log("lock: refused, owner '" + owner + "' is not the sender '" + current + "' (send from requests/" + owner + ".txt)"); return; }
            double age = AgeSeconds(currentBatch);
            if (age > MaxLockRequestAge) { Log("lock: refused, the request is " + (int)age + "s old (sent while the editor was busy or stalled); send it again"); return; }
            ExpireLease();
            if (lease != null && lease.owner != owner) { Log("lock: refused, " + Describe(lease)); return; }
            bool renew = lease != null;
            string reason = p.Length > 2 ? Regex.Replace(p[2], "[\"\\\\\\r\\n]", "'") : renew ? lease.reason : "";
            lease = new Lease
            {
                owner = owner, reason = reason,
                since = renew ? lease.since : DateTime.UtcNow,
                until = DateTime.UtcNow.AddMinutes(Math.Min(minutes, MaxLeaseMinutes)),
            };
            SaveLease();
            told.Clear();
            Log("lock: granted" + (renew ? " (renewed)" : "") + ", " + Describe(lease));
        }

        static void Unlock(string arg)
        {
            var p = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string owner = p.Length > 0 ? p[0].ToLowerInvariant() : current;
            bool force = p.Length > 1 && p[1] == "force";
            ExpireLease();
            if (lease == null) { Log("unlock: no lease held"); return; }
            if (lease.owner != owner) { Log("unlock: refused, " + Describe(lease) + " (not " + owner + ")"); return; }
            if (owner != current && !force) { Log("unlock: refused, sender '" + current + "' is not '" + owner + "' (add 'force' to break it)"); return; }
            lease = null;
            SaveLease();
            told.Clear();
            Log("unlock: released " + owner + (owner != current ? " (forced by " + current + ")" : ""));
            if (owner != current) LogTo(owner, "lock: your lease was force-released by " + current);
        }

        static string LockStatus()
        {
            string by = PlayBy();
            return "lockstatus: " + Describe(lease) + " | queue=" + queue.Count + Owners() + " active=" + ActiveOwner() +
                   (by == "user" ? " | USER IS PLAYING (Play started from the editor, not the bridge): do not stop or enter Play" : "") +
                   " | playing=" + EditorApplication.isPlaying + " playBy=" + by + " focused=" + UnityEditorInternal.InternalEditorUtility.isApplicationActive +
                   " | commands run only while Unity is not compiling/importing; unfocused Unity ticks slowly or stalls";
        }

        // ---------------------------------------------------------------- commands

        static Dictionary<string, MethodInfo> Commands()
        {
            if (commands != null) return commands;
            commands = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in TypeCache.GetMethodsWithAttribute<GKCommandAttribute>())
            {
                if (!m.IsStatic) continue;
                var ps = m.GetParameters();
                if (ps.Length != 1 || ps[0].ParameterType != typeof(string)) { Debug.LogError("[GKBridge] bad command signature " + m.DeclaringType + "." + m.Name); continue; }
                var a = (GKCommandAttribute)m.GetCustomAttributes(typeof(GKCommandAttribute), false)[0];
                commands[a.Name] = m;
            }
            return commands;
        }

        static bool Run(string cmd)
        {
            // "play" reloads the domain: anything after it must run in the new domain
            if (cmd == "play" || cmd.StartsWith("play ")) SessionState.SetString(PlayByKey, current ?? lastOwner);
            if (cmd == "play") { Application.runInBackground = true; PlayerSettings.runInBackground = true; EditorApplication.isPlaying = true; return true; }
            string arg = "";
            int sp = cmd.IndexOf(' ');
            if (sp > 0) { arg = cmd.Substring(sp + 1).Trim(); cmd = cmd.Substring(0, sp); }
            switch (cmd)
            {
                case "refresh":
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    return EditorApplication.isCompiling;
                case "reimport":
                    AssetDatabase.ImportAsset(arg, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ImportRecursive);
                    return EditorApplication.isCompiling;
                case "menu": EditorApplication.ExecuteMenuItem(arg); return false;
                case "recompile":
                    CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
                    return true;
                case "play": Application.runInBackground = true; PlayerSettings.runInBackground = true; EditorApplication.isPlaying = true; return false;
                case "stop": EditorApplication.isPlaying = false; return false;
                case "unpause": EditorApplication.isPaused = false; return false;
                case "steps": stepsRemaining = int.Parse(arg); stepsOwner = current; return true;          // later commands run after the frames
                case "wait": nextPoll = EditorApplication.timeSinceStartup + double.Parse(arg, CultureInfo.InvariantCulture); return true;
                case "timescale": Time.timeScale = float.Parse(arg, CultureInfo.InvariantCulture); return false;
                case "save": UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes(); return false;
                case "open": UnityEditor.SceneManagement.EditorSceneManager.OpenScene(arg); return false;
                case "lock": Lock(arg); return false;
                case "unlock": Unlock(arg); return false;
                case "lockstatus": Log(LockStatus()); return false;
                case "status":
                    Log("playing=" + EditorApplication.isPlaying + " paused=" + EditorApplication.isPaused + " timeScale=" + Time.timeScale +
                        " frame=" + Time.frameCount + " t=" + Time.time.ToString("F1") + " focused=" + UnityEditorInternal.InternalEditorUtility.isApplicationActive +
                        " commands=" + Commands().Count + " lock=" + (lease == null ? "none" : lease.owner) + " playBy=" + PlayBy());
                    return false;
                case "commands":
                    {
                        var names = new List<string>(Commands().Keys); names.Sort();
                        Log("commands: " + string.Join(", ", names.ToArray()));
                        return false;
                    }
            }
            MethodInfo m;
            if (!Commands().TryGetValue(cmd, out m)) { Log("unknown command " + cmd); return false; }
            object r = m.Invoke(null, new object[] { arg });
            return (r is bool && (bool)r) || EditorApplication.isCompiling;
        }
    }
}
