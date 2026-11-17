using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// tposefix: Unity's "Enforce T-Pose" for clips whose own skeleton rests in another pose (Rokoko's Newton, the UE5
    /// mannequin from ActionForge). A humanoid avatar takes the model's rest pose as its T-pose; a rest pose with the
    /// arms down made every clip read relative to it, so the knight stood in a T. Here the source skeleton is straightened
    /// into a true T (arms out along the body's right/left, legs down) and that is stored as the avatar's reference pose.
    /// </summary>
    public static class GKTPose
    {
        [GKCommand("tposefix", "tposefix <file prefix>: give the humanoid clips whose file starts with prefix a true T-pose reference (Enforce T-Pose)")]
        static void Cmd(string prefix)
        {
            prefix = (prefix ?? "").Trim();
            if (prefix.Length == 0) { GKBridge.Log("tposefix: give a file prefix (AF_, RK_)"); return; }
            int done = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { GKPaths.Mixamo }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!System.IO.Path.GetFileName(path).StartsWith(prefix)) continue;
                GKBridge.Log("tposefix: " + Fix(path));
                done++;
            }
            GKBridge.Log("tposefix: " + done + " model(s)");
        }

        static string Fix(string path)
        {
            var imp = AssetImporter.GetAtPath(path) as ModelImporter;
            if (imp == null) return path + " is not a model";
            var desc = imp.humanDescription;
            if (desc.human == null || desc.human.Length == 0) return path + " has no humanoid mapping";
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var go = Object.Instantiate(src);
            try
            {
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var byName = new Dictionary<string, Transform>();
                foreach (var t in go.GetComponentsInChildren<Transform>(true)) if (!byName.ContainsKey(t.name)) byName[t.name] = t;
                var human = new Dictionary<string, Transform>();
                foreach (var h in desc.human) { Transform t; if (byName.TryGetValue(h.boneName, out t)) human[h.humanName] = t; }

                // the body's own frame: forward from the hips' facing, up the world up, right from the two hips/shoulders
                Transform lHip, rHip;
                Vector3 right = Vector3.right;
                if (human.TryGetValue("LeftUpperLeg", out lHip) && human.TryGetValue("RightUpperLeg", out rHip))
                    right = Vector3.ProjectOnPlane(rHip.position - lHip.position, Vector3.up).normalized;
                var notes = new List<string>();
                // parents before children: each child's position follows its parent's new rotation
                foreach (var side in new[] { "Left", "Right" })
                {
                    Vector3 outward = side == "Right" ? right : -right;
                    Align(human, side + "Shoulder", side + "UpperArm", outward, notes, 0.7f);
                    Align(human, side + "UpperArm", side + "LowerArm", outward, notes, 1f);
                    Align(human, side + "LowerArm", side + "Hand", outward, notes, 1f);
                    Align(human, side + "Hand", side + "Middle Proximal", outward, notes, 1f);
                    Align(human, side + "UpperLeg", side + "LowerLeg", Vector3.down, notes, 1f);
                    Align(human, side + "LowerLeg", side + "Foot", Vector3.down, notes, 1f);
                }
                // the reference pose the avatar is built from: every listed skeleton bone as it now stands
                var sk = desc.skeleton;
                for (int i = 0; i < sk.Length; i++)
                {
                    Transform t;
                    if (!byName.TryGetValue(sk[i].name, out t) || t == go.transform) continue;
                    sk[i].position = t.localPosition;
                    sk[i].rotation = t.localRotation;
                    sk[i].scale = t.localScale;
                }
                desc.skeleton = sk;
                imp.humanDescription = desc;
                imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                imp.SaveAndReimport();
                return System.IO.Path.GetFileName(path) + " -> " + string.Join(", ", notes);
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>Turns bone a so that the direction to bone b is dir (weight 1 = fully, below 1 = part of the way:
        /// the collarbone only lifts, it does not straighten).</summary>
        static void Align(Dictionary<string, Transform> human, string a, string b, Vector3 dir, List<string> notes, float weight)
        {
            Transform ta, tb;
            if (!human.TryGetValue(a, out ta) || !human.TryGetValue(b, out tb)) return;
            Vector3 cur = tb.position - ta.position;
            if (cur.sqrMagnitude < 1e-8f) return;
            var fix = Quaternion.FromToRotation(cur.normalized, dir.normalized);
            if (weight < 1f) fix = Quaternion.Slerp(Quaternion.identity, fix, weight);
            float deg = Quaternion.Angle(Quaternion.identity, fix);
            ta.rotation = fix * ta.rotation;
            if (deg > 2f) notes.Add(a + " " + deg.ToString("F0") + "°");
        }
    }
}
