using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>Scale and skin sanity of the Hero V2 garment test model: file scale, node scales, bind poses, bounds.</summary>
    public static class GKGarmentDiag
    {
        const string ModelPath = "Assets/_GeorgianKnight/Art/Characters/Hero_V2/GK_HeroV2_GarmentTest.fbx";

        [GKCommand("gtdiag", "gtdiag: scale and skin sanity of the garment test model")]
        static void Diag(string arg)
        {
            var sb = new StringBuilder("gtdiag:\n");
            var imp = (ModelImporter)AssetImporter.GetAtPath(ModelPath);
            sb.AppendLine("  importer fileScale " + imp.fileScale + " globalScale " + imp.globalScale + " useFileScale " + imp.useFileScale + " useFileUnits " + imp.useFileUnits);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            try
            {
                foreach (Transform t in inst.transform)
                    sb.AppendLine("  child " + t.name + " localPos " + t.localPosition.ToString("F3") + " localScale " + t.localScale.ToString("F4"));
                var anim = inst.GetComponent<Animator>();
                sb.AppendLine("  avatar " + (anim && anim.avatar ? anim.avatar.name + " valid " + anim.avatar.isValid + " humanScale " + anim.humanScale.ToString("F4") : "none"));
                foreach (var hb in new[] { HumanBodyBones.Hips, HumanBodyBones.LeftUpperLeg, HumanBodyBones.Head, HumanBodyBones.RightHand })
                {
                    var t = anim.GetBoneTransform(hb);
                    if (t) sb.AppendLine("  " + hb + " " + t.name + " pos " + t.position.ToString("F3") + " lossy " + t.lossyScale.ToString("F4"));
                }
                foreach (var r in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var m = r.sharedMesh;
                    int bi = System.Array.FindIndex(r.bones, b => b != null && b.name == "CC_Base_Hip");
                    string bp = bi >= 0 ? (m.bindposes[bi].inverse.GetColumn(3)).ToString("F3") + " s " + m.bindposes[bi].lossyScale.ToString("F3") : "-";
                    // how far the skinned result is from the mesh as stored (both in world space): 0 when bind poses and bones agree
                    var baked = new Mesh(); r.BakeMesh(baked, true);
                    var mw = Matrix4x4.TRS(r.transform.position, r.transform.rotation, Vector3.one);
                    var a = baked.vertices; var b0 = m.vertices; float worst = 0f;
                    for (int i = 0; i < a.Length; i += 7) worst = Mathf.Max(worst, (mw.MultiplyPoint3x4(a[i]) - r.transform.localToWorldMatrix.MultiplyPoint3x4(b0[i])).magnitude);
                    Object.DestroyImmediate(baked);
                    sb.AppendLine("  smr " + r.name + " verts " + m.vertexCount + " meshBounds " + m.bounds.size.ToString("F3") + " rendererBounds " + r.bounds.size.ToString("F3") +
                                  " t.lossy " + r.transform.lossyScale.ToString("F4") + " hip bindpose^-1 " + bp + " | skinned vs stored " + (worst * 1000f).ToString("F1") + " mm");
                }
                var cones = inst.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("GK_Cone_")).Select(t => t.name + "@" + t.position.ToString("F3"));
                sb.AppendLine("  cones " + string.Join(", ", cones.ToArray()));
                // a humanoid clip sampled the way garmenttest does it
                string clipName = string.IsNullOrEmpty(arg) ? "AX_standing_idle" : arg.Trim();
                var guid = AssetDatabase.FindAssets(clipName + " t:AnimationClip", new[] { "Assets/_GeorgianKnight/Animations" }).FirstOrDefault();
                var clip = guid == null ? null : AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(guid)).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
                if (clip != null)
                {
                    sb.AppendLine("  clip " + clip.name + " human " + clip.humanMotion + " length " + clip.length.ToString("F2") + " from " + AssetDatabase.GetAssetPath(clip));
                    var spot = new Vector3(0f, 1000f, 0f);
                    foreach (float tt in new[] { 0f, 0.25f, 0.5f })
                    {
                        if (!AnimationMode.InAnimationMode()) AnimationMode.StartAnimationMode();
                        AnimationMode.BeginSampling(); AnimationMode.SampleAnimationClip(inst, clip, clip.length * tt); AnimationMode.EndSampling();
                        Vector3 rootAfterSample = inst.transform.position;
                        inst.transform.SetPositionAndRotation(spot, Quaternion.identity);
                        var hips = anim.GetBoneTransform(HumanBodyBones.Hips); var hand = anim.GetBoneTransform(HumanBodyBones.RightHand); var foot = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
                        sb.AppendLine("   t" + tt.ToString("F2") + " root after sample " + rootAfterSample.ToString("F3") + " | hips " + (hips.position - spot).ToString("F3") + " hand " + (hand.position - spot).ToString("F3") +
                                      " foot " + (foot.position - spot).ToString("F3") + " hips.localPos " + hips.localPosition.ToString("F3") + " parent " + hips.parent.name + " " + hips.parent.localPosition.ToString("F3"));
                    }
                    if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
                }
            }
            finally { if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode(); Object.DestroyImmediate(inst); }
            GKBridge.Log(sb.ToString());
        }
    }
}
