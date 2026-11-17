using System.Globalization;
using System.Text;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Play-mode QA for ladder climbing ("ladderqa up|down"). Puts the player at the foot (or the top) of the nearest
    /// ladder, starts the climb through the same API the interact button uses, holds the stick, and measures every frame
    /// after animation and IK:
    ///   * contact quality: wrist / ankle / toe distance to the nearest rung axis (inside the rung = penetration),
    ///     how far IK had to pull a limb, the loop's phase error against the rung lattice;
    ///   * continuity: hips displacement per frame relative to the expected climb motion (pops), root jumps;
    ///   * body vs. building: capsule-sized probes of torso and head against the environment layer;
    ///   * timing and the warp corrections of mount / exit.
    /// "ladderqa report" (or the run ending) writes GKBridge/ladderqa.txt.
    /// </summary>
    [DefaultExecutionOrder(2100)]
    public sealed class GKLadderProbe : MonoBehaviour
    {
        public string Mode = "up";
        /// <summary>Set by the editor bridge: where the report goes and how a line reaches the bridge log.</summary>
        public static string OutDir;
        public static System.Action<string> Log;
        LadderClimber climber;
        GKInput input;
        Animator anim;
        Transform hips, head, chest, lh, rh, lf, rf, lt, rt;
        Ladder ladder;
        float t0, lastSwitch;
        Vector3 lastHips, lastRoot;
        bool has, finished, started;
        int frames;
        float worstHand = 9f, worstFoot = 9f, worstToe = 9f, worstPull, worstPhase, worstHipsJump, worstRootJump, worstBody;
        string worstHandAt = "", worstFootAt = "", worstToeAt = "", worstPullAt = "", worstHipsAt = "", worstRootAt = "", worstBodyAt = "";
        readonly StringBuilder log = new StringBuilder();
        LadderClimber.Phase lastPhase;

        static string F(float v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }

        void Start()
        {
            climber = GetComponent<LadderClimber>();
            input = GetComponent<GKInput>();
            anim = GetComponent<Animator>();
            if (climber == null || anim == null) { log.AppendLine("no climber"); finished = true; return; }
            hips = anim.GetBoneTransform(HumanBodyBones.Hips); head = anim.GetBoneTransform(HumanBodyBones.Head);
            chest = anim.GetBoneTransform(HumanBodyBones.Chest) ?? hips;
            lh = anim.GetBoneTransform(HumanBodyBones.LeftHand); rh = anim.GetBoneTransform(HumanBodyBones.RightHand);
            lf = anim.GetBoneTransform(HumanBodyBones.LeftFoot); rf = anim.GetBoneTransform(HumanBodyBones.RightFoot);
            lt = anim.GetBoneTransform(HumanBodyBones.LeftToes) ?? lf; rt = anim.GetBoneTransform(HumanBodyBones.RightToes) ?? rf;
            float best = float.MaxValue;
            foreach (var l in Ladder.All)
            {
                if (l == null || !l.IsValid) continue;
                float d = Vector3.Distance(transform.position, l.standBottom.position);
                if (d < best) { best = d; ladder = l; }
            }
            if (ladder == null) { log.AppendLine("no ladder in the scene"); finished = true; return; }
            var motor = GetComponent<CharacterMotor>();
            Vector3 at = Mode == "down" ? ladder.standTop.position : ladder.standBottom.position;
            Quaternion face = Quaternion.LookRotation(GK.Flat(ladder.Forward).normalized * (Mode == "down" ? -1f : 1f), Vector3.up);
            if (motor != null) motor.Teleport(at + Vector3.up * 0.05f, face); else transform.SetPositionAndRotation(at, face);
            t0 = Time.time;
            log.AppendLine("ladderqa " + Mode + ": ladder pitch " + F(ladder.Pitch) + " rungs " + ladder.rungCount + " lean " + F(Vector3.Angle(ladder.Up, Vector3.up)) +
                           " planeZ " + F(climber.profile != null ? climber.profile.planeZ : 0f));
        }

        void Update()
        {
            if (finished || climber == null || ladder == null) return;
            float t = Time.time - t0;
            if (!started && t > 0.4f)
            {
                started = Mode == "down" ? climber.BeginFromTop(ladder) : climber.BeginFromBottom(ladder);
                log.AppendLine(F(t) + " begin -> " + started);
                if (!started) { Finish("could not start"); return; }
            }
            if (started && climber.Current == LadderClimber.Phase.Climbing)
                input.InjectMove(new Vector2(0f, Mode == "down" ? -1f : 1f), true);
            if (started && climber.Current == LadderClimber.Phase.None && t > 1f) Finish("climb finished");
            if (t > 40f) Finish("timeout");
        }

        void LateUpdate()
        {
            if (finished || !started || climber == null || ladder == null) return;
            frames++;
            float t = Time.time - t0;
            var ph = climber.Current;
            if (ph != lastPhase)
            {
                log.AppendLine(F(t) + " phase " + ph + " along " + F(climber.Along) + " pos " + transform.position.ToString("F3") + " warp " + F(climber.LastWarp));
                lastPhase = ph;
            }
            bool onLadder = ph == LadderClimber.Phase.Mounting || ph == LadderClimber.Phase.Climbing || ph == LadderClimber.Phase.Exiting;
            if (onLadder)
            {
                string at = F(t) + " " + ph;
                Check(lh, 0.0f, ref worstHand, ref worstHandAt, at + " L hand");
                Check(rh, 0.0f, ref worstHand, ref worstHandAt, at + " R hand");
                Check(lf, 0.0f, ref worstFoot, ref worstFootAt, at + " L ankle");
                Check(rf, 0.0f, ref worstFoot, ref worstFootAt, at + " R ankle");
                Check(lt, 0.0f, ref worstToe, ref worstToeAt, at + " L toes");
                Check(rt, 0.0f, ref worstToe, ref worstToeAt, at + " R toes");
                if (climber.LastIkPull > worstPull) { worstPull = climber.LastIkPull; worstPullAt = at; }
                if (ph == LadderClimber.Phase.Climbing) worstPhase = Mathf.Max(worstPhase, Mathf.Abs(climber.LastPhaseError));
                // body inside the building: torso and head spheres against the environment colliders
                float pen = Penetration(chest.position, 0.16f) + 0f;
                pen = Mathf.Max(pen, Penetration(head.position, 0.11f));
                if (pen > worstBody) { worstBody = pen; worstBodyAt = at; }
            }
            Vector3 hp = transform.InverseTransformPoint(hips.position);
            if (has)
            {
                float hj = (hp - lastHips).magnitude;
                if (hj > worstHipsJump) { worstHipsJump = hj; worstHipsAt = F(t) + " " + ph; }
                float rj = (transform.position - lastRoot).magnitude;
                if (rj > worstRootJump) { worstRootJump = rj; worstRootAt = F(t) + " " + ph; }
            }
            lastHips = hp; lastRoot = transform.position; has = true;
        }

        /// <summary>Distance from a joint to the nearest rung's surface (negative = inside the rung).</summary>
        void Check(Transform b, float radius, ref float worst, ref string where, string label)
        {
            if (b == null) return;
            float best = float.MaxValue;
            for (int i = 0; i < ladder.rungCount; i++)
            {
                Vector3 c = ladder.RungCenter(i);
                Vector3 d = b.position - c;
                float lat = Vector3.Dot(d, ladder.Right);
                if (Mathf.Abs(lat) > ladder.halfWidth) continue;
                Vector3 radial = d - ladder.Right * lat;
                best = Mathf.Min(best, radial.magnitude - ladder.rungRadius - radius);
            }
            if (best < worst) { worst = best; where = label; }
        }

        static readonly Collider[] hits = new Collider[8];
        static float Penetration(Vector3 p, float r)
        {
            int n = Physics.OverlapSphereNonAlloc(p, r, hits, 1 << GK.LayerEnvironment, QueryTriggerInteraction.Ignore);
            float worst = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector3 c = hits[i].ClosestPoint(p);
                float d = (c - p).magnitude;
                worst = Mathf.Max(worst, r - d);
            }
            return worst;
        }

        void Finish(string why)
        {
            if (finished) return;
            finished = true;
            if (input != null) input.InjectMove(Vector2.zero, false);
            log.AppendLine(F(Time.time - t0) + " end: " + why + " (" + frames + " frames)");
            Write();
            if (Log != null) Log("ladderqa: " + why + " -> GKBridge/ladderqa.txt");
        }

        public string Summary()
        {
            var s = new StringBuilder(log.ToString());
            s.AppendLine("closest wrist to a rung surface " + F(worstHand) + " m at " + worstHandAt + "   (wrist centre; < 0.02 = hand inside the rung)");
            s.AppendLine("closest ankle to a rung surface " + F(worstFoot) + " m at " + worstFootAt + "   (< 0.04 = foot inside the rung)");
            s.AppendLine("closest toes to a rung surface  " + F(worstToe) + " m at " + worstToeAt + "   (< 0.0 = toes inside the rung)");
            s.AppendLine("largest IK pull " + F(worstPull) + " m at " + worstPullAt + "; worst loop phase error " + F(worstPhase) + " m");
            s.AppendLine("largest hips step (root space) " + F(worstHipsJump) + " m at " + worstHipsAt + "; largest root step " + F(worstRootJump) + " m at " + worstRootAt);
            s.AppendLine("deepest body penetration into the building " + F(worstBody) + " m at " + worstBodyAt);
            return s.ToString();
        }

        public void Write()
        {
            string dir = OutDir ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "GKBridge");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "ladderqa.txt"), Summary());
        }
    }
}
