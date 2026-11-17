using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Rig diagnostics for the Character Creator hero: where the CC helper bones (twist, share) sit in the hierarchy,
    /// whether the body actually skins to them, and how the Humanoid maps the neck. The Humanoid retarget drives only
    /// the mapped bones; the CC helpers stay at their bind pose relative to their parent unless something drives them.
    /// </summary>
    static class GKRigDiag
    {
        [GKCommand("ccbones", "ccbones [prefabPath]: CC twist/share/neck helper bones: parent, children, skin weight they carry")]
        static void CcBones(string arg)
        {
            string path = string.IsNullOrEmpty(arg) ? GKHeroCC.PrefabPath : arg.Trim();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) { GKBridge.Log("ccbones: no prefab at " + path); return; }
            var sb = new StringBuilder();
            var weight = new Dictionary<Transform, float>();
            var count = new Dictionary<Transform, int>();
            foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                if (mesh == null) continue;
                var bones = smr.bones;
                var bpv = mesh.GetBonesPerVertex();
                var bw = mesh.GetAllBoneWeights();
                int k = 0;
                for (int v = 0; v < bpv.Length; v++)
                {
                    int n = bpv[v];
                    for (int j = 0; j < n; j++, k++)
                    {
                        var w = bw[k];
                        if (w.boneIndex < 0 || w.boneIndex >= bones.Length || bones[w.boneIndex] == null) continue;
                        var t = bones[w.boneIndex];
                        float f; weight.TryGetValue(t, out f); weight[t] = f + w.weight;
                        if (w.weight > 0.1f) { int c; count.TryGetValue(t, out c); count[t] = c + 1; }
                    }
                }
            }
            var anim = prefab.GetComponentInChildren<Animator>();
            if (anim != null && anim.avatar != null)
            {
                var hd = anim.avatar.humanDescription;
                foreach (var hb in hd.human)
                    if (hb.humanName == "Neck" || hb.humanName == "Head" || hb.humanName.Contains("Arm") || hb.humanName.Contains("Leg") || hb.humanName.Contains("Shoulder"))
                        sb.Append(hb.humanName + "=" + hb.boneName + " ");
                sb.Append("| twist u/l arm " + hd.upperArmTwist + "/" + hd.lowerArmTwist + " leg " + hd.upperLegTwist + "/" + hd.lowerLegTwist);
                GKBridge.Log("ccbones humanoid: " + sb); sb.Length = 0;
            }
            foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name;
                if (!(n.Contains("Twist") || n.Contains("Share") || n.Contains("Neck") || n.EndsWith("_Head") || n.EndsWith("Upperarm") || n.EndsWith("Forearm") || n.EndsWith("_Hand") || n.EndsWith("Thigh") || n.EndsWith("Calf") || n.EndsWith("_Foot") || n.EndsWith("Clavicle"))) continue;
                sb.Length = 0;
                sb.Append(n + " < " + (t.parent != null ? t.parent.name : "-") + " [");
                for (int i = 0; i < t.childCount; i++) sb.Append(t.GetChild(i).name.Replace("CC_Base_", "") + (i < t.childCount - 1 ? "," : ""));
                float wsum; weight.TryGetValue(t, out wsum); int vc; count.TryGetValue(t, out vc);
                sb.Append("] lp " + t.localPosition.ToString("F3") + " lr " + t.localRotation.eulerAngles.ToString("F1") + " w " + wsum.ToString("F0") + " v>0.1 " + vc);
                GKBridge.Log("ccbones: " + sb);
            }
        }
    }
}
