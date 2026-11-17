using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// The camera with three foes (QUALITY camera rules): three Seljuks stand 3 m away, left, ahead and right (AI off);
    /// a gameplay-camera still of the 1 v 3 in each framing preset; then the lock goes to the left one and switches to the
    /// right one, and the camera's yaw is followed at 60 fps until it holds within 1 deg: the switch time (rule: 0.25 s).
    /// </summary>
    public static class GKClipLockQa
    {
        [GKCommand("lockqa", "lockqa <name> [pack4]: three standing foes (or four packed tight), a 1v3 still per camera preset, then the lock-on switch time left -> right (play mode)")]
        static void Cmd(string arg)
        {
            var p = GameObject.FindWithTag("Player");
            if (p == null || !Application.isPlaying) { GKBridge.Log("lockqa: no player in play mode"); return; }
            var old = p.GetComponent<GKLockRecorder>(); if (old != null) Object.Destroy(old);
            var parts = (arg ?? "").Trim().Split(' ');
            var r = p.AddComponent<GKLockRecorder>();
            r.pack4 = parts.Length > 1 && parts[1] == "pack4";
            r.Begin(parts[0].Length > 0 ? parts[0] : "lockqa");
        }
    }

    public sealed class GKLockRecorder : MonoBehaviour
    {
        string name0; int frame; Combatant[] foes = new Combatant[3]; public bool pack4; PlayerCamera cam; LockOnSystem lockOn;
        int phase; int switchFrame = -1; float finalYaw; int stable;
        System.Collections.Generic.List<float> yaws = new System.Collections.Generic.List<float>(), times = new System.Collections.Generic.List<float>();

        public void Begin(string n)
        {
            name0 = n;
            foreach (var w in FindObjectsByType<WaveManager>(FindObjectsSortMode.None)) { w.StopAllCoroutines(); w.enabled = false; }
            var me = GetComponent<Combatant>();
            foreach (var c in FindObjectsByType<Combatant>(FindObjectsSortMode.None)) if (c != me && c.team != Team.Player) Destroy(c.gameObject);
            cam = FindAnyObjectByType<PlayerCamera>(); lockOn = GetComponent<LockOnSystem>();
            Vector3 F = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(GKGameBuilder.EnemyPrefabPath);
            // pack4: four foes packed tight (the Team Lead's case for the clearance), else the 1 v 3 at -45 / 0 / +45
            float[] ang = pack4 ? new[] { -30f, -10f, 10f, 30f } : new[] { -45f, 0f, 45f };
            foes = new Combatant[ang.Length];
            for (int i = 0; i < ang.Length; i++)
            {
                Vector3 at = transform.position + Quaternion.AngleAxis(ang[i], Vector3.up) * F * 3f;
                RaycastHit h; if (Physics.Raycast(at + Vector3.up * 5f, Vector3.down, out h, 15f, ~0, QueryTriggerInteraction.Ignore)) at.y = h.point.y + 0.05f;
                var go = Instantiate(prefab, at, Quaternion.LookRotation(GK.Flat(transform.position - at).normalized));
                foreach (var ai in go.GetComponents<EnemyAI>()) ai.enabled = false;
                foes[i] = go.GetComponent<Combatant>();
                foes[i].Target = GetComponent<Combatant>();   // engaged on the hero, so the camera frames a fight (AI off)
            }
            Time.captureFramerate = 60;
            GKBridge.Log("lockqa: " + n + ": " + ang.Length + " foes at 3 m (" + string.Join(", ", System.Array.ConvertAll(ang, a => a.ToString("+0;-0"))) + " deg)");
        }

        /// <summary>Per foe, from the output camera: the angular gap between his chest and the hero's against the gap that
        /// keeps him clear of the hero's silhouette (shoulders 0.34 m, foe 0.3 m half-width); "HIDDEN" when short.</summary>
        /// A foe is also clear when his chest shows over the hero's head (height). The spare is the larger of the two
        /// margins (rule: 2 deg).
        string Hidden()
        {
            var c = Camera.main; if (c == null) return "no camera";
            Transform ct = c.transform;
            Vector3 h = ct.InverseTransformPoint(transform.position + Vector3.up * 1.25f);
            Vector3 top = ct.InverseTransformPoint(transform.position + Vector3.up * 1.85f);
            float hAz = Mathf.Atan2(h.x, h.z) * Mathf.Rad2Deg, hHalf = Mathf.Atan2(0.34f, h.z) * Mathf.Rad2Deg, topEl = Mathf.Atan2(top.y, top.z) * Mathf.Rad2Deg;
            var sb = new System.Text.StringBuilder();
            float worst = float.MaxValue;
            for (int i = 0; i < foes.Length; i++)
            {
                if (foes[i] == null) continue;
                Vector3 f = ct.InverseTransformPoint(foes[i].transform.position + Vector3.up * 1.25f);
                float gap = Mathf.Abs(Mathf.DeltaAngle(Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg, hAz));
                float need = hHalf + Mathf.Atan2(0.3f, f.z) * Mathf.Rad2Deg;
                float over = Mathf.Atan2(f.y, f.z) * Mathf.Rad2Deg - topEl;
                float spare = Mathf.Max(gap - need, over);
                worst = Mathf.Min(worst, spare);
                sb.Append("foe").Append(i + 1).Append(spare >= 0f ? " clear " : " HIDDEN ").Append("side ").Append((gap - need).ToString("+0.0;-0.0"))
                  .Append(" over ").Append(over.ToString("+0.0;-0.0")).Append(" deg; ");
            }
            return "worst spare " + worst.ToString("+0.0;-0.0") + " deg (rule +2) | " + sb;
        }

        void LateUpdate()
        {
            frame++;
            if (phase == 0 && frame == 40) phase = 1;
            if (phase == 1)
            {
                // the 1 v 3 stills, one preset per 120 frames (time for the framing and the clearance orbit to settle)
                int k = (frame - 40) / 120, sub = (frame - 40) % 120;
                if (k < 6)
                {
                    // without the threat clearance first (the before), then with it
                    var pr = (PlayerCamera.FramingPreset)(k % 3);
                    bool clear = k >= 3;
                    if (sub == 0) { PlayerCamera.SetPreset(pr); if (cam != null) cam.threatClearance = clear; }
                    if (sub == 115) { Shot(name0 + "_1v3_" + pr + (clear ? "" : "_noclear")); GKBridge.Log("lockqa: " + name0 + ": preset " + pr + " mode " + (cam != null ? cam.CurrentMode.ToString() : "?") + " orbit " + (cam != null ? cam.ClearYaw.ToString("F1") : "?") + " deg lift " + (cam != null ? (cam.ClearLift * 100f).ToString("F0") : "?") + " cm; " + Hidden()); }
                    return;
                }
                PlayerCamera.SetPreset(PlayerCamera.FramingPreset.A);
                if (lockOn != null) lockOn.LockOn(foes[0]);
                phase = 2; switchFrame = frame + 60;
            }
            if (phase == 2 && cam != null)
            {
                if (frame == switchFrame && lockOn != null) lockOn.LockOn(foes[foes.Length - 1]);
                if (frame >= switchFrame)
                {
                    yaws.Add(cam.Yaw); times.Add(Time.unscaledTime);
                    if (yaws.Count >= 2 && Mathf.Abs(Mathf.DeltaAngle(yaws[yaws.Count - 1], yaws[yaws.Count - 2])) < 0.2f) stable++; else stable = 0;
                    if (stable >= 10 || yaws.Count > 240) Finish();
                }
            }
        }

        void Shot(string n)
        {
            var main = Camera.main;
            if (main == null) return;
            GKPreview.Capture(main, n, 1280, 720);
        }

        void Finish()
        {
            finalYaw = yaws[yaws.Count - 1];
            float total = Mathf.Abs(Mathf.DeltaAngle(yaws[0], finalYaw));
            int within = yaws.Count - 1;
            for (int i = 0; i < yaws.Count; i++) if (Mathf.Abs(Mathf.DeltaAngle(yaws[i], finalYaw)) <= 1f) { within = i; break; }
            // the camera runs on real (unscaled) time: the switch time is read off the real clock, not the 60 fps frames
            float secs = times[within] - times[0];
            int at025 = times.FindIndex(x => x - times[0] >= 0.25f);
            GKBridge.Log("lockqa: " + name0 + ": switch left -> right turned the camera " + total.ToString("F0") + " deg, within 1 deg of its final yaw after " +
                         secs.ToString("F2") + " s real time (rule 0.25 s); 0.25 s after the switch it was " +
                         (at025 >= 0 ? Mathf.Abs(Mathf.DeltaAngle(yaws[at025], finalYaw)).ToString("F1") : "?") + " deg off");
            Time.captureFramerate = 0;
            foreach (var f in foes) if (f != null) Destroy(f.gameObject);
            Destroy(this);
        }

        void OnDestroy() { Time.captureFramerate = 0; }
    }
}
