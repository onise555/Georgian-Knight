using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GeorgianKnight.EditorTools
{
    /// <summary>Play-mode test commands for remote QA (bridge): drive gameplay features and take framed shots.</summary>
    public static class GKPlayCommands
    {
        static GameObject Player() { return GameObject.FindWithTag("Player"); }

        [GKCommand("camera", "camera a|b|c|state: the gameplay camera's framing preset (A today, B closer, C half way; F7 cycles)")]
        static void CameraPresetCmd(string arg)
        {
            arg = (arg ?? "").Trim().ToLowerInvariant();
            if (arg == "a") PlayerCamera.SetPreset(PlayerCamera.FramingPreset.A);
            else if (arg == "b") PlayerCamera.SetPreset(PlayerCamera.FramingPreset.B);
            else if (arg == "c") PlayerCamera.SetPreset(PlayerCamera.FramingPreset.C);
            GKBridge.Log("camera: preset " + PlayerCamera.Preset);
        }

        [GKCommand("tp", "tp <x> <z> [yawDeg]: put the player on the ground at x, z (play mode)")]
        static void Teleport(string arg)
        {
            var p = Player();
            var parts = (arg ?? "").Trim().Split(' ');
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            float x, z, yaw = p != null ? p.transform.eulerAngles.y : 0f;
            if (p == null || parts.Length < 2 || !float.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out x) || !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out z)) { GKBridge.Log("tp: tp <x> <z> [yaw]"); return; }
            if (parts.Length > 2) float.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out yaw);
            Vector3 at = new Vector3(x, 500f, z);
            RaycastHit h;
            if (Physics.Raycast(at, Vector3.down, out h, 1000f, ~0, QueryTriggerInteraction.Ignore)) at.y = h.point.y + 0.05f; else at.y = p.transform.position.y;
            var motor = p.GetComponent<CharacterMotor>();
            if (motor != null) motor.Teleport(at, Quaternion.Euler(0f, yaw, 0f)); else p.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, yaw, 0f));
            GKBridge.Log("tp: " + at.ToString("F1") + " yaw " + yaw.ToString("F0", inv));
        }

        [GKCommand("swingaim", "swingaim on|off|state: the spine bends a cut toward a foe above or below (SwingAim)")]
        static void SwingAimCmd(string arg)
        {
            arg = (arg ?? "").Trim().ToLowerInvariant();
            if (arg == "on") SwingAim.Enabled = true; else if (arg == "off") SwingAim.Enabled = false;
            GKBridge.Log("swingaim: " + (SwingAim.Enabled ? "on" : "off"));
        }

        [GKCommand("tempo", "tempo a|b|state: the swing-speed A/B (b: attacks over ~35 m/s at the tip slowed to ~33, Generated/Resources/swing_tempo_b.txt)")]
        static void Tempo(string arg)
        {
            arg = (arg ?? "").Trim().ToLowerInvariant();
            if (arg == "a") SwingTempo.Set(false); else if (arg == "b") SwingTempo.Set(true);
            GKBridge.Log("tempo: " + (SwingTempo.B ? "B" : "A"));
        }

        /// <summary>The hero's height and gear, measured: body soles to crown, the sword's blade / hilt / overall length,
        /// the scabbard's length, mouth width and thickness, how it hangs (back from vertical, out from the leg) and how
        /// high its tip rides over the ground.</summary>
        [GKCommand("gearqa", "gearqa: hero height, sword blade / hilt / overall, scabbard length, width, hang angles and tip height over the ground")]
        static void GearQa(string arg)
        {
            var p = Player();
            if (p == null) { GKBridge.Log("gearqa: no player"); return; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            System.Func<float, string> cm = v => (v * 100f).ToString("F1", inv);
            var sb = new System.Text.StringBuilder("gearqa:");
            var body = System.Array.Find(p.GetComponentsInChildren<SkinnedMeshRenderer>(true), r => r.name == "CC_Base_Body");
            if (body != null)
            {
                var m = new Mesh(); body.BakeMesh(m);
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var v in m.vertices) { float y = body.transform.TransformPoint(v).y; lo = Mathf.Min(lo, y); hi = Mathf.Max(hi, y); }
                Object.DestroyImmediate(m);
                sb.Append(" body " + cm(hi - lo) + " cm (soles " + lo.ToString("F3", inv) + ")");
            }
            float ground = p.transform.position.y;
            RaycastHit gh;
            if (Physics.Raycast(p.transform.position + Vector3.up * 2f, Vector3.down, out gh, 6f, ~0, QueryTriggerInteraction.Ignore) && !gh.collider.transform.IsChildOf(p.transform)) ground = gh.point.y;
            Transform sword = null, scab = null, pivot = null;
            foreach (var t in p.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Sword" && sword == null) sword = t;
                else if (t.name == "ScabbardPivot") pivot = t;
            }
            if (pivot != null) { scab = pivot.Find("Scabbard"); if (scab == null && pivot.Find("ScabbardFit") != null) scab = pivot.Find("ScabbardFit/Scabbard"); }
            if (sword != null)
            {
                Transform bb = sword.Find("BladeBase"), bt = sword.Find("BladeTip"), gp = sword.Find("Grip");
                var rs = sword.GetComponentsInChildren<Renderer>(true);
                Vector3 axis = bb != null && bt != null ? (bt.position - bb.position).normalized : sword.forward;
                float mn = float.MaxValue, mx = float.MinValue;
                foreach (var r in rs)
                {
                    var mf = r.GetComponent<MeshFilter>(); if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                    foreach (var v in mf.sharedMesh.vertices) { float d = Vector3.Dot(r.transform.TransformPoint(v), axis); mn = Mathf.Min(mn, d); mx = Mathf.Max(mx, d); }
                }
                if (bb != null && bt != null) sb.Append(" | sword overall " + cm(mx - mn) + " blade " + cm(Vector3.Distance(bb.position, bt.position)) + " hilt " + cm(Vector3.Dot(bb.position, axis) - mn) + " cm");
            }
            if (scab != null && pivot != null)
            {
                var mf = scab.GetComponentInChildren<MeshFilter>(true);
                if (mf != null && mf.sharedMesh != null && mf.sharedMesh.isReadable)
                {
                    Vector3 D = pivot.forward;   // throat to tip
                    Vector3 W = pivot.up;
                    Vector3 T = Vector3.Cross(D, W).normalized;
                    float dMin = float.MaxValue, dMax = float.MinValue, wMin = float.MaxValue, wMax = float.MinValue, tMin = float.MaxValue, tMax = float.MinValue, tipY = float.MaxValue;
                    var vs = mf.sharedMesh.vertices;
                    foreach (var v0 in vs)
                    {
                        Vector3 v = mf.transform.TransformPoint(v0) - pivot.position;
                        float d = Vector3.Dot(v, D); dMin = Mathf.Min(dMin, d); dMax = Mathf.Max(dMax, d);
                        tipY = Mathf.Min(tipY, v.y + pivot.position.y);
                    }
                    // width and thickness at the mouth (the first 8 cm below the throat)
                    foreach (var v0 in vs)
                    {
                        Vector3 v = mf.transform.TransformPoint(v0) - pivot.position;
                        float d = Vector3.Dot(v, D); if (d > dMin + 0.08f) continue;
                        float w = Vector3.Dot(v, W), th = Vector3.Dot(v, T);
                        wMin = Mathf.Min(wMin, w); wMax = Mathf.Max(wMax, w); tMin = Mathf.Min(tMin, th); tMax = Mathf.Max(tMax, th);
                    }
                    var anim = p.GetComponent<Animator>();
                    Vector3 fwd = Vector3.ProjectOnPlane(p.transform.forward, Vector3.up).normalized, left = -Vector3.Cross(Vector3.up, fwd);
                    float back = Mathf.Atan2(-Vector3.Dot(D, fwd), -D.y) * Mathf.Rad2Deg, outw = Mathf.Atan2(Vector3.Dot(D, left), -D.y) * Mathf.Rad2Deg;
                    Transform hipsB = anim != null ? anim.GetBoneTransform(HumanBodyBones.Hips) : p.transform;
                    Vector3 rel = pivot.position - hipsB.position;
                    Vector3 lThigh = anim != null ? anim.GetBoneTransform(HumanBodyBones.LeftUpperLeg).position : hipsB.position;
                    Vector3 lKnee = anim != null ? anim.GetBoneTransform(HumanBodyBones.LeftLowerLeg).position : hipsB.position;
                    Vector3 mid = mf.transform.TransformPoint(mf.sharedMesh.bounds.center);
                    sb.Append(" | throat " + cm(Vector3.Dot(rel, fwd)) + " fwd / " + cm(Vector3.Dot(rel, left)) + " left of the hips; scabbard middle " +
                              cm(Vector3.Dot(mid - Vector3.Lerp(lThigh, lKnee, 0.5f), fwd)) + " fwd / " + cm(Vector3.Dot(mid - Vector3.Lerp(lThigh, lKnee, 0.5f), left)) + " out of the mid-thigh");
                    sb.Append(" | scabbard " + cm(dMax - dMin) + " long, mouth " + cm(Mathf.Max(wMax - wMin, tMax - tMin)) + " x " + cm(Mathf.Min(wMax - wMin, tMax - tMin)) +
                              " cm, hangs " + back.ToString("F0", inv) + " deg back / " + outw.ToString("F0", inv) + " deg out, throat " + cm(pivot.position.y - ground) + " tip " + cm(tipY - ground) + " cm over the ground");
                }
            }
            GKBridge.Log(sb.ToString());
        }

        /// <summary>Every enemy's state for "why does he not come": place and distance to the player, whether his agent is
        /// on the NavMesh and its path, the AI's mode and whether a wave engaged him.</summary>
        [GKCommand("enemyqa", "enemyqa: each enemy's position, distance, isOnNavMesh, path status, remaining distance, AI mode, engaged, combat state (play mode)")]
        static void EnemyQa(string arg)
        {
            var p = Player();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder("enemyqa:");
            foreach (var ai in Object.FindObjectsByType<EnemyAI>(FindObjectsSortMode.None))
            {
                var ag = ai.GetComponent<UnityEngine.AI.NavMeshAgent>();
                var c = ai.GetComponent<Combatant>();
                Vector3 at = ai.transform.position;
                sb.Append(" | " + ai.name + " at (" + at.x.ToString("F1", inv) + ", " + at.y.ToString("F1", inv) + ", " + at.z.ToString("F1", inv) + ")" +
                          (p != null ? " " + Vector3.Distance(at, p.transform.position).ToString("F1", inv) + " m" : "") +
                          " onMesh " + (ag != null && ag.isOnNavMesh) + (ag != null && ag.isOnNavMesh ? " path " + ag.pathStatus + " left " + ag.remainingDistance.ToString("F1", inv) : "") +
                          " mode " + ai.CurrentMode + " engaged " + ai.engaged + " ai " + (ai.enabled ? "on" : "off") + (c != null ? " " + c.CurrentState : ""));
            }
            GKBridge.Log(sb.ToString());
        }

        /// <summary>Sets the lanes of each wave on the open scene's WaveManager ("3|3,1|1,2": wave 1 from lane 3, wave 2
        /// from 3 and 1 ...) and saves that scene (edit mode).</summary>
        [GKCommand("wavelanes", "wavelanes <w1>|<w2>|<w3>: lanes per wave on the open scene's WaveManager, e.g. 3|3,1|1,2@HoldPoint_BlackBannerRise (lanes@hold point); saves the scene")]
        static void WaveLanes(string arg)
        {
            if (Application.isPlaying) { GKBridge.Log("wavelanes: edit mode only"); return; }
            var wm = Object.FindAnyObjectByType<WaveManager>();
            if (wm == null) { GKBridge.Log("wavelanes: no WaveManager in the open scene"); return; }
            var parts = (arg ?? "").Trim().Split('|');
            if (parts.Length != wm.waves.Length) { GKBridge.Log("wavelanes: " + wm.waves.Length + " waves, got " + parts.Length); return; }
            for (int i = 0; i < parts.Length; i++)
            {
                // "1,2@HoldPoint_BlackBannerRise": the lanes, then the scene object the wave holds (C17 note)
                var lh = parts[i].Split('@');
                wm.waves[i].lanes = System.Array.ConvertAll(lh[0].Split(','), s => int.Parse(s.Trim()));
                wm.waves[i].holdPoint = lh.Length > 1 ? lh[1].Trim() : "";
            }
            UnityEditor.EditorUtility.SetDirty(wm);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(wm.gameObject.scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(wm.gameObject.scene);
            GKBridge.Log("wavelanes " + wm.gameObject.scene.name + ": " + string.Join(" | ", System.Array.ConvertAll(wm.waves, w => w.title + " [" + string.Join(",", w.lanes) + "]" + (string.IsNullOrEmpty(w.holdPoint) ? "" : " holds " + w.holdPoint + (GameObject.Find(w.holdPoint) != null ? "" : " (NOT IN SCENE)")))) + ", saved");
        }

        /// <summary>Where the player stands: root, feet and capsule bottom against the terrain and the first collider
        /// below (sinking, floating, standing on the wrong layer).</summary>
        [GKCommand("groundqa", "groundqa: the player's root, feet and capsule bottom vs the terrain height and the collider below")]
        static void GroundQa(string arg)
        {
            var p = Player();
            if (p == null) { GKBridge.Log("groundqa: no player"); return; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            System.Func<float, string> f = v => v.ToString("F3", inv);
            Vector3 at = p.transform.position;
            var sb = new System.Text.StringBuilder("groundqa: " + p.name + " root " + f(at.x) + "," + f(at.y) + "," + f(at.z));
            var cc = p.GetComponent<CharacterController>();
            if (cc != null) sb.Append(" | capsule bottom " + f(p.transform.TransformPoint(cc.center).y - cc.height * 0.5f) + " grounded " + cc.isGrounded + " enabled " + cc.enabled);
            var anim = p.GetComponent<Animator>();
            if (anim != null && anim.isHuman)
            {
                sb.Append(" | feet L " + f(anim.GetBoneTransform(HumanBodyBones.LeftFoot).position.y) + " R " + f(anim.GetBoneTransform(HumanBodyBones.RightFoot).position.y) + " hips " + f(anim.GetBoneTransform(HumanBodyBones.Hips).position.y) + " applyRoot " + anim.applyRootMotion);
                sb.Append(" | avatar " + (anim.avatar != null ? anim.avatar.name : "none") + " controller " + (anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "none") + " bodyPos " + f(anim.bodyPosition.y) + " humanScale " + f(anim.humanScale));
                if (Application.isPlaying)
                    for (int l = 0; l < anim.layerCount; l++)
                    {
                        var ci = anim.GetCurrentAnimatorClipInfo(l);
                        if (anim.GetLayerWeight(l) <= 0f && l > 0) continue;
                        sb.Append(" | L" + l + " w" + f(l == 0 ? 1f : anim.GetLayerWeight(l)) + " " + string.Join("+", System.Array.ConvertAll(ci, c => c.clip != null ? c.clip.name + "@" + f(c.weight) : "null")));
                    }
            }
            var t = Terrain.activeTerrain;
            if (t != null) sb.Append(" | terrain " + t.name + " h " + f(t.SampleHeight(at) + t.transform.position.y) + " collider " + (t.GetComponent<TerrainCollider>() != null && t.GetComponent<TerrainCollider>().enabled));
            else sb.Append(" | no active terrain");
            foreach (var h in Physics.RaycastAll(at + Vector3.up * 3f, Vector3.down, 20f, ~0, QueryTriggerInteraction.Ignore))
                if (!h.collider.transform.IsChildOf(p.transform))
                    sb.Append(" | hit " + h.collider.name + " (layer " + LayerMask.LayerToName(h.collider.gameObject.layer) + ") y " + f(h.point.y));
            var rb = p.GetComponentsInChildren<Rigidbody>();
            int dyn = 0; foreach (var r in rb) if (!r.isKinematic) dyn++;
            sb.Append(" | rigidbodies " + rb.Length + " non-kinematic " + dyn);
            GKBridge.Log(sb.ToString());
        }

        /// <summary>clipswap from=to[,from=to...] | reset: plays another clip in place of one the animator uses, on the
        /// running player only (an override controller), so candidate clips can be filmed before anything is rebuilt.</summary>
        [GKCommand("clipswap", "clipswap <usedClip>=<candidateClip>[,...] | reset: try candidate clips on the running player (play mode)")]
        static void ClipSwap(string arg)
        {
            var p = Player();
            var anim = p != null ? p.GetComponent<Animator>() : null;
            if (anim == null) { GKBridge.Log("clipswap: no player animator"); return; }
            arg = (arg ?? "").Trim();
            var aoc = anim.runtimeAnimatorController as AnimatorOverrideController;
            if (arg == "reset")
            {
                if (aoc != null) anim.runtimeAnimatorController = aoc.runtimeAnimatorController;
                GKBridge.Log("clipswap: back to the built controller");
                return;
            }
            if (aoc == null) { aoc = new AnimatorOverrideController(anim.runtimeAnimatorController) { name = "clipswap" }; }
            var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<AnimationClip, AnimationClip>>();
            aoc.GetOverrides(list);
            var sb = new System.Text.StringBuilder();
            foreach (var pair in arg.Split(','))
            {
                var kv = pair.Split('=');
                if (kv.Length != 2) continue;
                var to = FindClip(kv[1].Trim());
                if (to == null) { sb.Append(kv[1] + " not found; "); continue; }
                int hits = 0;
                for (int i = 0; i < list.Count; i++)
                    if (list[i].Key != null && list[i].Key.name == kv[0].Trim()) { list[i] = new System.Collections.Generic.KeyValuePair<AnimationClip, AnimationClip>(list[i].Key, to); hits++; }
                sb.Append(kv[0] + " -> " + to.name + (hits == 0 ? " (not used by the animator)" : "") + "; ");
            }
            aoc.ApplyOverrides(list);
            // the layers keep their state: swapping the controller restarts them, so put the style back afterwards
            var fs = p.GetComponent<FightingStyles>();
            anim.runtimeAnimatorController = aoc;
            if (fs != null) fs.ForceStyle(fs.Style);
            GKBridge.Log("clipswap: " + sb);
        }

        static AnimationClip FindClip(string name)
        {
            foreach (var guid in UnityEditor.AssetDatabase.FindAssets(name + " t:AnimationClip", new[] { "Assets/_GeorgianKnight/Animations" }))
                foreach (var o in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(UnityEditor.AssetDatabase.GUIDToAssetPath(guid)))
                    if (o is AnimationClip c && c.name == name && !c.name.StartsWith("__preview")) return c;
            return null;
        }

        [GKCommand("waves","waves: the run's phase, wave, kills, time, the hero's health and the enemies alive (play mode)")]
        static void Waves(string arg)
        {
            var wm = Object.FindFirstObjectByType<WaveManager>();
            var p = Player();
            var c = p != null ? p.GetComponent<Combatant>() : null;
            int enemies = 0;
            foreach (var e in Object.FindObjectsByType<Combatant>(FindObjectsSortMode.None)) if (e.team != Team.Player && !e.IsDead) enemies++;
            GKBridge.Log("waves: " + (wm != null ? wm.CurrentPhase + " wave " + (wm.WaveIndex + 1) + "/" + wm.WaveCount + ", kills " + wm.Kills +
                         ", " + wm.ElapsedTime.ToString("F0") + " s" : "no WaveManager") + ", hero health " +
                         (c != null && c.Stats != null ? c.Stats.Health.ToString("F0") + "/" + c.Stats.maxHealth.ToString("F0") : "-") + ", enemies alive " + enemies);
        }

        [GKCommand("holster", "holster draw|sheathe|toggle|state (play mode)")]
        static void Holster(string arg)
        {
            var p = Player();
            var h = p != null ? p.GetComponent<WeaponHolster>() : null;
            if (h == null) { GKBridge.Log("holster: no player holster"); return; }
            bool ok = true;
            if (arg == "draw") ok = h.Draw();
            else if (arg == "sheathe") ok = h.Sheathe();
            else if (arg == "toggle") h.Toggle();
            else if (arg == "snapsheathed") h.ForceState(false);
            else if (arg == "snapdrawn") h.ForceState(true);
            else if (arg == "autooff" || arg == "autoon") { var pc = p.GetComponent<PlayerController>(); if (pc != null) pc.autoHolster = arg == "autoon"; }
            GKBridge.Log("holster " + arg + " -> " + ok + ", state " + h.State + ", player state " + p.GetComponent<Combatant>().CurrentState);
        }

        [GKCommand("style", "style 1|2|3|instant1|instant2|instant3|state: fighting style (sword and shield, sword, two swords); instantN skips the gear moves (tests) (play mode)")]
        static void Style(string arg)
        {
            var p = Player();
            var fs = p != null ? p.GetComponent<FightingStyles>() : null;
            if (fs == null) { GKBridge.Log("style: no FightingStyles on the player"); return; }
            arg = (arg ?? "").Trim();
            bool ok = true;
            if (arg.StartsWith("instant") && arg.Length == 8) fs.ForceStyle((WeaponStyle)(arg[7] - '1'));
            else if (arg.Length == 1 && arg[0] >= '1' && arg[0] <= '3') ok = fs.Select((WeaponStyle)(arg[0] - '1'));
            var c = p.GetComponent<Combatant>();
            GKBridge.Log("style " + arg + " -> " + ok + ": " + fs.Style + ", moves " + (c.moves != null ? c.moves.name : "-") +
                         ", shield " + (fs.ShieldOnArm ? "on the arm" : "on the back") + ", second sword " + (fs.OffInHand ? "in hand" : "sheathed"));
        }

        [GKCommand("fxtrace", "fxtrace on|off: log every impact, its result and whether it was a critical to warnings.txt")]
        static void FxTrace(string arg)
        {
            HitFX.Trace = arg == null || arg.Trim() != "off";
            GKBridge.Log("fxtrace: " + (HitFX.Trace ? "on" : "off"));
        }

        [GKCommand("devices", "devices off|on: ignore / read the keyboard, mouse and pad while remote tests drive the player (play mode)")]
        static void Devices(string arg)
        {
            var p = Player();
            var input = p != null ? p.GetComponent<GKInput>() : null;
            if (input == null) { GKBridge.Log("devices: no player input"); return; }
            input.ignoreDevices = (arg ?? "").Trim() == "off";
            GKBridge.Log("devices: " + (input.ignoreDevices ? "ignored (remote test)" : "read again"));
        }

        [GKCommand("holsterqa","holsterqa start|report: per-frame draw/sheathe metrics (play mode)")]
        static void HolsterQa(string arg)
        {
            var p = Player();
            if (p == null) { GKBridge.Log("holsterqa: no player"); return; }
            var probe = p.GetComponent<GKHolsterProbe>();
            if (probe == null) probe = p.AddComponent<GKHolsterProbe>();
            if (arg == "report") GKBridge.Log(probe.Summary());
            else { probe.ResetStats(); GKBridge.Log("holsterqa: started"); }
        }

        [GKCommand("popqa", "popqa start|report: per-frame pose continuity (animation pops) with layer/state causes (play mode)")]
        static void PopQa(string arg)
        {
            var p = Player();
            if (p == null) { GKBridge.Log("popqa: no player"); return; }
            var probe = p.GetComponent<GKPosePopProbe>();
            if (probe == null) probe = p.AddComponent<GKPosePopProbe>();
            if (arg == "report")
            {
                string s = probe.Summary();
                System.IO.File.WriteAllText(System.IO.Path.Combine(GKBridge.Dir, "popqa.txt"), s);
                GKBridge.Log(s.Length > 1500 ? s.Substring(0, 1500) + "… (full: GKBridge/popqa.txt)" : s);
            }
            else { probe.ResetStats(); GKBridge.Log("popqa: started"); }
        }

        [GKCommand("ladderqa", "ladderqa up|down|report: climb the nearest ladder from its foot (up) or top (down) with per-frame contact / pop / penetration metrics (play mode)")]
        static void LadderQa(string arg)
        {
            var p = Player();
            if (p == null) { GKBridge.Log("ladderqa: no player"); return; }
            GKLadderProbe.OutDir = GKBridge.Dir;
            GKLadderProbe.Log = GKBridge.Log;
            var old = p.GetComponent<GKLadderProbe>();
            if (arg == "report") { if (old != null) { old.Write(); GKBridge.Log("ladderqa: report written"); } else GKBridge.Log("ladderqa: not running"); return; }
            if (old != null) Object.Destroy(old);
            var probe = p.AddComponent<GKLadderProbe>();
            probe.Mode = arg == "down" ? "down" : "up";
            GKBridge.Log("ladderqa: started " + probe.Mode);
        }

        [GKCommand("latency", "latency block|light|heavy|dodge|kick|report: input-to-response timing (play mode)")]
        static void Latency(string arg)
        {
            var p = Player();
            if (p == null) { GKBridge.Log("latency: no player"); return; }
            var probe = p.GetComponent<GKLatencyProbe>();
            if (probe == null) probe = p.AddComponent<GKLatencyProbe>();
            if (arg == "report") { GKBridge.Log("latency results:\n" + probe.Results); return; }
            GKLatencyProbe.Action a;
            switch (arg)
            {
                case "block": a = GKLatencyProbe.Action.Block; break;
                case "light": a = GKLatencyProbe.Action.Light; break;
                case "heavy": a = GKLatencyProbe.Action.Heavy; break;
                case "dodge": a = GKLatencyProbe.Action.Dodge; break;
                case "kick": a = GKLatencyProbe.Action.Kick; break;
                default: GKBridge.Log("latency: unknown action " + arg); return;
            }
            probe.Begin(a);
        }

        [GKCommand("inputstate", "log GKInput / controller / game mode state (play mode)")]
        static void InputState(string arg)
        {
            var p = Player();
            if (p == null) { GKBridge.Log("inputstate: no player"); return; }
            var i = p.GetComponent<GKInput>(); var pc = p.GetComponent<PlayerController>(); var c = p.GetComponent<Combatant>();
            GeorgianKnight.Core.IGameStateMachine st; GeorgianKnight.Core.Services.TryGet(out st);
            string devs = "";
            foreach (var d in UnityEngine.InputSystem.InputSystem.devices) devs += d.name + (d.enabled ? "" : "(off)") + " ";
            var kq = UnityEngine.InputSystem.InputSystem.GetDevice<UnityEngine.InputSystem.Keyboard>("GKVirtualKeyboard") ?? UnityEngine.InputSystem.Keyboard.current;
            GKBridge.Log("inputstate: mode " + (st != null ? st.Current.ToString() : "?") + " inGameplay " + (st != null && st.InGameplay) +
                " inputEnabled " + (pc != null && pc.InputEnabled) + " block " + i.BlockHeld + " heavyHeld " + i.HeavyHeld + " move " + i.Move +
                " combatant " + c.CurrentState + " blocking " + c.IsBlocking + " weaponReady " + c.WeaponReady +
                " | bg " + UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior + " devices: " + devs +
                " | q(virtual) " + (kq != null && kq.qKey.isPressed) + " focus " + Application.isFocused + " updateMode " + UnityEngine.InputSystem.InputSystem.settings.updateMode +
                " runInBg " + Application.runInBackground);
        }

        [GKCommand("where", "where <name>[,<name>...]: parent chain and position relative to the player's hips")]
        static void Where(string arg)
        {
            var p = Player();
            if (p == null) { GKBridge.Log("where: no player"); return; }
            var anim = p.GetComponent<Animator>();
            Transform hips = anim != null ? anim.GetBoneTransform(HumanBodyBones.Hips) : p.transform;
            foreach (var n in (arg ?? "").Split(','))
            {
                Transform t = null;
                foreach (var c in p.GetComponentsInChildren<Transform>(true)) if (c.name == n.Trim()) { t = c; break; }
                if (t == null) { GKBridge.Log("where: " + n + " not found under the player"); continue; }
                string chain = t.name; var q = t.parent; int k = 0;
                while (q != null && k++ < 4) { chain += " < " + q.name; q = q.parent; }
                GKBridge.Log("where: " + chain + " | hips-local pos " + hips.InverseTransformPoint(t.position).ToString("F2") + " fwd " + hips.InverseTransformDirection(t.forward).ToString("F2"));
            }
        }

        [GKCommand("gameshot", "gameshot <name> [yawOffset pitch]: render what the gameplay camera sees (play mode)")]
        internal static void GameShot(string arg)
        {
            var parts = (arg ?? "game").Split(' ');
            var pc = Object.FindAnyObjectByType<PlayerCamera>();
            if (pc != null && parts.Length >= 3)
            {
                float yo, pi;
                if (float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out yo) &&
                    float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pi))
                {
                    var p = Player();
                    pc.SetView((p != null ? p.transform.eulerAngles.y : 0f) + yo, pi);
                    GKBridge.Log("gameshot: view set; step a few frames before the next shot to let the rig settle");
                    return;
                }
            }
            var cam = Camera.main;
            if (cam == null) { GKBridge.Log("gameshot: no main camera"); return; }
            GKPreview.Capture(cam, parts[0], 1280, 720);
            if (pc != null)
            {
                var f = pc.CurrentFraming;
                var p = Player();
                float d = p != null ? Vector3.Distance(cam.transform.position, p.transform.position + Vector3.up * 1.5f) : -1f;
                GKBridge.Log(string.Format("gameshot {0}: mode {1}, framing dist {2:F2} shoulder ({3:F2},{4:F2}) fov {5:F1}, actual cam-to-head {6:F2} m, yaw {7:F0} pitch {8:F0}",
                    parts[0], pc.CurrentMode, f.distance, f.shoulderX, f.shoulderY, f.fov, d, pc.Yaw, pc.Pitch));
            }
        }

        /// <summary>closeshot name [side|front|back|hip|hand] — renders from a temporary camera framed on the player.</summary>
        [GKCommand("smrinfo", "smrinfo: the player's skinned renderers (active, root bone, bounds vs the head) for culling bugs")]
        static void SmrInfo(string arg)
        {
            var p = Player();
            if (p == null) { GKBridge.Log("smrinfo: no player"); return; }
            var anim = p.GetComponent<Animator>();
            Transform head = anim != null && anim.isHuman ? anim.GetBoneTransform(HumanBodyBones.Head) : null;
            foreach (var r in p.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Vector3 c = r.bounds.center;
                GKBridge.Log(string.Format("smr {0,-18} active={1} enabled={2} visible={3} root={4} offscreen={5} boundsC={6} size={7} head={8} dHead={9:F2}",
                    r.name, r.gameObject.activeInHierarchy, r.enabled, r.isVisible, r.rootBone != null ? r.rootBone.name : "-",
                    r.updateWhenOffscreen, c.ToString("F2"), r.bounds.size.ToString("F2"), head != null ? head.position.ToString("F2") : "-",
                    head != null ? Vector3.Distance(c, head.position) : -1f));
            }
        }

        /// <summary>The scale proof: a temporary 1.70 m box 1 m beside the player on the same ground, a ruler with a tick every
        /// 10 cm on his other side, and an orthographic camera square-on from the front (no perspective). Nothing stays.</summary>
        [GKCommand("scaleshot", "scaleshot <name>: the player beside a 1.70 m box 1 m away, a 10 cm ruler, orthographic front view (play mode)")]
        static void ScaleShot(string arg)
        {
            var p = Player();
            if (p == null) { GKBridge.Log("scaleshot: no player"); return; }
            string name = string.IsNullOrEmpty(arg) ? "scaleshot" : arg.Trim();
            Vector3 F = Vector3.ProjectOnPlane(p.transform.forward, Vector3.up).normalized, R = Vector3.Cross(Vector3.up, F);
            float g = p.transform.position.y;
            RaycastHit gh;
            if (Physics.Raycast(p.transform.position + Vector3.up * 1.5f, Vector3.down, out gh, 4f, ~0, QueryTriggerInteraction.Ignore) && !gh.collider.transform.IsChildOf(p.transform)) g = gh.point.y;
            var made = new System.Collections.Generic.List<GameObject>();
            System.Func<Vector3, Vector3, Color, GameObject> box = (c, size, col) =>
            {
                var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.DestroyImmediate(b.GetComponent<Collider>());
                b.transform.position = c; b.transform.rotation = Quaternion.LookRotation(F); b.transform.localScale = size;
                var m = new Material(Shader.Find("Universal Render Pipeline/Unlit")); m.color = col;
                b.GetComponent<Renderer>().sharedMaterial = m;
                made.Add(b); return b;
            };
            try
            {
                Vector3 basePos = new Vector3(p.transform.position.x, g, p.transform.position.z);
                box(basePos + R * 1.0f + Vector3.up * 0.85f, new Vector3(0.3f, 1.70f, 0.3f), new Color(0.85f, 0.85f, 0.85f));
                Vector3 rulerAt = basePos - R * 0.6f;
                box(rulerAt + Vector3.up * 0.95f, new Vector3(0.015f, 1.9f, 0.015f), Color.black);
                for (int k = 0; k <= 19; k++)
                    box(rulerAt + Vector3.up * (k * 0.1f) + R * 0.03f, new Vector3(k % 5 == 0 ? 0.12f : 0.06f, 0.006f, 0.01f), k % 5 == 0 ? Color.red : Color.black);
                box(rulerAt + Vector3.up * 1.78f + R * 0.06f, new Vector3(0.14f, 0.008f, 0.01f), new Color(0.1f, 0.4f, 1f));   // 1.78 m mark
                var go = new GameObject("~scaleshot");
                made.Add(go);
                var cam = go.AddComponent<Camera>();
                cam.orthographic = true; cam.orthographicSize = 1.1f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 50f;
                var data = cam.GetUniversalAdditionalCameraData(); data.renderPostProcessing = false;
                Vector3 look = basePos + R * 0.2f + Vector3.up * 1.0f;
                go.transform.position = look + F * 8f;
                go.transform.rotation = Quaternion.LookRotation(-F, Vector3.up);
                GKPreview.Capture(cam, name, 1280, 1280);
                GKBridge.Log("scaleshot: " + name + " (ground " + g.ToString("F3") + ", box 1.70 m, ruler 10 cm ticks, blue at 1.78 m)");
            }
            finally { foreach (var m in made) if (m != null) Object.DestroyImmediate(m); }
        }

        /// <summary>
        /// A close shot of an enemy (the archer's draw, the veteran's mace): the nearest living enemy Combatant to the
        /// player, or the nearest whose name contains the filter, from ~2 m at chest height. Same render as closeshot.
        /// </summary>
        [GKCommand("enemyshot", "enemyshot <name> [filter|-] [front|side|hand|full|face] [dist]: an enemy close up (nearest enemy, or the nearest whose name has the filter; play mode)")]
        static void EnemyShot(string arg)
        {
            var parts = (arg ?? "shot").Trim().Split(' ');
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string name = parts[0], filter = parts.Length > 1 && parts[1] != "-" ? parts[1].ToLowerInvariant() : "", view = parts.Length > 2 ? parts[2] : "front";
            float dist = parts.Length > 3 ? float.Parse(parts[3], inv) : 2f;
            var p = Player();
            Vector3 from = p != null ? p.transform.position : Vector3.zero;
            Combatant e = null; float best = float.MaxValue;
            foreach (var c in Object.FindObjectsByType<Combatant>(FindObjectsSortMode.None))
            {
                if (c.team == Team.Player || !c.isActiveAndEnabled) continue;
                if (filter.Length > 0 && !c.name.ToLowerInvariant().Contains(filter)) continue;
                float d = (c.transform.position - from).sqrMagnitude;
                if (d < best) { best = d; e = c; }
            }
            if (e == null) { GKBridge.Log("enemyshot: no enemy" + (filter.Length > 0 ? " named *" + filter + "*" : "")); return; }
            var anim = e.GetComponentInChildren<Animator>();
            Transform hips = anim != null && anim.isHuman ? anim.GetBoneTransform(HumanBodyBones.Hips) : e.transform;
            Transform chest = anim != null && anim.isHuman ? anim.GetBoneTransform(HumanBodyBones.UpperChest) ?? anim.GetBoneTransform(HumanBodyBones.Chest) : null;
            Transform head = anim != null && anim.isHuman ? anim.GetBoneTransform(HumanBodyBones.Head) : null;
            Transform hand = anim != null && anim.isHuman ? anim.GetBoneTransform(HumanBodyBones.LeftHand) : null;
            Vector3 F = e.transform.forward, R = e.transform.right, U = Vector3.up;
            Vector3 look = chest != null ? chest.position : hips.position + U * 0.35f, pos;
            float fov = 36f;
            switch (view)
            {
                case "side": pos = look - R * dist + U * 0.05f; break;
                // the bow hand (left) and the string, from the front-left
                case "hand": look = hand != null ? hand.position : look; pos = look + (F * 0.8f - R * 0.5f + U * 0.05f).normalized * Mathf.Min(dist, 1.2f); fov = 32f; break;
                case "face": look = head != null ? head.position + U * 0.05f : look + U * 0.4f; pos = look + (F * 0.9f + R * 0.3f) .normalized * Mathf.Min(dist, 1f); fov = 28f; break;
                case "full": look = hips.position + U * 0.05f; pos = look + (F * 0.94f + R * 0.34f).normalized * Mathf.Max(dist, 3.4f) + U * 0.2f; break;
                default: pos = look + (F * 0.87f + R * 0.5f).normalized * dist + U * 0.1f; break;   // front three-quarter
            }
            // nothing between the camera and the enemy: another fighter within half a metre of the line of sight turns the
            // camera round the enemy (60 deg steps) until it is clear (the hero's shield filled a warrior's front still)
            System.Func<Vector3, Combatant> blocker = cp =>
            {
                foreach (var o in Object.FindObjectsByType<Combatant>(FindObjectsSortMode.None))
                {
                    if (o == e || !o.isActiveAndEnabled) continue;
                    Vector3 c = o.transform.position + Vector3.up * 1.0f, ab = look - cp;
                    float k = Mathf.Clamp01(Vector3.Dot(c - cp, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
                    if (k > 0.02f && k < 0.98f && Vector3.Distance(cp + ab * k, c) < 0.5f) return o;
                }
                return null;
            };
            string moved = "";
            var blockedBy = blocker(pos);
            if (blockedBy != null)
            {
                Vector3 off = pos - look;
                foreach (float a in new[] { 60f, -60f, 120f, -120f, 180f })
                {
                    Vector3 cand = look + Quaternion.AngleAxis(a, Vector3.up) * off;
                    if (blocker(cand) == null) { pos = cand; moved = " (turned " + a.ToString("F0") + " deg round, " + blockedBy.name + " was in the way)"; break; }
                }
                if (moved.Length == 0) moved = " (BLOCKED by " + blockedBy.name + " from every side)";
            }
            var go = new GameObject("~enemyshot");
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.nearClipPlane = 0.03f; cam.farClipPlane = 30000f; cam.fieldOfView = fov;
                cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
                go.transform.position = pos;
                go.transform.rotation = Quaternion.LookRotation(look - pos, Vector3.up);
                GKPreview.Capture(cam, name, 1280, 720);
                GKBridge.Log("enemyshot: " + e.name + " " + view + " from " + Vector3.Distance(pos, look).ToString("F2", inv) + " m, " + Mathf.Sqrt(best).ToString("F1", inv) + " m from the player" +
                             (e.CurrentAttack != null ? ", in " + e.CurrentAttack.name : "") + moved + " -> " + name);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [GKCommand("closeshot", "closeshot <name> [side|front|back|back34|full|chest|face|hip|hand]")]
        static void CloseShot(string arg)
        {
            var parts = (arg ?? "shot").Split(' ');
            string name = parts[0], view = parts.Length > 1 ? parts[1] : "side";
            var p = Player();
            if (p == null) { GKBridge.Log("closeshot: no player"); return; }
            var anim = p.GetComponent<Animator>();
            Transform hips = anim != null ? anim.GetBoneTransform(HumanBodyBones.Hips) : p.transform;
            Transform hand = anim != null ? anim.GetBoneTransform(HumanBodyBones.RightHand) : p.transform;
            Vector3 F = p.transform.forward, R = p.transform.right, U = Vector3.up;
            Vector3 look = hips.position, pos;
            float fov = 40f;
            switch (view)
            {
                case "front": pos = look + F * 2.6f + U * 0.3f; look += U * 0.25f; break;
                case "back": pos = look - F * 2.4f - R * 0.6f + U * 0.4f; break;
                case "hip": look = hips.position - R * 0.18f - U * 0.1f; pos = look - R * 1.2f + F * 0.5f + U * 0.15f; fov = 38f; break;
                case "hand": look = hand.position; pos = look + R * 0.6f + F * 0.4f + U * 0.1f; fov = 34f; break;
                case "face":
                {
                    Transform head = anim != null ? anim.GetBoneTransform(HumanBodyBones.Head) : p.transform;
                    float dist = parts.Length > 2 ? float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) : 0.75f;
                    look = head.position + U * 0.06f; pos = look + (F * 0.75f + R * 0.25f + U * 0.02f) * (dist / 0.75f); fov = 28f; break;
                }
                case "chest":
                {
                    Transform chest = anim != null ? anim.GetBoneTransform(HumanBodyBones.UpperChest) ?? anim.GetBoneTransform(HumanBodyBones.Chest) : p.transform;
                    look = chest.position; pos = look + F * 1.25f + R * 0.55f + U * 0.1f; fov = 34f; break;
                }
                case "full": look = hips.position + U * 0.05f; pos = look + F * 3.3f + R * 1.2f + U * 0.2f; fov = 36f; break;
                case "back34": look = hips.position + U * 0.1f; pos = look - F * 2.6f + R * 1.4f + U * 0.35f; fov = 36f; break;
                // the hero beside World's 1.70 m scale figure, square-on from 7 m at waist height (an editor aid: edit mode)
                case "scale":
                {
                    var fig = GameObject.Find("ScaleFigure_1.70m");
                    Vector3 fp = fig != null ? fig.transform.position : p.transform.position + F * 1.2f;
                    Vector3 a = p.transform.position, mid = (a + fp) * 0.5f;
                    Vector3 across = Vector3.Cross(Vector3.up, Vector3.ProjectOnPlane(fp - a, Vector3.up).normalized);
                    look = new Vector3(mid.x, a.y + 0.95f, mid.z); pos = look + across * 7f; fov = 24f; break;
                }
                // straight down from above the head (where the scabbard lies against the thigh, in front or behind)
                case "top": look = hips.position - U * 0.2f; pos = look + U * 3.2f + F * 0.02f; fov = 34f; break;
                // the left hip from its own side, low (the scabbard against the thigh)
                case "lefthip": look = hips.position - R * 0.2f - U * 0.25f; pos = look - R * 2.2f + U * 0.1f; fov = 34f; break;
                default: pos = look - R * 2.6f + U * 0.3f; look += U * 0.2f; break;
            }
            var go = new GameObject("~closeshot");
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.nearClipPlane = 0.03f; cam.farClipPlane = 30000f; cam.fieldOfView = fov;
                var data = cam.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = true;
                go.transform.position = pos;
                go.transform.rotation = Quaternion.LookRotation(look - pos, Vector3.up);
                GKPreview.Capture(cam, name, 1280, 720);
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
