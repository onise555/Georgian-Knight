using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// world_perf: what the arena's terrain details (grass, straw, short grass; Site.HayMeadow) cost in Play. Samples the
    /// profiler counters for 120 frames with the details drawn, then 120 with them off (Terrain.drawTreesAndFoliage), and
    /// logs both and the difference. The counters exist in the editor and development players; editor overhead is in
    /// the frame times, so read the difference, not the absolute value.
    /// </summary>
    static class GKArenaPerf
    {
        static readonly string[] Counters =
        {
            "CPU Main Thread Frame Time", "CPU Render Thread Frame Time", "GPU Frame Time", "Triangles Count", "Batches Count", "SetPass Calls Count",
        };

        static List<ProfilerRecorder> recs;
        static Terrain[] terrains;
        static bool[] wasDrawn;
        static float[] treeDist;
        static bool treesOnly;
        static GameObject forest;
        static int frame, phase;
        static double[][] sums;
        static int[] counts;

        [GKCommand("world_perf", "world_perf [trees]: cost of the arena's terrain details in Play (profiler counters, 120 frames with details, 120 without); trees: only the trees off (tree distance 0), the grass kept")]
        static void Command(string a)
        {
            if (!EditorApplication.isPlaying) { GKBridge.Log("world_perf: Play mode only"); return; }
            terrains = Terrain.activeTerrains;
            wasDrawn = new bool[terrains.Length];
            treeDist = new float[terrains.Length];
            treesOnly = (a ?? "").Trim() == "trees";
            forest = GameObject.Find("GreyForest");
            for (int i = 0; i < terrains.Length; i++) { wasDrawn[i] = terrains[i].drawTreesAndFoliage; treeDist[i] = terrains[i].treeDistance; }
            recs = new List<ProfilerRecorder>();
            foreach (var c in Counters) recs.Add(ProfilerRecorder.StartNew(c.EndsWith("Count") ? ProfilerCategory.Render : ProfilerCategory.Internal, c));
            sums = new[] { new double[Counters.Length], new double[Counters.Length] };
            counts = new int[Counters.Length * 2];
            frame = 0; phase = 0;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            GKBridge.Log("world_perf: sampling (" + (treesOnly ? "trees" : "details") + " on, then off) ...");
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { Finish(false); return; }
            frame++;
            if (frame > 30)   // let each state settle
                for (int i = 0; i < recs.Count; i++)
                    if (recs[i].Valid && recs[i].Count > 0) { sums[phase][i] += recs[i].LastValue; counts[phase * Counters.Length + i]++; }
            if (frame < 150) return;
            if (phase == 0)
            {
                phase = 1; frame = 0;
                foreach (var t in terrains) { if (treesOnly) t.treeDistance = 0f; else t.drawTreesAndFoliage = false; }
                if (forest != null) forest.SetActive(false);   // the greybox trees (GK.World GreyForest) count as trees and details
                return;
            }
            Finish(true);
        }

        static void Finish(bool report)
        {
            EditorApplication.update -= Tick;
            if (forest != null) forest.SetActive(true);
            for (int i = 0; terrains != null && i < terrains.Length; i++) if (terrains[i] != null) { terrains[i].drawTreesAndFoliage = wasDrawn[i]; terrains[i].treeDistance = treeDist[i]; }
            if (report)
            {
                var sb = new StringBuilder("world_perf (" + (treesOnly ? "trees" : "details") + " on / off / cost):");
                for (int i = 0; i < Counters.Length; i++)
                {
                    int n0 = counts[i], n1 = counts[Counters.Length + i];
                    if (n0 == 0 || n1 == 0) { sb.Append("\n  " + Counters[i] + ": not available"); continue; }
                    double on = sums[0][i] / n0, off = sums[1][i] / n1;
                    bool time = Counters[i].Contains("Time");
                    if (time) { on *= 1e-6; off *= 1e-6; }   // ns -> ms
                    sb.AppendFormat("\n  {0}: {1:F2} / {2:F2} / {3:+0.00;-0.00}{4}", Counters[i], on, off, on - off, time ? " ms" : "");
                }
                GKBridge.Log(sb.ToString());
            }
            if (recs != null) foreach (var r in recs) r.Dispose();
            recs = null;
        }
    }
}
