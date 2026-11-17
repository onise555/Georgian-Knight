using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// QUALITY 3c, measured: a button sequence played at a fixed 60 fps against an undying Seljuk, and per attack the
    /// swing plane (fitted to the tip path in the active window; deviation = the steepest the tip leaves it), the edge at
    /// contact (cutting edge vs the tip's motion across the blade), the tip speed curve, the feet's slide while planted,
    /// the frames (startup / active / recovery, contact vs the frame the blade first meets the body) and the reach.
    /// The foe can stand, reel, lie down, walk across or circle, in front, at the side or behind.
    /// </summary>
    public static class GKClipSwingQa
    {
        [GKCommand("swingqa", "swingqa <LLLHHK...> [name] [dist=2] [angle=0] [foe=stand|stagger|down|move|circle]: 60 fps swings vs an undying Seljuk; per attack plane, edge, tip speed, foot slide, frames, reach -> GKBridge/swing/<name>.txt (play mode)")]
        static void Cmd(string arg)
        {
            var p = GameObject.FindWithTag("Player");
            var me = p != null ? p.GetComponent<Combatant>() : null;
            if (me == null || !Application.isPlaying) { GKBridge.Log("swingqa: no player in play mode"); return; }
            var parts = (arg ?? "").Trim().Split(' ').Where(s => s.Length > 0).ToList();
            if (parts.Count == 0) { GKBridge.Log("swingqa: give a sequence, e.g. LLL or HHH"); return; }
            var o = new Options { seq = parts[0].ToUpperInvariant(), name = parts[0] };
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var s in parts.Skip(1))
            {
                if (s.StartsWith("dist=")) float.TryParse(s.Substring(5), System.Globalization.NumberStyles.Float, inv, out o.dist);
                else if (s.StartsWith("angle=")) float.TryParse(s.Substring(6), System.Globalization.NumberStyles.Float, inv, out o.angle);
                else if (s.StartsWith("foe=")) o.foe = s.Substring(4);
                else o.name = s;
            }
            var old = p.GetComponent<GKSwingRecorder>();
            if (old != null) Object.Destroy(old);
            p.AddComponent<GKSwingRecorder>().Begin(me, o);
        }

        public class Options { public string seq, name, foe = "stand"; public float dist = 2f, angle = 0f; }
    }

    [DefaultExecutionOrder(2000)]   // after the animator, the blade solver and the holster: the pose the player sees
    public sealed class GKSwingRecorder : MonoBehaviour
    {
        class Sample { public int f; public float p; public Vector3 tip, bas, wide, lFoot, rFoot, hips; public float gap; }
        class Swing
        {
            public string name; public AttackData data; public int start; public float dist; public string foeState;
            public int contact = -1; public string result = "MISS"; public float minGap = float.MaxValue;
            public readonly List<Sample> s = new List<Sample>();
        }

        Combatant me, foe; GKClipSwingQa.Options opt;
        readonly List<Swing> swings = new List<Swing>();
        int idx, frame; float quietSince = -1f; int deadline;
        Transform sword, lFoot, rFoot, hips; Vector3 wideLocal; float groundY;
        Vector3 home;

        public void Begin(Combatant player, GKClipSwingQa.Options o)
        {
            me = player; opt = o;
            foreach (var w in FindObjectsByType<WaveManager>(FindObjectsSortMode.None)) { w.StopAllCoroutines(); w.enabled = false; }
            foreach (var c in FindObjectsByType<Combatant>(FindObjectsSortMode.None)) if (c != me && c.team != Team.Player) Destroy(c.gameObject);
            var motor = me.GetComponent<CharacterMotor>();
            home = me.transform.position;
            if (me.Stats != null) me.Stats.ResetAll();
            var anim = me.GetComponent<Animator>();
            lFoot = anim.GetBoneTransform(HumanBodyBones.LeftFoot); rFoot = anim.GetBoneTransform(HumanBodyBones.RightFoot);
            hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            sword = me.weapon != null ? me.weapon.transform : null;
            wideLocal = WideAxis();
            RaycastHit gh;
            groundY = Physics.Raycast(home + Vector3.up * 2f, Vector3.down, out gh, 6f, ~0, QueryTriggerInteraction.Ignore) && !gh.collider.transform.IsChildOf(me.transform) ? gh.point.y : home.y;
            Vector3 fwd = GK.Flat(me.transform.forward).normalized;
            Vector3 dirToFoe = Quaternion.AngleAxis(o.angle, Vector3.up) * fwd;
            Vector3 at = home + dirToFoe * o.dist;
            // on a slope the foe stands on the ground there, not at the hero's height (inside the hill or in the air)
            RaycastHit fg;
            if (Physics.Raycast(at + Vector3.up * 5f, Vector3.down, out fg, 15f, ~0, QueryTriggerInteraction.Ignore) && !fg.collider.transform.IsChildOf(me.transform)) at.y = fg.point.y + 0.05f;
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(GKGameBuilder.EnemyPrefabPath);
            var go = Instantiate(prefab, at, Quaternion.LookRotation(GK.Flat(home - at).normalized));
            foreach (var ai in go.GetComponents<EnemyAI>()) ai.enabled = false;
            foe = go.GetComponent<Combatant>();
            var st = go.GetComponent<CombatStats>();
            if (st != null) { st.maxHealth = 1e6f; st.ResetAll(); }
            me.Target = Mathf.Abs(o.angle) < 1f ? foe : null;       // at the side or behind: no lock, the swing must find him
            me.HitDealt += OnDealt; me.AttackStarted += OnStarted;
            Time.captureFramerate = 60;
            deadline = 60 * 60;
            GKBridge.Log("swingqa: " + o.name + " (" + o.seq + ") foe " + o.foe + " at " + o.dist.ToString("F1") + " m, " + o.angle.ToString("F0") + " deg");
        }

        Vector3 WideAxis()
        {
            // the cross-guard's line (a double edge lies along it): the widest extent of the sword mesh across the blade
            if (sword == null || me.weapon.bladeBase == null || me.weapon.bladeTip == null) return Vector3.right;
            Vector3 axis = (me.weapon.bladeTip.position - me.weapon.bladeBase.position).normalized;
            Vector3 best = Vector3.zero; float bestD = -1f;
            foreach (var mf in sword.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                var vs = mf.sharedMesh.vertices;
                Vector3 c = mf.transform.TransformPoint(mf.sharedMesh.bounds.center);
                for (int i = 0; i < vs.Length; i += Mathf.Max(1, vs.Length / 4000))
                {
                    Vector3 d = Vector3.ProjectOnPlane(mf.transform.TransformPoint(vs[i]) - c, axis);
                    if (d.sqrMagnitude > bestD) { bestD = d.sqrMagnitude; best = d; }
                }
            }
            return bestD > 0f ? sword.InverseTransformDirection(best.normalized) : Vector3.right;
        }

        void OnStarted(AttackData a)
        {
            swings.Add(new Swing
            {
                name = a != null ? a.name : "?", data = a, start = frame,
                dist = Vector3.Distance(GK.Flat(me.transform.position), GK.Flat(foe.transform.position)), foeState = foe.CurrentState.ToString()
            });
        }

        void OnDealt(Combatant target, HitData hit, HitResult r)
        {
            if (target != foe || swings.Count == 0) return;
            var s = swings[swings.Count - 1];
            if (s.contact < 0) { s.contact = frame; s.result = r.ToString() + (hit.critical ? " CRIT" : ""); }
        }

        void DriveFoe()
        {
            if (foe == null) return;
            var fm = foe.GetComponent<CharacterMotor>();
            switch (opt.foe)
            {
                case "stagger": if (foe.CurrentState == Combatant.State.Free) foe.ApplyStagger(2f); break;
                case "down": if (foe.CurrentState == Combatant.State.Free) foe.ApplyKnockdown(); break;
                case "move":
                    if (fm != null && foe.CurrentState == Combatant.State.Free)
                    {
                        // across the knight's front and back, 1.2 m/s, turning every 1.5 s
                        Vector3 across = Vector3.Cross(Vector3.up, GK.Flat(foe.transform.position - me.transform.position).normalized);
                        fm.SetLocomotionVelocity(across * (Mathf.Repeat(frame / 90f, 2f) < 1f ? 1.2f : -1.2f));
                    }
                    break;
                case "circle":
                    if (fm != null && foe.CurrentState == Combatant.State.Free)
                    {
                        Vector3 r = GK.Flat(foe.transform.position - me.transform.position);
                        Vector3 tangent = Vector3.Cross(Vector3.up, r.normalized);
                        fm.SetLocomotionVelocity(tangent * 1.5f + r.normalized * (opt.dist - r.magnitude) * 2f);
                    }
                    break;
            }
        }

        void LateUpdate()
        {
            if (me == null || foe == null) { Finish(); return; }
            frame++;
            if (me.Stats != null) me.Stats.ResetAll();
            DriveFoe();
            if (me.CurrentState == Combatant.State.Attacking && swings.Count > 0 && sword != null)
            {
                var s = swings[swings.Count - 1];
                float gap = BladeGap();
                s.minGap = Mathf.Min(s.minGap, gap);
                s.s.Add(new Sample
                {
                    f = frame, p = me.ActionProgress, tip = me.weapon.bladeTip.position, bas = me.weapon.bladeBase.position,
                    wide = sword.TransformDirection(wideLocal), lFoot = lFoot.position, rFoot = rFoot.position, hips = hips.position, gap = gap
                });
            }
            if (idx < opt.seq.Length)
            {
                if (Mathf.Abs(opt.angle) < 1f) me.Target = foe;
                char c = opt.seq[idx];
                bool ok = c == 'K' ? me.RequestKick() : me.RequestAttack(c == 'H' ? AttackKind.Heavy : AttackKind.Light, false);
                if (ok) idx++;
                quietSince = -1f;
            }
            else if (me.CurrentState == Combatant.State.Free)
            {
                if (quietSince < 0f) quietSince = frame;
                if (frame - quietSince > 36) Finish();
            }
            if (frame > deadline) Finish();
        }

        /// <summary>The blade's distance to the foe's body: its hurtbox colliders (limbs, torso, head), where a hit
        /// registers, sampled at 11 points along the edge.</summary>
        float BladeGap()
        {
            if (foeBoxes == null) foeBoxes = foe.GetComponentsInChildren<Hurtbox>(true).SelectMany(h => h.GetComponents<Collider>()).Where(c => c != null).ToArray();
            Vector3 a = me.weapon.bladeBase.position, b = me.weapon.bladeTip.position;
            float best = float.MaxValue;
            foreach (var c in foeBoxes)
            {
                if (!c.enabled || !c.gameObject.activeInHierarchy) continue;
                for (int i = 0; i <= 10; i++)
                {
                    Vector3 q = Vector3.Lerp(a, b, i / 10f);
                    best = Mathf.Min(best, Vector3.Distance(q, c.ClosestPoint(q)));
                }
            }
            return Mathf.Max(0f, best - 0.02f);   // the blade's half-width
        }
        Collider[] foeBoxes;

        bool done;
        void Finish()
        {
            if (done) return;
            done = true;
            Time.captureFramerate = 0;
            if (me != null) { me.HitDealt -= OnDealt; me.AttackStarted -= OnStarted; }
            var sb = new StringBuilder();
            sb.AppendLine("swingqa " + opt.name + " (" + opt.seq + "), foe " + opt.foe + " at " + opt.dist.ToString("F1") + " m / " + opt.angle.ToString("F0") + " deg, 60 fps");
            sb.AppendLine("attack | result | dist m | foe | startup/active/recovery f | contact vs first touch f | plane tilt deg, dev deg | edge deg | tip peak m/s (world) @f, at contact (own vs the hips, cuts) | foot slide cm | verdict");
            int pass = 0;
            for (int i = 0; i < swings.Count; i++)
            {
                var w = swings[i];
                int end = i + 1 < swings.Count ? swings[i + 1].start : (w.s.Count > 0 ? w.s[w.s.Count - 1].f : w.start);
                string line = Measure(w, end, out bool ok);
                if (ok) pass++;
                sb.AppendLine(line);
            }
            sb.AppendLine("PASS " + pass + "/" + swings.Count);
            string dir = Path.Combine(GKBridge.Dir, "swing");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, opt.name + ".txt"), sb.ToString());
            GKBridge.Log("swingqa " + opt.name + ": " + pass + "/" + swings.Count + " PASS (3c) | " + sb.ToString().Replace("\n", " | "));
            if (foe != null) Destroy(foe.gameObject);
            Destroy(this);
        }

        string Measure(Swing w, int end, out bool ok)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            System.Func<float, string> F1 = v => v.ToString("F1", inv);
            var a = w.data; var S = w.s;
            ok = false;
            if (a == null || S.Count < 4) return w.name + " | too few frames";
            int startup = S.Count(x => x.p < a.hitStart), active = S.Count(x => x.p >= a.hitStart && x.p <= a.hitEnd), recovery = S.Count(x => x.p > a.hitEnd);
            // first frame the blade reaches the body surface
            int touch = -1; foreach (var x in S) if (x.gap <= 0.02f) { touch = x.f; break; }
            string contactDelta = w.contact >= 0 && touch >= 0 ? (w.contact - touch).ToString() : (w.contact >= 0 ? "no touch" : "-");
            // tip speed (m/s) per frame
            // in the world (QUALITY 3c: what the eye and the target meet), and the blade's own part relative to the hips
            var speed = new float[S.Count]; var own = new float[S.Count];
            for (int k = 1; k < S.Count; k++)
            {
                speed[k] = (S[k].tip - S[k - 1].tip).magnitude * 60f;
                own[k] = ((S[k].tip - S[k].hips) - (S[k - 1].tip - S[k - 1].hips)).magnitude * 60f;
            }
            int pk = 1; for (int k = 1; k < S.Count; k++) if (speed[k] > speed[pk]) pk = k;
            int ci = w.contact >= 0 ? S.FindIndex(x => x.f >= w.contact) : -1;
            float atContact = ci > 0 ? speed[ci] : 0f;
            // the active window's tip path, relative to the hips (the step and root motion taken out), and its plane
            var win = Enumerable.Range(0, S.Count).Where(k => S[k].p >= a.hitStart - 0.06f && S[k].p <= a.hitEnd + 0.06f).ToList();
            // one plane per cut: a two-cut clip (the combo) is split where the blade slows below a third of its peak
            float tilt = 0f, dev = 0f; int cuts = 0;
            if (win.Count >= 4)
            {
                float wPeak = win.Max(k => own[k]);
                var runs = new List<List<int>>(); List<int> cur = null;
                foreach (int k in win)
                {
                    if (own[k] >= wPeak * 0.33f) { if (cur == null) { cur = new List<int>(); runs.Add(cur); } cur.Add(k); }
                    else cur = null;
                }
                foreach (var run in runs)
                {
                    if (run.Count < 3) continue;
                    var idx = new List<int> { run[0] - 1 }; idx.AddRange(run); idx.RemoveAll(k => k < 0);
                    var pts = idx.Select(k => S[k].tip - S[k].hips).ToList();
                    Vector3 n = PlaneNormal(pts);
                    float tl = Vector3.Angle(n, Vector3.up); if (tl > 90f) tl = 180f - tl;
                    if (cuts == 0 || (ci >= 0 && run.Contains(ci))) tilt = tl;
                    cuts++;
                    for (int k = 1; k < pts.Count; k++)
                    {
                        Vector3 v = pts[k] - pts[k - 1];
                        if (v.magnitude < 0.01f) continue;
                        dev = Mathf.Max(dev, Mathf.Asin(Mathf.Clamp01(Mathf.Abs(Vector3.Dot(v.normalized, n)))) * Mathf.Rad2Deg);
                    }
                }
            }
            // the edge at contact: the cutting edge (along the cross-guard) against the tip's motion across the blade
            float edge = -1f;
            if (ci > 0)
            {
                Vector3 v = S[ci].tip - S[ci - 1].tip, axis = (S[ci].tip - S[ci].bas).normalized;
                Vector3 across = Vector3.ProjectOnPlane(v, axis);
                if (across.magnitude > 1e-4f) { float e = Vector3.Angle(across, S[ci].wide); edge = Mathf.Min(e, 180f - e); }
            }
            // the feet while planted (on the ground, hardly rising): how far each slides over the ground
            float slide = Mathf.Max(Slide(S.Select(x => x.lFoot).ToList()), Slide(S.Select(x => x.rFoot).ToList()));
            bool hit = w.contact >= 0 && w.result != "Dodged";
            bool fPlane = dev <= 10f, fEdge = edge < 0f || edge <= 15f, fSpeed = speed[pk] <= 35f && speed[pk] >= (a.kind == AttackKind.Light || a.kind == AttackKind.Riposte ? 12f : 18f), fSlide = slide <= 3f;
            bool fFrames = touch < 0 || w.contact < 0 || Mathf.Abs(w.contact - touch) <= 1;
            ok = hit && fPlane && fEdge && fSpeed && fSlide && fFrames;
            string why = ok ? "PASS" : "FAIL(" + string.Join(",", new[] { hit ? null : "miss", fPlane ? null : "plane", fEdge ? null : "edge", fSpeed ? null : "speed", fSlide ? null : "slide", fFrames ? null : "frames" }.Where(x => x != null)) + ")";
            return w.name + " | " + w.result + (w.contact < 0 ? " by " + (w.minGap * 100f).ToString("F0", inv) + " cm" : "") + " | " + F1(w.dist) + " | " + w.foeState +
                   " | " + startup + "/" + active + "/" + recovery + " | " + contactDelta + " | " + F1(tilt) + ", " + F1(dev) + " | " + (edge < 0f ? "-" : F1(edge)) +
                   " | " + F1(speed[pk]) + " @" + S[pk].f + ", " + F1(atContact) + " (own " + F1(own.Max()) + ", " + cuts + " cut" + (cuts == 1 ? "" : "s") + ") | " + F1(slide) + " | " + why;
        }

        float Slide(List<Vector3> foot)
        {
            // planted: the ankle within 2 cm of its lowest in the swing and hardly rising or sinking (the foot bone is at
            // the ankle, 8-10 cm up: a fixed height over the ground counted the stepping foot as planted)
            float minY = float.MaxValue; foreach (var f in foot) minY = Mathf.Min(minY, f.y);
            float worst = 0f, run = 0f;
            for (int k = 1; k < foot.Count; k++)
            {
                bool planted = foot[k].y < minY + 0.02f && foot[k - 1].y < minY + 0.02f && Mathf.Abs(foot[k].y - foot[k - 1].y) * 60f < 0.15f;
                if (planted) { run += GK.Flat(foot[k] - foot[k - 1]).magnitude; worst = Mathf.Max(worst, run); }
                else run = 0f;
            }
            return worst * 100f;
        }

        /// <summary>Normal of the best-fit plane through the points (the covariance's smallest axis, by power iteration
        /// on the two largest).</summary>
        static Vector3 PlaneNormal(List<Vector3> pts)
        {
            Vector3 c = Vector3.zero; foreach (var p in pts) c += p; c /= pts.Count;
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var p0 in pts) { var p = p0 - c; xx += p.x * p.x; xy += p.x * p.y; xz += p.x * p.z; yy += p.y * p.y; yz += p.y * p.z; zz += p.z * p.z; }
            System.Func<Vector3, Vector3> M = v => new Vector3(xx * v.x + xy * v.y + xz * v.z, xy * v.x + yy * v.y + yz * v.z, xz * v.x + yz * v.y + zz * v.z);
            Vector3 e1 = new Vector3(1f, 0.3f, 0.2f).normalized;
            for (int i = 0; i < 30; i++) { e1 = M(e1); if (e1.sqrMagnitude < 1e-12f) return Vector3.up; e1.Normalize(); }
            Vector3 e2 = Vector3.Cross(e1, Vector3.up).sqrMagnitude > 1e-4f ? Vector3.Cross(e1, Vector3.up).normalized : Vector3.Cross(e1, Vector3.right).normalized;
            for (int i = 0; i < 30; i++) { e2 = M(e2); e2 -= Vector3.Dot(e2, e1) * e1; if (e2.sqrMagnitude < 1e-12f) break; e2.Normalize(); }
            return Vector3.Cross(e1, e2).normalized;
        }

        void OnDestroy() { if (!done) Time.captureFramerate = 0; }
    }
}
