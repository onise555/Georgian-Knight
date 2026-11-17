using System.Linq;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// clipscan &lt;clip&gt;[,&lt;clip&gt;...]: samples Mixamo clips on the clean knight in the editor (no game code, no
    /// layers, no pose fixes) at 60 fps and reports, per bone, the largest rotation between two frames. A step the
    /// popqa probe flags in play mode that also shows here is in the clip itself; one that does not comes from the
    /// runtime (blending, layers, procedural fixes).
    /// </summary>
    public static class GKClipScan
    {
        [GKCommand("clipscan", "clipscan <clip>[,<clip>...]: largest per-frame bone rotation of each clip sampled on the clean knight at 60 fps (edit mode)")]
        static void Cmd(string arg)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("clipscan: stop play mode first"); return; }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKHeroCC.CleanPrefabPath);
            if (prefab == null) { GKBridge.Log("clipscan: no Knight_Clean"); return; }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                var anim = go.GetComponent<Animator>();
                var bones = new List<KeyValuePair<string, Transform>>();
                for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
                {
                    var b = (HumanBodyBones)i;
                    if (b == HumanBodyBones.Hips || b == HumanBodyBones.Jaw || b == HumanBodyBones.LeftEye || b == HumanBodyBones.RightEye) continue;
                    if (i >= (int)HumanBodyBones.LeftThumbProximal && i <= (int)HumanBodyBones.RightLittleDistal) continue;
                    var t = anim.GetBoneTransform(b);
                    if (t != null) bones.Add(new KeyValuePair<string, Transform>(b.ToString(), t));
                }
                foreach (var name in (arg ?? "").Split(','))
                {
                    var clip = GKPreview.Clip(name.Trim());
                    if (clip == null) { GKBridge.Log("clipscan: no clip " + name); continue; }
                    int n = Mathf.CeilToInt(clip.length * 60f);
                    var last = new Quaternion[bones.Count];
                    var worst = new float[bones.Count];
                    var worstAt = new int[bones.Count];
                    for (int f = 0; f <= n; f++)
                    {
                        clip.SampleAnimation(go, Mathf.Min(clip.length, f / 60f));
                        for (int i = 0; i < bones.Count; i++)
                        {
                            var q = bones[i].Value.localRotation;
                            if (f > 0)
                            {
                                float a = Quaternion.Angle(last[i], q);
                                if (a > worst[i]) { worst[i] = a; worstAt[i] = f; }
                            }
                            last[i] = q;
                        }
                    }
                    var sb = new StringBuilder("clipscan " + clip.name + " (" + clip.length.ToString("F2") + " s):");
                    var order = new List<int>();
                    for (int i = 0; i < bones.Count; i++) order.Add(i);
                    order.Sort((a, b) => worst[b].CompareTo(worst[a]));
                    for (int k = 0; k < 6 && k < order.Count; k++)
                    {
                        int i = order[k];
                        sb.Append(' ').Append(bones[i].Key).Append(' ').Append(worst[i].ToString("F0")).Append("° @").Append(worstAt[i]);
                    }
                    GKBridge.Log(sb.ToString());
                }
            }
            finally { Object.DestroyImmediate(go); }
        }

        [GKCommand("rmemptydir", "rmemptydir <folder>[,<folder>...]: delete asset folders that hold no files at all (removes their .meta) (edit mode)")]
        static void RmEmptyDir(string arg)
        {
            foreach (var raw in (arg ?? "").Split(','))
            {
                string f = raw.Trim().TrimEnd('/');
                if (f.Length == 0) continue;
                if (!AssetDatabase.IsValidFolder(f)) { GKBridge.Log("rmemptydir: not a folder: " + f); continue; }
                string abs = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), f);
                int files = System.IO.Directory.GetFiles(abs, "*", System.IO.SearchOption.AllDirectories).Length;
                if (files > 0) { GKBridge.Log("rmemptydir: kept " + f + " (" + files + " files inside)"); continue; }
                GKBridge.Log("rmemptydir: " + f + (AssetDatabase.DeleteAsset(f) ? " deleted" : " NOT deleted"));
            }
        }

        [GKCommand("clipzig", "clipzig <clip>[,<clip>...]: the sword tip's frame-to-frame zigzag in each clip, sampled on the clean knight at 60 fps with no game code (edit mode)")]
        static void Zig(string arg)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("clipzig: stop play mode first"); return; }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKHeroCC.CleanPrefabPath);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                var tip = go.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "BladeTip");
                var anim = go.GetComponent<Animator>();
                var hand = anim.GetBoneTransform(HumanBodyBones.RightHand);
                foreach (var name in (arg ?? "").Split(','))
                {
                    var clip = GKPreview.Clip(name.Trim());
                    if (clip == null || tip == null) { GKBridge.Log("clipzig: no clip / tip " + name); continue; }
                    int n = Mathf.CeilToInt(clip.length * 60f);
                    var pts = new List<Vector3>(); var hrot = new List<Quaternion>();
                    for (int f = 0; f <= n; f++)
                    {
                        clip.SampleAnimation(go, Mathf.Min(clip.length, f / 60f));
                        pts.Add(go.transform.InverseTransformPoint(tip.position));
                        hrot.Add(hand.localRotation);
                    }
                    int zig = 0; float worst = 0f; int worstF = 0; Vector3 lastR = Vector3.zero;
                    var sb = new StringBuilder();
                    for (int f = 1; f < pts.Count - 1; f++)
                    {
                        Vector3 r = pts[f] - (pts[f - 1] + pts[f + 1]) * 0.5f;
                        if (Vector3.Dot(r, lastR) < 0f && r.magnitude > 0.004f && lastR.magnitude > 0.004f)
                        {
                            zig++;
                            float m = Mathf.Min(r.magnitude, lastR.magnitude);
                            if (m > worst) { worst = m; worstF = f; }
                            if (sb.Length < 400) sb.Append(" f" + f + ":" + (m * 1000f).ToString("F0"));
                        }
                        lastR = r;
                    }
                    GKBridge.Log("clipzig " + clip.name + " (" + clip.length.ToString("F2") + " s, " + clip.frameRate + " fps keys): " + zig + " zigzag frames, worst " + (worst * 1000f).ToString("F0") + " mm @f" + worstF + " |" + sb);
                }
            }
            finally { Object.DestroyImmediate(go); }
        }

        [GKCommand("phasescan", "phasescan <clip>[,<clip>...]: which foot leads (left minus right foot, forward, m) at each quarter of the cycle, to line up blended locomotion (edit mode)")]
        static void Phase(string arg)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("phasescan: stop play mode first"); return; }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKHeroCC.CleanPrefabPath);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                var anim = go.GetComponent<Animator>();
                Transform lf = anim.GetBoneTransform(HumanBodyBones.LeftFoot), rf = anim.GetBoneTransform(HumanBodyBones.RightFoot);
                foreach (var name in (arg ?? "").Split(','))
                {
                    var clip = GKPreview.Clip(name.Trim());
                    if (clip == null) { GKBridge.Log("phasescan: no clip " + name); continue; }
                    var sb = new StringBuilder("phasescan " + clip.name + ":");
                    for (int q = 0; q < 8; q++)
                    {
                        clip.SampleAnimation(go, clip.length * q / 8f);
                        float d = Vector3.Dot(lf.position - rf.position, go.transform.forward);
                        sb.Append(' ').Append((q / 8f).ToString("F3")).Append('=').Append(d.ToString("+0.00;-0.00"));
                    }
                    GKBridge.Log(sb.ToString());
                }
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
