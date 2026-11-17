using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering.Universal;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Hero V2 garment test: the Style3D gambeson and trousers, skinned in Blender to the CC rig, played through the
    /// Mixamo library. For every sampled frame it measures how far the body (and the trousers) come out through the
    /// cloth, and renders the worst frame of every clip from the front and the back.
    /// </summary>
    public static class GKGarmentTest
    {
        const string ModelPath = "Assets/_GeorgianKnight/Art/Characters/Hero_V2/GK_HeroV2_GarmentTest.fbx";
        const string ClipDir = GKPaths.Mixamo;
        static readonly string[] DefaultClips =
        {
            "AX_standing_idle", "AX_standing_walk_forward", "AX_standing_run_forward", "AX_standing_melee_attack_horizontal",
            "AX_standing_melee_attack_downward", "AX_standing_melee_attack_360_high", "AX_standing_melee_attack_kick_ver_1",
            "GK_Dodge_Fwd", "AX_crouch_idle", "AX_standing_jump"
        };
        static readonly Vector3 Spot = new Vector3(0f, 1000f, 0f);

        [GKCommand("garmentimport", "garmentimport: humanoid import of the Hero V2 garment test model")]
        static void Import(string a)
        {
            var imp = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (imp == null) { GKBridge.Log("garmentimport: model not found " + ModelPath); return; }
            imp.animationType = ModelImporterAnimationType.Human;
            imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            imp.importAnimation = false;
            imp.importBlendShapes = true;
            imp.SaveAndReimport();
            // the avatar's skeleton (the rest pose humanoid sampling rebuilds the bones from) is kept in the .meta across
            // re-exports; rebuild it from the model as it is now, or an old export's bone offsets come back (cm -> m)
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            var hd = imp.humanDescription;
            hd.skeleton = model.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone
            {
                name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale
            }).ToArray();
            imp.humanDescription = hd;
            imp.SaveAndReimport();
            var thigh = imp.humanDescription.skeleton.FirstOrDefault(s => s.name == "CC_Base_L_Thigh");
            GKBridge.Log("garmentimport: skeleton " + imp.humanDescription.skeleton.Length + " bones, L thigh offset " + thigh.position.ToString("F4") +
                         ", human bones " + imp.humanDescription.human.Length);
            var av = AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<Avatar>().FirstOrDefault();
            GKBridge.Log("garmentimport: avatar " + (av == null ? "missing" : ("valid=" + av.isValid + " human=" + av.isHuman)));
        }

        static AnimationClip FindClip(string name)
        {
            foreach (var guid in AssetDatabase.FindAssets(name, new[] { ClipDir }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(p) != name) continue;
                var c = AssetDatabase.LoadAllAssetsAtPath(p).OfType<AnimationClip>().FirstOrDefault(x => !x.name.StartsWith("__preview"));
                if (c != null) return c;
            }
            return null;
        }

        class Probe
        {
            public SkinnedMeshRenderer smr; public int[] idx; public string label;
            public int[] tri; public Vector3[] bary;     // the skin under each probed cloth point (body probes)
        }

        [GKCommand("garmenttest", "garmenttest [clip,clip|all] [noshots]: body / trousers poking through the Hero V2 garments, per clip")]
        static void Test(string arg)
        {
            var parts = (arg ?? "").Split(' ').Where(s => s.Length > 0).ToArray();
            bool shots = !parts.Contains("noshots");
            bool noCones = parts.Contains("nocones");
            int joinedOverride = parts.Where(s => s.StartsWith("joined=")).Select(s => int.Parse(s.Substring(7))).DefaultIfEmpty(-1).First();
            string tag = parts.Where(s => s.StartsWith("tag=")).Select(s => s.Substring(4) + "_").DefaultIfEmpty("").First();
            string filter = parts.FirstOrDefault(s => s != "noshots" && s != "nocones" && !s.Contains("="));
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (model == null) { GKBridge.Log("garmenttest: model not found"); return; }
            var temps = new List<GameObject>();
            var sb = new StringBuilder("garmenttest: how far the body / trousers come out through the cloth (mm). worst | frames over 2 mm | where\n");
            try
            {
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
                temps.Add(inst);
                inst.transform.SetPositionAndRotation(Spot, Quaternion.identity);
                var anim = inst.GetComponent<Animator>();
                if (anim == null) anim = inst.AddComponent<Animator>();
                if (anim.avatar == null) anim.avatar = AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<Avatar>().FirstOrDefault();
                anim.applyRootMotion = false;
                var smrs = inst.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var body = smrs.FirstOrDefault(r => r.name == "CC_Base_Body");
                var gamb = smrs.FirstOrDefault(r => r.name == "GK_Gambeson");
                var trou = smrs.FirstOrDefault(r => r.name == "GK_Trousers");
                if (body == null || gamb == null || trou == null) { GKBridge.Log("garmenttest: renderers missing " + string.Join(",", smrs.Select(r => r.name))); return; }
                foreach (var r in smrs) r.updateWhenOffscreen = true;

                // the skirt as cloth: the GK_Cloth_ bones go through the game's own builder (leg cones, ring, seams)
                ClothGrid cloth = null;
                {
                    // the humanoid's own mapping (the name-based mapper does not know the CC_Base_ names)
                    var map = new Dictionary<string, Transform>();
                    foreach (HumanBodyBones hb in System.Enum.GetValues(typeof(HumanBodyBones)))
                    {
                        if (hb == HumanBodyBones.LastBone) continue;
                        var bt = anim.GetBoneTransform(hb); if (bt != null) map[hb.ToString()] = bt;
                    }
                    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                    var bindM = typeof(GKGameBuilder).GetMethod("ClothBind", flags);
                    var buildM = typeof(GKGameBuilder).GetMethod("BuildClothGrid", flags);
                    var bind = bindM != null ? bindM.Invoke(null, new object[] { inst, map }) : null;
                    if (bind != null && buildM != null) cloth = (ClothGrid)buildM.Invoke(null, new object[] { inst, map, bind, new CapsuleCollider[0], true });
                    if (cloth != null)
                    {
                        if (noCones) { cloth.coneBones = new Transform[0]; cloth.coneA = cloth.coneB = new Vector3[0]; cloth.coneRA = cloth.coneRB = new float[0]; }
                        if (joinedOverride >= 0) cloth.joinedRows = joinedOverride;
                        // experiments: clearance cap (m), cone radius scale, share of each thigh cone cut off at its top
                        System.Func<string, float> num = key => parts.Where(s => s.StartsWith(key + "=")).Select(s => float.Parse(s.Substring(key.Length + 1), System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(-1f).First();
                        float clearCap = num("clear"), rScale = num("rscale"), thighCut = num("thighcut");
                        for (int k = 0; k < cloth.coneBones.Length; k++)
                        {
                            if (rScale > 0f) { cloth.coneRA[k] *= rScale; cloth.coneRB[k] *= rScale; }
                            if (thighCut > 0f && cloth.coneBones[k].name.Contains("Thigh"))
                            {
                                cloth.coneA[k] = Vector3.Lerp(cloth.coneA[k], cloth.coneB[k], thighCut);
                                cloth.coneRA[k] = Mathf.Lerp(cloth.coneRA[k], cloth.coneRB[k], thighCut);
                            }
                        }
                        if (clearCap >= 0f || rScale > 0f || thighCut > 0f)
                        {
                            // the builder's clearance (what each joint has from the cones at the bind pose), against the changed cones
                            float cap = clearCap >= 0f ? clearCap : 0.045f;
                            for (int i = 0; i < cloth.clearance.Length; i++)
                            {
                                Vector3 w = cloth.hips.TransformPoint(cloth.restPos[i]);
                                float best = cap;
                                for (int k = 0; k < cloth.coneBones.Length; k++)
                                {
                                    var t = cloth.coneBones[k];
                                    Vector3 a = t.TransformPoint(cloth.coneA[k]), ab = t.TransformPoint(cloth.coneB[k]) - a;
                                    float u = Vector3.Dot(w - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude);
                                    if (u < 0f) continue;
                                    u = Mathf.Min(u, 1f);
                                    best = Mathf.Min(best, (w - (a + ab * u)).magnitude - Mathf.Lerp(cloth.coneRA[k], cloth.coneRB[k], u) - 0.002f);
                                }
                                cloth.clearance[i] = best;
                            }
                        }
                        cloth.enabled = false; cloth.Prepare();
                        GKBridge.Log("garmenttest: cloth options cones " + cloth.coneBones.Length + ", joined rows " + cloth.joinedRows + ", clear cap " + clearCap + ", rscale " + rScale + ", thighcut " + thighCut +
                                     ", clearance " + (cloth.clearance.Length > 0 ? cloth.clearance.Min().ToString("F3") + ".." + cloth.clearance.Max().ToString("F3") : "-"));
                    }
                    int nb = inst.GetComponentsInChildren<Transform>(true).Count(t => t.name.StartsWith("GK_Cloth_"));
                    GKBridge.Log("garmenttest: cloth bones " + nb + ", hips " + (map.ContainsKey("Hips") ? map["Hips"].name : "?") + ", bindM " + (bindM != null) + ", buildM " + (buildM != null) +
                                 ", bind " + (bind != null) + " -> " + (cloth == null ? "none" : cloth.columns.Length + " columns x " + cloth.rows + " rows"));
                }

                // colliders that follow the baked body and trousers
                var bodyGo = new GameObject("~gt_body"); temps.Add(bodyGo);
                var bodyCol = bodyGo.AddComponent<MeshCollider>();
                var trouGo = new GameObject("~gt_trousers"); temps.Add(trouGo);
                var trouCol = trouGo.AddComponent<MeshCollider>();
                var bakedBody = new Mesh(); var bakedTrou = new Mesh(); var bakedG = new Mesh(); var bakedT = new Mesh();

                var probes = new[]
                {
                    new Probe { smr = gamb, label = "body>gambeson" },
                    new Probe { smr = trou, label = "body>trousers" },
                    new Probe { smr = gamb, label = "trousers>skirt" },
                };
                foreach (var p in probes) p.idx = Enumerable.Range(0, p.smr.sharedMesh.vertexCount).Where(i => i % 2 == 0).ToArray();
                {
                    var bw = gamb.sharedMesh.boneWeights; var gb = gamb.bones;
                    System.Func<int, float> cw = i =>
                        (gb[bw[i].boneIndex0] != null && gb[bw[i].boneIndex0].name.StartsWith("GK_Cloth_") ? bw[i].weight0 : 0f) +
                        (gb[bw[i].boneIndex1] != null && gb[bw[i].boneIndex1].name.StartsWith("GK_Cloth_") ? bw[i].weight1 : 0f) +
                        (gb[bw[i].boneIndex2] != null && gb[bw[i].boneIndex2].name.StartsWith("GK_Cloth_") ? bw[i].weight2 : 0f) +
                        (gb[bw[i].boneIndex3] != null && gb[bw[i].boneIndex3].name.StartsWith("GK_Cloth_") ? bw[i].weight3 : 0f);
                    probes[2].idx = Enumerable.Range(0, bw.Length).Where(i => cw(i) > 0.9f && i % 2 == 0).ToArray();
                    // the skirt is the cloth's business (trousers>skirt); the body probe looks at what lies on the body
                    probes[0].idx = probes[0].idx.Where(i => cw(i) < 0.05f).ToArray();
                }
                // only the outer shell of each (solidified) garment is what the eye sees: at the bind pose a vertex is on it
                // when a ray from it along -normal reaches the body (the inner shell's normals face the body, its rays go out)
                {
                    BakeCollider(body, bodyGo, bodyCol, bakedBody);
                    foreach (var p in probes)
                    {
                        var baked = new Mesh(); p.smr.BakeMesh(baked, true);
                        var v = baked.vertices; var n = baked.normals;
                        var m = Matrix4x4.TRS(p.smr.transform.position, p.smr.transform.rotation, Vector3.one);
                        int before = p.idx.Length;
                        p.idx = p.idx.Where(i =>
                        {
                            Vector3 w = m.MultiplyPoint3x4(v[i]); Vector3 nn = m.MultiplyVector(n[i]).normalized;
                            return IsOuter(bodyCol, w, nn);
                        }).ToArray();
                        Object.DestroyImmediate(baked);
                        GKBridge.Log("garmenttest: " + p.label + " outer shell " + p.idx.Length + " of " + before + " probed vertices");
                        if (p.label.StartsWith("body>"))
                        {
                            // the skin each cloth point lies over at the bind pose (within 5 cm): poking through is that skin point
                            // coming out past the cloth point, so an arm or a hand crossing the cloth is not counted as the body
                            baked = new Mesh(); p.smr.BakeMesh(baked, true); v = baked.vertices; n = baked.normals;
                            var keepI = new List<int>(); var keepT = new List<int>(); var keepB = new List<Vector3>();
                            foreach (int i in p.idx)
                            {
                                Vector3 w = m.MultiplyPoint3x4(v[i]); Vector3 nn = m.MultiplyVector(n[i]).normalized;
                                if (!bodyCol.Raycast(new Ray(w - nn * 0.0005f, -nn), out RaycastHit h, 0.025f)) continue;
                                // skin of the hands, feet and head is not under the cloth (cuffs, hems, the collar's edge)
                                string bn0 = DominantBone(body, bakedBody.triangles[h.triangleIndex * 3]);
                                if (Extremity(bn0)) continue;
                                keepI.Add(i); keepT.Add(h.triangleIndex); keepB.Add(h.barycentricCoordinate);
                            }
                            Object.DestroyImmediate(baked);
                            p.idx = keepI.ToArray(); p.tri = keepT.ToArray(); p.bary = keepB.ToArray();
                            GKBridge.Log("garmenttest: " + p.label + " lies on the skin at " + p.idx.Length + " points");
                        }
                    }
                    GKBridge.Log("garmenttest: hips lossyScale " + hips0(anim).lossyScale.ToString("F4") + ", root " + inst.transform.lossyScale.ToString("F4"));
                }
                // the trousers as an obstacle: their outer shell only (from inside the leg the inner shell would be met first)
                int[] trouOuterTris;
                {
                    var baked = new Mesh(); trou.BakeMesh(baked, true);
                    var v = baked.vertices; var n = baked.normals;
                    var m = Matrix4x4.TRS(trou.transform.position, trou.transform.rotation, Vector3.one);
                    var outer = new bool[v.Length];
                    for (int i = 0; i < v.Length; i++)
                    {
                        Vector3 w = m.MultiplyPoint3x4(v[i]); Vector3 nn = m.MultiplyVector(n[i]).normalized;
                        outer[i] = IsOuter(bodyCol, w, nn);
                    }
                    var tris = baked.triangles; var keep = new List<int>();
                    for (int t = 0; t < tris.Length; t += 3)
                        if (outer[tris[t]] && outer[tris[t + 1]] && outer[tris[t + 2]]) { keep.Add(tris[t]); keep.Add(tris[t + 1]); keep.Add(tris[t + 2]); }
                    trouOuterTris = keep.ToArray();
                    Object.DestroyImmediate(baked);
                    GKBridge.Log("garmenttest: trousers outer shell " + trouOuterTris.Length / 3 + " of " + tris.Length / 3 + " triangles");
                    // the probe leans on back-face hits: from the middle of the chest a ray out must leave the body
                    bool hb0 = Physics.queriesHitBackfaces; Physics.queriesHitBackfaces = true;
                    var chest = anim.GetBoneTransform(HumanBodyBones.Chest);
                    bool ok = bodyCol.Raycast(new Ray(chest.position, inst.transform.forward), out RaycastHit hh, 1f);
                    Physics.queriesHitBackfaces = hb0;
                    GKBridge.Log("garmenttest: back-face probe " + (ok ? "ok, chest surface at " + (hh.distance * 100f).ToString("F1") + " cm" : "NOT HONOURED"));
                }

                List<AnimationClip> clips;
                if (filter == "all") clips = AssetDatabase.FindAssets("t:AnimationClip", new[] { ClipDir }).Select(g => AssetDatabase.GUIDToAssetPath(g)).Distinct()
                        .Select(p => AssetDatabase.LoadAllAssetsAtPath(p).OfType<AnimationClip>().FirstOrDefault(x => !x.name.StartsWith("__preview"))).Where(c => c != null).ToList();
                else if (!string.IsNullOrEmpty(filter)) clips = filter.Split(',').Select(FindClip).Where(c => c != null).ToList();
                else clips = DefaultClips.Select(FindClip).Where(c => c != null).ToList();

                var camGo = new GameObject("~gt_cam"); temps.Add(camGo);
                var cam = camGo.AddComponent<Camera>(); cam.nearClipPlane = 0.03f; cam.farClipPlane = 50f; cam.fieldOfView = 30f;
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.36f, 0.37f, 0.39f);
                var cd = cam.GetUniversalAdditionalCameraData(); cd.renderPostProcessing = false; cd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                var key = new GameObject("~gt_key"); temps.Add(key);
                var kl = key.AddComponent<Light>(); kl.type = LightType.Directional; kl.intensity = 1.6f; kl.shadows = LightShadows.Soft;
                key.transform.rotation = Quaternion.Euler(35f, 150f, 0f);
                var fill = new GameObject("~gt_fill"); temps.Add(fill);
                var fl = fill.AddComponent<Light>(); fl.type = LightType.Directional; fl.intensity = 0.6f; fl.shadows = LightShadows.None;
                fill.transform.rotation = Quaternion.Euler(20f, -40f, 0f);
                Transform hips = anim.GetBoneTransform(HumanBodyBones.Hips);
                var bones = new List<KeyValuePair<string, Transform>>();
                foreach (HumanBodyBones hb in System.Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (hb == HumanBodyBones.LastBone) continue;
                    var t = anim.GetBoneTransform(hb); if (t != null) bones.Add(new KeyValuePair<string, Transform>(hb.ToString(), t));
                }

                int flagged = 0;
                foreach (var clip in clips)
                {
                    int frames = Mathf.Max(2, Mathf.CeilToInt(clip.length * 30f));
                    var worst = new float[probes.Length]; var over = new int[probes.Length]; var where = new string[probes.Length]; var worstT = new float[probes.Length];
                    for (int k = 0; k < probes.Length; k++) where[k] = "-";
                    if (cloth != null)
                    {
                        cloth.ResetDynamics();
                        for (int f = 0; f < 30; f++) { Pose(inst, clip, 0f); cloth.Simulate(1f / 60f); }
                    }
                    float maxStrain = 1f; string strainAt = "-";
                    for (int f = 0; f <= frames; f++)
                    {
                        float t = clip.length * f / frames;
                        Pose(inst, clip, t);
                        if (cloth != null) { cloth.Simulate(clip.length / frames); float ms = cloth.MaxStrain(); if (ms > maxStrain) { maxStrain = ms; strainAt = cloth.StrainWhere() + " @" + t.ToString("F2") + "s"; } }
                        if (f % 2 != 0) continue;
                        BakeCollider(body, bodyGo, bodyCol, bakedBody);
                        BakeCollider(trou, trouGo, trouCol, bakedTrou, trouOuterTris);
                        for (int k = 0; k < probes.Length; k++)
                        {
                            var p = probes[k];
                            var col = k == 2 ? trouCol : bodyCol;
                            if (p.idx.Length == 0) continue;
                            var baked = p.smr == gamb ? bakedG : bakedT;
                            p.smr.BakeMesh(baked, true);
                            var v = baked.vertices; var n = baked.normals;
                            var m = Matrix4x4.TRS(p.smr.transform.position, p.smr.transform.rotation, Vector3.one);
                            float fw = 0f; Vector3 fwPos = Vector3.zero;
                            // poking through = the cloth point lies inside the body (or the trousers' leg): a ray from it straight out
                            // leaves that surface from inside (a back face) before 6 cm; a hand or the other leg lying over the cloth is
                            // met from outside (a front face) and does not count
                            if (p.tri != null)
                            {
                                var bv = bakedBody.vertices; var bn = bakedBody.normals; var bt = bakedBody.triangles;
                                var mb = Matrix4x4.TRS(body.transform.position, body.transform.rotation, Vector3.one);
                                for (int q = 0; q < p.idx.Length; q++)
                                {
                                    int i = p.idx[q], t0 = p.tri[q] * 3; Vector3 b = p.bary[q];
                                    Vector3 sp = mb.MultiplyPoint3x4(bv[bt[t0]] * b.x + bv[bt[t0 + 1]] * b.y + bv[bt[t0 + 2]] * b.z);
                                    Vector3 sn = mb.MultiplyVector(bn[bt[t0]] * b.x + bn[bt[t0 + 1]] * b.y + bn[bt[t0 + 2]] * b.z).normalized;
                                    Vector3 w = m.MultiplyPoint3x4(v[i]);
                                    float depth = -Vector3.Dot(w - sp, sn);
                                    if (depth > fw) { fw = depth; fwPos = w; }
                                }
                            }
                            bool hb0 = Physics.queriesHitBackfaces; Physics.queriesHitBackfaces = true;
                            if (p.tri == null) foreach (int i in p.idx)
                            {
                                Vector3 w = m.MultiplyPoint3x4(v[i]); Vector3 nn = m.MultiplyVector(n[i]).normalized;
                                const float L = 0.06f;
                                if (col.Raycast(new Ray(w, nn), out RaycastHit hit, L) && Vector3.Dot(hit.normal, nn) > 0.3f)
                                {
                                    float depth = hit.distance;
                                    if (depth > fw) { fw = depth; fwPos = w; }
                                }
                            }
                            Physics.queriesHitBackfaces = hb0;
                            if (fw > 0.002f) over[k]++;
                            if (fw > worst[k]) { worst[k] = fw; worstT[k] = t; where[k] = Nearest(bones, fwPos) + " h" + (fwPos.y - Spot.y).ToString("F2"); }
                        }
                    }
                    Pose(inst, clip, 0f); Vector3 h0 = anim.GetBoneTransform(HumanBodyBones.RightHand).position;
                    Pose(inst, clip, clip.length * 0.5f); Vector3 h1 = anim.GetBoneTransform(HumanBodyBones.RightHand).position;
                    sb.Append("[hand moves " + ((h1 - h0).magnitude * 100f).ToString("F0") + " cm] ");
                    bool bad = worst.Any(x => x > 0.004f);
                    if (bad) flagged++;
                    sb.Append(clip.name.PadRight(42));
                    for (int k = 0; k < probes.Length; k++)
                        sb.Append(" | " + probes[k].label + " " + (worst[k] * 1000f).ToString("F1") + " mm, " + over[k] + " fr, " + where[k] + " @" + worstT[k].ToString("F2") + "s");
                    sb.Append(" | cloth strain " + maxStrain.ToString("F2") + " (" + strainAt + ")");
                    sb.AppendLine(bad ? "  <<" : "");
                    if (shots)
                    {
                        // three moments of every clip from the front three-quarter, the middle one also from behind
                        var moments = new[] { 0.2f, 0.5f, 0.8f };
                        if (cloth != null) { cloth.ResetDynamics(); for (int f = 0; f < 30; f++) { Pose(inst, clip, 0f); cloth.Simulate(1f / 60f); } }
                        int fdone = 0;
                        for (int q = 0; q < moments.Length; q++)
                        {
                            int ft = Mathf.RoundToInt(moments[q] * frames);
                            for (; fdone <= ft; fdone++) { Pose(inst, clip, clip.length * fdone / frames); if (cloth != null) cloth.Simulate(clip.length / frames); }
                            foreach (var view in q == 1 ? new[] { "f34", "b34", "skirt" } : new[] { "f34" })
                            {
                                Vector3 look = hips.position + Vector3.up * 0.05f;
                                Vector3 fwd = inst.transform.forward, right = inst.transform.right;
                                Vector3 pos = view == "f34" ? look + fwd * 3.6f + right * 1.4f + Vector3.up * 0.25f : view == "b34" ? look - fwd * 3.6f - right * 1.2f + Vector3.up * 0.35f : look + fwd * 1.5f + right * 0.6f - Vector3.up * 0.05f;
                                camGo.transform.position = pos; camGo.transform.rotation = Quaternion.LookRotation(look - pos, Vector3.up);
                                GKPreview.Capture(cam, "gt_" + tag + clip.name + "_" + q + "_" + view, 600, 760);
                            }
                        }
                    }
                }
                sb.AppendLine("flagged (over 4 mm): " + flagged + " of " + clips.Count);
                File.WriteAllText(Path.Combine(GKBridge.Dir, "garmenttest" + (tag.Length > 0 ? "_" + tag.TrimEnd((char)95) : "") + ".txt"), sb.ToString());
                GKBridge.Log("garmenttest: " + flagged + " of " + clips.Count + " clips flagged");
            }
            finally
            {
                ReleaseGraph();
                foreach (var g in temps) if (g != null) Object.DestroyImmediate(g);
            }
        }

        // humanoid clips are sampled the way the Animation window does it (AnimationMode); a plain SampleAnimation or an
        // edit-mode playable graph leaves a humanoid in its bind / muscle-zero pose
        static void Pose(GameObject inst, AnimationClip clip, float t)
        {
            if (!AnimationMode.InAnimationMode()) AnimationMode.StartAnimationMode();
            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(inst, clip, t);
            AnimationMode.EndSampling();
            inst.transform.SetPositionAndRotation(Spot, Quaternion.identity);
        }

        static Transform hips0(Animator a) { return a.GetBoneTransform(HumanBodyBones.Hips); }

        // a vertex is on a solidified garment's outer shell when the skin it covers lies behind it (along -normal) and
        // nearer than anything in front of it: the inner shell faces its own skin, and its back may face another limb
        static bool IsOuter(MeshCollider body, Vector3 w, Vector3 nn)
        {
            if (!body.Raycast(new Ray(w - nn * 0.0005f, -nn), out RaycastHit hb, 0.35f)) return false;
            if (!body.Raycast(new Ray(w + nn * 0.0005f, nn), out RaycastHit hf, 0.35f)) return true;
            return hb.distance < hf.distance;
        }

        static string DominantBone(SkinnedMeshRenderer r, int vertex)
        {
            var bw = r.sharedMesh.boneWeights;
            if (vertex < 0 || vertex >= bw.Length) return "";
            var b = r.bones[bw[vertex].boneIndex0];
            return b != null ? b.name : "";
        }

        static bool Extremity(string bone)
        {
            foreach (var k in new[] { "Hand", "Thumb", "Index", "Mid", "Ring", "Pinky", "Foot", "Toe", "Head", "Jaw", "Eye", "Neck" })
                if (bone.Contains(k)) return true;
            return false;
        }

        static void ReleaseGraph()
        {
            if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
        }

        static void BakeCollider(SkinnedMeshRenderer r, GameObject go, MeshCollider col, Mesh baked, int[] tris = null)
        {
            r.BakeMesh(baked, true);
            if (tris != null) { baked.subMeshCount = 1; baked.SetTriangles(tris, 0); }
            go.transform.SetPositionAndRotation(r.transform.position, r.transform.rotation);
            col.sharedMesh = null; col.sharedMesh = baked;
            Physics.SyncTransforms();
        }

        static string Nearest(List<KeyValuePair<string, Transform>> bones, Vector3 p)
        {
            string best = "-"; float bd = float.MaxValue;
            foreach (var kv in bones) { float d = (kv.Value.position - p).sqrMagnitude; if (d < bd) { bd = d; best = kv.Key; } }
            return best;
        }
    }
}
