using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// One enemy type against the hero (S2: every type meets QUALITY 3c and its counter must read): the enemy prefab
    /// stands 4 m ahead, engaged; the hero stands or holds his guard through his own input path and cannot die. Every
    /// blow is logged with its result on the hero, unblockables with whether their tell came first, and the time from
    /// the blow's start to its contact (the window the player has to read it).
    /// </summary>
    public static class GKClipDuelQa
    {
        [GKCommand("hitcontact", "hitcontact on|off [gap cm]: WeaponHitbox delivers each hit at contact (the blade within the gap of the body capsules) or at detection, as before; the gap for every weapon (play or edit; not saved)")]
        static void HitContact(string arg)
        {
            var p = (arg ?? "").Trim().Split(' ');
            WeaponHitbox.DeliverAtContact = p.Length > 0 && p[0] == "on";
            float cm;
            WeaponHitbox.GapOverride = p.Length > 1 && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cm) ? cm / 100f : -1f;
            GKBridge.Log("hitcontact: " + (WeaponHitbox.DeliverAtContact ? "on" : "off") + ", gap " + (WeaponHitbox.GapOverride >= 0f ? (WeaponHitbox.GapOverride * 100f).ToString("F1") + " cm" : "each weapon's own"));
        }

        [GKCommand("duelqatest", "duelqatest: the duel tally on fixed results (a hit with a still appended is a hit)")]
        static void DuelQaTest(string arg)
        {
            var cases = new[] { new KeyValuePair<string, int>("Hit", 1), new KeyValuePair<string, int>("Hit [still w_airhit1_18cm]", 1),
                                new KeyValuePair<string, int>("Hit (reach miss, closest 6 cm)", 1), new KeyValuePair<string, int>("Killed", 1),
                                new KeyValuePair<string, int>("Blocked", 2), new KeyValuePair<string, int>("Parried [still x]", 2),
                                new KeyValuePair<string, int>("GuardBroken", 3), new KeyValuePair<string, int>("missed", 0),
                                new KeyValuePair<string, int>("missed (reach miss, closest 4 cm)", 0), new KeyValuePair<string, int>("Dodged", 0) };
            int bad = 0; var sb = new System.Text.StringBuilder();
            foreach (var c in cases) { int k = GKDuelRecorder.Kind(c.Key); if (k != c.Value) { bad++; sb.Append(" | FAIL '" + c.Key + "' -> " + k + ", wants " + c.Value); } }
            GKBridge.Log("duelqatest: " + (cases.Length - bad) + "/" + cases.Length + " passed" + sb);
        }

        [GKCommand("duelqa", "duelqa <prefab: warrior|veteran|archer|path> <name> [hero=stand|block] [secs=30]: one enemy type against the hero, every blow logged (play mode)")]
        static void Cmd(string arg)
        {
            var p = GameObject.FindWithTag("Player");
            if (p == null || !Application.isPlaying) { GKBridge.Log("duelqa: no player in play mode"); return; }
            var parts = (arg ?? "").Trim().Split(' ');
            if (parts.Length < 2) { GKBridge.Log("duelqa: give a prefab and a name"); return; }
            string path = parts[0] == "warrior" ? GKGameBuilder.EnemyPrefabPath : parts[0] == "veteran" ? GKCombatBuilder.VeteranPrefabPath :
                          parts[0] == "archer" ? GKCombatBuilder.ArcherPrefabPath : parts[0];
            var old = p.GetComponent<GKDuelRecorder>(); if (old != null) Object.Destroy(old);
            var r = p.AddComponent<GKDuelRecorder>();
            r.name0 = parts[1];
            for (int i = 2; i < parts.Length; i++)
            {
                int e = parts[i].IndexOf('='); if (e < 0) continue;
                string k = parts[i].Substring(0, e), v = parts[i].Substring(e + 1); float f;
                if (k == "hero") r.hero = v;
                else if (k == "unb" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) r.unblockableChance = f;
                else if (k == "shots") r.shots = v == "1";
                else if (k == "secs" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) r.secs = f;
            }
            r.Begin(path);
        }
    }

    public sealed class GKDuelRecorder : MonoBehaviour
    {
        public string name0 = "duel", hero = "stand"; public float secs = 30f;
        [Tooltip("The AI's unblockable chance for this duel (< 0: the prefab's).")] public float unblockableChance = -1f;
        [Tooltip("A gameplay still and a side still when a blow gets through a raised guard.")] public bool shots;
        int shotN;

        class Blow
        {
            public string name; public bool unblockable, told; public float start, contact = -1f; public string result = "missed";
            public AttackData atk; public float dist0, face0, distWin = -1f, faceWin, gapMin = float.MaxValue, heroMoved; public bool windowSeen;
            public int touchF = -1, hitF = -1; public float visGap = -1f;
        }
        Vector3 heroAtLast; bool haveHeroAt; int missShots;
        readonly List<Vector3> segPrev = new List<Vector3>(); AttackData segFor;
        int airShots;
        float nextPress; bool pressed;
        readonly List<float> heroGaps = new List<float>(); int heroReachMiss, foeReachMiss; float heroReachWorst;

        void OnHeroHit(HitData hit, HitResult r)
        {
            if (hit.attacker != me || (r != HitResult.Hit && r != HitResult.Killed)) return;
            Vector3 body, blade;
            float g = VisibleGap(me, foe, out body, out blade);
            if (g < 0f) return;
            heroGaps.Add(g);
            if (g > 0.03f && airShots < 4) { airShots++; AirStill(name0 + "_heroair" + airShots + "_" + Mathf.RoundToInt(g * 100f) + "cm", body, blade); }
        }

        void OnReachMiss(WeaponHitbox w, Combatant target, float best)
        {
            if (w.Owner == me) { heroReachMiss++; heroReachWorst = Mathf.Max(heroReachWorst, best); }
            else if (w.Owner == foe)
            {
                foeReachMiss++;
                if (blows.Count > 0) blows[blows.Count - 1].result += " (reach miss, closest " + (best * 100f).ToString("F0") + " cm)";
            }
        }

        /// <summary>The foe's blade (its cutting segments, edge radius off) to the hero's skinned body as drawn now: the
        /// least distance (m) and the body point it is nearest to.</summary>
        float VisibleGap(out Vector3 at, out Vector3 bladeAt) { return VisibleGap(foe, this, out at, out bladeAt); }

        /// <summary>The striker's active blade (edge segments) to the victim's skinned body as drawn now (m).</summary>
        static float VisibleGap(Combatant striker, Component victim, out Vector3 at, out Vector3 bladeAt)
        {
            at = victim.transform.position; bladeAt = at;
            var segs = new List<Vector3>();
            foreach (var w in new[] { striker.weapon, striker.offWeapon })
            {
                if (w == null || !w.Active) continue;
                for (int s = 0; s < w.SegmentCount; s++) { Vector3 a, b; if (w.GetSegment(s, out a, out b)) { segs.Add(a); segs.Add(b); } }
            }
            if (segs.Count == 0) return -1f;
            float best = float.MaxValue;
            var mesh = new Mesh();
            try
            {
                foreach (var smr in victim.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    if (!smr.enabled || !smr.gameObject.activeInHierarchy) continue;
                    smr.BakeMesh(mesh, true);
                    var vs = mesh.vertices; var tr = smr.transform;
                    Quaternion rot = tr.rotation; Vector3 pos = tr.position;
                    for (int i = 0; i < vs.Length; i++)
                    {
                        Vector3 v = pos + rot * vs[i];
                        for (int s = 0; s + 1 < segs.Count; s += 2)
                        {
                            Vector3 a = segs[s], ab = segs[s + 1] - a;
                            float k = Mathf.Clamp01(Vector3.Dot(v - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude));
                            Vector3 q = a + ab * k;
                            float dd = (v - q).sqrMagnitude;
                            if (dd < best) { best = dd; at = v; bladeAt = q; }
                        }
                    }
                }
            }
            finally { Object.Destroy(mesh); }
            return best < float.MaxValue ? Mathf.Sqrt(best) : -1f;
        }

        /// <summary>A close still of a hit: square-on to the blade, 0.9 m from where it is nearest the body.</summary>
        void AirStill(string name, Vector3 body, Vector3 blade)
        {
            var go = new GameObject("~airhit");
            try
            {
                var cam = go.AddComponent<Camera>(); cam.nearClipPlane = 0.02f; cam.fieldOfView = 32f;
                Vector3 look = (body + blade) * 0.5f, gapDir = (blade - body).sqrMagnitude > 1e-6f ? (blade - body).normalized : transform.right;
                Vector3 side = Vector3.Cross(gapDir, Vector3.up); if (side.sqrMagnitude < 1e-4f) side = transform.right;
                go.transform.position = look + side.normalized * 0.9f + Vector3.up * 0.1f;
                go.transform.rotation = Quaternion.LookRotation(look - go.transform.position, Vector3.up);
                GKPreview.Capture(cam, name, 1280, 720);
            }
            finally { Destroy(go); }
        }

        /// <summary>The foe's cutting segments swept from last frame's place to this frame's (6 sub-steps) against the
        /// hero's hurtboxes: the least gap (m). A fast swing travels further than a body's width in one frame, so a gap
        /// sampled once a frame can read 15 cm on the frame the blade went through.</summary>
        float SweptGap()
        {
            var cur = new List<Vector3>();
            var a0 = foe.CurrentAttack;
            WeaponHand hand = a0 != null && foe.offWeapon != null && a0.kind != AttackKind.Kick ? a0.hand : WeaponHand.Main;
            foreach (var w in new[] { hand != WeaponHand.Off ? foe.weapon : null, hand != WeaponHand.Main ? foe.offWeapon : null })
            {
                if (w == null) continue;
                for (int s = 0; s < w.SegmentCount; s++) { Vector3 a, b; if (w.GetSegment(s, out a, out b)) { cur.Add(a); cur.Add(b); } }
            }
            bool sweep = segFor == a0 && segPrev.Count == cur.Count;
            float best = float.MaxValue;
            var boxes = GetComponentsInChildren<Hurtbox>();
            for (int k = sweep ? 0 : 6; k <= 6; k++)
                for (int s = 0; s + 1 < cur.Count; s += 2)
                {
                    Vector3 a = sweep ? Vector3.Lerp(segPrev[s], cur[s], k / 6f) : cur[s], b = sweep ? Vector3.Lerp(segPrev[s + 1], cur[s + 1], k / 6f) : cur[s + 1];
                    foreach (var hb in boxes)
                    {
                        var c = hb.GetComponent<Collider>();
                        if (c == null || !c.enabled) continue;   // the ragdoll's colliders are off while alive
                        for (int i = 0; i <= 8; i++) { Vector3 q = Vector3.Lerp(a, b, i / 8f); best = Mathf.Min(best, Vector3.Distance(c.ClosestPoint(q), q)); }
                    }
                }
            segPrev.Clear(); segPrev.AddRange(cur); segFor = a0;
            return best;
        }

        /// <summary>Flat distance foe to hero (centres) and how far the foe's facing is off the hero (deg).</summary>
        void Where(out float dist, out float face)
        {
            Vector3 d = Vector3.ProjectOnPlane(transform.position - foe.transform.position, Vector3.up);
            dist = d.magnitude;
            face = Vector3.Angle(Vector3.ProjectOnPlane(foe.transform.forward, Vector3.up), d);
        }
        readonly List<Blow> blows = new List<Blow>();
        Combatant me, foe; GKInput input;
        float end, toldAt = -10f;
        bool done;

        public void Begin(string prefabPath)
        {
            me = GetComponent<Combatant>(); input = GetComponent<GKInput>();
            foreach (var w in FindObjectsByType<WaveManager>(FindObjectsSortMode.None)) { w.StopAllCoroutines(); w.enabled = false; }
            foreach (var c in FindObjectsByType<Combatant>(FindObjectsSortMode.None)) if (c != me && c.team != Team.Player) Destroy(c.gameObject);
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) { GKBridge.Log("duelqa: no prefab " + prefabPath); Destroy(this); return; }
            Vector3 F = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            Vector3 at = transform.position + F * 4f;
            RaycastHit h; if (Physics.Raycast(at + Vector3.up * 10f, Vector3.down, out h, 20f, GK.GroundMask, QueryTriggerInteraction.Ignore)) at.y = h.point.y + 0.05f;
            var go = Instantiate(prefab, at, Quaternion.LookRotation(-F));
            foe = go.GetComponent<Combatant>();
            var ai = go.GetComponent<EnemyAI>(); if (ai != null) { ai.engaged = true; if (unblockableChance >= 0f) ai.unblockableChance = unblockableChance; }
            var ar = go.GetComponent<ArcherAI>(); if (ar != null) ar.engaged = true;
            foe.AttackStarted += OnAttack;
            // the tell is raised in the same frame, just after AttackStarted: it marks the blow just begun
            foe.Telegraphed += a => { toldAt = Time.time; if (blows.Count > 0 && Time.time - blows[blows.Count - 1].start < 0.05f) blows[blows.Count - 1].told = true; };
            me.HitReceived += OnHit;
            foe.HitReceived += OnHeroHit;
            WeaponHitbox.ReachMiss += OnReachMiss;
            if (input != null) input.ignoreDevices = true;
            end = Time.realtimeSinceStartup + secs;
            GKBridge.Log("duelqa: " + name0 + ": " + prefab.name + " at 4 m, hero " + hero + ", " + secs.ToString("F0") + " s");
        }

        void OnAttack(AttackData a)
        {
            var b = new Blow { name = a != null ? a.name : "?", atk = a, unblockable = a != null && a.unblockable, start = Time.time, told = Time.time - toldAt < 1.5f };
            Where(out b.dist0, out b.face0);
            b.heroMoved = haveHeroAt ? Vector3.ProjectOnPlane(transform.position - heroAtLast, Vector3.up).magnitude : 0f;
            heroAtLast = transform.position; haveHeroAt = true;
            blows.Add(b);
        }

        void OnHit(HitData hit, HitResult r)
        {
            if (hit.attacker != foe || blows.Count == 0) return;
            var b = blows[blows.Count - 1];
            if (b.contact < 0f)
            {
                b.contact = Time.time; b.result = r.ToString(); b.hitF = Time.frameCount;
                // the hit as seen: the blade's gap to the drawn body on the frame the hit registers
                Vector3 body, blade;
                b.visGap = VisibleGap(out body, out blade);
                if (b.visGap > 0.03f && airShots < 4 && (r == HitResult.Hit || r == HitResult.Killed))
                {
                    airShots++;
                    string nm = name0 + "_airhit" + airShots + "_" + Mathf.RoundToInt(b.visGap * 100f) + "cm";
                    AirStill(nm, body, blade);
                    b.result += " [still " + nm + "]";
                }
            }
            // a blow through a raised guard: where did it come from (stills now, at contact)
            if (shots && hero == "block" && !b.unblockable && (r == HitResult.Hit || r == HitResult.Killed) && shotN < 3)
            {
                shotN++;
                float bearing = Vector3.SignedAngle(Vector3.ProjectOnPlane(transform.forward, Vector3.up), Vector3.ProjectOnPlane(foe.transform.position - transform.position, Vector3.up), Vector3.up);
                b.result += " (from " + bearing.ToString("F0") + " deg, guard " + (me.IsBlocking ? "up" : "DOWN: " + me.CurrentState) + ")";
                GKPlayCommands.GameShot(name0 + "_through" + shotN);
                GKPlayCommands.GameShot(name0 + "_through" + shotN + "_side 90 8");
            }
        }

        void Update()
        {
            if (done) return;
            if (me != null && me.Stats != null) me.Stats.ResetAll();
            if (input != null && hero == "block") input.Inject(GKInput.Injected.Block, true);
            // hero=attack: a light press every 1.1 s (held one frame)
            if (input != null && hero == "attack")
            {
                if (Time.time >= nextPress) { input.Inject(GKInput.Injected.Light, true); nextPress = Time.time + 1.1f; pressed = true; }
                else if (pressed) { input.Inject(GKInput.Injected.Light, false); pressed = false; }
            }
            // the swing in progress: where it is when its window opens, and the blade's closest pass
            if (foe != null && blows.Count > 0 && foe.CurrentAttack != null && foe.CurrentAttack == blows[blows.Count - 1].atk)
            {
                var b = blows[blows.Count - 1];
                float g = SweptGap();
                b.gapMin = Mathf.Min(b.gapMin, g);
                if (g <= 0.005f && b.touchF < 0) b.touchF = Time.frameCount;
                if (!b.windowSeen && foe.ActionProgress >= b.atk.hitStart)
                {
                    b.windowSeen = true;
                    Where(out b.distWin, out b.faceWin);
                    // a likely miss (well beyond its ideal range, or turned away): a still now
                    if (shots && missShots < 4 && (b.distWin > b.atk.idealRange + 0.45f || b.faceWin > 35f))
                    {
                        missShots++;
                        GKPlayCommands.GameShot(name0 + "_miss" + missShots);
                        GKPlayCommands.GameShot(name0 + "_miss" + missShots + "_side 90 8");
                        b.result += " [still " + name0 + "_miss" + missShots + "]";
                    }
                }
            }
            if (Time.realtimeSinceStartup > end || foe == null) Finish();
        }

        /// <summary>A blow's result by its first word (1 hit, 2 blocked, 3 guard broken, 0 missed): a hit with a still or a
        /// note appended (" [still ...]") is still a hit (leases O-W counted those as misses, up to 4 a duel).</summary>
        public static int Kind(string result)
        {
            if (string.IsNullOrEmpty(result)) return 0;
            if (result.StartsWith("Hit") || result.StartsWith("Killed")) return 1;
            if (result.StartsWith("Blocked") || result.StartsWith("Parried")) return 2;
            if (result.StartsWith("GuardBroken")) return 3;
            return 0;
        }

        void Finish()
        {
            done = true;
            if (me != null) me.HitReceived -= OnHit;
            if (foe != null) foe.HitReceived -= OnHeroHit;
            WeaponHitbox.ReachMiss -= OnReachMiss;
            if (input != null && hero == "attack") input.Inject(GKInput.Injected.Light, false);
            if (input != null) { input.Inject(GKInput.Injected.Block, false); input.ignoreDevices = false; }
            int hit = 0, blocked = 0, broken = 0, missed = 0, unb = 0, unbTold = 0;
            float readMin = 99f, readSum = 0f; int reads = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var b in blows)
            {
                switch (Kind(b.result)) { case 1: hit++; break; case 2: blocked++; break; case 3: broken++; break; }
                if (Kind(b.result) == 0) missed++;
                if (b.unblockable) { unb++; if (b.told) unbTold++; }
                float read = b.contact >= 0f ? b.contact - b.start : -1f;
                if (read >= 0f) { readMin = Mathf.Min(readMin, read); readSum += read; reads++; }
                sb.Append(" | ").Append(b.name).Append(b.unblockable ? " [U" + (b.told ? ", told]" : ", NOT told]") : "").Append(' ').Append(b.result)
                  .Append(read >= 0f ? " after " + read.ToString("F2") + " s" : "");
                // where it was thrown from (ideal: the attack's own range)
                if (b.atk != null)
                    sb.Append(" {start ").Append(b.dist0.ToString("F2")).Append(" m ").Append(b.face0.ToString("F0")).Append(" deg, window ")
                      .Append(b.distWin >= 0f ? b.distWin.ToString("F2") + " m " + b.faceWin.ToString("F0") + " deg" : "-").Append(", ideal ").Append(b.atk.idealRange.ToString("F2"))
                      .Append(", blade closest (swept) ").Append(b.gapMin < 9f ? (b.gapMin * 100f).ToString("F0") + " cm" : "-").Append(", hero moved ").Append(b.heroMoved.ToString("F2")).Append(" m")
                      .Append(b.visGap >= 0f ? ", visible gap at the hit " + (b.visGap * 100f).ToString("F1") + " cm" : "")
                      .Append(b.hitF >= 0 ? (b.touchF >= 0 ? ", hit " + (b.hitF - b.touchF >= 0 ? "+" : "") + (b.hitF - b.touchF) + " frames from the touch" : ", hit WITHOUT a touch") : (b.touchF >= 0 ? ", touched, no hit" : "")).Append("}");
            }
            string heroPart = "";
            if (hero == "attack")
            {
                float hm = 0f, hs = 0f; int ho = 0;
                foreach (var g in heroGaps) { hm = Mathf.Max(hm, g); hs += g; if (g > 0.03f) ho++; }
                heroPart = "; hero's cuts: " + heroGaps.Count + " hits" + (heroGaps.Count > 0 ? ", visible gap max " + (hm * 100f).ToString("F1") + " cm mean " + (hs / heroGaps.Count * 100f).ToString("F1") + " cm, " + ho + " over 3 cm" : "") +
                           ", reach misses " + heroReachMiss + (heroReachMiss > 0 ? " (worst closest " + (heroReachWorst * 100f).ToString("F0") + " cm)" : "");
            }
            float vMax = 0f, vSum = 0f; int vN = 0, vOver = 0;
            foreach (var b in blows) if (b.visGap >= 0f && (b.result.StartsWith("Hit") || b.result.StartsWith("Killed"))) { vMax = Mathf.Max(vMax, b.visGap); vSum += b.visGap; vN++; if (b.visGap > 0.03f) vOver++; }
            GKBridge.Log("duelqa " + name0 + ": " + blows.Count + " blows, hero " + hero + ": hit " + hit + ", blocked " + blocked + ", guard broken " + broken + ", missed " + missed +
                         heroPart + "; foe reach misses " + foeReachMiss +
                         (vN > 0 ? "; visible gap at the hit (skinned body) max " + (vMax * 100f).ToString("F1") + " cm mean " + (vSum / vN * 100f).ToString("F1") + " cm, " + vOver + " of " + vN + " over 3 cm" : "") +
                         "; unblockables " + unb + " (" + unbTold + " told); start to contact min " + (reads > 0 ? readMin.ToString("F2") : "-") + " s mean " +
                         (reads > 0 ? (readSum / reads).ToString("F2") : "-") + " s" + sb);
            if (foe != null) Destroy(foe.gameObject);
            Destroy(this);
        }
    }
}
