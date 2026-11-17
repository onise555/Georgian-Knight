using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// The running jump strike (the Director: "jumping and striking from the air with the left button, the last strike
    /// misses"). Through the player's own input path (GKInput injection, so the controller's buffer and the air strike
    /// rules apply): the hero sprints at a standing Seljuk (AI off, cannot die, reels as in a fight), jumps when he is
    /// jumpAt metres away, and mashes the light button until <c>presses</c> attacks have started, holding the stick
    /// forward all along as a player does. Every swing is logged: hit or the blade's miss distance, the distance and the
    /// hero's speed when it began, and the foe's state.
    /// </summary>
    public static class GKClipRunJumpQa
    {
        [GKCommand("runjumpqa", "runjumpqa <name> [jumpAt=3.5] [presses=3] [lock=1] [hold=1]: sprint at a Seljuk, jump at jumpAt m, mash light in the air, log every swing (play mode)")]
        static void Cmd(string arg)
        {
            var p = GameObject.FindWithTag("Player");
            if (p == null || !Application.isPlaying) { GKBridge.Log("runjumpqa: no player in play mode"); return; }
            var old = p.GetComponent<GKRunJumpRecorder>(); if (old != null) Object.Destroy(old);
            var parts = (arg ?? "").Trim().Split(' ');
            var r = p.AddComponent<GKRunJumpRecorder>();
            r.name0 = parts.Length > 0 && parts[0].Length > 0 && !parts[0].Contains("=") ? parts[0] : "runjump";
            foreach (var s in parts)
            {
                int e = s.IndexOf('='); if (e < 0) continue;
                string k = s.Substring(0, e); float v;
                if (!float.TryParse(s.Substring(e + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) continue;
                if (k == "jumpAt") r.jumpAt = v; else if (k == "presses") r.presses = (int)v; else if (k == "lock") r.useLock = v > 0f; else if (k == "hold") r.holdForward = v > 0f;
            }
            r.Begin();
        }
    }

    public sealed class GKRunJumpRecorder : MonoBehaviour
    {
        public string name0 = "runjump"; public float jumpAt = 3.5f; public int presses = 3; public bool useLock = true, holdForward = true;

        class Swing { public string name, foeState, result = ""; public float dist, speed, gap = float.MaxValue; public bool air, hit; }
        readonly List<Swing> swings = new List<Swing>();
        Combatant me, foe; GKInput input; CharacterMotor motor;
        int phase, frame, lastPress; float deadline, quietSince = -1f, jumpDist; bool wasAir, landed, done;

        public void Begin()
        {
            me = GetComponent<Combatant>(); input = GetComponent<GKInput>(); motor = GetComponent<CharacterMotor>();
            foreach (var w in FindObjectsByType<WaveManager>(FindObjectsSortMode.None)) { w.StopAllCoroutines(); w.enabled = false; }
            foreach (var c in FindObjectsByType<Combatant>(FindObjectsSortMode.None)) if (c != me && c.team != Team.Player) Destroy(c.gameObject);
            Vector3 F = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            Vector3 at = transform.position + F * 9f;
            RaycastHit h; if (Physics.Raycast(at + Vector3.up * 5f, Vector3.down, out h, 15f, ~0, QueryTriggerInteraction.Ignore)) at.y = h.point.y + 0.05f;
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(GKGameBuilder.EnemyPrefabPath);
            var go = Instantiate(prefab, at, Quaternion.LookRotation(-F));
            foreach (var ai in go.GetComponents<EnemyAI>()) ai.enabled = false;
            foe = go.GetComponent<Combatant>();
            var st = go.GetComponent<CombatStats>(); if (st != null) { st.maxHealth = 1e6f; st.ResetAll(); }
            if (me.Stats != null) me.Stats.ResetAll();
            me.HitDealt += Dealt; me.AttackStarted += Started;
            input.ignoreDevices = true;
            deadline = Time.realtimeSinceStartup + 20f;
            GKBridge.Log("runjumpqa: " + name0 + ": sprint at a Seljuk 9 m ahead, jump at " + jumpAt.ToString("F1") + " m, " + presses +
                         " light presses from the air, lock " + (useLock ? "on" : "off") + ", stick " + (holdForward ? "held forward" : "released at the jump"));
        }

        float Dist() { return Vector3.Distance(GK.Flat(transform.position), GK.Flat(foe.transform.position)); }

        void Started(AttackData a)
        {
            if (foe == null) return;
            swings.Add(new Swing { name = a != null ? a.name : "?", dist = Dist(), speed = GK.Flat(motor.Velocity).magnitude, air = !motor.IsGrounded, foeState = foe.CurrentState.ToString() });
        }

        void Dealt(Combatant target, HitData hit, HitResult r)
        {
            if (target != foe || swings.Count == 0) return;
            var s = swings[swings.Count - 1]; s.hit = true; s.result = r.ToString();
        }

        void Update()
        {
            if (done) return;
            if (me == null || foe == null) { Finish(); return; }
            frame++;
            if (me.Stats != null) me.Stats.ResetAll();
            if (me.CurrentState == Combatant.State.Attacking && swings.Count > 0) { var s = swings[swings.Count - 1]; s.gap = Mathf.Min(s.gap, GKClothQa.BladeGap(me, foe)); }
            if (phase == 0)
            {
                // the run-up: sprint at him (locked on as in a duel, or free)
                var lockOn = GetComponent<LockOnSystem>();
                if (frame == 2 && useLock && lockOn != null) lockOn.LockOn(foe);
                input.InjectMove(Vector2.up, true);
                input.Inject(GKInput.Injected.Sprint, true);
                if (frame > 20 && Dist() <= jumpAt)
                {
                    // one injected button at a time: the sprint is let go at the jump (the jump reads the run speed)
                    input.Inject(GKInput.Injected.Sprint, false); input.Inject(GKInput.Injected.Jump, true);
                    jumpDist = Dist(); phase = 1; lastPress = frame;
                }
            }
            else
            {
                if (!holdForward) input.InjectMove(Vector2.zero, false);
                bool air = !motor.IsGrounded;
                if (wasAir && !air) landed = true;
                wasAir = air;
                // mash: a fresh light press every 6 frames once he is off the ground, until enough attacks have begun
                if (swings.Count < presses && (air || landed) && frame - lastPress >= 6)
                {
                    input.Inject(GKInput.Injected.Jump, false); input.Inject(GKInput.Injected.Light, false);
                    input.Inject(GKInput.Injected.Light, true); lastPress = frame;
                }
                if (swings.Count >= presses)
                {
                    input.Inject(GKInput.Injected.Light, false);
                    if (me.CurrentState == Combatant.State.Free)
                    {
                        if (quietSince < 0f) quietSince = Time.realtimeSinceStartup;
                        if (Time.realtimeSinceStartup - quietSince > 0.6f) Finish();
                    }
                    else quietSince = -1f;
                }
            }
            if (Time.realtimeSinceStartup > deadline) Finish();
        }

        void Finish()
        {
            if (done) return;
            done = true;
            if (input != null)
            {
                input.InjectMove(Vector2.zero, false);
                input.Inject(GKInput.Injected.Light, false); input.Inject(GKInput.Injected.Sprint, false); input.Inject(GKInput.Injected.Jump, false);
                input.ignoreDevices = false;
            }
            if (me != null) { me.HitDealt -= Dealt; me.AttackStarted -= Started; }
            int hits = 0; foreach (var s in swings) if (s.hit) hits++;
            var sb = new System.Text.StringBuilder("runjumpqa " + name0 + ": " + hits + "/" + swings.Count + " connected (jumped at " + jumpDist.ToString("F1") + " m) |");
            foreach (var s in swings)
                sb.Append(' ').Append(s.name).Append(s.air ? " [air]" : "").Append(s.hit ? " " + s.result : " MISS by " + (s.gap < 1e5f ? (s.gap * 100f).ToString("F0") : "?") + " cm")
                  .Append(" (at ").Append(s.dist.ToString("F1")).Append(" m, ").Append(s.speed.ToString("F1")).Append(" m/s, foe ").Append(s.foeState).Append(") |");
            GKBridge.Log(sb.ToString());
            if (foe != null) Destroy(foe.gameObject);
            Destroy(this);
        }
    }
}
