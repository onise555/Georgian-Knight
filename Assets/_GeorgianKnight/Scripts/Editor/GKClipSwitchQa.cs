using System.Text;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// S1 through the player's own input path (GKInput injection): melee A -> B -> A (SwitchStarted, its duration, when
    /// ActiveWeaponChanged lands), a B asked for in the middle of a light chain (queued, run when the blow ends), the hold
    /// to sheathe and draw again, and the kick from its press to its first frame (UI's tracker; QUALITY §3b).
    /// The recorder stays in this file: an Editor-folder MonoBehaviour with a file of its own name cannot be added in play.
    /// </summary>
    public static class GKClipSwitchQa
    {
        [GKCommand("switchqa", "switchqa <name>: weapon slots A -> B -> A, a switch asked mid-chain, sheathe / draw, kick press-to-first-frame (play mode)")]
        static void Cmd(string arg)
        {
            var p = GameObject.FindWithTag("Player");
            if (p == null || !Application.isPlaying) { GKBridge.Log("switchqa: no player in play mode"); return; }
            var old = p.GetComponent<GKSwitchRecorder>(); if (old != null) Object.Destroy(old);
            p.AddComponent<GKSwitchRecorder>().Begin(string.IsNullOrEmpty(arg) ? "switch" : arg.Trim());
        }
    }

    public sealed class GKSwitchRecorder : MonoBehaviour
    {
        string name0; Equipment eq; Combatant me; GKInput input; WeaponHolster holster;
        int step, frame, stepFrame; float stepAt, startedAt = -1f, changedAt = -1f, startedDur, kickPressAt = -1f, kickAt = -1f;
        EquipSlot startedSlot;
        readonly StringBuilder sb = new StringBuilder();

        public void Begin(string n)
        {
            name0 = n;
            me = GetComponent<Combatant>(); eq = GetComponent<Equipment>(); input = GetComponent<GKInput>(); holster = GetComponent<WeaponHolster>();
            if (eq == null || input == null) { GKBridge.Log("switchqa: no Equipment / GKInput on the player"); Destroy(this); return; }
            foreach (var w in FindObjectsByType<WaveManager>(FindObjectsSortMode.None)) { w.StopAllCoroutines(); w.enabled = false; }
            foreach (var c in FindObjectsByType<Combatant>(FindObjectsSortMode.None)) if (c != me && c.team != Team.Player) Destroy(c.gameObject);
            eq.SwitchStarted += (s, d) => { startedAt = Time.time; startedSlot = s; startedDur = d; };
            eq.ActiveWeaponChanged += (a, b) => changedAt = Time.time;
            me.AttackStarted += a => { if (a != null && a.kind == AttackKind.Kick && kickAt < 0f) kickAt = Time.time; };
            input.ignoreDevices = true;
            GKBridge.Log("switchqa: " + n + ": starts in " + eq.ActiveMelee + " (" + (GetComponent<FightingStyles>() != null ? GetComponent<FightingStyles>().Style.ToString() : "?") + ")");
            Next(1);
        }

        void Next(int s) { step = s; stepAt = Time.time; stepFrame = frame; startedAt = changedAt = -1f; }

        void Press(GKInput.Injected b) { input.Inject(b, false); input.Inject(b, true); }

        void Update()
        {
            frame++;
            if (me.Stats != null) me.Stats.ResetAll();
            float t = Time.time - stepAt;
            switch (step)
            {
                case 1: if (frame - stepFrame == 2) Press(GKInput.Injected.SlotB); if (t > 3f) { Report("A -> B"); Next(2); } break;
                case 2: if (frame - stepFrame == 2) Press(GKInput.Injected.SlotA); if (t > 3f) { Report("B -> A"); Next(3); } break;
                case 3:
                    // a light chain, and B asked for while the first blow is still in the air
                    if (frame - stepFrame == 2) Press(GKInput.Injected.Light);
                    if (frame - stepFrame == 10) Press(GKInput.Injected.SlotB);
                    if (frame - stepFrame == 11) sb.Append(" | mid-chain B asked in ").Append(me.CurrentState).Append(startedAt < 0f ? " (queued)" : " (began at once)");
                    if (t > 3.5f) { Report("B asked mid-chain"); Next(4); }
                    break;
                case 4:
                    if (frame - stepFrame == 2) Press(GKInput.Injected.Holster);
                    if (t > 3f) { sb.Append(" | sheathe: ").Append(holster != null ? (holster.IsDrawn ? "still drawn" : "sheathed") : "no holster"); Next(5); }
                    break;
                case 5:
                    if (frame - stepFrame == 2) Press(GKInput.Injected.Holster);
                    if (t > 3f) { sb.Append(" | draw: ").Append(holster != null ? (holster.IsDrawn ? "drawn" : "still sheathed") : "no holster"); Next(6); }
                    break;
                case 6:
                    if (frame - stepFrame == 2) { kickAt = -1f; kickPressAt = Time.time; Press(GKInput.Injected.Kick); }
                    if (t > 2f)
                    {
                        sb.Append(" | kick press to first frame ").Append(kickAt >= 0f ? ((kickAt - kickPressAt) * 1000f).ToString("F0") + " ms" : "none");
                        Finish();
                    }
                    break;
            }
        }

        void Report(string what)
        {
            sb.Append(" | ").Append(what).Append(": ");
            if (startedAt < 0f) sb.Append("no switch began");
            else sb.Append("began ").Append(((startedAt - stepAt) * 1000f).ToString("F0")).Append(" ms after the press, to ").Append(startedSlot)
                   .Append(", ").Append(startedDur.ToString("F2")).Append(" s announced");
            sb.Append(changedAt >= 0f ? ", weapon changed " + ((changedAt - stepAt)).ToString("F2") + " s after the press" : ", no weapon change");
            sb.Append(", now ").Append(eq.ActiveMelee);
        }

        void Finish()
        {
            input.Inject(GKInput.Injected.Kick, false);
            input.ignoreDevices = false;
            GKBridge.Log("switchqa " + name0 + ":" + sb);
            Destroy(this);
        }
    }
}
