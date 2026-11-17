using System.Collections.Generic;
using GeorgianKnight.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GeorgianKnight
{
    /// <summary>
    /// Pooled impact effects driven by combat events, built in code so the look is tuned in one place:
    /// <list type="bullet">
    /// <item>steel on steel: hot streaks that bounce off the ground, embers that linger, a star flash, a short warm
    /// light on the scene and a wisp of smoke. On a shield's wooden face: splinters and dust, sparks only where the
    /// edge meets the iron boss. A parry doubles the steel burst.</item>
    /// <item>flesh: droplets thrown along the cut, heavier drops and a red mist; drops that reach the ground leave
    /// splats, and a pool spreads under a body.</item>
    /// <item>a critical: more blood and a bigger burst (the slow motion is CombatFeel's, the screen accent FeelPulse's).</item>
    /// </list>
    /// Size, count and speed follow the blow's force. Every particle material is soft against what it touches and fades
    /// near the camera; dust, smoke, mist and embers move with the scene's WindZone; self-lit sparks are capped below the
    /// sun's brightness. Honors the gore level and "reduce flashes".
    /// </summary>
    // late: the blade blood film is drawn in LateUpdate, after the arm and finger fixes (SwordArmPose 164 ... CCHelperBones
    // 170), the blade's self-collision (210) and its hitbox (220), so the film sits on the steel as drawn, not a frame behind
    [DefaultExecutionOrder(230)]
    public class HitFX : MonoBehaviour
    {
        public Material additive;   // sparks / flash
        public Material alpha;      // blood / dust
        [Tooltip("What blood and sparks land on (and splats are laid on).")]
        public LayerMask groundMask = (1 << 0) | (1 << 10);
        [Tooltip("Blood marks kept on the ground at once (the oldest is reused).")]
        public int maxSplats = 96;

        [Tooltip("Log every impact and its result to the console as a warning (tests).")]
        public static bool Trace;
        readonly HashSet<Combatant> hooked = new HashSet<Combatant>();
        ParticleSystem sparks, embers, flash, smoke, splinters, blood, bloodChunks, bloodMist, dust;
        GameSettings settings;
        Subscriptions subs;

        void Start()
        {
            ISettingsService s;
            if (Services.TryGet(out s)) settings = s.Current;
            subs = new Subscriptions().On<SettingsChanged>(e => settings = e.Settings);
            if (additive == null) additive = MakeMat(true, SoftDot());
            if (alpha == null) alpha = MakeMat(false, SoftDot());
            var hot = MakeMat(true, HotDot());
            var star = MakeMat(true, Star());
            var puff = MakeMat(false, Puff());
            selfLit = new[] { additive, hot, star };
            var drop = MakeMat(false, Drop());

            // ---- steel
            // short hot streaks (a spark's travel in a few milliseconds of exposure, not a laser line)
            sparks = Make("Sparks", hot, Grad(new Color(1f, 0.78f, 0.42f), new Color(1f, 0.45f, 0.1f), new Color(0.75f, 0.12f, 0.02f)),
                          0.01f, 0.022f, 3.5f, 9f, 0.2f, 0.5f, 1.25f, Stretch: 0.016f);
            Cone(sparks, 38f, 0.015f);
            Collide(sparks, 0.35f, 0.45f, 0.15f, false);
            embers = Make("Embers", hot, Grad(new Color(4f, 2.2f, 0.7f), new Color(2.5f, 0.8f, 0.15f), new Color(0.8f, 0.1f, 0.02f)),
                          0.006f, 0.012f, 0.6f, 2.4f, 0.6f, 1.3f, 0.3f);
            Cone(embers, 60f, 0.03f);
            var en = embers.noise; en.enabled = true; en.strength = 0.6f; en.frequency = 2.2f; en.scrollSpeed = 1.5f;
            Collide(embers, 0.2f, 0.6f, 0.2f, false);
            flash = Make("Flash", star, Grad(new Color(5f, 4.6f, 3.6f), new Color(4f, 3f, 1.6f), new Color(2f, 1f, 0.3f)),
                         0.32f, 0.5f, 0f, 0f, 0.06f, 0.09f, 0f);
            var fm = flash.main; fm.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            Grow(flash, 0.6f, 1.25f);
            smoke = Make("SteelSmoke", puff, Grad(new Color(0.55f, 0.55f, 0.55f, 0.32f), new Color(0.5f, 0.5f, 0.5f, 0.18f), new Color(0.45f, 0.45f, 0.45f, 0f)),
                         0.12f, 0.2f, 0.2f, 0.6f, 0.5f, 0.9f, -0.03f);
            Grow(smoke, 0.5f, 2.2f);
            Spin(smoke);
            splinters = Make("Splinters", alpha, Grad(new Color(0.42f, 0.28f, 0.15f), new Color(0.32f, 0.2f, 0.1f), new Color(0.25f, 0.15f, 0.08f)),
                             0.008f, 0.022f, 2f, 5f, 0.5f, 1f, 1.4f, Stretch: 0.02f);
            Cone(splinters, 45f, 0.03f);
            Collide(splinters, 0.25f, 0.5f, 0f, false);

            // ---- flesh
            // fresh blood: a deep arterial red that darkens in flight; lit, so each drop carries a wet highlight
            var wet = MakeLitMat(Drop());
            // streaked only as far as a camera's exposure would smear them (a few cm), not into dark spikes
            // (darker than first tuned: at 1.5 m the old red read as bright petals; arterial blood is near black-red)
            // D22: 6-16 mm camera-facing cards read as red flakes at 1-2 m. Real spatter is 1-6 mm drops: small round
            // lit beads (mesh, not cards), many of them, dark and glossy
            blood = Make("Blood", wet, Grad(new Color(0.42f, 0.018f, 0.012f), new Color(0.3f, 0.008f, 0.006f), new Color(0.22f, 0.004f, 0.003f)),
                         0.0025f, 0.006f, 2.2f, 5.5f, 0.45f, 0.95f, 1.8f);
            Round(blood);
            Cone(blood, 24f, 0.03f);
            Collide(blood, 0f, 0f, 1f, true);
            // streams: the thin ligaments a cut throws, long and narrow in flight
            bloodChunks = Make("BloodChunks", wet, Grad(new Color(0.38f, 0.015f, 0.01f), new Color(0.28f, 0.007f, 0.005f), new Color(0.2f, 0.004f, 0.003f)),
                               0.003f, 0.006f, 1.6f, 3.6f, 0.55f, 1f, 2f, Stretch: 0.012f);
            Cone(bloodChunks, 30f, 0.03f);
            Collide(bloodChunks, 0f, 0f, 1f, true);
            // a faint fine mist at the wound, gone in under half a second (a dense cloud read as paint)
            bloodMist = Make("BloodMist", puff, Grad(new Color(0.45f, 0.03f, 0.025f, 0.28f), new Color(0.35f, 0.02f, 0.015f, 0.14f), new Color(0.28f, 0.01f, 0.01f, 0f)),
                             0.04f, 0.09f, 0.4f, 1.4f, 0.25f, 0.45f, 0.08f);
            Grow(bloodMist, 0.7f, 1.7f);
            Spin(bloodMist);
            foreach (var ps in new[] { blood, bloodChunks })
                ps.gameObject.AddComponent<BloodDrops>().fx = this;

            dust = Make("Dust", puff, Grad(new Color(0.55f, 0.5f, 0.42f, 0.4f), new Color(0.5f, 0.45f, 0.38f, 0.25f), new Color(0.45f, 0.4f, 0.33f, 0f)),
                        0.5f, 1f, 0.6f, 1.6f, 0.9f, 1.4f, -0.05f);
            Cone(dust, 70f, 0.4f);
            Grow(dust, 0.6f, 1.8f);
            Spin(dust);

            // the scene's one wind (World's WindZone) carries the light things; heavy drops and sparks fly on their own
            Wind(dust, 0.9f); Wind(smoke, 0.7f); Wind(bloodMist, 0.45f); Wind(embers, 0.5f);

            BuildLights();
            BuildSplats();
            BuildSplatPool();     // at load, not on the first drop that lands (GK-26: 17.5 KB in a fight frame)
            BuildDecalPool();
            BuildFallingDrops();
            CapToSun();
            Prewarm();
            WarmHits();
        }

        /// <summary>
        /// The first hit of a fight must not hitch (QUALITY 3b, rhythm): emit one particle of every system far below the
        /// world for a frame so its material and shader variant are uploaded at load, then clear them. Decals and lights
        /// are touched the same way.
        /// </summary>
        void Prewarm()
        {
            var hidden = new Vector3(0f, -5000f, 0f);
            foreach (var ps in new[] { sparks, embers, flash, smoke, splinters, blood, bloodChunks, bloodMist, dust })
            {
                if (ps == null) continue;
                var ep = new ParticleSystem.EmitParams { position = hidden, applyShapeToPosition = false, startLifetime = 0.05f };
                ps.Emit(ep, 1);
            }
            WarmDecals();
            prewarmClearAt = Time.frameCount + 2;
        }

        int prewarmClearAt = -1;

        /// <summary>
        /// One hit of every result through OnHit at load, far below the world, with no attacker or target (so no wound,
        /// no blood on a blade): the first real hit of a fight then finds every path already run once (GK-26: ~2.3 KB
        /// on each of the first hits of wave 1, first use only). The particles land with Prewarm's and are cleared with them.
        /// </summary>
        void WarmHits()
        {
            var h = new HitData { point = new Vector3(0f, -5000f, 0f), direction = Vector3.forward, damage = 1f };
            OnHit(null, h, HitResult.Hit);
            OnHit(null, h, HitResult.Killed);
            OnHit(null, h, HitResult.Blocked);
            OnHit(null, h, HitResult.GuardBroken);
            OnHit(null, h, HitResult.Parried);
            h.critical = true; h.reaction = HitReaction.Knockdown;
            OnHit(null, h, HitResult.Hit);
        }

        void OnDestroy()
        {
            if (subs != null) subs.Dispose();
            foreach (var kv in films) if (kv.Value != null) kv.Value.Dispose();
            films.Clear();
        }

        float Gore { get { return settings == null ? 1f : settings.gore == GoreLevel.Off ? 0f : settings.gore == GoreLevel.Reduced ? 0.4f : 1f; } }
        bool CalmFlashes { get { return settings != null && settings.reduceFlashes; } }

        // ------------------------------------------------------------------ events

        void Update()
        {
            var all = CombatDirector.I.all;
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c != null && hooked.Add(c)) Hook(c);
            }
            // one blade film a frame for the combatants just hooked (~80 KB each; a wave's spawn is spread over frames)
            if (filmPending.Count > 0)
            {
                var owner = filmPending.Dequeue();
                if (owner != null && owner.weapon != null) FilmFor(owner, owner.weapon);
            }
            UpdateLights();
            UpdateSplats();
            UpdateDecals();
            WarmDecalStep();
            UpdateTimers();
            if (prewarmClearAt > 0 && Time.frameCount >= prewarmClearAt)
            {
                prewarmClearAt = -1;
                foreach (var ps in new[] { sparks, embers, flash, smoke, splinters, blood, bloodChunks, bloodMist, dust })
                    if (ps != null) ps.Clear();
            }
            if (Time.unscaledTime >= nextSunCheck) CapToSun();
        }

        void LateUpdate() { UpdateBlades(); }

        // in its own method: the lambdas' closure is allocated only when a combatant is hooked (GK-26: inline in the loop,
        // its scope allocated 32 B per combatant every frame, hooked or not)
        void Hook(Combatant victim)
        {
            if (victim.weapon != null) filmPending.Enqueue(victim);   // its blade film is built ahead of its first cut
            HurtCollidersOf(victim);                                   // and its hurtboxes gathered (GK-26: not on the first wound)
            // the lookups its first blocked or wounding hit makes, made now (GK-26: first-use allocations)
            victim.GetComponent<FightingStyles>();
            if (victim.Anim != null && victim.Anim.isHuman) victim.Anim.GetBoneTransform(HumanBodyBones.Head);
            victim.HitDealt += OnHit;
            victim.SwingStarted += a => { if (Trace && victim != null) Debug.LogWarning("[GKFX] swing " + (a != null ? a.name : "?") + " by " + victim.name); };
            victim.Died += h =>
            {
                if (victim == null) return;
                Emit(dust, victim.transform.position + Vector3.up * 0.15f, Vector3.up, 14);
                if (Gore > 0f) Pool(victim);
            };
        }

        void OnHit(Combatant target, HitData hit, HitResult r)
        {
            Vector3 dir = hit.direction.sqrMagnitude > 1e-4f ? hit.direction.normalized : Vector3.up;
            Vector3 away = target != null ? (target.transform.position - (hit.attacker != null ? hit.attacker.transform.position : hit.point)) : dir;
            away.y = 0f;
            away = away.sqrMagnitude > 1e-4f ? away.normalized : dir;
            bool crit = hit.critical || hit.execution;
            // the blow's force: a light cut and a heavy one never look the same (QUALITY 3b, force and direction)
            float force = Force(hit);
            if (Trace) Debug.LogWarning("[GKFX] " + r + (crit ? " CRITICAL" : "") + " by " + (hit.attack != null ? hit.attack.name : "?") + " on " + (target != null ? target.name + " (" + target.CurrentState + ")" : "?"));
            // a boot or a shield's face: a dull thud of dust off the cloth, no blood
            bool blunt = hit.attack != null && (hit.attack.kind == AttackKind.Kick || hit.attack.shieldBash);
            if (blunt && (r == HitResult.Hit || r == HitResult.Killed))
            {
                Emit(dust, hit.point, (away + Vector3.up * 0.3f).normalized, 3);
                return;
            }
            switch (r)
            {
                case HitResult.Hit:
                case HitResult.Killed:
                    {
                        float g = Gore * (crit ? 1.8f : 1f) * (r == HitResult.Killed ? 1.5f : 1f) * force;
                        if (g <= 0f) break;
                        // thrown along the blade's travel and out of the far side, a little up; a harder blow throws faster
                        Vector3 spray = (dir * 0.65f + away * 0.45f + Vector3.up * 0.25f).normalized;
                        float sp = Mathf.Lerp(0.8f, 1.25f, Mathf.InverseLerp(0.6f, 1.8f, force));
                        Emit(blood, hit.point, spray, Mathf.RoundToInt(90 * g), sp);
                        Emit(bloodChunks, hit.point, (spray + Vector3.down * 0.15f).normalized, Mathf.RoundToInt(10 * g), sp);
                        Emit(bloodMist, hit.point, spray, Mathf.RoundToInt(4 * g));
                        // a few drops back the way the blade came (the wound opening toward the attacker)
                        Emit(blood, hit.point, (-away + Vector3.up * 0.3f).normalized, Mathf.RoundToInt(8 * g), sp * 0.8f);
                        // the wound, where the edge went in
                        if (target != null && Gore > 0f) Wound(target, hit.point, dir, crit ? 1.5f : r == HitResult.Killed ? 1.25f : 1f);
                        // blood stays on the blade that cut
                        if (hit.attacker != null && Gore > 0f && (hit.attack == null || (hit.attack.kind != AttackKind.Kick && !hit.attack.shieldBash)))
                            BloodOnBlade(hit.attacker, (crit ? 1.6f : 1f) * Mathf.Sqrt(force), hit.point,
                                         target != null ? target.transform.position - hit.attacker.transform.position : dir);
                        break;
                    }
                case HitResult.Blocked:
                case HitResult.GuardBroken:
                    {
                        Vector3 back = (-dir + Vector3.up * 0.35f).normalized;
                        var styles = target != null ? target.GetComponent<FightingStyles>() : null;
                        bool onShield = styles != null && styles.ShieldOnArm;
                        bool heavy = r == HitResult.GuardBroken || (hit.attack != null && hit.attack.kind != AttackKind.Light);
                        float k = force * (heavy ? 1.2f : 1f);
                        if (onShield && !OnBoss(styles.shield, hit.point))
                        {
                            // steel into the wooden face: splinters and a puff of dust, no sparks, no flash
                            Emit(splinters, hit.point, (back + away * 0.5f).normalized, Mathf.RoundToInt(16 * k));
                            Emit(dust, hit.point, back, Mathf.RoundToInt(2 * k));
                            Gouge(styles.shield, hit.point, dir, heavy);
                            break;
                        }
                        // steel on steel (a blade, or the shield's iron boss)
                        Emit(sparks, hit.point, back, Mathf.RoundToInt((onShield ? 14 : 34) * k), Mathf.Lerp(0.85f, 1.2f, Mathf.InverseLerp(0.6f, 1.8f, force)));
                        Emit(embers, hit.point, back, Mathf.RoundToInt((onShield ? 4 : 9) * k));
                        Emit(flash, hit.point, Vector3.up, 1);
                        Emit(smoke, hit.point, Vector3.up, 2);
                        Flare(hit.point, (heavy ? 1.6f : 1.1f) * Mathf.Min(1f, sunCap / 2.5f), 0.07f, new Color(1f, 0.66f, 0.32f));
                        break;
                    }
                case HitResult.Parried:
                    {
                        Vector3 back = (-dir + Vector3.up * 0.45f).normalized;
                        Emit(sparks, hit.point, back, 75);
                        Emit(sparks, hit.point, (dir + Vector3.up * 0.3f).normalized, 25);
                        Emit(embers, hit.point, back, 18);
                        Emit(flash, hit.point, Vector3.up, 2);
                        Emit(smoke, hit.point, Vector3.up, 3);
                        Flare(hit.point, 2f * Mathf.Min(1f, sunCap / 2.5f), 0.1f, new Color(1f, 0.72f, 0.42f));
                        break;
                    }
            }
            if (hit.reaction == HitReaction.Knockdown && target != null) Emit(dust, target.transform.position + Vector3.up * 0.1f, Vector3.up, 10);
        }

        void Emit(ParticleSystem ps, Vector3 pos, Vector3 dir, int count, float speed = 1f)
        {
            if (ps == null || count <= 0) return;
            if (CalmFlashes && ps == flash) return;
            if (TooFar(pos)) return;
            ps.transform.position = pos;
            ps.transform.rotation = Quaternion.LookRotation(dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.up);
            if (Mathf.Approximately(speed, 1f)) { ps.Emit(count); return; }
            var main = ps.main;
            float was = main.startSpeedMultiplier;
            main.startSpeedMultiplier = was * speed;
            ps.Emit(count);
            main.startSpeedMultiplier = was;
        }

        /// <summary>0.6 (a light cut) .. 1.8 (an execution, a charged blow) from the attack's kind and damage.</summary>
        static float Force(HitData hit)
        {
            float k = 1f;
            if (hit.attack != null)
            {
                switch (hit.attack.kind)
                {
                    case AttackKind.Light: k = 0.8f; break;
                    case AttackKind.Riposte: k = 1.05f; break;
                    case AttackKind.Heavy: case AttackKind.Sprint: k = 1.25f; break;
                    case AttackKind.Charged: case AttackKind.Skill: k = 1.4f; break;
                    case AttackKind.Execution: k = 1.6f; break;
                }
            }
            if (hit.execution) k = Mathf.Max(k, 1.6f);
            // within a kind, the damage it really did (charge, stats) nudges it
            if (hit.attack != null && hit.attack.damage > 0f) k *= Mathf.Clamp(Mathf.Sqrt(hit.damage / hit.attack.damage), 0.85f, 1.2f);
            return Mathf.Clamp(k, 0.6f, 1.8f);
        }

        /// <summary>The blade hit the shield's iron boss (its middle 9 cm), not the wooden face.</summary>
        static bool OnBoss(Transform shield, Vector3 point)
        {
            if (shield == null) return false;
            var plate = shield.GetComponent<BoxCollider>();
            Vector3 c = plate != null ? shield.TransformPoint(plate.center) : shield.position;
            Vector3 d = point - c;
            if (plate != null)
            {
                Vector3 sz = plate.size;
                int thin = sz.x <= sz.y && sz.x <= sz.z ? 0 : sz.y <= sz.z ? 1 : 2;
                Vector3 nl = Vector3.zero; nl[thin] = 1f;
                d = Vector3.ProjectOnPlane(d, shield.TransformDirection(nl));
            }
            return d.sqrMagnitude < 0.09f * 0.09f;
        }

        // ---- distance: nothing is emitted beyond what the camera can read (CRAFT 5b, particles culled by distance)
        const float EmitDistance = 45f;
        Transform cam;
        float nextCamCheck;

        bool TooFar(Vector3 pos)
        {
            if (cam == null || Time.unscaledTime >= nextCamCheck)
            {
                var c = Camera.main;
                cam = c != null ? c.transform : null;
                nextCamCheck = Time.unscaledTime + 1f;
            }
            return cam != null && (pos - cam.position).sqrMagnitude > EmitDistance * EmitDistance;
        }

        // ---- the sun is the brightest thing in the frame: self-lit sparks, embers and flashes are capped under it
        Material[] selfLit;
        float sunCap = 2.5f, nextSunCheck;
        static readonly int BaseColorProp = Shader.PropertyToID("_BaseColor");

        void CapToSun()
        {
            nextSunCheck = Time.unscaledTime + 2f;
            var sun = RenderSettings.sun;
            // a white surface in full sun reads at about the sun's intensity; stay at 90 % of it (dawn sun is weak, so
            // sparks get dimmer with it instead of glowing on their own)
            float cap = sun != null && sun.isActiveAndEnabled ? sun.intensity * sun.color.maxColorComponent * 0.9f : 2.5f;
            sunCap = Mathf.Clamp(cap, 0.6f, 3f);
            if (selfLit == null) return;
            for (int i = 0; i < selfLit.Length; i++)
                if (selfLit[i] != null) selfLit[i].SetColor(BaseColorProp, new Color(sunCap, sunCap, sunCap, 1f));
        }

        static void Wind(ParticleSystem ps, float multiplier)
        {
            if (ps == null) return;
            var ef = ps.externalForces;
            ef.enabled = true;
            ef.multiplier = multiplier;
            ef.influenceFilter = ParticleSystemGameObjectFilter.LayerMask;
            ef.influenceMask = ~0;
        }

        // ------------------------------------------------------------------ particle systems

        ParticleSystem Make(string n, Material mat, Gradient life, float size0, float size1, float speed0, float speed1,
                            float life0, float life1, float gravity, float Stretch = 0f)
        {
            var go = new GameObject(n);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 0.5f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life0, life1);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed0, speed1);
            main.startSize = new ParticleSystem.MinMaxCurve(size0, size1);
            main.startColor = Color.white;
            main.gravityModifier = gravity;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 800;
            var em = ps.emission; em.enabled = false;
            var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Sphere; sh.radius = 0.01f;
            var col = ps.colorOverLifetime; col.enabled = true; col.color = life;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = Stretch > 0f ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
            r.velocityScale = Stretch;
            r.lengthScale = Stretch > 0f ? 1f : 1f;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sortingFudge = Stretch > 0f ? -2f : 0f;
            if (Stretch > 0f)
            {
                var sol = ps.sizeOverLifetime; sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0.45f));
            }
            return ps;
        }

        static Gradient Grad(Color a, Color b, Color c)
        {
            // hue and fade; the additive materials' HDR base colour gives the brightness
            var g = new Gradient();
            g.mode = GradientMode.Blend;
            g.SetKeys(new[] { new GradientColorKey(Norm(a), 0f), new GradientColorKey(Norm(b), 0.35f), new GradientColorKey(Norm(c), 1f) },
                      new[] { new GradientAlphaKey(a.a, 0f), new GradientAlphaKey(b.a, 0.45f), new GradientAlphaKey(0f, 1f) });
            return g;
        }

        // a gradient key cannot exceed 1: keep the hue
        static Color Norm(Color c) { float m = Mathf.Max(1f, Mathf.Max(c.r, Mathf.Max(c.g, c.b))); return new Color(c.r / m, c.g / m, c.b / m, c.a); }

        /// <summary>Round beads: the particle drawn as a small lit sphere, never a card.</summary>
        static void Round(ParticleSystem ps)
        {
            var r = ps.GetComponent<ParticleSystemRenderer>();
            if (beadMesh == null) beadMesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            r.renderMode = ParticleSystemRenderMode.Mesh;
            r.mesh = beadMesh;
            r.alignment = ParticleSystemRenderSpace.World;
        }

        static void Cone(ParticleSystem ps, float angle, float radius)
        {
            var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = angle; sh.radius = radius;
        }

        static void Grow(ParticleSystem ps, float from, float to)
        {
            var sol = ps.sizeOverLifetime; sol.enabled = true;
            var c = new AnimationCurve(new Keyframe(0f, from, 0f, (to - from) * 2.5f), new Keyframe(1f, to, 0f, 0f));
            sol.size = new ParticleSystem.MinMaxCurve(1f, c);
        }

        static void Spin(ParticleSystem ps)
        {
            var m = ps.main; m.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            var rol = ps.rotationOverLifetime; rol.enabled = true; rol.z = new ParticleSystem.MinMaxCurve(-0.8f, 0.8f);
        }

        void Collide(ParticleSystem ps, float bounce, float dampen, float lifetimeLoss, bool messages)
        {
            var c = ps.collision;
            c.enabled = true;
            c.type = ParticleSystemCollisionType.World;
            c.mode = ParticleSystemCollisionMode.Collision3D;
            c.quality = ParticleSystemCollisionQuality.Medium;
            c.collidesWith = groundMask;
            c.bounce = bounce;
            c.dampen = dampen;
            c.lifetimeLoss = lifetimeLoss;
            c.radiusScale = 0.5f;
            c.sendCollisionMessages = messages;
            c.maxCollisionShapes = 32;
        }

        // ------------------------------------------------------------------ materials and textures

        static Material MakeMat(bool add, Texture2D tex)
        {
            var sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            var m = new Material(sh);
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", add ? 2f : 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", add ? (int)BlendMode.One : (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = 3000;
            m.SetTexture("_BaseMap", tex);
            // HDR brightness for the additive ones (bloom picks the cores up); capped to the sun by CapToSun
            m.SetColor("_BaseColor", add ? new Color(3f, 3f, 3f, 1f) : Color.white);
            Soft(m);
            return m;
        }

        // particles lit by the scene, with a wet specular: blood drops catch the sun like real ones
        static Material MakeLitMat(Texture2D tex)
        {
            var sh = Shader.Find("Universal Render Pipeline/Particles/Lit");
            if (sh == null) return MakeMat(false, tex);
            var m = new Material(sh);
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = 3000;
            m.SetTexture("_BaseMap", tex);
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Smoothness", 0.88f);
            m.SetFloat("_Metallic", 0f);
            // a trace of its own red so a drop in shade reads dark red, not black
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", new Color(0.09f, 0.004f, 0.003f));
            Soft(m);
            return m;
        }

        /// <summary>No hard line where a particle meets the ground, a body or a wall (soft particles over the last
        /// 12 cm), and nothing pops against the lens (camera fade from 30 cm to 1 m).</summary>
        static void Soft(Material m)
        {
            m.SetFloat("_SoftParticlesEnabled", 1f);
            m.SetFloat("_SoftParticlesNearFadeDistance", 0f);
            m.SetFloat("_SoftParticlesFarFadeDistance", 0.12f);
            m.SetVector("_SoftParticleFadeParams", new Vector4(0f, 1f / 0.12f, 0f, 0f));
            m.EnableKeyword("_SOFTPARTICLES_ON");
            m.SetFloat("_CameraFadingEnabled", 1f);
            m.SetFloat("_CameraNearFadeDistance", 0.3f);
            m.SetFloat("_CameraFarFadeDistance", 1f);
            m.SetVector("_CameraFadeParams", new Vector4(0.3f, 1f / 0.7f, 0f, 0f));
            m.EnableKeyword("_FADING_ON");
        }

        delegate float Shape(float x, float y);   // x, y in -1..1 -> alpha

        static Texture2D Bake(int s, Shape f, bool mips = false)
        {
            var t = new Texture2D(s, s, TextureFormat.RGBA32, mips);
            t.wrapMode = TextureWrapMode.Clamp;
            var px = new Color32[s * s];
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float a = Mathf.Clamp01(f((x + 0.5f) / s * 2f - 1f, (y + 0.5f) / s * 2f - 1f));
                    px[y * s + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            t.SetPixels32(px);
            t.Apply(mips, true);
            return t;
        }

        static Texture2D softDotTex, hotDotTex, starTex, puffTex, dropTex;
        static Texture2D SoftDot() { return softDotTex != null ? softDotTex : softDotTex = Bake(64, (x, y) => { float a = 1f - Mathf.Sqrt(x * x + y * y); a = Mathf.Clamp01(a); return a * a * (3f - 2f * a); }); }
        // a white-hot core with a soft halo
        static Texture2D HotDot() { return hotDotTex != null ? hotDotTex : hotDotTex = Bake(64, (x, y) => { float d2 = x * x + y * y; return Mathf.Exp(-d2 * 18f) + 0.35f * Mathf.Exp(-d2 * 4f); }); }
        // a glint: round glow and four long thin rays
        static Texture2D Star()
        {
            return starTex != null ? starTex : starTex = Bake(128, (x, y) =>
            {
                float d = Mathf.Sqrt(x * x + y * y);
                float glow = Mathf.Exp(-d * d * 22f) + 0.25f * Mathf.Exp(-d * d * 5f);
                float rays = Mathf.Exp(-Mathf.Abs(x) * 60f) * Mathf.Clamp01(1f - Mathf.Abs(y)) + Mathf.Exp(-Mathf.Abs(y) * 60f) * Mathf.Clamp01(1f - Mathf.Abs(x));
                return glow + rays * 0.8f;
            });
        }
        // a cloudy puff: a soft disc broken up by noise
        static Texture2D Puff()
        {
            return puffTex != null ? puffTex : puffTex = Bake(64, (x, y) =>
            {
                float d = Mathf.Sqrt(x * x + y * y);
                float n = Mathf.PerlinNoise(x * 2.3f + 11.7f, y * 2.3f + 3.1f) * 0.6f + Mathf.PerlinNoise(x * 5.1f + 2.2f, y * 5.1f + 7.9f) * 0.4f;
                float a = Mathf.Clamp01(1f - d) * (0.45f + n);
                return a * a * 1.6f;
            });
        }
        // a drop: dense, with a slightly hard edge
        static Texture2D Drop() { return dropTex != null ? dropTex : dropTex = Bake(32, (x, y) => Mathf.Clamp01((1f - Mathf.Sqrt(x * x + y * y)) * 3f)); }

        // ------------------------------------------------------------------ lights

        const int LightCount = 4;
        readonly Light[] lights = new Light[LightCount];
        readonly float[] lightStart = new float[LightCount], lightLen = new float[LightCount], lightPeak = new float[LightCount];
        int nextLight;

        void BuildLights()
        {
            for (int i = 0; i < LightCount; i++)
            {
                var l = new GameObject("ImpactLight" + i).AddComponent<Light>();
                l.transform.SetParent(transform, false);
                l.type = LightType.Point;
                l.range = 1.4f;                 // lights the blades and hands at the clash, not the whole body
                l.shadows = LightShadows.None;
                l.intensity = 0f;
                l.enabled = false;
                lights[i] = l;
            }
        }

        void Flare(Vector3 at, float peak, float seconds, Color c)
        {
            if (CalmFlashes) peak *= 0.35f;
            var l = lights[nextLight];
            lightStart[nextLight] = Time.time; lightLen[nextLight] = seconds; lightPeak[nextLight] = peak;
            nextLight = (nextLight + 1) % LightCount;
            l.transform.position = at;
            l.color = c;
            l.intensity = peak;
            l.enabled = true;
        }

        void UpdateLights()
        {
            for (int i = 0; i < LightCount; i++)
            {
                var l = lights[i];
                if (l == null || !l.enabled) continue;
                float t = (Time.time - lightStart[i]) / Mathf.Max(1e-3f, lightLen[i]);
                if (t >= 1f) { l.enabled = false; continue; }
                l.intensity = lightPeak[i] * (1f - t) * (1f - t);
            }
        }

        // ------------------------------------------------------------------ blood on the ground

        class Splat { public Transform t; public MeshRenderer r; public float born, life, grow, size; public Color col; }
        readonly List<Splat> splats = new List<Splat>();
        int nextSplat;
        Material[] splatMats;
        Mesh splatQuad;
        MaterialPropertyBlock mpb;
        int splatsThisFrame, splatFrame;
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor"), SmoothId = Shader.PropertyToID("_Smoothness");
        static readonly Color SplatColor = new Color(0.3f, 0.012f, 0.008f, 0.94f);
        static readonly Color DriedColor = new Color(0.13f, 0.028f, 0.018f, 0.92f);   // dried blood browns

        void BuildSplats()
        {
            mpb = new MaterialPropertyBlock();
            splatQuad = new Mesh { name = "SplatQuad" };
            splatQuad.vertices = new[] { new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(-0.5f, 0f, 0.5f) };
            splatQuad.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            splatQuad.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            splatQuad.tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) };
            splatQuad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            splatQuad.RecalculateBounds();
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            splatMats = new Material[7];
            for (int v = 0; v < splatMats.Length; v++)
            {
                var m = new Material(lit);
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.SetShaderPassEnabled("ShadowCaster", false);
                m.SetShaderPassEnabled("DepthOnly", false);
                m.SetShaderPassEnabled("DepthNormals", false);
                m.renderQueue = 2990;
                m.SetTexture("_BaseMap", SplatTexture(v));
                m.SetColor("_BaseColor", SplatColor);
                // wet: a sun highlight, but no sky in it (the blue sky over dark red read as purple)
                m.SetFloat("_Smoothness", 0.6f);
                m.SetFloat("_EnvironmentReflections", 0f);
                m.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
                m.SetFloat("_Metallic", 0f);
                m.enableInstancing = true;
                splatMats[v] = m;
            }
        }

        /// <summary>A splat of blood as it lands. Variants 0-5: a drop that struck at a slant - an elongated body along +u
        /// (the way it travelled), its leading edge broken into thin spines with tiny satellite drops flung past them,
        /// a few specks behind. Variant 6: a round pool with a softly wavering rim. The rim is darker (it dries first),
        /// the middle thicker.</summary>
        static Texture2D SplatTexture(int variant)
        {
            const int S = 256;
            var rnd = new System.Random(4127 + variant * 31);
            System.Func<float> R = () => (float)rnd.NextDouble();
            bool pool = variant == 6;
            float seed = variant * 7.31f;
            // the body: an ellipse (or circle for the pool) carried by a few overlapping lobes
            var lobes = new List<Vector4>();   // x, y, rx, ry
            float stretch = pool ? 1f : 1.3f + 0.5f * R();
            int nl = pool ? 7 : 3 + variant % 3;
            for (int i = 0; i < nl; i++)
            {
                float a = R() * Mathf.PI * 2f, d = R() * (pool ? 0.18f : 0.12f);
                float r = (pool ? 0.36f : 0.2f) + R() * 0.08f;
                lobes.Add(new Vector4(Mathf.Cos(a) * d - (pool ? 0f : 0.15f), Mathf.Sin(a) * d * 0.7f, r * stretch, r));
            }
            // spines on the leading edge and satellites past them (not for the pool)
            var spines = new List<Vector4>();  // angle, length, width, start radius
            var sats = new List<Vector3>();
            if (!pool)
            {
                int ns = 5 + variant;
                for (int i = 0; i < ns; i++)
                {
                    float ang = (R() - 0.5f) * 1.6f;                      // within ~45 deg of the travel
                    float len = 0.12f + R() * 0.3f * (1f - Mathf.Abs(ang));
                    spines.Add(new Vector4(ang, len, 0.012f + R() * 0.018f, 0.2f * stretch - 0.15f));
                    if (R() < 0.7f)
                    {
                        float sr = 0.2f * stretch - 0.15f + len + 0.04f + R() * 0.15f;
                        sats.Add(new Vector3(Mathf.Cos(ang) * sr, Mathf.Sin(ang) * sr, 0.01f + R() * 0.025f));
                    }
                }
                for (int i = 0; i < 6; i++)   // specks behind and to the sides
                {
                    float ang = Mathf.PI + (R() - 0.5f) * 2.4f, sr = 0.3f + R() * 0.45f;
                    sats.Add(new Vector3(Mathf.Cos(ang) * sr * 0.8f - 0.1f, Mathf.Sin(ang) * sr * 0.8f, 0.006f + R() * 0.012f));
                }
            }
            var t = new Texture2D(S, S, TextureFormat.RGBA32, true);
            t.wrapMode = TextureWrapMode.Clamp;
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float u = (x + 0.5f) / S * 2f - 1f, w = (y + 0.5f) / S * 2f - 1f;
                    float n = (Mathf.PerlinNoise(u * 7f + seed, w * 7f + seed) - 0.5f) * 0.06f + (Mathf.PerlinNoise(u * 23f + seed, w * 23f - seed) - 0.5f) * 0.02f;
                    float inside = -1f;
                    foreach (var l in lobes)
                    {
                        float dx = (u - l.x) / l.z, dy = (w - l.y) / l.w;
                        float sdf = (1f - Mathf.Sqrt(dx * dx + dy * dy)) * Mathf.Min(l.z, l.w) + n;
                        if (sdf > inside) inside = sdf;
                    }
                    foreach (var sp in spines)
                    {
                        // a thin tapering ray from the body's edge outward
                        Vector2 dir = new Vector2(Mathf.Cos(sp.x), Mathf.Sin(sp.x));
                        Vector2 q = new Vector2(u + 0.15f, w);
                        float along = Vector2.Dot(q, dir) - sp.w;
                        if (along < -0.05f || along > sp.y) continue;
                        float across = Mathf.Abs(dir.x * q.y - dir.y * q.x);
                        float width = sp.z * (1f - Mathf.Clamp01(along / sp.y));
                        float sdf = width - across + n * 0.3f;
                        if (sdf > inside) inside = sdf;
                    }
                    foreach (var st in sats)
                    {
                        float dx = u - st.x, dy = w - st.y;
                        float sdf = st.z - Mathf.Sqrt(dx * dx + dy * dy);
                        if (sdf > inside) inside = sdf;
                    }
                    float a = Mathf.Clamp01(inside * 110f);                 // ~1 px soft edge
                    float depth = Mathf.Clamp01(inside * 9f);
                    // a darker dried rim, a thicker middle
                    byte shade = (byte)(255f * Mathf.Lerp(0.55f, 1f, depth));
                    px[y * S + x] = new Color32(shade, shade, shade, (byte)(a * 255f));
                }
            t.SetPixels32(px);
            t.Apply(true, true);
            return t;
        }

        void BuildSplatPool()
        {
            for (int i = 0; i < maxSplats; i++)
            {
                var go = new GameObject("Splat");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = splatQuad;
                var r = go.AddComponent<MeshRenderer>();
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = true;
                go.SetActive(false);
                splats.Add(new Splat { t = go.transform, r = r });
            }
        }

        /// <summary>Lays a splat on the ground at <paramref name="at"/> (normal <paramref name="normal"/>), streaks along
        /// <paramref name="along"/>. <paramref name="grow"/> &gt; 0 spreads it out over that many seconds (a pool).</summary>
        public void LaySplat(Vector3 at, Vector3 normal, Vector3 along, float size, float grow = 0f, bool round = false)
        {
            if (Gore <= 0f || splatMats == null) return;
            if (splats.Count == 0) BuildSplatPool();
            if (Time.frameCount != splatFrame) { splatFrame = Time.frameCount; splatsThisFrame = 0; }
            if (++splatsThisFrame > 6) return;
            var s = splats[nextSplat];
            nextSplat = (nextSplat + 1) % splats.Count;
            Vector3 fwd = Vector3.ProjectOnPlane(along, normal);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(Random.onUnitSphere, normal);
            // the texture's streaks run along +u (local x): x = along, y = normal
            Quaternion q = Quaternion.LookRotation(Vector3.Cross(fwd.normalized, normal), normal);
            s.t.SetPositionAndRotation(at + normal * (0.004f + Random.value * 0.004f), q);
            s.size = size;
            s.t.localScale = Vector3.one * size * (grow > 0f ? 0.15f : 1f);
            s.r.sharedMaterial = splatMats[round ? 6 : Random.Range(0, 6)];
            s.born = Time.time; s.life = 50f + Random.value * 20f; s.grow = grow;
            // each mark its own shade: fresher and redder, or thinner and browner
            s.col = Color.Lerp(SplatColor, new Color(0.22f, 0.015f, 0.01f, 0.9f), Random.value * 0.6f);
            mpb.SetColor(BaseColorId, s.col);
            s.r.SetPropertyBlock(mpb);
            s.t.gameObject.SetActive(true);
        }

        public bool GroundUnder(Vector3 from, out RaycastHit hit)
        {
            // through anything loose lying there (a dropped weapon or shield is on the Default layer and moves on): the
            // death pool goes on the ground under it, not on the steel in the air
            Vector3 o = from + Vector3.up * 0.3f;
            hit = default(RaycastHit);
            for (int k = 0; k < 3; k++)
            {
                if (!Physics.Raycast(o, Vector3.down, out hit, 4f, groundMask, QueryTriggerInteraction.Ignore)) return false;
                if (hit.rigidbody == null || hit.rigidbody.isKinematic) return true;
                o = hit.point + Vector3.down * 0.01f;
            }
            return false;
        }

        void UpdateSplats()
        {
            for (int i = 0; i < splats.Count; i++)
            {
                var s = splats[i];
                if (!s.t.gameObject.activeSelf) continue;
                float age = Time.time - s.born;
                if (s.grow > 0f && age <= s.grow)
                {
                    float k = age / s.grow; k = 1f - (1f - k) * (1f - k);
                    s.t.localScale = Vector3.one * s.size * Mathf.Lerp(0.15f, 1f, k);
                }
                float fade = Mathf.Clamp01((s.life - age) / 6f);
                if (fade <= 0f) { s.t.gameObject.SetActive(false); continue; }
                // it dries: from wet red to a dark brown over ~20 s, losing its sheen
                if (age < 25f || fade < 1f)
                {
                    float dry = Mathf.Clamp01(age / 20f);
                    var c = Color.Lerp(s.col, DriedColor, dry * dry); c.a *= fade;
                    mpb.SetColor(BaseColorId, c);
                    mpb.SetFloat(SmoothId, Mathf.Lerp(0.6f, 0.2f, dry));
                    s.r.SetPropertyBlock(mpb);
                }
            }
        }

        // ---- the blade drips for a moment after the cut; a pool spreads under a body once it lies still. Timers in
        // fixed arrays ticked from Update (no coroutine allocated per hit).
        const int MaxDrips = 8, MaxPools = 8;
        readonly Transform[] dripTip = new Transform[MaxDrips];
        readonly float[] dripNext = new float[MaxDrips];
        readonly int[] dripLeft = new int[MaxDrips];
        int nextDrip;
        readonly Combatant[] poolVictim = new Combatant[MaxPools];
        readonly float[] poolAt = new float[MaxPools];
        int nextPool;

        void Drip(Transform tip, int drops)
        {
            int i = nextDrip; nextDrip = (nextDrip + 1) % MaxDrips;
            dripTip[i] = tip; dripLeft[i] = drops; dripNext[i] = Time.time + (drops == 1 ? 0f : 0.2f);
        }

        void Pool(Combatant victim)
        {
            int i = nextPool; nextPool = (nextPool + 1) % MaxPools;
            poolVictim[i] = victim; poolAt[i] = Time.time + 1.1f;
        }

        void UpdateTimers()
        {
            float now = Time.time;
            for (int i = 0; i < MaxDrips; i++)
            {
                if (dripLeft[i] <= 0 || now < dripNext[i]) continue;
                var tip = dripTip[i];
                if (tip == null || blood == null) { dripLeft[i] = 0; continue; }
                var em = new ParticleSystem.EmitParams
                {
                    position = tip.position, velocity = Random.insideUnitSphere * 0.15f + Vector3.down * 0.3f,
                    startSize = Random.Range(0.012f, 0.02f), startLifetime = 1.4f, applyShapeToPosition = false
                };
                blood.Emit(em, 1);
                dripLeft[i]--;
                dripNext[i] = now + Random.Range(0.25f, 0.5f);
            }
            for (int i = 0; i < MaxPools; i++)
            {
                var victim = poolVictim[i];
                if (victim == null || now < poolAt[i]) continue;
                poolVictim[i] = null;
                Transform hips = victim.Anim != null && victim.Anim.isHuman ? victim.Anim.GetBoneTransform(HumanBodyBones.Hips) : null;
                Transform chest = victim.Anim != null && victim.Anim.isHuman ? victim.Anim.GetBoneTransform(HumanBodyBones.Chest) : null;
                Vector3 at = chest != null ? (hips != null ? Vector3.Lerp(hips.position, chest.position, 0.6f) : chest.position) : victim.transform.position;
                RaycastHit g;
                if (GroundUnder(at, out g)) LaySplat(g.point, g.normal, victim.transform.forward, 1.1f + Random.value * 0.4f, 5f, true);
            }
        }

        // ---- decals (wounds on bodies, gouges on shields): one fixed pool of projectors, reused oldest first, so a
        // long fight never piles up objects (CRAFT 5b: decals ring-buffered). Each mark rides with the bone or shield
        // it was laid on and fades out at the end of its life.
        const int DecalPoolSize = 64;
        const float WoundLife = 60f, GougeLife = 240f, DecalFadeTime = 5f;
        DecalProjector[] decalPool;
        float[] decalBorn, decalLife;
        int nextDecal;
        Material[] woundMats, gougeMats;

        void BuildDecalPool()
        {
            decalPool = new DecalProjector[DecalPoolSize];
            decalBorn = new float[DecalPoolSize];
            decalLife = new float[DecalPoolSize];
            for (int i = 0; i < DecalPoolSize; i++) decalPool[i] = NewDecal(i);
            woundMats = new Material[4];
            for (int v = 0; v < woundMats.Length; v++) woundMats[v] = Resources.Load<Material>("FX/WoundDecal" + v);
            gougeMats = new Material[3];
            for (int v = 0; v < gougeMats.Length; v++) gougeMats[v] = Resources.Load<Material>("FX/GougeDecal" + v);
        }

        DecalProjector NewDecal(int i)
        {
            var go = new GameObject("Decal" + i);
            go.transform.SetParent(transform, false);
            var dp = go.AddComponent<DecalProjector>();
            dp.pivot = Vector3.zero;
            dp.scaleMode = DecalScaleMode.ScaleInvariant;
            dp.drawDistance = 40f;
            go.SetActive(false);
            return dp;
        }

        /// <param name="pivotZ">Where the box starts along its projection: 0 = centred on <paramref name="pos"/> (a
        /// gouge on the plank's middle plane); size.z / 2 = it starts at pos and reaches size.z inward (a wound).</param>
        DecalProjector PlaceDecal(Transform parent, Vector3 pos, Quaternion rot, Material mat, Vector3 size, float angleFrom, float angleTo, float life, float pivotZ = 0f)
        {
            if (decalPool == null) BuildDecalPool();
            int i = nextDecal;
            nextDecal = (nextDecal + 1) % DecalPoolSize;
            var dp = decalPool[i];
            // a body or shield destroyed with a mark on it takes that projector along: make the slot a new one
            if (dp == null) dp = decalPool[i] = NewDecal(i);
            var t = dp.transform;
            t.SetParent(parent, false);
            t.SetPositionAndRotation(pos, rot);
            dp.material = mat;
            dp.size = size;
            dp.pivot = new Vector3(0f, 0f, pivotZ);
            dp.startAngleFade = angleFrom; dp.endAngleFade = angleTo;
            dp.fadeFactor = 1f;
            decalBorn[i] = Time.time; decalLife[i] = life;
            dp.gameObject.SetActive(true);
            return dp;
        }

        void UpdateDecals()
        {
            if (decalPool == null) return;
            float now = Time.time;
            for (int i = 0; i < DecalPoolSize; i++)
            {
                var dp = decalPool[i];
                if (dp == null || !dp.gameObject.activeSelf) continue;
                float left = decalBorn[i] + decalLife[i] - now;
                if (left > DecalFadeTime) continue;
                if (left <= 0f) { dp.gameObject.SetActive(false); dp.transform.SetParent(transform, false); continue; }
                dp.fadeFactor = left / DecalFadeTime;
            }
        }

        // URP keeps one chunk of arrays per decal material and destroys it the frame its last decal goes (its
        // DecalEntityManager.Update), so a material's next mark rebuilt it in a fight frame (GK-26: ~2.2 KB on the first
        // wound or gouge of each of the 7 materials, then 18 KB as the chunks grew). One hidden anchor projector per
        // material stays on for the scene's life, so no chunk is ever destroyed; then, one material a frame, the whole
        // pool is placed (hidden, gone a frame later) so each chunk's capacity is grown to the pool's size at load.
        DecalProjector[] decalAnchors;
        int decalWarm = -1;

        Material DecalMat(int k) { return k < woundMats.Length ? woundMats[k] : gougeMats[k - woundMats.Length]; }

        void WarmDecals()
        {
            if (decalPool == null) BuildDecalPool();
            int mats = woundMats.Length + gougeMats.Length;
            decalAnchors = new DecalProjector[mats];
            for (int k = 0; k < mats; k++)
            {
                var mat = DecalMat(k);
                if (mat == null) continue;
                var dp = NewDecal(DecalPoolSize + k);
                dp.gameObject.name = "DecalAnchor" + k;
                dp.transform.position = new Vector3(0f, -5000f, 0f);   // past every camera's draw distance
                dp.material = mat;
                dp.size = Vector3.one * 0.01f;
                dp.gameObject.SetActive(true);
                decalAnchors[k] = dp;
            }
            decalWarm = 0;
        }

        void WarmDecalStep()
        {
            if (decalWarm < 0) return;
            int mats = woundMats.Length + gougeMats.Length;
            if (decalWarm >= mats) { decalWarm = -1; nextDecal = 0; return; }
            var mat = DecalMat(decalWarm++);
            if (mat == null) return;
            // the previous material's batch was switched off by UpdateDecals just before (life 0.01 s)
            for (int i = 0; i < DecalPoolSize; i++)
                PlaceDecal(transform, new Vector3(0f, -5000f, 0f), Quaternion.identity, mat, Vector3.one * 0.01f, 0f, 1f, 0.01f);
        }

        /// <summary>The shield keeps the marks of the blows it took: a gouge where each edge bit into it, along the cut.</summary>
        void Gouge(Transform shield, Vector3 point, Vector3 cut, bool heavy)
        {
            var plate = shield.GetComponent<BoxCollider>();
            if (plate == null) return;
            if (decalPool == null) BuildDecalPool();
            var mat = gougeMats[Random.Range(0, gougeMats.Length)];
            if (mat == null) return;
            // the face's normal (the plate's thinnest axis, pointing out of the side the blow came from)
            Vector3 sz = plate.size;
            int thin = sz.x <= sz.y && sz.x <= sz.z ? 0 : sz.y <= sz.z ? 1 : 2;
            Vector3 nl = Vector3.zero; nl[thin] = 1f;
            Vector3 n = shield.TransformDirection(nl).normalized;
            Vector3 c = shield.TransformPoint(plate.center);
            if (Vector3.Dot(point - c, n) < 0f) n = -n;
            Vector3 onFace = point - n * Vector3.Dot(point - c, n);   // projected onto the plate's middle plane
            Vector3 along = Vector3.ProjectOnPlane(cut, n);
            if (along.sqrMagnitude < 1e-4f) along = Vector3.ProjectOnPlane(Vector3.up, n);
            float len = (heavy ? 0.16f : 0.11f) * Random.Range(0.85f, 1.15f);
            PlaceDecal(shield, onFace, Quaternion.LookRotation(-n, Vector3.Cross(-n, along.normalized)), mat,
                       new Vector3(len, len * 0.25f, 0.035f), 60f, 88f, GougeLife);   // ±1.75 cm: the plank, not the arm behind it
        }

        // the struck combatant's hurtbox colliders, gathered once per body (no per-hit GetComponentsInChildren)
        readonly Dictionary<Combatant, Collider[]> hurtColliders = new Dictionary<Combatant, Collider[]>();

        Collider[] HurtCollidersOf(Combatant target)
        {
            Collider[] cols;
            if (hurtColliders.TryGetValue(target, out cols) && cols != null) return cols;
            var boxes = target.GetComponentsInChildren<Hurtbox>(true);
            var list = new List<Collider>(boxes.Length);
            for (int i = 0; i < boxes.Length; i++)
            {
                var col = boxes[i].GetComponent<Collider>();
                if (col != null) list.Add(col);
            }
            cols = list.ToArray();
            // a body gathered before its hurtboxes exist (hooked at spawn) is asked again at its first wound
            if (cols.Length > 0) hurtColliders[target] = cols;
            return cols;
        }

        /// <summary>
        /// A blood mark on the struck body part, laid on its real surface along the cut and riding with that bone.
        /// The bone is the hurtbox collider nearest the contact; the projector sits just outside that collider and
        /// projects inward through a box deep enough to reach the cloth, mail or skin over it, so the mark follows
        /// the folds of what is really there. No mesh is baked: the cost per hit is a few ClosestPoint calls.
        /// </summary>
        public void Wound(Combatant target, Vector3 point, Vector3 cut, float scale)
        {
            if (blood == null || target == null) return;
            if (decalPool == null) BuildDecalPool();
            var cols = HurtCollidersOf(target);
            Collider best = null; Vector3 bestP = point; float bestD = float.MaxValue;
            for (int i = 0; i < cols.Length; i++)
            {
                var col = cols[i];
                if (col == null || !col.enabled) continue;
                Vector3 cp = col.ClosestPoint(point);
                float d = (cp - point).sqrMagnitude;
                if (d < bestD) { bestD = d; best = col; bestP = cp; }
            }
            if (best == null || bestD > 0.09f) { if (Trace) Debug.LogWarning("[GKFX] wound: no hurtbox within 30 cm of the cut on " + target.name); return; }
            // the outward normal at the contact: square off the collider's core line (a limb's capsule axis) through
            // the contact. A blade that has sunk into the capsule gives a contact inside it, and the old fallback (back
            // along the cut) pointed the projector along the body, so the angle fade dropped the whole mark (D22).
            Vector3 n0 = point - CoreNear(best, point);
            if (n0.sqrMagnitude < 1e-6f) n0 = bestP - best.bounds.center;
            if (n0.sqrMagnitude < 1e-6f) n0 = -target.transform.forward;
            n0.Normalize();
            // the surface under the cut, from just outside it along that normal
            bestP = best.ClosestPoint(point + n0 * 0.5f);
            Vector3 along = Vector3.ProjectOnPlane(cut, n0);
            if (along.sqrMagnitude < 1e-4f) along = Vector3.ProjectOnPlane(Vector3.up, n0);
            along.Normalize();
            var mat = woundMats[Random.Range(0, woundMats.Length)];
            if (mat == null) { if (Trace) Debug.LogWarning("[GKFX] wound: no decal material in Resources/FX"); return; }
            Vector3 fwd = -n0;
            Vector3 up = Vector3.Cross(fwd, along);
            if (Vector3.Dot(up, Vector3.up) < 0f) { along = -along; up = -up; }    // the runs trickle downward
            // smaller on the head: a cut there is a line, not a sheet over the face
            Transform head = target.Anim != null && target.Anim.isHuman ? target.Anim.GetBoneTransform(HumanBodyBones.Head) : null;
            float headK = head != null && Vector3.Distance(head.position, point) < 0.18f ? 0.55f : 1f;
            // gore Reduced (0.4) keeps a smaller, shorter cut (GameSettings: "less blood"); only the spray was scaled before
            float goreK = Gore < 1f ? 0.65f : 1f;
            float len = Random.Range(0.16f, 0.24f) * scale * headK * goreK;
            // The box starts 5 cm outside the hurtbox (coat, mail and skin over it are inside its reach) and ends short
            // of the limb's core, so the far side of the limb is never reached. URP's decal graph has no angle fade
            // here (no decal layers), so the depth is the only thing keeping the far side clean. Before, a centred box
            // (pivot 0) spanned 15 cm in front of the body and painted blades, hands and shields passing the wound.
            const float standoff = 0.05f;
            float reach = Mathf.Clamp((bestP - CoreNear(best, bestP)).magnitude * 0.85f, 0.03f, 0.12f);
            float depth = standoff + reach;
            PlaceDecal(best.transform, bestP + n0 * standoff, Quaternion.LookRotation(fwd, up), mat,
                       new Vector3(len, len * 0.75f, depth), 50f, 85f, WoundLife, depth * 0.5f);
            if (Trace) Debug.LogWarning("[GKFX] wound on " + target.name + " at " + best.name);
        }

        /// <summary>The nearest point on a collider's core: a capsule's axis segment, else its centre.</summary>
        static Vector3 CoreNear(Collider c, Vector3 p)
        {
            var cap = c as CapsuleCollider;
            if (cap == null) return c.bounds.center;
            var t = cap.transform;
            Vector3 ax = cap.direction == 0 ? Vector3.right : cap.direction == 1 ? Vector3.up : Vector3.forward;
            Vector3 sc = t.lossyScale;
            float axisScale = Mathf.Abs(cap.direction == 0 ? sc.x : cap.direction == 1 ? sc.y : sc.z);
            float radScale = cap.direction == 0 ? Mathf.Max(Mathf.Abs(sc.y), Mathf.Abs(sc.z)) : cap.direction == 1 ? Mathf.Max(Mathf.Abs(sc.x), Mathf.Abs(sc.z)) : Mathf.Max(Mathf.Abs(sc.x), Mathf.Abs(sc.y));
            float halfLine = Mathf.Max(0f, cap.height * axisScale * 0.5f - cap.radius * radScale);
            Vector3 c0 = t.TransformPoint(cap.center), dir = t.TransformDirection(ax).normalized;
            float k = Mathf.Clamp(Vector3.Dot(p - c0, dir), -halfLine, halfLine);
            return c0 + dir * k;
        }

        // a point in a transform's space without its scale: with ScalingMode.Shape the particles' local positions are
        // not scaled by the bone (a sword or bone scaled on import put the drops 10 cm off the steel)
        static Vector3 Unscaled(Transform t, Vector3 world) { return Quaternion.Inverse(t.rotation) * (world - t.position); }

        // ---- blood on the blade (QUALITY 3b): a film on the steel that runs with gravity, gathers into drops, dries from
        // its edges and is wiped on sheathe (BladeBloodFilm). Drops that let go are round lit beads that fall, land and
        // mark the ground (the same splats as the spray).
        readonly Dictionary<WeaponHitbox, BladeBloodFilm> films = new Dictionary<WeaponHitbox, BladeBloodFilm>();
        readonly HashSet<WeaponHitbox> noFilm = new HashSet<WeaponHitbox>();
        readonly Queue<Combatant> filmPending = new Queue<Combatant>(32);
        Material filmMat;
        ParticleSystem fallingDrops;
        static Mesh beadMesh;

        void BloodOnBlade(Combatant attacker, float amount, Vector3 contact, Vector3 towardTarget)
        {
            var w = attacker.weapon;
            if (w == null) return;
            var f = FilmFor(attacker, w);
            if (f == null) return;
            // a new pattern on every cut: the seed comes from the cut itself, not a fixed one
            f.Cut(contact, towardTarget, 0.55f * amount, Random.Range(int.MinValue, int.MaxValue));
        }

        /// <summary>The film of this weapon, built once (normally at its owner's spawn, from Update's queue). A weapon with
        /// no blade to hold a film (a mace, a missing steel renderer) is remembered, so it is not searched again per hit.</summary>
        BladeBloodFilm FilmFor(Combatant owner, WeaponHitbox w)
        {
            BladeBloodFilm f;
            if (films.TryGetValue(w, out f) && f != null) return f;
            if (noFilm.Contains(w) || w.bladeBase == null || w.bladeTip == null) return null;
            if (filmMat == null)
            {
                var fs = Feel.FeelSettings.Active;
                filmMat = fs.bladeBloodMaterial;
                if (filmMat == null) { var sh = Shader.Find("GK/Feel/BladeBlood"); if (sh != null) filmMat = new Material(sh); }
            }
            f = BladeBloodFilm.Create(w, filmMat, EmitFallingDrop);
            if (f == null)
            {
                if (filmMat != null) noFilm.Add(w);   // no steel to hold it; without a material it may still come later
                if (Trace) Debug.LogWarning("[GKFX] blade film: no steel renderer or material for " + w.name);
                return null;
            }
            films[w] = f;
            var holster = owner.GetComponent<WeaponHolster>();
            if (holster != null) holster.Sheathed += () => { if (f != null) f.Wipe(); };
            return f;
        }

        void BuildFallingDrops()
        {
            var go = new GameObject("FallingDrops");
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false; main.loop = false; main.duration = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSpeed = 0f; main.gravityModifier = 1f; main.maxParticles = 128;
            main.startLifetime = 2.5f;
            var em = ps.emission; em.enabled = false;
            var sh = ps.shape; sh.enabled = false;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = blood.GetComponent<ParticleSystemRenderer>().sharedMaterial;
            if (beadMesh == null) beadMesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            r.renderMode = ParticleSystemRenderMode.Mesh;       // round beads, not cards
            r.mesh = beadMesh;
            r.shadowCastingMode = ShadowCastingMode.Off;
            Collide(ps, 0f, 0f, 1f, true);
            go.AddComponent<BloodDrops>().fx = this;
            fallingDrops = ps;
        }

        /// <summary>A round drop of blood that falls, lands and marks the ground (the blade film lets them go).</summary>
        public void EmitFallingDrop(Vector3 at, Vector3 velocity, float radius)
        {
            if (fallingDrops == null || Gore <= 0f) return;
            var ep = new ParticleSystem.EmitParams
            {
                position = at, velocity = velocity, startSize = radius * 2f, applyShapeToPosition = false,
                startColor = new Color(0.3f, 0.008f, 0.006f)
            };
            fallingDrops.Emit(ep, 1);
        }

        void UpdateBlades()
        {
            if (films.Count == 0) return;
            float dt = Time.deltaTime;
            foreach (var kv in films) if (kv.Value != null) kv.Value.Update(dt);
        }
    }
}
