using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// The archers against the hero (E06, BALANCE 3.2 / 5.1): n archers stand dist metres ahead in an arc, engaged; the
    /// hero stands, holds his guard, strafes, or dodges each tell, through his own input path, and cannot die. Every
    /// arrow is logged (hit, blocked, parried, dodged, or lost to the world, its flight time and range), every shot's
    /// tell (draw start to loose), and the most archers drawing at once (rule: the director's ranged tokens).
    /// </summary>
    public static class GKClipArcherQa
    {
        [GKCommand("bowlog", "bowlog [seconds=3]: every frame of the nearest archer's bow: action, progress, string held, the string hand's travel and the bend weight -> GKBridge/bowlog.txt (play mode)")]
        static void BowLog(string arg)
        {
            float secs = 3f;
            if (!string.IsNullOrEmpty(arg)) float.TryParse(arg.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out secs);
            var p = GameObject.FindWithTag("Player");
            BowRig best = null; float bd = float.MaxValue;
            foreach (var r in Object.FindObjectsByType<BowRig>(FindObjectsSortMode.None))
            {
                float d = p != null ? Vector3.Distance(r.transform.position, p.transform.position) : 0f;
                if (d < bd) { bd = d; best = r; }
            }
            if (best == null) { GKBridge.Log("bowlog: no archer"); return; }
            var rec = best.gameObject.AddComponent<GKBowLogger>();
            rec.until = Time.time + secs;
            GKBridge.Log("bowlog: logging " + best.GetComponentInParent<Combatant>().name + " for " + secs.ToString("F1") + " s of game time");
        }

        [GKCommand("archerqa", "archerqa <name> [n=2] [dist=14] [hero=stand|block|strafe|dodge] [secs=25]: archers against the hero, every arrow and tell logged (play mode)")]
        static void Cmd(string arg)
        {
            var p = GameObject.FindWithTag("Player");
            if (p == null || !Application.isPlaying) { GKBridge.Log("archerqa: no player in play mode"); return; }
            var old = p.GetComponent<GKArcherRecorder>(); if (old != null) Object.Destroy(old);
            var r = p.AddComponent<GKArcherRecorder>();
            foreach (var s in (arg ?? "").Trim().Split(' '))
            {
                int e = s.IndexOf('=');
                if (e < 0) { if (s.Length > 0) r.name0 = s; continue; }
                string k = s.Substring(0, e), v = s.Substring(e + 1); float f;
                bool num = float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f);
                if (k == "n" && num) r.count = Mathf.Clamp((int)f, 1, 6);
                else if (k == "dist" && num) r.dist = f;
                else if (k == "secs" && num) r.secs = f;
                else if (k == "hero") r.hero = v;
            }
            r.Begin();
        }
    }

    public sealed class GKArcherRecorder : MonoBehaviour
    {
        public string name0 = "archer", hero = "stand"; public int count = 2; public float dist = 14f, secs = 25f;

        class Shot { public float drawAt = -1f, looseAt = -1f, range; public string result = "in flight"; public float flight; }
        readonly List<ArcherAI> archers = new List<ArcherAI>();
        readonly Dictionary<ArcherAI, Shot> drawing = new Dictionary<ArcherAI, Shot>();
        readonly Dictionary<Arrow, Shot> inFlight = new Dictionary<Arrow, Shot>();
        readonly List<Shot> shots = new List<Shot>();
        Combatant me; GKInput input;
        float end, strafeFlip; int maxDrawing, frame;
        bool done;

        public void Begin()
        {
            me = GetComponent<Combatant>(); input = GetComponent<GKInput>();
            foreach (var w in FindObjectsByType<WaveManager>(FindObjectsSortMode.None)) { w.StopAllCoroutines(); w.enabled = false; }
            foreach (var c in FindObjectsByType<Combatant>(FindObjectsSortMode.None)) if (c != me && c.team != Team.Player) Destroy(c.gameObject);
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(GKCombatBuilder.ArcherPrefabPath);
            if (prefab == null) { GKBridge.Log("archerqa: no " + GKCombatBuilder.ArcherPrefabPath + " (run archer)"); Destroy(this); return; }
            Vector3 F = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            for (int i = 0; i < count; i++)
            {
                float ang = count == 1 ? 0f : Mathf.Lerp(-30f, 30f, (float)i / (count - 1));
                Vector3 at = transform.position + Quaternion.AngleAxis(ang, Vector3.up) * F * dist;
                RaycastHit h; if (Physics.Raycast(at + Vector3.up * 20f, Vector3.down, out h, 40f, GK.GroundMask, QueryTriggerInteraction.Ignore)) at.y = h.point.y + 0.05f;
                var go = Instantiate(prefab, at, Quaternion.LookRotation(GK.Flat(transform.position - at).normalized));
                var ai = go.GetComponent<ArcherAI>();
                ai.engaged = true;
                ai.DrawStarted += OnDraw; ai.Loosed += OnLoose;
                archers.Add(ai);
            }
            Arrow.Landed += OnLanded;
            if (input != null) input.ignoreDevices = true;
            end = Time.realtimeSinceStartup + secs;
            GKBridge.Log("archerqa: " + name0 + ": " + count + " archers at " + dist.ToString("F0") + " m, hero " + hero + ", " + secs.ToString("F0") + " s");
        }

        void OnDraw(ArcherAI a) { var s = new Shot { drawAt = Time.time }; drawing[a] = s; shots.Add(s); if (hero == "dodge") dodgeAt = Time.time + 0.25f; }
        float dodgeAt = -1f;

        void OnLoose(ArcherAI a, Arrow arrow)
        {
            Shot s;
            if (!drawing.TryGetValue(a, out s)) { s = new Shot(); shots.Add(s); }
            drawing.Remove(a);
            s.looseAt = Time.time; s.range = Vector3.Distance(a.transform.position, transform.position);
            if (arrow != null) inFlight[arrow] = s;
        }

        void OnLanded(Arrow arrow, Combatant man, HitResult r)
        {
            Shot s;
            if (!inFlight.TryGetValue(arrow, out s)) return;
            if (man == null) s.result = "missed (world)";
            else if (man == me) s.result = r.ToString();
            else return;                                   // passed another archer: still flying
            s.flight = Time.time - s.looseAt;
            inFlight.Remove(arrow);
        }

        void Update()
        {
            if (done) return;
            frame++;
            if (me != null && me.Stats != null) me.Stats.ResetAll();   // the hero cannot die: the test is the archers
            int nowDrawing = 0;
            foreach (var a in archers) if (a != null && (a.CurrentMode == ArcherAI.Mode.Draw || a.CurrentMode == ArcherAI.Mode.Aim)) nowDrawing++;
            maxDrawing = Mathf.Max(maxDrawing, nowDrawing);
            if (input != null)
            {
                switch (hero)
                {
                    case "block": input.Inject(GKInput.Injected.Block, true); break;
                    case "strafe":
                        if (Time.time > strafeFlip) strafeFlip = Time.time + 2.5f;
                        input.InjectMove(new Vector2(Mathf.Repeat(Time.time, 5f) < 2.5f ? 1f : -1f, 0f), true);
                        break;
                    case "dodge":
                        if (dodgeAt > 0f && Time.time >= dodgeAt) { dodgeAt = -1f; input.InjectMove(Vector2.right, true); input.Inject(GKInput.Injected.Dodge, false); input.Inject(GKInput.Injected.Dodge, true); }
                        else if (frame % 20 == 0) { input.Inject(GKInput.Injected.Dodge, false); input.InjectMove(Vector2.zero, false); }
                        break;
                }
            }
            if (Time.realtimeSinceStartup > end) Finish();
        }

        void Finish()
        {
            done = true;
            Arrow.Landed -= OnLanded;
            if (input != null)
            {
                input.Inject(GKInput.Injected.Block, false); input.Inject(GKInput.Injected.Dodge, false);
                input.InjectMove(Vector2.zero, false); input.ignoreDevices = false;
            }
            int loosed = 0, hit = 0, blocked = 0, dodged = 0, missed = 0, spoilt = 0;
            float tellMin = 99f, tellSum = 0f, flightSum = 0f; int flights = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var s in shots)
            {
                if (s.looseAt < 0f) { spoilt++; continue; }
                loosed++;
                float tell = s.drawAt >= 0f ? s.looseAt - s.drawAt : -1f;
                if (tell >= 0f) { tellMin = Mathf.Min(tellMin, tell); tellSum += tell; }
                if (s.result == "Hit" || s.result == "Killed") hit++;
                else if (s.result == "Blocked" || s.result == "Parried" || s.result == "GuardBroken") blocked++;
                else if (s.result == "Dodged") dodged++;
                else missed++;
                if (s.flight > 0f) { flightSum += s.flight; flights++; }
                sb.Append(" | ").Append(s.result).Append(" at ").Append(s.range.ToString("F1")).Append(" m, tell ").Append(tell.ToString("F2")).Append(" s, flight ").Append(s.flight.ToString("F2")).Append(" s");
            }
            GKBridge.Log("archerqa " + name0 + ": " + loosed + " loosed (" + spoilt + " draws spoilt or cut off), hero " + hero + ": hit " + hit + ", blocked " + blocked + ", dodged " + dodged +
                         ", missed " + missed + "; tell min " + (loosed > 0 ? tellMin.ToString("F2") : "-") + " s mean " + (loosed > 0 ? (tellSum / loosed).ToString("F2") : "-") +
                         " s; flight mean " + (flights > 0 ? (flightSum / flights).ToString("F2") : "-") + " s; most drawing at once " + maxDrawing + " (rule <= " +
                         (CombatDirector.Existing != null ? CombatDirector.Existing.maxRangedShooters : 2) + ")" + sb);
            foreach (var a in archers) if (a != null) Destroy(a.gameObject);
            Destroy(this);
        }
    }

    /// <summary>The bowlog recorder: one line per frame, then a summary.</summary>
    public sealed class GKBowLogger : MonoBehaviour
    {
        public float until;
        BowRig rig; Combatant who;
        readonly System.Text.StringBuilder sb = new System.Text.StringBuilder("frame,time,action,progress,held,travel_m,draw\n");
        float maxDraw, maxTravel; int frames, bentLoose;

        void Start() { rig = GetComponent<BowRig>(); who = GetComponentInParent<Combatant>(); }

        void LateUpdate()
        {
            if (rig == null) { Destroy(this); return; }
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var a = who != null ? who.CurrentAction : null;
            sb.Append(Time.frameCount).Append(',').Append(Time.time.ToString("F3", ci)).Append(',').Append(a != null ? a.state : "-").Append(',')
              .Append(who != null ? who.ActionProgress.ToString("F3", ci) : "-").Append(',').Append(rig.StringHeld ? 1 : 0).Append(',')
              .Append(rig.DrawTravel.ToString("F3", ci)).Append(',').Append(rig.Draw.ToString("F3", ci)).Append('\n');
            frames++; maxDraw = Mathf.Max(maxDraw, rig.Draw); maxTravel = Mathf.Max(maxTravel, rig.DrawTravel);
            if (!rig.StringHeld && rig.Draw > 0.001f) bentLoose++;
            if (Time.time < until) return;
            System.IO.File.WriteAllText(System.IO.Path.Combine(GKBridge.Dir, "bowlog.txt"), sb.ToString());
            GKBridge.Log("bowlog: " + frames + " frames, string travel max " + (maxTravel * 100f).ToString("F1") + " cm, bend max " + maxDraw.ToString("F2") +
                         ", frames bent with the string not held " + bentLoose + " -> GKBridge/bowlog.txt");
            Destroy(this);
        }
    }
}
