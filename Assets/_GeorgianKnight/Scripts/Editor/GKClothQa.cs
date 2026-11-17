using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Play-mode locomotion driver for checking the hero's clothes in motion (no enemies, scripted input):
    /// clothqa calm | walk | run | sprint | circle | stop | report.
    /// </summary>
    public static class GKClothQa
    {
        [GKCommand("clothqa", "clothqa calm|walk|run|sprint|circle|back|left|stop|light|heavy|kick|dodge|block|release|home|report: drive the player for cloth checks (play mode)")]
        static void Cmd(string arg)
        {
            if (!Application.isPlaying) { GKBridge.Log("clothqa: play mode only"); return; }
            var p = GameObject.FindWithTag("Player");
            if (p == null) { GKBridge.Log("clothqa: no player"); return; }
            var input = p.GetComponent<GKInput>();
            string a = string.IsNullOrEmpty(arg) ? "report" : arg.Trim();
            switch (a)
            {
                case "calm":
                    foreach (var w in Object.FindObjectsByType<WaveManager>(FindObjectsSortMode.None)) { w.StopAllCoroutines(); w.enabled = false; }
                    int n = 0;
                    foreach (var c in Object.FindObjectsByType<Combatant>(FindObjectsSortMode.None))
                        if (c.team != Team.Player) { Object.Destroy(c.gameObject); n++; }
                    GKBridge.Log("clothqa: waves off, " + n + " enemies removed");
                    break;
                case "walk": input.Inject(GKInput.Injected.Sprint, false); input.InjectMove(new Vector2(0f, 0.45f), true); break;
                case "run": input.Inject(GKInput.Injected.Sprint, false); input.InjectMove(new Vector2(0f, 1f), true); break;
                case "sprint": input.InjectMove(new Vector2(0f, 1f), true); input.Inject(GKInput.Injected.Sprint, true); break;
                case "circle": input.Inject(GKInput.Injected.Sprint, false); input.InjectMove(new Vector2(0.8f, 0.6f), true); break;
                case "stop": input.Inject(GKInput.Injected.Sprint, false); input.InjectMove(Vector2.zero, false); break;
                case "back": input.Inject(GKInput.Injected.Sprint, false); input.InjectMove(new Vector2(0f, -1f), true); break;
                case "left": input.Inject(GKInput.Injected.Sprint, false); input.InjectMove(new Vector2(-1f, 0f), true); break;
                case "light": input.InjectMove(Vector2.zero, false); input.Inject(GKInput.Injected.Light, true); break;
                case "heavy": input.InjectMove(Vector2.zero, false); input.Inject(GKInput.Injected.Heavy, true); break;
                case "kick": input.InjectMove(Vector2.zero, false); input.Inject(GKInput.Injected.Kick, true); break;
                case "dodge": input.Inject(GKInput.Injected.Dodge, true); break;
                case "block": input.InjectMove(Vector2.zero, false); input.Inject(GKInput.Injected.Block, true); break;
                case "jump": input.Inject(GKInput.Injected.Jump, true); break;
                case "knockdown":
                    p.GetComponent<Combatant>().ReceiveHit(new HitData
                    {
                        damage = 1f, reaction = HitReaction.Knockdown, knockback = 1f,
                        point = p.transform.position + p.transform.forward + Vector3.up, direction = -p.transform.forward
                    });
                    break;
                case "release":
                    foreach (GKInput.Injected b in System.Enum.GetValues(typeof(GKInput.Injected))) input.Inject(b, false);
                    break;
                case "home":
                {
                    var ctl = p.GetComponent<CharacterController>();
                    if (ctl != null) ctl.enabled = false;
                    p.transform.position = new Vector3(0f, 0.05f, -4f);
                    if (ctl != null) ctl.enabled = true;
                    break;
                }
            }
            Report(p);
        }

        [GKCommand("scabqa", "scabqa: how the scabbard hangs now (throat vs coat profile, hang angles, solver push) (play mode)")]
        static void ScabQa(string arg)
        {
            var p = GameObject.FindWithTag("Player");
            var m = p != null ? p.GetComponent<ScabbardMotion>() : null;
            if (m == null || m.hips == null || m.scabbard == null) { GKBridge.Log("scabqa: no scabbard"); return; }
            var hips = m.hips;
            Vector3 up = hips.TransformDirection(m.bodyUpLocal).normalized;
            Vector3 fwd = Vector3.ProjectOnPlane(hips.TransformDirection(m.bodyFwdLocal), up).normalized;
            Vector3 right = Vector3.Cross(up, fwd);
            Vector3 throat = m.scabbard.position;
            Vector3 dir = m.scabbard.forward;
            Vector3 d = throat - hips.position;
            float h = Vector3.Dot(d, up);
            Vector3 radial = d - up * h;
            string s = "scabqa: throat lateral " + Vector3.Dot(d, -right).ToString("F3") + " fwd " + Vector3.Dot(d, fwd).ToString("F3") + " up " + h.ToString("F3") +
                       " radial " + radial.magnitude.ToString("F3");
            if (m.skirtRadius != null && m.skirtRadius.Length > 0)
            {
                int bins = m.skirtRadius.Length / Mathf.Max(1, m.skirtSectors);
                float ang = Mathf.Atan2(Vector3.Dot(radial, right), Vector3.Dot(radial, fwd)) * Mathf.Rad2Deg; if (ang < 0f) ang += 360f;
                int sector = Mathf.Clamp(Mathf.FloorToInt(ang / (360f / m.skirtSectors)), 0, m.skirtSectors - 1);
                s += " | skirt at throat sector " + sector + ":";
                for (int b = 0; b < bins; b++) s += " " + m.skirtRadius[b * m.skirtSectors + sector].ToString("F2");
                s += " (bins of " + m.skirtStep.ToString("F2") + " m from " + m.skirtTop.ToString("F2") + "), needs +" + (m.radius + m.clearance).ToString("F3");
            }
            else s += " | no skirt profile";
            s += " | hang: " + Vector3.Angle(dir, -up).ToString("F0") + " deg from down, out " + Vector3.Dot(dir, -right).ToString("F2") + " fwd " + Vector3.Dot(dir, fwd).ToString("F2") +
                 " down " + Vector3.Dot(dir, -up).ToString("F2") + " | follow " + m.LastFollow.ToString("F0") + " swing " + m.LastSwing.ToString("F0") +
                 " residual " + m.LastResidual.ToString("F3") + " clearance " + m.LastClearance.ToString("F3") + " | sword " +
                 (p.GetComponent<WeaponHolster>() != null ? (p.GetComponent<WeaponHolster>().IsDrawn ? "drawn" : "sheathed") : "?");
            GKBridge.Log(s);
        }

        static int hqContacts, hqSwings;
        static System.Text.StringBuilder hqLog = new System.Text.StringBuilder();
        static Combatant hqDummy;

        /// <summary>hitqa dummy [dist] [angle]: a Seljuk who stands still (AI off, cannot die) at dist m, angle deg off the
        /// hero's facing; hitqa report: swings vs blade contacts since the dummy was placed.</summary>
        [GKCommand("hitqa", "hitqa dummy [dist] [angleDeg] | report: does the sword connect? (play mode)")]
        static void HitQa(string arg)
        {
            var p = GameObject.FindWithTag("Player");
            if (p == null) { GKBridge.Log("hitqa: no player"); return; }
            var me = p.GetComponent<Combatant>();
            var parts = (arg ?? "").Split(' ');
            if (parts[0] == "dummy")
            {
                float dist = parts.Length > 1 ? float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 2.2f;
                float ang = parts.Length > 2 ? float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) : 0f;
                if (hqDummy != null) Object.Destroy(hqDummy.gameObject);
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(GKGameBuilder.EnemyPrefabPath);
                Vector3 dir = Quaternion.Euler(0f, ang, 0f) * Vector3.ProjectOnPlane(p.transform.forward, Vector3.up).normalized;
                var go = Object.Instantiate(prefab, p.transform.position + dir * dist, Quaternion.LookRotation(-dir));
                foreach (var ai in go.GetComponents<EnemyAI>()) ai.enabled = false;
                hqDummy = go.GetComponent<Combatant>();
                var st = go.GetComponent<CombatStats>();
                if (st != null) { st.maxHealth = 1e6f; st.maxPoise = 1e6f; st.ResetAll(); }
                hqContacts = 0; hqSwings = 0; hqLog.Length = 0;
                me.HitDealt -= OnDealt; me.HitDealt += OnDealt;
                me.AttackStarted -= OnSwing; me.AttackStarted += OnSwing;
                me.SwingStarted -= OnSwingOpen; me.SwingStarted += OnSwingOpen;
                me.SwingEnded -= OnSwingClose; me.SwingEnded += OnSwingClose;
                hqMe = me;
                UnityEditor.EditorApplication.update -= TrackSwing; UnityEditor.EditorApplication.update += TrackSwing;
                GKBridge.Log("hitqa: dummy at " + dist + " m, " + ang + " deg");
                return;
            }
            FlushAttack();
            GKBridge.Log("hitqa: " + hqContacts + " contacts / " + hqSwings + " swings | " + hqLog);
        }

        static void OnDealt(Combatant target, HitData hit, HitResult res)
        {
            hqContacts++;
            hqLog.Append((hit.attack != null ? hit.attack.name : "?") + ":" + res + " ");
        }

        static void OnSwing(AttackData a)
        {
            hqSwings++;
            FlushAttack();
            hqAtkAll = a; hqAllGap = float.MaxValue; hqAllAt = 0f;
        }
        static AttackData hqAtkAll; static float hqAllGap, hqAllAt;
        static void FlushAttack()
        {
            if (hqAtkAll == null) return;
            hqLog.Append("{" + hqAtkAll.name + " whole move: closest " + (hqAllGap * 100f).ToString("F0") + " cm at " + (hqAllAt * 100f).ToString("F0") +
                         "%, window " + (hqAtkAll.hitStart * 100f).ToString("F0") + "-" + (hqAtkAll.hitEnd * 100f).ToString("F0") + "%} ");
            hqAtkAll = null;
        }

        // blade-to-body gap during each open swing: how far the edge passed from the dummy when it missed
        static Combatant hqMe; static bool hqOpen; static float hqGap; static string hqAtk; static float hqDistAtOpen;
        static void OnSwingOpen(AttackData a)
        {
            hqOpen = true; hqGap = float.MaxValue; hqAtk = a != null ? a.name : "?";
            hqDistAtOpen = hqDummy != null && hqMe != null ? Vector3.Distance(GK.Flat(hqMe.transform.position), GK.Flat(hqDummy.transform.position)) : -1f;
        }
        static void OnSwingClose()
        {
            if (!hqOpen) return;
            hqOpen = false;
            hqLog.Append("[" + hqAtk + " start " + hqDistAtOpen.ToString("F2") + " m, closest edge " + (hqGap * 100f).ToString("F0") + " cm] ");
        }
        static void TrackSwing()
        {
            if (!Application.isPlaying || hqMe == null || hqDummy == null || hqMe.weapon == null) return;
            if (hqAtkAll != null)
            {
                if (hqMe.CurrentState != Combatant.State.Attacking) FlushAttack();
                else
                {
                    float d = BladeGap(hqMe, hqDummy);
                    if (d < hqAllGap) { hqAllGap = d; hqAllAt = hqMe.ActionProgress; }
                }
            }
            if (!hqOpen) return;
            float g = BladeGap(hqMe, hqDummy);
            if (g < hqGap) hqGap = g;
        }

        // ------------------------------------------------------------------ attack window calibration
        static System.Collections.Generic.List<AttackData> calList;
        static int calIdx, calPhase, calTry;
        static float calWait, calFirst, calLast, calMinGap, calMinAt, calBaseRange, calDistAtHit;
        static Combatant calSavedTarget;
        // ideal-range offsets tried in turn until the blade really passes through the body (0 = the current value)
        static readonly float[] calOffsets = { 0f, -0.2f, 0.2f, -0.4f, 0.4f, -0.6f, 0.6f };
        static System.Action<Combatant, Hurtbox, Vector3, Vector3> calSavedContact;
        static System.Text.StringBuilder calOut;
        /// <summary>Blade-to-body distance that counts as contact while calibrating: what the hitbox registers on the
        /// intended target in game (sphere 6 cm + assist 10 cm, kept a little inside that).</summary>
        // contact for the calibration (enemycal contact=<cm>): the blade's 7 cm sphere by default, or the hitbox's delivery
        // gap when calibrating for delivery at contact (the Lead: 4 cm, per attack)
        static float CalContact = 0.07f;
        public const string CalibrationPath = "Assets/_GeorgianKnight/Generated/attack_calibration.txt";

        /// <summary>attackcal: plays every player attack on the real hero against a standing dummy, records when the
        /// blade actually passes through the body, and sets each hit window to that (also written to
        /// Generated/attack_calibration.txt, which the combat builder applies on every rebuild).</summary>
        [GKCommand("attackcal", "attackcal: measure each player attack's real contact window on the hero and set its hit window (play mode)")]
        static void AttackCal(string arg)
        {
            CalContact = 0.07f; calOnly = null;   // the player's calibration keeps the blade's sphere (enemycal's options do not carry over)
            var p = GameObject.FindWithTag("Player");
            var me = p != null ? p.GetComponent<Combatant>() : null;
            if (me == null || me.moves == null) { GKBridge.Log("attackcal: no player / moves"); return; }
            calList = new System.Collections.Generic.List<AttackData>();
            var queue = new System.Collections.Generic.Queue<AttackData>();
            foreach (var a in new[] { me.moves.lightStart, me.moves.heavyStart, me.moves.sprintAttack, me.moves.riposte, me.moves.kick })
                if (a != null) queue.Enqueue(a);
            while (queue.Count > 0)
            {
                var a = queue.Dequeue();
                if (a == null || calList.Contains(a)) continue;
                calList.Add(a);
                queue.Enqueue(a.nextLight); queue.Enqueue(a.nextHeavy);
            }
            HitQa("dummy 1.8 0");
            // nothing else in the arena while measuring: practice waves stop and their fighters leave
            foreach (var w in Object.FindObjectsByType<WaveManager>(FindObjectsSortMode.None)) { w.StopAllCoroutines(); w.enabled = false; }
            foreach (var c in Object.FindObjectsByType<Combatant>(FindObjectsSortMode.None))
                if (c != me && c != hqDummy && c.team != Team.Player) Object.Destroy(c.gameObject);
            if (UnityEditor.EditorApplication.isPaused) UnityEditor.EditorApplication.isPaused = false;
            calSavedContact = me.weapon != null ? me.weapon.OnContact : null;
            if (me.weapon != null) me.weapon.OnContact = (t, h, pt, d) => { };   // measure only: no damage, no reactions
            if (me.offWeapon != null) { calSavedOffContact = me.offWeapon.OnContact; me.offWeapon.OnContact = (t, h, pt, d) => { }; }
            hqMe = me;
            calSavedTarget = me.Target;
            calIdx = 0; calPhase = 0; calTry = 0; calOut = new System.Text.StringBuilder();
            UnityEditor.EditorApplication.update -= CalTick; UnityEditor.EditorApplication.update += CalTick;
            GKBridge.Log("attackcal: " + calList.Count + " attacks");
        }

        // ------------------------------------------------------------------ chainqa (combo chains against a live foe)

        class ChainSwing { public string name; public float dist; public string foeState; public bool hit; public string result = ""; public float gap = float.MaxValue; public bool crit; }
        static readonly System.Collections.Generic.List<ChainSwing> cqSwings = new System.Collections.Generic.List<ChainSwing>();
        static bool cqSide;
        static string cqSeq; static int cqIdx; static float cqDeadline, cqQuietSince; static Combatant cqFoe; static string cqName;

        /// <summary>chainqa &lt;sequence&gt;: a Seljuk who reacts and is pushed back as in a fight but cannot die (AI off) stands
        /// 2 m in front; the hero plays the sequence (L light, H heavy, K kick) pressing each button the moment the next
        /// move is allowed (a player mashing), and every swing is logged: connected or not, how far the blade passed,
        /// the distance and the foe's state when it began, critical or not.</summary>
        [GKCommand("chainqa", "chainqa <LLLLHH...> [name]: play a button sequence against a reacting, undying Seljuk and log every swing (play mode)")]
        static void ChainQa(string arg)
        {
            var p = GameObject.FindWithTag("Player");
            var me = p != null ? p.GetComponent<Combatant>() : null;
            if (me == null) { GKBridge.Log("chainqa: no player"); return; }
            var parts = (arg ?? "").Trim().Split(' ');
            cqSeq = parts[0].ToUpperInvariant(); cqName = parts.Length > 1 ? parts[1] : cqSeq;
            if (cqSeq.Length == 0) { GKBridge.Log("chainqa: give a sequence, e.g. LLLL or HHH"); return; }
            foreach (var w in Object.FindObjectsByType<WaveManager>(FindObjectsSortMode.None)) { w.StopAllCoroutines(); w.enabled = false; }
            foreach (var c in Object.FindObjectsByType<Combatant>(FindObjectsSortMode.None))
                if (c != me && c.team != Team.Player) Object.Destroy(c.gameObject);
            if (UnityEditor.EditorApplication.isPaused) UnityEditor.EditorApplication.isPaused = false;
            var motor = me.GetComponent<CharacterMotor>();
            Vector3 home = new Vector3(0f, 0.05f, -4f);
            if (motor != null) motor.Teleport(home, Quaternion.identity);
            if (me.Stats != null) me.Stats.ResetAll();
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(GKGameBuilder.EnemyPrefabPath);
            // "side": the foe at the knight's right shoulder and no lock-on (an unlocked swing must still find him)
            cqSide = parts.Length > 2 && parts[2] == "side";
            Vector3 at = cqSide ? home + Vector3.right * 1.8f : home + Vector3.forward * 2f;
            var go = Object.Instantiate(prefab, at, Quaternion.LookRotation(GK.Flat(home - at).normalized));
            foreach (var ai in go.GetComponents<EnemyAI>()) ai.enabled = false;
            cqFoe = go.GetComponent<Combatant>();
            var st = go.GetComponent<CombatStats>();
            if (st != null) { st.maxHealth = 1e6f; st.ResetAll(); }     // poise as usual: he reels and falls as in a fight
            me.Target = cqSide ? null : cqFoe;
            cqSwings.Clear(); cqIdx = 0; cqDeadline = Time.realtimeSinceStartup + 60f; cqQuietSince = -1f;
            me.HitDealt -= CqDealt; me.HitDealt += CqDealt;
            me.AttackStarted -= CqStarted; me.AttackStarted += CqStarted;
            hqMe = me;
            UnityEditor.EditorApplication.update -= CqTick; UnityEditor.EditorApplication.update += CqTick;
            GKBridge.Log("chainqa: " + cqName + " (" + cqSeq + ") against a reacting Seljuk at 2 m");
        }

        static void CqStarted(AttackData a)
        {
            if (cqFoe == null || hqMe == null) return;
            cqSwings.Add(new ChainSwing
            {
                name = a != null ? a.name : "?",
                dist = Vector3.Distance(GK.Flat(hqMe.transform.position), GK.Flat(cqFoe.transform.position)),
                foeState = cqFoe.CurrentState.ToString()
            });
        }

        static void CqDealt(Combatant target, HitData hit, HitResult r)
        {
            if (target != cqFoe || cqSwings.Count == 0) return;
            var s = cqSwings[cqSwings.Count - 1];
            s.hit = true; s.result = r.ToString(); s.crit = hit.critical;
        }

        static void CqTick()
        {
            var me = hqMe;
            if (!Application.isPlaying || me == null || cqFoe == null) { UnityEditor.EditorApplication.update -= CqTick; return; }
            if (me.CurrentState == Combatant.State.Attacking && cqSwings.Count > 0)
            {
                var s = cqSwings[cqSwings.Count - 1];
                s.gap = Mathf.Min(s.gap, BladeGap(me, cqFoe));
            }
            if (me.Stats != null) me.Stats.ResetAll();          // stamina never decides the test
            if (cqIdx < cqSeq.Length)
            {
                me.Target = cqSide ? null : cqFoe;
                char c = cqSeq[cqIdx];
                // 'J': a jump (the next press is made while he is in the air, as a player mashes)
                bool ok = c == 'J' ? me.RequestJump(false) : c == 'K' ? me.RequestKick() : me.RequestAttack(c == 'H' ? AttackKind.Heavy : AttackKind.Light, false);
                if (ok) cqIdx++;
                cqQuietSince = -1f;
            }
            else if (me.CurrentState == Combatant.State.Free)
            {
                if (cqQuietSince < 0f) cqQuietSince = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - cqQuietSince > 0.6f) CqFinish();
            }
            if (Time.realtimeSinceStartup > cqDeadline) CqFinish();
        }

        static void CqFinish()
        {
            UnityEditor.EditorApplication.update -= CqTick;
            if (hqMe != null) { hqMe.HitDealt -= CqDealt; hqMe.AttackStarted -= CqStarted; }
            int hits = cqSwings.Count(s => s.hit);
            var sb = new System.Text.StringBuilder("chainqa " + cqName + ": " + hits + "/" + cqSwings.Count + " connected |");
            foreach (var s in cqSwings)
                sb.Append(' ').Append(s.name).Append(s.hit ? " " + s.result + (s.crit ? " CRIT" : "") : " MISS by " + (s.gap * 100f).ToString("F0") + " cm")
                  .Append(" (at ").Append(s.dist.ToString("F1")).Append(" m, foe ").Append(s.foeState).Append(") |");
            GKBridge.Log(sb.ToString());
            if (cqFoe != null) Object.Destroy(cqFoe.gameObject);
            cqFoe = null;
        }

        static Combatant calEnemy;   // enemycal: the Seljuk measuring his swings (removed when done)
        static System.Collections.Generic.HashSet<string> calOnly;   // enemycal only=: just these attacks
        static int calBackoff; static bool calKeepBase; static float calAxisMin;
        // the closest range that touched before the main strike, and the nearest beyond it that missed: the range is
        // bisected between them (lease J: E_A4_Swing stepped 1.10 / 1.30 m back and forth, then kept a 6 % window)
        static float calEarlyMax = -1f, calMissMin = float.MaxValue;

        /// <summary>Where the clip's main strike begins (normalized): a combo's first cut, else its longest fast run (the
        /// warrior's swing whips at 8-10 % in the wind-up and cuts at 18-30 %).</summary>
        static float MainStrike(AttackData a)
        {
            var runs = GKCombatBuilder.StrikeRuns(a.name);
            if (runs.Count == 0) return 0.12f;
            if (a.name.Contains("Combo")) return runs[0].x;
            var best = runs[0];
            foreach (var r in runs) if (r.y - r.x > best.y - best.x) best = r;
            return best.x;
        }

        /// <summary>Closest distance from the cutting blade to the target's spine (hips to neck).</summary>
        static float AxisGap(Combatant me, Combatant target)
        {
            var anim = target.GetComponent<Animator>();
            Transform hips = anim != null && anim.isHuman ? anim.GetBoneTransform(HumanBodyBones.Hips) : null;
            Transform neck = anim != null && anim.isHuman ? anim.GetBoneTransform(HumanBodyBones.Neck) : null;
            if (hips == null || neck == null) return 0f;
            var a0 = me.CurrentAttack;
            WeaponHand hand = a0 != null && me.offWeapon != null && a0.kind != AttackKind.Kick ? a0.hand : WeaponHand.Main;
            float best = float.MaxValue;
            foreach (var w in new[] { hand != WeaponHand.Off ? me.weapon : null, hand != WeaponHand.Main ? me.offWeapon : null })
            {
                if (w == null) continue;
                for (int sgi = 0; sgi < w.SegmentCount; sgi++)
                {
                    Vector3 a, b;
                    if (!w.GetSegment(sgi, out a, out b)) continue;
                    Vector3 pS, pC;
                    ScabbardMotion.ClosestPointsSegments(a, b, hips.position, neck.position, out pS, out pC);
                    best = Mathf.Min(best, Vector3.Distance(pS, pC));
                }
            }
            return best;
        }

        [GKCommand("meshverts", "meshverts <model path> <mesh name>: vertices Unity keeps for a mesh against those its triangles use (loose vertices cost GPU memory and skinning)")]
        static void MeshVerts(string arg)
        {
            var parts = (arg ?? "").Trim().Split(' ');
            if (parts.Length < 2) { GKBridge.Log("meshverts: give a model path and a mesh name"); return; }
            foreach (var o in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(parts[0]))
            {
                var m = o as Mesh;
                if (m == null || m.name != parts[1]) continue;
                var used = new HashSet<int>();
                for (int s = 0; s < m.subMeshCount; s++) foreach (int i in m.GetTriangles(s)) used.Add(i);
                GKBridge.Log("meshverts: " + m.name + " keeps " + m.vertexCount + " vertices, triangles use " + used.Count + ", loose " + (m.vertexCount - used.Count) +
                             " (" + m.subMeshCount + " submeshes, " + m.blendShapeCount + " blend shapes)");
                return;
            }
            GKBridge.Log("meshverts: no mesh " + parts[1] + " in " + parts[0]);
        }

        [GKCommand("bridge", "bridge on|off: the chain bridge (L2 -> L3 of the shield chain) for A/B takes (play mode)")]
        static void Bridge(string arg)
        {
            ChainBridge.Enabled = (arg ?? "").Trim() != "off";
            GKBridge.Log("bridge: " + (ChainBridge.Enabled ? "on" : "off") + " (" + Object.FindObjectsByType<ChainBridge>(FindObjectsSortMode.None).Length + " bridges)");
        }

        [GKCommand("bladeahead", "bladeahead <seconds>: the blade solver's look-ahead on every sword in the scene (0 = off; A/B takes, play mode)")]
        static void BladeAhead(string arg)
        {
            float s; if (!float.TryParse((arg ?? "").Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out s)) s = 0.1f;
            int n = 0;
            foreach (var w in Object.FindObjectsByType<WeaponSelfCollision>(FindObjectsInactive.Include, FindObjectsSortMode.None)) { w.anticipate = s; n++; }
            GKBridge.Log("bladeahead: " + s.ToString("F2") + " s on " + n + " blade solvers");
        }

        [GKCommand("enemycal", "enemycal [veteran|archer|prefab path]: a Seljuk (AI off; the warrior unless named) swings each of his attacks at the standing hero; measures the real contact window and range and sets them (play mode)")]
        static void EnemyCal(string arg)
        {
            var p = GameObject.FindWithTag("Player");
            var hero = p != null ? p.GetComponent<Combatant>() : null;
            if (hero == null) { GKBridge.Log("enemycal: no player"); return; }
            // nothing else in the arena: practice waves stop and their fighters leave
            foreach (var w in Object.FindObjectsByType<WaveManager>(FindObjectsSortMode.None)) { w.StopAllCoroutines(); w.enabled = false; }
            foreach (var c in Object.FindObjectsByType<Combatant>(FindObjectsSortMode.None))
                if (c != hero && c.team != Team.Player) Object.Destroy(c.gameObject);
            if (UnityEditor.EditorApplication.isPaused) UnityEditor.EditorApplication.isPaused = false;
            // enemycal [veteran|archer|prefab] [only=A,B] [contact=<cm>]
            string which = "";
            System.Collections.Generic.HashSet<string> only = null;
            CalContact = 0.07f;
            foreach (var tok in (arg ?? "").Trim().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                float cm;
                if (tok.StartsWith("only=")) only = new System.Collections.Generic.HashSet<string>(tok.Substring(5).Split(','));
                else if (tok.StartsWith("contact=") && float.TryParse(tok.Substring(8), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cm)) CalContact = cm / 100f;
                else which = tok;
            }
            calOnly = only;
            string path = which == "veteran" ? GKCombatBuilder.VeteranPrefabPath : which == "archer" ? GKCombatBuilder.ArcherPrefabPath :
                          which.Length > 0 ? which : GKGameBuilder.EnemyPrefabPath;
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) { GKBridge.Log("enemycal: no enemy prefab " + path); return; }
            var go = Object.Instantiate(prefab, new Vector3(0f, 0.05f, -4f), Quaternion.identity);
            var archer = go.GetComponent<ArcherAI>(); if (archer != null) archer.enabled = false;
            var foe = go.GetComponent<Combatant>();
            if (foe == null || foe.moves == null) { Object.Destroy(go); GKBridge.Log("enemycal: enemy without moves"); return; }
            // a puppet: no AI, no path-following; he only swings when told
            var ai = go.GetComponent<EnemyAI>(); if (ai != null) ai.enabled = false;
            var agent = go.GetComponent<UnityEngine.AI.NavMeshAgent>(); if (agent != null) agent.enabled = false;
            calList = new System.Collections.Generic.List<AttackData>();
            var queue = new System.Collections.Generic.Queue<AttackData>();
            var m = foe.moves;
            foreach (var a in m.aiAttacks ?? new AttackData[0]) queue.Enqueue(a);
            foreach (var a in m.aiUnblockables ?? new AttackData[0]) queue.Enqueue(a);
            foreach (var a in new[] { m.lightStart, m.heavyStart, m.kick }) queue.Enqueue(a);
            while (queue.Count > 0)
            {
                var a = queue.Dequeue();
                if (a == null || calList.Contains(a)) continue;
                calList.Add(a);
                queue.Enqueue(a.nextLight); queue.Enqueue(a.nextHeavy);
            }
            // the hero stands still and takes nothing: the Seljuk's blows only measure
            calEnemy = foe;
            hqMe = foe; hqDummy = hero;
            calSavedContact = foe.weapon != null ? foe.weapon.OnContact : null;
            if (foe.weapon != null) foe.weapon.OnContact = (t, h, pt, d) => { };
            calSavedOffContact = null;
            if (foe.offWeapon != null) { calSavedOffContact = foe.offWeapon.OnContact; foe.offWeapon.OnContact = (t, h, pt, d) => { }; }
            calSavedTarget = null;
            if (calOnly != null) calList.RemoveAll(a => a == null || !calOnly.Contains(a.name));
            GKBridge.Log("enemycal: " + calList.Count + " attacks" + (calOnly != null ? " (only " + string.Join(",", calOnly) + ")" : "") + ", contact " + (CalContact * 100f).ToString("F1") + " cm");
            calIdx = 0; calPhase = 0; calTry = 0; calOut = new System.Text.StringBuilder();
            UnityEditor.EditorApplication.update -= CalTick; UnityEditor.EditorApplication.update += CalTick;
            GKBridge.Log("enemycal: " + calList.Count + " attacks of " + m.name);
        }

        static void CalTick()
        {
            if (!Application.isPlaying || hqMe == null || hqDummy == null) { UnityEditor.EditorApplication.update -= CalTick; return; }
            var me = hqMe;
            switch (calPhase)
            {
                case 0:
                {
                    if (calIdx >= calList.Count) { CalFinish(); return; }
                    if (me.CurrentState != Combatant.State.Free) return;
                    var a = calList[calIdx];
                    Vector3 home = new Vector3(0f, 0.05f, -4f);
                    var motor = me.GetComponent<CharacterMotor>();
                    if (motor != null) motor.Teleport(home, Quaternion.identity);
                    // try this ideal range: the warp holds the hero at ideal range + half the target's body
                    if (calTry == 0 && !calKeepBase) calBaseRange = a.idealRange;
                    calKeepBase = false;
                    a.idealRange = Mathf.Clamp(calBaseRange + calOffsets[calTry], 0.65f, 2.4f);
                    float dist = a.idealRange + hqDummy.bodyRadius * 0.5f;
                    me.Target = hqDummy;
                    var dm = hqDummy.GetComponent<CharacterMotor>();
                    Vector3 dp = home + Vector3.forward * dist;
                    if (dm != null) dm.Teleport(dp, Quaternion.LookRotation(Vector3.back)); else hqDummy.transform.SetPositionAndRotation(dp, Quaternion.LookRotation(Vector3.back));
                    if (me.Stats != null) me.Stats.ResetAll();
                    calWait = Time.realtimeSinceStartup + 0.5f;
                    calPhase = 1;
                    break;
                }
                case 1:
                    if (Time.realtimeSinceStartup < calWait) return;
                    calFirst = -1f; calLast = -1f; calMinGap = float.MaxValue; calMinAt = 0f; calDistAtHit = -1f; calAxisMin = float.MaxValue;
                    me.Target = hqDummy;
                    if (!me.StartAttack(calList[calIdx])) { calOut.Append(calList[calIdx].name + " could not start; "); calIdx++; calPhase = 0; return; }
                    calPhase = 2;
                    break;
                case 2:
                    if (me.CurrentState == Combatant.State.Attacking || me.CurrentState == Combatant.State.Executing)
                    {
                        float gap = BladeGap(me, hqDummy);
                        if (gap < calMinGap) { calMinGap = gap; calMinAt = me.ActionProgress; }
                        if (me.ActionProgress >= 0.12f) calAxisMin = Mathf.Min(calAxisMin, AxisGap(me, hqDummy));
                        if (calDistAtHit < 0f && me.ActionProgress >= calList[calIdx].hitStart)
                            calDistAtHit = Vector3.Distance(GK.Flat(me.transform.position), GK.Flat(hqDummy.transform.position));
                        if (gap < CalContact)
                        {
                            if (calFirst < 0f) calFirst = me.ActionProgress;
                            calLast = me.ActionProgress;
                        }
                        return;
                    }
                    var at = calList[calIdx];
                    string tried = " (ideal " + at.idealRange.ToString("F2") + " m, at window start " + calDistAtHit.ToString("F2") + " m)";
                    // touching from the wind-up on: he stood inside his own swing (a blow landing before it is thrown);
                    // step back and measure again
                    // the blow must go through the body, not graze its edge: a swing whose blade passes the target's
                    // spine further than most of its body radius reads as a cut in the air beside him (step in); one
                    // touching from the very start of the clip is thrown from inside the target (step back)
                    float strikeFloor = Mathf.Max(0.12f, MainStrike(at) - 0.05f);
                    if (calFirst >= 0f && calBackoff < 6)
                    {
                        float deep = hqDummy.bodyRadius * 0.6f;
                        bool early = calFirst < strikeFloor;
                        bool graze = calAxisMin > deep;
                        if (early || graze)
                        {
                            float move = early ? 0.2f : -0.12f;
                            if (early)
                            {
                                calEarlyMax = Mathf.Max(calEarlyMax, at.idealRange);
                                if (calMissMin < float.MaxValue) move = (calEarlyMax + calMissMin) * 0.5f - at.idealRange;
                            }
                            GKBridge.Log("attackcal: " + at.name + (early ? " touches from " + (calFirst * 100f).ToString("F0") + "% (inside the swing)" : " only grazes (blade " + (calAxisMin * 100f).ToString("F0") + " cm from the spine, needs " + (deep * 100f).ToString("F0") + ")") + tried + (early ? " - 20 cm back" : " - 12 cm closer"));
                            calBaseRange = Mathf.Clamp(at.idealRange + move, 0.65f, 2.4f); calBackoff++; calTry = 0; calPhase = 0;
                            calKeepBase = true;
                            return;
                        }
                    }
                    // a miss beyond a range that touched too early: try half way between them
                    if (calFirst < 0f && calEarlyMax > 0f && at.idealRange > calEarlyMax && calBackoff < 6 && at.idealRange - calEarlyMax > 0.03f)
                    {
                        calMissMin = Mathf.Min(calMissMin, at.idealRange);
                        GKBridge.Log("attackcal: " + at.name + " missed by " + (calMinGap * 100f).ToString("F0") + " cm at " + (calMinAt * 100f).ToString("F0") + "%" + tried + " - half way to " + calEarlyMax.ToString("F2") + " m");
                        calBaseRange = (calEarlyMax + calMissMin) * 0.5f; calBackoff++; calTry = 0; calPhase = 0; calKeepBase = true;
                        return;
                    }
                    if (calFirst >= 0f)
                    {
                        // never a window inside the wind-up, even when the range search ran out
                        float hs = Mathf.Clamp(Mathf.Max(calFirst - 0.015f, strikeFloor - 0.03f), 0.05f, 0.85f);
                        float he = Mathf.Clamp(calLast + 0.02f, hs + 0.05f, 0.95f);
                        var ci = System.Globalization.CultureInfo.InvariantCulture;
                        calOut.Append(at.name + " " + hs.ToString("F3", ci) + " " + he.ToString("F3", ci) + " " + at.idealRange.ToString("F3", ci) + "\n");
                        GKBridge.Log("attackcal: " + at.name + " window " + (at.hitStart * 100f).ToString("F0") + "-" + (at.hitEnd * 100f).ToString("F0") + "% -> " + (hs * 100f).ToString("F0") + "-" + (he * 100f).ToString("F0") + "%" + tried + (calTry > 0 ? " after " + calTry + " other ranges" : ""));
                        ApplyWindow(at, hs, he);
                        UnityEditor.EditorUtility.SetDirty(at);
                    }
                    else if (calTry + 1 < calOffsets.Length)
                    {
                        GKBridge.Log("attackcal: " + at.name + " missed by " + (calMinGap * 100f).ToString("F0") + " cm at " + (calMinAt * 100f).ToString("F0") + "%" + tried);
                        calTry++; calPhase = 0;
                        return;
                    }
                    else
                    {
                        at.idealRange = calBaseRange;
                        GKBridge.Log("attackcal: " + at.name + " never touched the dummy at any range (last closest " + (calMinGap * 100f).ToString("F0") + " cm) - kept " + (at.hitStart * 100f).ToString("F0") + "-" + (at.hitEnd * 100f).ToString("F0") + "%, ideal " + calBaseRange.ToString("F2"));
                    }
                    calIdx++; calPhase = 0; calTry = 0; calBackoff = 0; calEarlyMax = -1f; calMissMin = float.MaxValue;
                    break;
            }
        }

        public static void ApplyWindow(AttackData a, float hs, float he)
        {
            a.hitStart = hs; a.hitEnd = he;
            bool light = a.kind == AttackKind.Light || a.kind == AttackKind.Riposte;
            a.comboWindow = Mathf.Clamp(he + (light ? 0.02f : 0.06f), 0f, 0.95f);
            a.dodgeCancel = Mathf.Clamp(he + 0.06f, 0f, 0.95f);
            a.end = Mathf.Clamp(Mathf.Max(a.end, he + 0.12f), he + 0.1f, 0.97f);
            a.steerUntil = Mathf.Clamp(hs * 0.75f, 0.05f, 0.5f);
            UnityEditor.EditorUtility.SetDirty(a);
        }

        static void CalFinish()
        {
            UnityEditor.EditorApplication.update -= CalTick;
            if (hqMe != null && hqMe.weapon != null) hqMe.weapon.OnContact = calSavedContact;
            if (hqMe != null && hqMe.offWeapon != null && calSavedOffContact != null) hqMe.offWeapon.OnContact = calSavedOffContact;
            if (hqMe != null) hqMe.Target = calSavedTarget;
            // one run measures one style: keep the other styles' lines, replace this run's
            var merged = new System.Collections.Generic.List<string>();
            var fresh = calOut.ToString().Split(new[] { '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
            var names = new System.Collections.Generic.HashSet<string>();
            foreach (var l in fresh) names.Add(l.Split(' ')[0]);
            if (System.IO.File.Exists(CalibrationPath))
                foreach (var l in System.IO.File.ReadAllLines(CalibrationPath))
                    if (l.Trim().Length > 0 && !names.Contains(l.Split(' ')[0])) merged.Add(l);
            merged.AddRange(fresh);
            System.IO.File.WriteAllText(CalibrationPath, string.Join("\n", merged) + "\n");
            UnityEditor.AssetDatabase.SaveAssets();
            if (calEnemy != null) { Object.Destroy(calEnemy.gameObject); calEnemy = null; hqDummy = null; }
            GKBridge.Log("attackcal: done, " + CalibrationPath);
        }

        /// <summary>Closest distance from the striking segments (blade, or the legs in a kick) to the target's body.</summary>
        static System.Action<Combatant, Hurtbox, Vector3, Vector3> calSavedOffContact;

        internal static float BladeGap(Combatant me, Combatant target)
        {
            // the blade that cuts in this swing: a left-hand (mirrored) cut measures the second sword, a two-blade cut
            // the nearer of the two
            var a0 = me.CurrentAttack;
            WeaponHand hand = a0 != null && me.offWeapon != null && a0.kind != AttackKind.Kick ? a0.hand : WeaponHand.Main;
            float best = float.MaxValue;
            if (hand != WeaponHand.Off) best = Mathf.Min(best, BladeGap(me.weapon, target));
            if (hand != WeaponHand.Main) best = Mathf.Min(best, BladeGap(me.offWeapon, target));
            return best;
        }

        static float BladeGap(WeaponHitbox w, Combatant target)
        {
            if (w == null) return float.MaxValue;
            float best = float.MaxValue;
            var boxes = target.GetComponentsInChildren<Hurtbox>();
            for (int s = 0; s < w.SegmentCount; s++)
            {
                Vector3 a, b;
                if (!w.GetSegment(s, out a, out b)) continue;
                foreach (var hb in boxes)
                {
                    var c = hb.GetComponent<Collider>();
                    if (c == null || !c.enabled) continue;   // the ragdoll's colliders are off while alive
                    for (int i = 0; i <= 8; i++)
                    {
                        Vector3 q = Vector3.Lerp(a, b, i / 8f);
                        float d = Vector3.Distance(c.ClosestPoint(q), q);
                        if (d < best) best = d;
                    }
                }
            }
            return best;
        }

        [GKCommand("gripqa", "gripqa: where the fingers sit on the sword handle, measured along the blade from the cross-guard (play mode)")]
        static void GripQa(string arg)
        {
            var p = GameObject.FindWithTag("Player");
            var a = p != null ? p.GetComponent<Animator>() : null;
            var hand = a != null ? a.GetBoneTransform(HumanBodyBones.RightHand) : null;
            var sword = hand != null ? hand.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Sword") : null;   // under the hand or its SwordSocket
            if (sword == null) { GKBridge.Log("gripqa: sword not in the right hand"); return; }
            Transform guard = sword.Find("SheathRef"), bb = sword.Find("BladeBase"), bt = sword.Find("BladeTip"), grip = sword.Find("Grip");
            Vector3 axis = (bt.position - bb.position).normalized;    // toward the tip
            System.Func<HumanBodyBones, string> along = b =>
            {
                var t = a.GetBoneTransform(b);
                if (t == null) return "?";
                Vector3 d = t.position - guard.position;
                return (Vector3.Dot(d, axis) * 100f).ToString("F1") + "cm/" + (Vector3.ProjectOnPlane(d, axis).magnitude * 100f).ToString("F1");
            };
            GKBridge.Log("gripqa (along blade from guard, + = toward tip / off-axis): grip point " + (Vector3.Dot(grip.position - guard.position, axis) * 100f).ToString("F1") +
                         " | index prox " + along(HumanBodyBones.RightIndexProximal) + " middle prox " + along(HumanBodyBones.RightMiddleProximal) +
                         " ring prox " + along(HumanBodyBones.RightRingProximal) + " little prox " + along(HumanBodyBones.RightLittleProximal) +
                         " | thumb prox " + along(HumanBodyBones.RightThumbProximal) + " thumb inter " + along(HumanBodyBones.RightThumbIntermediate) + " thumb dist " + along(HumanBodyBones.RightThumbDistal) +
                         " | state " + p.GetComponent<Combatant>().CurrentState +
                         (p.GetComponent<HandGrip>() != null ? " | thumb fix: excess " + (p.GetComponent<HandGrip>().LastExcess * 100f).ToString("F1") + " cm, curl " + p.GetComponent<HandGrip>().LastCurl.ToString("F0") + " deg, fingertip gap " + (p.GetComponent<HandGrip>().LastTipGap * 100f).ToString("F1") + " cm" : " | no HandGrip"));
        }

        [GKCommand("gripshot", "gripshot <name>: three macro views of the sword hand (thumb side, back of the hand, from below) (play mode)")]
        static void GripShot(string arg)
        {
            string name = string.IsNullOrEmpty(arg) ? "grip" : arg.Trim();
            var p = GameObject.FindWithTag("Player");
            var a = p != null ? p.GetComponent<Animator>() : null;
            var hand = a != null ? a.GetBoneTransform(HumanBodyBones.RightHand) : null;
            var sword = hand != null ? hand.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Sword") : null;   // under the hand or its SwordSocket
            Transform grip = sword != null ? sword.Find("Grip") : null, bb = sword != null ? sword.Find("BladeBase") : null, bt = sword != null ? sword.Find("BladeTip") : null;
            if (grip == null || bb == null || bt == null) { GKBridge.Log("gripshot: sword not in the hand"); return; }
            Vector3 axis = (bt.position - bb.position).normalized;
            Vector3 mid = a.GetBoneTransform(HumanBodyBones.RightMiddleProximal).position;
            Vector3 knuckles = Vector3.ProjectOnPlane(mid - grip.position, axis).normalized;   // from the handle to the knuckles
            Vector3 side = Vector3.Cross(axis, knuckles).normalized;
            var views = new[] { new { n = "_a", d = side }, new { n = "_b", d = knuckles }, new { n = "_c", d = -side }, new { n = "_d", d = (-knuckles + side * 0.6f).normalized } };
            foreach (var v in views)
            {
                var go = new GameObject("~gripshot");
                try
                {
                    var cam = go.AddComponent<Camera>();
                    cam.nearClipPlane = 0.02f; cam.farClipPlane = 500f; cam.fieldOfView = 30f;
                    var data = UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(cam);
                    data.renderPostProcessing = true;
                    Vector3 look = grip.position + axis * 0.02f;
                    go.transform.position = look + v.d * 0.34f + axis * 0.04f;
                    go.transform.rotation = Quaternion.LookRotation(look - go.transform.position, axis);
                    GKPreview.Capture(cam, name + v.n, 960, 720);
                }
                finally { Object.DestroyImmediate(go); }
            }
            GKBridge.Log("gripshot: " + name + "_a.._d");
        }

        static Combatant bqEnemy; static int bqBlocked, bqParried, bqHit, bqOther, bqShots;
        [GKCommand("blockqa", "blockqa start [dist] [angleDeg, 180 = behind] | report: a live Seljuk attacks the hero who holds his guard; counts blocks / hits, shoots the first impacts (play mode)")]
        static void BlockQa(string arg)
        {
            var p = GameObject.FindWithTag("Player");
            var me = p != null ? p.GetComponent<Combatant>() : null;
            if (me == null) { GKBridge.Log("blockqa: no player"); return; }
            var parts = (arg ?? "").Split(' ');
            if (parts[0] == "start")
            {
                float dist = parts.Length > 1 ? float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 2.2f;
                float ang = parts.Length > 2 ? float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) : 0f;
                if (bqEnemy != null) Object.Destroy(bqEnemy.gameObject);
                // nothing else in the arena: practice waves stop and their fighters leave (they held the attack turns)
                foreach (var w in Object.FindObjectsByType<WaveManager>(FindObjectsSortMode.None)) { w.StopAllCoroutines(); w.enabled = false; }
                foreach (var c in Object.FindObjectsByType<Combatant>(FindObjectsSortMode.None))
                    if (c != me && c.team != Team.Player) Object.Destroy(c.gameObject);
                if (UnityEditor.EditorApplication.isPaused) UnityEditor.EditorApplication.isPaused = false;
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(GKGameBuilder.EnemyPrefabPath);
                Vector3 dir = Quaternion.Euler(0f, ang, 0f) * Vector3.ProjectOnPlane(p.transform.forward, Vector3.up).normalized;
                var go = Object.Instantiate(prefab, p.transform.position + dir * dist, Quaternion.LookRotation(-dir));
                bqEnemy = go.GetComponent<Combatant>();
                var st = go.GetComponent<CombatStats>();
                if (st != null) { st.maxHealth = 1e6f; st.ResetAll(); }
                if (me.Stats != null) { me.Stats.maxHealth = 1e6f; me.Stats.ResetAll(); }
                bqBlocked = bqParried = bqHit = bqOther = 0; bqShots = 0; bqWhy.Length = 0;
                me.HitReceived -= OnBlockQaHit; me.HitReceived += OnBlockQaHit;
                var input = p.GetComponent<GKInput>();
                if (input != null) { input.InjectMove(Vector2.zero, false); input.Inject(GKInput.Injected.Block, true); }
                GKBridge.Log("blockqa: Seljuk at " + dist + " m, " + ang + " deg, hero guarding");
                return;
            }
            GKBridge.Log("blockqa: blocked " + bqBlocked + ", parried " + bqParried + ", hit " + bqHit + ", other " + bqOther + " | hero stamina " + (me.Stats != null ? me.Stats.Stamina.ToString("F0") : "?") + " | got through: " + bqWhy);
        }

        static readonly System.Text.StringBuilder bqWhy = new System.Text.StringBuilder();

        static void OnBlockQaHit(HitData hit, HitResult r)
        {
            if (r == HitResult.Blocked) bqBlocked++; else if (r == HitResult.Parried) bqParried++; else if (r == HitResult.Hit) bqHit++; else bqOther++;
            if (r != HitResult.Blocked && r != HitResult.Parried)
            {
                var hero = GameObject.FindWithTag("Player");
                var hc = hero != null ? hero.GetComponent<Combatant>() : null;
                float ang = hc != null && hit.attacker != null ? Vector3.Angle(Vector3.ProjectOnPlane(hc.transform.forward, Vector3.up), Vector3.ProjectOnPlane(hit.attacker.transform.position - hc.transform.position, Vector3.up)) : -1f;
                bqWhy.Append((hit.attack != null ? hit.attack.name : "?") + (hit.unblockable ? " UNBLOCKABLE" : "") + (hit.guardBreak ? " guardbreak" : "") +
                             " state " + (hc != null ? hc.CurrentState.ToString() : "?") + " blocking " + (hc != null && hc.IsBlocking) + " angle " + ang.ToString("F0") + "; ");
            }
            var me = GameObject.FindWithTag("Player");
            if (me != null && me.GetComponent<CombatStats>() != null) me.GetComponent<CombatStats>().ResetAll();   // keep the guard up (stamina)
            if (bqShots >= 2 || r != HitResult.Blocked) return;
            int k = bqShots++;
            // two frames later: the recoil and the sparks are on screen
            int frames = 2;
            UnityEditor.EditorApplication.CallbackFunction cb = null;
            cb = () =>
            {
                if (--frames > 0) return;
                UnityEditor.EditorApplication.update -= cb;
                var p = GameObject.FindWithTag("Player");
                if (p == null) return;
                Vector3 F = p.transform.forward, R = p.transform.right;
                Vector3 look = p.transform.position + Vector3.up * 1.35f;
                Shot("bq_impact" + k, look + F * 2.0f + R * 1.1f + Vector3.up * 0.15f, look, 38f);
                Shot("bq_impact" + k + "_back", look - F * 1.6f + R * 0.9f + Vector3.up * 0.35f, look + F * 0.8f, 42f);
            };
            UnityEditor.EditorApplication.update += cb;
        }

        static void Shot(string name, Vector3 pos, Vector3 look, float fov)
        {
            var go = new GameObject("~qashot");
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.nearClipPlane = 0.03f; cam.farClipPlane = 3000f; cam.fieldOfView = fov;
                UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(cam).renderPostProcessing = true;
                go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(look - pos, Vector3.up));
                GKPreview.Capture(cam, name, 1280, 720);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [GKCommand("lhandshot", "lhandshot <name>: three macro views of the left hand (back, palm, side) (play mode)")]
        static void LeftHandShot(string arg)
        {
            string name = string.IsNullOrEmpty(arg) ? "lhand" : arg.Trim();
            var p = GameObject.FindWithTag("Player");
            var a = p != null ? p.GetComponent<Animator>() : null;
            var hand = a != null ? a.GetBoneTransform(HumanBodyBones.LeftHand) : null;
            var mid = a != null ? a.GetBoneTransform(HumanBodyBones.LeftMiddleProximal) : null;
            var idx = a != null ? a.GetBoneTransform(HumanBodyBones.LeftIndexProximal) : null;
            var lit = a != null ? a.GetBoneTransform(HumanBodyBones.LeftLittleProximal) : null;
            if (hand == null || mid == null || idx == null || lit == null) { GKBridge.Log("lhandshot: no left hand"); return; }
            Vector3 fw = (mid.position - hand.position).normalized;
            Vector3 palm = Vector3.Cross(idx.position - lit.position, fw).normalized;
            Vector3 look = hand.position + fw * 0.06f;
            var views = new[] { new { n = "_back", d = -palm }, new { n = "_palm", d = palm }, new { n = "_side", d = (idx.position - lit.position).normalized } };
            foreach (var v in views) Shot(name + v.n, look + v.d * 0.32f + fw * 0.05f, look, 34f);
            GKBridge.Log("lhandshot: " + name + "_back/_palm/_side");
        }

        [GKCommand("fingerqa", "fingerqa: per finger, how far it bends out of its own plane (a twisted finger) and its total curl, both hands (play mode)")]
        static void FingerQa(string arg)
        {
            var p = GameObject.FindWithTag("Player");
            var fa = p != null ? p.GetComponent<FingerAxes>() : null;
            if (fa == null) { GKBridge.Log("fingerqa: no FingerAxes"); return; }
            string[] names = { "thumb", "index", "middle", "ring", "little" };
            foreach (bool right in new[] { true, false })
            {
                var sb = new System.Text.StringBuilder(right ? "fingerqa R:" : "fingerqa L:");
                for (int f = 0; f < 5; f++)
                {
                    Transform a0 = fa.Bone(right, f, 0), a1 = fa.Bone(right, f, 1), a2 = fa.Bone(right, f, 2);
                    if (a0 == null || a1 == null || a2 == null) continue;
                    Vector3 tip = a2.childCount > 0 ? a2.GetChild(0).position : a2.position + (a2.position - a1.position) * 0.8f;
                    Vector3 s0 = a1.position - a0.position, s1 = a2.position - a1.position, s2 = tip - a2.position;
                    Vector3 n1 = Vector3.Cross(s0, s1), n2 = Vector3.Cross(s1, s2);
                    float outOfPlane = n1.sqrMagnitude > 1e-8f && n2.sqrMagnitude > 1e-8f && Vector3.Angle(s0, s1) > 8f && Vector3.Angle(s1, s2) > 8f ? Vector3.Angle(n1, n2) : 0f;
                    float curl = Vector3.Angle(s0, s1) + Vector3.Angle(s1, s2);
                    sb.Append(" " + names[f] + " curl " + curl.ToString("F0") + " twist " + outOfPlane.ToString("F0") + ";");
                }
                GKBridge.Log(sb.ToString());
            }
        }

        [GKCommand("compqa", "compqa <ComponentType> on|off: enable or disable one of the player's components (A/B shots, play mode)")]
        static void CompQa(string arg)
        {
            var parts = (arg ?? "").Split(' ');
            var p = GameObject.FindWithTag("Player");
            if (p == null || parts.Length < 2) { GKBridge.Log("compqa: no player or usage compqa Type on|off"); return; }
            int n = 0;
            foreach (var b in p.GetComponentsInChildren<Behaviour>(true))
                if (b != null && b.GetType().Name == parts[0]) { b.enabled = parts[1] == "on"; n++; }
            GKBridge.Log("compqa: " + parts[0] + " " + parts[1] + " (" + n + ")");
        }

        [GKCommand("postureqa", "postureqa: torso lean the animation made and how much PostureCorrector took out (play mode)")]
        static void PostureQa(string arg)
        {
            var p = GameObject.FindWithTag("Player");
            var pc = p != null ? p.GetComponent<PostureCorrector>() : null;
            var m = p != null ? p.GetComponent<CharacterMotor>() : null;
            GKBridge.Log("postureqa: " + (pc != null ? "lean " + pc.LastLean.ToString("F1") + " deg, corrected " + pc.LastCorrection.ToString("F1") : "no PostureCorrector") +
                         (m != null ? " | grounded " + m.IsGrounded + " air " + m.AirTime.ToString("F2") + " y " + p.transform.position.y.ToString("F3") : "") +
                         " | state " + p.GetComponent<Combatant>().CurrentState +
                         (p.GetComponent<SwordArmPose>() != null ? " | arms: brace " + p.GetComponent<SwordArmPose>().BraceWeight.ToString("F2") + " (palm off the blade " + (p.GetComponent<SwordArmPose>().LastBraceMiss * 100f).ToString("F1") + " cm), carry " + p.GetComponent<SwordArmPose>().CarryWeight.ToString("F2") + " turn " + p.GetComponent<SwordArmPose>().LastCarryTurn.ToString("F0") + " deg" : "") +
                         (p.GetComponent<CCHelperBones>() != null ? " | limb roll max " + p.GetComponent<CCHelperBones>().LastMaxRoll.ToString("F0") + " deg" : ""));
        }

        [GKCommand("holsterdiag", "holsterdiag: why draw / sheathe animate or snap (moveset clips, animator layers) (play mode)")]
        static void HolsterDiag(string arg)
        {
            var p = GameObject.FindWithTag("Player");
            if (p == null) { GKBridge.Log("holsterdiag: no player"); return; }
            var c = p.GetComponent<Combatant>();
            var a = p.GetComponent<Animator>();
            var m = c != null ? c.moves : null;
            string s = "holsterdiag: moves " + (m != null ? m.name : "null");
            if (m != null)
                s += " | draw state '" + m.drawSword.state + "' clip " + (m.drawSword.clip != null ? m.drawSword.clip.name : "null") + " valid " + m.drawSword.IsValid +
                     " | sheathe state '" + m.sheathSword.state + "' clip " + (m.sheathSword.clip != null ? m.sheathSword.clip.name : "null") + " valid " + m.sheathSword.IsValid;
            if (a != null)
            {
                s += " | controller " + (a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "null") + " layers " + a.layerCount + ":";
                for (int i = 0; i < a.layerCount; i++) s += " " + a.GetLayerName(i) + "(" + a.GetLayerWeight(i).ToString("F2") + ")";
                s += " | UpperBody " + a.GetLayerIndex(GK.LayerUpperBody) + " FullBodyAction " + a.GetLayerIndex(GK.LayerFullBodyAction);
            }
            GKBridge.Log(s);
        }

        static void Report(GameObject p)
        {
            var cloth = p.GetComponentsInChildren<Cloth>(true);
            var cc = p.GetComponent<CharacterController>();
            string s = "clothqa: pos " + p.transform.position.ToString("F2") + " speed " + (cc != null ? cc.velocity.magnitude.ToString("F2") : "?") +
                       " state " + p.GetComponent<Combatant>().CurrentState;
            foreach (var c in cloth)
            {
                var smr = c.GetComponent<SkinnedMeshRenderer>();
                var v = c.vertices;
                // how far the simulated coat strays from the body: the largest distance of a cloth vertex from the hips
                var anim = p.GetComponent<Animator>();
                var hips = anim != null ? anim.GetBoneTransform(HumanBodyBones.Hips) : p.transform;
                float far = 0f; int nan = 0;
                foreach (var x in v)
                {
                    if (float.IsNaN(x.x) || float.IsNaN(x.y) || float.IsNaN(x.z)) { nan++; continue; }
                    float d = Vector3.Distance(c.transform.TransformPoint(x), hips.position);
                    if (d > far) far = d;
                }
                s += " | cloth " + c.name + " enabled " + c.enabled + " verts " + v.Length + " farthest-from-hips " + far.ToString("F2") + " m, NaN " + nan +
                     ", colliders " + (c.capsuleColliders != null ? c.capsuleColliders.Length : 0) + "+" + (c.sphereColliders != null ? c.sphereColliders.Length : 0) +
                     ", worldVel " + c.worldVelocityScale.ToString("F2") + " worldAcc " + c.worldAccelerationScale.ToString("F2") + " damping " + c.damping.ToString("F2");
            }
            GKBridge.Log(s);
        }
    }
}
