using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// The combat fit every player hero gets, whatever the rig (only the humanoid Avatar and the weapon parts GKWeapons
    /// names: Sword / Grip / BladeBase / BladeTip / ScabbardPivot): the handle seated in the fingers with the thumb
    /// wrapped (HandGrip, FingerAxes), the sword socket, the round shield on the left forearm with its straps
    /// (ShieldGrip), the three fighting styles with the back-shield mount and the second sword (FightingStyles, which
    /// adds GuardAim, TremorFilter and ShieldYield at run time), the clothed torso shells for the blade solver
    /// (BodyShell), the solver, holster and scabbard settings, and the sword-and-shield move set. Made on Knight_Clean
    /// and measured there (chainqa 33/33 per style, tremble probe); the M1 hero Bakur (Knight_Player) gets the same.
    /// </summary>
    public static class GKHeroFit
    {
        [GKCommand("herofit", "herofit [prefab path]: the combat fit (grip, shield, styles, body shells, solver, moves) on a built player prefab; default the M1 hero, Knight_Player")]
        static void Cmd(string arg)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("herofit: stop play mode first"); return; }
            FitPrefab(string.IsNullOrEmpty(arg) ? GKHeroSwap.M1PlayerPath : arg.Trim());
        }

        /// <summary>Fits the prefab at <paramref name="path"/> in place (run it again after the game builder rebuilds
        /// the player). Returns the saved prefab, or null.</summary>
        public static GameObject FitPrefab(string path)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null) { GKBridge.Log("herofit: no prefab " + path); return null; }
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var anim = root.GetComponent<Animator>();
                if (anim == null || anim.avatar == null || !anim.avatar.isHuman) { GKBridge.Log("herofit: " + path + " has no humanoid Animator"); return null; }
                FitHand(root, 0f);
                FitCombat(root);
                var saved = PrefabUtility.SaveAsPrefabAsset(root, path);
                var c = root.GetComponent<Combatant>();
                GKBridge.Log("herofit: " + path + " fitted: shells " + root.GetComponents<BodyShell>().Length + ", shield grip " + (root.GetComponent<ShieldGrip>() != null) +
                             ", styles " + (root.GetComponent<FightingStyles>() != null) + ", moveset " + (c != null && c.moves != null ? c.moves.name : "MISSING (run 'combat')"));
                return saved;
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        /// <summary>The sword seated in the right fist and the fingers that close on it (on the visual, before or after
        /// the character is built).</summary>
        public static void FitHand(GameObject go, float slide)
        {
            var anim = go.GetComponent<Animator>();
            if (anim == null) return;
            // a gloved hand wider than the generic one the attach was tuned on (the CC glove) slides the sword along the
            // blade by `slide`, so the cross-guard rides above the thumb and index finger instead of cutting the thumb
            var swordT = anim.GetBoneTransform(HumanBodyBones.RightHand) != null ? anim.GetBoneTransform(HumanBodyBones.RightHand).Find("Sword") : null;
            if (swordT != null)
            {
                Transform bb = swordT.Find("BladeBase"), bt = swordT.Find("BladeTip");
                if (bb != null && bt != null && slide != 0f) swordT.position += (bt.position - bb.position).normalized * slide;
                // seat the handle in the fingers: the knuckle joints sat 6-7 cm off the handle axis, so the fist closed
                // around air and the sword looked strapped to the back of the hand. A real grip has the handle in the
                // crease at the root of the fingers, its axis about 3.4 cm from the knuckle joints.
                Transform gp = swordT.Find("Grip");
                if (bb != null && bt != null && gp != null)
                {
                    Vector3 axis = (bt.position - bb.position).normalized;
                    Vector3 off = Vector3.zero; int n = 0; float before = 0f;
                    foreach (var hb in new[] { HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightRingProximal, HumanBodyBones.RightLittleProximal })
                    {
                        var k = anim.GetBoneTransform(hb);
                        if (k == null) continue;
                        Vector3 d = Vector3.ProjectOnPlane(k.position - gp.position, axis);
                        off += d; before += d.magnitude; n++;
                    }
                    if (n > 0)
                    {
                        off /= n; before /= n;
                        float move = off.magnitude - GKHeroCC.GripSeat;
                        if (move > 0.002f) swordT.position += off.normalized * move;
                        GKBridge.Log("herofit: grip seat - knuckles " + (before * 100f).ToString("F1") + " cm off the handle axis (mean line " + (off.magnitude * 100f).ToString("F1") + "), sword moved " + (Mathf.Max(0f, move) * 100f).ToString("F1") + " cm into the fingers");
                    }
                }
            }
            // the anatomical bend axis of every finger joint, measured from the avatar (see FingerAxes)
            if (go.GetComponent<FingerAxes>() == null) go.AddComponent<FingerAxes>();
            // the thumb wraps under the cross-guard (see HandGrip)
            var grip = go.GetComponent<HandGrip>() ?? go.AddComponent<HandGrip>();
            grip.thumbProximal = anim.GetBoneTransform(HumanBodyBones.RightThumbProximal);
            grip.thumbIntermediate = anim.GetBoneTransform(HumanBodyBones.RightThumbIntermediate);
            grip.thumbDistal = anim.GetBoneTransform(HumanBodyBones.RightThumbDistal);
            grip.sword = swordT;
            System.Func<HumanBodyBones, HumanBodyBones, HumanBodyBones, Transform[]> chain = (a, b, c) =>
                new[] { anim.GetBoneTransform(a), anim.GetBoneTransform(b), anim.GetBoneTransform(c) };
            grip.index = chain(HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal);
            grip.middle = chain(HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal);
            grip.ring = chain(HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal);
            grip.little = chain(HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal);
        }

        /// <summary>The scabbard keeps off the legs as they look, not as the ragdoll's capsules are (fatter: it leaned 35-45 deg
        /// out). Per leg capsule: the distance from its axis that 90 % of the skinned vertices around it stay within (the
        /// trouser leg at the bind pose), plus 1.2 cm of cloth; and its outward lean held to 10 deg, solved with them.</summary>
        static readonly string[] OuterGarments = { "coat", "qaba", "cloak", "skirt", "kaftan", "robe", "tunic" };

        static void FitScabbardLegs(GameObject root)
        {
            var scab = root.GetComponent<ScabbardMotion>();
            if (scab == null || scab.legs == null || scab.legs.Length == 0) return;
            var pts = new System.Collections.Generic.List<Vector3>();
            var baked = new Mesh();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr.sharedMesh == null || !smr.gameObject.activeInHierarchy) continue;
                // the leg as the trousers / hose show it: an outer garment hanging round the thigh (the coat, a qaba, a
                // cloak) is what the scabbard lies ON, not what it must keep off (it read the thigh 16 cm thick)
                string n = smr.name.ToLowerInvariant();
                if (OuterGarments.Any(g => n.Contains(g))) continue;
                smr.BakeMesh(baked, true);
                foreach (var v in baked.vertices) pts.Add(smr.transform.TransformPoint(v));
            }
            Object.DestroyImmediate(baked);
            var radii = new float[scab.legs.Length];
            var sb = new System.Text.StringBuilder("herofit: scabbard leg radii (visible + 1.2 cm)");
            for (int i = 0; i < scab.legs.Length; i++)
            {
                var c = scab.legs[i];
                if (c == null) continue;
                var tr = c.transform;
                Vector3 axis = c.direction == 0 ? Vector3.right : c.direction == 2 ? Vector3.forward : Vector3.up;
                float s = tr.lossyScale.x, cr = c.radius * s;
                float half = Mathf.Max(0f, c.height * 0.5f * s - cr);
                Vector3 ctr = tr.TransformPoint(c.center), w = tr.TransformDirection(axis);
                Vector3 a = ctr - w * half, b = ctr + w * half;
                var ds = new System.Collections.Generic.List<float>();
                foreach (var p in pts)
                {
                    float u = Vector3.Dot(p - a, b - a) / Mathf.Max(1e-6f, (b - a).sqrMagnitude);
                    if (u < 0.1f || u > 0.9f) continue;
                    float d = Vector3.Distance(p, a + (b - a) * u);
                    if (d < cr * 1.4f) ds.Add(d);
                }
                if (ds.Count < 20) continue;
                ds.Sort();
                radii[i] = ds[(int)(ds.Count * 0.75f)] + 0.012f;   // 3 in 4 of the trouser-leg vertices inside
                sb.Append(" | " + tr.name + " " + (radii[i] * 100f).ToString("F1") + " cm (capsule " + (cr * 100f).ToString("F1") + ")");
            }
            scab.SetLegRadii(radii);
            scab.clearance = 0.004f;
            scab.maxOutward = 0f;   // off: at 10 deg it buzzed against the legs in the guard and jumped when it switched off (256 mm); open
            GKBridge.Log(sb.ToString());
        }

        /// <summary>Everything else of the fit, on a built player (see the class summary).</summary>
        public static void FitCombat(GameObject root)
        {
            foreach (var t in new[] { typeof(PoseInertializer), typeof(PostureCorrector), typeof(AnatomyLimiter), typeof(SwordArmPose) })
            {
                var c = root.GetComponent(t);
                if (c != null) UnityEngine.Object.DestroyImmediate(c);
            }
            // a belt scabbard swings a little and settles fast; at 85 deg of free swing it flew out of the hand's
            // reach during draw / sheathe (the hilt was 46 cm from the palm at the hand-over) and flailed like a rope
            var scab = root.GetComponent<ScabbardMotion>();
            if (scab != null)
            {
                scab.maxSwing = 25f; scab.stiffness = 140f; scab.damping = 18f;
                // the hanger carries it at the side of the hip; the left hand brings the throat 20 cm forward to
                // where the draw / sheathe clips grip the hilt (fitted there, it hung across the front of the thigh)
                scab.drawnBack = 0.20f;
            }
            // the clip's hand is still ~15 cm from the hilt at the measured grab frame on the live knight: a
            // 0.14 s hand-over turned the sword ~85 deg in three frames, a visible snap; 0.3 s reads as the grip
            var holster = root.GetComponent<WeaponHolster>();
            if (holster != null) holster.handOverBlend = 0.3f;
            // falling, the blade turns down along the forearm: at 6/s that was up to 49 deg a frame, a flip;
            // 2.5/s lays it down over 0.4 s
            var bladeSolver = root.GetComponent<WeaponSelfCollision>();
            if (bladeSolver != null)
            {
                bladeSolver.carryBlendRate = 2.5f;
                // the sword-and-shield jumps hold the sword already: laying it low and back turned it in the
                // fist 11 deg a frame on takeoff and again on landing
                bladeSolver.carryInJumps = false;
                // nor on a fall: the carry turned the sword in the fist to lay it along the forearm
                bladeSolver.carryInKnockdown = false;
                // and a blade kept clear of the body or the ground bends the wrist, never turns in the fist
                bladeSolver.correctWithWrist = true;
            }
            GKHeroCC.FitSwordSocket(root);
            GKHeroCC.AttachShield(root);
            GKHeroCC.SetupStyles(root);
            GKHeroCC.BuildBodyShells(root);
            FitScabbardLegs(root);
            // the two-sword style is parked: its second sword (left hand) and right-hip scabbard are stored switched off,
            // so no view of the knight, in the editor or a scene, shows a second set (FightingStyles shows it when enabled)
            var styles = root.GetComponent<FightingStyles>();
            // the shield's arm <-> back move follows the controller's speed for it (a fit re-run updates a built hero)
            var reach = GKCombatBuilder.Clip("AX_standing_disarm_over_shoulder");
            if (styles != null && reach != null) styles.shieldReachLength = reach.length / GKCombatBuilder.ShieldMoveSpeed;
            if (styles != null && !styles.dualSwordsEnabled)
            {
                if (styles.offSword != null) styles.offSword.gameObject.SetActive(false);
                if (styles.offSheath != null) styles.offSheath.gameObject.SetActive(false);
            }
            // the sword-and-shield clips hold the shield arm: no relaxed-arm override (made for a free left hand)
            var shieldBearer = root.GetComponent<Combatant>();
            if (shieldBearer != null) { shieldBearer.offHandWeightFree = 0f; shieldBearer.offHandWeightAction = 0f; }
            var cleanMoves = GKCombatBuilder.CleanPlayerMoves;
            var cf = root.GetComponent<Combatant>();
            if (cf != null && cleanMoves != null) cf.moves = cleanMoves;
        }
    }
}
