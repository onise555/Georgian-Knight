using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Animation QA for the held weapon. Samples every clip of the player's animator at 60 Hz on the real character and
    /// reports, per clip: blade self-penetration (blade inside the own torso/head/legs/left arm), frames where the blade
    /// sinks into the ground, and jitter (frame-to-frame blade direction reversals at high angular speed).
    /// "swordcheck [filter]" — results in the bridge log and GKBridge/swordcheck.txt.
    /// </summary>
    public static class GKSwordCheck
    {
        [GKCommand("swordcheck", "swordcheck [clip filter]: blade self-intersection / jitter report")]
        public static void Run(string filter)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKGameBuilder.PlayerPrefabPath);
            if (prefab == null) { GKBridge.Log("swordcheck: no player prefab"); return; }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            inst.transform.SetPositionAndRotation(new Vector3(0f, 5000f, 0f), Quaternion.identity);
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "~swordcheck_ground";
            ground.transform.position = new Vector3(0f, 5000f - 0.5f, 0f);
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
            var report = new StringBuilder();
            var solver = inst.GetComponent<WeaponSelfCollision>();
            if (solver != null) solver.Init();
            report.AppendLine("sword: raw = authored animation, fixed = after WeaponSelfCollision (" + (solver != null ? "present" : "MISSING") + ")");
            int bad = 0, total = 0;
            try
            {
                var anim = inst.GetComponent<Animator>();
                var rag = inst.GetComponent<Ragdoll>();
                var sword = inst.GetComponentsInChildren<Transform>(true).First(t => t.name == "Sword");
                Transform bb = sword.Find("BladeBase"), bt = sword.Find("BladeTip");
                var map = GKCharacters.MapBones(inst.transform);
                // colliders the blade must never be inside: everything except the sword arm
                var excluded = new HashSet<Transform> { map["RightUpperArm"], map["RightLowerArm"] };
                var bodyCols = rag.colliders.Where(c => !excluded.Contains(c.transform)).ToList();
                foreach (var c in bodyCols) c.enabled = true;

                var clips = anim.runtimeAnimatorController.animationClips.Distinct()
                    .Where(c => string.IsNullOrEmpty(filter) || c.name.ToLowerInvariant().Contains(filter.ToLowerInvariant()))
                    .OrderBy(c => c.name).ToList();
                const int samplesAlongBlade = 14;
                foreach (var clip in clips)
                {
                    total++;
                    int frames = Mathf.Max(2, Mathf.CeilToInt(clip.length * 60f));
                    int penFrames = 0; float worstPen = 0f; string worstCol = ""; float worstT = 0f;
                    int groundFrames = 0;
                    int jitterEvents = 0; float worstJitter = 0f; float worstCorr = 0f;
                    Vector3 prevDir = Vector3.zero, prevDelta = Vector3.zero;
                    bool dropsWeapon = clip.name.Contains("Death") || clip.name.Contains("Exec_Loser");
                    for (int f = 0; f <= frames; f++)
                    {
                        float t = clip.length * f / frames;
                        if (dropsWeapon && t > 0.35f) break;      // Combatant.Die drops the sword at 0.35 s
                        clip.SampleAnimation(inst, t);
                        inst.transform.SetPositionAndRotation(new Vector3(0f, 5000f, 0f), Quaternion.identity);
                        Physics.SyncTransforms();
                        if (solver != null)
                        {
                            // the solver runs every frame in game; feed it consecutive frames at 60 Hz
                            solver.Solve(f == 0 ? 0f : 1f / 60f);
                            if (solver.LastCorrection > worstCorr) worstCorr = solver.LastCorrection;
                        }
                        Vector3 a = bb.position, b = bt.position;
                        float bladeLen = Vector3.Distance(a, b);
                        // penetration: how much of the blade length lies inside a body collider
                        float insideLen = 0f; string inCol = "";
                        for (int i = 0; i <= samplesAlongBlade; i++)
                        {
                            Vector3 p = Vector3.Lerp(a, b, i / (float)samplesAlongBlade);
                            foreach (var c in bodyCols)
                            {
                                Vector3 cp = c.ClosestPoint(p);
                                if ((cp - p).sqrMagnitude < 1e-8f) { insideLen += bladeLen / (samplesAlongBlade + 1); inCol = c.transform.name; break; }
                            }
                        }
                        if (insideLen > 0.06f) penFrames++;
                        if (insideLen > worstPen) { worstPen = insideLen; worstCol = inCol; worstT = t; }
                        // ground (character root is at y = 5000)
                        if (Mathf.Min(a.y, b.y) < 5000f - 0.03f) groundFrames++;
                        // jitter: blade direction reversing between consecutive frames
                        Vector3 dir = (b - a).normalized;
                        if (f > 0)
                        {
                            Vector3 delta = dir - prevDir;
                            float speed = Vector3.Angle(prevDir, dir) * 60f;     // deg/s
                            if (f > 1 && speed > 400f && Vector3.Dot(delta, prevDelta) < -0.5f * delta.magnitude * prevDelta.magnitude)
                            {
                                jitterEvents++;
                                worstJitter = Mathf.Max(worstJitter, speed);
                            }
                            prevDelta = delta;
                        }
                        prevDir = dir;
                    }
                    float penPct = penFrames / (float)(frames + 1);
                    bool lying = clip.name.Contains("Death") || clip.name.Contains("Exec_Loser");
                    if (lying) groundFrames = 0;
                    bool flagged = penFrames >= 3 || jitterEvents > 0 || groundFrames >= 3;
                    if (flagged) bad++;
                    string line = string.Format("{0}{1,-34} len={2,5:F2}s  pen {3,4:P0} max {4:F2}m ({5} @{6:F2}s)  ground {7}f  jitter {8} (max {9:F0}°/s)  wrist fix max {10:F0}°",
                        flagged ? "! " : "  ", clip.name, clip.length, penPct, worstPen, worstCol.Replace("mixamorig:", ""), worstT, groundFrames, jitterEvents, worstJitter, worstCorr);
                    report.AppendLine(line);
                    if (flagged) GKBridge.Log("swordcheck " + line);
                }
            }
            finally { Object.DestroyImmediate(inst); Object.DestroyImmediate(ground); }
            report.AppendLine();
            report.Append(ScabbardPass(filter, ref bad, ref total));
            System.IO.File.WriteAllText(System.IO.Path.Combine(GKBridge.Dir, "swordcheck.txt"), report.ToString());
            GKBridge.Log("swordcheck: " + bad + " of " + total + " clips flagged (full report GKBridge/swordcheck.txt)");
        }

        /// <summary>Runs ScabbardMotion over every clip and reports the worst scabbard-to-leg clearance.</summary>
        static string ScabbardPass(string filter, ref int bad, ref int total)
        {
            var sb = new StringBuilder("scabbard clearance (negative = inside the leg/coat):\n");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKGameBuilder.PlayerPrefabPath);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            inst.transform.SetPositionAndRotation(new Vector3(0f, 5000f, 0f), Quaternion.identity);
            try
            {
                var motion = inst.GetComponent<ScabbardMotion>();
                if (motion == null) return "no ScabbardMotion on the player\n";
                var anim = inst.GetComponent<Animator>();
                var clips = anim.runtimeAnimatorController.animationClips.Distinct()
                    .Where(c => string.IsNullOrEmpty(filter) || c.name.ToLowerInvariant().Contains(filter.ToLowerInvariant()))
                    .OrderBy(c => c.name).ToList();
                motion.Init();                                   // rest hang captured once, in the bind pose
                foreach (var clip in clips)
                {
                    total++;
                    motion.ResetDynamics();
                    int frames = Mathf.Max(2, Mathf.CeilToInt(clip.length * 60f));
                    float worst = float.MaxValue; float worstT = 0f; int inside = 0; float maxSwing = 0f, maxFollow = 0f, maxRate = 0f, maxResidual = 0f;
                    Vector3 lastLocal = Vector3.zero; bool hasLast = false;
                    for (int f = 0; f <= frames; f++)
                    {
                        float t = clip.length * f / frames;
                        clip.SampleAnimation(inst, t);
                        inst.transform.SetPositionAndRotation(new Vector3(0f, 5000f, 0f), Quaternion.identity);
                        motion.Solve(1f / 60f);
                        if (motion.LastSwing > maxSwing) maxSwing = motion.LastSwing;
                        if (motion.LastFollow > maxFollow) maxFollow = motion.LastFollow;
                        if (motion.LastResidual > maxResidual) maxResidual = motion.LastResidual;
                        if (motion.LastClearance < worst) { worst = motion.LastClearance; worstT = t; }
                        if (motion.LastClearance < -0.005f) inside++;
                        // angular speed of the scabbard in hip space: pops / flips read as bugs even without penetration
                        Vector3 local = motion.hips.InverseTransformDirection(motion.scabbard.forward);
                        if (hasLast && f > 1) maxRate = Mathf.Max(maxRate, Vector3.Angle(lastLocal, local) * 60f);
                        lastLocal = local; hasLast = true;
                    }
                    bool flagged = inside >= 2 || maxSwing > 35f || maxRate > 720f;
                    if (flagged) bad++;
                    string line = string.Format("{0}{1,-34} min clearance {2,6:F3} m @{3:F2}s, frames inside {4}, swing {5:F0}° (follow {6:F0}°), max rate {7:F0}°/s, residual {8:F3}",
                        flagged ? "! " : "  ", clip.name, worst, worstT, inside, maxSwing, maxFollow, maxRate, maxResidual);
                    sb.AppendLine(line);
                    if (flagged) GKBridge.Log("scabbard " + line);
                }
            }
            finally { Object.DestroyImmediate(inst); }
            return sb.ToString();
        }
    }
}
