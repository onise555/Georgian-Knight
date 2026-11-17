using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// The hero as Character Creator dressed him (tunic, long coat with its soft-cloth weight map, belt over the coat,
    /// breeches, boots, gloves), brought in exactly the way CC_Hero was: CC Unity Tools, high-quality materials,
    /// parallax eyes, two-pass hair, the full facial expression set (395 shapes) with the beard following the face,
    /// expression bone drivers. The coat follows Character Creator's skin weights (no simulation: CoatPhysics), and the
    /// double-sided garments use the two-sided cloth shader so their insides are lit from the side the camera sees.
    /// Then the game's character builder makes Prefabs/HeroCC_Player.prefab, which the sandbox prefers.
    /// Source: ArtSource/Hero_V2/cc_export/CC_HeroOutfit_Unity.* (FBX, json, .fbm, textures).
    /// </summary>
    public static class GKHeroCC
    {
        public const string Name = "CC_HeroOutfit_Unity";
        public const string Dir = GKPaths.Characters + "/CC_HeroOutfit";
        public const string Fbx = Dir + "/" + Name + ".fbx";
        public const string PrefabPath = GKPaths.Prefabs + "/HeroCC_Player.prefab";
        public const string CleanPrefabPath = GKPaths.Prefabs + "/Knight_Clean.prefab";
        const string SourceDir = "ArtSource/Hero_V2/cc_export";

        // ExpressionTranspose (beard and brows follow the face) | ConstraintData | BoneDriver (expression-driven jaw and
        // eye bones) | ClothPhysics + UnityClothPhysics (the coat) | SmoothBlendshapeNormals (clean shading in expressions)
        const int Flags = 65536 | 131072 | 32768 | 2 | 128 | 1048576;

        public static bool Available { get { return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null; } }

        [GKCommand("herocc", "herocc [copy|import|prefab|clean|all]: the Character Creator outfit through CC Unity Tools to Prefabs/HeroCC_Player ('clean': Prefabs/Knight_Clean, one-handed clips and no pose fixes)")]
        static void Cmd(string arg)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("herocc: stop play mode first"); return; }
            string a = string.IsNullOrEmpty(arg) ? "all" : arg.Trim();
            if (a == "copy" || a == "all") { if (!Copy()) return; }
            if (a == "import" || a == "all") { if (!Import()) return; }
            if (a == "prefab" || a == "all") BuildPrefab(false);
            if (a == "clean") BuildPrefab(true);
            if (a == "diag") Diag();
            if (a.StartsWith("poses")) Poses(a.Substring(5).Trim());
            if (a.StartsWith("macro")) Macro(a.Substring(5).Trim());
            if (a == "weights") Weights();
            if (a == "grip") Grip();
            if (a == "stance") Stance();
            if (a == "height") Height();
            if (a.StartsWith("heightqa")) HeightQa(a.Substring(8).Trim());
        }

        /// <summary>Bakur's height (Director, DECISIONS f5f6d71): soles to the top of the head, bare body.</summary>
        public const float HeroHeight = 1.78f;

        /// <summary>The sole's height in the FBX at scale 1 (CC_Base_Body vertex #52548, 0.01076 m; Art's round trip
        /// 91f70d1). Character Creator deleted the body under the outfit, leaving 22,781 vertices without faces, and
        /// Unity drops them: the lowest vertex Unity keeps sits inside the heel at 5.2 cm, which read the hero 4 cm short.
        /// The crown (#11324) survives in Unity.</summary>
        public const float FileSoleY = 0.01076f;

        /// <summary>The body measured at its import scale (the Character Creator export is 1.826 m), and the FBX's one
        /// uniform import scale set so it stands <see cref="HeroHeight"/> tall: body and every garment scale together, the
        /// prefab root stays at 1 and the Blender sources stay at CC scale. Run 'herocc clean' after it.</summary>
        static void Height()
        {
            var imp = AssetImporter.GetAtPath(Fbx) as ModelImporter;
            if (imp == null) { GKBridge.Log("herocc height: no importer for " + Fbx); return; }
            float h = MeasureHeight();
            if (h <= 0f) { GKBridge.Log("herocc height: no CC_Base_Body to measure"); return; }
            float s = imp.globalScale * HeroHeight / h;
            if (Mathf.Abs(s - imp.globalScale) < 0.0005f) { GKBridge.Log("herocc height: " + h.ToString("F3") + " m at scale " + imp.globalScale.ToString("F4") + ", already right"); return; }
            float was = imp.globalScale;
            imp.globalScale = s;
            // Unity writes the human description's scale as import scale x file unit (0.01 for this cm file) while its
            // skeleton is already in metres: the avatar came out 100x (humanScale 105, the hips 87 m up in play)
            var so = new SerializedObject(imp);
            var hs = so.FindProperty("m_HumanDescription.m_GlobalScale");
            if (hs != null) { hs.floatValue = s; so.ApplyModifiedPropertiesWithoutUndo(); }
            imp.SaveAndReimport();
            GKBridge.Log("herocc height: " + h.ToString("F3") + " m at scale " + was.ToString("F4") + " -> scale " + s.ToString("F4") + ", now " + MeasureHeight().ToString("F3") + " m");
        }

        /// <summary>Where a height reading comes from, for comparing with the FBX read raw: the importer's scales, the
        /// CC_Base_Body renderer's scale chain, the raw mesh bounds, the lowest-sole and crown vertex indices, and
        /// optionally two named indices ("heightqa 1234 5678") read from sharedMesh, raw and through the transform.</summary>
        static void HeightQa(string arg)
        {
            var imp = AssetImporter.GetAtPath(Fbx) as ModelImporter;
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
            if (imp == null || src == null) { GKBridge.Log("heightqa: no " + Fbx); return; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder("heightqa: importer globalScale " + imp.globalScale.ToString("F5", inv) + " useFileScale " + imp.useFileScale + " fileScale " + imp.fileScale.ToString("F5", inv));
            var go = (GameObject)UnityEngine.Object.Instantiate(src);
            try
            {
                var body = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(r => r.name == "CC_Base_Body");
                if (body == null) { GKBridge.Log("heightqa: no CC_Base_Body"); return; }
                for (var tr = body.transform; tr != null; tr = tr.parent)
                    sb.Append(" | " + tr.name + " localScale " + tr.localScale.ToString("F5") + " pos " + tr.localPosition.ToString("F4") + " rot " + tr.localEulerAngles.ToString("F1"));
                var mesh = body.sharedMesh;
                sb.Append(" | mesh " + mesh.name + " " + mesh.vertexCount + " verts, bounds size " + mesh.bounds.size.ToString("F4"));
                var vs = mesh.vertices;
                int lo = 0, hi = 0; float ly = float.MaxValue, hy = float.MinValue;
                for (int i = 0; i < vs.Length; i++) { float y = body.transform.TransformPoint(vs[i]).y; if (y < ly) { ly = y; lo = i; } if (y > hy) { hy = y; hi = i; } }
                sb.Append(" | lowest sole v" + lo + " raw " + vs[lo].ToString("F4") + " world y " + ly.ToString("F4", inv) + "; crown v" + hi + " raw " + vs[hi].ToString("F4") + " world y " + hy.ToString("F4", inv) + "; height " + (hy - ly).ToString("F4", inv) + " m");
                var bp = mesh.bindposes;
                if (bp != null && bp.Length > 0) sb.Append(" | bindpose[0] scale " + bp[0].lossyScale.ToString("F5"));
                var named = arg.Split(' ').Where(s => s.Length > 0).Select(s => { int n; return int.TryParse(s, out n) ? n : -1; }).Where(n => n >= 0 && n < vs.Length).ToList();
                foreach (int n in named) sb.Append(" | v" + n + " raw " + vs[n].ToString("F5") + " world " + body.transform.TransformPoint(vs[n]).ToString("F5"));
                if (named.Count == 2) sb.Append(" | named span raw " + Mathf.Abs(vs[named[0]].y - vs[named[1]].y).ToString("F5", inv) + ", world " + Mathf.Abs(body.transform.TransformPoint(vs[named[0]]).y - body.transform.TransformPoint(vs[named[1]]).y).ToString("F5", inv));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
            GKBridge.Log(sb.ToString());
        }

        static float MeasureHeight()
        {
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
            if (src == null) return 0f;
            var go = (GameObject)UnityEngine.Object.Instantiate(src);
            try
            {
                var body = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(r => r.name == "CC_Base_Body");
                if (body == null) return 0f;
                // the bind pose (DECISIONS: the mesh as authored, no skinning, no stance): the raw vertices, in the
                // renderer's frame and scale; the skinned bake of the file's default pose stood 4 cm lower (knees, head)
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var v in body.sharedMesh.vertices) { float y = body.transform.TransformPoint(v).y; lo = Mathf.Min(lo, y); hi = Mathf.Max(hi, y); }
                // the crown from Unity's mesh, the sole from the file (see FileSoleY): barefoot, soles to crown
                var imp0 = AssetImporter.GetAtPath(Fbx) as ModelImporter;
                float scale0 = imp0 != null ? imp0.globalScale : 1f;
                float trueSole = FileSoleY * scale0 + (body.transform.position.y);
                GKBridge.Log("herocc height: lowest vertex Unity keeps " + lo.ToString("F4") + " m (inside the heel), the file's sole " + trueSole.ToString("F4") + " m");
                lo = trueSole;
                var m = new Mesh();
                body.BakeMesh(m);
                float blo = float.MaxValue, bhi = float.MinValue;
                foreach (var v in m.vertices) { float y = body.transform.TransformPoint(v).y; blo = Mathf.Min(blo, y); bhi = Mathf.Max(bhi, y); }
                UnityEngine.Object.DestroyImmediate(m);
                GKBridge.Log("herocc height: CC_Base_Body bind pose " + (hi - lo).ToString("F4") + " m, default pose skinned " + (bhi - blo).ToString("F4") + " m");
                return hi - lo;
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        /// <summary>Bone influences per vertex in each garment vs what the quality level lets the GPU skin with.</summary>
        static void Weights()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null) { GKBridge.Log("herocc weights: no prefab"); return; }
            var imp = (ModelImporter)AssetImporter.GetAtPath(Fbx);
            string s = "herocc weights: importer " + imp.skinWeights + " max " + imp.maxBonesPerVertex + ", quality '" + QualitySettings.names[QualitySettings.GetQualityLevel()] + "' skins with " + QualitySettings.skinWeights + " |";
            foreach (var r in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r.sharedMesh == null) continue;
                var per = r.sharedMesh.GetBonesPerVertex();
                int max = 0, over4 = 0;
                for (int i = 0; i < per.Length; i++) { if (per[i] > max) max = per[i]; if (per[i] > 4) over4++; }
                s += " " + r.name + " max " + max + (over4 > 0 ? " (" + over4 + " verts >4)" : "");
            }
            GKBridge.Log(s);
        }

        /// <summary>Retarget check: the hero next to Mixamo's X Bot (the rig the clips were authored on), both in the
        /// humanoid zero pose (T-pose) and in the main stances, front and side: hs_&lt;pose&gt;_&lt;view&gt;.png.</summary>
        static void Stance()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var xbotSrc = AssetDatabase.LoadAssetAtPath<GameObject>(GKPaths.XBotFbx);
            if (prefab == null || xbotSrc == null) { GKBridge.Log("herocc stance: prefab or X Bot missing"); return; }
            var hidden = new List<GameObject>();
            foreach (var c in UnityEngine.Object.FindObjectsByType<Combatant>(FindObjectsSortMode.None))
                if (c.gameObject.activeSelf) { c.gameObject.SetActive(false); hidden.Add(c.gameObject); }
            var hero = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var xbot = (GameObject)UnityEngine.Object.Instantiate(xbotSrc);
            foreach (var smr in hero.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.forceMatrixRecalculationPerRender = true;
            foreach (var smr in xbot.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.forceMatrixRecalculationPerRender = true;
            var camGo = new GameObject("~hs_cam");
            try
            {
                Quaternion Q = Quaternion.Euler(0f, 180f, 0f);
                Vector3 PH = new Vector3(-0.6f, 0f, 4f), PX = new Vector3(0.6f, 0f, 4f);
                var ctl = hero.GetComponent<CharacterController>(); if (ctl != null) ctl.enabled = false;
                var ha = hero.GetComponent<Animator>();
                var xa = xbot.GetComponent<Animator>(); if (xa == null) xa = xbot.AddComponent<Animator>();
                if (xa.avatar == null) { GKBridge.Log("herocc stance: X Bot has no avatar"); return; }
                var cam = camGo.AddComponent<Camera>(); cam.fieldOfView = 30f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 100f;
                var all = ha.runtimeAnimatorController.animationClips.Distinct().ToList();
                System.Action<string> shoot = tag =>
                {
                    hero.transform.SetPositionAndRotation(PH, Q); xbot.transform.SetPositionAndRotation(PX, Q);
                    Vector3 mid = new Vector3(0f, 0.95f, 4f);
                    cam.transform.position = mid + Vector3.back * -1f * 0f + Q * Vector3.forward * 5.2f; cam.transform.LookAt(mid);
                    GKPreview.Capture(cam, "hs_" + tag + "_front", 900, 700);
                    cam.transform.position = mid + Vector3.right * 5.2f + Vector3.up * 0.1f; cam.transform.LookAt(mid);
                    GKPreview.Capture(cam, "hs_" + tag + "_side", 900, 700);
                };
                // humanoid zero pose: every muscle at 0 is the avatar's own T-pose definition
                foreach (var pair in new[] { new KeyValuePair<Animator, GameObject>(ha, hero), new KeyValuePair<Animator, GameObject>(xa, xbot) })
                {
                    var h = new HumanPoseHandler(pair.Key.avatar, pair.Value.transform);
                    var hp = new HumanPose(); h.GetHumanPose(ref hp);
                    for (int i = 0; i < hp.muscles.Length; i++) hp.muscles[i] = 0f;
                    hp.bodyPosition = new Vector3(0f, hp.bodyPosition.y, 0f); hp.bodyRotation = Quaternion.identity;
                    h.SetHumanPose(ref hp); h.Dispose();
                }
                shoot("tpose");
                var poses = new[] { new KeyValuePair<string, float>("AX_standing_idle", 0.3f), new KeyValuePair<string, float>("AX_unarmed_idle", 0.3f),
                                    new KeyValuePair<string, float>("GK_Run_Sword", 0.3f), new KeyValuePair<string, float>("AX_standing_walk_forward", 0.3f) };
                foreach (var pz in poses)
                {
                    var clip = all.FirstOrDefault(c => c.name == pz.Key);
                    if (clip == null) { GKBridge.Log("herocc stance: no clip " + pz.Key); continue; }
                    clip.SampleAnimation(hero, clip.length * pz.Value);
                    clip.SampleAnimation(xbot, clip.length * pz.Value);
                    shoot(pz.Key);
                }
                GKBridge.Log("herocc stance: hs_*.png (hero left, X Bot right)");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(hero);
                UnityEngine.Object.DestroyImmediate(xbot);
                foreach (var g in hidden) if (g != null) g.SetActive(true);
            }
        }

        /// <summary>The sword hand close up in the main holds (guard, cut, power cut, block, run): hg_&lt;clip&gt;_&lt;view&gt;.png.</summary>
        static void Grip()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null) { GKBridge.Log("herocc grip: no prefab"); return; }
            var hidden = new System.Collections.Generic.List<GameObject>();
            foreach (var c in UnityEngine.Object.FindObjectsByType<Combatant>(FindObjectsSortMode.None))
                if (c.gameObject.activeSelf) { c.gameObject.SetActive(false); hidden.Add(c.gameObject); }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var camGo = new GameObject("~hg_cam");
            // several poses are rendered inside one editor frame: skin again for every render
            foreach (var smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.forceMatrixRecalculationPerRender = true;
            try
            {
                Vector3 P = new Vector3(0f, 0f, 4f); Quaternion Q = Quaternion.Euler(0f, 180f, 0f);
                inst.transform.SetPositionAndRotation(P, Q);
                var ctl = inst.GetComponent<CharacterController>(); if (ctl != null) ctl.enabled = false;
                var anim = inst.GetComponent<Animator>();
                var cam = camGo.AddComponent<Camera>(); cam.fieldOfView = 26f; cam.nearClipPlane = 0.01f; cam.farClipPlane = 60f;
                var all = anim.runtimeAnimatorController.animationClips.Distinct().ToList();
                var holds = new[] { new KeyValuePair<string, float>("AX_standing_idle", 0.3f), new KeyValuePair<string, float>("GK_Slash_Outward", 0.45f),
                                    new KeyValuePair<string, float>("SS_Power_Slash", 0.55f), new KeyValuePair<string, float>("AX_standing_block_idle", 0.5f),
                                    new KeyValuePair<string, float>("GK_Run_Sword", 0.5f) };
                foreach (var h in holds)
                {
                    var clip = all.FirstOrDefault(c => c.name == h.Key);
                    if (clip == null) { GKBridge.Log("herocc grip: no clip " + h.Key); continue; }
                    clip.SampleAnimation(inst, clip.length * h.Value);
                    inst.transform.SetPositionAndRotation(P, Q);
                    var hand = anim.GetBoneTransform(HumanBodyBones.RightHand);
                    var mid = anim.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
                    Vector3 at = mid != null ? Vector3.Lerp(hand.position, mid.position, 0.6f) : hand.position;
                    Vector3 F = inst.transform.forward, R = inst.transform.right, U = Vector3.up;
                    var views = new[] { new KeyValuePair<string, Vector3>("front", F + U * 0.2f), new KeyValuePair<string, Vector3>("thumb", R * 0.2f + U + F * 0.3f),
                                        new KeyValuePair<string, Vector3>("out", R + U * 0.15f), new KeyValuePair<string, Vector3>("in", -R + F * 0.4f + U * 0.1f) };
                    foreach (var vw in views)
                    {
                        cam.transform.position = at + vw.Value.normalized * 0.42f; cam.transform.LookAt(at);
                        GKPreview.Capture(cam, "hg_" + h.Key + "_" + vw.Key, 480, 480);
                    }
                }
                GKBridge.Log("herocc grip: hand shots hg_*.png");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(inst);
                foreach (var g in hidden) if (g != null) g.SetActive(true);
            }
        }

        static readonly string[] MacroClips =
        {
            "AX_standing_melee_attack_downward", "AX_standing_melee_attack_360_high", "SS_Power_Slash", "GK_Slash_Outward",
            "GK_Slash_Inward", "AX_standing_melee_attack_backhand", "SS_Cross_Slash", "AX_standing_block_idle", "GK_Sword_Draw"
        };

        /// <summary>Macro shots of both shoulders through the attacks (40 % and 65 %): front, back and top, on magenta so a
        /// hole (faces Character Creator deleted as hidden) shows at once. "herocc macro" uses the quality level's skin
        /// weights; "herocc macro unlimited" renders with every influence the meshes carry, for comparison.
        /// Files: hm_&lt;clip&gt;_&lt;pct&gt;_&lt;L|R&gt;_&lt;view&gt;.png (and hmu_ for unlimited).</summary>
        static void Macro(string arg)
        {
            bool unlimited = arg.Contains("unlimited");
            // "hide:Tunic,CC_Base_Body" hides those renderers (what shows through a gap?), "tag:x" prefixes the files
            string hide = null, tagExtra = "";
            var words = arg.Split(' ').Where(w => w.Length > 0).ToList();
            foreach (var w in words.ToList())
            {
                if (w.StartsWith("hide:")) { hide = w.Substring(5); words.Remove(w); }
                else if (w.StartsWith("tag:")) { tagExtra = w.Substring(4) + "_"; words.Remove(w); }
            }
            string filter = string.Join(" ", words.ToArray()).Replace("unlimited", "").Trim();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null) { GKBridge.Log("herocc macro: no prefab"); return; }
            var hidden = new System.Collections.Generic.List<GameObject>();
            foreach (var c in UnityEngine.Object.FindObjectsByType<Combatant>(FindObjectsSortMode.None))
                if (c.gameObject.activeSelf) { c.gameObject.SetActive(false); hidden.Add(c.gameObject); }
            var oldSkin = QualitySettings.skinWeights;
            if (unlimited) QualitySettings.skinWeights = SkinWeights.Unlimited;
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var camGo = new GameObject("~hm_cam");
            // several poses are rendered inside one editor frame: skin again for every render
            foreach (var smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.forceMatrixRecalculationPerRender = true;
            try
            {
                Vector3 P = new Vector3(0f, 0f, 4f);
                Quaternion Q = Quaternion.Euler(0f, 180f, 0f);
                inst.transform.SetPositionAndRotation(P, Q);
                var ctl = inst.GetComponent<CharacterController>(); if (ctl != null) ctl.enabled = false;
                var anim = inst.GetComponent<Animator>();
                var cam = camGo.AddComponent<Camera>(); cam.fieldOfView = 24f; cam.nearClipPlane = 0.02f; cam.farClipPlane = 50f;
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(1f, 0f, 1f);
                var all = anim.runtimeAnimatorController.animationClips.Distinct().ToList();
                var clips = string.IsNullOrEmpty(filter)
                    ? MacroClips.Select(n => all.FirstOrDefault(c => c.name == n)).Where(c => c != null).ToList()
                    : all.Where(c => filter.Split(',').Any(f => c.name.ToLowerInvariant().Contains(f.Trim().ToLowerInvariant()))).ToList();
                string pre = (unlimited ? "hmu_" : "hm_") + tagExtra;
                if (hide != null)
                    foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
                        if (hide.Split(',').Contains(r.name)) r.enabled = false;
                int shots = 0;
                foreach (var clip in clips)
                    foreach (int pct in new[] { 40, 65 })
                    {
                        clip.SampleAnimation(inst, clip.length * pct / 100f);
                        inst.transform.SetPositionAndRotation(P, Q);
                        Vector3 F = inst.transform.forward, R = inst.transform.right, U = Vector3.up;
                        foreach (var side in new[] { "L", "R" })
                        {
                            var arm = anim.GetBoneTransform(side == "L" ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
                            var sh = anim.GetBoneTransform(side == "L" ? HumanBodyBones.LeftShoulder : HumanBodyBones.RightShoulder);
                            Vector3 at = Vector3.Lerp(sh != null ? sh.position : arm.position, arm.position, 0.7f);
                            Vector3 outDir = side == "L" ? -R : R;
                            string tag = pre + clip.name + "_" + pct + "_" + side;
                            cam.transform.position = at + (F * 0.85f + outDir * 0.35f + U * 0.12f).normalized * 0.75f; cam.transform.LookAt(at);
                            GKPreview.Capture(cam, tag + "_front", 520, 520);
                            cam.transform.position = at + (-F * 0.85f + outDir * 0.35f + U * 0.12f).normalized * 0.75f; cam.transform.LookAt(at);
                            GKPreview.Capture(cam, tag + "_back", 520, 520);
                            cam.transform.position = at + (U * 0.9f + outDir * 0.3f + F * 0.15f).normalized * 0.75f; cam.transform.LookAt(at);
                            GKPreview.Capture(cam, tag + "_top", 520, 520);
                            shots += 3;
                        }
                    }
                GKBridge.Log("herocc macro: " + clips.Count + " clips, " + shots + " shots (" + pre + "*.png), skin weights " + QualitySettings.skinWeights);
            }
            finally
            {
                QualitySettings.skinWeights = oldSkin;
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(inst);
                foreach (var g in hidden) if (g != null) g.SetActive(true);
            }
        }

        static readonly string[] PoseClips =
        {
            "AX_standing_idle", "AX_standing_walk_forward", "GK_Run_Sword", "AX_standing_melee_attack_360_high", "SS_Power_Slash",
            "SS_Sparta_Kick", "AX_standing_melee_attack_kick_ver_1", "GK_Dodge_Left", "GK_Roll_Stand", "GK_Sword_Draw"
        };

        /// <summary>The prefab posed by the game's own clips (edit mode, deterministic): each clip at 30 / 55 / 80 %,
        /// the scabbard settled, photographed from the front, the scabbard side and behind (hp_&lt;clip&gt;_&lt;pct&gt;_&lt;view&gt;.png).
        /// "herocc poses" runs the default set; "herocc poses kick,slash" filters by name.</summary>
        static void Poses(string filter)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null) { GKBridge.Log("herocc poses: no prefab"); return; }
            var hidden = new System.Collections.Generic.List<GameObject>();
            foreach (var c in UnityEngine.Object.FindObjectsByType<Combatant>(FindObjectsSortMode.None))
                if (c.gameObject.activeSelf) { c.gameObject.SetActive(false); hidden.Add(c.gameObject); }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var camGo = new GameObject("~hp_cam");
            // several poses are rendered inside one editor frame: skin again for every render
            foreach (var smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.forceMatrixRecalculationPerRender = true;
            try
            {
                Vector3 P = new Vector3(0f, 0f, 4f);
                Quaternion Q = Quaternion.Euler(0f, 180f, 0f);
                inst.transform.SetPositionAndRotation(P, Q);
                var ctl = inst.GetComponent<CharacterController>(); if (ctl != null) ctl.enabled = false;
                var anim = inst.GetComponent<Animator>();
                var hips = anim.GetBoneTransform(HumanBodyBones.Hips);
                var scab = inst.GetComponent<ScabbardMotion>();
                if (scab != null) scab.Init();
                var cam = camGo.AddComponent<Camera>(); cam.fieldOfView = 40f; cam.nearClipPlane = 0.02f; cam.farClipPlane = 3000f;
                var all = anim.runtimeAnimatorController.animationClips.Distinct().ToList();
                var clips = string.IsNullOrEmpty(filter)
                    ? PoseClips.Select(n => all.FirstOrDefault(c => c.name == n)).Where(c => c != null).ToList()
                    : all.Where(c => filter.Split(',').Any(f => c.name.ToLowerInvariant().Contains(f.Trim().ToLowerInvariant()))).ToList();
                int shots = 0;
                foreach (var clip in clips)
                    foreach (int pct in new[] { 30, 55, 80 })
                    {
                        float t = clip.length * pct / 100f;
                        if (scab != null) scab.ResetDynamics();
                        for (int k = 0; k < 40; k++)
                        {
                            clip.SampleAnimation(inst, t);
                            inst.transform.SetPositionAndRotation(P, Q);
                            if (scab != null) scab.Solve(1f / 60f);
                        }
                        Vector3 hip = hips.position;
                        Vector3 F = inst.transform.forward, R = inst.transform.right;
                        Vector3 at = new Vector3(hip.x, P.y + 0.92f, hip.z);
                        string tag = "hp_" + clip.name + "_" + pct;
                        cam.transform.position = at + F * 2.5f + Vector3.up * 0.15f; cam.transform.LookAt(at);
                        GKPreview.Capture(cam, tag + "_front", 700, 900);
                        cam.transform.position = at - R * 2.5f + F * 0.4f + Vector3.up * 0.15f; cam.transform.LookAt(at);
                        GKPreview.Capture(cam, tag + "_left", 700, 900);
                        cam.transform.position = at - F * 2.3f + R * 1.0f + Vector3.up * 0.25f; cam.transform.LookAt(at);
                        GKPreview.Capture(cam, tag + "_back", 700, 900);
                        shots += 3;
                        if (scab != null)
                            GKBridge.Log("herocc poses: " + clip.name + " " + pct + "% scabbard swing " + scab.LastSwing.ToString("F0") + " follow " + scab.LastFollow.ToString("F0") +
                                         " clearance " + scab.LastClearance.ToString("F3") + " residual " + scab.LastResidual.ToString("F3") +
                                         " hang " + Vector3.Angle(scab.scabbard.forward, Vector3.down).ToString("F0") + " deg from down");
                    }
                GKBridge.Log("herocc poses: " + clips.Count + " clips, " + shots + " shots (hp_*.png)");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(inst);
                foreach (var g in hidden) if (g != null) g.SetActive(true);
            }
        }

        /// <summary>Close shots with layers hidden one at a time, to see what lies under the brows, the hair and the
        /// belt (holes from Character Creator's "delete hidden faces" show as sky): hc_*.png in GKBridge.</summary>
        static void Diag()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null) { GKBridge.Log("herocc diag: no prefab"); return; }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var camGo = new GameObject("~hc_cam");
            try
            {
                go.transform.SetPositionAndRotation(new Vector3(-40f, 0f, 40f), Quaternion.identity);
                var cam = camGo.AddComponent<Camera>(); cam.fieldOfView = 22f; cam.nearClipPlane = 0.02f; cam.farClipPlane = 3000f;
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(1f, 0f, 1f);   // magenta: any hole shows
                var anim = go.GetComponent<Animator>();
                var head = anim.GetBoneTransform(HumanBodyBones.Head);
                var hips = anim.GetBoneTransform(HumanBodyBones.Hips);
                var rs = go.GetComponentsInChildren<Renderer>(true);
                System.Action<string, Vector3, Vector3, System.Func<Renderer, bool>> shot = (n, at, from, keep) =>
                {
                    foreach (var r in rs) r.enabled = keep(r);
                    cam.transform.position = at + from; cam.transform.LookAt(at);
                    GKPreview.Capture(cam, "hc_" + n, 1000, 1000);
                };
                Vector3 hp = head.position + Vector3.up * 0.07f;
                Vector3 fwd = go.transform.forward, right = go.transform.right;
                shot("face_all", hp, fwd * 0.55f, r => true);
                shot("face_body_only", hp, fwd * 0.55f, r => r.name == "CC_Base_Body");
                shot("top_body_only", hp + Vector3.up * 0.05f, Vector3.up * 0.5f + fwd * 0.25f, r => r.name == "CC_Base_Body");
                shot("top_all", hp + Vector3.up * 0.05f, Vector3.up * 0.5f + fwd * 0.25f, r => true);
                Vector3 waist = hips.position + Vector3.up * 0.12f;
                shot("belt_side_all", waist, -right * 0.9f + fwd * 0.3f, r => true);
                shot("belt_side_nobelt", waist, -right * 0.9f + fwd * 0.3f, r => !r.name.StartsWith("Strip"));
                shot("belt_back_all", waist, -fwd * 0.9f - right * 0.2f, r => true);
                GKBridge.Log("herocc diag: renderers " + string.Join(", ", rs.Select(r => r.name).Distinct().ToArray()));
            }
            finally { UnityEngine.Object.DestroyImmediate(camGo); UnityEngine.Object.DestroyImmediate(go); }
        }

        static bool Copy()
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            string src = Path.Combine(root, SourceDir);
            string dst = Path.Combine(root, Dir);
            string fbxSrc = new[] { ".Fbx", ".fbx", ".FBX" }.Select(e => Path.Combine(src, Name + e)).FirstOrDefault(File.Exists);
            if (fbxSrc == null || !File.Exists(Path.Combine(src, Name + ".json"))) { GKBridge.Log("herocc: export missing in " + src); return false; }
            Directory.CreateDirectory(dst);
            File.Copy(fbxSrc, Path.Combine(dst, Name + ".fbx"), true);
            File.Copy(Path.Combine(src, Name + ".json"), Path.Combine(dst, Name + ".json"), true);
            int n = 2;
            n += CopyDir(Path.Combine(src, Name + ".fbm"), Path.Combine(dst, Name + ".fbm"));
            n += CopyDir(Path.Combine(src, "textures", Name), Path.Combine(dst, "textures", Name));
            GKBridge.Log("herocc: copied " + n + " files to " + Dir + ", importing");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            bool ok = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx) != null;
            GKBridge.Log("herocc: fbx " + (ok ? "imported" : "NOT imported"));
            return ok;
        }

        static int CopyDir(string from, string to)
        {
            if (!Directory.Exists(from)) return 0;
            int n = 0;
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(from.Length).TrimStart('\\', '/');
                string t = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(t));
                File.Copy(f, t, true);
                n++;
            }
            return n;
        }

        /// <summary>CC Unity Tools' own import (the same call its batch window makes), through reflection so our editor
        /// assembly needs no reference to the package.</summary>
        static bool Import()
        {
            string guid = AssetDatabase.AssetPathToGUID(Fbx);
            if (string.IsNullOrEmpty(guid)) { GKBridge.Log("herocc: no " + Fbx); return false; }
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == "soupday.cc3_unity_tools.editor");
            if (asm == null) { GKBridge.Log("herocc: CC Unity Tools not loaded"); return false; }
            var tInfo = asm.GetType("Reallusion.Import.CharacterInfo");
            var tImp = asm.GetType("Reallusion.Import.Importer");
            var info = Activator.CreateInstance(tInfo, new object[] { guid });
            SetEnum(tInfo, info, "BuildQuality", "High");
            SetEnum(tInfo, info, "QualEyes", "Parallax");
            SetEnum(tInfo, info, "QualHair", "TwoPass");
            SetEnum(tInfo, info, "RigType", "Humanoid");
            SetEnum(tInfo, info, "QualTexSize", "HighTextureSize");
            SetEnum(tInfo, info, "QualTexCompress", "HighTextureQuality");
            var pFlags = tInfo.GetProperty("ShaderFlags");
            pFlags.SetValue(info, Enum.ToObject(pFlags.PropertyType, Flags));
            tInfo.GetMethod("FixCharSettings").Invoke(info, null);
            tInfo.GetMethod("Refresh", Type.EmptyTypes).Invoke(info, null);
            GameObject prefab = null;
            try
            {
                var imp = Activator.CreateInstance(tImp, new object[] { info });
                prefab = (GameObject)tImp.GetMethod("Import").Invoke(imp, new object[] { true });
            }
            catch (Exception e)
            {
                GKBridge.Log("herocc: CC import failed: " + (e.InnerException ?? e).Message);
                return false;
            }
            finally
            {
                tInfo.GetMethod("Write").Invoke(info, null);
                var rel = tInfo.GetMethod("Release"); if (rel != null) rel.Invoke(info, null);
            }
            GKBridge.Log("herocc: CC Unity Tools built " + (prefab != null ? AssetDatabase.GetAssetPath(prefab) : "nothing") +
                         ", flags " + pFlags.GetValue(info));
            return prefab != null;
        }

        static void SetEnum(Type t, object o, string prop, string value)
        {
            var p = t.GetProperty(prop);
            if (p == null) { GKBridge.Log("herocc: no property " + prop); return; }
            p.SetValue(o, Enum.Parse(p.PropertyType, value));
        }

        public const int ClothColliderLayer = 12;

        /// <summary>Unity Cloth on the coat (false: the coat follows the skin weights only).</summary>
        public static bool CoatPhysics = false;

        /// <summary>
        /// The coat's Unity Cloth, settled for a fighter: it collides with the hips and legs only (an arm capsule caught
        /// the front panel and folded it up over the forearm when running), it follows the body's own motion more and
        /// the world's less (no flag-like lift at running speed), and tethers keep it from stretching.
        /// </summary>
        static void TuneCoat(GameObject go)
        {
            System.Func<Component, bool> isArm = c =>
            {
                string n = c.name.ToLowerInvariant();
                return n.Contains("arm") || n.Contains("hand") || n.Contains("clavicle") || n.Contains("elbow") || n.Contains("finger") || n.Contains("shoulder");
            };
            if (!CoatPhysics)
            {
                // the coat as Character Creator skinned it, no simulation: exactly what was approved at import, and
                // nothing that can flap, hook or stretch in a fight
                foreach (var cl in go.GetComponentsInChildren<Cloth>(true))
                {
                    GKBridge.Log("herocc: coat " + cl.name + " skinned only (Unity Cloth removed)");
                    UnityEngine.Object.DestroyImmediate(cl);
                }
                return;
            }
            foreach (var cl in go.GetComponentsInChildren<Cloth>(true))
            {
                var caps = (cl.capsuleColliders ?? new CapsuleCollider[0]).Where(c => c != null).ToArray();
                var keep = caps.Where(c => !isArm(c)).ToArray();
                cl.capsuleColliders = keep;
                // the capsules were fitted to the skin; the coat lies on the breeches and the tunic, so they grow by
                // that thickness (otherwise the breeches showed through the coat over the thigh). Done once: the
                // original radius is kept in the collider's name suffix check.
                foreach (var c in keep)
                {
                    if (c.name.EndsWith("_GK")) continue;
                    string n = c.name.ToLowerInvariant();
                    float add = n.Contains("thigh") ? 0.025f : 0.02f;
                    var ls = c.transform.lossyScale;
                    float s = Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
                    c.radius += add / Mathf.Max(s, 1e-4f);     // world metres, whatever the bone scale
                    c.name += "_GK";
                }
                var spheres = (cl.sphereColliders ?? new ClothSphereColliderPair[0])
                    .Where(p => (p.first == null || !isArm(p.first)) && (p.second == null || !isArm(p.second))).ToArray();
                cl.sphereColliders = spheres;
                cl.worldVelocityScale = 0.25f;
                cl.worldAccelerationScale = 0.35f;
                cl.damping = Mathf.Max(cl.damping, 0.5f);
                cl.useTethers = true;
                cl.friction = Mathf.Max(cl.friction, 0.4f);
                GKBridge.Log("herocc: coat cloth " + cl.name + ": colliders kept " + string.Join(", ", keep.Select(c => c.name).ToArray()) +
                             " | dropped " + string.Join(", ", caps.Where(isArm).Select(c => c.name).ToArray()) +
                             " | velocity 0.25, acceleration 0.35, damping " + cl.damping.ToString("F2") + ", tethers on");
            }
        }

        /// <summary>Layer 12 "ClothCollider": named, and set to collide with no layer (cloth reads its colliders directly).</summary>
        static void EnsureClothColliderLayer()
        {
            var tm = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var p = tm.FindProperty("layers").GetArrayElementAtIndex(ClothColliderLayer);
            if (string.IsNullOrEmpty(p.stringValue) || p.stringValue != "ClothCollider")
            {
                if (!string.IsNullOrEmpty(p.stringValue) && p.stringValue != "ClothCollider") { GKBridge.Log("herocc: layer 12 is '" + p.stringValue + "', not touched"); return; }
                p.stringValue = "ClothCollider";
                tm.ApplyModifiedProperties();
            }
            for (int i = 0; i < 32; i++) Physics.IgnoreLayerCollision(ClothColliderLayer, i, true);
            AssetDatabase.SaveAssets();
        }

        /// <summary>The belt, gloves and boots read as worn leather, not lacquer: the packed gloss map is scaled down
        /// (CC exports these with a near-mirror smoothness scale of 0.9, which caught the sky as white edges).</summary>
        static void LeatherSheen()
        {
            string matDir = Dir + "/Materials/" + Name;
            var scale = new System.Collections.Generic.Dictionary<string, float>
            {
                { "THD_Strips_OKSG", 0.55f }, { "THD_Strips_OKSG1", 0.55f }, { "THD_Strips_OKSG2", 0.55f }, { "THD_Strips_OKSG3", 0.55f },
                { "THD_Gloves_0212SG", 0.6f }, { "THD_Gloves_0212SG1", 0.6f }, { "Boots", 0.65f },
                { "Long_slim_coat", 0.6f }, { "Tunic", 0.5f }, { "THD_Pants", 0.55f },
            };
            int n = 0;
            foreach (var kv in scale)
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(matDir + "/" + kv.Key + ".mat");
                if (m == null || !m.HasProperty("_Smoothness")) continue;
                m.SetFloat("_Smoothness", kv.Value);
                EditorUtility.SetDirty(m);
                n++;
            }
            // the blade: polished steel, but not a mirror (at full gloss it showed nothing but blue sky)
            var steel = GKCharacters.SwordMaterial;
            if (steel != null && steel.HasProperty("_Smoothness")) { steel.SetFloat("_Smoothness", 0.72f); EditorUtility.SetDirty(steel); n++; }
            AssetDatabase.SaveAssets();
            GKBridge.Log("herocc: leather, cloth and steel sheen set on " + n + " materials");
        }

        /// <summary>The tunic under the coat is seen only through the coat's front opening and at the neck. Where the coat
        /// always covers it (back, shoulders, sleeves) it pushed through the coat whenever the sword arm came up. Those
        /// faces are removed: a tunic vertex counts as covered when rays along its normal and four 35-degree tilts all
        /// meet the coat within 8 cm at the bind pose; a triangle goes only when all three of its vertices are covered.
        /// The trimmed copy is Generated/&lt;name&gt;_covered_trim.asset (the FBX is untouched).</summary>
        static void TrimCovered(GameObject go, string target)
        {
            var smrs = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var coat = smrs.FirstOrDefault(r => r.name == "Long_slim_coat");
            var t = smrs.FirstOrDefault(r => r.name == target);
            if (coat == null || t == null || t.sharedMesh == null) { GKBridge.Log("herocc: trim " + target + " skipped (coat or target missing)"); return; }
            var coatBaked = new Mesh();
            coat.BakeMesh(coatBaked, true);
            var probe = new GameObject("~coat_probe");
            probe.layer = ClothColliderLayer;
            probe.transform.SetPositionAndRotation(coat.transform.position, coat.transform.rotation);
            var mc = probe.AddComponent<MeshCollider>();
            mc.sharedMesh = coatBaked;
            bool oldBack = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            Physics.SyncTransforms();
            try
            {
                var baked = new Mesh();
                t.BakeMesh(baked, true);
                var v = baked.vertices; var n = baked.normals;
                var pos = t.transform.position; var rot = t.transform.rotation;
                int mask = 1 << ClothColliderLayer;
                var covered = new bool[v.Length];
                int nc = 0;
                for (int i = 0; i < v.Length; i++)
                {
                    Vector3 p = pos + rot * v[i];
                    Vector3 nw = (rot * n[i]).normalized;
                    if (nw.sqrMagnitude < 0.5f) continue;
                    Vector3 a = Vector3.Cross(nw, Mathf.Abs(nw.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                    Vector3 b = Vector3.Cross(nw, a);
                    bool all = true;
                    foreach (var d in new[] { nw, (nw + 0.7f * a).normalized, (nw - 0.7f * a).normalized, (nw + 0.7f * b).normalized, (nw - 0.7f * b).normalized })
                        if (!Physics.Raycast(p - nw * 0.002f, d, 0.08f, mask, QueryTriggerInteraction.Collide)) { all = false; break; }
                    covered[i] = all;
                    if (all) nc++;
                }
                UnityEngine.Object.DestroyImmediate(baked);
                var src = t.sharedMesh;
                var trimmed = UnityEngine.Object.Instantiate(src);
                trimmed.name = src.name + "_covered_trim";
                int before = 0, after = 0;
                for (int sm = 0; sm < src.subMeshCount; sm++)
                {
                    var tris = src.GetTriangles(sm);
                    var keep = new System.Collections.Generic.List<int>(tris.Length);
                    for (int k = 0; k < tris.Length; k += 3)
                    {
                        before++;
                        if (covered[tris[k]] && covered[tris[k + 1]] && covered[tris[k + 2]]) continue;
                        keep.Add(tris[k]); keep.Add(tris[k + 1]); keep.Add(tris[k + 2]);
                        after++;
                    }
                    trimmed.SetTriangles(keep, sm, true);
                }
                string dir = Dir + "/Generated";
                if (!AssetDatabase.IsValidFolder(dir)) AssetDatabase.CreateFolder(Dir, "Generated");
                string path = dir + "/" + target + "_covered_trim.asset";
                if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
                AssetDatabase.CreateAsset(trimmed, path);
                t.sharedMesh = trimmed;
                GKBridge.Log("herocc: " + target + " trimmed under the coat: " + nc + " of " + v.Length + " vertices covered, triangles " + before + " -> " + after);
            }
            finally
            {
                Physics.queriesHitBackfaces = oldBack;
                UnityEngine.Object.DestroyImmediate(probe);
                UnityEngine.Object.DestroyImmediate(coatBaked);
            }
        }

        /// <summary>The coat is cut like a real coat: the sleeves and the lapels are separate pieces whose edges meet the
        /// body panels. Each piece got its own weights, so with the arm raised overhead the sleeve's armhole edge followed
        /// the arm and the body panel's edge stayed with the chest, and the seam opened (the lining showed in the armpit).
        /// Open-edge vertices of one piece that lie within 1.2 cm of an open edge of another piece take the averaged
        /// weights of both, so seams stay closed in every pose. Writes Generated/&lt;name&gt;_welded.asset.</summary>
        static void WeldSeams(GameObject go, string target)
        {
            var t = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(r => r.name == target);
            if (t == null || t.sharedMesh == null) { GKBridge.Log("herocc: weld " + target + " skipped"); return; }
            var src = t.sharedMesh;
            var v = src.vertices;
            int nv = v.Length;
            // positions are merged first (UV / normal splits are the same point on the garment)
            var key = new int[nv];
            var byPos = new System.Collections.Generic.Dictionary<Vector3Int, int>();
            for (int i = 0; i < nv; i++)
            {
                var q = new Vector3Int(Mathf.RoundToInt(v[i].x * 20000f), Mathf.RoundToInt(v[i].y * 20000f), Mathf.RoundToInt(v[i].z * 20000f));
                int k; if (!byPos.TryGetValue(q, out k)) { k = i; byPos[q] = i; }
                key[i] = k;
            }
            // islands (connected pieces) and open edges on merged positions
            var parent = new int[nv]; for (int i = 0; i < nv; i++) parent[i] = i;
            System.Func<int, int> find = null;
            find = a => { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; };
            var edgeCount = new System.Collections.Generic.Dictionary<long, int>();
            System.Action<int, int> edge = (a, b) => { long e = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a; int c; edgeCount.TryGetValue(e, out c); edgeCount[e] = c + 1; };
            for (int sm = 0; sm < src.subMeshCount; sm++)
            {
                var tris = src.GetTriangles(sm);
                for (int k = 0; k < tris.Length; k += 3)
                {
                    int a = key[tris[k]], b = key[tris[k + 1]], c = key[tris[k + 2]];
                    parent[find(a)] = find(b); parent[find(b)] = find(c);
                    edge(a, b); edge(b, c); edge(c, a);
                }
            }
            var open = new System.Collections.Generic.HashSet<int>();
            foreach (var kv in edgeCount) if (kv.Value == 1) { open.Add((int)(kv.Key >> 32)); open.Add((int)(kv.Key & 0xffffffff)); }
            var islands = new System.Collections.Generic.HashSet<int>();
            foreach (var i in open) islands.Add(find(i));
            // pair open-edge points of different pieces within the tolerance (scale-aware: the mesh may be in cm)
            float unit = 1f / Mathf.Max(1e-6f, t.transform.lossyScale.x);
            float tol = 0.012f * unit;
            var openList = open.ToList();
            var cell = new System.Collections.Generic.Dictionary<Vector3Int, System.Collections.Generic.List<int>>();
            System.Func<Vector3, Vector3Int> cellOf = p => new Vector3Int(Mathf.FloorToInt(p.x / tol), Mathf.FloorToInt(p.y / tol), Mathf.FloorToInt(p.z / tol));
            foreach (var i in openList)
            {
                var c = cellOf(v[i]);
                System.Collections.Generic.List<int> l; if (!cell.TryGetValue(c, out l)) { l = new System.Collections.Generic.List<int>(); cell[c] = l; }
                l.Add(i);
            }
            var partner = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<int>>();
            int pairs = 0;
            foreach (var i in openList)
            {
                var c = cellOf(v[i]);
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                {
                    System.Collections.Generic.List<int> l;
                    if (!cell.TryGetValue(new Vector3Int(c.x + dx, c.y + dy, c.z + dz), out l)) continue;
                    foreach (var j in l)
                    {
                        if (j <= i || find(j) == find(i) || (v[j] - v[i]).sqrMagnitude > tol * tol) continue;
                        System.Collections.Generic.List<int> pi, pj;
                        if (!partner.TryGetValue(i, out pi)) { pi = new System.Collections.Generic.List<int>(); partner[i] = pi; }
                        if (!partner.TryGetValue(j, out pj)) { pj = new System.Collections.Generic.List<int>(); partner[j] = pj; }
                        pi.Add(j); pj.Add(i); pairs++;
                    }
                }
            }
            if (pairs == 0) { GKBridge.Log("herocc: weld " + target + ": " + islands.Count + " pieces, no seams within 1.2 cm"); return; }
            // averaged weights for every vertex sharing a merged position with a seam point
            var per = src.GetBonesPerVertex();
            var all = src.GetAllBoneWeights();
            var start = new int[nv]; int acc = 0;
            for (int i = 0; i < nv; i++) { start[i] = acc; acc += per[i]; }
            System.Func<int, System.Collections.Generic.Dictionary<int, float>> weightsOf = i =>
            {
                var d = new System.Collections.Generic.Dictionary<int, float>();
                for (int k = 0; k < per[i]; k++) { var w = all[start[i] + k]; float x; d.TryGetValue(w.boneIndex, out x); d[w.boneIndex] = x + w.weight; }
                return d;
            };
            var newW = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.Dictionary<int, float>>();
            foreach (var kv in partner)
            {
                var sum = weightsOf(kv.Key);
                foreach (var j in kv.Value) foreach (var w in weightsOf(j)) { float x; sum.TryGetValue(w.Key, out x); sum[w.Key] = x + w.Value; }
                newW[kv.Key] = sum;
            }
            var outPer = new byte[nv];
            var outW = new System.Collections.Generic.List<BoneWeight1>(all.Length + pairs * 4);
            int changed = 0;
            for (int i = 0; i < nv; i++)
            {
                System.Collections.Generic.Dictionary<int, float> d;
                if (newW.TryGetValue(key[i], out d) || newW.TryGetValue(i, out d))
                {
                    var top = d.OrderByDescending(x => x.Value).Take(8).ToList();
                    float total = top.Sum(x => x.Value);
                    outPer[i] = (byte)top.Count;
                    foreach (var x in top) outW.Add(new BoneWeight1 { boneIndex = x.Key, weight = x.Value / total });
                    changed++;
                }
                else
                {
                    outPer[i] = per[i];
                    for (int k = 0; k < per[i]; k++) outW.Add(all[start[i] + k]);
                }
            }
            var welded = UnityEngine.Object.Instantiate(src);
            welded.name = src.name + "_welded";
            var perNA = new Unity.Collections.NativeArray<byte>(outPer, Unity.Collections.Allocator.Temp);
            var wNA = new Unity.Collections.NativeArray<BoneWeight1>(outW.ToArray(), Unity.Collections.Allocator.Temp);
            welded.SetBoneWeights(perNA, wNA);
            perNA.Dispose(); wNA.Dispose();
            string dir = Dir + "/Generated";
            if (!AssetDatabase.IsValidFolder(dir)) AssetDatabase.CreateFolder(Dir, "Generated");
            string path = dir + "/" + target + "_welded.asset";
            if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(welded, path);
            t.sharedMesh = welded;
            GKBridge.Log("herocc: " + target + " seams welded: " + islands.Count + " pieces with open edges, " + pairs + " seam pairs, " + changed + " vertices re-weighted");
        }

        /// <summary>How far the sword slides toward its tip in the hand (m).</summary>
        public static float GripSlide = 0.008f;
        /// <summary>Handle axis to knuckle-joint line distance in a closed fist (m): handle radius 1.6 cm + the finger root.</summary>
        public static float GripSeat = 0.034f;

        public const string TwoSidedShader = "GeorgianKnight/Lit Two-Sided Cloth";

        /// <summary>Character Creator exports the coat, pants, boots and belt straps double-sided (Cull Off). Stock URP Lit
        /// lights a back face with the outward normal, which points away from the camera, so the inside of the coat and
        /// the belt tails flashed pale blue-white. Those materials move to the same Lit shader with back faces lit from
        /// the viewer's side; hair, lashes and other alpha cards keep their shaders.</summary>
        static void TwoSidedCloth(GameObject go)
        {
            var sh = Shader.Find(TwoSidedShader);
            if (sh == null) { GKBridge.Log("herocc: shader '" + TwoSidedShader + "' not found — linings keep stock URP Lit"); return; }
            if (ShaderUtil.ShaderHasError(sh))
            {
                foreach (var msg in ShaderUtil.GetShaderMessages(sh)) GKBridge.Log("herocc: shader error " + msg.message + " (" + msg.file + ":" + msg.line + ")");
                GKBridge.Log("herocc: two-sided shader has errors — linings keep stock URP Lit");
                return;
            }
            var done = new System.Collections.Generic.List<string>();
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null || done.Contains(m.name)) continue;
                    if (m.shader != sh && m.shader.name != "Universal Render Pipeline/Lit") continue;
                    // the belt, its straps and the pouch are open shells: seen from behind their faces vanished and the
                    // pouch showed black holes, so they are drawn from both sides like the coat
                    if (m.name.StartsWith("THD_Strips") && m.HasProperty("_Cull") && m.GetFloat("_Cull") != 0f) { m.SetFloat("_Cull", 0f); m.doubleSidedGI = true; EditorUtility.SetDirty(m); }
                    if (!m.HasProperty("_Cull") || m.GetFloat("_Cull") != 0f) continue;
                    if (m.HasProperty("_Surface") && m.GetFloat("_Surface") != 0f) continue;
                    string n = m.name.ToLowerInvariant();
                    if (n.Contains("hair") || n.Contains("beard") || n.Contains("lash") || n.Contains("brow") || n.Contains("transparency") || n.Contains("scalp")) continue;
                    if (m.shader != sh)
                    {
                        m.shader = sh;
                        EditorUtility.SetDirty(m);
                    }
                    done.Add(m.name);
                }
            AssetDatabase.SaveAssets();
            GKBridge.Log("herocc: two-sided cloth lighting on " + (done.Count > 0 ? string.Join(", ", done.ToArray()) : "nothing"));
        }

        static GameObject CCPrefab()
        {
            string p = Dir + "/Prefabs/" + Name + ".prefab";
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
            if (go != null) return go;
            // CC Unity Tools names the prefab after the character; take whatever it made in Prefabs
            string guid = AssetDatabase.FindAssets("t:Prefab", new[] { Dir + "/Prefabs" }).FirstOrDefault();
            return guid != null ? AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid)) : null;
        }

        /// <summary>The jaw stays out of the humanoid (body clips carry no jaw curve and a mapped jaw opens the mouth);
        /// the face rig (expressions, bone drivers) moves it.</summary>
        static void NoJaw()
        {
            var imp = (ModelImporter)AssetImporter.GetAtPath(Fbx);
            var hd = imp.humanDescription;
            if (hd.human == null || !hd.human.Any(h => h.humanName == "Jaw")) return;
            hd.human = hd.human.Where(h => h.humanName != "Jaw").ToArray();
            imp.humanDescription = hd;
            imp.SaveAndReimport();
            GKBridge.Log("herocc: jaw left out of the humanoid");
        }

        /// <summary>
        /// The sword hangs from a socket on the hand (SwordSocket, at the grip point, oriented like the sword), as in an
        /// authored rig: the holster hands the sword to the socket, and a different weapon only needs its own socket
        /// pose. The hand grip learns the real handle radius from the sword's mesh around the grip point: it assumed
        /// 1.6 cm, and the arming sword's handle is thinner, so the fingers closed around air.
        /// </summary>
        internal static void FitSwordSocket(GameObject root)
        {
            var anim = root.GetComponent<Animator>();
            var hand = anim != null ? anim.GetBoneTransform(HumanBodyBones.RightHand) : null;
            var sword = hand != null ? hand.Find("Sword") : null;
            if (sword == null) { GKBridge.Log("herocc clean: no sword in the right hand, no socket"); return; }
            Transform gp = sword.Find("Grip"), bb = sword.Find("BladeBase"), bt = sword.Find("BladeTip");
            if (gp == null || bb == null || bt == null) return;

            // handle radius: median distance from the handle axis of the mesh vertices within 2.5 cm of the grip point
            Vector3 axis = (bt.position - bb.position).normalized;
            var radii = new List<float>();
            foreach (var mf in sword.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var verts = mf.sharedMesh.vertices;
                foreach (var v in verts)
                {
                    Vector3 w = mf.transform.TransformPoint(v) - gp.position;
                    if (Mathf.Abs(Vector3.Dot(w, axis)) < 0.025f) radii.Add(Vector3.ProjectOnPlane(w, axis).magnitude);
                }
            }
            var grip = root.GetComponent<HandGrip>();
            if (radii.Count > 4 && grip != null)
            {
                radii.Sort();
                float r = radii[radii.Count / 2];
                if (r > 0.006f && r < 0.03f) grip.handleRadius = r;
                GKBridge.Log("herocc clean: handle radius " + (r * 100f).ToString("F2") + " cm from " + radii.Count + " vertices");
            }

            // the socket: at the grip point, oriented like the sword; the sword keeps its world pose under it
            var old = hand.Find("SwordSocket");
            if (old != null && old != sword.parent) UnityEngine.Object.DestroyImmediate(old.gameObject);
            var socket = new GameObject("SwordSocket").transform;
            socket.SetParent(hand, false);
            socket.SetPositionAndRotation(gp.position, sword.rotation);
            sword.SetParent(socket, true);
            var holster = root.GetComponent<WeaponHolster>();
            if (holster != null) holster.hand = socket;
            GKBridge.Log("herocc clean: sword socket on " + hand.name + " at " + (socket.localPosition * 100f).ToString("F1") + " cm");
        }

        /// <summary>
        /// Straps the kite shield to the left forearm, the way enarmes hold it: its back against the outside of the
        /// forearm, the boss outward (the back of the hand's side), the round top toward the thumb and the point
        /// toward the little finger, so that in the sword-and-shield guard (forearm across the body) it stands upright
        /// in front of the knight. The shield's own frame is read from its mesh: the thinnest axis is its normal, the
        /// boss protrudes on the front, the round top is the wider end.
        /// </summary>
        internal static void AttachShield(GameObject root)
        {
            var anim = root.GetComponent<Animator>();
            Transform fore = anim != null ? anim.GetBoneTransform(HumanBodyBones.LeftLowerArm) : null;
            Transform hand = anim != null ? anim.GetBoneTransform(HumanBodyBones.LeftHand) : null;
            Transform idx = anim != null ? anim.GetBoneTransform(HumanBodyBones.LeftIndexProximal) : null;
            Transform lit = anim != null ? anim.GetBoneTransform(HumanBodyBones.LeftLittleProximal) : null;
            if (fore == null || hand == null || idx == null || lit == null) { GKBridge.Log("shield: left arm bones missing"); return; }
            var old = fore.Find("Shield"); if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(GKPaths.ShieldFbx);
            if (model == null) { GKBridge.Log("shield: no model at " + GKPaths.ShieldFbx); return; }
            var shield = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(shield, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
            shield.name = "Shield";
            var mat = GKCharacters.ShieldMaterial;
            foreach (var r in shield.GetComponentsInChildren<Renderer>())
            {
                // the round shield has a material per slot (face, wood, iron); the kite shield one for all
                var slots = r.sharedMaterials;
                bool own = false;
                for (int i = 0; i < slots.Length; i++)
                {
                    var m = GKCharacters.RoundShieldMaterial(slots[i] != null ? slots[i].name : null);
                    if (m != null) { slots[i] = m; own = true; }
                }
                if (own) r.sharedMaterials = slots;
                else if (mat != null) r.sharedMaterial = mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }

            // ---- the shield's frame, in its root's space, from the mesh
            var pts = new List<Vector3>();
            foreach (var mf in shield.GetComponentsInChildren<MeshFilter>())
                if (mf.sharedMesh != null)
                    foreach (var v in mf.sharedMesh.vertices) pts.Add(shield.transform.InverseTransformPoint(mf.transform.TransformPoint(v)));
            if (pts.Count == 0) { UnityEngine.Object.DestroyImmediate(shield); return; }
            Vector3 mn = pts[0], mx = pts[0];
            foreach (var p in pts) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            Vector3 size = mx - mn, mid = (mn + mx) * 0.5f;
            int thin = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
            int tall = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            int wide = 3 - thin - tall;
            Vector3 nAx = Vector3.zero; nAx[thin] = 1f;
            Vector3 upAx = Vector3.zero; upAx[tall] = 1f;
            // the boss: the farthest point off the plate (its median depth, not the bounds' middle: the boss and the
            // rim's back edge stand equally far off that) along the normal is on the front
            var depths = new List<float>(pts.Count);
            foreach (var p in pts) depths.Add(p[thin]);
            depths.Sort();
            float plate = depths[depths.Count / 2];
            float far = 0f; Vector3 boss = mid;
            foreach (var p in pts) { float d = p[thin] - plate; if (Mathf.Abs(d) > Mathf.Abs(far)) { far = d; boss = p; } }
            if (far < 0f) nAx = -nAx;
            // the round top is the wider half
            float wTop = 0f, wBot = 0f;
            foreach (var p in pts)
            {
                float w = Mathf.Abs(p[wide] - mid[wide]);
                if (p[tall] > mid[tall]) wTop = Mathf.Max(wTop, w); else wBot = Mathf.Max(wBot, w);
            }
            if (wBot > wTop) upAx = -upAx;
            // the strap point: the plate's back right behind the boss
            float sNear = 0.12f * size[wide], sBack = float.MaxValue;
            Vector3 strap = boss;
            foreach (var p in pts)
            {
                if (Mathf.Abs(p[wide] - boss[wide]) > sNear || Mathf.Abs(p[tall] - boss[tall]) > sNear) continue;
                float d = Vector3.Dot(p, nAx);
                if (d < sBack) { sBack = d; strap = boss; strap[thin] = p[thin]; }
            }

            // ---- mount it in the shield guard: the arm as the sword-and-shield block clip holds it, the shield upright
            // in front of the knight, its face square to the front and its top leaning a little back over the forearm.
            // Every other pose then carries it the way the forearm does, as straps would.
            var bones = root.GetComponentsInChildren<Transform>(true);
            var savedPos = new Vector3[bones.Length]; var savedRot = new Quaternion[bones.Length];
            for (int i = 0; i < bones.Length; i++) { savedPos[i] = bones[i].localPosition; savedRot[i] = bones[i].localRotation; }
            Vector3 rootPos = root.transform.position; Quaternion rootRot = root.transform.rotation;
            var guard = GKCombatBuilder.Clip("SS_Block_Idle");
            if (guard != null) guard.SampleAnimation(root, guard.length * 0.5f);
            root.transform.SetPositionAndRotation(rootPos, rootRot);

            Vector3 along = (hand.position - fore.position);
            float foreLen = along.magnitude; along /= Mathf.Max(1e-4f, foreLen);
            Vector3 fwd = Vector3.ProjectOnPlane(root.transform.forward, Vector3.up).normalized;
            Vector3 face = Quaternion.AngleAxis(-10f, Vector3.up) * fwd;                // turned a touch toward the shield side
            // the plate lies parallel to the line from the forearm strap to the fist, so both sit the same depth behind it
            Vector3 fist = FistCentre(anim, out _);
            Vector3 elbowStrap = fore.position + along * (foreLen * StrapAt);
            face = Vector3.ProjectOnPlane(face, (fist - elbowStrap).normalized).normalized;
            Vector3 top = Quaternion.AngleAxis(-12f, Vector3.Cross(Vector3.up, face)) * Vector3.up;   // leaning back
            top = Vector3.ProjectOnPlane(top, face).normalized;

            // a kite shield is scaled to 1.05 m, chin to knee; a round one (as wide as tall) to 72 cm over its rim,
            // the size the sword-and-shield captures were taken with
            bool round = Mathf.Abs(size[tall] - size[wide]) < 0.05f * size[tall];
            float Height = round ? 0.72f : 1.05f;
            float k = Height / Mathf.Max(1e-4f, size[tall]);
            Quaternion rot = Quaternion.LookRotation(face, top) * Quaternion.Inverse(Quaternion.LookRotation(nAx, upAx));
            shield.transform.localScale = Vector3.one * k;
            shield.transform.rotation = rot;
            // the fist right behind the boss, the plate a sleeve's thickness plus 5 mm off the forearm's axis
            Vector3 strapWorld = fist + face * (SleeveR + 0.005f);
            shield.transform.position = strapWorld - rot * (strap * k);
            shield.transform.SetParent(fore, true);
            AddStraps(shield.transform, root.transform, fore, hand, anim, shield.transform.TransformPoint(strap),
                      shield.transform.TransformDirection(nAx).normalized, along, foreLen);
            // the plate as an obstacle for the sword: a blade reaching through the shield is turned back behind it
            var plateBox = shield.GetComponent<BoxCollider>();
            if (plateBox == null) plateBox = shield.AddComponent<BoxCollider>();
            // the plate's own depth, from the rim's back to 6 mm in front of the plate's median face: not the boss and
            // its rivets standing off it (a quarter of the vertices), which made the box 7 cm thick
            float sFront = far >= 0f ? 1f : -1f;
            float dBack = sFront > 0f ? depths[depths.Count / 20] : depths[depths.Count * 19 / 20];
            float dFront = plate + sFront * 0.006f / k;
            float dLo = Mathf.Min(dBack, dFront), dHi = Mathf.Max(dBack, dFront);
            // for the back mount (SetupStyles): the plate's frame and the middle of its back face, in shield space
            shieldFrontLocal = nAx; shieldTopLocal = upAx;
            shieldBackCentreLocal = mid; shieldBackCentreLocal[thin] = dBack;
            shieldRoundRadius = round ? Height * 0.5f : 0f;
            Vector3 boxC = mid, boxS = size;
            boxC[thin] = (dLo + dHi) * 0.5f; boxS[thin] = Mathf.Max(0.004f / k, dHi - dLo);
            plateBox.center = boxC; plateBox.size = boxS;
            GKBridge.Log("shield: plate collider " + (boxS * k * 100f).ToString("F1") + " cm");
            plateBox.isTrigger = true;
            shield.layer = 2;   // Ignore Raycast: only the blade solver reads it
            var bladeSolver = root.GetComponent<WeaponSelfCollision>();
            if (bladeSolver != null)
            {
                var avoid = new List<Collider>(bladeSolver.avoid ?? new Collider[0]);
                avoid.RemoveAll(c => c == null || c.transform.IsChildOf(shield.transform));
                avoid.Add(plateBox);
                bladeSolver.avoid = avoid.ToArray();
            }

            // what a shield bash strikes with: the face, from below the top to above the point
            var combatant = root.GetComponent<Combatant>();
            if (combatant != null)
            {
                var bashFace = new Transform[2];
                for (int e = 0; e < 2; e++)
                {
                    var p = new GameObject(e == 0 ? "BashTop" : "BashPoint").transform;
                    p.SetParent(shield.transform, false);
                    Vector3 l = mid + upAx * (size[tall] * (e == 0 ? 0.35f : -0.3f));
                    l[thin] = sFront > 0f ? dHi : dLo;
                    p.localPosition = l;
                    bashFace[e] = p;
                }
                combatant.shieldEdge = bashFace;
            }

            // the wrist and the closed fist as the straps were fitted, held there in every clip
            var grip = root.GetComponent<ShieldGrip>();
            if (grip == null) grip = root.AddComponent<ShieldGrip>();
            var held = new List<Transform> { hand };
            for (var b = HumanBodyBones.LeftThumbProximal; b <= HumanBodyBones.LeftLittleDistal; b++)
            {
                var t = anim.GetBoneTransform(b);
                if (t != null) held.Add(t);
            }
            grip.bones = held.ToArray();
            grip.guard = held.ConvertAll(t => t.localRotation).ToArray();
            grip.shield = shield.transform; grip.forearm = fore;
            grip.handInShield = shield.transform.InverseTransformPoint(hand.position);
            grip.loopInShield = shield.transform.InverseTransformPoint(fore.position + along * (foreLen * StrapAt));
            float tiltOut = Vector3.Angle(along, face) - 90f;   // 0 = forearm flat along the plate
            Vector3 c = shield.transform.TransformPoint(mid);
            GKBridge.Log("shield: on " + fore.name + " in " + (guard != null ? guard.name : "bind pose") + ", " + Height +
                         " m tall, forearm " + tiltOut.ToString("F0") + " deg off the plate, centre " +
                         (root.transform.InverseTransformPoint(c) * 100f).ToString("F0") + " cm from the feet");

            for (int i = 0; i < bones.Length; i++) { bones[i].localPosition = savedPos[i]; bones[i].localRotation = savedRot[i]; }
            root.transform.SetPositionAndRotation(rootPos, rootRot);
        }

        /// <summary>
        /// The enarmes the model lacks, fitted to the hand and forearm in the guard pose: a grip strap riveted to the
        /// plate at both ends that runs through the closed fist (centred on the circle the middle finger curls around),
        /// and a forearm loop round the sleeve a third of the way down from the elbow. One leather mesh under the shield.
        /// </summary>
        static void AddStraps(Transform shield, Transform root, Transform fore, Transform hand, Animator anim,
                              Vector3 platePt, Vector3 front, Vector3 along, float foreLen)
        {
            var old = shield.Find("Straps"); if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);
            Transform idx = anim.GetBoneTransform(HumanBodyBones.LeftIndexProximal), lit = anim.GetBoneTransform(HumanBodyBones.LeftLittleProximal);
            if (idx == null || lit == null) { GKBridge.Log("shield straps: finger bones missing"); return; }
            // a point on the back of the plate behind p, lifted 3 mm so the riveted ends never show through the front
            System.Func<Vector3, Vector3> onPlate = p => p - front * (Vector3.Dot(p - platePt, front) + 0.003f);

            // ---- grip strap through the fist
            Vector3 fist = FistCentre(anim, out float curl);
            Vector3 across = idx.position - lit.position;
            float handW = across.magnitude; across /= Mathf.Max(1e-4f, handW);
            Vector3 gripW = Vector3.Cross(front, across).normalized;          // the strap's width lies along the fingers' height
            Vector3 endA = fist + across * (handW * 0.5f + 0.022f), endB = fist - across * (handW * 0.5f + 0.022f);
            var grip = new List<Vector3> { onPlate(endA), Vector3.Lerp(onPlate(endA), endA, 0.85f), endA, endB, Vector3.Lerp(onPlate(endB), endB, 0.85f), onPlate(endB) };

            // ---- forearm loop round the sleeve, its ends riveted to the plate on either side of the arm
            Vector3 c = fore.position + along * (foreLen * StrapAt);
            Vector3 side = Vector3.Cross(along, front).normalized;
            var loop = new List<Vector3> { onPlate(c + side * (SleeveR + 0.004f)) };
            for (int i = 0; i <= 12; i++)
            {
                float t = Mathf.PI * i / 12f;
                loop.Add(c + (side * Mathf.Cos(t) - front * Mathf.Sin(t)) * (SleeveR + 0.004f));
            }
            loop.Add(onPlate(c - side * (SleeveR + 0.004f)));

            var verts = new List<Vector3>(); var tris = new List<int>(); var uvs = new List<Vector2>();
            // the strap's two legs rise from the plate to a round, leather-bound handle the fist closes on
            Band(verts, tris, uvs, grip.GetRange(0, 3), gripW, 0.028f, 0.006f, shield);
            Band(verts, tris, uvs, grip.GetRange(3, 3), gripW, 0.028f, 0.006f, shield);
            Tube(verts, tris, uvs, new List<Vector3> { endA + across * 0.008f, endA, Vector3.Lerp(endA, endB, 0.5f), endB, endB - across * 0.008f }, 0.0135f, 12, shield);
            Band(verts, tris, uvs, loop, along, 0.034f, 0.006f, shield);
            // iron rivets where the straps meet the plate
            foreach (var r in new[] { grip[0], grip[5], loop[0], loop[loop.Count - 1] })
                Tube(verts, tris, uvs, new List<Vector3> { r + front * 0.001f, r - front * 0.005f }, 0.0075f, 10, shield);
            var mesh = new Mesh { name = "ShieldStraps" };
            mesh.SetVertices(verts); mesh.SetTriangles(tris, 0); mesh.SetUVs(0, uvs);
            mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
            string path = GKPaths.Generated + "/ShieldStraps.asset";
            GKPaths.Ensure(GKPaths.Generated);
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);

            var go = new GameObject("Straps");
            go.transform.SetParent(shield, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = StrapMaterial();
            float gap = -Vector3.Dot(fist - platePt, front);
            float armGap = -Vector3.Dot(c - platePt, front) - SleeveR;
            GKBridge.Log("shield straps: fist " + (gap * 100f).ToString("F1") + " cm off the plate (curl radius " + (curl * 100f).ToString("F1") +
                         " cm), sleeve " + (armGap * 100f).ToString("F1") + " cm off the plate");
        }

        /// <summary>
        /// The clothed torso's outer surface for the blade solver (see BodyShell): the skinned meshes baked at the bind
        /// pose, arms, hands, neck and head left out by their skinning, measured as the furthest reach of the coat per
        /// 4 cm band and 15 degree sector around two vertical axes - one riding on the hips (thigh tops to the waist),
        /// one on the upper chest (waist to the shoulders), so the shell bends with the spine.
        /// </summary>
        internal static void BuildBodyShells(GameObject root)
        {
            var anim = root.GetComponent<Animator>();
            var solver = root.GetComponent<WeaponSelfCollision>();
            if (anim == null || solver == null) return;
            Transform hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            Transform upper = anim.GetBoneTransform(HumanBodyBones.UpperChest) ?? anim.GetBoneTransform(HumanBodyBones.Chest);
            if (hips == null || upper == null) return;
            foreach (var old in root.GetComponentsInChildren<BodyShell>(true)) UnityEngine.Object.DestroyImmediate(old);
            // bones whose skin is not torso: the arms from the upper arm out, the neck and head, the legs from the knee
            var skip = new HashSet<Transform>();
            foreach (var b in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, HumanBodyBones.Neck, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg })
            {
                var t = anim.GetBoneTransform(b);
                if (t != null) foreach (var c in t.GetComponentsInChildren<Transform>(true)) skip.Add(c);
            }
            var pts = new List<Vector3>();
            var baked = new Mesh();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr.sharedMesh == null || !smr.gameObject.activeInHierarchy) continue;
                var bw = smr.sharedMesh.boneWeights; var bones = smr.bones;
                smr.BakeMesh(baked, true);
                var vs = baked.vertices;
                for (int i = 0; i < vs.Length; i++)
                {
                    if (bw != null && i < bw.Length && bones != null && bw[i].boneIndex0 < bones.Length && bones[bw[i].boneIndex0] != null && skip.Contains(bones[bw[i].boneIndex0])) continue;
                    pts.Add(smr.transform.TransformPoint(vs[i]));
                }
            }
            UnityEngine.Object.DestroyImmediate(baked);
            Vector3 up = Vector3.up, fwd = Vector3.ProjectOnPlane(root.transform.forward, Vector3.up).normalized;
            var shells = new List<BodyShell>();
            foreach (var spec in new[] { new { t = hips, lo = -0.22f, hi = 0.16f }, new { t = upper, lo = -0.18f, hi = 0.22f } })
            {
                var sh = root.AddComponent<BodyShell>();
                sh.frame = spec.t;
                Vector3 o = spec.t.position;
                sh.originLocal = spec.t.InverseTransformPoint(o);
                sh.upLocal = spec.t.InverseTransformDirection(up);
                sh.forwardLocal = spec.t.InverseTransformDirection(fwd);
                sh.yStep = 0.04f; sh.sectors = 24; sh.yMin = spec.lo;
                sh.bins = Mathf.CeilToInt((spec.hi - spec.lo) / sh.yStep);
                sh.radius = new float[sh.bins * sh.sectors];
                Vector3 right = Vector3.Cross(up, fwd);
                int used = 0;
                foreach (var p in pts)
                {
                    Vector3 d = p - o;
                    float h = Vector3.Dot(d, up);
                    int bin = Mathf.FloorToInt((h - sh.yMin) / sh.yStep);
                    if (bin < 0 || bin >= sh.bins) continue;
                    Vector3 flat = d - up * h;
                    float r = flat.magnitude;
                    if (r > 0.45f) continue;      // a stray far vertex (a strap end) is not the torso
                    float ang = Mathf.Atan2(Vector3.Dot(flat, right), Vector3.Dot(flat, fwd));
                    int sec = Mathf.Clamp(Mathf.FloorToInt((ang + Mathf.PI) / (2f * Mathf.PI) * sh.sectors), 0, sh.sectors - 1);
                    int k = bin * sh.sectors + sec;
                    if (r > sh.radius[k]) sh.radius[k] = r;
                    used++;
                }
                // a hole in the mesh between two covered sectors (an open coat's edge) would let a blade through: closed
                // with the smaller neighbour; a gap with nothing on one side (between the thighs) stays open
                var filled = (float[])sh.radius.Clone();
                for (int b2 = 0; b2 < sh.bins; b2++)
                    for (int s2 = 0; s2 < sh.sectors; s2++)
                    {
                        int k = b2 * sh.sectors + s2;
                        if (sh.radius[k] > 0f) continue;
                        float l = sh.radius[b2 * sh.sectors + (s2 + sh.sectors - 1) % sh.sectors], r = sh.radius[b2 * sh.sectors + (s2 + 1) % sh.sectors];
                        if (l > 0f && r > 0f) filled[k] = Mathf.Min(l, r);
                    }
                sh.radius = filled;
                shells.Add(sh);
                float front = sh.radius[(sh.bins / 2) * sh.sectors + sh.sectors / 2];
                var ring = new System.Text.StringBuilder();
                for (int b2 = 0; b2 < sh.bins; b2 += 2)
                {
                    ring.Append(" | h" + ((sh.yMin + b2 * sh.yStep) * 100f).ToString("F0") + ":");
                    for (int s2 = 0; s2 < sh.sectors; s2 += 3) ring.Append(' ').Append((sh.radius[b2 * sh.sectors + s2] * 100f).ToString("F0"));
                }
                GKBridge.Log("body shell on " + spec.t.name + ": " + used + " skin points, " + sh.bins + " bands, front reach at mid height " + (front * 100f).ToString("F0") + " cm" + ring);
            }
            solver.shells = shells.ToArray();
        }

        static Vector3 shieldFrontLocal = Vector3.forward, shieldTopLocal = Vector3.up, shieldBackCentreLocal;
        static float shieldRoundRadius;   // a round shield's radius (m), 0 for the kite

        /// <summary>
        /// The gear for the three fighting styles (see FightingStyles): the shield's place on the back (plate against
        /// the coat between the shoulder blades, measured from the skinned meshes), and a second sword with its own
        /// scabbard on the right hip, both mirrored from the right-hand sword and the left-hip scabbard across the
        /// body's middle (bind pose, which is symmetric), with the left hand's grip, cut and blade-collision solver.
        /// </summary>
        internal static void SetupStyles(GameObject root)
        {
            var anim = root.GetComponent<Animator>();
            var holster = root.GetComponent<WeaponHolster>();
            var combat = root.GetComponent<Combatant>();
            var shield = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Shield");
            if (anim == null || holster == null || combat == null || shield == null) { GKBridge.Log("styles: knight parts missing"); return; }
            var styles = root.GetComponent<FightingStyles>();
            if (styles == null) styles = root.AddComponent<FightingStyles>();
            styles.swordShield = GKCombatBuilder.CleanPlayerMoves;
            styles.sword = AssetDatabase.LoadAssetAtPath<MoveSet>(GKPaths.Data + "/Moves_Style_Sword.asset");
            styles.dualSwords = AssetDatabase.LoadAssetAtPath<MoveSet>(GKPaths.Data + "/Moves_Style_Dual.asset");
            Transform rootT = root.transform;
            System.Func<Vector3, Vector3> mirP = p => { var l = rootT.InverseTransformPoint(p); l.x = -l.x; return rootT.TransformPoint(l); };
            System.Func<Quaternion, Quaternion> mirR = q =>
            {
                var l = Quaternion.Inverse(rootT.rotation) * q;
                return rootT.rotation * new Quaternion(l.x, -l.y, -l.z, l.w);
            };

            // ---- shield: arm mount as fitted, back mount behind the shoulder blades
            styles.shield = shield;
            styles.shieldArmParent = shield.parent;
            styles.shieldArmPos = shield.localPosition; styles.shieldArmRot = shield.localRotation;
            Transform spine = anim.GetBoneTransform(HumanBodyBones.UpperChest) ?? anim.GetBoneTransform(HumanBodyBones.Chest);
            Transform chest = anim.GetBoneTransform(HumanBodyBones.Chest) ?? spine;
            float chestY = rootT.InverseTransformPoint(chest.position).y;
            float backZ = 0f; int found = 0;
            var baked = new Mesh();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr.sharedMesh == null || !smr.gameObject.activeInHierarchy) continue;
                smr.BakeMesh(baked, true);
                foreach (var v in baked.vertices)
                {
                    var l = rootT.InverseTransformPoint(smr.transform.TransformPoint(v));
                    if (Mathf.Abs(l.y - chestY) > 0.14f || Mathf.Abs(l.x) > 0.13f) continue;
                    if (found == 0 || l.z < backZ) backZ = l.z;
                    found++;
                }
            }
            UnityEngine.Object.DestroyImmediate(baked);
            if (found == 0) backZ = rootT.InverseTransformPoint(chest.position).z - 0.16f;
            // face out behind, top up and leaning a little forward over the shoulders, its middle a hand below the chest
            Vector3 face = -rootT.forward;
            Vector3 top = Quaternion.AngleAxis(8f, rootT.right) * rootT.up;
            top = Vector3.ProjectOnPlane(top, face).normalized;
            Quaternion backRot = Quaternion.LookRotation(face, top) * Quaternion.Inverse(Quaternion.LookRotation(shieldFrontLocal, shieldTopLocal));
            // a round shield hangs from its strap with the rim just under the shoulders, as it is carried, clear of
            // the seat and the scabbard at the hip; the kite's middle a hand below the chest
            float centreY = chestY - 0.12f;
            var lUp = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm); var rUp = anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
            if (shieldRoundRadius > 0f && lUp != null && rUp != null)
                centreY = rootT.InverseTransformPoint((lUp.position + rUp.position) * 0.5f).y - 0.01f - shieldRoundRadius;
            Vector3 backPoint = rootT.TransformPoint(new Vector3(0f, centreY, backZ - 0.012f));
            Vector3 backPos = backPoint - backRot * Vector3.Scale(shieldBackCentreLocal, shield.lossyScale);
            var parkPos = shield.position; var parkRot = shield.rotation; var parkParent = shield.parent;
            shield.SetParent(spine, true);
            shield.SetPositionAndRotation(backPos, backRot);
            styles.shieldBackParent = spine;
            styles.shieldBackPos = shield.localPosition; styles.shieldBackRot = shield.localRotation;
            shield.SetParent(parkParent, true);
            shield.SetPositionAndRotation(parkPos, parkRot);
            GKBridge.Log("styles: shield back mount on " + spine.name + ", coat back " + (-backZ * 100f).ToString("F0") + " cm behind the root (" + found + " skin points), centre "
                         + (centreY * 100f).ToString("F0") + " cm up (chest " + (chestY * 100f).ToString("F0") + ")");

            // ---- the left hand's swing timing for the over-shoulder reaches (hand furthest back, above the shoulder)
            styles.shieldReachLength = ReachLength("AX_standing_disarm_over_shoulder", GKCombatBuilder.ShieldMoveSpeed);
            styles.shieldSwapAt = ShoulderReach(root, "AX_standing_disarm_over_shoulder");

            // ---- second sword in the left hand, mirrored from the right
            var sword = holster.weapon; var socket = holster.hand;
            Transform lHand = anim.GetBoneTransform(HumanBodyBones.LeftHand);
            var oldL = lHand.Find("SwordSocketL"); if (oldL != null) UnityEngine.Object.DestroyImmediate(oldL.gameObject);
            var socketL = UnityEngine.Object.Instantiate(socket.gameObject).transform;
            socketL.name = "SwordSocketL";
            // seat it as the exact mirror of the right sword in a mirrored pose, not at the bind pose: the CC bind pose
            // is not quite symmetric at the hands, and the second blade came out tilted, passing a metre from a target
            // its mirrored cuts should have gone through
            MirrorSeat(root, socket, socketL, lHand);
            socketL.SetParent(lHand, true);
            var swordL = socketL.Find(sword.name);
            if (swordL == null) { GKBridge.Log("styles: the socket copy has no sword"); return; }
            swordL.name = "SwordL";
            var hitL = swordL.GetComponent<WeaponHitbox>();
            combat.offWeapon = hitL;

            // ---- its scabbard on the right hip
            var pivot = holster.sheath;
            var oldS = pivot.parent.Find("ScabbardPivotR"); if (oldS != null) UnityEngine.Object.DestroyImmediate(oldS.gameObject);
            var pivotR = UnityEngine.Object.Instantiate(pivot.gameObject).transform;
            pivotR.name = "ScabbardPivotR";
            pivotR.SetPositionAndRotation(mirP(pivot.position), mirR(pivot.rotation));
            pivotR.SetParent(pivot.parent, true);
            pivotR.localScale = pivot.localScale;
            Transform sr = swordL.Find("SheathRef");
            if (sr != null)
            {
                float s = swordL.lossyScale.x;
                Quaternion rotW = pivotR.rotation * Quaternion.Inverse(sr.localRotation);
                Vector3 guardW = pivotR.position - pivotR.forward * holster.throatOffset;
                Vector3 posW = guardW - rotW * (sr.localPosition * s);
                styles.offSheathRot = Quaternion.Inverse(pivotR.rotation) * rotW;
                styles.offSheathPos = pivotR.InverseTransformPoint(posW);
            }
            styles.offSword = swordL; styles.offSocket = socketL; styles.offSheath = pivotR;
            // it swings with the right thigh and keeps out of the legs, as the left one does
            foreach (var sm in root.GetComponents<ScabbardMotion>()) if (sm.scabbard == pivotR || sm.scabbard == null) UnityEngine.Object.DestroyImmediate(sm);
            var mainSwing = root.GetComponents<ScabbardMotion>().FirstOrDefault(sm => sm.scabbard == pivot);
            if (mainSwing != null)
            {
                var swingR = root.AddComponent<ScabbardMotion>();
                EditorUtility.CopySerialized(mainSwing, swingR);
                swingR.scabbard = pivotR;
                swingR.thigh = anim.GetBoneTransform(HumanBodyBones.RightUpperLeg);
                swingR.restDirInThigh = Vector3.zero;
                swingR.BakeThighRest();
                var ragR = root.GetComponent<Ragdoll>();
                if (ragR != null)
                {
                    var want = new[] { HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.LeftUpperLeg }.Select(b => anim.GetBoneTransform(b)).ToList();
                    swingR.legs = want.Select(b => ragR.colliders.OfType<CapsuleCollider>().FirstOrDefault(c => c.transform == b)).Where(c => c != null).ToArray();
                }
            }
            var moves = GKCombatBuilder.CleanPlayerMoves;
            styles.offDrawLength = moves != null && moves.drawSword.clip != null ? moves.drawSword.clip.length / 1.1f : 1.2f;
            styles.offSheatheLength = moves != null && moves.sheathSword.clip != null ? moves.sheathSword.clip.length / 1.1f : 1.4f;

            // ---- the left hand's grip and blade solver
            foreach (var g in root.GetComponents<HandGrip>()) if (g.leftHand) UnityEngine.Object.DestroyImmediate(g);
            var right = root.GetComponent<HandGrip>();
            var gripL = root.AddComponent<HandGrip>();
            if (right != null) EditorUtility.CopySerialized(right, gripL);
            gripL.leftHand = true;
            gripL.sword = swordL;
            gripL.thumbProximal = anim.GetBoneTransform(HumanBodyBones.LeftThumbProximal);
            gripL.thumbIntermediate = anim.GetBoneTransform(HumanBodyBones.LeftThumbIntermediate);
            gripL.thumbDistal = anim.GetBoneTransform(HumanBodyBones.LeftThumbDistal);
            System.Func<HumanBodyBones, Transform[]> chainL = b => new[] { anim.GetBoneTransform(b), anim.GetBoneTransform(b + 1), anim.GetBoneTransform(b + 2) };
            gripL.index = chainL(HumanBodyBones.LeftIndexProximal);
            gripL.middle = chainL(HumanBodyBones.LeftMiddleProximal);
            gripL.ring = chainL(HumanBodyBones.LeftRingProximal);
            gripL.little = chainL(HumanBodyBones.LeftLittleProximal);

            foreach (var w in root.GetComponents<WeaponSelfCollision>()) if (w.leftHand) UnityEngine.Object.DestroyImmediate(w);
            var mainSolver = root.GetComponent<WeaponSelfCollision>();
            var solverL = root.AddComponent<WeaponSelfCollision>();
            if (mainSolver != null) EditorUtility.CopySerialized(mainSolver, solverL);
            solverL.leftHand = true;
            solverL.weapon = swordL;
            solverL.grip = swordL.Find("Grip"); solverL.bladeBase = swordL.Find("BladeBase"); solverL.bladeTip = swordL.Find("BladeTip");
            var rag = root.GetComponent<Ragdoll>();
            var leftArm = new HashSet<Transform> { anim.GetBoneTransform(HumanBodyBones.LeftUpperArm), anim.GetBoneTransform(HumanBodyBones.LeftLowerArm) };
            if (rag != null)
            {
                var avoid = rag.colliders.Where(c => c != null && !leftArm.Contains(c.transform)).ToList();
                var plate = shield.GetComponent<Collider>();
                if (plate != null) avoid.Add(plate);
                solverL.avoid = avoid.ToArray();
            }
            styles.offSolver = solverL;
            // the two-sword guard is a captured clip on the Block layer: the runtime IK guard stays off
            var oldGuard = root.GetComponent<DualGuardIK>(); if (oldGuard != null) UnityEngine.Object.DestroyImmediate(oldGuard);

            // the second blade's trail
            var trailMain = root.GetComponentInChildren<WeaponTrail>();
            if (trailMain != null && hitL != null)
            {
                var oldT = rootT.Find("BladeTrailL"); if (oldT != null) UnityEngine.Object.DestroyImmediate(oldT.gameObject);
                var tl = UnityEngine.Object.Instantiate(trailMain.gameObject, rootT).GetComponent<WeaponTrail>();
                tl.name = "BladeTrailL";
                tl.bladeBase = hitL.bladeBase; tl.bladeTip = hitL.bladeTip;
                tl.hand = WeaponHand.Off;
            }
            GKBridge.Log("styles: second sword in " + lHand.name + ", its scabbard on the right hip; sets " +
                         (styles.sword != null ? styles.sword.name : "NO SWORD SET") + ", " + (styles.dualSwords != null ? styles.dualSwords.name : "NO DUAL SET") +
                         "; over-shoulder hand-over at " + styles.shieldSwapAt.ToString("F2"));
        }

        /// <summary>
        /// Places <paramref name="copy"/> where <paramref name="original"/> (a right-hand socket) would be in the mirror:
        /// the knight is posed by a sword clip and then by the same clip mirrored (Mecanim's humanoid mirror, as the
        /// animator plays the left hand's cuts); the copy takes the original's first pose reflected across the body's
        /// middle, in the second pose, and stays on its (left-hand) parent. The mirrored cuts then move the second blade
        /// exactly as the clips move the first.
        /// </summary>
        static void MirrorSeat(GameObject root, Transform original, Transform copy, Transform parent)
        {
            var anim = root.GetComponent<Animator>();
            var clip = GKCombatBuilder.Clip("SS_Idle");
            Transform rootT = root.transform;
            if (anim == null || clip == null)
            {
                copy.SetPositionAndRotation(rootT.TransformPoint(Vector3.Scale(rootT.InverseTransformPoint(original.position), new Vector3(-1f, 1f, 1f))), original.rotation);
                return;
            }
            var bones = root.GetComponentsInChildren<Transform>(true);
            var pos = new Vector3[bones.Length]; var rot = new Quaternion[bones.Length];
            for (int i = 0; i < bones.Length; i++) { pos[i] = bones[i].localPosition; rot[i] = bones[i].localRotation; }
            var oldCtrl = anim.runtimeAnimatorController;
            bool oldRoot = anim.applyRootMotion;
            string path = GKPaths.Generated + "/~mirrorseat.controller";
            var ctrl = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(path);
            try
            {
                var sm = ctrl.layers[0].stateMachine;
                var a = sm.AddState("A"); a.motion = clip;
                var b = sm.AddState("B"); b.motion = clip; b.mirror = true;
                anim.runtimeAnimatorController = ctrl;
                anim.applyRootMotion = false;
                Vector3 rp0 = rootT.position; Quaternion rr0 = rootT.rotation;
                anim.Rebind(); anim.Play("A", 0, 0.25f); anim.Update(0f);
                rootT.SetPositionAndRotation(rp0, rr0);
                Vector3 lp = rootT.InverseTransformPoint(original.position);
                Quaternion lr = Quaternion.Inverse(rootT.rotation) * original.rotation;
                // the right sword's blade, width and grip point in this pose, reflected across the body's middle
                var swordR = original.GetComponentInChildren<WeaponHitbox>(true) != null ? original.GetComponentInChildren<WeaponHitbox>(true).transform : null;
                Vector3 bladeM = Vector3.zero, widthM = Vector3.zero, gripM = Vector3.zero;
                Vector3 bLocal = Vector3.forward, wLocal = Vector3.up, gLocal = Vector3.zero;
                if (swordR != null)
                {
                    var tip = swordR.Find("BladeTip"); var bas = swordR.Find("BladeBase"); var sr = swordR.Find("SheathRef"); var gp = swordR.Find("Grip");
                    if (tip != null && bas != null) bLocal = swordR.InverseTransformDirection(tip.position - bas.position).normalized;
                    if (sr != null) wLocal = Vector3.ProjectOnPlane(sr.localRotation * Vector3.up, bLocal).normalized;
                    if (gp != null) gLocal = gp.localPosition;
                    System.Func<Vector3, Vector3> mirD = d => { var l = rootT.InverseTransformDirection(d); l.x = -l.x; return rootT.TransformDirection(l); };
                    bladeM = mirD(swordR.TransformDirection(bLocal));
                    widthM = mirD(swordR.TransformDirection(wLocal));
                    var g = rootT.InverseTransformPoint(swordR.TransformPoint(gLocal)); g.x = -g.x; gripM = rootT.TransformPoint(g);
                }
                anim.Play("B", 0, 0.25f); anim.Update(0f);
                rootT.SetPositionAndRotation(rp0, rr0);
                copy.SetParent(null, true);
                copy.SetPositionAndRotation(rootT.TransformPoint(new Vector3(-lp.x, lp.y, lp.z)), rootT.rotation * new Quaternion(lr.x, -lr.y, -lr.z, lr.w));
                copy.SetParent(parent, true);
                // a rotation cannot reflect: the reflected socket rotation turned the blade end for end (a dagger
                // grip). Set the second sword itself so its blade, its width and its grip point are the reflection of
                // the first's
                var swordL = copy.GetComponentInChildren<WeaponHitbox>(true) != null ? copy.GetComponentInChildren<WeaponHitbox>(true).transform : null;
                if (swordR != null && swordL != null)
                {
                    Quaternion rotL = Quaternion.LookRotation(bladeM, widthM) * Quaternion.Inverse(Quaternion.LookRotation(bLocal, wLocal));
                    swordL.rotation = rotL;
                    swordL.position = gripM - (swordL.TransformPoint(gLocal) - swordL.position);
                }
            }
            finally
            {
                anim.runtimeAnimatorController = oldCtrl;
                anim.applyRootMotion = oldRoot;
                for (int i = 0; i < bones.Length; i++) if (bones[i] != null && bones[i] != copy && !bones[i].IsChildOf(copy)) { bones[i].localPosition = pos[i]; bones[i].localRotation = rot[i]; }
                AssetDatabase.DeleteAsset(path);
            }
        }

        static float ReachLength(string clip, float speed)
        {
            var c = GKCombatBuilder.Clip(clip);
            return c != null ? c.length / speed : 1.4f;
        }

        /// <summary>When, in an over-the-shoulder reach (right hand, unmirrored), the hand is furthest back above the
        /// shoulder: where the shield on the back is taken or let go.</summary>
        static float ShoulderReach(GameObject root, string clipName)
        {
            var clip = GKCombatBuilder.Clip(clipName);
            var anim = root.GetComponent<Animator>();
            if (clip == null || anim == null) return 0.42f;
            Transform hand = anim.GetBoneTransform(HumanBodyBones.RightHand), shoulder = anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
            var bones = root.GetComponentsInChildren<Transform>(true);
            var pos = new Vector3[bones.Length]; var rot = new Quaternion[bones.Length];
            for (int i = 0; i < bones.Length; i++) { pos[i] = bones[i].localPosition; rot[i] = bones[i].localRotation; }
            Vector3 rp = root.transform.position; Quaternion rr = root.transform.rotation;
            float best = float.MaxValue, at = 0.42f;
            for (int i = 0; i <= 60; i++)
            {
                float t = i / 60f;
                clip.SampleAnimation(root, t * clip.length);
                root.transform.SetPositionAndRotation(rp, rr);
                Vector3 h = root.transform.InverseTransformPoint(hand.position), s = root.transform.InverseTransformPoint(shoulder.position);
                if (h.y < s.y - 0.05f) continue;
                if (h.z < best) { best = h.z; at = t; }
            }
            for (int i = 0; i < bones.Length; i++) { bones[i].localPosition = pos[i]; bones[i].localRotation = rot[i]; }
            root.transform.SetPositionAndRotation(rp, rr);
            return at;
        }

        const float StrapAt = 0.35f;     // the forearm loop, along the forearm from the elbow
        const float SleeveR = 0.056f;    // the forearm with its coat sleeve, from the bone axis

        /// <summary>The hollow of the left fist: the centre of the circle the middle finger's three joints curl around.</summary>
        static Vector3 FistCentre(Animator anim, out float radius)
        {
            Transform m1 = anim.GetBoneTransform(HumanBodyBones.LeftMiddleProximal), m2 = anim.GetBoneTransform(HumanBodyBones.LeftMiddleIntermediate),
                      m3 = anim.GetBoneTransform(HumanBodyBones.LeftMiddleDistal);
            if (m1 == null || m2 == null || m3 == null) { radius = 0f; return anim.GetBoneTransform(HumanBodyBones.LeftHand).position; }
            return Circumcentre(m1.position, m2.position, m3.position, out radius);
        }

        /// <summary>Centre of the circle through three points (the fist's hollow); falls back to their mean.</summary>
        static Vector3 Circumcentre(Vector3 a, Vector3 b, Vector3 c, out float radius)
        {
            Vector3 ab = b - a, ac = c - a, n = Vector3.Cross(ab, ac);
            float d = 2f * n.sqrMagnitude;
            if (d < 1e-10f) { radius = 0f; return (a + b + c) / 3f; }
            Vector3 o = a + (Vector3.Cross(n, ab) * ac.sqrMagnitude + Vector3.Cross(ac, n) * ab.sqrMagnitude) / d;
            radius = (o - a).magnitude;
            return o;
        }

        /// <summary>Sweeps a flat strap (width along <paramref name="widthAxis"/>) through world points into the mesh, in <paramref name="space"/>.</summary>
        static void Band(List<Vector3> verts, List<int> tris, List<Vector2> uvs, List<Vector3> path, Vector3 widthAxis, float width, float thick, Transform space)
        {
            int start = verts.Count; float run = 0f;
            for (int i = 0; i < path.Count; i++)
            {
                Vector3 t = (path[Mathf.Min(i + 1, path.Count - 1)] - path[Mathf.Max(i - 1, 0)]).normalized;
                Vector3 w = Vector3.ProjectOnPlane(widthAxis, t).normalized * (width * 0.5f);
                Vector3 h = Vector3.Cross(t, w).normalized * (thick * 0.5f);
                if (i > 0) run += (path[i] - path[i - 1]).magnitude;
                Vector3[] ring = { path[i] + w + h, path[i] - w + h, path[i] - w - h, path[i] + w - h };
                for (int k = 0; k < 4; k++) { verts.Add(space.InverseTransformPoint(ring[k])); uvs.Add(new Vector2(k / 3f, run * 10f)); }
            }
            for (int i = 0; i + 1 < path.Count; i++)
                for (int k = 0; k < 4; k++)
                {
                    int a = start + i * 4 + k, b = start + i * 4 + (k + 1) % 4, a2 = a + 4, b2 = b + 4;
                    tris.Add(a); tris.Add(a2); tris.Add(b);
                    tris.Add(b); tris.Add(a2); tris.Add(b2);
                }
        }

        /// <summary>Sweeps a round section (radius, sides) through world points into the mesh, capped, in <paramref name="space"/>.</summary>
        static void Tube(List<Vector3> verts, List<int> tris, List<Vector2> uvs, List<Vector3> path, float radius, int sides, Transform space)
        {
            int start = verts.Count;
            Vector3 t0 = (path[1] - path[0]).normalized;
            Vector3 n = Vector3.Cross(t0, Mathf.Abs(Vector3.Dot(t0, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up).normalized;
            float run = 0f;
            for (int i = 0; i < path.Count; i++)
            {
                Vector3 t = (path[Mathf.Min(i + 1, path.Count - 1)] - path[Mathf.Max(i - 1, 0)]).normalized;
                n = Vector3.ProjectOnPlane(n, t).normalized;
                Vector3 b = Vector3.Cross(t, n);
                if (i > 0) run += (path[i] - path[i - 1]).magnitude;
                for (int k = 0; k < sides; k++)
                {
                    float a = Mathf.PI * 2f * k / sides;
                    verts.Add(space.InverseTransformPoint(path[i] + (n * Mathf.Cos(a) + b * Mathf.Sin(a)) * radius));
                    uvs.Add(new Vector2(k / (float)sides, run * 10f));
                }
            }
            for (int i = 0; i + 1 < path.Count; i++)
                for (int k = 0; k < sides; k++)
                {
                    int a = start + i * sides + k, b2 = start + i * sides + (k + 1) % sides;
                    tris.Add(a); tris.Add(a + sides); tris.Add(b2);
                    tris.Add(b2); tris.Add(a + sides); tris.Add(b2 + sides);
                }
            // caps
            for (int e = 0; e < 2; e++)
            {
                int ring = start + (e == 0 ? 0 : (path.Count - 1) * sides);
                verts.Add(space.InverseTransformPoint(path[e == 0 ? 0 : path.Count - 1])); uvs.Add(Vector2.zero);
                int c = verts.Count - 1;
                for (int k = 0; k < sides; k++) { tris.Add(c); tris.Add(ring + (k + 1) % sides); tris.Add(ring + k); }
            }
        }

        static Material StrapMaterial()
        {
            string path = GKPaths.Materials + "/ShieldStrap.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "ShieldStrap" };
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetColor("_BaseColor", new Color(0.24f, 0.14f, 0.08f));
            m.SetFloat("_Smoothness", 0.32f);
            m.SetFloat("_Cull", 0f);     // the strap's winding follows the path, so draw both faces
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <param name="clean">The clean knight: one-handed clips only (Moves_KnightClean) and none of the pose fixes
        /// that bend the retargeted two-handed clips into a one-handed stance (posture, sword arm, anatomy limits,
        /// inertializer). Only the hand grip and the CC skin bones stay. Saved to <see cref="CleanPrefabPath"/>.</param>
        static void BuildPrefab(bool clean)
        {
            var src = CCPrefab();
            if (src == null) { GKBridge.Log("herocc: no CC Unity Tools prefab in " + Dir + "/Prefabs (run 'herocc import')"); return; }
            NoJaw();
            src = CCPrefab();
            var go = (GameObject)PrefabUtility.InstantiatePrefab(src);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
            int cloth = 0, smr = 0, shapes = 0;
            foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                smr++;
                r.updateWhenOffscreen = false;
                r.skinnedMotionVectors = true;
                if (r.sharedMesh != null) shapes = Math.Max(shapes, r.sharedMesh.blendShapeCount);
                if (r.GetComponent<Cloth>() != null) cloth++;
            }
            var anim = go.GetComponent<Animator>();
            if (anim == null) anim = go.AddComponent<Animator>();
            var av = GKCharacters.GetAvatar(Fbx);
            if (av != null) anim.avatar = av;
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            GKBridge.Log("herocc: visual " + src.name + ": " + smr + " skinned meshes, most blend shapes " + shapes + ", cloth " + cloth +
                         ", avatar " + (anim.avatar != null && anim.avatar.isValid && anim.avatar.isHuman ? "humanoid" : "INVALID"));
            // CC Unity Tools' cloth collision shapes sit on the Default layer, which is our Environment layer: the camera
            // collided with them (it sat inside the head) and the character controller fought them. They only exist for
            // the coat's Unity Cloth, which reads them directly, so they go to a layer that collides with nothing.
            EnsureClothColliderLayer();
            int moved = 0;
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
            {
                if (!CoatPhysics)
                {
                    // nothing simulates against them any more: drop the shapes (and their empty holders)
                    var holder = col.gameObject;
                    UnityEngine.Object.DestroyImmediate(col);
                    if (holder.transform.childCount == 0 && holder.GetComponents<Component>().Length == 1) UnityEngine.Object.DestroyImmediate(holder);
                }
                else col.gameObject.layer = ClothColliderLayer;
                moved++;
            }
            GKBridge.Log("herocc: " + moved + " cloth collision shapes " + (CoatPhysics ? "moved to layer " + ClothColliderLayer + " (ClothCollider, no collisions)" : "removed (coat is skinned only)"));
            TuneCoat(go);
            LeatherSheen();
            TwoSidedCloth(go);
            TrimCovered(go, "Tunic");
            WeldSeams(go, "Long_slim_coat");
            GKCharacters.FitSkinnedBounds(go, anim);
            GKWeapons.Attach(go);
            // the empty scabbard rides back to the side of the hip once the sword is out (the left hand brings it
            // forward only to draw and sheathe): in the guard stance it hangs behind the forward thigh instead of
            // being shoved out sideways like a tail
            var scabMotion = go.GetComponent<ScabbardMotion>();
            if (scabMotion != null) scabMotion.drawnBack = 0.16f;
            GKHeroFit.FitHand(go, GripSlide);
            // the CC twist / share bones carry all the limb skin and Mecanim never poses them (see CCHelperBones)
            if (go.GetComponent<CCHelperBones>() == null) go.AddComponent<CCHelperBones>();
            if (clean)
            {
                GKGameBuilder.BuildCharacterPrefab(true, go, CleanPrefabPath);
                var root = PrefabUtility.LoadPrefabContents(CleanPrefabPath);
                try
                {
                    GKHeroFit.FitCombat(root);
                    PrefabUtility.SaveAsPrefabAsset(root, CleanPrefabPath);
                    GKBridge.Log("herocc clean: prefab " + CleanPrefabPath + ", moveset " + (GKCombatBuilder.CleanPlayerMoves != null ? GKCombatBuilder.CleanPlayerMoves.name : "MISSING (run 'combat')") + ", pose fixes removed");
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
                return;
            }
            // a straight back in the guard stance (see PostureCorrector)
            var posture = go.GetComponent<PostureCorrector>() ?? go.AddComponent<PostureCorrector>();
            posture.hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            posture.spine = anim.GetBoneTransform(HumanBodyBones.Spine);
            posture.chest = anim.GetBoneTransform(HumanBodyBones.Chest);
            posture.upperChest = anim.GetBoneTransform(HumanBodyBones.UpperChest);
            posture.neck = anim.GetBoneTransform(HumanBodyBones.Neck);
            posture.head = anim.GetBoneTransform(HumanBodyBones.Head);
            // arms kept inside what the clips (and a human arm) do, with the measured elbow hinges (see AnatomyLimiter)
            var anatomy = go.GetComponent<AnatomyLimiter>() ?? go.AddComponent<AnatomyLimiter>();
            GKBridge.Log("herocc: anatomy limits " + (GKJointAudit.ApplyRanges(anatomy) ? "from the joint audit" : "not measured yet (run jointaudit)"));
            // braced block, ready carry instead of a torch, a relaxed off hand (see SwordArmPose)
            if (go.GetComponent<SwordArmPose>() == null) go.AddComponent<SwordArmPose>();
            GKGameBuilder.BuildCharacterPrefab(true, go, PrefabPath);
            var built = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var fighter = built != null ? built.GetComponent<Combatant>() : null;
            GKBridge.Log("herocc: prefab " + PrefabPath + ", moveset " + (fighter != null && fighter.moves != null ? fighter.moves.name : "MISSING (no attacks, draw / sheathe would snap)"));
        }
    }
}
