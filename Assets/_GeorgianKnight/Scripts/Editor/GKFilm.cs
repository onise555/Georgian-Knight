using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Frame-by-frame film of the player from several angles at once (play mode): game time is locked to a fixed
    /// frame rate while recording, so every animation frame and transition is captured however slow the capture is.
    /// film &lt;name&gt; &lt;seconds&gt; writes GKBridge/film/&lt;name&gt;/&lt;angle&gt;/0000.png ... for the angles
    /// front, side, back (three-quarter from behind) and hand (close on the sword hand); ffmpeg turns them into video.
    /// </summary>
    public static class GKFilm
    {
        [GKCommand("film", "film <name> [seconds=3] [fps=30] [action@sec ...]: record the player frame by frame from front, side, back and the sword hand (play mode); actions on game time: light heavy kick dodge block draw sheathe hitlight hitheavy knockdown")]
        static void Cmd(string arg)
        {
            if (!Application.isPlaying) { GKBridge.Log("film: play mode only"); return; }
            var p = GameObject.FindWithTag("Player");
            if (p == null) { GKBridge.Log("film: no player"); return; }
            var parts = (arg ?? "").Trim().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            string name = parts.Length > 0 ? parts[0] : "film";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            float seconds = parts.Length > 1 ? float.Parse(parts[1], inv) : 3f;
            int fps = parts.Length > 2 && !parts[2].Contains("@") ? int.Parse(parts[2]) : 30;
            var actions = new System.Collections.Generic.List<KeyValuePair<float, string>>();
            foreach (var t in parts)
            {
                int at = t.IndexOf('@');
                if (at > 0) actions.Add(new KeyValuePair<float, string>(float.Parse(t.Substring(at + 1), inv), t.Substring(0, at)));
            }
            actions.Sort((x, y) => x.Key.CompareTo(y.Key));
            bool fast = System.Array.IndexOf(parts, "fast") >= 0;
            var old = p.GetComponent<GKFilmRecorder>();
            if (old != null) Object.DestroyImmediate(old);
            var rec = p.AddComponent<GKFilmRecorder>();
            rec.Begin(Path.Combine(GKBridge.Dir, "film", name), Mathf.CeilToInt(seconds * fps), fps);
            rec.actions = actions;
            rec.fast = fast;
            GKBridge.Log("film: " + name + " " + seconds + " s at " + fps + " fps -> " + rec.Dir);
        }
    }

    [DefaultExecutionOrder(5000)]   // after every pose fix, weapon solver and the camera
    public sealed class GKFilmRecorder : MonoBehaviour
    {
        public string Dir { get; private set; }
        public List<KeyValuePair<float, string>> actions = new List<KeyValuePair<float, string>>();
        public bool fast;                  // no pictures: actions, metrics and the pop probe only
        Combatant foe;                     // the take's own opponent (foe / foeai), kept while other enemies are cleared
        GKPosePopProbe probe;
        int nextAction;
        GKInput input;
        GKInput.Injected held; float releaseAt = -1f;
        static readonly string[] Angles = { "front", "side", "back", "hand", "game", "shield", "impact", "blade" };
        const int W = 640, H = 360;
        int frames, frame, fpsUsed;
        Camera cam;
        RenderTexture rt;
        Texture2D tex;
        Animator anim;
        Transform hand, chest, offHand;
        Vector3 fwd, right;
        // per-frame metrics, so every frame can be checked by number as well as by eye
        System.Text.StringBuilder csv;
        Combatant self;
        WeaponHolster holster;
        WeaponSelfCollision blade;
        Transform sword, grip, scabbard, hips;
        Quaternion lastSwordLocal; bool haveLast;
        Transform lCalf, rCalf; Quaternion lastL, lastR; bool haveCalf;

        public void Begin(string dir, int frameCount, int fps)
        {
            Dir = dir;
            frames = frameCount;
            foreach (var a in Angles) Directory.CreateDirectory(Path.Combine(dir, a));
            foreach (var f in Directory.GetFiles(dir, "*.png", SearchOption.AllDirectories)) File.Delete(f);
            anim = GetComponent<Animator>();
            hand = anim != null ? anim.GetBoneTransform(HumanBodyBones.RightHand) : null;
            chest = anim != null ? anim.GetBoneTransform(HumanBodyBones.Chest) : null;
            offHand = anim != null ? anim.GetBoneTransform(HumanBodyBones.LeftHand) : null;
            // the takes render from LateUpdate, before the frame's own skinning: without this the skin showed the
            // previous frame's pose while the shield and sword (rigid, on the bones) showed this one, so fast moves
            // seemed to pull the sleeve off the shield and the fist off its strap
            foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.forceMatrixRecalculationPerRender = true;
            // a fixed framing for the whole take: the character's facing when the take starts
            fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            right = Vector3.Cross(Vector3.up, fwd);
            var go = new GameObject("~GKFilmCamera");
            go.hideFlags = HideFlags.DontSave;
            cam = go.AddComponent<Camera>();
            if (Camera.main != null) cam.CopyFrom(Camera.main);
            // CopyFrom leaves URP's camera data behind: without it the takes had no bloom, vignette or tonemapping
            var camData = UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(cam);
            var mainData = Camera.main != null ? UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(Camera.main) : null;
            camData.renderPostProcessing = mainData == null || mainData.renderPostProcessing;
            if (mainData != null) { camData.antialiasing = mainData.antialiasing; camData.antialiasingQuality = mainData.antialiasingQuality; }
            cam.enabled = false;
            cam.nearClipPlane = 0.02f;
            rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 4;
            tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            self = GetComponent<Combatant>();
            holster = GetComponent<WeaponHolster>();
            blade = GetComponent<WeaponSelfCollision>();
            hips = anim != null ? anim.GetBoneTransform(HumanBodyBones.Hips) : null;
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Sword") sword = t;
                else if (t.name == "ScabbardPivot") scabbard = t;
            }
            grip = sword != null ? sword.Find("Grip") : null;
            lCalf = anim != null ? anim.GetBoneTransform(HumanBodyBones.LeftLowerLeg) : null;
            rCalf = anim != null ? anim.GetBoneTransform(HumanBodyBones.RightLowerLeg) : null;
            input = GetComponent<GKInput>();
            if (input != null) input.ignoreDevices = true;
            fpsUsed = fps;
            csv = new System.Text.StringBuilder("frame,state,holster,swordParent,solverDeg,swordInHandDeg,gripToPalmCm,scabbardFromHangDeg,speed,moveX,moveY,calfDeg,gripTight,tipGapCm,shieldHandCm,shieldLoopCm,gripDirR,gripDirL,backShieldArmCm,guardW,crossGapCm,crossDeg,crossBelowEyesCm,crossFwdCm,bladeInBodyCm,wristR,wristL,shoulderUpR,shoulderUpL,attack,progress" + System.Environment.NewLine);
            probe = GetComponent<GKPosePopProbe>();
            if (probe == null) probe = gameObject.AddComponent<GKPosePopProbe>();
            probe.ResetStats();
            Time.captureFramerate = fps;
            frame = 0;
        }

        void SpawnFoe(float dist, bool ai)
        {
            if (foe != null) Destroy(foe.gameObject);
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(GKGameBuilder.EnemyPrefabPath);
            if (prefab == null) return;
            Vector3 dir = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            var go = Instantiate(prefab, transform.position + dir * dist, Quaternion.LookRotation(-dir));
            if (!ai) foreach (var b in go.GetComponents<EnemyAI>()) b.enabled = false;
            foe = go.GetComponent<Combatant>();
        }

        void Move(Vector2 v, bool sprint)
        {
            if (input == null) return;
            input.Inject(GKInput.Injected.Sprint, sprint);
            if (!sprint && held == GKInput.Injected.Sprint) held = GKInput.Injected.None;
            input.InjectMove(v, v.sqrMagnitude > 0f);
        }

        /// <summary>Runs the scheduled actions on game time (frame / fps), pressing a button for 0.1 s.</summary>
        void RunActions()
        {
            float t = frame / (float)fpsUsed;
            if (releaseAt >= 0f && t >= releaseAt) { input.Inject(held, false); releaseAt = -1f; }
            while (nextAction < actions.Count && actions[nextAction].Key <= t)
            {
                string a = actions[nextAction].Value; nextAction++;
                var holster = GetComponent<WeaponHolster>();
                var self = GetComponent<Combatant>();
                GKInput.Injected b = GKInput.Injected.None; float hold = 0.1f;
                switch (a)
                {
                    case "light": b = GKInput.Injected.Light; break;
                    case "heavy": b = GKInput.Injected.Heavy; break;
                    case "kick": b = GKInput.Injected.Kick; break;
                    case "dodge": b = GKInput.Injected.Dodge; break;
                    case "block": b = GKInput.Injected.Block; hold = 1.2f; break;
                    case "guard": b = GKInput.Injected.Block; hold = 3f; break;
                    case "walk": Move(new Vector2(0f, 0.45f), false); break;
                    case "run": Move(new Vector2(0f, 1f), false); break;
                    case "sprint": Move(new Vector2(0f, 1f), true); break;
                    case "back": Move(new Vector2(0f, -1f), false); break;
                    case "left": Move(new Vector2(-1f, 0f), false); break;
                    case "stop": Move(Vector2.zero, false); break;
                    case "jump": b = GKInput.Injected.Jump; break;
                    case "foe": SpawnFoe(1.6f, false); break;
                    case "foeai": SpawnFoe(3.5f, true); break;
                    case "execprep":
                        // weakened below the execution threshold and staggered: the next light attack executes
                        if (foe != null)
                        {
                            var st = foe.GetComponent<CombatStats>();
                            if (st != null) { st.maxHealth = 100f; st.ResetAll(); }
                            foe.ReceiveHit(new HitData { damage = 70f, poiseDamage = 0f, unblockable = true, reaction = HitReaction.Light, point = foe.transform.position + Vector3.up * 1.2f, direction = foe.transform.forward * -1f });
                            foe.ApplyStagger(4f);
                        }
                        break;
                    case "draw": if (holster != null) holster.Draw(); break;
                    case "style1": case "style2": case "style3":
                        { var fs = GetComponent<FightingStyles>(); if (fs != null) fs.Select((WeaponStyle)(a[5] - '1')); break; }
                    case "sheathe": if (holster != null) holster.Sheathe(); break;
                    case "hitlight": GKPoseFixQa.HitPlayer(self, HitReaction.Light); break;
                    case "hitheavy": GKPoseFixQa.HitPlayer(self, HitReaction.Heavy); break;
                    case "knockdown": GKPoseFixQa.HitPlayer(self, HitReaction.Knockdown); break;
                }
                if (b != GKInput.Injected.None && input != null)
                {
                    if (releaseAt >= 0f) input.Inject(held, false);
                    input.Inject(b, true); held = b; releaseAt = t + hold;
                }
                GKBridge.Log("film: f" + frame + " " + a + (self != null ? " (state " + self.CurrentState + ")" : ""));
            }
        }

        void LateUpdate()
        {
            if (cam == null) return;
            // a take is the player alone: practice waves keep spawning enemies whose blows would mix into the test
            foreach (var w in FindObjectsByType<WaveManager>(FindObjectsSortMode.None)) { w.StopAllCoroutines(); w.enabled = false; }
            foreach (var c in FindObjectsByType<Combatant>(FindObjectsSortMode.None))
                if (c.team != Team.Player && c != foe) Destroy(c.gameObject);
            // ...and the swords the fallen dropped (loose props on the floor read as the knight's own gear)
            foreach (var w in FindObjectsByType<WeaponHitbox>(FindObjectsSortMode.None))
                if (w.GetComponentInParent<Combatant>() == null) Destroy(w.gameObject);
            RunActions();
            if (fast) { WriteMetrics(); frame++; if (frame >= frames) Finish(); return; }
            Vector3 root = transform.position;
            Vector3 body = chest != null ? Vector3.Lerp(root + Vector3.up * 1.0f, chest.position, 0.5f) : root + Vector3.up * 1.1f;
            for (int i = 0; i < Angles.Length; i++)
            {
                Vector3 eye, look; float fov;
                switch (Angles[i])
                {
                    case "front": eye = root + fwd * 3.4f + right * 0.8f + Vector3.up * 1.35f; look = body; fov = 38f; break;
                    case "side": eye = root + right * 3.6f + Vector3.up * 1.2f; look = body; fov = 38f; break;
                    case "back": eye = root - fwd * 3.0f - right * 1.6f + Vector3.up * 1.7f; look = body; fov = 38f; break;
                    case "game":
                        // what the player sees: the game camera's own pose and lens
                        var main = Camera.main;
                        eye = main != null ? main.transform.position : root - fwd * 4f + Vector3.up * 2f;
                        look = main != null ? eye + main.transform.forward : body;
                        fov = main != null ? main.fieldOfView : 50f; break;
                    case "blade":
                        // the outer blade, close, from the knight's right: blood on the steel
                        {
                            var ws = GetComponent<WeaponSelfCollision>();
                            if (ws != null && ws.bladeBase != null && ws.bladeTip != null)
                            {
                                look = Vector3.Lerp(ws.bladeBase.position, ws.bladeTip.position, 0.65f);
                                Vector3 bl = (ws.bladeTip.position - ws.bladeBase.position).normalized;
                                Vector3 sideB = Vector3.Cross(bl, Vector3.up).normalized;
                                if (sideB.sqrMagnitude < 0.1f) sideB = right;
                                eye = look + sideB * 0.45f + Vector3.up * 0.12f;
                            }
                            else { look = body; eye = body + right; }
                            fov = 34f; break;
                        }
                    case "impact":
                        // a macro view of the take's opponent: his chest and shoulders from the knight's right, ~1.1 m off,
                        // where the cuts land (blood, wounds, sparks); the knight himself when there is no opponent
                        {
                            Transform ft = foe != null ? foe.transform : transform;
                            var fa = ft.GetComponent<Animator>();
                            Transform fc = fa != null && fa.isHuman ? fa.GetBoneTransform(HumanBodyBones.Chest) : null;
                            look = fc != null ? fc.position : ft.position + Vector3.up * 1.3f;
                            Vector3 toMe = GK.Flat(transform.position - ft.position).normalized;
                            Vector3 side = Vector3.Cross(Vector3.up, toMe);
                            eye = look + (toMe * 0.75f + side * 0.75f + Vector3.up * 0.15f);
                            fov = 32f; break;
                        }
                    case "shield":
                        // close on the shield hand from the knight's own side: the grip, the forearm on the straps, the back of the plate
                        look = offHand != null ? offHand.position : body;
                        eye = look + (right * 0.6f + Vector3.up * 0.35f - fwd * 0.2f);
                        fov = 40f; break;
                    default:
                        // close on the sword hand from the thumb side, slightly in front of the body
                        look = hand != null ? hand.position : body;
                        eye = look + (fwd * 0.55f + right * 0.45f + Vector3.up * 0.2f);
                        fov = 30f; break;
                }
                cam.transform.position = eye;
                cam.transform.rotation = Quaternion.LookRotation(look - eye, Vector3.up);
                cam.fieldOfView = fov;
                cam.targetTexture = rt;
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply(false);
                RenderTexture.active = prev;
                File.WriteAllBytes(Path.Combine(Dir, Angles[i], frame.ToString("0000") + ".png"), tex.EncodeToPNG());
            }
            cam.targetTexture = null;
            WriteMetrics();
            frame++;
            if (frame >= frames) Finish();
        }

        /// <summary>With the shield slung on the back: the smallest gap (cm) between it and the hands, wrists and elbows,
        /// negative when one is inside the plate. 99 while the shield is on the arm.</summary>
        float BackShieldGap()
        {
            var fs = GetComponent<FightingStyles>();
            if (fs == null || fs.shield == null || fs.ShieldOnArm || anim == null) return 99f;
            var box = fs.shield.GetComponent<BoxCollider>();
            if (box == null) return 99f;
            float best = 99f;
            var t = box.transform;
            Vector3 half = Vector3.Scale(box.size * 0.5f, t.lossyScale);
            foreach (var b in new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm,
                                      HumanBodyBones.LeftMiddleProximal, HumanBodyBones.RightMiddleProximal })
            {
                var bone = anim.GetBoneTransform(b);
                if (bone == null) continue;
                Vector3 l = Vector3.Scale(t.InverseTransformPoint(bone.position) - box.center, t.lossyScale);
                Vector3 d = new Vector3(Mathf.Abs(l.x) - half.x, Mathf.Abs(l.y) - half.y, Mathf.Abs(l.z) - half.z);
                // a square plate is the round shield: its corners are air, the rim a circle
                int thin = half.x <= half.y && half.x <= half.z ? 0 : half.y <= half.z ? 1 : 2;
                int j1 = (thin + 1) % 3, j2 = (thin + 2) % 3;
                if (Mathf.Abs(half[j1] - half[j2]) < 0.05f * Mathf.Min(half[j1], half[j2]))
                {
                    float radial = new Vector2(l[j1], l[j2]).magnitude - Mathf.Min(half[j1], half[j2]);
                    d = Vector3.zero; d[thin] = Mathf.Abs(l[thin]) - half[thin]; d[j1] = radial; d[j2] = -1f;
                }
                float outside = Vector3.Max(d, Vector3.zero).magnitude, inside = Mathf.Min(0f, Mathf.Max(d.x, Mathf.Max(d.y, d.z)));
                // the bone runs through the middle of the limb: its skin is ~4 cm out
                best = Mathf.Min(best, (outside + inside - 0.04f) * 100f);
            }
            return best;
        }

        /// <summary>The two-sword guard: its weight; the closest gap between the two blades (cm, 0 = one through the
        /// other); the angle between them; where they cross, below the eyes and in front of the chest (cm); and the
        /// deepest either blade sits in the body (cm, from both blade solvers).</summary>
        readonly System.Text.StringBuilder penLog = new System.Text.StringBuilder();

        void Guard(System.Text.StringBuilder sb, System.Globalization.CultureInfo ci)
        {
            var g = GetComponent<DualGuardIK>();
            var c = self;
            float w = g != null ? g.Weight : 0f;
            sb.Append(w.ToString("F2", ci)).Append(',');
            if (c == null || c.weapon == null || c.offWeapon == null || anim == null || c.weapon.bladeBase == null || c.offWeapon.bladeBase == null)
            { sb.Append("-,-,-,-,-"); return; }
            Vector3 a0 = c.weapon.bladeBase.position, a1 = c.weapon.bladeTip.position;
            Vector3 b0 = c.offWeapon.bladeBase.position, b1 = c.offWeapon.bladeTip.position;
            Vector3 pa, pb;
            ScabbardMotion.ClosestPointsSegments(a0, a1, b0, b1, out pa, out pb);
            float gap = Vector3.Distance(pa, pb) * 100f;
            float ang = Vector3.Angle(a1 - a0, b1 - b0);
            Vector3 x = (pa + pb) * 0.5f;
            var head = anim.GetBoneTransform(HumanBodyBones.Head);
            var ch = anim.GetBoneTransform(HumanBodyBones.Chest);
            Vector3 f = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            float below = head != null ? (head.position.y + 0.08f - x.y) * 100f : 0f;
            float fwdCm = ch != null ? Vector3.Dot(x - ch.position, f) * 100f : 0f;
            float pen = 0f; string into = "";
            foreach (var s2 in GetComponents<WeaponSelfCollision>())
            {
                if (s2.weapon == null || !s2.weapon.gameObject.activeInHierarchy) continue;   // a parked, hidden sword
                // the solver's own result when it runs; a plain measurement while it is off (drawing, sheathing) - the
                // sheathed blade, inside its scabbard at the hip, is not counted
                float p2; string inN;
                if (s2.enabled) { p2 = s2.LastPenetration; inN = s2.LastPenetrationInto; }
                else if (holster != null && holster.State == WeaponHolster.HolsterState.Sheathed) { p2 = 0f; inN = ""; }
                else { p2 = s2.MeasureNow(out inN); inN = "(off) " + inN; }
                if (p2 > pen) { pen = p2; into = inN; }
            }
            if (pen > 0.02f)
            {
                penLog.Append(frame + ":" + into + " " + (pen * 100f).ToString("F0") + "cm");
                if (into == "Shield") penLog.Append(PlateCross());
                penLog.Append("; ");
            }
            sb.Append(gap.ToString("F1", ci)).Append(',').Append(ang.ToString("F0", ci)).Append(',')
              .Append(below.ToString("F0", ci)).Append(',').Append(fwdCm.ToString("F0", ci)).Append(',').Append((pen * 100f).ToString("F1", ci));
        }

        // where the sword's line meets the shield plate, as a fraction of its radius, the blade's tip speed and how far
        // the shield arm had yielded
        string PlateCross()
        {
            var ws = GetComponent<WeaponSelfCollision>();
            var box = ws != null && ws.avoid != null ? System.Array.Find(ws.avoid, c => c is BoxCollider && c.name == "Shield") as BoxCollider : null;
            if (box == null) return "";
            var t = box.transform;
            Vector3 la = t.InverseTransformPoint(ws.bladeBase.position) - box.center, lb = t.InverseTransformPoint(ws.bladeTip.position) - box.center;
            Vector3 sz = box.size;
            int thin = sz.x <= sz.y && sz.x <= sz.z ? 0 : sz.y <= sz.z ? 1 : 2;
            float r = Mathf.Max(sz[(thin + 1) % 3], sz[(thin + 2) % 3]) * 0.5f;
            string cross = "no crossing";
            if (Mathf.Sign(la[thin]) != Mathf.Sign(lb[thin]))
            {
                Vector3 lx = Vector3.Lerp(la, lb, la[thin] / (la[thin] - lb[thin]));
                lx[thin] = 0f;
                cross = "at " + (lx.magnitude / r).ToString("F2") + "R";
            }
            float tipV = lastTip != Vector3.zero ? (ws.bladeTip.position - lastTip).magnitude / Mathf.Max(1e-4f, Time.deltaTime) : 0f;
            lastTip = ws.bladeTip.position;
            var y = GetComponent<ShieldYield>();
            return " (" + cross + ", tip " + tipV.ToString("F1") + " m/s, yield " + (y != null ? y.Weight.ToString("F2") : "-") + ")";
        }
        Vector3 lastTip;

        float Wrist(bool left)
        {
            if (anim == null) return 0f;
            var la = anim.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
            var h = anim.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            var m = anim.GetBoneTransform(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            if (la == null || h == null || m == null) return 0f;
            return Vector3.Angle(h.position - la.position, m.position - h.position);
        }

        float Shoulder(bool left)
        {
            if (anim == null) return 0f;
            var ua = anim.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
            var la = anim.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
            if (ua == null || la == null) return 0f;
            Vector3 d = (la.position - ua.position).normalized;
            return Mathf.Asin(Mathf.Clamp(Vector3.Dot(d, transform.up), -1f, 1f)) * Mathf.Rad2Deg;
        }

        float GripDir(bool left)
        {
            if (anim == null) return 0f;
            Transform idx = anim.GetBoneTransform(left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
            Transform lit = anim.GetBoneTransform(left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal);
            Transform hb = anim.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (idx == null || lit == null || hb == null) return 0f;
            WeaponHitbox blade = null;
            foreach (var w in GetComponentsInChildren<WeaponHitbox>())
                if (w.transform.IsChildOf(hb)) { blade = w; break; }
            if (blade == null || blade.bladeBase == null || blade.bladeTip == null) return 0f;
            return Vector3.Dot((blade.bladeTip.position - blade.bladeBase.position).normalized, (idx.position - lit.position).normalized);
        }

        // ---- tremble: a part shaking back and forth from frame to frame (a solver or a spring fighting itself), which
        // real motion never does. Per part, in the character's own space so walking does not count: the residual of each
        // frame off the line between its neighbours, and a zigzag where two residuals in a row point opposite ways
        // and both exceed 3 mm (or 1.5 deg for a direction)
        readonly Dictionary<string, List<Vector3>> trem = new Dictionary<string, List<Vector3>>();
        readonly Dictionary<string, int> tremZig = new Dictionary<string, int>();
        readonly Dictionary<string, float> tremMax = new Dictionary<string, float>();
        readonly Dictionary<string, string> tremAt = new Dictionary<string, string>();
        readonly Dictionary<string, Vector3> tremLastR = new Dictionary<string, Vector3>();
        readonly System.Text.StringBuilder tremList = new System.Text.StringBuilder();

        void TrackTremble(string part, Vector3 local, float thresh)
        {
            List<Vector3> l;
            if (!trem.TryGetValue(part, out l)) { l = new List<Vector3>(); trem[part] = l; tremZig[part] = 0; tremMax[part] = 0f; tremAt[part] = ""; tremLastR[part] = Vector3.zero; }
            l.Add(local);
            int n = l.Count;
            if (n < 3) return;
            Vector3 r = l[n - 2] - (l[n - 3] + l[n - 1]) * 0.5f;
            Vector3 pr = tremLastR[part];
            if (Vector3.Dot(r, pr) < 0f && r.magnitude > thresh && pr.magnitude > thresh)
            {
                tremZig[part]++;
                float m = Mathf.Min(r.magnitude, pr.magnitude);
                if (m > thresh * 3f && tremList.Length < 20000)
                    tremList.Append(part).Append(" f").Append(frame - 1).Append(' ').Append((m * 1000f).ToString("F0")).Append("mm ")
                            .Append(self != null ? self.CurrentState.ToString() : "").Append('/').Append(holster != null ? holster.State.ToString() : "")
                            .Append(self != null && self.CurrentAttack != null ? " " + self.CurrentAttack.name + " " + self.ActionProgress.ToString("F2") : "").AppendLine();
                if (m > tremMax[part]) { tremMax[part] = m; tremAt[part] = "f" + (frame - 1) + " " + (self != null ? self.CurrentState.ToString() : "") + (holster != null ? "/" + holster.State : ""); }
            }
            tremLastR[part] = r;
        }

        void Tremble()
        {
            if (anim == null) return;
            Transform root = transform;
            System.Func<Transform, Vector3> L = t => root.InverseTransformPoint(t.position);
            var rh = anim.GetBoneTransform(HumanBodyBones.RightHand); var lh = anim.GetBoneTransform(HumanBodyBones.LeftHand);
            if (rh != null) TrackTremble("handR", L(rh), 0.003f);
            if (lh != null) TrackTremble("handL", L(lh), 0.003f);
            var head = anim.GetBoneTransform(HumanBodyBones.Head);
            if (head != null) TrackTremble("head", L(head), 0.003f);
            if (blade != null && blade.bladeTip != null) TrackTremble("swordTip", L(blade.bladeTip), 0.004f);
            if (scabbard != null) TrackTremble("scabbardChape", root.InverseTransformPoint(scabbard.TransformPoint(Vector3.forward * 0.9f)), 0.003f);
            var fs = GetComponent<FightingStyles>();
            if (fs != null && fs.shield != null) TrackTremble("shield", L(fs.shield), 0.003f);
            var sy = GetComponent<ShieldYield>(); if (sy != null) TrackTremble("yieldWeight", new Vector3(sy.Weight, 0f, 0f), 0.01f);
            var ga = GetComponent<GuardAim>(); if (ga != null) TrackTremble("guardWeight", new Vector3(ga.Weight, 0f, 0f), 0.01f);
            // the raw animated pose's left hand (before any procedural layer) is not reachable here; the shoulder is
            var lu = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm); if (lu != null) TrackTremble("lUpperArmRot", lu.localRotation * Vector3.forward, 0.01f);
        }

        string TrembleSummary()
        {
            var sb = new System.Text.StringBuilder("tremble (zigzag frames, worst residual):");
            foreach (var k in trem.Keys)
                sb.Append(" | ").Append(k).Append(' ').Append(tremZig[k]).Append(" ").Append((tremMax[k] * 1000f).ToString("F0")).Append("mm ").Append(tremAt[k]);
            return sb.ToString();
        }

        void WriteMetrics()
        {
            Tremble();
            string parent = sword != null && sword.parent != null ? sword.parent.name : "-";
            // how much the sword turned in the hand since the last frame (a steady grip keeps this near zero)
            float inHand = 0f;
            if (sword != null && hand != null && sword.IsChildOf(hand))
            {
                if (haveLast) inHand = Quaternion.Angle(lastSwordLocal, sword.localRotation);
                lastSwordLocal = sword.localRotation; haveLast = true;
            }
            else haveLast = false;
            float palm = -1f;
            if (grip != null && hand != null && anim != null)
            {
                var mid = anim.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
                Vector3 p = mid != null ? Vector3.Lerp(hand.position, mid.position, 0.5f) : hand.position;
                palm = Vector3.Distance(p, grip.position) * 100f;
            }
            float hang = -1f;
            if (scabbard != null && hips != null) hang = Vector3.Angle(scabbard.forward, Vector3.down);
            csv.Append(frame).Append(',').Append(self != null ? self.CurrentState.ToString() : "-").Append(',')
               .Append(holster != null ? holster.State.ToString() : "-").Append(',').Append(parent).Append(',')
               .Append((blade != null ? blade.LastCorrection : 0f).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
               .Append(inHand.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
               .Append(palm.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
               .Append(hang.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            float calf = 0f;
            if (lCalf != null && rCalf != null)
            {
                if (haveCalf) calf = Mathf.Max(Quaternion.Angle(lastL, lCalf.localRotation), Quaternion.Angle(lastR, rCalf.localRotation));
                lastL = lCalf.localRotation; lastR = rCalf.localRotation; haveCalf = true;
            }
            csv.Append(anim != null ? anim.GetFloat(GK.P_Speed).ToString("F2", ci) : "0").Append(',')
               .Append(anim != null ? anim.GetFloat(GK.P_MoveX).ToString("F2", ci) : "0").Append(',')
               .Append(anim != null ? anim.GetFloat(GK.P_MoveY).ToString("F2", ci) : "0").Append(',')
               .Append(calf.ToString("F1", ci)).Append(',');
            var hg = GetComponent<HandGrip>();
            csv.Append(hg != null ? hg.Tightness.ToString("F2", ci) : "-").Append(',')
               .Append(hg != null ? (hg.LastTipGap * 100f).ToString("F2", ci) : "-").Append(',');
            var sg = GetComponent<ShieldGrip>();
            csv.Append(sg != null ? (sg.HandOff * 100f).ToString("F1", ci) : "-").Append(',')
               .Append(sg != null ? (sg.LoopOff * 100f).ToString("F1", ci) : "-").Append(',');
            // which side of the fist the blade leaves: +1 the index side (an ordinary grip), -1 the little finger's
            // (held like a dagger)
            csv.Append(GripDir(false).ToString("F2", ci)).Append(',').Append(GripDir(true).ToString("F2", ci)).Append(',')
               .Append(BackShieldGap().ToString("F1", ci)).Append(',');
            Guard(csv, ci);
            // wrists: how far each hand bends off its forearm (deg); shoulders: how far each upper arm rises above
            // the horizontal (deg, the shrug that reads as a broken shoulder past ~70)
            csv.Append(',').Append(Wrist(false).ToString("F0", ci)).Append(',').Append(Wrist(true).ToString("F0", ci))
               .Append(',').Append(Shoulder(false).ToString("F0", ci)).Append(',').Append(Shoulder(true).ToString("F0", ci));
            // the attack and how far into it (the chain's rhythm: press to contact)
            var cur = self != null ? self.CurrentAttack : null;
            csv.Append(',').Append(cur != null ? cur.name : "-").Append(',').Append(cur != null ? self.ActionProgress.ToString("F3", ci) : "-");
            csv.AppendLine();
        }

        void Finish()
        {
            if (input != null) input.ignoreDevices = false;
            if (csv != null) File.WriteAllText(Path.Combine(Dir, "metrics.csv"), csv.ToString());
            if (probe != null) File.WriteAllText(Path.Combine(Dir, "popqa.txt"), probe.Summary());
            File.WriteAllText(Path.Combine(Dir, "blade_inside.txt"), penLog.ToString());
            File.WriteAllText(Path.Combine(Dir, "tremble.txt"), TrembleSummary());
            File.WriteAllText(Path.Combine(Dir, "tremble_frames.txt"), tremList.ToString());
            Time.captureFramerate = 0;
            GKBridge.Log("film: done, " + frame + " frames x " + Angles.Length + " angles in " + Dir);
            if (cam != null) Destroy(cam.gameObject);
            if (rt != null) { rt.Release(); Destroy(rt); }
            if (tex != null) Destroy(tex);
            cam = null;
            Destroy(this);
        }

        void OnDestroy() { if (cam != null) Finish(); }
    }
}
