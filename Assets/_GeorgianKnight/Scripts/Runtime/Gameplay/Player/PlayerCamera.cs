using System;
using System.Collections.Generic;
using GeorgianKnight.Core;
using Unity.Cinemachine;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Close over-the-shoulder gameplay camera (Cinemachine 3 ThirdPersonFollow). This component owns the view yaw /
    /// pitch on the <see cref="pivot"/> (the follow target) and blends a framing per state: explore, sprint, combat,
    /// group combat, lock-on, tight lanes / interiors and executions. ThirdPersonFollow does the arm, shoulder side and
    /// obstacle avoidance (pull in fast, ease out slow). The shoulder swaps automatically when a wall crowds it, and
    /// the player's own meshes hide when the camera is pushed into them, so the view never shows the inside of the head.
    /// Numbers: Docs/HANDBOOK/03_CAMERA_CINEMATICS.md §2.1 (close framing, owner decision 2026-10-20).
    /// </summary>
    [DefaultExecutionOrder(-150)]
    public class PlayerCamera : MonoBehaviour
    {
        [Serializable]
        public struct Framing
        {
            [Tooltip("Arm length behind the shoulder (m).")] public float distance;
            [Tooltip("Shoulder offset to the side (m).")] public float shoulderX;
            [Tooltip("Shoulder offset up from the anchor (m).")] public float shoulderY;
            [Tooltip("Vertical field of view (deg). Explore uses the player's FOV setting; other states keep their difference to explore.")] public float fov;
            public Framing(float d, float x, float y, float f) { distance = d; shoulderX = x; shoulderY = y; fov = f; }

            public static Framing Lerp(Framing a, Framing b, float t)
            {
                return new Framing(Mathf.Lerp(a.distance, b.distance, t), Mathf.Lerp(a.shoulderX, b.shoulderX, t),
                                   Mathf.Lerp(a.shoulderY, b.shoulderY, t), Mathf.Lerp(a.fov, b.fov, t));
            }
        }

        public enum Mode { Explore, Sprint, Combat, CombatGroup, LockOn, Tight, Executing }

        public CinemachineCamera vcam;
        public Transform player;
        [Tooltip("Follow target of the vcam: position = shoulder anchor, rotation = view yaw/pitch.")]
        public Transform pivot;
        LadderClimber climber;
        [Tooltip("Optional look target kept at the anchor (cutscene blends, legacy rigs).")]
        public Transform lookPoint;

        [Header("Anchor")]
        public float pivotHeight = 1.58f;
        [Tooltip("Horizontal follow sharpness (1/s).")] public float pivotSharpness = 22f;
        [Tooltip("Vertical follow sharpness (1/s): lower absorbs steps and stairs.")] public float pivotSharpnessY = 9f;

        [Header("Look")]
        [Tooltip("Pitch limits (deg): negative looks up, positive looks down.")]
        public Vector2 pitchRange = new Vector2(-40f, 65f);
        public float defaultPitch = 8f;
        [Tooltip("Seconds without look input (while moving) before the pitch eases back to default; 0 = never.")]
        public float pitchRecenterDelay = 2.5f;
        public float pitchRecenterSpeed = 10f;
        public float lockYawSharpness = 7f;
        [Tooltip("Seconds a lock-on target switch takes to bring the view round (QUALITY: at most 0.25 s).")]
        public float switchTime = 0.25f;
        Combatant lastLocked; float switchFrom, switchT = 1f;
        public float lockPitch = 10f;
        [Tooltip("Orbit the view a few degrees so the hero's body never hides a foe close by (QUALITY camera: every threat readable).")]
        public bool threatClearance = true;
        [Tooltip("Most the view orbits off the player's / lock-on yaw to clear a hidden foe (deg), and foes farther than clearRange (m) are ignored.")]
        public float maxClearYaw = 14f, clearRange = 9f;
        [Tooltip("How fast the clearance orbit comes in and goes out (1/s).")]
        public float clearSharpness = 4f;
        [Tooltip("In a group fight (three or more foes) the camera rises by groupLift and pulls back by groupPullBack (m) on "
                 + "top of the preset: height clears the hero's silhouette with less orbit, and orbit costs orientation (Team Lead 2026-10-31).")]
        public float groupLift = 0.3f, groupPullBack = 0.4f;
        [Tooltip("Most the camera rises on top of its framing to show a foe's chest over the hero's head (m). Height is "
                 + "preferred to orbit: it keeps the view's direction. Every foe keeps clearSpare deg to the hero's silhouette.")]
        public float maxClearLift = 0.6f, clearSpare = 2f;
        float clearYaw, clearWant, clearLift, clearLiftWant;

        [Header("Framing")]
        public Framing explore = new Framing(2.5f, 0.48f, 0.12f, 50f);
        public Framing sprint = new Framing(2.8f, 0.42f, 0.10f, 55f);
        public Framing combat = new Framing(3.2f, 0.45f, 0.16f, 52f);
        [Tooltip("Three or more engaged enemies.")]
        public Framing combatGroup = new Framing(3.9f, 0.40f, 0.26f, 54f);
        public Framing lockedOn = new Framing(3.3f, 0.55f, 0.18f, 52f);
        [Tooltip("Lanes narrower than tightWidth or low ceilings.")]
        public Framing tight = new Framing(2.0f, 0.34f, 0.10f, 54f);
        public Framing executing = new Framing(2.3f, 0.62f, 0.04f, 44f);
        [Tooltip("Finisher view: degrees off the executioner's facing (the camera looks across both fighters), its pitch, and how fast the camera swings there.")]
        public float execSideAngle = 100f, execPitch = 14f, execYawSharpness = 6f;
        bool execFraming;
        float execYaw;
        [Tooltip("Framing blend sharpness (1/s); ~0.8 s to settle.")]
        public float framingSharpness = 3.5f;
        [Tooltip("Explore vertical FOV; set from the player's settings. Other states keep their offset to explore.")]
        public float baseFov = 50f;
        public float chargeFovDrop = 5f;

        [Header("Space awareness")]
        public LayerMask obstacles = (1 << 0) | (1 << 10);
        [Tooltip("Free width (left + right of the anchor) below which the tight framing is used (m).")]
        public float tightWidth = 3.4f;
        [Tooltip("Ceiling height above the anchor below which the tight framing is used (m).")]
        public float tightCeiling = 1.7f;
        public float cameraRadius = 0.22f;
        [Tooltip("Hide the player's meshes when the camera is closer than this to the head (m).")]
        public float hideDistance = 0.42f;

        public Mode CurrentMode { get; private set; }
        public float Yaw { get { return yaw; } }
        public float Pitch { get { return pitch; } }
        /// <summary>The threat-clearance orbit on top of <see cref="Yaw"/> (deg).</summary>
        public float ClearYaw { get { return clearYaw; } }
        /// <summary>The threat-clearance rise on top of the framing (m).</summary>
        public float ClearLift { get { return clearLift; } }

        GKInput input;
        LockOnSystem lockOn;
        PlayerController controller;
        Combatant self;
        CinemachineThirdPersonFollow follow;
        Transform outputCamera, head;
        Renderer[] playerRenderers;
        UnityEngine.Rendering.ShadowCastingMode[] shadowModes;
        bool hidden;
        float yaw, pitch, lastLookTime, side = 1f, sideTarget = 1f, lastSideSwap = -10f, tightTimer;
        bool tightSpace;
        Framing current;
        Subscriptions subs;
        float punchFov, punchScale = 1f, punchStart = -10f, punchLen = 0.5f;

        /// <summary>The gameplay camera (the one following the player).</summary>
        public static PlayerCamera Main { get; private set; }

        /// <summary>A short cinematic push-in: field of view drops by <paramref name="fovDrop"/> degrees and the arm
        /// shortens to <paramref name="distanceScale"/>, in fast (15% of the time) and out softly. Real time.</summary>
        public void Punch(float fovDrop, float distanceScale, float seconds)
        {
            // a stronger punch replaces a weaker one still playing; a weaker one never cuts a stronger short
            if (PunchWeight() > 0.3f && fovDrop < punchFov) return;
            punchFov = fovDrop; punchScale = distanceScale; punchStart = Time.unscaledTime; punchLen = Mathf.Max(0.08f, seconds);
        }

        float PunchWeight()
        {
            float t = (Time.unscaledTime - punchStart) / punchLen;
            if (t <= 0f || t >= 1f) return 0f;
            return t < 0.15f ? Mathf.SmoothStep(0f, 1f, t / 0.15f) : 1f - Mathf.SmoothStep(0f, 1f, (t - 0.15f) / 0.85f);
        }

        void OnEnable() { Main = this; }
        void OnDisable() { if (Main == this) Main = null; }

        void OnSelfHit(HitData hit, HitResult r)
        {
            // the guard is the moment to SEE: a parry pushes in and slows the world for a breath, a plain block nudges
            if (r == HitResult.Parried)
            {
                Punch(7f, 0.84f, 0.7f);
                CombatFeel.I.Flourish(0.4f, 0.2f, 4f);
            }
            else if (r == HitResult.Blocked) Punch(2f, 0.95f, 0.3f);
            else if (r == HitResult.GuardBroken) Punch(4f, 0.9f, 0.5f);
        }

        void Start()
        {
            if (player != null)
            {
                input = player.GetComponent<GKInput>();
                lockOn = player.GetComponent<LockOnSystem>();
                controller = player.GetComponent<PlayerController>();
                self = player.GetComponent<Combatant>();
                if (self != null) self.HitReceived += OnSelfHit;
                var anim = player.GetComponent<Animator>();
                head = anim != null && anim.isHuman ? anim.GetBoneTransform(HumanBodyBones.Head) : null;
                CacheRenderers();
            }
            if (vcam != null) follow = vcam.GetComponent<CinemachineThirdPersonFollow>();
            var main = Camera.main;
            outputCamera = main != null ? main.transform : null;
            ApplyPreset(Preset);
            current = explore;
            current.fov = baseFov;
            Snap();
            ApplyRig(0f);
            subs = new Subscriptions().On<PlayerTeleported>(e => Snap());
        }

        void OnDestroy() { if (subs != null) subs.Dispose(); if (self != null) self.HitReceived -= OnSelfHit; SetHidden(false); }

        void CacheRenderers()
        {
            var list = new List<Renderer>();
            foreach (var r in player.GetComponentsInChildren<Renderer>(true))
                if (r is SkinnedMeshRenderer || r is MeshRenderer) list.Add(r);
            playerRenderers = list.ToArray();
            shadowModes = new UnityEngine.Rendering.ShadowCastingMode[playerRenderers.Length];
            for (int i = 0; i < playerRenderers.Length; i++) shadowModes[i] = playerRenderers[i].shadowCastingMode;
        }

        /// <summary>Sets the view direction (tools, cutscene exits that hand over a specific angle).</summary>
        public void SetView(float newYaw, float newPitch)
        {
            yaw = newYaw;
            pitch = Mathf.Clamp(newPitch, pitchRange.x, pitchRange.y);
            lastLookTime = Time.unscaledTime;
        }

        /// <summary>Current blended framing (QA / debug).</summary>
        public Framing CurrentFraming { get { return current; } }

        /// <summary>Puts the camera straight behind the player with no blend (spawn, teleport, cutscene exit).</summary>
        public void Snap()
        {
            if (player == null || pivot == null) return;
            yaw = player.eulerAngles.y;
            pitch = defaultPitch;
            clearYaw = clearWant = clearLift = clearLiftWant = 0f;
            pivot.position = player.position + Vector3.up * pivotHeight;
            pivot.rotation = Quaternion.Euler(pitch, yaw, 0f);
            if (lookPoint != null) lookPoint.position = pivot.position;
            if (vcam != null) vcam.PreviousStateIsValid = false;
        }

        // ------------------------------------------------------------------ framing presets (Director A/B/C, 2026-10-30)

        /// <summary>The framing presets for the Director's choice: A = the scene's framing, B = closer (the hero on the left
        /// third, the path ahead clear), C = half way. Switched at run time (F7 in the editor or a dev build, the bridge's
        /// 'camera a|b|c'); the tight-lane and finisher views and every space / threat rule stay as they are.</summary>
        public enum FramingPreset { A, B, C }
        public static FramingPreset Preset { get; private set; }
        public static void SetPreset(FramingPreset p)
        {
            Preset = p;
            foreach (var c in FindObjectsByType<PlayerCamera>(FindObjectsSortMode.None)) c.ApplyPreset(p);
            Debug.Log("[PlayerCamera] framing preset " + p);
        }

        struct PresetValues
        {
            public Framing explore, sprint, combat, group, locked; public float pivot, fov;
            public PresetValues(PlayerCamera c) { explore = c.explore; sprint = c.sprint; combat = c.combat; group = c.combatGroup; locked = c.lockedOn; pivot = c.pivotHeight; fov = c.baseFov; }
        }
        PresetValues presetA;
        bool presetInit;

        static readonly Framing CloseExplore = new Framing(2.0f, 0.50f, 0.12f, 52f), CloseSprint = new Framing(2.3f, 0.45f, 0.10f, 56f),
                                CloseCombat = new Framing(2.6f, 0.47f, 0.16f, 53f), CloseGroup = new Framing(3.3f, 0.42f, 0.24f, 54f),
                                CloseLocked = new Framing(2.8f, 0.55f, 0.18f, 52f);
        const float ClosePivot = 1.60f, CloseFov = 52f;

        void ApplyPreset(FramingPreset p)
        {
            if (!presetInit) { presetA = new PresetValues(this); presetInit = true; }
            float t = p == FramingPreset.A ? 0f : p == FramingPreset.B ? 1f : 0.5f;
            explore = Framing.Lerp(presetA.explore, CloseExplore, t);
            sprint = Framing.Lerp(presetA.sprint, CloseSprint, t);
            combat = Framing.Lerp(presetA.combat, CloseCombat, t);
            combatGroup = Framing.Lerp(presetA.group, CloseGroup, t);
            lockedOn = Framing.Lerp(presetA.locked, CloseLocked, t);
            pivotHeight = Mathf.Lerp(presetA.pivot, ClosePivot, t);
            baseFov = Mathf.Lerp(presetA.fov, CloseFov, t);
        }

        void LateUpdate()
        {
            if ((Application.isEditor || Debug.isDebugBuild) && UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.f7Key.wasPressedThisFrame)
                SetPreset((FramingPreset)(((int)Preset + 1) % 3));
            if (player == null || pivot == null) return;
            float dt = Time.unscaledDeltaTime;   // camera control never slows with hit-stop / slow motion
            if (dt <= 0f) return;

            // ---- anchor: fast horizontally, softer vertically (steps, stairs, landing)
            if (climber == null && player != null) climber = player.GetComponent<LadderClimber>();
            Vector3 target = climber != null && climber.IsClimbing ? climber.CameraAnchor(pivotHeight) : player.position + Vector3.up * pivotHeight;
            Vector3 p = pivot.position;
            float kx = 1f - Mathf.Exp(-pivotSharpness * dt), ky = 1f - Mathf.Exp(-pivotSharpnessY * dt);
            p.x = Mathf.Lerp(p.x, target.x, kx); p.z = Mathf.Lerp(p.z, target.z, kx); p.y = Mathf.Lerp(p.y, target.y, ky);
            if ((p - target).sqrMagnitude > 9f) p = target;   // never trail more than 3 m (teleports not announced)
            pivot.position = p;
            if (lookPoint != null) lookPoint.position = p;

            // ---- view direction
            Combatant locked = lockOn != null ? lockOn.Target : null;
            bool executing = self != null && self.CurrentState == Combatant.State.Executing;
            if (!executing) execFraming = false;
            if (executing)
            {
                // finisher: swing round to a three-quarter view from the side nearer the camera, so both fighters and
                // the blow are in frame (from behind, the knight's own back hid the victim)
                if (!execFraming)
                {
                    execFraming = true;
                    float f = Mathf.Atan2(player.forward.x, player.forward.z) * Mathf.Rad2Deg;
                    float a = f + execSideAngle, b = f - execSideAngle;
                    execYaw = Mathf.Abs(Mathf.DeltaAngle(yaw, a)) < Mathf.Abs(Mathf.DeltaAngle(yaw, b)) ? a : b;
                }
                float k = 1f - Mathf.Exp(-execYawSharpness * dt);
                yaw = Mathf.LerpAngle(yaw, execYaw, k);
                pitch = Mathf.Lerp(pitch, execPitch, k);
            }
            else if (locked != null)
            {
                Vector3 to = locked.transform.position - player.position;
                Vector3 flat = GK.Flat(to);
                if (flat.sqrMagnitude > 0.01f)
                {
                    float k = 1f - Mathf.Exp(-lockYawSharpness * dt);
                    float wantYaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
                    // a switch to another target eases over switchTime (QUALITY camera: at most 0.25 s); the follow
                    // alone took 1.28 s to settle an 84 deg switch
                    if (lastLocked != null && locked != lastLocked) { switchFrom = yaw; switchT = 0f; }
                    lastLocked = locked;
                    if (switchT < switchTime)
                    {
                        switchT += dt;
                        float s = Mathf.Clamp01(switchT / switchTime); s = s * s * (3f - 2f * s);
                        yaw = Mathf.LerpAngle(switchFrom, wantYaw, s);
                    }
                    else yaw = Mathf.LerpAngle(yaw, wantYaw, k);
                    // look slightly down at a smaller target, up at a taller / higher one
                    float rise = Mathf.Atan2(-(to.y), flat.magnitude) * Mathf.Rad2Deg;
                    pitch = Mathf.Lerp(pitch, Mathf.Clamp(lockPitch + rise * 0.5f, pitchRange.x, pitchRange.y), k);
                }
            }
            else if (input != null && Time.timeScale > 0f)
            {
                switchT = switchTime; lastLocked = null;
                Vector2 look = input.Look;
                if (look.sqrMagnitude > 1e-6f) lastLookTime = Time.unscaledTime;
                yaw = Mathf.Repeat(yaw + look.x + 180f, 360f) - 180f;
                pitch = Mathf.Clamp(pitch - look.y, pitchRange.x, pitchRange.y);
                bool moving = controller != null && controller.CurrentSpeed > 0.5f;
                if (pitchRecenterDelay > 0f && moving && Time.unscaledTime - lastLookTime > pitchRecenterDelay)
                    pitch = Mathf.MoveTowards(pitch, defaultPitch, pitchRecenterSpeed * dt);
            }
            UpdateClearance(dt, executing);
            pivot.rotation = Quaternion.Euler(pitch, yaw + clearYaw, 0f);

            ApplyRig(dt);
            UpdateSelfHide();
        }

        void ApplyRig(float dt)
        {
            // ---- state
            SenseSpace(dt);
            var director = CombatDirector.Existing;
            int engaged = director != null && director.InCombat ? director.EngagedEnemies : 0;
            Mode mode;
            if (self != null && self.CurrentState == Combatant.State.Executing) mode = Mode.Executing;
            else if (lockOn != null && lockOn.Target != null) mode = Mode.LockOn;
            else if (tightSpace) mode = Mode.Tight;
            else if (engaged >= 3) mode = Mode.CombatGroup;
            else if (engaged > 0) mode = Mode.Combat;
            else if (controller != null && controller.IsSprinting) mode = Mode.Sprint;
            else mode = Mode.Explore;
            CurrentMode = mode;

            Framing want = FramingFor(mode);
            want.fov = baseFov + (want.fov - explore.fov);
            if (self != null && self.IsCharging) want.fov -= chargeFovDrop * self.ChargeAmount;
            // looking up brings the camera in (it would sink into the ground), looking down pulls slightly out
            want.distance *= Mathf.Lerp(0.7f, 1f, Mathf.InverseLerp(pitchRange.x, 0f, pitch))
                           * Mathf.Lerp(1f, 1.1f, Mathf.InverseLerp(20f, pitchRange.y, pitch));
            float sharp = mode == Mode.Executing ? framingSharpness * 2f : framingSharpness;
            current = dt <= 0f ? want : Framing.Lerp(current, want, 1f - Mathf.Exp(-sharp * dt));

            // ---- shoulder side (auto swap when a wall crowds the camera shoulder, at most once per second)
            side = dt <= 0f ? sideTarget : Mathf.MoveTowards(side, sideTarget, dt * 2.5f);

            float pw = PunchWeight();
            if (follow != null)
            {
                follow.ShoulderOffset = new Vector3(current.shoulderX, current.shoulderY + clearLift, 0f);
                follow.VerticalArmLength = 0f;
                follow.CameraSide = side;
                follow.CameraDistance = current.distance * Mathf.Lerp(1f, punchScale, pw);
            }
            if (vcam != null)
            {
                LensSettings lens = vcam.Lens;
                lens.FieldOfView = current.fov - punchFov * pw;
                vcam.Lens = lens;
            }
        }

        Framing FramingFor(Mode m)
        {
            switch (m)
            {
                case Mode.Sprint: return sprint;
                case Mode.Combat: return combat;
                case Mode.CombatGroup: { var g = combatGroup; g.shoulderY += groupLift; g.distance += groupPullBack; return g; }
                case Mode.LockOn: return lockedOn;
                case Mode.Tight: return tight;
                case Mode.Executing: return executing;
                default: return explore;
            }
        }

        // ------------------------------------------------------------------ threat clearance
        // Over the shoulder, a foe standing behind the hero's line of sight is hidden by his back (the centre foe of a
        // 1 v 3 in the closer presets). Orbiting the view round the hero moves the foes against him (parallax) without
        // moving him in frame, so the view takes the smallest orbit, within maxClearYaw, that leaves no foe in range
        // behind his silhouette. Movement and strafing read the output camera, so they follow the orbit.
        readonly List<Vector3> clearFoes = new List<Vector3>(8);

        void UpdateClearance(float dt, bool executing)
        {
            clearFoes.Clear();
            var director = CombatDirector.Existing;
            if (threatClearance && !executing && !tightSpace && director != null && self != null)
            {
                Vector3 pp = player.position;
                for (int i = 0; i < director.all.Count; i++)
                {
                    var c = director.all[i];
                    if (c == null || c.team == self.team || c.IsDead) continue;
                    Vector3 cp = c.transform.position;
                    if ((cp - pp).sqrMagnitude <= clearRange * clearRange) clearFoes.Add(cp + Vector3.up * 1.25f);
                }
            }
            if (clearFoes.Count == 0) { clearWant = 0f; clearLiftWant = 0f; }
            else
            {
                // keep the current choice unless another hides clearly less (no hunting between two near-equal ones)
                float bestB = clearWant, bestL = clearLiftWant, bestCost = ClearCost(clearWant, clearLiftWant) - 0.02f;
                for (float l = 0f; l <= maxClearLift + 0.001f; l += 0.1f)
                    for (float b = -maxClearYaw; b <= maxClearYaw + 0.01f; b += 1f)
                    {
                        float cost = ClearCost(b, l);
                        if (cost < bestCost - 1e-4f) { bestB = b; bestL = l; bestCost = cost; }
                    }
                clearWant = bestB; clearLiftWant = bestL;
            }
            float ke = 1f - Mathf.Exp(-clearSharpness * dt);
            clearYaw = dt <= 0f || !threatClearance ? clearWant : Mathf.Lerp(clearYaw, clearWant, ke);
            // the lift settles twice as fast as the orbit (it was still 0.1-0.3 deg short of its spare at a still 2 s in)
            clearLift = dt <= 0f || !threatClearance ? clearLiftWant : Mathf.Lerp(clearLift, clearLiftWant, 1f - Mathf.Exp(-clearSharpness * 2f * dt));
        }

        /// <summary>How much the hero hides the foes in range with the view orbited by <paramref name="b"/> degrees and
        /// raised by <paramref name="lift"/> m: per foe behind him, the angle (deg) still missing to keep clearSpare beside
        /// his silhouette or over his head, whichever is less; plus a small price for orbit and a smaller one for height, so
        /// the least change clears them and height comes first.</summary>
        float ClearCost(float b, float lift)
        {
            Quaternion r = Quaternion.Euler(pitch, yaw + b, 0f), inv = Quaternion.Inverse(r);
            float sx = current.shoulderX * (2f * side - 1f);
            Vector3 cam = pivot.position + r * new Vector3(sx, current.shoulderY + lift, -current.distance);
            Vector3 h = inv * (player.position + Vector3.up * 1.25f - cam);
            if (h.z < 0.3f) return 0f;
            float hAz = Mathf.Atan2(h.x, h.z) * Mathf.Rad2Deg, hHalf = Mathf.Atan2(0.34f, h.z) * Mathf.Rad2Deg;
            Vector3 top = inv * (player.position + Vector3.up * 1.85f - cam);
            float topEl = Mathf.Atan2(top.y, top.z) * Mathf.Rad2Deg;
            float cost = Mathf.Abs(b) * 0.03f + lift * 0.2f;
            for (int i = 0; i < clearFoes.Count; i++)
            {
                Vector3 f = inv * (clearFoes[i] - cam);
                if (f.z < h.z + 0.3f) continue;   // beside or in front of him: not hidden
                float gap = Mathf.Abs(Mathf.DeltaAngle(Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg, hAz));
                float sideShort = hHalf + Mathf.Atan2(0.3f, f.z) * Mathf.Rad2Deg + clearSpare - gap;      // to show beside him
                float overShort = topEl + clearSpare - Mathf.Atan2(f.y, f.z) * Mathf.Rad2Deg;            // to show over his head
                float shortBy = Mathf.Min(sideShort, overShort);
                if (shortBy > 0f) cost += shortBy;
            }
            return cost;
        }

        void SenseSpace(float dt)
        {
            Vector3 o = pivot.position;
            Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
            float maxProbe = 3f;
            float r = Probe(o, right, maxProbe), l = Probe(o, -right, maxProbe), up = Probe(o, Vector3.up, 3f);
            bool tightNow = r + l < tightWidth || up < tightCeiling;
            // hysteresis: enter immediately, leave after 0.6 s of open space (no pumping at doorways)
            if (tightNow) { tightSpace = true; tightTimer = 0.6f; }
            else if (tightSpace) { tightTimer -= dt; if (tightTimer <= 0f) tightSpace = false; }

            float need = explore.shoulderX + cameraRadius + 0.1f;
            float want = sideTarget;
            if (sideTarget > 0.5f && r < need && l > r + 0.3f) want = 0f;
            else if (sideTarget < 0.5f && l < need && r > l + 0.3f) want = 1f;
            else if (sideTarget < 0.5f && r >= need + 0.4f) want = 1f;      // back to the default right shoulder when free
            if (want != sideTarget && Time.unscaledTime - lastSideSwap > 1f) { sideTarget = want; lastSideSwap = Time.unscaledTime; }
        }

        float Probe(Vector3 origin, Vector3 dir, float max)
        {
            RaycastHit hit;
            return Physics.SphereCast(origin, cameraRadius, dir, out hit, max, obstacles, QueryTriggerInteraction.Ignore) ? hit.distance : max;
        }

        void UpdateSelfHide()
        {
            if (outputCamera == null || playerRenderers == null) return;
            Vector3 h = head != null ? head.position : pivot.position;
            float d = Vector3.Distance(outputCamera.position, h);
            if (!hidden && d < hideDistance) SetHidden(true);
            else if (hidden && d > hideDistance + 0.15f) SetHidden(false);
        }

        void SetHidden(bool value)
        {
            if (hidden == value || playerRenderers == null) return;
            hidden = value;
            for (int i = 0; i < playerRenderers.Length; i++)
            {
                var r = playerRenderers[i];
                if (r == null) continue;
                // keep the shadow so the world still reads the character
                r.shadowCastingMode = value ? UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly : shadowModes[i];
            }
        }
    }
}
