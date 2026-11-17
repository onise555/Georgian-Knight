using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Builds the combat data (attacks with analysed hit windows, combo graphs, move sets) and the
    /// animator controller that plays them (locomotion / strafe trees, action states, block and
    /// off-hand layers).
    /// </summary>
    public static class GKCombatBuilder
    {
        public const string ControllerPath = GKPaths.Generated + "/GK_Knight.controller";
        /// <summary>Playback speed of the shield's move between arm and back (a weapon-slot switch, S1): 1.7 makes the
        /// 1.63 s clip 0.96 s. Team Lead 2026-10-31: at most 1.0 s, readable, never a snap (it was 1.15: 1.42 s).</summary>
        public const float ShieldMoveSpeed = 1.7f;
        /// <summary>Seconds an enemy's unblockable gives from its tell to contact (Team Lead: 0.80 with margin; the clip
        /// estimate reads ~0.02 s long in play, so the target is set at 0.84).</summary>
        const float UnblockableRead = 0.84f;
        /// <summary>Seconds any other enemy blow (not the kick) gives from its start to contact (QUALITY: 0.55).</summary>
        const float EnemyRead = 0.58f;
        static string AttackDir { get { return GKPaths.Data + "/Attacks"; } }

        // The movesets are assets (Data/Moves_*.asset). The statics used to be filled only by BuildAll, so any prefab
        // built after a script reload (herocc prefab, hero v2) got a Combatant with no moves: no attacks, kicks or
        // dodges, and draw / sheathe snapped instead of animating. They now fall back to the assets on disk.
        static MoveSet playerMoves, enemyMoves;
        public static MoveSet PlayerMoves
        {
            get { if (playerMoves == null) playerMoves = AssetDatabase.LoadAssetAtPath<MoveSet>(GKPaths.Data + "/Moves_Knight.asset"); return playerMoves; }
            set { playerMoves = value; }
        }
        public static MoveSet EnemyMoves
        {
            get { if (enemyMoves == null) enemyMoves = AssetDatabase.LoadAssetAtPath<MoveSet>(GKPaths.Data + "/Moves_Seljuk.asset"); return enemyMoves; }
            set { enemyMoves = value; }
        }

        // every clip the controller must contain: state name -> (clip, speed)
        static readonly Dictionary<string, KeyValuePair<AnimationClip, float>> actionStates = new Dictionary<string, KeyValuePair<AnimationClip, float>>();
        static GameObject probe;
        static Transform probeTip;

        /// <summary>Clips the locomotion, guard, off-hand and draw / sheathe states cannot do without. On 10-27 a rebuild
        /// ran with GKPaths.Mixamo on a folder that no longer existed: it wrote an animator with 150 of 170 motions gone,
        /// silently, and the hero sank into the ground in play.</summary>
        static readonly string[] CoreClips = { "SS_Idle", "SS_Walk", "SS_Run", "AX_unarmed_idle", "AX_unarmed_walk_forward",
            "AX_unarmed_run_forward", "KI_CombatIdle1H", "SS_draw_sword_1", "SS_sheath_sword_1", "AX_standing_disarm_over_shoulder",
            "AX_unarmed_equip_over_shoulder" };
        const int MinClipModels = 100;

        /// <summary>True when the clip library is where GKPaths says and the core clips load; otherwise logs an error and
        /// the build writes nothing.</summary>
        static bool ClipsPresent()
        {
            int models = AssetDatabase.IsValidFolder(GKPaths.Mixamo) ? AssetDatabase.FindAssets("t:Model", new[] { GKPaths.Mixamo }).Length : 0;
            var missing = CoreClips.Where(n => Clip(n) == null).ToList();
            if (models >= MinClipModels && missing.Count == 0) return true;
            string why = "combat: REFUSED, nothing written: " + models + " clip models in " + GKPaths.Mixamo + " (need " + MinClipModels + ")" +
                         (missing.Count > 0 ? ", missing " + string.Join(", ", missing) : "");
            Debug.LogError(why);
            GKBridge.Log(why);
            return false;
        }

        public static void BuildAll()
        {
            if (!ClipsPresent()) return;
            GKPaths.Ensure(AttackDir);
            actionStates.Clear();
            probe = GKCharacters.InstantiateKnightVisual();
            probe.name = "~probe";
            probeTip = probe.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "BladeTip");
            try
            {
                windupNeeds.Clear();
                phases.Clear(); suspect.Clear();
                BuildPlayerMoves();
                BuildEnemyMoves();
                BuildArcherMoves();
                BuildVeteranMoves();
                if (ApplyTrims())
                {
                    // clips changed on disk: rebuild the probe and analyse the trimmed clips again
                    Object.DestroyImmediate(probe);
                    probe = GKCharacters.InstantiateKnightVisual();
                    probe.name = "~probe";
                    probeTip = probe.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "BladeTip");
                    actionStates.Clear();
                    phases.Clear(); suspect.Clear();
                    BuildPlayerMoves();
                    BuildEnemyMoves();
                    BuildArcherMoves();
                    BuildVeteranMoves();
                }
            }
            finally { Object.DestroyImmediate(probe); probe = null; }
            System.IO.File.WriteAllLines(PhasesPath, phases);
            if (suspect.Count > 0) GKBridge.Log("swing: recalibrate " + string.Join(", ", suspect.Distinct()));
            BuildController();
            AssetDatabase.SaveAssets();
            GKBridge.Log("combat data + animator built: " + actionStates.Count + " action states");
        }

        // ------------------------------------------------------------------ clips

        public static AnimationClip Clip(string name) { return GKPreview.Clip(name); }

        static void Register(string state, AnimationClip clip, float speed)
        {
            if (clip == null || string.IsNullOrEmpty(state)) return;
            actionStates[state] = new KeyValuePair<AnimationClip, float>(clip, speed);
        }

        static ActionClip Act(string clipName, float speed = 1f, float fade = 0.1f, float end = 0.92f, float cancelFrom = 0.75f,
                              bool root = true, float iStart = 0f, float iEnd = 0f, float offHand = 0f)
        {
            var c = Clip(clipName);
            var a = new ActionClip { state = clipName, clip = c, speed = speed, fadeIn = fade, end = end, cancelFrom = cancelFrom, rootMotion = root, iFrameStart = iStart, iFrameEnd = iEnd, offHand = offHand };
            if (c == null) { GKBridge.Log("missing clip " + clipName); a.state = ""; return a; }
            Register(clipName, c, speed);
            return a;
        }

        class Spec
        {
            public AttackKind kind = AttackKind.Light;
            public float speed = 1f, damage = 18f, poise = 18f, stamina = 10f, knockback = 1.1f, hitStop = 0.06f, shake = 0.25f, fade = 0.08f, offHand;
            public HitReaction reaction = HitReaction.Light;
            public bool hyper, guardBreak, unblockable, chargeable;
            public float warp = 2.6f;
        }

        // clip -> wind-up (seconds of clip time before the hit) the slowest user of that clip needs
        static readonly Dictionary<string, float> windupNeeds = new Dictionary<string, float>();

        static float WindupFor(Spec s, bool enemy)
        {
            if (s.unblockable) return 0.78f;
            if (enemy) return 0.55f;
            switch (s.kind)
            {
                case AttackKind.Light: return 0.32f;
                case AttackKind.Riposte: return 0.3f;
                case AttackKind.Kick: return 0.3f;
                case AttackKind.Execution: return 99f;
                default: return 0.5f;
            }
        }

        /// <summary>Desired press-to-contact time per attack kind (player) — enemies keep their readable wind-ups.</summary>
        static float TargetWindup(AttackData a)
        {
            if (a.name.StartsWith("E_")) return 0f;
            switch (a.kind)
            {
                case AttackKind.Light: return 0.18f;
                case AttackKind.Riposte: return 0.2f;
                case AttackKind.Kick: return 0.2f;
                case AttackKind.Sprint: return 0.26f;
                case AttackKind.Heavy: return 0.36f;
                default: return 0f;
            }
        }

        /// <summary>A swing played mirrored (state "clip@M"): the left hand's version of a right-handed Mixamo cut. The
        /// timing analysis of the clip holds as it is: a mirror keeps every height, reach and moment.</summary>
        static AttackData AtkL(string asset, string clipName, Spec s)
        {
            var a = Atk(asset, clipName, s);
            if (a == null) return null;
            a.state = clipName + "@M";
            a.hand = WeaponHand.Off;
            Register(a.state, a.clip, s.speed);
            EditorUtility.SetDirty(a);
            return a;
        }

        static AttackData Atk(string asset, string clipName, Spec s)
        {
            float need = WindupFor(s, asset.StartsWith("E_"));
            float prev;
            windupNeeds[clipName] = windupNeeds.TryGetValue(clipName, out prev) ? Mathf.Max(prev, need) : need;
            var clip = Clip(clipName);
            if (clip == null) { GKBridge.Log("missing attack clip " + clipName); return null; }
            string path = AttackDir + "/" + asset + ".asset";
            var a = AssetDatabase.LoadAssetAtPath<AttackData>(path);
            if (a == null) { a = ScriptableObject.CreateInstance<AttackData>(); AssetDatabase.CreateAsset(a, path); }
            a.kind = s.kind; a.state = clipName; a.clip = clip; a.speed = s.speed; a.fadeIn = s.fade;
            a.damage = s.damage; a.poiseDamage = s.poise; a.staminaCost = s.stamina;
            a.reaction = s.reaction; a.knockback = s.knockback; a.hyperArmor = s.hyper;
            a.guardBreak = s.guardBreak; a.unblockable = s.unblockable; a.chargeable = s.chargeable;
            a.maxChargeTime = 1.1f; a.chargeDamageMultiplier = 1.8f;
            a.hitStop = s.hitStop; a.shake = s.shake; a.warpMaxDistance = s.warp; a.maxWarpSpeed = 9f;
            a.offHand = s.offHand;
            a.hand = WeaponHand.Main;
            a.nextLight = null; a.nextHeavy = null; a.nextLightVia = null; a.viaEnd = 0f;
            Analyse(a);
            Register(clipName, clip, s.speed);
            EditorUtility.SetDirty(a);
            return a;
        }

        /// <summary>
        /// Samples the clip on the real knight and finds the swing from the blade-tip speed curve:
        /// hit window, recovery, combo window and reach (used for motion warping).
        /// </summary>
        static void Analyse(AttackData a)
        {
            if (probe == null || probeTip == null) return;
            const int N = 120;
            var clip = a.clip;
            var tips = new Vector3[N + 1];
            Transform root = probe.transform;
            for (int i = 0; i <= N; i++)
            {
                clip.SampleAnimation(probe, clip.length * i / N);
                tips[i] = root.InverseTransformPoint(probeTip.position);
            }
            var sp = new float[N];
            for (int i = 0; i < N; i++) sp[i] = (tips[i + 1] - tips[i]).magnitude * N / clip.length;
            var sm = new float[N];
            for (int i = 0; i < N; i++) sm[i] = (sp[Mathf.Max(0, i - 1)] + sp[i] * 2f + sp[Mathf.Min(N - 1, i + 1)]) / 4f;
            // the strike is the fast part of the swing that happens IN FRONT of the fighter, at body height. The plain
            // speed peak sometimes found the wind-up (the blade whipping back over the shoulder), so the hit window
            // opened while the edge was a metre from the target and closed before the real cut (Power Slash: window
            // 45-51 %, actual cut at 62 %): weight the speed by how far forward and how well placed the tip is
            var sw = new float[N];
            for (int i = 0; i < N; i++)
            {
                Vector3 t = (tips[i] + tips[i + 1]) * 0.5f;
                float front = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.05f, 0.55f, t.z));
                float height = t.y > 0.15f && t.y < 2.1f ? 1f : 0.25f;
                sw[i] = sm[i] * (0.15f + 0.85f * front) * height;
            }
            int lo = Mathf.RoundToInt(N * 0.08f), hi = Mathf.RoundToInt(N * 0.85f);
            int peak = lo;
            for (int i = lo; i < hi; i++) if (sw[i] > sw[peak]) peak = i;
            a.cutDir = (tips[peak + 1] - tips[peak]).normalized;
            float thr = sw[peak] * 0.42f;
            int s0 = peak, s1 = peak;
            while (s0 > 1 && sw[s0 - 1] > thr) s0--;
            while (s1 < N - 2 && sw[s1 + 1] > thr) s1++;
            float hs = s0 / (float)N, he = (s1 + 1) / (float)N;
            if (he - hs < 0.06f) { he = Mathf.Min(0.95f, hs + 0.06f); }
            if (he - hs > 0.32f) { float c = peak / (float)N; hs = Mathf.Max(hs, c - 0.14f); he = Mathf.Min(he, c + 0.16f); }
            // recovery: blade settles below 15% of the peak speed
            int r = s1 + 1;
            int quiet = 0;
            while (r < N && quiet < 5) { quiet = sm[r] < Mathf.Max(sm[peak], 1e-3f) * 0.15f ? quiet + 1 : 0; r++; }
            float settle = r / (float)N;
            float reach = 0f;
            for (int i = s0; i <= s1 + 1 && i <= N; i++) reach = Mathf.Max(reach, new Vector2(tips[i].x, tips[i].z).magnitude);

            a.hitStart = Mathf.Clamp(hs, 0.05f, 0.85f);
            a.hitEnd = Mathf.Clamp(he, a.hitStart + 0.05f, 0.92f);
            bool light = a.kind == AttackKind.Light || a.kind == AttackKind.Riposte;
            a.comboWindow = Mathf.Clamp(a.hitEnd + (light ? 0.02f : 0.06f), 0f, 0.95f);
            a.dodgeCancel = Mathf.Clamp(a.hitEnd + 0.06f, 0f, 0.95f);
            a.end = Mathf.Clamp(Mathf.Max(settle, a.hitEnd + 0.18f), a.hitEnd + 0.1f, 0.96f);
            a.steerUntil = Mathf.Clamp(a.hitStart * 0.75f, 0.05f, 0.5f);
            // wind-up compression for the player (expert target: lights land 120-200 ms after the press)
            float windupSec = a.hitStart * a.clip.length / Mathf.Max(0.1f, a.speed);
            float target = TargetWindup(a);
            a.windupSpeed = target > 0f ? Mathf.Clamp(windupSec / target, 1f, 2.2f) : 1f;
            a.idealRange = Mathf.Clamp(reach - 0.35f, 0.9f, 1.9f);
            // windows measured on the real hero against a dummy (attackcal) win over the analysis
            // every fast part in front of the fighter: a combo clip cuts more than once
            var runs = new List<Vector2Int>();
            for (int i = lo; i < hi; i++)
            {
                if (sw[i] <= thr) continue;
                int j = i; while (j + 1 < N && sw[j + 1] > thr) j++;
                runs.Add(new Vector2Int(i, j)); i = j;
            }
            if (runs.Count == 0) runs.Add(new Vector2Int(s0, s1));
            float cs, ce, ideal;
            if (Calibrated(a.name, out cs, out ce, out ideal))
            {
                GKClothQa.ApplyWindow(a, cs, ce); if (ideal > 0f) a.idealRange = ideal;
            }
            // the window never opens inside the wind-up: at most 5 % before the clip's first strike (the calibration
            // measured the low sweep from 11 % and the shield bash from 5 %, contacts made while winding up at close range)
            float firstStrike = runs[0].x / (float)N;
            if ((a.kind != AttackKind.Kick || a.shieldBash) && a.hitStart < firstStrike - 0.05f)
                GKClothQa.ApplyWindow(a, firstStrike - 0.05f, Mathf.Max(a.hitEnd, firstStrike + 0.03f));
            // one clip, several cuts: a window per strike, and the whole window to the last one (a calibration that found
            // only the first contact cut it to 21-42 % and the next swing started inside the third cut: a 66 deg arm pop)
            if (a.name.Contains("Combo") && runs.Count > 1)
            {
                a.cuts = runs.Select(rn => new Vector2(Mathf.Max(0f, rn.x / (float)N - 0.02f), Mathf.Min(1f, (rn.y + 1) / (float)N + 0.02f))).ToArray();
                GKClothQa.ApplyWindow(a, Mathf.Min(a.hitStart, a.cuts[0].x), Mathf.Max(a.hitEnd, a.cuts[a.cuts.Length - 1].y));
            }
            else a.cuts = null;
            // the follow-through: the next swing may begin once the blade that landed (around the hit window) has
            // slowed to a third of its speed, so a quick second press finishes this cut instead of swallowing it
            // measured from the contact: the cut's speed around the first touch, then the frame it falls to a third
            int w0 = Mathf.Clamp(Mathf.FloorToInt((a.hitStart - 0.05f) * N), 0, N - 1), w1 = Mathf.Clamp(Mathf.CeilToInt((a.hitStart + 0.12f) * N), w0, N - 1);
            float runPeak = 0f;
            for (int i = w0; i <= w1; i++) runPeak = Mathf.Max(runPeak, sm[i]);
            int ft = Mathf.Clamp(Mathf.CeilToInt((a.hitStart + 0.03f) * N), 0, N);
            while (ft < N && sm[ft] > runPeak * 0.33f) ft++;
            float follow = ft / (float)N;
            // a blade still lying on the target after its cut deals nothing more (one blow per swing): the window
            // ends with the cut, so the next swing is not held back until the clip's end
            bool multiCut = a.name.Contains("Combo");   // one clip, several cuts: its window spans them all
            if (!multiCut && a.hitEnd > follow + 0.02f && a.hitEnd - a.hitStart > 0.12f && a.kind != AttackKind.Kick)
                GKClothQa.ApplyWindow(a, a.hitStart, Mathf.Max(a.hitStart + 0.06f, follow));
            if (!multiCut) a.comboWindow = Mathf.Clamp(Mathf.Max(a.comboWindow, follow), a.hitEnd + 0.02f, Mathf.Max(a.hitEnd + 0.02f, a.end - 0.04f));
            EnemyReadRule(a);
            phases.Add(a.name + " " + string.Join(";", runs.Select(rn => (rn.x / (float)N).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "-" + ((rn.y + 1) / (float)N).ToString("F3", System.Globalization.CultureInfo.InvariantCulture))) +
                       " follow " + follow.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }

        public const string PhasesPath = "Assets/_GeorgianKnight/Generated/swing_phases.txt";
        static readonly List<string> phases = new List<string>();
        static readonly List<string> suspect = new List<string>();

        /// <summary>
        /// An enemy's unblockable gives at least UnblockableRead from its tell (the start) to contact: the wind-up alone
        /// slows, the swing keeps its speed and plane (the spin gave 0.43 s, the leap smash 0.69 s); every other enemy blow
        /// (not the kick) at least EnemyRead: the enemies' readability rule, not the hero's tempo (Team Lead; the warrior's
        /// horizontal cut reached a guard 0.43 s after it began). On the final window: the calibration and the first-strike
        /// floor can open it earlier than the analysis did (lease J: E_A4_Swing at 6 %, contact 0.26 s after the start).
        /// </summary>
        static void EnemyReadRule(AttackData a)
        {
            if (!a.name.StartsWith("E_") || a.clip == null) return;
            float windupSec = a.hitStart * a.clip.length / Mathf.Max(0.1f, a.speed);
            float read = a.unblockable ? UnblockableRead : a.kind == AttackKind.Kick ? 0f : EnemyRead;
            a.windupSpeed = 1f;
            if (windupSec >= read) return;
            a.windupSpeed = Mathf.Max(0.5f, windupSec / read);
            if (windupSec / a.windupSpeed < read - 0.01f) GKBridge.Log("combat: " + a.name + " cannot reach " + read + " s by its wind-up alone (" + (windupSec / a.windupSpeed).ToString("F2") + " s)");
        }

        static string Way(Vector3 d)
        {
            if (d == Vector3.zero) return "?";
            if (d.y < -0.6f) return "downward";
            if (d.y > 0.6f) return "upward";
            return Mathf.Abs(d.x) >= Mathf.Abs(d.z) ? (d.x < 0f ? "right-to-left" : "left-to-right") : (d.z > 0f ? "thrust" : "pull");
        }

        /// <summary>The cuts of an attack's clip (normalized start, end), from the last combat build; empty if unknown.</summary>
        public static List<Vector2> StrikeRuns(string attack)
        {
            var runs = new List<Vector2>();
            if (!System.IO.File.Exists(PhasesPath)) return runs;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var line in System.IO.File.ReadAllLines(PhasesPath))
            {
                var p = line.Split(' ');
                if (p.Length < 2 || p[0] != attack) continue;
                foreach (var r in p[1].Split(';'))
                {
                    var se = r.Split('-');
                    float a, b;
                    if (se.Length == 2 && float.TryParse(se[0], System.Globalization.NumberStyles.Float, ci, out a) && float.TryParse(se[1], System.Globalization.NumberStyles.Float, ci, out b))
                        runs.Add(new Vector2(a, b));
                }
                break;
            }
            return runs;
        }

        static bool Calibrated(string name, out float start, out float end, out float ideal)
        {
            start = end = ideal = 0f;
            if (!System.IO.File.Exists(GKClothQa.CalibrationPath)) return false;
            foreach (var line in System.IO.File.ReadAllLines(GKClothQa.CalibrationPath))
            {
                var p = line.Split(' ');
                if (p.Length < 3 || p[0] != name) continue;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                if (p.Length > 3) float.TryParse(p[3], System.Globalization.NumberStyles.Float, ci, out ideal);
                return float.TryParse(p[1], System.Globalization.NumberStyles.Float, ci, out start) &&
                       float.TryParse(p[2], System.Globalization.NumberStyles.Float, ci, out end);
            }
            return false;
        }

        /// <summary>
        /// Mixamo attacks start from a long idle anticipation. Trim each clip so the wind-up before the
        /// blade connects matches what its users need (snappy player lights, readable enemy swings).
        /// </summary>
        static bool ApplyTrims()
        {
            var attacks = AssetDatabase.FindAssets("t:AttackData", new[] { AttackDir })
                .Select(g => AssetDatabase.LoadAssetAtPath<AttackData>(AssetDatabase.GUIDToAssetPath(g))).Where(a => a != null && a.clip != null).ToList();
            bool changed = false;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var grp in attacks.GroupBy(a => a.state))
                {
                    float need;
                    if (!windupNeeds.TryGetValue(grp.Key, out need) || need > 10f) continue;
                    var a0 = grp.First();
                    string path = AssetDatabase.GetAssetPath(a0.clip);
                    var mi = AssetImporter.GetAtPath(path) as ModelImporter;
                    if (mi == null || (!string.IsNullOrEmpty(mi.userData) && mi.userData.StartsWith("trim:"))) continue; // already trimmed once
                    float hitSec = grp.Min(a => a.hitStart) * a0.clip.length;
                    float trimSec = hitSec - need;
                    if (trimSec < 0.08f) { mi.userData = "trim:0"; continue; }
                    float frames = Mathf.Floor(trimSec * a0.clip.frameRate);
                    mi.userData = "trim:" + frames.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    EditorUtility.SetDirty(mi);
                    mi.SaveAndReimport();
                    changed = true;
                    GKBridge.Log("trimmed " + grp.Key + " by " + frames + " frames (" + trimSec.ToString("F2") + "s)");
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }
            if (changed) AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            return changed;
        }

        // ------------------------------------------------------------------ move sets

        static MoveSet MoveSetAsset(string name)
        {
            string path = GKPaths.Data + "/" + name + ".asset";
            var m = AssetDatabase.LoadAssetAtPath<MoveSet>(path);
            if (m == null) { m = ScriptableObject.CreateInstance<MoveSet>(); AssetDatabase.CreateAsset(m, path); }
            return m;
        }

        static void Link(AttackData a, AttackData light, AttackData heavy)
        {
            if (a == null) return;
            a.nextLight = light; a.nextHeavy = heavy;
            EditorUtility.SetDirty(a);
        }

        /// <summary>Jump clips come from Mixamo with the body's rise baked into the pose. For a physical jump the rise must
        /// be root motion instead: root height based on the feet (stays on the ground through the crouch, rises with the
        /// feet in the air), not baked, so the capsule is lifted by exactly the animated height (no double jump height).</summary>
        static void PrepareJumpClips(params string[] names)
        {
            foreach (var n in names)
            {
                var clip = Clip(n);
                if (clip == null) continue;
                string path = AssetDatabase.GetAssetPath(clip);
                var imp = AssetImporter.GetAtPath(path) as ModelImporter;
                if (imp == null) continue;
                var clips = imp.clipAnimations;
                if (clips == null || clips.Length == 0) clips = imp.defaultClipAnimations;
                bool dirty = false;
                foreach (var c in clips)
                {
                    if (c.lockRootHeightY || c.keepOriginalPositionY || !c.heightFromFeet)
                    {
                        c.lockRootHeightY = false; c.keepOriginalPositionY = false; c.heightFromFeet = true; dirty = true;
                    }
                }
                if (!dirty) continue;
                imp.clipAnimations = clips;
                imp.SaveAndReimport();
                GKBridge.Log("combat: " + n + " root height now from the feet (jump)");
            }
        }

        static void BuildPlayerMoves()
        {
            // Light chain — alternating cuts, ends with a spinning finisher (hyper armour)
            var l1 = Atk("P_L1_Outward", "GK_Slash_Outward", new Spec { speed = 1.25f, damage = 20f, poise = 16f, stamina = 9f });
            var l2 = Atk("P_L2_Inward", "GK_Slash_Inward", new Spec { speed = 1.25f, damage = 20f, poise = 16f, stamina = 9f });
            var l3 = Atk("P_L3_Overhead", "SS_Downward_Slash", new Spec { speed = 1.2f, damage = 24f, poise = 22f, stamina = 10f, knockback = 1.3f, offHand = 0.85f });
            var l4 = Atk("P_L4_Backhand", "GK_Torch_Atk02", new Spec { speed = 1.2f, damage = 24f, poise = 24f, stamina = 10f });
            var l5 = Atk("P_L5_SpinFinisher", "GK_Torch_Atk03", new Spec { speed = 1.1f, damage = 36f, poise = 45f, stamina = 14f, reaction = HitReaction.Heavy, knockback = 2.4f, hyper = true, hitStop = 0.09f, shake = 0.45f });
            // Heavy branches
            var h1 = Atk("P_H1_PowerSlash", "SS_Power_Slash", new Spec { kind = AttackKind.Heavy, speed = 1.05f, damage = 40f, poise = 50f, stamina = 22f, reaction = HitReaction.Heavy, knockback = 2.2f, chargeable = true, hyper = true, hitStop = 0.1f, shake = 0.5f, offHand = 0.85f });
            var h2 = Atk("P_H2_LowSweep", "SS_Low_Attack", new Spec { offHand = 0.85f, kind = AttackKind.Heavy, speed = 1.1f, damage = 30f, poise = 55f, stamina = 18f, reaction = HitReaction.Knockdown, knockback = 1.6f, hyper = true, hitStop = 0.08f, shake = 0.4f });
            var h3 = Atk("P_H3_Pommel", "SS_Hilt_Melee", new Spec { kind = AttackKind.Heavy, speed = 1.15f, damage = 14f, poise = 60f, stamina = 14f, reaction = HitReaction.Stagger, guardBreak = true, knockback = 1.8f, hitStop = 0.08f, shake = 0.35f, offHand = 0.85f });
            var h4 = Atk("P_H4_CrossSlash", "SS_Cross_Slash", new Spec { kind = AttackKind.Heavy, speed = 1.1f, damage = 34f, poise = 40f, stamina = 18f, reaction = HitReaction.Heavy, knockback = 2f, hyper = true, hitStop = 0.09f, shake = 0.4f, offHand = 0.85f });
            var sprint = Atk("P_Sprint_Leap", "SS_Jump_Attack", new Spec { kind = AttackKind.Sprint, speed = 1.1f, damage = 32f, poise = 45f, stamina = 18f, reaction = HitReaction.Heavy, knockback = 2.2f, hyper = true, warp = 4f, hitStop = 0.09f, shake = 0.45f, offHand = 0.85f });
            var riposte = Atk("P_Riposte_Thrust", "GK_Torch_Stab", new Spec { kind = AttackKind.Riposte, speed = 1.2f, damage = 55f, poise = 60f, stamina = 6f, reaction = HitReaction.Heavy, knockback = 2.5f, hitStop = 0.12f, shake = 0.5f });
            var kick = Atk("P_Kick", "SS_Sparta_Kick", new Spec { kind = AttackKind.Kick, speed = 1.15f, damage = 8f, poise = 40f, stamina = 12f, reaction = HitReaction.Stagger, guardBreak = true, knockback = 3.2f, hitStop = 0.07f, shake = 0.35f, offHand = 0.85f });
            var exec = Atk("P_Execution", "GK_Exec_Winner", new Spec { kind = AttackKind.Execution, speed = 1f, damage = 999f, poise = 999f, stamina = 0f, reaction = HitReaction.Knockdown, hitStop = 0.12f, shake = 0.6f });
            if (exec != null) { exec.idealRange = 1.15f; EditorUtility.SetDirty(exec); }

            Link(l1, l2, h1); Link(l2, l3, h2); Link(l3, l4, h3); Link(l4, l5, h4); Link(l5, l1, h1);
            Link(h1, l2, h2); Link(h2, l4, h1); Link(h3, l4, h4); Link(h4, l1, h2);
            Link(sprint, l2, h3); Link(riposte, l1, h1); Link(kick, l1, h1);

            var m = MoveSetAsset("Moves_Knight");
            m.lightStart = l1; m.heavyStart = h1; m.sprintAttack = sprint; m.riposte = riposte; m.kick = kick; m.execution = exec;
            m.aiAttacks = new AttackData[0]; m.aiUnblockables = new AttackData[0];
            FillDefense(m, true);
            m.warCry = Act("SS_Powering_Up", 1f, 0.15f, 0.9f, 0.85f, false);
            // jumps: the clips' vertical motion is extracted from the feet (PrepareJumpClips) and moves the capsule
            PrepareJumpClips("SS_Jump_From_Idle", "SS_Running_Jump");
            m.jump = Act("SS_Jump_From_Idle", 1.05f, 0.08f, 0.94f, 0.82f, true, 0, 0, 0f);
            m.jumpRun = Act("SS_Running_Jump", 1f, 0.06f, 0.94f, 0.8f, true, 0, 0, 0f);
            m.drawSword = Act("SS_draw_sword_1", 1.1f, 0.15f, 0.9f, 0.8f, false, 0, 0, 0.6f);
            m.sheathSword = Act("SS_sheath_sword_1", 1f, 0.15f, 0.92f, 0.85f, false, 0, 0, 0.6f);
            m.victory = Act("SS_Sword_Play_Idle", 1f, 0.2f, 0.98f, 0.98f, false);
            AnalyseHolster(m);
            EditorUtility.SetDirty(m);
            PlayerMoves = m;
            BuildCleanPlayerMoves(m);
            BuildStyleMoves();
        }

        public static MoveSet CleanPlayerMoves
        {
            get { return AssetDatabase.LoadAssetAtPath<MoveSet>(GKPaths.Data + "/Moves_KnightClean.asset"); }
        }

        /// <summary>
        /// The clean knight's attacks: only the Mixamo Sword and Shield pack, a one-handed weapon like ours. The main
        /// set mixes in two-handed and torch swings, which the pose fixes then have to bend into a one-handed cut.
        /// Defence, jumps, draw and sheathe are copied from <paramref name="main"/>.
        /// </summary>
        static void BuildCleanPlayerMoves(MoveSet main)
        {
            var l1 = Atk("C_L1_High", "SS_High_Attack", new Spec { speed = 1.2f, damage = 20f, poise = 16f, stamina = 9f });
            var l2 = Atk("C_L2_Downward", "SS_Downward_Slash", new Spec { speed = 1.2f, damage = 22f, poise = 20f, stamina = 10f, knockback = 1.2f });
            var l3Spec = new Spec { speed = 1.1f, damage = 34f, poise = 40f, stamina = 14f, reaction = HitReaction.Heavy, knockback = 2.2f, hyper = true, hitStop = 0.09f, shake = 0.45f };
            var l3 = Atk("C_L3_Combo", "SS_Slash_Combo", l3Spec);
            // option (d), the f548 jerk: L2 into L3 through the authored transition (chainvia build), the same blow after
            // it; reached from L2 only while AttackData.UseVia is on (the old chain stays live until the Director's A/B)
            var l3v = GKPreview.Clip(GKClipChainVia.DefaultName) != null ? Atk("C_L3_Combo_Via", GKClipChainVia.DefaultName, l3Spec) : null;
            var h1 = Atk("C_H1_PowerSlash", "SS_Power_Slash", new Spec { kind = AttackKind.Heavy, speed = 1.05f, damage = 40f, poise = 50f, stamina = 22f, reaction = HitReaction.Heavy, knockback = 2.2f, chargeable = true, hyper = true, hitStop = 0.1f, shake = 0.5f });
            var h2 = Atk("C_H2_CrossSlash", "SS_Cross_Slash", new Spec { kind = AttackKind.Heavy, speed = 1.1f, damage = 34f, poise = 40f, stamina = 18f, reaction = HitReaction.Heavy, knockback = 2f, hyper = true, hitStop = 0.09f, shake = 0.4f });
            var h3 = Atk("C_H3_LowSweep", "SS_Low_Attack", new Spec { kind = AttackKind.Heavy, speed = 1.1f, damage = 30f, poise = 55f, stamina = 18f, reaction = HitReaction.Knockdown, knockback = 1.6f, hyper = true, hitStop = 0.08f, shake = 0.4f });
            // the leap (SS_Jump_Attack) and the pommel strike never reached a standing target in attackcal: a running
            // power slash and an overhead chop do
            var sprint = Atk("C_Sprint_Slash", "SS_Power_Slash", new Spec { kind = AttackKind.Sprint, speed = 1.15f, damage = 32f, poise = 45f, stamina = 18f, reaction = HitReaction.Heavy, knockback = 2.2f, hyper = true, warp = 4f, hitStop = 0.09f, shake = 0.45f });
            // the shield set's "kick" is a bash with the shield's face (Kevin Iglesias, Human Melee FREE): the shield is
            // the weapon that opens a guard; the boot stays for the other styles
            var kick = Atk("C_ShieldBash", "KI_AttackShield", new Spec { kind = AttackKind.Kick, speed = 1f, damage = 8f, poise = 45f, stamina = 12f, reaction = HitReaction.Stagger, guardBreak = true, knockback = 2.6f, hitStop = 0.08f, shake = 0.4f })
                    ?? Atk("C_Kick", "SS_Sparta_Kick", new Spec { kind = AttackKind.Kick, speed = 1.15f, damage = 8f, poise = 40f, stamina = 12f, reaction = HitReaction.Stagger, guardBreak = true, knockback = 3.2f, hitStop = 0.07f, shake = 0.35f });
            if (kick != null) { kick.shieldBash = kick.state == "KI_AttackShield"; EditorUtility.SetDirty(kick); }
            var riposte = Atk("C_Riposte_Chop", "SS_Downward_Slash", new Spec { kind = AttackKind.Riposte, speed = 1f, damage = 55f, poise = 60f, stamina = 6f, reaction = HitReaction.Heavy, knockback = 2.5f, hitStop = 0.12f, shake = 0.5f });
            if (l1 == null || l2 == null || l3 == null || h1 == null || h2 == null || h3 == null) { GKBridge.Log("clean moves: an SS attack clip is missing"); return; }

            Link(l1, l2, h1); Link(l2, l3, h2); Link(l3, l1, h3);
            Link(h1, l2, h2); Link(h2, l3, h3); Link(h3, l1, h1);
            Link(sprint, l2, h2); Link(riposte, l1, h1); Link(kick, l1, h1);
            // (c3), the f548 root: L2 with its wind-up re-keyed off the head and its follow-through out of the shield into
            // L3's first frame (chainvia ft, GK_SS_Downward_Slash_FT), reached from L1 while AttackData.UseVia is on (the old
            // chain stays live until the Director's A/B). Its window is L2's calibrated one: everything to contact is the same
            var l2ft = GKPreview.Clip(GKClipChainVia.FtName) != null && l2 != null ? Atk("C_L2_Downward_FT", GKClipChainVia.FtName, new Spec { speed = 1.2f, damage = 22f, poise = 20f, stamina = 10f, knockback = 1.2f }) : null;
            if (l2ft != null)
            {
                l2ft.hitStart = l2.hitStart; l2ft.hitEnd = l2.hitEnd; l2ft.comboWindow = l2.comboWindow; l2ft.dodgeCancel = l2.dodgeCancel;
                l2ft.steerUntil = l2.steerUntil; l2ft.idealRange = l2.idealRange; l2ft.windupSpeed = l2.windupSpeed; l2ft.cutDir = l2.cutDir;
                Link(l2ft, l3, h2);
                l1.nextLightVia = l2ft;
                EditorUtility.SetDirty(l2ft); EditorUtility.SetDirty(l1);
            }
            if (l3v != null)
            {
                Link(l3v, l1, h3);
                l3v.viaEnd = GKClipChainVia.ViaEnd(GKClipChainVia.DefaultName);
                // (d) retired (lease L): the transition started inside the shield; (c3) replaces it, so L2 no longer leads into it
                EditorUtility.SetDirty(l2);
            }

            // the whole swing plays: the analysis ended the move about 0.1 after the contact, which cut the
            // follow-through off mid-arc; and the wind-up keeps its weight (at 2.2x it read as a jerk).
            // Chained strikes stand in different stances: a 0.08 s crossfade moved the hips 6-14 cm a frame (popqa)
            foreach (var a in new[] { l1, l2, l2ft, l3, l3v, h1, h2, h3, sprint, riposte, kick })
            {
                if (a == null) continue;
                a.end = Mathf.Max(a.end, 0.9f);
                a.windupSpeed = Mathf.Min(a.windupSpeed, 1.4f);
                a.fadeIn = Mathf.Max(a.fadeIn, 0.18f);
                EditorUtility.SetDirty(a);
            }
            // the opener is also where the chain restarts from the finisher's deep lunge, and the sprint strike starts
            // from a full run: both still moved the hips ~7 cm a frame at 0.18 s
            if (l1 != null) l1.fadeIn = 0.24f;
            if (sprint != null) sprint.fadeIn = 0.25f;
            // the bash starts from Kevin Iglesias' own stance, a hand lower and squarer than the sword-and-shield one:
            // at 0.18 s the hips jumped 6 cm in one frame (popqa)
            if (kick != null && kick.shieldBash) kick.fadeIn = 0.3f;

            // the finisher: one weighty overhead blow on the staggered victim, who holds the stagger until it lands.
            // The paired Mixamo execution (GK_Exec_Winner / Loser) ran ~5.5 s out of sync, the victim swinging back
            // mid-"execution". The kill lands where the blade really passes through (attackcal: SS_Downward_Slash 52-57%)
            var exec = Atk("C_Execution", "SS_Downward_Slash", new Spec { kind = AttackKind.Execution, speed = 0.85f, damage = 999f, poise = 999f, stamina = 0f, reaction = HitReaction.Knockdown, hitStop = 0.15f, shake = 0.6f });
            if (exec != null)
            {
                exec.pairedVictim = false;
                exec.hitStart = 0.53f; exec.hitEnd = 0.58f;
                // no chain follows an execution, but the window must still open after the blow (QA DATA.ATTACK_TIMING:
                // the analysed 0.535 sat inside the hand-set hit window)
                exec.comboWindow = 0.62f;
                exec.end = 0.92f; exec.windupSpeed = 1f; exec.fadeIn = 0.18f;
                exec.idealRange = 1.3f;
                EditorUtility.SetDirty(exec);
            }

            var m = MoveSetAsset("Moves_KnightClean");
            EditorUtility.CopySerialized(main, m);
            m.name = "Moves_KnightClean";
            m.lightStart = l1; m.heavyStart = h1; m.sprintAttack = sprint; m.riposte = riposte;
            // a foe on the ground takes the downward cut, as in the sword-alone style: the power slash (the fallback when
            // this was empty) passed 24 cm over a knocked-down foe at 1.3 m (smoke chainqa 10-31)
            if (l2 != null) m.groundBlow = l2;
            if (kick != null) m.kick = kick;
            if (exec != null) m.execution = exec;
            // no off-hand override: the OffHand layer holds the left arm in an unarmed idle, made for the two-handed
            // clips; on these clips it fought the arm the clip animates
            var so = new SerializedObject(m);
            var it = so.GetIterator();
            while (it.Next(true))
                if (it.name == "offHand" && it.propertyType == SerializedPropertyType.Float) it.floatValue = 0f;
            so.ApplyModifiedPropertiesWithoutUndo();
            // the get-up starts from the trimmed clip's first frame, not quite the knockdown's last pose: 0.1 s moved
            // the hips 6 cm a frame
            if (m.getUp != null) m.getUp.fadeIn = Mathf.Max(m.getUp.fadeIn, 0.25f);
            EditorUtility.SetDirty(m);
            GKBridge.Log("clean moves: light SS_High_Attack > SS_Downward_Slash > SS_Slash_Combo, heavy SS_Power_Slash / SS_Cross_Slash / SS_Low_Attack");
        }

        /// <summary>
        /// The other two fighting styles, built on the sword-and-shield set (defence, jumps, draw, sheathe, kick and
        /// finisher copied from it):
        /// Sword alone: quicker cuts with the free left arm (OffHand layer, the unarmed arm swinging with the gait), a
        /// low thrust as its heavy opener and riposte, and a high sword guard instead of the shield.
        /// Two swords: the cuts alternate right and left (the left hand's are the same Mixamo cuts mirrored, cutting with
        /// the second blade), the idle left arm holds its sword in guard (mirrored sword stance), crossed-blade guard.
        /// </summary>
        static void BuildStyleMoves()
        {
            var clean = CleanPlayerMoves;
            if (clean == null) return;
            // the shield set itself
            clean.style = WeaponStyle.SwordShield;
            clean.blockIdleState = "BlockIdle"; clean.offHandState = "OffHandShield"; clean.offHandFree = 1f; clean.offHandBlock = 0f;
            clean.blockHit.state = "BlockHit";
            EditorUtility.SetDirty(clean);

            // ---- sword alone
            // the attacks play their own left arm (the counter-balance swing of the capture); the unarmed off-hand pose
            // used to hang the free arm limp beside every cut
            const float Free = 0f;
            // (Kevin Iglesias' cut snapped the upper arm 63 degrees in one frame at its 18th frame: Mixamo's stable outward
            // slash opens the chain instead, no step above 16 degrees per frame)
            var s0 = Atk("S_L0_Cut", Clip("MX_Stable_Sword_Outward_Slash") != null ? "MX_Stable_Sword_Outward_Slash" : "KI_Attack1H_R", new Spec { speed = 1.35f, damage = 18f, poise = 14f, stamina = 7f, offHand = Free });
            var s1 = Atk("S_L1_High", "SS_High_Attack", new Spec { speed = 1.3f, damage = 20f, poise = 16f, stamina = 8f, offHand = Free });
            var s2 = Atk("S_L2_Downward", "SS_Downward_Slash", new Spec { speed = 1.3f, damage = 22f, poise = 18f, stamina = 9f, knockback = 1.2f, offHand = Free });
            var s3 = Atk("S_L3_Combo", "SS_Slash_Combo", new Spec { speed = 1.2f, damage = 32f, poise = 36f, stamina = 13f, reaction = HitReaction.Heavy, knockback = 2f, hyper = true, hitStop = 0.09f, shake = 0.4f, offHand = Free });
            // (the torch stab held the sword low at the hip and pushed the free arm forward as if holding a torch:
            // Mixamo's one-handed thrust-and-slash and upward thrust take the heavy and the riposte)
            var sh1 = Atk("S_H1_Thrust", Clip("MX_Thrust_Slash") != null ? "MX_Thrust_Slash" : "GK_Torch_Stab", new Spec { kind = AttackKind.Heavy, speed = 1.35f, damage = 42f, poise = 40f, stamina = 18f, reaction = HitReaction.Heavy, knockback = 1.8f, hitStop = 0.1f, shake = 0.45f, offHand = Free });
            // (the leaping power slash threw the head back and the knee up in place: it stays a running leap only)
            var sh2 = Atk("S_H2_CrossSlash", "SS_Cross_Slash", new Spec { kind = AttackKind.Heavy, speed = 1.1f, damage = 38f, poise = 46f, stamina = 19f, reaction = HitReaction.Heavy, knockback = 2.1f, chargeable = true, hyper = true, hitStop = 0.1f, shake = 0.5f, offHand = Free });
            var sh3 = Atk("S_H3_LowSweep", "SS_Low_Attack", new Spec { kind = AttackKind.Heavy, speed = 1.15f, damage = 28f, poise = 55f, stamina = 17f, reaction = HitReaction.Knockdown, knockback = 1.6f, hyper = true, hitStop = 0.08f, shake = 0.4f, offHand = Free });
            var sSprint = Atk("S_Sprint_Slash", "SS_Power_Slash", new Spec { kind = AttackKind.Sprint, speed = 1.2f, damage = 30f, poise = 42f, stamina = 17f, reaction = HitReaction.Heavy, knockback = 2.2f, hyper = true, warp = 4f, hitStop = 0.09f, shake = 0.45f, offHand = Free });
            // (the upward thrust passed over a standing target at every range: the inward slash answers a parry instead,
            // the mirror of the opening outward cut)
            var sRip = Atk("S_Riposte_Thrust", Clip("MX_Stable_Sword_Inward_Slash") != null ? "MX_Stable_Sword_Inward_Slash" : "GK_Torch_Stab", new Spec { kind = AttackKind.Riposte, speed = 1.3f, damage = 60f, poise = 60f, stamina = 6f, reaction = HitReaction.Heavy, knockback = 2.5f, hitStop = 0.12f, shake = 0.5f, offHand = Free });
            // the heavy opens with the cross slash: Mixamo's Thrust Slash steps back and turns the body a quarter away
            // as it strikes (it never reached a target at any range); it stays built but out of the chain
            Link(s0, s1, sh2); Link(s1, s2, sh2); Link(s2, s3, sh2); Link(s3, s0 ?? s1, sh3);
            Link(sh1, s2, sh2); Link(sh2, s3, sh3); Link(sh3, s1, sh2);
            Link(sSprint, s2, sh2); Link(sRip, s1, sh2);
            var sm = StyleSet("Moves_Style_Sword", clean, WeaponStyle.Sword, s0 ?? s1, sh2, sSprint, sRip,
                              "BlockIdle_Sword", "BlockHit_Sword", "AX_standing_block_react_large", "OffHandSword", 1f, 1f);
            // its heavy opener (the cross slash) passed 22-59 cm over a foe on the ground (swingqa foe=down): the
            // downward cut reaches him
            if (sm != null && s2 != null) { sm.groundBlow = s2; EditorUtility.SetDirty(sm); }

            // ---- two swords
            const float Guard = 1f;   // the idle left arm (mirrored sword stance) while the right hand cuts
            // Kevin Iglesias' one-handed cut (Human Melee FREE), right then the same cut mirrored for the left hand.
            // His own left-hand attack turns the wrist his way: with the second sword seated as the mirror of the
            // first, it came out blade-back like a dagger. Mirrored cuts hold both swords the same, blade forward
            var k1 = Atk("D_L0_Cut_R", "KI_Attack1H_R", new Spec { speed = 1.1f, damage = 14f, poise = 10f, stamina = 6f, offHand = Guard });
            var k2 = AtkL("D_L0_Cut_L", "KI_Attack1H_R", new Spec { speed = 1.1f, damage = 14f, poise = 10f, stamina = 6f });
            var d1 = Atk("D_L1_High_R", "SS_High_Attack", new Spec { speed = 1.35f, damage = 15f, poise = 12f, stamina = 7f, offHand = Guard });
            var d2 = AtkL("D_L2_High_L", "SS_High_Attack", new Spec { speed = 1.35f, damage = 15f, poise = 12f, stamina = 7f });
            var d3 = Atk("D_L3_Down_R", "SS_Downward_Slash", new Spec { speed = 1.35f, damage = 17f, poise = 15f, stamina = 8f, offHand = Guard });
            var d4 = AtkL("D_L4_Down_L", "SS_Downward_Slash", new Spec { speed = 1.35f, damage = 17f, poise = 15f, stamina = 8f });
            var d5 = Atk("D_L5_Combo_R", "SS_Slash_Combo", new Spec { speed = 1.25f, damage = 28f, poise = 34f, stamina = 12f, reaction = HitReaction.Heavy, knockback = 2f, hyper = true, hitStop = 0.09f, shake = 0.4f, offHand = Guard });
            var dh1 = AtkL("D_H1_Combo_L", "SS_Slash_Combo", new Spec { kind = AttackKind.Heavy, speed = 1.15f, damage = 34f, poise = 44f, stamina = 18f, reaction = HitReaction.Heavy, knockback = 2.1f, chargeable = true, hyper = true, hitStop = 0.1f, shake = 0.5f });
            var dh2 = Atk("D_H2_Cross_R", "SS_Cross_Slash", new Spec { kind = AttackKind.Heavy, speed = 1.15f, damage = 32f, poise = 40f, stamina = 17f, reaction = HitReaction.Heavy, knockback = 2f, hyper = true, hitStop = 0.09f, shake = 0.4f, offHand = Guard });
            var dh3 = AtkL("D_H3_Low_L", "SS_Low_Attack", new Spec { kind = AttackKind.Heavy, speed = 1.2f, damage = 26f, poise = 52f, stamina = 16f, reaction = HitReaction.Knockdown, knockback = 1.6f, hyper = true, hitStop = 0.08f, shake = 0.4f });
            var dSprint = Atk("D_Sprint_Slash", "SS_Power_Slash", new Spec { kind = AttackKind.Sprint, speed = 1.2f, damage = 28f, poise = 40f, stamina = 16f, reaction = HitReaction.Heavy, knockback = 2.1f, hyper = true, warp = 4f, hitStop = 0.09f, shake = 0.45f, offHand = Guard });
            var dRip = AtkL("D_Riposte_L", "SS_Downward_Slash", new Spec { kind = AttackKind.Riposte, speed = 1f, damage = 55f, poise = 60f, stamina = 6f, reaction = HitReaction.Heavy, knockback = 2.5f, hitStop = 0.12f, shake = 0.5f });
            // (the mirrored Kevin cut never reached a standing target in attackcal: the right cut hands on to the
            // mirrored high cut, which does)
            Link(k1, d2, dh1); Link(k2, d1, dh2);
            Link(d1, d2, dh1); Link(d2, d3, dh2); Link(d3, d4, dh3); Link(d4, d5, dh2); Link(d5, k1 ?? d1, dh1);
            Link(dh1, d3, dh2); Link(dh2, d4, dh3); Link(dh3, d1, dh1);
            Link(dSprint, d2, dh2); Link(dRip, d1, dh1);
            var dm = StyleSet("Moves_Style_Dual", clean, WeaponStyle.DualSwords, k1 ?? d1, dh1, dSprint, dRip,
                              "BlockIdle_Dual", "BlockHit_Dual", Clip("XR_Dual_Block_Hit") != null ? "XR_Dual_Block_Hit" : Clip("MX_Center_Block_v2") != null ? "MX_Center_Block_v2" : "AX_standing_block_react_large", "OffHandDual", 1f, 0f);

            // the whole swing plays and a chained cut never jerks the hips (as for the shield set)
            foreach (var a in new[] { s0, s1, s2, s3, sh1, sh2, sh3, sSprint, sRip, k1, k2, d1, d2, d3, d4, d5, dh1, dh2, dh3, dSprint, dRip })
            {
                if (a == null) continue;
                a.end = Mathf.Max(a.end, 0.88f);
                a.windupSpeed = Mathf.Min(a.windupSpeed, 1.5f);
                a.fadeIn = Mathf.Max(a.fadeIn, 0.16f);
                EditorUtility.SetDirty(a);
            }
            // the thrust-and-slash starts in a long lunge stance (hips 27 cm ahead of the idle's): a longer blend in
            // carries the hips forward as a step instead of a 12 cm-per-frame slide
            if (sh1 != null) { sh1.fadeIn = Mathf.Max(sh1.fadeIn, 0.32f); EditorUtility.SetDirty(sh1); }
            // without a shield on the arm the knight kicks again
            var boot = Atk("X_Kick", "SS_Sparta_Kick", new Spec { kind = AttackKind.Kick, speed = 1.15f, damage = 8f, poise = 40f, stamina = 12f, reaction = HitReaction.Stagger, guardBreak = true, knockback = 3.2f, hitStop = 0.07f, shake = 0.35f });
            if (boot != null)
            {
                boot.fadeIn = Mathf.Max(boot.fadeIn, 0.18f); boot.end = Mathf.Max(boot.end, 0.9f); EditorUtility.SetDirty(boot);
                Link(boot, sm != null ? sm.lightStart : null, sm != null ? sm.heavyStart : null);
                if (sm != null) { sm.kick = boot; EditorUtility.SetDirty(sm); }
            }
            // the two-sword kick is its own asset: the shared one chained into the one-sword cuts, so after a kick the
            // knight swung a single-sword combo with two blades in his hands
            var bootD = Atk("D_Kick", "SS_Sparta_Kick", new Spec { kind = AttackKind.Kick, speed = 1.15f, damage = 8f, poise = 40f, stamina = 12f, reaction = HitReaction.Stagger, guardBreak = true, knockback = 3.2f, hitStop = 0.07f, shake = 0.35f });
            if (bootD != null)
            {
                bootD.fadeIn = Mathf.Max(bootD.fadeIn, 0.18f); bootD.end = Mathf.Max(bootD.end, 0.9f); EditorUtility.SetDirty(bootD);
                Link(bootD, dm != null ? dm.lightStart : null, dm != null ? dm.heavyStart : null);
                if (dm != null) { dm.kick = bootD; EditorUtility.SetDirty(dm); }
            }
            GKBridge.Log("style moves: sword alone " + (sm != null) + ", two swords " + (dm != null) + ", shield bash " + (clean.kick != null && clean.kick.shieldBash));
        }

        static MoveSet StyleSet(string name, MoveSet from, WeaponStyle style, AttackData light, AttackData heavy, AttackData sprint, AttackData riposte,
                                string blockIdle, string blockHitState, string blockHitClip, string offHandState, float offFree, float offBlock)
        {
            if (light == null || heavy == null) { GKBridge.Log("style moves: " + name + " is missing a clip"); return null; }
            var m = MoveSetAsset(name);
            EditorUtility.CopySerialized(from, m);
            m.name = name;
            m.style = style;
            m.lightStart = light; m.heavyStart = heavy;
            if (sprint != null) m.sprintAttack = sprint;
            if (riposte != null) m.riposte = riposte;
            m.blockIdleState = blockIdle;
            m.blockHit = new ActionClip { state = blockHitState, clip = Clip(blockHitClip) ?? from.blockHit.clip, speed = 1.2f, fadeIn = 0.04f };
            m.offHandState = offHandState;
            m.offHandFree = offFree;
            m.offHandBlock = offBlock;
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>
        /// Finds, on the real knight, the frame where the right hand meets the scabbard throat in the draw clip (grab)
        /// and in the sheathe clip (release), so the sword changes parent exactly when the hand is at the hilt.
        /// </summary>
        static AnimationClip HolsterDrawClip, HolsterSheatheClip;

        /// <summary>
        /// Places the scabbard where the chosen draw / sheathe animations actually put the hand: the sheathed grip goes
        /// to the hand position at the grab / release frames (hips space), and the scabbard axis follows the direction
        /// the hand pulls the blade out. Saved to Generated/holster_fit.json; GKWeapons.Attach uses it.
        /// </summary>
        static void FitScabbardToClips(GameObject inst, Dictionary<string, Transform> map, ActionClip draw, float grab, ActionClip sheathe, float release, float gripAbove)
        {
            if (draw == null || !draw.IsValid || sheathe == null || !sheathe.IsValid || grab < 0f || release < 0f) return;
            var hips = map["Hips"]; var hand = map["RightHand"];
            Transform mid; map.TryGetValue("Right Middle Proximal", out mid);
            System.Func<AnimationClip, float, Vector3> palmAt = (clip, t) =>
            {
                clip.SampleAnimation(inst, Mathf.Clamp(t, 0f, clip.length));
                inst.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                Vector3 palm = mid != null ? Vector3.Lerp(hand.position, mid.position, 0.5f) : hand.position;
                return hips.InverseTransformPoint(palm);
            };
            float tg = grab * draw.clip.length, tr = release * sheathe.clip.length;
            Vector3 g0 = palmAt(draw.clip, tg);
            Vector3 r0 = palmAt(sheathe.clip, tr);
            var fit = new HolsterFit { grip = (g0 + r0) * 0.5f, gripDraw = g0, gripSheathe = r0 };
            System.IO.File.WriteAllText(GKPaths.Generated + "/holster_fit.json", JsonUtility.ToJson(fit, true));
            GKBridge.Log(string.Format("holster fit: draw/sheathe grips {0:F2} m apart; hilt (hips space) {1}", Vector3.Distance(g0, r0) * hips.lossyScale.x, fit.grip));
        }

        [System.Serializable]
        public class HolsterFit { public Vector3 grip, gripDraw, gripSheathe; }

        static void AnalyseHolster(MoveSet m)
        {
            var inst = GKCharacters.InstantiateKnightVisual();
            try
            {
                var map = GKCharacters.MapBones(inst.transform);
                var pivot = inst.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "ScabbardPivot");
                var hand = map["RightHand"];
                Transform mid; map.TryGetValue("Right Middle Proximal", out mid);
                if (pivot == null) { GKBridge.Log("holster: no ScabbardPivot"); return; }
                // the hand closes on the GRIP of the sheathed sword, which sits above the throat along the hilt
                var swordT = inst.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Sword");
                float gripAbove = 0.1f;
                if (swordT != null)
                {
                    Transform gp = swordT.Find("Grip"), sr = swordT.Find("SheathRef");
                    if (gp != null && sr != null) gripAbove = Vector3.Distance(gp.position, sr.position) + 0.012f;
                }
                System.Func<ActionClip, float, float, Vector2> find = (ac, from, to) =>
                {
                    if (ac == null || !ac.IsValid) return new Vector2(-1f, 99f);
                    float bestT = -1f, bestD = 99f;
                    int frames = Mathf.CeilToInt(ac.clip.length * 60f);
                    for (int f = Mathf.FloorToInt(frames * from); f <= frames * to; f++)
                    {
                        float t = ac.clip.length * f / frames;
                        ac.clip.SampleAnimation(inst, t);
                        inst.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                        Vector3 palm = mid != null ? Vector3.Lerp(hand.position, mid.position, 0.5f) : hand.position;
                        Vector3 sheathedGrip = pivot.position - pivot.forward * gripAbove;
                        float d = Vector3.Distance(palm, sheathedGrip);
                        if (d < bestD) { bestD = d; bestT = f / (float)frames; }
                    }
                    return new Vector2(bestT, bestD);
                };
                // evaluate every available draw / sheathe clip and keep the one whose hand really meets our scabbard
                System.Func<string[], float, float, ActionClip, ActionClip> best = (names, from, to, current) =>
                {
                    ActionClip pick = current; float pickD = find(current, from, to).y;
                    foreach (var n in names)
                    {
                        var c = Clip(n);
                        if (c == null) continue;
                        var ac = new ActionClip { state = n, clip = c, speed = 1f };
                        var res = find(ac, from, to);
                        GKBridge.Log(string.Format("holster candidate {0,-28} min hand-grip {1:F2} m at {2:P0}", n, res.y, res.x));
                        if (res.y < pickD - 0.01f) { pickD = res.y; pick = Act(n, 1.1f, 0.15f, 0.9f, 0.8f, false, 0, 0, 0.6f); }
                    }
                    return pick;
                };
                m.drawSword = best(new[] { "SS_draw_sword_1", "SS_draw_sword_2", "GK_Sword_Draw", "AX_unarmed_equip_underarm" }, 0.05f, 0.75f, m.drawSword);
                m.sheathSword = best(new[] { "SS_sheath_sword_1", "SS_sheath_sword_2", "GK_Sword_Sheath", "AX_standing_disarm_underarm" }, 0.25f, 0.98f, m.sheathSword);
                HolsterDrawClip = m.drawSword.clip; HolsterSheatheClip = m.sheathSword.clip;
                var g = find(m.drawSword, 0.05f, 0.75f);
                var r = find(m.sheathSword, 0.25f, 0.98f);
                FitScabbardToClips(inst, map, m.drawSword, g.x, m.sheathSword, r.x, gripAbove);
                if (g.x >= 0f) m.drawGrab = g.x;
                if (r.x >= 0f) m.sheatheRelease = r.x;
                GKBridge.Log(string.Format("holster: draw grab at {0:P0} (hand {1:F2} m from throat), sheathe release at {2:P0} (hand {3:F2} m)", g.x, g.y, r.x, r.y));
                if (g.y > 0.18f || r.y > 0.18f) GKBridge.Log("holster WARNING: the clip's hand never reaches our scabbard throat closely — consider another draw/sheathe clip");
            }
            finally { Object.DestroyImmediate(inst); }
        }

        static void BuildEnemyMoves()
        {
            var e1 = Atk("E_A1_Horizontal", "GK_Slash_Outward", new Spec { speed = 0.95f, damage = 16f, poise = 18f, stamina = 10f });
            var e2 = Atk("E_A2_Downward", "SS_Downward_Slash", new Spec { speed = 0.95f, damage = 20f, poise = 22f, stamina = 12f, knockback = 1.3f, offHand = 0.85f });
            var e3 = Atk("E_A3_Swing", "GK_Torch_Atk01", new Spec { speed = 1f, damage = 15f, poise = 16f, stamina = 9f });
            // GK_Torch_Atk02 retired from the AI too (lease L, Team Lead): its blade reaches the hero only between 1.10 m
            // (touching in the wind-up) and 1.15 m (missing), and missed 4 of 6 in the duel
            var e4 = Atk("E_A4_Swing", "GK_Torch_Atk02", new Spec { speed = 1f, damage = 15f, poise = 16f, stamina = 9f });
            var e5 = Atk("E_A5_Swing", "GK_Torch_Atk03", new Spec { speed = 1f, damage = 15f, poise = 16f, stamina = 9f });
            var e6 = Atk("E_A6_High", "SS_High_Attack", new Spec { speed = 0.95f, damage = 18f, poise = 18f, stamina = 10f, offHand = 0.85f });
            var e7 = Atk("E_A7_Low", "SS_Low_Attack", new Spec { speed = 0.95f, damage = 16f, poise = 20f, stamina = 10f, offHand = 0.85f });
            var e8 = Atk("E_A8_Combo", "SS_Slash_Combo", new Spec { speed = 1f, damage = 18f, poise = 20f, stamina = 14f, offHand = 0.85f });
            var u1 = Atk("E_U1_LeapSmash", "SS_Jump_Attack", new Spec { offHand = 0.85f, kind = AttackKind.Heavy, speed = 0.9f, damage = 34f, poise = 60f, stamina = 20f, unblockable = true, reaction = HitReaction.Knockdown, knockback = 2.4f, hyper = true, warp = 4f, hitStop = 0.1f, shake = 0.5f });
            var u2 = Atk("E_U2_Power", "SS_Power_Slash", new Spec { kind = AttackKind.Heavy, speed = 0.85f, damage = 32f, poise = 55f, stamina = 20f, unblockable = true, reaction = HitReaction.Heavy, knockback = 2.2f, hyper = true, hitStop = 0.1f, shake = 0.45f, offHand = 0.85f });
            // GK_Torch_Atk01 and _Atk03 begin mid-swing: their blade is on the target from 3-6 % of the clip at every
            // range (enemycal), a blow with no wind-up to read. They stay built but out of the AI's hands; Atk02, which
            // winds up, opens on its own
            Link(e1, e6, null); Link(e6, e2, null); Link(e4, null, null); Link(e7, e1, null);

            var m = MoveSetAsset("Moves_Seljuk");
            m.lightStart = e1; m.heavyStart = u2;
            m.kick = Atk("E_Kick", "SS_Sparta_Kick", new Spec { offHand = 0.85f, kind = AttackKind.Kick, speed = 1f, damage = 6f, poise = 35f, stamina = 10f, reaction = HitReaction.Stagger, guardBreak = true, knockback = 2.8f });
            m.aiAttacks = new[] { e1, e2, e6, e7, e8 }.Where(a => a != null).ToArray();
            GKBridge.Log("enemy moves: " + string.Join(", ", m.aiAttacks.Concat(new[] { u1, u2 }).Where(a => a != null).Select(a => a.name + " " + Way(a.cutDir))) +
                         "; chains " + string.Join(", ", m.aiAttacks.Where(a => a.nextLight != null).Select(a => a.name + " > " + a.nextLight.name + (AttackData.SameWay(a, a.nextLight) ? " SAME WAY" : ""))));
            m.aiUnblockables = new[] { u1, u2 }.Where(a => a != null).ToArray();
            FillDefense(m, false);
            m.warCry = Act("SS_Powering_Up", 1f, 0.15f, 0.9f, 0.85f, false);
            EditorUtility.SetDirty(m);
            EnemyMoves = m;
        }

        static void FillDefense(MoveSet m, bool player)
        {
            float dodgeSpeed = player ? 1.25f : 1.1f;
            m.dodge.front = Act("GK_Roll_Stand", dodgeSpeed, 0.06f, 0.78f, 0.62f, true, 0.06f, 0.55f, 0.7f);
            m.dodge.back = Act("GK_Dodge_JumpBack", dodgeSpeed, 0.06f, 0.82f, 0.6f, true, 0.05f, 0.5f, 0.7f);
            m.dodge.left = Act("GK_Dodge_Left", dodgeSpeed, 0.06f, 0.8f, 0.6f, true, 0.05f, 0.5f, 0.9f);
            m.dodge.right = Act("GK_Dodge_Right", dodgeSpeed, 0.06f, 0.8f, 0.6f, true, 0.05f, 0.5f, 0.9f);
            m.blockHit = new ActionClip { state = "BlockHit", clip = Clip("SS_Blocked_Impact_2") ?? Clip("SS_Blocked_Impact"), speed = 1.2f, fadeIn = 0.04f };
            m.guardBreak = Act("SS_Unblocked_Impact_2", 1f, 0.06f, 0.88f, 0.8f, true, 0, 0, 0.85f);
            m.parried = Act("SS_Blocked_Impact", 0.9f, 0.05f, 0.92f, 0.85f, true, 0, 0, 0.85f);
            // a swing that meets a guard rebounds off it (the sword-and-shield pack's blocked impact: arms thrown back)
            m.blockRecoil = Clip("SS_Blocked_Impact_2") != null ? Act("SS_Blocked_Impact_2", 1.3f, 0.06f, 0.7f, 0.5f, true, 0, 0, 0.85f)
                                                              : Act("SS_Blocked_Impact", 0.9f, 0.05f, 0.92f, 0.85f, true, 0, 0, 0.85f);
            m.hitLight.front = Act("GK_HitS_Front", 1.15f, 0.05f, 0.75f, 0.55f);
            m.hitLight.back = Act("GK_HitS_Back", 1.15f, 0.05f, 0.75f, 0.55f);
            m.hitLight.left = Act("GK_HitS_Left", 1.15f, 0.05f, 0.75f, 0.55f);
            m.hitLight.right = Act("GK_HitS_Right", 1.15f, 0.05f, 0.75f, 0.55f);
            m.hitHeavy = Act("GK_HitL_Front", 1.05f, 0.05f, 0.8f, 0.6f);
            m.stagger = Act("SS_Unblocked_Impact", 0.85f, 0.06f, 0.92f, 0.85f, true, 0, 0, 0.85f);
            m.knockdown = Act("GK_KD_Back", 1.05f, 0.06f, 0.95f, 0.95f);
            m.getUp = Act("GK_GetUp_Back", 1.15f, 0.1f, 0.9f, 0.8f);
            m.executionVictim = Act("GK_Exec_Loser", 1f, 0.1f, 0.99f, 0.99f);
            m.deaths = new[]
            {
                Act("GK_Death_Fwd", 1f, 0.1f), Act("GK_Death_Back", 1f, 0.1f), Act("GK_Death_Left", 1f, 0.1f), Act("GK_Death_Right", 1f, 0.1f),
                Act("SS_Falling_Back_Death", 1f, 0.1f, 0.92f, 0.9f, true, 0, 0, 0.85f), Act("SS_Falling_Forward_Death", 1f, 0.1f, 0.92f, 0.9f, true, 0, 0, 0.85f),
            }.Where(a => a.IsValid).ToArray();
        }

        // ------------------------------------------------------------------ animator controller

        static AvatarMask Mask(string name, params AvatarMaskBodyPart[] on)
        {
            string path = GKPaths.Generated + "/" + name + ".mask";
            var m = AssetDatabase.LoadAssetAtPath<AvatarMask>(path);
            if (m == null) { m = new AvatarMask(); AssetDatabase.CreateAsset(m, path); }
            for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++) m.SetHumanoidBodyPartActive((AvatarMaskBodyPart)i, false);
            foreach (var p in on) m.SetHumanoidBodyPartActive(p, true);
            EditorUtility.SetDirty(m);
            return m;
        }

        static void AddTimeScaled(BlendTree tree, AnimationClip clip, float threshold)
        {
            if (clip == null) return;
            tree.AddChild(clip, threshold);
            var ch = tree.children;
            float natural = clip.averageSpeed.magnitude;
            if (threshold > 0.1f && natural > 0.2f) ch[ch.Length - 1].timeScale = Mathf.Clamp(threshold / natural, 0.6f, 1.6f);
            tree.children = ch;
        }

        static void AddTimeScaled2D(BlendTree tree, AnimationClip clip, float speed, float armed)
        {
            if (clip == null) return;
            tree.AddChild(clip, new Vector2(speed, armed));
            var ch = tree.children;
            float natural = clip.averageSpeed.magnitude;
            if (speed > 0.1f && natural > 0.2f) ch[ch.Length - 1].timeScale = Mathf.Clamp(speed / natural, 0.6f, 1.6f);
            tree.children = ch;
        }

        /// <summary>Points every character prefab whose Animator lost its controller (or uses an older copy) at the
        /// current GK_Knight controller.</summary>
        [GKCommand("relinkanim", "relinkanim: give every character prefab the current GK_Knight animator controller")]
        public static void RelinkControllers(string arg)
        {
            var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
            if (ctrl == null) { GKBridge.Log("relinkanim: no controller at " + ControllerPath); return; }
            int fixedN = 0, ok = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { GKPaths.Prefabs }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var anim = root.GetComponent<Animator>();
                    var fighter = root.GetComponent<Combatant>();
                    if (anim == null || fighter == null) continue;
                    // nobody carries a shield: the off-hand layer replaces the sword-and-shield set's shield arm with
                    // a free left arm whenever the character is not in an action
                    bool offHand = fighter.offHandWeightFree < 0.99f;
                    if (offHand) fighter.offHandWeightFree = 1f;
                    if (anim.runtimeAnimatorController == ctrl && !offHand) { ok++; continue; }
                    anim.runtimeAnimatorController = ctrl;
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    fixedN++;
                    GKBridge.Log("relinkanim: " + path);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            GKBridge.Log("relinkanim: " + fixedN + " prefabs relinked, " + ok + " already right");
        }

        static void BuildController()
        {
            // rebuilt in place: deleting and re-creating the asset gave it a new GUID, and every prefab built earlier
            // (player, Seljuk warriors, heroes) silently lost its Animator Controller and froze in the bind pose
            var c = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (c == null) c = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            else
            {
                c.layers = new AnimatorControllerLayer[0];
                c.parameters = new AnimatorControllerParameter[0];
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(ControllerPath))
                    if (o != null && o != c) Object.DestroyImmediate(o, true);
                c.AddLayer("Base Layer");
            }
            c.AddParameter("Speed", AnimatorControllerParameterType.Float);
            c.AddParameter("MoveX", AnimatorControllerParameterType.Float);
            c.AddParameter("MoveY", AnimatorControllerParameterType.Float);
            c.AddParameter("Strafe", AnimatorControllerParameterType.Float);
            c.AddParameter(new AnimatorControllerParameter { name = "Armed", type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
            var sm = c.layers[0].stateMachine;

            c.AddParameter("Dual", AnimatorControllerParameterType.Float);
            // locomotion: speed x armed (sheathed sword = unarmed set, drawn = sword set), and on "Dual" the two-sword
            // stance: square to the front (Kevin Iglesias' one-handed combat idle) instead of the shield side forward
            BlendTree locoRoot;
            var locoState = c.CreateBlendTreeInController("Locomotion", out locoRoot, 0);
            locoRoot.blendType = BlendTreeType.Simple1D;
            locoRoot.blendParameter = "Dual";
            locoRoot.useAutomaticThresholds = false;
            for (int v = 0; v < 2; v++)
            {
                var loco = locoRoot.CreateBlendTreeChild(v);
                loco.name = v == 0 ? "Loco" : "LocoDual";
                loco.blendType = BlendTreeType.FreeformCartesian2D;
                loco.blendParameter = "Speed";
                loco.blendParameterY = "Armed";
                loco.useAutomaticThresholds = false;
                AddTimeScaled2D(loco, Clip("AX_unarmed_idle") ?? Clip("AX_standing_idle"), 0f, 0f);
                AddTimeScaled2D(loco, Clip("AX_unarmed_walk_forward") ?? Clip("AX_standing_walk_forward"), 1.6f, 0f);
                AddTimeScaled2D(loco, Clip("AX_unarmed_run_forward") ?? Clip("AX_standing_run_forward"), 3.9f, 0f);
                AddTimeScaled2D(loco, Clip("AX_unarmed_run_forward") ?? Clip("AX_standing_run_forward"), 6.3f, 0f);
                // sword drawn: the one-handed sword set (the axe set held the blade upright like a torch and crouched
                // the stance); the shield arm of that set is replaced on the off-hand layer when there is no shield
                AddTimeScaled2D(loco, (v == 1 ? Clip("XR_Dual_Idle_Kenway") ?? Clip("KI_CombatIdle1H") : null) ?? Clip("SS_Idle") ?? Clip("AX_standing_idle"), 0f, 1f);
                AddTimeScaled2D(loco, Clip("SS_Walk") ?? Clip("AX_standing_walk_forward"), 1.6f, 1f);
                AddTimeScaled2D(loco, Clip("SS_Run") ?? Clip("AX_standing_run_forward"), 3.9f, 1f);
                AddTimeScaled2D(loco, Clip("GK_Run_Sword") ?? Clip("SS_Run"), 6.3f, 1f);
            }

            BlendTree strafe;
            var strafeState = c.CreateBlendTreeInController("Strafe", out strafe, 0);
            strafe.blendType = BlendTreeType.FreeformDirectional2D;
            strafe.blendParameter = "MoveX";
            strafe.blendParameterY = "MoveY";
            const float R = 0.62f;
            System.Action<string, Vector2> add2 = (n, p) => { var cl = Clip(n); if (cl != null) strafe.AddChild(cl, p); };
            System.Action<string, string, Vector2> add2s = (n, fallback, p) => { var cl = Clip(n) ?? Clip(fallback); if (cl != null) strafe.AddChild(cl, p); };
            add2s("SS_Idle", "AX_standing_idle", Vector2.zero);
            add2s("SS_Walk", "AX_standing_walk_forward", new Vector2(0f, R));
            add2s("SS_Backward_Walk", "AX_standing_walk_back", new Vector2(0f, -R));
            add2s("SS_Left_Strafe_Walk", "AX_standing_walk_left", new Vector2(-R, 0f));
            add2s("SS_Right_Strafe_Walk", "AX_standing_walk_right", new Vector2(R, 0f));

            sm.defaultState = locoState;
            var toStrafe = locoState.AddTransition(strafeState);
            toStrafe.hasExitTime = false; toStrafe.duration = 0.2f; toStrafe.AddCondition(AnimatorConditionMode.Greater, 0.5f, "Strafe");
            var toLoco = strafeState.AddTransition(locoState);
            toLoco.hasExitTime = false; toLoco.duration = 0.2f; toLoco.AddCondition(AnimatorConditionMode.Less, 0.5f, "Strafe");

            int i = 0;
            foreach (var kv in actionStates.OrderBy(k => k.Key))
            {
                for (int dup = 0; dup < 2; dup++)
                {
                    string name = dup == 0 ? kv.Key : kv.Key + "#2";
                    var st = sm.AddState(name, new Vector3(600f + (i / 20) * 260f + dup * 120f, (i % 20) * 55f, 0f));
                    st.motion = kv.Value.Key;
                    st.speed = kv.Value.Value;
                    st.mirror = kv.Key.EndsWith("@M");
                    st.writeDefaultValues = true;
                }
                i++;
            }

            // ladder mount / climb / exits (base layer, IK pass for hands and feet on the rungs)
            GKTraversalBuilder.AddStates(c);

            // Block layer (upper body)
            c.AddLayer(GK.LayerBlock);
            c.AddLayer(GK.LayerOffHand);
            c.AddLayer(GK.LayerUpperBody);
            c.AddLayer(GK.LayerFullBodyAction);
            var layers = c.layers;
            layers[1].avatarMask = Mask("Mask_UpperBody", AvatarMaskBodyPart.Body, AvatarMaskBodyPart.Head, AvatarMaskBodyPart.LeftArm, AvatarMaskBodyPart.RightArm,
                                        AvatarMaskBodyPart.LeftFingers, AvatarMaskBodyPart.RightFingers, AvatarMaskBodyPart.LeftHandIK, AvatarMaskBodyPart.RightHandIK);
            layers[1].defaultWeight = 0f;
            layers[1].blendingMode = AnimatorLayerBlendingMode.Override;
            layers[1].iKPass = true;    // the two-sword guard sets the arms by IK (DualGuardIK)
            layers[2].avatarMask = Mask("Mask_LeftArm", AvatarMaskBodyPart.LeftArm, AvatarMaskBodyPart.LeftFingers, AvatarMaskBodyPart.LeftHandIK);
            layers[2].defaultWeight = 0f;
            layers[2].blendingMode = AnimatorLayerBlendingMode.Override;
            layers[3].avatarMask = layers[1].avatarMask;
            layers[3].defaultWeight = 0f;
            layers[3].blendingMode = AnimatorLayerBlendingMode.Override;
            layers[4].defaultWeight = 0f;                       // no mask: standing-still variants of the same actions
            layers[4].blendingMode = AnimatorLayerBlendingMode.Override;
            c.layers = layers;

            var bsm = c.layers[1].stateMachine;
            var bIdle = bsm.AddState("BlockIdle");
            bIdle.motion = Clip("SS_Block_Idle") ?? Clip("AX_standing_block_idle");
            var bHit = bsm.AddState("BlockHit");
            bHit.motion = Clip("SS_Blocked_Impact_2") ?? Clip("SS_Blocked_Impact");
            bHit.speed = 1.2f;
            var back = bHit.AddTransition(bIdle);
            back.hasExitTime = true; back.exitTime = 0.8f; back.duration = 0.15f;
            bsm.defaultState = bIdle;
            // the other styles' guards: a high sword guard (one sword), crossed blades (two swords; the axe pack's
            // two-hand guard lifts both hands together), each with its own reaction returning to it
            foreach (var g in new[] { "Sword", "Dual" })
            {
                var gi = bsm.AddState("BlockIdle_" + g);
                // two swords: the cross block captured from video (Rokoko, retargeted onto X Bot), both blades raised
                // and crossed before the face; Mixamo's centre block (fists up like a boxer) stays as the fallback
                gi.motion = (g == "Dual" ? Clip("XR_Dual_Block_Idle") ?? Clip("MX_Center_Block_v1") : null) ?? Clip("AX_standing_block_idle") ?? Clip("SS_Block_Idle");
                var gh = bsm.AddState("BlockHit_" + g);
                gh.motion = (g == "Dual" ? Clip("XR_Dual_Block_Hit") ?? Clip("MX_Center_Block_v2") : null) ?? Clip("AX_standing_block_react_large") ?? bHit.motion;
                gh.speed = g == "Dual" ? 1f : 1.2f;
                var gb = gh.AddTransition(gi);
                // the one-sword recoil (a large react) lowers the blade from ~25 % on: keep only the jolt of the
                // impact and blend straight back into the high guard, so the guard holds (Assassin's Creed style)
                bool jolt = g == "Sword";
                gb.hasExitTime = true; gb.exitTime = jolt ? 0.2f : 0.8f; gb.duration = jolt ? 0.18f : 0.15f;
                if (jolt) gh.speed = 1.3f;
            }

            // upper-body actions that must not stop the legs: draw / sheathe (later: drink, read, signal)
            var usm = c.layers[3].stateMachine;
            var uEmpty = usm.AddState(GK.S_UBEmpty);
            usm.defaultState = uEmpty;
            var uDraw = usm.AddState(GK.S_UBDraw);
            uDraw.motion = HolsterDrawClip != null ? HolsterDrawClip : Clip("SS_draw_sword_1"); uDraw.speed = 1.1f;
            var uSheathe = usm.AddState(GK.S_UBSheathe);
            uSheathe.motion = HolsterSheatheClip != null ? HolsterSheatheClip : Clip("SS_sheath_sword_1"); uSheathe.speed = 1.1f;

            // full-body versions, used when standing still so the feet / hips match the hand-over analysis exactly
            var fsm = c.layers[4].stateMachine;
            var fEmpty = fsm.AddState(GK.S_FBEmpty);
            fsm.defaultState = fEmpty;
            var fDraw = fsm.AddState(GK.S_FBDraw);
            fDraw.motion = uDraw.motion; fDraw.speed = uDraw.speed;
            var fSheathe = fsm.AddState(GK.S_FBSheathe);
            fSheathe.motion = uSheathe.motion; fSheathe.speed = uSheathe.speed;

            // off hand: a free left arm that swings with the gait (unarmed set), over the shield arm of the sword set
            BlendTree ob;
            var oPose = c.CreateBlendTreeInController("OffHandPose", out ob, 2);
            ob.blendType = BlendTreeType.Simple1D;
            ob.blendParameter = "Speed";
            ob.useAutomaticThresholds = false;
            System.Action<string, string, float> addO = (n, fallback, th) => { var cl = Clip(n) ?? Clip(fallback); if (cl != null) ob.AddChild(cl, th); };
            addO("AX_unarmed_idle", "AX_standing_idle", 0f);
            addO("AX_unarmed_walk_forward", "AX_standing_walk_forward", 1.6f);
            addO("AX_unarmed_run_forward", "AX_standing_run_forward", 3.9f);
            addO("AX_unarmed_run_forward", "AX_standing_run_forward", 6.3f);
            c.layers[2].stateMachine.defaultState = oPose;

            // two swords: the left arm in the sword stance, mirrored from the right arm's (with the gait)
            BlendTree db;
            c.CreateBlendTreeInController("OffHandDual", out db, 2);
            db.blendType = BlendTreeType.Simple1D;
            db.blendParameter = "Speed";
            db.useAutomaticThresholds = false;
            addO = (n, fallback, th) => { var cl = Clip(n) ?? Clip(fallback); if (cl != null) db.AddChild(cl, th); };
            addO("XR_Dual_Idle_Kenway", "KI_CombatIdle1H", 0f);
            addO("SS_Walk", "AX_standing_walk_forward", 1.6f);
            addO("SS_Run", "AX_standing_run_forward", 3.9f);
            addO("GK_Run_Sword", "SS_Run", 6.3f);
            var dch = db.children;
            // the walk and run mirrored: the left arm takes the right arm's sword carry from the same capture, so both
            // blades are held alike; the two-sword idle (Kenway's low guard, captured from video and retargeted onto
            // X Bot) already holds both swords and plays as it is
            for (int k = 0; k < dch.Length; k++) dch[k].mirror = dch[k].motion == null || !(dch[k].motion.name.StartsWith("GK_") || dch[k].motion.name.StartsWith("XR_Dual"));
            db.children = dch;

            // sword and shield, standing and walking: the shield arm as Kevin Iglesias' one-handed combat stance holds
            // it (forearm level, the shield upright at the left side). The sword-and-shield idle let the forearm hang
            // and the kite shield lay across the thighs, its point at the right knee
            // one sword: the free hand kept up as a guard before the chest, as a one-handed swordsman holds it (the
            // left arm of Kevin Iglesias' one-handed combat stance standing, of the sword walk and run moving)
            BlendTree sb;
            c.CreateBlendTreeInController("OffHandSword", out sb, 2);
            sb.blendType = BlendTreeType.Simple1D;
            sb.blendParameter = "Speed";
            sb.useAutomaticThresholds = false;
            addO = (n, fallback, th) => { var cl = Clip(n) ?? Clip(fallback); if (cl != null) sb.AddChild(cl, th); };
            addO("KI_CombatIdle1H", "SS_Idle", 0f);
            addO("SS_Walk", "AX_standing_walk_forward", 1.6f);
            addO("SS_Run", "AX_standing_run_forward", 3.9f);
            addO("GK_Run_Sword", "SS_Run", 6.3f);

            var sCarry = c.layers[2].stateMachine.AddState("OffHandShield");
            sCarry.motion = Clip("KI_CombatIdle1H") ?? Clip("SS_Idle");

            // the left arm's gear moves (FightingStyles): the shield over the shoulder onto the back and back, and
            // the second sword drawn from / sheathed at the right hip (the right hand's draw, mirrored)
            var osm = c.layers[2].stateMachine;
            System.Action<string, string, float> addArm = (state, clip, speed) =>
            {
                var cl = Clip(clip);
                if (cl == null) { GKBridge.Log("combat: no clip " + clip + " for " + state); return; }
                var st = osm.AddState(state);
                st.motion = cl; st.mirror = true; st.speed = speed;
            };
            addArm(FightingStyles.S_ShieldToBack, "AX_standing_disarm_over_shoulder", ShieldMoveSpeed);
            addArm(FightingStyles.S_ShieldFromBack, "AX_unarmed_equip_over_shoulder", ShieldMoveSpeed);
            addArm(FightingStyles.S_OffDraw, HolsterDrawClip != null ? HolsterDrawClip.name : "SS_draw_sword_1", 1.1f);
            addArm(FightingStyles.S_OffSheathe, HolsterSheatheClip != null ? HolsterSheatheClip.name : "SS_sheath_sword_1", 1.1f);

            EditorUtility.SetDirty(c);
        }

        // ------------------------------------------------------------------ archer (E06 Turkmen archer)
        // Mixamo's Pro Longbow / Longbow Locomotion packs on X Bot, copied in as LB_* (the bow in the left hand, the
        // draw by the right). The archer plays the shared GK_Knight state machine through an override controller: the
        // sword locomotion and strafe clips are swapped for the bow's, every other state (hits, dodges, deaths,
        // knockdown) is the same as the swordsmen's, and the bow's draw / hold / loose are action states of their own.

        public const string ArcherControllerPath = GKPaths.Generated + "/GK_Archer.overrideController";
        public const string ArcherPrefabPath = GKPaths.Prefabs + "/Seljuk_Archer.prefab";
        public const string ArrowPrefabPath = GKPaths.Prefabs + "/Arrow.prefab";
        const string BowFbx = GKPaths.Weapons + "/CompositeBow/CompositeBow.fbx";
        const string ArrowFbx = GKPaths.Weapons + "/CompositeBow/Arrow.fbx";
        static readonly string[] ArcherCoreClips = { "LB_Idle", "LB_Walk_F", "LB_Run_F", "LB_Draw", "LB_Hold_Loop", "LB_Loose" };

        public static MoveSet ArcherMoves { get { return AssetDatabase.LoadAssetAtPath<MoveSet>(GKPaths.Data + "/Moves_Archer.asset"); } }

        static void BuildArcherMoves()
        {
            var missing = ArcherCoreClips.Where(n => Clip(n) == null).ToList();
            if (missing.Count > 0) { GKBridge.Log("archer: moves skipped, missing " + string.Join(", ", missing)); return; }
            var m = MoveSetAsset("Moves_Archer");
            FillDefense(m, false);
            System.Func<ActionClip, ActionClip, ActionClip> or = (a, b) => a != null && a.IsValid ? a : b;
            m.dodge.front = or(Act("LB_Dodge_F", 1.1f, 0.06f, 0.8f, 0.6f, true, 0.05f, 0.5f), m.dodge.front);
            m.dodge.back = or(Act("LB_Dodge_B", 1.1f, 0.06f, 0.8f, 0.6f, true, 0.05f, 0.5f), m.dodge.back);
            m.dodge.left = or(Act("LB_Dodge_L", 1.1f, 0.06f, 0.8f, 0.6f, true, 0.05f, 0.5f), m.dodge.left);
            m.dodge.right = or(Act("LB_Dodge_R", 1.1f, 0.06f, 0.8f, 0.6f, true, 0.05f, 0.5f), m.dodge.right);
            m.hitLight.front = or(Act("LB_Hit", 1.1f, 0.05f, 0.75f, 0.55f), m.hitLight.front);
            var deaths = new List<ActionClip> { Act("LB_Death_F", 1f, 0.1f), Act("LB_Death_B", 1f, 0.1f) };
            m.deaths = deaths.Where(a => a.IsValid).Concat(m.deaths ?? new ActionClip[0]).ToArray();
            // the shot: draw (the tell) to the cheek, hold while the aim settles, loose. Stood still: no root motion
            m.bowDraw = Act("LB_Draw", 1f, 0.12f, 1f, 1f, false);
            m.bowHold = Act("LB_Hold_Loop", 1f, 0.1f, 1f, 0f, false);
            m.bowLoose = Act("LB_Loose", 1f, 0.03f, 0.8f, 0.55f, false);
            m.bowFullDraw = FullDrawTime(m.bowDraw.clip);
            m.kick = Atk("E_Archer_Kick", Clip("LB_Kick") != null ? "LB_Kick" : "SS_Sparta_Kick",
                         new Spec { kind = AttackKind.Kick, speed = 1f, damage = 5f, poise = 30f, stamina = 8f, reaction = HitReaction.Stagger, knockback = 2.6f });
            m.lightStart = null; m.heavyStart = null; m.aiAttacks = new AttackData[0]; m.aiUnblockables = new AttackData[0];
            EditorUtility.SetDirty(m);
            GKBridge.Log("archer: moves built, full draw at " + m.bowFullDraw.ToString("F2") + " of LB_Draw (" + (m.bowDraw.clip != null ? m.bowDraw.clip.length.ToString("F2") : "?") + " s)");
        }

        /// <summary>When in the draw the string is at the cheek: the first moment the right hand is within 3 cm of its
        /// farthest from the left (the bow hand), on the analysis probe, searched in the clip's second half only (in the
        /// first the right hand reaches back to the quiver at the hip, the hands' widest apart, and that was taken).</summary>
        static float FullDrawTime(AnimationClip clip)
        {
            if (clip == null || probe == null) return 0.85f;
            var anim = probe.GetComponentInChildren<Animator>();
            if (anim == null || !anim.isHuman) return 0.85f;
            Transform l = anim.GetBoneTransform(HumanBodyBones.LeftHand), r = anim.GetBoneTransform(HumanBodyBones.RightHand);
            const int N = 60;
            var d = new float[N + 1];
            for (int i = 0; i <= N; i++) { clip.SampleAnimation(anim.gameObject, clip.length * i / N); d[i] = Vector3.Distance(l.position, r.position); }
            float max = 0f;
            for (int i = N / 2; i <= N; i++) max = Mathf.Max(max, d[i]);
            for (int i = N / 2; i <= N; i++) if (d[i] >= max - 0.03f) return Mathf.Clamp((float)i / N, 0.5f, 0.98f);
            return 0.85f;
        }

        [GKCommand("archer", "archer: build the E06 archer (Seljuk_Archer prefab with the composite bow, Arrow prefab, GK_Archer override controller); run combat first")]
        static void BuildArcher(string arg)
        {
            var moves = ArcherMoves;
            if (moves == null || !moves.bowDraw.IsValid) { GKBridge.Log("archer: no Moves_Archer with a draw: import the LB_ clips and run combat first"); return; }
            var baseCtrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
            // the bending bow (Art, 0c9a18d) is a skinned mesh with blendshapes Draw50 / Draw100 on one bone: its importer
            // must keep both (the weapons rule imports rigid props without a rig or blendshapes, and the file kept the old
            // bow's settings, so the first install came in rigid)
            var bowImp = AssetImporter.GetAtPath(BowFbx) as ModelImporter;
            if (bowImp != null && (!bowImp.importBlendShapes || bowImp.animationType != ModelImporterAnimationType.Generic))
            {
                bowImp.importBlendShapes = true;
                bowImp.animationType = ModelImporterAnimationType.Generic;
                bowImp.avatarSetup = ModelImporterAvatarSetup.NoAvatar;
                bowImp.importAnimation = false;
                bowImp.SaveAndReimport();
                GKBridge.Log("archer: bow re-imported with its skin and blendshapes");
            }
            var bowModel = AssetDatabase.LoadAssetAtPath<GameObject>(BowFbx);
            var arrowModel = AssetDatabase.LoadAssetAtPath<GameObject>(ArrowFbx);
            if (baseCtrl == null || bowModel == null || arrowModel == null) { GKBridge.Log("archer: missing " + (baseCtrl == null ? ControllerPath : bowModel == null ? BowFbx : ArrowFbx)); return; }

            // ---- materials (plain lit colours until Art's textures)
            var limb = LitMat("Bow_Limb", new Color(0.23f, 0.12f, 0.06f), 0.45f);
            var grip = LitMat("Bow_Grip", new Color(0.10f, 0.07f, 0.05f), 0.2f);
            var tips = LitMat("Bow_Siyah", new Color(0.78f, 0.72f, 0.60f), 0.5f);
            var shaft = LitMat("Arrow_Shaft", new Color(0.55f, 0.42f, 0.26f), 0.25f);
            var head = LitMat("Arrow_Head", new Color(0.35f, 0.35f, 0.37f), 0.6f);
            var fletch = LitMat("Arrow_Fletch", new Color(0.42f, 0.38f, 0.33f), 0.1f);
            var str = LitMat("Bow_String", new Color(0.72f, 0.66f, 0.52f), 0.2f);
            InterimSeljukDress();
            // Art's composite bow (2026-10-31, horn belly, birch-bark back, bone siyahs): one 1024 atlas on all three slots,
            // URP Lit (BaseColor sRGB, Normal OpenGL, MetalSmooth linear with smoothness in its alpha)
            string bowTex = GKPaths.Weapons + "/CompositeBow/Textures/T_CompositeBow_";
            var bowBase = BowTexture(bowTex + "BaseColor.png", true, false);
            var bowNormal = BowTexture(bowTex + "Normal.png", false, true);
            var bowMs = BowTexture(bowTex + "MetalSmooth.png", false, false);
            if (bowBase != null)
                foreach (var m in new[] { limb, grip, tips })
                {
                    m.SetColor("_BaseColor", Color.white); m.SetTexture("_BaseMap", bowBase);
                    if (bowNormal != null) { m.SetTexture("_BumpMap", bowNormal); m.SetFloat("_BumpScale", 1f); m.EnableKeyword("_NORMALMAP"); }
                    if (bowMs != null) { m.SetTexture("_MetallicGlossMap", bowMs); m.EnableKeyword("_METALLICSPECGLOSSMAP"); m.SetFloat("_Smoothness", 1f); m.SetFloat("_SmoothnessTextureChannel", 0f); }
                    EditorUtility.SetDirty(m);
                }

            // ---- the arrow prefab
            // the model inside a root whose +Z is the flight and whose origin is the tip: the shaft (from the tip to the
            // mesh's middle) is turned onto -Z, whatever axes the export used
            var model = (GameObject)PrefabUtility.InstantiatePrefab(arrowModel);
            PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            var ag = new GameObject("Arrow");
            model.transform.SetParent(ag.transform, false);
            var mr0 = model.GetComponentInChildren<MeshRenderer>();
            Vector3 shaftDir = mr0 != null ? (mr0.bounds.center - model.transform.position).normalized : Vector3.back;
            model.transform.rotation = Quaternion.FromToRotation(shaftDir, Vector3.back) * model.transform.rotation;
            foreach (var r in ag.GetComponentsInChildren<MeshRenderer>()) { r.sharedMaterials = ArrowMats(r, shaft, head, fletch); r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; }
            foreach (var c in ag.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);
            if (ag.GetComponent<Arrow>() == null) ag.AddComponent<Arrow>();
            var arrowPrefab = PrefabUtility.SaveAsPrefabAsset(ag, ArrowPrefabPath);
            Object.DestroyImmediate(ag);

            // ---- the override controller: bow locomotion in place of the sword's
            var oc = AssetDatabase.LoadAssetAtPath<AnimatorOverrideController>(ArcherControllerPath);
            if (oc == null) { oc = new AnimatorOverrideController(baseCtrl); AssetDatabase.CreateAsset(oc, ArcherControllerPath); }
            oc.runtimeAnimatorController = baseCtrl;
            var map = new Dictionary<string, string>
            {
                { "SS_Idle", "LB_Idle" }, { "AX_unarmed_idle", "LB_Idle" }, { "AX_standing_idle", "LB_Idle" }, { "KI_CombatIdle1H", "LB_Idle" }, { "XR_Dual_Idle_Kenway", "LB_Idle" },
                { "SS_Walk", "LB_Walk_F" }, { "AX_unarmed_walk_forward", "LB_Walk_F" }, { "AX_standing_walk_forward", "LB_Walk_F" },
                { "SS_Run", "LB_Run_F" }, { "GK_Run_Sword", "LB_Run_F" }, { "AX_unarmed_run_forward", "LB_Run_F" }, { "AX_standing_run_forward", "LB_Run_F" },
                { "SS_Backward_Walk", "LB_Walk_B" }, { "AX_standing_walk_back", "LB_Walk_B" },
                { "SS_Left_Strafe_Walk", "LB_Walk_L" }, { "AX_standing_walk_left", "LB_Walk_L" },
                { "SS_Right_Strafe_Walk", "LB_Walk_R" }, { "AX_standing_walk_right", "LB_Walk_R" },
            };
            var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
            oc.GetOverrides(pairs);
            int swapped = 0;
            for (int i = 0; i < pairs.Count; i++)
            {
                string to;
                if (pairs[i].Key != null && map.TryGetValue(pairs[i].Key.name, out to) && Clip(to) != null) { pairs[i] = new KeyValuePair<AnimationClip, AnimationClip>(pairs[i].Key, Clip(to)); swapped++; }
                else pairs[i] = new KeyValuePair<AnimationClip, AnimationClip>(pairs[i].Key, null);
            }
            oc.ApplyOverrides(pairs);
            EditorUtility.SetDirty(oc);

            // ---- where the bow sits in the left hand: from the held aim (LB_Hold_Loop) on a scratch Seljuk, the bow's
            // shot axis along the line from the string hand to the bow hand, its limbs upright across it
            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKGameBuilder.EnemyPrefabPath);
            Vector3 bowLocalPos; Quaternion bowLocalRot;
            {
                var tmp = (GameObject)Object.Instantiate(basePrefab);
                try
                {
                    var an = tmp.GetComponent<Animator>();
                    var hold = Clip("LB_Hold_Loop");
                    hold.SampleAnimation(tmp, hold.length * 0.5f);
                    Transform lh = an.GetBoneTransform(HumanBodyBones.LeftHand), rh = an.GetBoneTransform(HumanBodyBones.RightHand);
                    Transform lm = an.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
                    Vector3 shot = (lh.position - rh.position).normalized;
                    Vector3 up = Vector3.ProjectOnPlane(Vector3.up, shot).normalized;
                    // the grip in the fist: a little past the knuckles from the wrist
                    Vector3 gripW = lm != null ? lh.position + (lm.position - lh.position) * 0.75f : lh.position + shot * 0.06f;
                    Quaternion bowW = Quaternion.LookRotation(shot, up);
                    bowLocalPos = lh.InverseTransformPoint(gripW);
                    bowLocalRot = Quaternion.Inverse(lh.rotation) * bowW;
                    GKBridge.Log("archer: bow fitted at full draw, string hand " + (Vector3.Distance(lh.position, rh.position) * 100f).ToString("F0") + " cm behind the bow hand");
                }
                finally { Object.DestroyImmediate(tmp); }
            }

            // ---- the archer prefab: the Seljuk warrior with the bow, his sword and scabbard hidden (the sword's hitbox
            // stays for the kick's limbs), the archer's brain in place of the swordsman's
            var root = PrefabUtility.LoadPrefabContents(GKGameBuilder.EnemyPrefabPath);
            try
            {
                root.name = "Seljuk_Archer";
                foreach (var ai in root.GetComponents<EnemyAI>()) Object.DestroyImmediate(ai);
                foreach (var h in root.GetComponentsInChildren<WeaponHolster>(true)) Object.DestroyImmediate(h);
                foreach (var s in root.GetComponentsInChildren<ScabbardMotion>(true)) Object.DestroyImmediate(s);
                foreach (var s in root.GetComponentsInChildren<WeaponSelfCollision>(true)) Object.DestroyImmediate(s);
                foreach (var s in root.GetComponentsInChildren<SwingAim>(true)) Object.DestroyImmediate(s);
                foreach (var tr in root.GetComponentsInChildren<Transform>(true))
                    if (tr.name == "Sword" || tr.name == "Scabbard" || tr.name == "ScabbardPivot" || tr.name == "BladeTrail")
                        foreach (var r in tr.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                var cb = root.GetComponent<Combatant>();
                cb.moves = moves; cb.canBlock = false; cb.canParry = false;
                var an = root.GetComponent<Animator>();
                an.runtimeAnimatorController = oc;
                var lh = an.GetBoneTransform(HumanBodyBones.LeftHand);
                var oldBow = lh.Find("CompositeBow"); if (oldBow != null) Object.DestroyImmediate(oldBow.gameObject);
                var bow = (GameObject)PrefabUtility.InstantiatePrefab(bowModel);
                PrefabUtility.UnpackPrefabInstance(bow, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                bow.name = "CompositeBow";
                bow.transform.SetParent(lh, false);
                // the model's own frame from its empties (limbs: bottom nock to top; shot: from the string's middle to the
                // grip, the model's origin), so the fit holds whatever axes the export used. Not to the arrow rest: on the
                // composite bow it sits 2 cm to the right of the grip (thumb draw), which would yaw the bow ~7 deg in the hand
                Transform nt = bow.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == "NockTop");
                Transform nb = bow.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == "NockBottom");
                Transform rest = bow.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == "ArrowRest");
                Quaternion bowFrame = Quaternion.identity;
                if (nt != null && nb != null && rest != null)
                {
                    Vector3 limbL = bow.transform.InverseTransformDirection(nt.position - nb.position).normalized;
                    Vector3 shotL = Vector3.ProjectOnPlane(-bow.transform.InverseTransformPoint((nt.position + nb.position) * 0.5f), limbL).normalized;
                    bowFrame = Quaternion.LookRotation(shotL, limbL);
                }
                bow.transform.localPosition = bowLocalPos; bow.transform.localRotation = bowLocalRot * Quaternion.Inverse(bowFrame);
                // the bending bow is skinned (one bone at the grip, blendshapes Draw50 / Draw100), the old one a plain mesh
                foreach (var r in bow.GetComponentsInChildren<Renderer>()) r.sharedMaterials = new[] { limb, grip, tips }.Take(Mathf.Max(1, r.sharedMaterials.Length)).ToArray();
                var bowSkin = bow.GetComponentInChildren<SkinnedMeshRenderer>();
                if (bowSkin != null)
                {
                    // bounds wide enough for the drawn limbs: it never culls at the draw
                    bowSkin.updateWhenOffscreen = false;
                    var lb = bowSkin.localBounds; lb.Expand(0.5f); bowSkin.localBounds = lb;
                }
                var rig = bow.AddComponent<BowRig>();
                rig.nockTop = bow.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == "NockTop");
                rig.nockBottom = bow.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == "NockBottom");
                rig.arrowRest = bow.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == "ArrowRest");
                System.Func<string, Transform> empty = n => bow.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == n);
                rig.nockTopHalf = empty("NockTopHalf"); rig.nockBottomHalf = empty("NockBottomHalf");
                rig.nockTopDrawn = empty("NockTopDrawn"); rig.nockBottomDrawn = empty("NockBottomDrawn");
                rig.limbs = bowSkin;
                GKBridge.Log("archer: bow " + (bowSkin != null && bowSkin.sharedMesh != null ? "skinned, blendshapes " + bowSkin.sharedMesh.blendShapeCount + ", nocks half/drawn " + (rig.nockTopHalf != null && rig.nockTopDrawn != null) : "rigid"));
                var sgo = new GameObject("String");
                sgo.transform.SetParent(bow.transform, false);
                var lr = sgo.AddComponent<LineRenderer>();
                lr.sharedMaterial = str; lr.widthMultiplier = 0.004f; lr.positionCount = 3; lr.useWorldSpace = true;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; lr.numCapVertices = 0;
                rig.stringLine = lr;
                var nocked = (GameObject)PrefabUtility.InstantiatePrefab(arrowPrefab);
                PrefabUtility.UnpackPrefabInstance(nocked, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                foreach (var a in nocked.GetComponents<Arrow>()) Object.DestroyImmediate(a);
                nocked.name = "NockedArrow";
                nocked.transform.SetParent(bow.transform, false);
                nocked.SetActive(false);
                rig.nockedArrow = nocked.transform;
                var ai2 = root.GetComponent<ArcherAI>();
                if (ai2 == null) ai2 = root.AddComponent<ArcherAI>();
                ai2.arrowPrefab = arrowPrefab;
                ai2.arrowRest = rig.arrowRest;
                PrefabUtility.SaveAsPrefabAsset(root, ArcherPrefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            AssetDatabase.SaveAssets();
            GKBridge.Log("archer: built " + ArcherPrefabPath + " (" + swapped + " locomotion clips swapped to the bow's; draw " +
                         moves.bowDraw.clip.length.ToString("F2") + " s, full draw at " + moves.bowFullDraw.ToString("F2") + "), " + ArrowPrefabPath);
        }

        static Material[] ArrowMats(Renderer r, Material shaft, Material head, Material fletch)
        {
            int n = Mathf.Max(1, r.sharedMaterials.Length);
            return new[] { shaft, head, fletch }.Take(n).ToArray();
        }

        /// <summary>
        /// Art's interim coalition dress (2026-10-31, until the archer's own Turkmen kit): the Seljuk enemies wore the Knight's
        /// surcoat with the Georgian cross. Seljuk.mat (all three: warrior, veteran, archer) takes Art's copy of the Knight
        /// texture with the badge painted out and the tabard dyed rust, and its flattened normal; Knight.mat keeps the
        /// originals. The material's sun-faded tint stays. GKGameBuilder rebuilds Seljuk.mat from Knight.mat (tools), so the
        /// combat build sets these again each time.
        /// </summary>
        static void InterimSeljukDress()
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(GKPaths.Materials + "/Seljuk.mat");
            string dir = GKPaths.Characters + "/Knight/Textures/";
            var bc = BowTexture(dir + "Seljuk_Interim_BaseColor.png", true, false, 4096);
            var nm = BowTexture(dir + "Seljuk_Interim_Normal.png", false, true, 4096);
            if (m == null || bc == null) return;
            m.SetTexture("_BaseMap", bc);
            if (nm != null) { m.SetTexture("_BumpMap", nm); m.EnableKeyword("_NORMALMAP"); }
            EditorUtility.SetDirty(m);
            GKBridge.Log("enemy dress: Seljuk.mat on the interim coalition textures (no cross)");
        }

        /// <summary>A bow texture with its import settings (colour space, normal map), re-imported when they change.</summary>
        static Texture2D BowTexture(string path, bool srgb, bool normal, int maxSize = 1024)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return null;
            var type = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            if (ti.textureType != type || ti.sRGBTexture != srgb || ti.maxTextureSize != maxSize)
            {
                ti.textureType = type; ti.sRGBTexture = srgb; ti.maxTextureSize = maxSize;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Material LitMat(string name, Color c, float smooth)
        {
            string path = GKPaths.Materials + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                var sh = Shader.Find("Universal Render Pipeline/Lit");
                m = new Material(sh); AssetDatabase.CreateAsset(m, path);
            }
            m.SetColor("_BaseColor", c); m.SetFloat("_Smoothness", smooth);
            EditorUtility.SetDirty(m);
            return m;
        }

        // ------------------------------------------------------------------ veteran (E08 ghulam veteran, mace and shield)
        // DECISIONS 2026-10-31: the seasoned ghulam fights with a six-flanged mace and his round shield. His cuts are
        // Mixamo's one-handed axe set (AX_*: the same arcs a mace swings), the shield arm held by the off-hand layer like
        // the swordsmen's; the spinning blow is his unblockable. Numbers from BALANCE 5.1 E08 (damage x1.45 of the base
        // enemy blows, poise 75, HP 190).

        public const string VeteranPrefabPath = GKPaths.Prefabs + "/Seljuk_Veteran.prefab";
        const string MaceFbx = GKPaths.Weapons + "/FlangedMace/FlangedMace.fbx";

        public static MoveSet VeteranMoves { get { return AssetDatabase.LoadAssetAtPath<MoveSet>(GKPaths.Data + "/Moves_Veteran.asset"); } }

        static void BuildVeteranMoves()
        {
            if (Clip("AX_standing_melee_attack_downward") == null) { GKBridge.Log("veteran: moves skipped, no AX_ clips"); return; }
            // speed 0.72: at 0.95 the downward blow reached the hero 0.40 s after it began (duelqa), under the 0.55 s an
            // enemy blow must give to be read
            var v1 = Atk("E_V1_MaceDown", "AX_standing_melee_attack_downward", new Spec { speed = 0.72f, damage = 28f, poise = 32f, stamina = 12f, knockback = 1.5f, offHand = 0.85f });
            var v2 = Atk("E_V2_MaceSide", "AX_standing_melee_attack_horizontal", new Spec { speed = 1f, damage = 25f, poise = 26f, stamina = 11f, knockback = 1.3f, offHand = 0.85f });
            var v3 = Atk("E_V3_Backhand", "AX_standing_melee_attack_backhand", new Spec { speed = 1f, damage = 24f, poise = 24f, stamina = 10f, offHand = 0.85f });
            // its blade touched a standing hero 0-1 cm just before its window opened and went on as a miss (lease L): the
            // window opens 2 % earlier; the read rule then applies to the new window
            if (v3 != null) { GKClothQa.ApplyWindow(v3, Mathf.Max(0.05f, v3.hitStart - 0.02f), v3.hitEnd); EnemyReadRule(v3); EditorUtility.SetDirty(v3); }
            var v4 = Atk("E_V4_Combo", "AX_standing_melee_combo_attack_ver_1", new Spec { speed = 1f, damage = 22f, poise = 22f, stamina = 14f, offHand = 0.85f });
            var u1 = Atk("E_VU_Spin", "AX_standing_melee_attack_360_high", new Spec { kind = AttackKind.Heavy, speed = 0.9f, damage = 40f, poise = 70f, stamina = 22f, unblockable = true,
                                                                                     reaction = HitReaction.Knockdown, knockback = 2.6f, hyper = true, hitStop = 0.1f, shake = 0.5f, offHand = 0.85f });
            Link(v1, v2, null); Link(v2, v3, null);
            // the clips were analysed with the probe's sword (its tip ~0.85 m from the grip); the mace head ends 0.47 m from
            // it: every range shortened by the difference, or he swings from too far (backhand missed a standing hero 5 of
            // 8, the spin 3 of 3)
            const float MaceShort = 0.38f;
            foreach (var a in new[] { v1, v2, v3, v4, u1 })
            {
                if (a == null) continue;
                a.idealRange = Mathf.Max(0.9f, a.idealRange - MaceShort);
                EditorUtility.SetDirty(a);
            }
            var m = MoveSetAsset("Moves_Veteran");
            m.lightStart = v1; m.heavyStart = u1;
            var seljuk = EnemyMoves;
            m.kick = seljuk != null ? seljuk.kick : null;
            m.aiAttacks = new[] { v1, v2, v3, v4 }.Where(a => a != null).ToArray();
            GKBridge.Log("veteran moves: " + string.Join(", ", m.aiAttacks.Concat(new[] { u1 }).Where(a => a != null).Select(a => a.name + " " + Way(a.cutDir))) +
                         (v3 != null ? "; backhand window " + (v3.hitStart * 100f).ToString("F0") + "-" + (v3.hitEnd * 100f).ToString("F0") + "%, start to window " +
                          (v3.hitStart * v3.clip.length / Mathf.Max(0.1f, v3.speed) / Mathf.Max(0.5f, v3.windupSpeed)).ToString("F2") + " s" : ""));
            m.aiUnblockables = new[] { u1 }.Where(a => a != null).ToArray();
            FillDefense(m, false);
            var cry = Act("AX_standing_taunt_battlecry", 1f, 0.15f, 0.9f, 0.85f, false);
            m.warCry = cry.IsValid ? cry : (seljuk != null ? seljuk.warCry : m.warCry);
            EditorUtility.SetDirty(m);
            GKBridge.Log("veteran: moves built, " + m.aiAttacks.Length + " blows + " + m.aiUnblockables.Length + " unblockable");
        }

        [GKCommand("veteran", "veteran: build the E08 ghulam veteran (Seljuk_Veteran prefab: flanged mace and round shield, Moves_Veteran, BALANCE E08 numbers); run combat first")]
        static void BuildVeteran(string arg)
        {
            var moves = VeteranMoves;
            var maceModel = AssetDatabase.LoadAssetAtPath<GameObject>(MaceFbx);
            if (moves == null || moves.lightStart == null) { GKBridge.Log("veteran: no Moves_Veteran: run combat first"); return; }
            if (maceModel == null) { GKBridge.Log("veteran: missing " + MaceFbx); return; }
            var haft = LitMat("Mace_Haft", new Color(0.42f, 0.30f, 0.18f), 0.35f);
            var grip = LitMat("Mace_Grip", new Color(0.16f, 0.10f, 0.06f), 0.2f);
            var iron = LitMat("Mace_Iron", new Color(0.30f, 0.30f, 0.31f), 0.55f);
            iron.SetFloat("_Metallic", 1f);

            var root = PrefabUtility.LoadPrefabContents(GKGameBuilder.EnemyPrefabPath);
            string log;
            try
            {
                root.name = "Seljuk_Veteran";
                var cb = root.GetComponent<Combatant>();
                cb.moves = moves;
                var st = root.GetComponent<CombatStats>();
                if (st != null) { st.maxHealth = 190f; st.maxPoise = 75f; st.maxStamina = 110f; }
                var ai = root.GetComponent<EnemyAI>();
                if (ai != null)
                {
                    ai.aggression = 0.7f; ai.blockSkill = 0.6f; ai.dodgeSkill = 0.25f; ai.comboChance = 0.6f;
                    ai.unblockableChance = 0.2f; ai.reactionTime = 0.2f; ai.runSpeed = 3.4f;
                    ai.attackRange = 1.7f;   // the mace is 0.38 m shorter than the sword the swordsman's 2.1 m was set for
                }
                // the mace in place of the sword: its grip where the sword's was, its haft along the blade's line; the
                // sword's hitbox now spans the mace head (the haft does not strike)
                var sword = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Sword");
                var hitbox = cb.weapon;
                if (sword == null || hitbox == null || hitbox.bladeBase == null || hitbox.bladeTip == null) { GKBridge.Log("veteran: the Seljuk has no sword with a hitbox to replace"); return; }
                Transform gripT = sword.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Grip");
                Vector3 gripW = gripT != null ? gripT.position : hitbox.bladeBase.position - (hitbox.bladeTip.position - hitbox.bladeBase.position).normalized * 0.12f;
                Vector3 dir = (hitbox.bladeTip.position - hitbox.bladeBase.position).normalized;
                foreach (var r in sword.GetComponentsInChildren<Renderer>(true)) if (r.GetComponent<TrailRenderer>() == null) r.enabled = false;
                var old = sword.Find("FlangedMace"); if (old != null) Object.DestroyImmediate(old.gameObject);
                var mace = (GameObject)PrefabUtility.InstantiatePrefab(maceModel);
                PrefabUtility.UnpackPrefabInstance(mace, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                mace.name = "FlangedMace";
                // a sibling of the (hidden) sword in the hand, at world scale 1: under the sword it took the sword's fit
                // scale (0.49) and came out half size. The haft's line is read off the model's own HeadTip (origin = the
                // grip), whatever axes the export used
                mace.transform.SetParent(sword.parent, false);
                mace.transform.localScale = Vector3.one / Mathf.Max(1e-6f, sword.parent.lossyScale.x);
                mace.transform.position = gripW; mace.transform.rotation = Quaternion.identity;
                Transform tip0 = mace.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "HeadTip");
                Vector3 haft0 = tip0 != null ? (tip0.position - mace.transform.position).normalized : Vector3.up;
                mace.transform.rotation = Quaternion.FromToRotation(haft0, dir);
                mace.transform.position = gripW;
                foreach (var r in mace.GetComponentsInChildren<MeshRenderer>()) r.sharedMaterials = new[] { haft, grip, iron }.Take(Mathf.Max(1, r.sharedMaterials.Length)).ToArray();
                Transform hb = mace.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "HeadBase");
                Transform ht = mace.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "HeadTip");
                if (hb != null && ht != null) { hitbox.bladeBase.position = hb.position; hitbox.bladeTip.position = ht.position; }
                // no scabbard for a mace: it hangs from the belt by its thong (the sheathed pose keeps the sword's slot)
                foreach (var tr in root.GetComponentsInChildren<Transform>(true))
                    if (tr.name == "Scabbard" || tr.name == "ScabbardPivot")
                        foreach (var r in tr.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                log = "head " + (hb != null && ht != null ? (Vector3.Distance(hb.position, ht.position) * 100f).ToString("F0") + " cm" : "?") +
                      ", grip to head tip " + (ht != null ? (Vector3.Distance(gripW, ht.position) * 100f).ToString("F0") + " cm" : "?");
                PrefabUtility.SaveAsPrefabAsset(root, VeteranPrefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            AssetDatabase.SaveAssets();
            GKBridge.Log("veteran: built " + VeteranPrefabPath + " (" + log + ")");
        }
    }
}
