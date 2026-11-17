using System.Collections.Generic;
using System.Linq;
using Unity.AI.Navigation;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering.Universal;

namespace GeorgianKnight.EditorTools
{
    /// <summary>Player / enemy prefabs (ragdoll hurtboxes, weapon, brains) and the playable Didgori scene.</summary>
    public static class GKGameBuilder
    {
        public const string PlayerPrefabPath = GKPaths.Prefabs + "/Knight_Player.prefab";
        public const string EnemyPrefabPath = GKPaths.Prefabs + "/Seljuk_Warrior.prefab";
        public const string GameScenePath = GKPaths.Scenes + "/Didgori.unity";

        public static void BuildGame()
        {
            GKSetup.ConfigureProject();
            GKConfigBuilder.Build("");
            var av = GKCharacters.GetAvatar(GKPaths.KnightFbx);
            if (av == null || !av.isHuman || GKCharacters.KnightMaterial == null) GKCharacters.PrepareAll();
            GKCombatBuilder.BuildAll();
            GKPaths.Ensure(GKPaths.Prefabs);
            var player = BuildCharacterPrefab(true);
            var enemy = BuildCharacterPrefab(false);
            BuildScene(player, enemy);
        }

        // ------------------------------------------------------------------ characters

        static Material EnemyMaterial()
        {
            string p = GKPaths.Materials + "/Seljuk.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (m == null) { m = new Material(GKCharacters.KnightMaterial); AssetDatabase.CreateAsset(m, p); }
            m.CopyPropertiesFromMaterial(GKCharacters.KnightMaterial);
            m.SetColor("_BaseColor", new Color(0.78f, 0.6f, 0.46f));   // sun-faded leather and bronze instead of black and gold
            EditorUtility.SetDirty(m);
            return m;
        }

        sealed class ClothBindPose
        {
            public Dictionary<Transform, Vector3> pos = new Dictionary<Transform, Vector3>();
            public Dictionary<Transform, Quaternion> rot = new Dictionary<Transform, Quaternion>();
            public List<Transform> coneBones = new List<Transform>();
            public List<Vector3> coneA = new List<Vector3>(), coneB = new List<Vector3>();
            public List<float> coneRA = new List<float>(), coneRB = new List<float>();
            public List<string> coneInfo = new List<string>();
        }

