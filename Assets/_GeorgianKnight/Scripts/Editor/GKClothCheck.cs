using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Drives the player's coat cloth and scabbard through clips frame by frame (60 Hz, root moving at the clip's own
    /// speed so running drags the cloth) and measures what a viewer would call a bug: skirt vertices inside a leg,
    /// cloth stretched past its length, particles jumping. Optionally photographs each clip at three moments.
    /// </summary>
    public static class GKClothCheck
    {
        static readonly string[] DefaultClips =
        {
            "AX_standing_idle", "AX_standing_walk_forward", "GK_Run_Sword", "AX_standing_melee_attack_360_high", "SS_Sparta_Kick",
            "AX_standing_melee_attack_kick_ver_1", "GK_Dodge_Left", "GK_Roll_Stand", "GK_KD_Back", "SS_Power_Slash", "GK_Sword_Draw"
        };

        [GKCommand("clothcheck", "clothcheck [clip,clip|all] [shots]: coat cloth + scabbard through clips (metrics, optional frames)")]
        static void Cmd(string a) { Run(a); }

        public static void Run(string arg)
        {
            var parts = (arg ?? "").Split(' ').Where(s => s.Length > 0).ToArray();
            bool shots = parts.Contains("shots");
            bool debug = parts.Contains("debug");
            string filter = parts.FirstOrDefault(s => s != "shots" && s != "debug");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKGameBuilder.PlayerPrefabPath);
            var temps = new List<GameObject>();
            var hidden = new List<GameObject>();
            foreach (var c in Object.FindObjectsByType<Combatant>(FindObjectsSortMode.None))
                if (c.gameObject.activeSelf) { c.gameObject.SetActive(false); hidden.Add(c.gameObject); }
            var sb = new StringBuilder("clothcheck: skirt penetration into the legs (mm, worst / frames over 5 mm), strain, particle speed\n");
            try
            {
                Vector3 P = new Vector3(0f, 0f, 4f);
                P.y = GKEnvironment.SampleHeight(P);
                Quaternion Q = Quaternion.Euler(0f, 180f, 0f);
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                temps.Add(inst);
                inst.transform.SetPositionAndRotation(P, Q);
                var cc = inst.GetComponent<CharacterController>(); if (cc != null) cc.enabled = false;
                var cloth = inst.GetComponent<ClothGrid>();
                var scab = inst.GetComponent<ScabbardMotion>();
                if (cloth == null) { GKBridge.Log("clothcheck: no ClothGrid on the player"); return; }
                cloth.Prepare();
                if (scab != null) scab.Init();
                var anim = inst.GetComponent<Animator>();
                var all = anim.runtimeAnimatorController.animationClips.Distinct().ToList();
                List<AnimationClip> clips;
                if (filter == "all") clips = all.OrderBy(c => c.name).ToList();
                else if (!string.IsNullOrEmpty(filter)) clips = all.Where(c => filter.Split(',').Any(f => c.name.ToLowerInvariant().Contains(f.ToLowerInvariant()))).ToList();
                else clips = DefaultClips.Select(n => all.FirstOrDefault(c => c.name == n)).Where(c => c != null).ToList();

                // the skirt's vertices: everything the cloth bones move (coat skirt, lining, border, arming and mail skirts)
                var smrs = inst.GetComponentsInChildren<SkinnedMeshRenderer>().Where(r => r.sharedMesh != null).ToArray();
                var watch = new List<KeyValuePair<SkinnedMeshRenderer, int[]>>();
                foreach (var r in smrs)
                {
                    var clothBones = new HashSet<int>(Enumerable.Range(0, r.bones.Length).Where(b => r.bones[b] != null && r.bones[b].name.StartsWith("GK_Cloth_")));
                    if (clothBones.Count == 0) continue;
                    var bw = r.sharedMesh.boneWeights;
                    // the coat itself: vertices moved by the cloth bones alone (skirt, lining, its gold border)
                    System.Func<int, float> clothW = i =>
                        (clothBones.Contains(bw[i].boneIndex0) ? bw[i].weight0 : 0f) + (clothBones.Contains(bw[i].boneIndex1) ? bw[i].weight1 : 0f) +
                        (clothBones.Contains(bw[i].boneIndex2) ? bw[i].weight2 : 0f) + (clothBones.Contains(bw[i].boneIndex3) ? bw[i].weight3 : 0f);
                    var idx = Enumerable.Range(0, bw.Length).Where(i => clothW(i) > 0.95f).ToArray();
                    // every 3rd vertex is plenty for a 1-2 cm grid
                    watch.Add(new KeyValuePair<SkinnedMeshRenderer, int[]>(r, idx.Where((v, k) => k % 3 == 0).ToArray()));
                }
                var cam = new GameObject("gk_cloth_cam"); temps.Add(cam);
                var camC = cam.AddComponent<Camera>(); camC.nearClipPlane = 0.02f; camC.farClipPlane = 30000f;
                var cd = camC.GetUniversalAdditionalCameraData(); cd.renderPostProcessing = true; cd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                var baked = new Mesh();
                int flagged = 0;
                foreach (var clip in clips)
                {
                    cloth.ResetDynamics(); if (scab != null) scab.ResetDynamics();
                    Vector3 vel = clip.averageSpeed; vel.y = 0f;
                    int frames = Mathf.Max(2, Mathf.CeilToInt(clip.length * 60f));
                    // settle on the first frame
                    for (int f = 0; f < 30; f++) { Pose(inst, clip, 0f, P, Q, Vector3.zero); cloth.Simulate(1f / 60f); if (scab != null) scab.Solve(1f / 60f); }
                    float worstPen = 0f, worstT = 0f, maxStrain = 1f, maxSpeed = 0f, speedT = 0f; int deepFrames = 0, speedCol = -1; string strainAt = "-", penAt = "-";
                    var last = new Vector3[cloth.columns.Length];
                    for (int f = 0; f <= frames; f++)
                    {
                        float t = clip.length * f / frames;
                        Pose(inst, clip, t, P, Q, vel);
                        cloth.Simulate(1f / 60f);
                        if (scab != null) scab.Solve(1f / 60f);
                        float st = cloth.MaxStrain();
                        if (st > maxStrain) { maxStrain = st; strainAt = cloth.StrainWhere(); }
                        for (int c = 0; c < cloth.columns.Length; c++)
                        {
                            Vector3 h = cloth.Particle(c, cloth.rows - 1) - inst.transform.position;
                            float sp = (h - last[c]).magnitude * 60f;
                            if (f > 0 && sp > maxSpeed) { maxSpeed = sp; speedCol = c; speedT = t; }
                            last[c] = h;
                        }
                        if (f % 4 == 0)
                        {
                            string where;
                            float pen = Penetration(watch, cloth, baked, out where);
                            if (pen > worstPen) { worstPen = pen; worstT = t; penAt = where; }
                            if (pen > 0.005f) deepFrames++;
                        }
                        if (debug && f == frames / 2) DebugShots(inst, cloth, camC, clip.name, temps);
                        if (shots && (f == frames / 4 || f == frames / 2 || f == frames * 3 / 4))
                        {
                            Vector3 hip = cloth.hips.position;
                            Vector3 F = inst.transform.forward, R = inst.transform.right;
                            string tag = clip.name + "_" + (f * 4 / frames);
                            Shot(camC, hip - R * 2.3f + Vector3.up * 0.15f, hip - Vector3.up * 0.1f, 38f, "cc_" + tag + "_side");
                            Shot(camC, hip - F * 2.2f + R * 1.1f + Vector3.up * 0.35f, hip - Vector3.up * 0.1f, 38f, "cc_" + tag + "_back");
                        }
                    }
                    bool bad = deepFrames >= 2 || maxStrain > 1.12f || maxSpeed > 12f;
                    if (bad) flagged++;
                    string line = string.Format("{0}{1,-36} pen {2,5:F0} mm @{3:F2}s ({4} frames), strain {5:F2}, hem speed {6:F1} m/s ({9}@{10:F2}s)  [pen {7}] [strain {8}]",
                        bad ? "! " : "  ", clip.name, worstPen * 1000f, worstT, deepFrames, maxStrain, maxSpeed, penAt, strainAt,
                        speedCol >= 0 ? cloth.columns[speedCol].panel + speedCol : "-", speedT);
                    sb.AppendLine(line);
                    GKBridge.Log("cloth " + line);
                }
                Object.DestroyImmediate(baked);
                System.IO.File.WriteAllText(System.IO.Path.Combine(GKBridge.Dir, "clothcheck.txt"), sb.ToString());
                GKBridge.Log("clothcheck: " + flagged + " of " + clips.Count + " clips flagged");
            }
            finally
            {
                foreach (var g in temps) if (g != null) Object.DestroyImmediate(g);
                foreach (var g in hidden) if (g != null) g.SetActive(true);
            }
        }

        static void Pose(GameObject inst, AnimationClip clip, float t, Vector3 P, Quaternion Q, Vector3 vel)
        {
            clip.SampleAnimation(inst, t);
            inst.transform.SetPositionAndRotation(P + Q * (vel * t), Q);
        }

        /// <summary>Deepest skirt vertex inside the legs (the cloth's leg cones: trousers and boots) (m, 0 = none).</summary>
        static float Penetration(List<KeyValuePair<SkinnedMeshRenderer, int[]>> watch, ClothGrid cloth, Mesh baked, out string where)
        {
            float worst = 0f; where = "-";
            foreach (var kv in watch)
            {
                kv.Key.BakeMesh(baked);
                var v = baked.vertices;
                Matrix4x4 m = Matrix4x4.TRS(kv.Key.transform.position, kv.Key.transform.rotation, Vector3.one);
                foreach (int i in kv.Value)
                {
                    Vector3 w = m.MultiplyPoint3x4(v[i]);
                    int cone;
                    float pen = cloth.Inside(w, out cone);
                    if (pen > worst)
                    {
                        worst = pen;
                        where = kv.Key.name + " v" + i + " hips" + cloth.hips.InverseTransformPoint(w).ToString("F2") + " in " + cloth.coneBones[cone].name;
                    }
                }
            }
            return worst;
        }

        /// <summary>The leg cones as spheres along each taper and the cloth particles as dots, photographed front and side.</summary>
        static void DebugShots(GameObject inst, ClothGrid cloth, Camera cam, string clip, List<GameObject> temps)
        {
            var made = new List<GameObject>();
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            var red = new Material(shader); red.color = new Color(0.9f, 0.15f, 0.1f);
            var yel = new Material(shader); yel.color = new Color(1f, 0.85f, 0.1f);
            var blu = new Material(shader); blu.color = new Color(0.2f, 0.6f, 1f);
            for (int k = 0; k < cloth.coneBones.Length; k++)
            {
                var t = cloth.coneBones[k];
                Vector3 a = t.TransformPoint(cloth.coneA[k]), b = t.TransformPoint(cloth.coneB[k]);
                for (int j = 0; j <= 8; j++)
                {
                    float u = j / 8f;
                    var g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Object.DestroyImmediate(g.GetComponent<Collider>());
                    g.transform.position = Vector3.Lerp(a, b, u);
                    g.transform.localScale = Vector3.one * 2f * Mathf.Lerp(cloth.coneRA[k], cloth.coneRB[k], u);
                    g.GetComponent<MeshRenderer>().sharedMaterial = red;
                    made.Add(g);
                }
            }
            for (int c = 0; c < cloth.columns.Length; c++)
                for (int r = 1; r < cloth.rows; r++)
                {
                    var g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Object.DestroyImmediate(g.GetComponent<Collider>());
                    g.transform.position = cloth.Particle(c, r);
                    g.transform.localScale = Vector3.one * 0.022f;
                    g.GetComponent<MeshRenderer>().sharedMaterial = r < cloth.joinedRows ? blu : yel;
                    made.Add(g);
                }
            var skins = inst.GetComponentsInChildren<SkinnedMeshRenderer>().Where(r => r.name == "Bakur_Cloth" || r.name == "Bakur_Trim" || r.name == "Bakur_Mail" || r.name == "Bakur_Armour").ToList();
            Vector3 hip = cloth.hips.position, F = inst.transform.forward, R = inst.transform.right;
            foreach (var sm in skins) sm.enabled = false;
            Shot(cam, hip + F * 2.0f + Vector3.up * 0.1f, hip - Vector3.up * 0.2f, 40f, "cd_" + clip + "_front_bare");
            Shot(cam, hip + R * 2.0f + Vector3.up * 0.1f, hip - Vector3.up * 0.2f, 40f, "cd_" + clip + "_side_bare");
            foreach (var sm in skins) sm.enabled = true;
            Shot(cam, hip + F * 2.0f + Vector3.up * 0.1f, hip - Vector3.up * 0.2f, 40f, "cd_" + clip + "_front");
            Shot(cam, hip + R * 2.0f + Vector3.up * 0.1f, hip - Vector3.up * 0.2f, 40f, "cd_" + clip + "_side");
            foreach (var g in made) Object.DestroyImmediate(g);
            Object.DestroyImmediate(red); Object.DestroyImmediate(yel); Object.DestroyImmediate(blu);
        }

        static void Shot(Camera cam, Vector3 pos, Vector3 look, float fov, string name)
        {
            cam.transform.position = pos;
            cam.transform.rotation = Quaternion.LookRotation(look - pos, Vector3.up);
            cam.fieldOfView = fov;
            GKPreview.Capture(cam, name, 960, 720);
        }
    }
}
