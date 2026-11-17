using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// qa.build: the player build (Docs/QA.md §13). Builds the enabled scenes of Build Settings for Windows 64 into
    /// D:\GeorgianKnight_Builds\&lt;yyyyMMdd-HHmm&gt;_&lt;commit&gt;_&lt;rel|qa|dev&gt;\. rel is the shipping build; qa is the budget
    /// build (release configuration plus the GK_QA define, which compiles in the -gkperf capture and the -gkgod cheat; the
    /// Team Lead, 2026-10-31); dev is a development build plus GK_QA, for drill-down. Writes build_info.json next to the
    /// exe (commit, tracked files modified since it, type, defines, Unity version, result), which the frame-time capture
    /// (GK.QA.Runtime GKPerfCapture) copies into every summary. The build is of the working tree: a non-zero
    /// "modified" means it is not exactly the commit.
    /// </summary>
    public static class QaBuild
    {
        const string Root = @"D:\GeorgianKnight_Builds";
        const long MinFreeBytes = 20L << 30;   // a build is ~ a few GB; keep room for several

        const string QaDefine = "GK_QA";

        [GKCommand("qa.build", "qa.build [rel|qa|dev] [profiler]: player build (Win64, enabled build scenes); qa = release + GK_QA (the budget build), dev = development + GK_QA → D:\\GeorgianKnight_Builds\\<date>_<commit>_<type>\\ + build_info.json")]
        public static void Build(string arg)
        {
            var words = new List<string>((arg ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            bool dev = words.Contains("dev"), qa = dev || words.Contains("qa");
            string why = Precheck();
            if (why != null) { GKBridge.Log("qa.build: not started: " + why); return; }

            var scenes = new List<string>();
            foreach (var s in EditorBuildSettings.scenes) if (s.enabled) scenes.Add(s.path);
            string commit = Git("rev-parse --short HEAD") ?? "nogit";
            int modified = CountLines(Git("status --porcelain --untracked-files=no"));
            string type = dev ? "dev" : qa ? "qa" : "rel";
            string[] defines = qa ? new[] { QaDefine } : new string[0];
            string dir = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + "_" + commit + "_" + type);
            var options = BuildOptions.None;
            if (dev) options |= BuildOptions.Development;
            if (dev && words.Contains("profiler")) options |= BuildOptions.ConnectWithProfiler;

            GKBridge.Log("qa.build: building " + type + " of " + commit + (modified > 0 ? " (+" + modified + " modified tracked files)" : "") +
                         ", " + scenes.Count + " scenes → " + dir);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes.ToArray(),
                locationPathName = Path.Combine(dir, PlayerSettings.productName + ".exe"),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = options,
                extraScriptingDefines = defines,
            });
            var sum = report.summary;
            if (Directory.Exists(dir)) File.WriteAllText(Path.Combine(dir, "build_info.json"), Info(commit, modified, type, options, defines, scenes, sum), new UTF8Encoding(false));
            GKBridge.Log(string.Format(CultureInfo.InvariantCulture, "qa.build: {0}: {1:F0} MB, {2:F0} s, {3} errors, {4} warnings → {5}",
                sum.result, sum.totalSize / 1048576.0, sum.totalTime.TotalSeconds, sum.totalErrors, sum.totalWarnings, dir));
            if (sum.result == BuildResult.Succeeded && qa)
                GKBridge.Log("qa.build: capture, e.g. \"" + Path.Combine(dir, PlayerSettings.productName + ".exe") +
                             "\" -gkgod -gkperf 600 -gkrun R2 -gkquality High -gkscene Level_Didgori_Arena -gkautopilot -gkuntilend -gkquit");
        }

        static string Precheck()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "the editor is in Play";
            if (EditorApplication.isCompiling) return "scripts are compiling";
            if (EditorUtility.scriptCompilationFailed) return "scripts have compile errors (Editor.log; Unity refuses to build)";
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                return "the active build target is " + EditorUserBuildSettings.activeBuildTarget + " (switching it reimports everything; ask first)";
            bool any = false;
            foreach (var s in EditorBuildSettings.scenes) any |= s.enabled;
            if (!any) return "no enabled scene in Build Settings";
            if (!Directory.Exists(Path.GetPathRoot(Root))) return "drive " + Path.GetPathRoot(Root) + " not found";
            long free = new DriveInfo(Path.GetPathRoot(Root)).AvailableFreeSpace;
            if (free < MinFreeBytes) return string.Format(CultureInfo.InvariantCulture, "{0:F1} GB free on {1}, need 20", free / 1073741824.0, Path.GetPathRoot(Root));
            return null;
        }

        static string Info(string commit, int modified, string type, BuildOptions options, string[] defines, List<string> scenes, BuildSummary sum)
        {
            var sb = new StringBuilder("{\n");
            Field(sb, "commit", commit);
            sb.Append("  \"modified\": ").Append(modified).Append(",\n");
            Field(sb, "type", type);
            Field(sb, "options", options.ToString());
            Field(sb, "defines", string.Join(";", defines));
            Field(sb, "date", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            Field(sb, "unity", Application.unityVersion);
            Field(sb, "version", PlayerSettings.bundleVersion);
            Field(sb, "backend", PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone).ToString());
            Field(sb, "quality_default", QualitySettings.names[QualitySettings.GetQualityLevel()]);
            sb.Append("  \"scenes\": [");
            for (int i = 0; i < scenes.Count; i++) sb.Append(i > 0 ? ", " : "").Append('"').Append(Esc(scenes[i])).Append('"');
            sb.Append("],\n");
            Field(sb, "result", sum.result.ToString());
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"size_mb\": {0:F1},\n  \"seconds\": {1:F0},\n  \"errors\": {2},\n  \"warnings\": {3}\n}}\n",
                            sum.totalSize / 1048576.0, sum.totalTime.TotalSeconds, sum.totalErrors, sum.totalWarnings);
            return sb.ToString();
        }

        static void Field(StringBuilder sb, string k, string v) { sb.Append("  \"").Append(k).Append("\": \"").Append(Esc(v)).Append("\",\n"); }

        static string Esc(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\""); }

        static int CountLines(string s) { return string.IsNullOrEmpty(s) ? 0 : s.Split('\n').Length; }

        /// <summary>git output (trimmed), or null when git is missing or fails.</summary>
        static string Git(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("git", args)
                {
                    WorkingDirectory = Path.GetDirectoryName(Application.dataPath),
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                    return p.ExitCode == 0 ? o.Trim() : null;
                }
            }
            catch (Exception e) { GKBridge.Log("qa.build: git " + args + " failed: " + e.Message); return null; }
        }
    }
}