        /// <summary>
        /// Tapered capsules around the legs as the cloth meets them: fitted at the bind pose to the outermost leg layer
        /// (trousers, boots; bare skin where nothing covers it), per segment the 60th percentile of the distance from the
        /// bone axis in 8 bands (the seat and the calf's back are not the leg's round), fitted as a straight taper. The
        /// thigh starts a third of the way down so it never reaches into the hips, where the skirt is sewn close.
        /// </summary>
        static void FitLegCones(GameObject go, Dictionary<string, Transform> map, ClothBindPose b)
        {
            // fitted in Blender to the trousers and boots: marker bones GK_Cone_<side>_<seg>_A|B_<radius mm> on the leg bone
            var marks = go.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("GK_Cone_")).ToList();
            if (marks.Count > 0)
            {
                foreach (var ga in marks.Where(t => t.name.Contains("_A_")))
                {
                    string stem = ga.name.Substring(0, ga.name.IndexOf("_A_"));
                    var gb = marks.FirstOrDefault(t => t.name.StartsWith(stem + "_B_"));
                    if (gb == null || ga.parent == null) continue;
                    float ra = int.Parse(ga.name.Substring(ga.name.LastIndexOf('_') + 1)) / 1000f;
                    float rb = int.Parse(gb.name.Substring(gb.name.LastIndexOf('_') + 1)) / 1000f;
                    var bone = ga.parent;
                    b.coneBones.Add(bone);
                    b.coneA.Add(bone.InverseTransformPoint(ga.position)); b.coneB.Add(bone.InverseTransformPoint(gb.position));
                    b.coneRA.Add(ra); b.coneRB.Add(rb);
                    b.coneInfo.Add(stem.Substring("GK_Cone_".Length) + " r " + ra.ToString("F3") + "->" + rb.ToString("F3"));
                }
                if (b.coneBones.Count > 0) return;
            }
            var segs = new[] { new[] { "LeftUpperLeg", "LeftLowerLeg" }, new[] { "LeftLowerLeg", "LeftFoot" },
                               new[] { "RightUpperLeg", "RightLowerLeg" }, new[] { "RightLowerLeg", "RightFoot" } };
            var pts = new Dictionary<Transform, List<Vector3>>();
            foreach (var sg in segs) if (map.ContainsKey(sg[0])) pts[map[sg[0]]] = new List<Vector3>();
            var baked = new Mesh();
            foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r.sharedMesh == null || !r.gameObject.activeInHierarchy) continue;
                var bw = r.sharedMesh.boneWeights;
                if (bw.Length == 0) continue;
                r.BakeMesh(baked);
                var v = baked.vertices;
                var m = Matrix4x4.TRS(r.transform.position, r.transform.rotation, Vector3.one);
                for (int i = 0; i < v.Length && i < bw.Length; i++)
                {
                    if (bw[i].weight0 < 0.6f) continue;
                    var bone = r.bones[bw[i].boneIndex0];
                    List<Vector3> list;
                    if (bone != null && pts.TryGetValue(bone, out list)) list.Add(m.MultiplyPoint3x4(v[i]));
                }
            }
            Object.DestroyImmediate(baked);
            foreach (var sg in segs)
            {
                if (!map.ContainsKey(sg[0]) || !map.ContainsKey(sg[1])) continue;
                Transform bone = map[sg[0]];
                Vector3 a = bone.position, c = map[sg[1]].position, ab = c - a;
                bool thigh = sg[0].EndsWith("UpperLeg");
                float u0 = thigh ? 0.32f : 0.08f, u1 = thigh ? 1.0f : 0.85f;
                var bands = new List<Vector2>();
                for (int k = 0; k < 8; k++)
                {
                    float lo = u0 + (Mathf.Min(u1, 0.95f) - u0) * k / 8f, hi = u0 + (Mathf.Min(u1, 0.95f) - u0) * (k + 1) / 8f;
                    var d = pts[bone].Select(w => new { u = Vector3.Dot(w - a, ab) / ab.sqrMagnitude, w })
                        .Where(x => x.u >= lo && x.u < hi).Select(x => (x.w - (a + ab * x.u)).magnitude).OrderBy(x => x).ToList();
                    if (d.Count >= 12) bands.Add(new Vector2(0.5f * (lo + hi), d[(int)(d.Count * 0.6f)]));
                }
                if (bands.Count < 3) continue;
                // least-squares straight taper r(u) = p + q u
                float n = bands.Count, su = bands.Sum(x => x.x), sr = bands.Sum(x => x.y), suu = bands.Sum(x => x.x * x.x), sur = bands.Sum(x => x.x * x.y);
                float q = (n * sur - su * sr) / Mathf.Max(1e-8f, n * suu - su * su), pp = (sr - q * su) / n;
                b.coneBones.Add(bone);
                b.coneA.Add(bone.InverseTransformPoint(a + ab * u0)); b.coneB.Add(bone.InverseTransformPoint(a + ab * u1));
                b.coneRA.Add(Mathf.Max(0.03f, pp + q * u0)); b.coneRB.Add(Mathf.Max(0.03f, pp + q * u1));
                b.coneInfo.Add(sg[0] + " r " + (pp + q * u0).ToString("F3") + "->" + (pp + q * u1).ToString("F3"));
            }
        }

        /// <summary>The cloth joints in hips space at the bind pose (null when the model has no cloth grid).</summary>
        static ClothBindPose ClothBind(GameObject go, Dictionary<string, Transform> map)
        {
            Transform hips;
            if (!map.TryGetValue("Hips", out hips)) return null;
            var bones = go.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("GK_Cloth_")).ToArray();
            if (bones.Length == 0) return null;
            var b = new ClothBindPose();
            foreach (var t in bones)
            {
                b.pos[t] = hips.InverseTransformPoint(t.position);
                b.rot[t] = Quaternion.Inverse(hips.rotation) * t.rotation;
            }
            FitLegCones(go, map, b);
            return b;
        }

        static int ClothRow(string name, out bool joined)
        {
            string last = name.Substring(name.LastIndexOf('_') + 1);
            joined = last.EndsWith("j");
            return int.Parse(joined ? last.Substring(0, last.Length - 1) : last);
        }

        /// <summary>Columns in order around the body, panel neighbours, seams and overlaps, rest data and clearances.</summary>
        static ClothGrid BuildClothGrid(GameObject go, Dictionary<string, Transform> map, ClothBindPose bind, CapsuleCollider[] legs, bool log)
        {
            var hips = map["Hips"];
            Vector3 right, fwd;
            GKCharacters.CharacterFrame(map, out right, out fwd);
            var cols = bind.pos.Keys.GroupBy(t => t.name.Substring(0, t.name.LastIndexOf('_')))
                .Select(g =>
                {
                    bool j;
                    var bones = g.OrderBy(t => ClothRow(t.name, out j)).ToArray();
                    string key = g.Key.Substring("GK_Cloth_".Length);
                    return new ClothGrid.Column { panel = key.Substring(0, key.LastIndexOf('_')), bones = bones };
                }).ToList();
            int rows = cols.Min(c => c.bones.Length);
            int joined = cols[0].bones.Count(t => { bool j; ClothRow(t.name, out j); return j; });
            if (ClothJoinedRows >= 0) joined = Mathf.Min(ClothJoinedRows, rows);
            System.Func<ClothGrid.Column, float> azimuth = c =>
            {
                Vector3 d = Vector3.ProjectOnPlane(hips.TransformPoint(bind.pos[c.bones[rows - 1]]) - hips.position, Vector3.up);
                return Mathf.Atan2(Vector3.Dot(d, right), Vector3.Dot(d, fwd));
            };
            // panels in order of their mean azimuth, columns in order inside each panel (relative to the panel's mean):
            // the overlapping edge columns of two panels can sit a degree past each other, so a plain sort interleaves them
            var panelMean = cols.GroupBy(c => c.panel).ToDictionary(g => g.Key,
                g => Mathf.Atan2(g.Sum(c => Mathf.Sin(azimuth(c))), g.Sum(c => Mathf.Cos(azimuth(c)))));
            cols = cols.OrderBy(c => panelMean[c.panel])
                       .ThenBy(c => Mathf.DeltaAngle(panelMean[c.panel] * Mathf.Rad2Deg, azimuth(c) * Mathf.Rad2Deg)).ToList();
            int nc = cols.Count;
            var cg = go.GetComponent<ClothGrid>() ?? go.AddComponent<ClothGrid>();
            cg.hips = hips; cg.columns = cols.ToArray(); cg.rows = rows; cg.joinedRows = joined;
            cg.left = new int[nc]; cg.right = new int[nc]; cg.boundaryNext = new int[nc]; cg.overNext = new bool[nc];
            cg.restPos = new Vector3[nc * rows]; cg.restRot = new Quaternion[nc * rows]; cg.clearance = new float[nc * rows];
            for (int c = 0; c < nc; c++)
                for (int r = 0; r < rows; r++)
                {
                    cg.restPos[c * rows + r] = bind.pos[cols[c].bones[r]];
                    cg.restRot[c * rows + r] = bind.rot[cols[c].bones[r]];
                }
            int seams = 0, openings = 0;
            for (int c = 0; c < nc; c++)
            {
                int pc = (c + nc - 1) % nc, nx = (c + 1) % nc;
                cg.left[c] = cols[pc].panel == cols[c].panel ? pc : -1;
                cg.right[c] = cols[nx].panel == cols[c].panel ? nx : -1;
                cg.boundaryNext[c] = -1;
                if (cols[nx].panel != cols[c].panel)
                {
                    float gap = Vector3.Distance(cg.restPos[c * rows + rows - 1], cg.restPos[nx * rows + rows - 1]);
                    if (gap < 0.06f)
                    {
                        cg.boundaryNext[c] = nx;
                        // the hips' local frame may be rotated: compare radii about the hips' own vertical
                        Vector3 upL = hips.InverseTransformDirection(Vector3.up);
                        System.Func<int, float> rad = cc =>
                        {
                            float sum = 0f; int k = 0;
                            for (int r = joined; r < rows; r++) { Vector3 v = cg.restPos[cc * rows + r]; sum += Vector3.ProjectOnPlane(v, upL).magnitude; k++; }
                            return k > 0 ? sum / k : 0f;
                        };
                        cg.overNext[c] = rad(c) > rad(nx);
                        seams++;
                    }
                    else openings++;
                }
            }
            // the leg cones; clearance: what each joint has from them at the bind pose (the layers between), at most 4.5 cm
            cg.coneBones = bind.coneBones.ToArray(); cg.coneA = bind.coneA.ToArray(); cg.coneB = bind.coneB.ToArray();
            cg.coneRA = bind.coneRA.ToArray(); cg.coneRB = bind.coneRB.ToArray();
            for (int i = 0; i < nc * rows; i++)
            {
                Vector3 w = hips.TransformPoint(cg.restPos[i]);
                float best = ClothClearanceCap;
                for (int k = 0; k < cg.coneBones.Length; k++)
                {
                    var t = cg.coneBones[k];
                    Vector3 a = t.TransformPoint(cg.coneA[k]), ab = t.TransformPoint(cg.coneB[k]) - a;
                    float u = Vector3.Dot(w - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude);
                    if (u < 0f) continue;                        // the cones are open above the thigh (ClothGrid)
                    u = Mathf.Min(u, 1f);
                    best = Mathf.Min(best, (w - (a + ab * u)).magnitude - Mathf.Lerp(cg.coneRA[k], cg.coneRB[k], u) - 0.002f);
                }
                cg.clearance[i] = best;
            }
            if (log) GKBridge.Log("  leg cones: " + string.Join("; ", bind.coneInfo.ToArray()));
            cg.colliders = legs;
            if (log)
            {
                var sb = new System.Text.StringBuilder("  cloth ring:");
                for (int c = 0; c < nc; c++)
                {
                    int nx = (c + 1) % nc;
                    sb.Append(' ').Append(cols[c].panel).Append(azimuth(cols[c]).ToString("F0") == "" ? "" : "@" + (azimuth(cols[c]) * Mathf.Rad2Deg).ToString("F0"));
                    if (cols[nx].panel != cols[c].panel)
                        sb.Append(cg.boundaryNext[c] >= 0 ? (cg.overNext[c] ? " >|" : " |<") : " ||" + Vector3.Distance(cg.restPos[c * rows + rows - 1], cg.restPos[nx * rows + rows - 1]).ToString("F2"));
                }
                GKBridge.Log(sb.ToString());
            }
            if (log)
                GKBridge.Log("  cloth grid " + nc + " columns x " + rows + " rows (" + joined + " sewn), " + seams + " slits/seams, " + openings +
                             " opening(s), clearance " + cg.clearance.Min().ToString("F3") + ".." + cg.clearance.Max().ToString("F3") + " m");
            return cg;
        }

        /// <summary>Per-character cloth tuning a builder may set before BuildCharacterPrefab (reset after): the most a
        /// joint keeps off the leg cones (the layers between; 4.5 cm for the Knight's coat over mail and arming coat) and
        /// the rows sewn to the neighbouring panel (-1 = the model's own 'j' rows).</summary>
        internal static float ClothClearanceCap = 0.045f;
        internal static int ClothJoinedRows = -1;

        public static GameObject BuildCharacterPrefab(bool isPlayer, GameObject visual = null, string prefabPath = null)
        {
            // the player is Bakur when his model is in the project (Characters/Bakur); the Knight remains the enemy base
            var go = visual != null ? visual
                   : isPlayer && GKHero.Available ? GKHero.InstantiateVisual() : GKCharacters.InstantiateKnightVisual(isPlayer ? null : EnemyMaterial());
            go.name = isPlayer ? "Knight_Player" : "Seljuk_Warrior";
            go.layer = GK.LayerCharacter;
            go.tag = isPlayer ? "Player" : "Untagged";
            var map = GKCharacters.MapBones(go.transform);
            var clothBind = ClothBind(go, map);       // before the ragdoll fit poses anything

            var anim = go.GetComponent<Animator>();
            anim.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(GKCombatBuilder.ControllerPath);
            anim.applyRootMotion = true;
            anim.cullingMode = isPlayer ? AnimatorCullingMode.AlwaysAnimate : AnimatorCullingMode.CullUpdateTransforms;
            anim.updateMode = AnimatorUpdateMode.Normal;

            var cc = go.AddComponent<CharacterController>();
            cc.height = 1.78f; cc.radius = 0.3f; cc.center = new Vector3(0f, 0.9f, 0f);
            cc.stepOffset = 0.35f; cc.slopeLimit = 50f; cc.skinWidth = 0.03f; cc.minMoveDistance = 0f;
            go.AddComponent<CharacterMotor>();
            var stats = go.AddComponent<CombatStats>();
            if (isPlayer) { stats.maxHealth = 320f; stats.maxStamina = 110f; stats.staminaRegen = 38f; stats.maxPoise = 70f; }
            else { stats.maxHealth = 110f; stats.maxStamina = 80f; stats.staminaRegen = 26f; stats.maxPoise = 45f; }

            var rep = new GKRagdollFit.Report();
            var rag = GKRagdollFit.Build(go, map, rep);
            if (isPlayer)
            {
                GKRagdollFit.Coverage(go, rag, rep);
                GKRagdollFit.TestJoints(go, rep);
                foreach (var l in rep.lines) GKBridge.Log("  " + l);
            }

            if (go.GetComponent<PoseInertializer>() == null) go.AddComponent<PoseInertializer>();

            // the coat skirt as cloth (Bakur): GK_Cloth_<panel>_<column>_<row>[j]; older exports: one chain per panel
            var legCaps = new[] { "LeftUpperLeg", "LeftLowerLeg", "RightUpperLeg", "RightLowerLeg" }
                .Where(n => map.ContainsKey(n)).Select(n => map[n].GetComponent<CapsuleCollider>()).Where(c => c != null).ToArray();
            ClothGrid cloth = clothBind != null ? BuildClothGrid(go, map, clothBind, legCaps, isPlayer) : null;
            var skirtBones = go.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("GK_Skirt_")).ToArray();
            if (cloth == null && skirtBones.Length > 0)
            {
                var sc = go.GetComponent<SkirtChains>() ?? go.AddComponent<SkirtChains>();
                sc.chains = skirtBones.GroupBy(t => t.name.Substring(0, t.name.LastIndexOf('_')))
                    .OrderBy(g => g.Key)
                    .Select(g => new SkirtChains.Chain { name = g.Key, bones = g.OrderBy(t => t.name).ToArray() })
                    .ToArray();
                sc.colliders = legCaps;
                if (isPlayer) GKBridge.Log("  skirt chains " + sc.chains.Length + " x " + (sc.chains.Length > 0 ? sc.chains[0].bones.Length : 0) + ", colliders " + sc.colliders.Length);
            }
            var scabMotion = go.GetComponent<ScabbardMotion>();
            if (scabMotion != null)
            {
                scabMotion.legs = new[] { "LeftUpperLeg", "LeftLowerLeg", "RightUpperLeg" }
                    .Select(n => map[n].GetComponent<CapsuleCollider>()).Where(c => c != null).ToArray();
                Vector3 fr, ff;
                GKCharacters.CharacterFrame(map, out fr, out ff);
                var hipsT = map["Hips"];
                float[] radii;
                // the sword and scabbard must not count as "coat": hide them while sampling the mesh
                if (cloth != null)
                {
                    // the scabbard lies on the coat and the cloth gives way under it (ClothGrid presses it in toward
                    // the thigh); it keeps off the leg by the layers beneath: trousers, arming and mail skirts, coat
                    scabMotion.skirtRadius = null;
                    scabMotion.clearance = 0.05f;
                    cloth.scabbard = scabMotion;
                    if (isPlayer) rep.lines.Add("scabbard rests on the cloth (clearance 0.05 m off the legs)");
                }
                else
                {
                    GKRagdollFit.SkirtProfile(go, hipsT, Vector3.up, ff, 12, 0.06f, 0.06f, -0.75f, out radii, isPlayer ? rep : null);
                    scabMotion.skirtRadius = radii; scabMotion.skirtSectors = 12; scabMotion.skirtTop = 0.06f; scabMotion.skirtStep = 0.06f;
                }
                scabMotion.bodyUpLocal = hipsT.InverseTransformDirection(Vector3.up);
                scabMotion.bodyFwdLocal = hipsT.InverseTransformDirection(ff);
                // the scabbard hangs on the left hip: the left thigh carries it (baked at the bind pose)
                Transform leftThigh;
                if (map.TryGetValue("LeftUpperLeg", out leftThigh)) { scabMotion.thigh = leftThigh; scabMotion.BakeThighRest(); }
                if (isPlayer) GKBridge.Log("  " + rep.lines[rep.lines.Count - 1]);
            }

            var sword = go.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Sword");
            WeaponHitbox hitbox = null;
            if (sword != null)
            {
                hitbox = sword.gameObject.AddComponent<WeaponHitbox>();
                hitbox.bladeBase = sword.Find("BladeBase");
                hitbox.bladeTip = sword.Find("BladeTip");
                hitbox.samples = 6; hitbox.radius = 0.07f;
            }

            var combat = go.AddComponent<Combatant>();
            combat.team = isPlayer ? Team.Player : Team.Enemy;
            combat.moves = isPlayer ? GKCombatBuilder.PlayerMoves : GKCombatBuilder.EnemyMoves;
            combat.weapon = hitbox;
            combat.bodyRadius = 0.4f;
            combat.offHandWeightFree = 1f;   // no shields: a free left arm over the sword-and-shield locomotion
            if (!isPlayer) { combat.parryWindow = 0.15f; }

            if (sword != null)
            {
                var wsc = go.AddComponent<WeaponSelfCollision>();
                wsc.weapon = sword;
                wsc.grip = sword.Find("Grip");
                wsc.bladeBase = sword.Find("BladeBase");
                wsc.bladeTip = sword.Find("BladeTip");
                var swordArm = new HashSet<Transform> { map["RightUpperArm"], map["RightLowerArm"] };
                wsc.avoid = rag.colliders.Where(c => c != null && !swordArm.Contains(c.transform)).ToArray();

                var holster = go.AddComponent<WeaponHolster>();
                holster.weapon = sword;
                holster.hand = map["RightHand"];
                holster.sheath = go.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "ScabbardPivot");
                holster.startSheathed = isPlayer;     // the knight travels with the sword at his hip; enemies come in fighting
            }

            if (hitbox != null)
            {
                var trailGo = new GameObject("BladeTrail");
                trailGo.transform.SetParent(go.transform, false);
                var trail = trailGo.AddComponent<WeaponTrail>();
                trail.bladeBase = hitbox.bladeBase; trail.bladeTip = hitbox.bladeTip; trail.owner = combat;
                if (!isPlayer) trail.color = new Color(1f, 0.78f, 0.6f, 0.4f);
            }

            if (isPlayer)
            {
                go.AddComponent<PlayerBinding>();
                go.AddComponent<GKInput>();
                go.AddComponent<PlayerController>();
                go.AddComponent<LockOnSystem>();
                go.AddComponent<SkillSystem>();
                var climber = go.AddComponent<LadderClimber>();
                climber.profile = AssetDatabase.LoadAssetAtPath<ClimbProfile>(GKTraversalBuilder.ProfilePath);
                if (climber.profile == null) climber.profile = GKTraversalBuilder.BuildProfile();
            }
            else
            {
                var agent = go.AddComponent<NavMeshAgent>();
                agent.radius = 0.35f; agent.height = 1.8f; agent.speed = 3.6f; agent.angularSpeed = 0f;
                agent.acceleration = 30f; agent.stoppingDistance = 0.4f; agent.autoBraking = true;
                agent.obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;
                go.AddComponent<EnemyAI>();
            }

            string path = prefabPath ?? (isPlayer ? PlayerPrefabPath : EnemyPrefabPath);
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            GKBridge.Log("prefab " + path + " ragdoll bodies=" + rag.bodies.Count);
            return prefab;
        }

        // ------------------------------------------------------------------ scene

        /// <summary>Main camera, Cinemachine over-the-shoulder rig and PlayerCamera, wired to the player (Didgori, sandbox).</summary>
        internal static GameObject BuildCameraRig(GameObject player, Vector3 spawn)
        {
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 50f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 32000f;
            var cd = camGo.AddComponent<UniversalAdditionalCameraData>();
            cd.renderPostProcessing = true;
            cd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            cd.antialiasingQuality = AntialiasingQuality.High;
            camGo.AddComponent<AudioListener>();
            var brain = camGo.AddComponent<CinemachineBrain>();
            brain.DefaultBlend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.EaseInOut, 0.4f);

            var pivot = new GameObject("Cam Pivot").transform;
            var look = new GameObject("Cam Look").transform;
            pivot.position = spawn + Vector3.up * 1.58f;
            look.position = pivot.position;
            var vcamGo = new GameObject("CM Player Camera");
            var vcam = vcamGo.AddComponent<CinemachineCamera>();
            vcam.Follow = pivot;   // ThirdPersonFollow: the camera takes the pivot's rotation (no aim component)
            var lens = vcam.Lens; lens.FieldOfView = 50f; lens.NearClipPlane = 0.05f; lens.FarClipPlane = 32000f; vcam.Lens = lens;
            // close over-the-shoulder rig (owner decision 2026-10-20; HANDBOOK/03 §2.1). PlayerCamera drives the framing.
            var tpf = vcamGo.AddComponent<CinemachineThirdPersonFollow>();
            tpf.Damping = new Vector3(0.1f, 0.25f, 0.15f);
            tpf.ShoulderOffset = new Vector3(0.48f, 0.12f, 0f);
            tpf.VerticalArmLength = 0f;
            tpf.CameraSide = 1f;
            tpf.CameraDistance = 2.5f;
            var avoid = tpf.AvoidObstacles;
            avoid.Enabled = true;
            avoid.CollisionFilter = (1 << GK.LayerEnvironment) | (1 << GK.LayerTerrain);
            avoid.IgnoreTag = "Player";
            avoid.CameraRadius = 0.22f;
            avoid.DampingIntoCollision = 0.03f;   // pull in fast
            avoid.DampingFromCollision = 0.6f;    // ease out slow
            tpf.AvoidObstacles = avoid;
            vcamGo.AddComponent<CinemachineImpulseListener>();

            var rig = new GameObject("Camera Rig");
            var pcam = rig.AddComponent<PlayerCamera>();
            pcam.vcam = vcam; pcam.player = player.transform; pcam.pivot = pivot; pcam.lookPoint = look;
            pivot.SetParent(rig.transform, true); look.SetParent(rig.transform, true);
            camGo.transform.position = spawn + new Vector3(0f, 2f, 4f);
            camGo.transform.LookAt(spawn + Vector3.up * 1.4f);

            player.GetComponent<PlayerController>().cameraTransform = camGo.transform;
            player.GetComponent<LockOnSystem>().cameraTransform = camGo.transform;

            // the hero's own fill and rim: spot lights on the camera that light only the hero rendering layer
            HeroLight.MarkHero(player);
            var hl = camGo.AddComponent<HeroLight>();
            var hanim = player.GetComponent<Animator>();
            Transform chest = hanim != null && hanim.avatar != null && hanim.avatar.isHuman
                ? (hanim.GetBoneTransform(HumanBodyBones.UpperChest) ?? hanim.GetBoneTransform(HumanBodyBones.Chest)) : null;
            hl.target = chest != null ? chest : player.transform;
            System.Func<string, Color, float, Light> spot = (n, c, angle) =>
            {
                var g = new GameObject(n); g.transform.SetParent(camGo.transform, false);
                var l = g.AddComponent<Light>();
                l.type = LightType.Spot; l.color = c; l.range = 7f; l.spotAngle = angle; l.innerSpotAngle = angle * 0.45f;
                l.shadows = LightShadows.None; l.renderingLayerMask = 1 << HeroLight.HeroLayerBit;
                return l;
            };
            hl.fill = spot("Hero Fill", new Color(0.98f, 0.96f, 0.93f), 60f);   // neutral: a blue fill tinted the face like dusk
            hl.rim = spot("Hero Rim", new Color(1f, 0.95f, 0.88f), 42f);
            hl.Place();
            return camGo;
        }

        /// <summary>Combat director, feel, hit FX, waves, HUD, audio and bootstrap (Didgori, sandbox).</summary>
        internal static WaveManager BuildGameSystems(GameObject player, GameObject enemyPrefab, Vector3 wavesCenter)
        {
            var systems = new GameObject("Game Systems");
            new GameObject("Combat Director").AddComponent<CombatDirector>().transform.SetParent(systems.transform);
            var feelGo = new GameObject("Combat Feel");
            feelGo.transform.SetParent(systems.transform);
            var impulse = feelGo.AddComponent<CinemachineImpulseSource>();
            var feel = feelGo.AddComponent<CombatFeel>();
            feel.impulse = impulse;
            var fx = new GameObject("Hit FX").AddComponent<HitFX>();
            fx.transform.SetParent(systems.transform);
            var wavesGo = new GameObject("Waves");
            wavesGo.transform.SetParent(systems.transform);
            wavesGo.transform.position = wavesCenter;
            var waves = wavesGo.AddComponent<WaveManager>();
            waves.enemyPrefab = enemyPrefab;
            waves.player = player.GetComponent<Combatant>();
            var hud = new GameObject("HUD").AddComponent<GameHUD>();
            hud.transform.SetParent(systems.transform);
            hud.player = player.GetComponent<Combatant>();
            hud.waves = waves;
            var audioGo = new GameObject("Combat Audio");
            audioGo.transform.SetParent(systems.transform);
            audioGo.AddComponent<CombatAudio>().lib = GKAudioBuilder.Build();
            var boot = new GameObject("Bootstrap").AddComponent<GameBootstrap>();
            boot.transform.SetParent(systems.transform);
            boot.input = player.GetComponent<GKInput>();
            boot.waves = waves;
            return waves;
        }

        static void BuildScene(GameObject playerPrefab, GameObject enemyPrefab)
        {
            GKPaths.Ensure(GKPaths.Scenes);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GKEnvironment.Build();

            // ---- navigation over the plateau
            var navGo = new GameObject("NavMesh");
            var surface = navGo.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Volume;
            surface.center = new Vector3(0f, 0f, 0f);
            surface.size = new Vector3(240f, 120f, 240f);
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = (1 << 0) | (1 << 10);
            surface.BuildNavMesh();
            if (surface.navMeshData != null)
            {
                string np = GKPaths.Generated + "/Environment/DidgoriNavMesh.asset";
                if (AssetDatabase.LoadAssetAtPath<Object>(np) != null) AssetDatabase.DeleteAsset(np);
                AssetDatabase.CreateAsset(surface.navMeshData, np);
            }

            // ---- the knight
            Vector3 spawn = new Vector3(0f, 0f, 4f);
            spawn.y = GKEnvironment.SampleHeight(spawn) + 0.05f;
            var player = (GameObject)PrefabUtility.InstantiatePrefab(playerPrefab);
            player.transform.position = spawn;
            player.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            var idle = GKPreview.Clip("AX_standing_idle");
            if (idle != null) idle.SampleAnimation(player, 0.6f);
            player.transform.position = spawn;                     // sampling a humanoid clip moves the root
            GKHouseImport.PlaceTestHouse(spawn);                   // Blender-built architecture test asset (if present)
            player.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            BuildCameraRig(player, spawn);
            BuildGameSystems(player, enemyPrefab, new Vector3(0f, GKEnvironment.SampleHeight(Vector3.zero), 0f));

            EditorSceneManager.SaveScene(scene, GameScenePath);
            GKPreview.BakeEnvironmentLighting();
            EditorSceneManager.SaveScene(scene, GameScenePath);
            var list = EditorBuildSettings.scenes.Where(s => s.path != GameScenePath).ToList();
            list.Insert(0, new EditorBuildSettingsScene(GameScenePath, true));
            EditorBuildSettings.scenes = list.ToArray();

            // a still for the user: over-the-shoulder view of the knight on the plateau
            var shot = new GameObject("~shot").AddComponent<Camera>();
            shot.gameObject.AddComponent<UniversalAdditionalCameraData>().renderPostProcessing = true;
            shot.fieldOfView = 50f; shot.farClipPlane = 32000f; shot.nearClipPlane = 0.05f;
            shot.transform.position = spawn + new Vector3(-0.9f, 1.75f, 3.9f);
            shot.transform.LookAt(spawn + new Vector3(0.6f, 1.25f, -4f));
            GKPreview.Capture(shot, "game_start");
            Object.DestroyImmediate(shot.gameObject);
            EditorSceneManager.SaveScene(scene, GameScenePath);
            GKBridge.Log("game scene ready: " + GameScenePath);
        }
    }
}
