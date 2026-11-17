using System.Collections.Generic;
using System.Text;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.ProBuilder;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Level_Didgori_Arena (M1), the greybox of the approved layout (Docs/World/Didgori_Arena_Brief.md, Director's
    /// decision #6): a natural bowl on the Didgori saddle, 3 enemy lanes from the SW / W / S high ground, the player on
    /// the NE lip facing 220°, dawn light. Origin = bowl centre, +Z north, bearings clockwise from north, 1 unit = 1 m.
    /// Real terrain from GKDem re-centred on the bowl; grey ProBuilder blocks for the boundary, lanes, crowds and the
    /// E shoulder; spawn markers; the game systems and the player; NavMesh; Build &amp; QA's six QAView cameras;
    /// Audio's AMB_ markers; an EditorOnly metrics zoo. Didgori.unity and its assets are not touched.
    /// </summary>
    public static class GKArenaGreybox
    {
        public const string ScenePath = GKPaths.Scenes + "/Level_Didgori_Arena.unity";
        const string Dir = GKPaths.Generated + "/Environment/Greybox";

        static readonly Vector2 BowlInDem = new Vector2(125f, 80f);   // GKDem frame, metres E / N (didgori_map.py ARENA)
        const float RCore = 15f, RPlay = 45f, RBoundary = 60f;

        struct Lane { public string id; public float bearing, spawn, width; }
        static readonly Lane[] Lanes =
        {
            new Lane { id = "L1", bearing = 235f, spawn = 88f, width = 10f },
            new Lane { id = "L2", bearing = 275f, spawn = 82f, width = 8f },
            new Lane { id = "L3", bearing = 185f, spawn = 80f, width = 8f },
        };
        // the hand-off: on the N lip facing 190° (the Critics' review): the dawn sun at 83° then lights the enemies from the
        // side instead of from behind the camera, and the hero's 8 m shadow falls off to the right, not mid-frame.
        // The first approved layout (NE lip, facing 220°) stays as a comparison camera.
        const float PlayerBearing = 10f, PlayerRadius = 40f, PlayerFacing = 190f;
        // the host line stands 10 m behind P (Bakur has already walked out of it at the hand-off); Saba holds the 6th place
        // to the left in its front rank, ~11.7 m from P at bearing ~41°, behind his left shoulder (Story bible §9 change 4)
        const float LineBehindP = 10f, FileSpacing = 1.2f; const int SabaFile = 5;
        // assault 3 (C17, Director 2026-10-30): the pocket in front of Black Banner Rise (255°, 118 m) opens for wave 3
        const float RiseFrom = 244f, RiseTo = 268f, RiseOuter = 140f; const int RiseOpensAtWave = 2;
        static readonly Vector3 RiseTop = new Vector3(-114f, 0f, -30.5f);   // Pol(255, 118)
        const float SunAzimuth = 83f, SunElevation = 12f;   // 19 Aug (Gregorian) 06:15 LMT, about 65 min after sunrise

        struct View { public string name; public float x, z, h, look, pitch, fov; }
        static readonly View[] Views =
        {
            new View { name = "QAView_Q1_HandOff",      x = 7.6f, z = 43.3f, h = 1.9f, look = 190f, pitch = 5f, fov = 55f },   // shoulder height, 4 m behind P (the host line 6 m further back)
            new View { name = "QAView_Q2_DawnSun",      x = 0f,   z = 0f,   h = 1.7f, look = 83f,  pitch = 2f,  fov = 55f },
            new View { name = "QAView_Q3_Layout",       x = 13f,  z = 73.9f, h = 35f, look = 190f, pitch = float.NaN, fov = 55f },
            // not a QA view: the first approved hand-off (NE lip, facing 220°) under the same sun, for the comparison still
            new View { name = "Compare_A_HandOff220",   x = 28f,  z = 34f,  h = 1.7f, look = 220f, pitch = 4f,  fov = 55f },
            new View { name = "QAView_Q4_EnemyCrest",   x = -57f, z = -40f, h = 1.7f, look = 55f,  pitch = 6f,  fov = 55f },
            new View { name = "QAView_Q5_SideLight",    x = 52f,  z = -19f, h = 1.7f, look = 270f, pitch = 3f,  fov = 55f },
            new View { name = "QAView_Q6_GroundTiling", x = 0f,   z = -4f,  h = 1.7f, look = 180f, pitch = 22f, fov = 40f },
            // turned toward the host: 4 m in front of P, over the hero to the line 10 m behind him and Saba_Mark (41° from P)
            // the villagers' haystacks (Critics AR-6), from 21 m on the host side
            new View { name = "QAView_Q8_MeadowStacks", x = 12.06f, z = 56.73f, h = 1.7f, look = 59f, pitch = 3f, fov = 45f },
            new View { name = "QAView_Q7_TurnToHost",   x = 6.2f, z = 35.5f, h = 2.0f, look = 10f,  pitch = 4f,  fov = 55f },
        };

        [GKCommand("world_greybox", "world_greybox [hdri=<polyhaven id>]: Level_Didgori_Arena greybox: DEM terrain on the bowl, blocks, spawns, systems, NavMesh, QA views (lease); hdri= builds once with another sky (style-frame comparisons; rebuild without it after)")]
        static void Command(string a)
        {
            string hdri = null;
            foreach (var part in (a ?? "").Split(' '))
            {
                if (part.StartsWith("hdri=")) hdri = part.Substring(5);
                // throw away unsaved edits to the open arena (a capture's leftovers) instead of refusing
                if (part == "discard" && !EditorApplication.isPlayingOrWillChangePlaymode && SceneManager.GetActiveScene().path == ScenePath)
                    EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }
            hdriOverride = string.IsNullOrEmpty(hdri) ? null : hdri;
            try { Build(); } finally { hdriOverride = null; }
        }

        static string hdriOverride;

        [GKCommand("world_shots", "world_shots [aids]: stills of the QA views of the open arena scene into GKBridge/, as in Play (editor aids hidden); 'aids' keeps them")]
        static void Shots(string a) { KeepClean(() => CaptureViews(a != null && a.Trim() == "aids")); }

        /// <summary>Runs a capture and, if it left a scene that was clean dirty (temporary cameras, figures, a sampled pose),
        /// reloads it from disk, so a later world_greybox or another session never meets unsaved capture leftovers.</summary>
        static void KeepClean(System.Action capture)
        {
            var scene = SceneManager.GetActiveScene();
            bool wasDirty = scene.isDirty;
            try { capture(); }
            finally
            {
                if (!wasDirty && scene.isDirty && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    EditorSceneManager.OpenScene(scene.path, OpenSceneMode.Single);
                    GKBridge.Log("world: the capture's leftovers discarded (scene reloaded clean)");
                }
            }
        }

        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("world_greybox: refused in Play mode"); return; }
            if (SceneManager.GetActiveScene().isDirty)
            {
                GKBridge.Log("world_greybox: refused, the open scene '" + SceneManager.GetActiveScene().path + "' has unsaved changes (save or discard it first)");
                return;
            }
            // the M1 hero, decided in one place (Combat's GKHeroSwap.M1PlayerPath; DECISIONS): the same man as in the intro
            var playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKHeroSwap.M1PlayerPath);
            var enemyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKGameBuilder.EnemyPrefabPath);
            if (playerPrefab == null || enemyPrefab == null) { GKBridge.Log("world_greybox: player or enemy prefab missing"); return; }
            GKBridge.Log("world_greybox: player = " + AssetDatabase.GetAssetPath(playerPrefab));

            GKPaths.Ensure(Dir);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ---- terrain, sky, sun, wind, post: the shared builder on this level's own assets
            GKEnvironment.Current = new GKEnvironment.Site
            {
                TerrainAsset = "Arena_Didgori.asset", FarAsset = "Arena_DidgoriFar.asset",
                SkyMaterial = "Sky_Arena_Didgori.mat", PostAsset = "PP_Didgori_Arena.asset",
                TerrainName = "Arena Terrain",
                DemOffset = BowlInDem,
                // the floor's E edge is the foot of the E shoulder (14° on the first build): level to r 42, blend out by
                // r 85, so the fight floor stays under 3° and the shoulders and lanes keep their real height
                FlattenInner = 42f, FlattenOuter = 85f,
                FloorRelief = 0.25f,
                Dressing = false, Snow = false,           // greybox: no art; mid-August: no snow
                SunAzimuth = SunAzimuth, SunElevation = SunElevation,
                // morning, not dusk (Critics AR-3, 2026-10-31, L* measured): lit straw at L* ~50-53. Sun 8.5 and exposure 0.4 since the
                // muted grade (saturation -8, the duller straw) took ~5 L* off the 6.5 / 0.3 of pass 3. 4000 K reads
                // crisp and rising; the sky fill brighter (earlier: 3400 K, 3.4, ambient 1.3-1.4, lit straw only L* 32-42)
                // recalibrated (the Team Lead, 2026-10-31, world_light, L* measured): the sun 8.5 over-lit the dark forest to gold
                // (the rim 'dunes'). Sun 3.5 with the exposure up to 1.3 and the sky fill (ambient 2.4) keeps lit straw p90 L* 53-56;
                // the sky exposure down to 0.3 so the Q2 disk survives the higher exposure (sky round the sun L* 74)
                // dawn contrast (the Lead and Critics, 2026-10-31: ambient 2.4 flattened the frame): the sky fill down to 1.0, the
                // exposure up to hold the lit straw; sun-facing and turned-away slopes split, the backlit Q2 ground darker
                // the sky exposure down by the same stops, so the west sky stays L* ~58 (0.3 at exposure 2.0: L* 71); 4700 K: with
                // less fill the warm sun alone turned the straw orange (h 73, the references 84-99)
                // with the sky fill (below) a stronger sun at a lower exposure keeps the lit-to-shade ratio: the straw where it was,
                // the fill-lit shade relatively darker (world_light, 2026-10-31)
                // physical albedo (the Lead, 2026-10-31; Docs/World/ALBEDO.md): meadow 0.22 linear. Re-anchored by measurement, not by
                // the factor (that left the meadow at p90 66 and the sky at 75): lit straw p90 L* 52-56, the west sky L* 51-54
                SunTemperature = 4700f, SunIntensity = 4.5f, SkyExposure = 0.38f, AmbientIntensity = 0.25f, PostExposure = 0.35f,
                PhysicalAlbedo = true,
                GrassClumping = 0.35f,                    // the Lead's review: clumps with bare gaps read planted, not a hay meadow
                // haze test 2026-10-30 (world_light): a warm dawn haze from 150 m, the far ranges paler, the depth stepped
                // pass 2: darker than the first test (0.80/0.73/0.74 out-shone the cool west sky at sky exposure 0.45: white ranges)
                // the Director's sky A (2026-10-31): the over-dark violet west lifted through ambient (1.4) and a lighter fog tint, sky
                // exposure only to 0.47 (from 0.5 up the sky round the sun turns white and the sun disk vanishes)
                // with the graded sky lifting the west (GK/SkyPanoramicGraded) the fog no longer has to: darker and slower, so the
                // far ranges keep their colour instead of fading to pale lavender
                // warm grey dawn haze: the bluish fog turned the far NE plain into a strip of water (the Team Lead, 2026-10-31)
                // the far range read white at exposure 2+ (the Lead, Critics: it should take the sky's blue-grey, a little darker than
                // the sky above it): a neutral grey haze, cool but not blue (blue read as water on the NE plain)
                // the post exposure (2.2 stops, x4.6) multiplies the fog too: 0.40 clipped white, 0.135 (sRGB, ~0.017 linear) left the
                // far ranges near-black with no aerial layering
                FogStart = 150f, FogEnd = 14000f, FogColor = new Color(0.41f, 0.41f, 0.43f),   // with the exposure 1.85 -> 0.35 and the meadow x1.88: the far haze holds
                // the Lead's review: the meadow thins out over ~25 m into the trampled bowl, with dry stubble, not a band of clumps
                DirtInner = 7f, DirtOuter = 32f, Stubble = true,
                DirtTint = new Color(0.52f, 0.42f, 0.35f),  // now baked (URP ignored it): the reddish-brown mountain-meadow earth, not bleached grey
                // lighting pass 2 (world_light, Q2/Q1): with the HDRI's sun glow now in the east (the sky-rotation fix) 0.65 burnt the
                // east sky white and hid any disk; 0.45 keeps the disk and the clouds, the west sky stays a cool fill
                SunDisk = 0.5f, SunHalo = 3f,
                // the Lead (2026-10-31): the west a deep navy, nearly a storm. Gain 3.6 and a near-neutral tint: anti-sun sky L* 56-59,
                // C* 15-16, hue 280-290 (was L* 49-50, C* 23-26, hue 278: navy); 1.08/0.98/1.0 read lavender (hue 320)
                SkyAwayGain = 3.6f, SkyGainPower = 1.5f,
                SkyAwayTint = new Color(0.9f, 1f, 1.04f),   // Critics R4: the anti-sun sky a pale cool blue 50 min after sunrise, not lavender     // Critics AR-3: the anti-sun sky lifted, the sun side as photographed
                FarEdgeFade = 5000f,                      // the Lead's review of Q3: no straight backdrop edge on the farthest ridge
                // the Lead's review of play4: the ranges are meadow and forest, not dunes; a fuller meadow; worn tracks across the floor
                // the grass budget agreed with Build & QA (2026-10-31: <= 1.5 ms GPU, <= 400 batches, <= 2 M triangles): drawn to
                // 80 m (was 110), tall clumps 16 per cell (was 24), straw cover <= 10, short green <= 8
                MacroColour = true, GrassDensity = 16f, DetailDistance = 80f,
                // muted August straw (the Team Lead: lime-yellow read like savanna): a quieter grade
                PostSaturation = -8f, PostContrast = 10f,
                // the Lead, Critics R2: a dawn valley in shade keeps sky fill (Q8 p10 L* ~2). A post lift (+0.05) only greyed every
                // view's darks; the fill is light from the open sky on the turned-away side
                // Q8 shaded valley p10 L* 2 -> ~5-9, median ~20 at fill 0.9-1.2 from 4 deg; it lifts every shaded face, Q1 spread 32 -> ~27
                // the Lead's (a) (2026-10-31): the balanced row at the meadow anchor (the Critics' Q3 rectangle 13.3, Q8 valley 13.0);
                // the Q3 ruler is being re-cut on a sun-shadow mask (c)
                ShadowLift = 0f, SkyFill = 0.7f,
                // GK/Terrain Lit (Sky Occlusion) with the baked sky visibility (the Lead's decision): built, off until Feel's review and
                // Build & QA's cost check. Alone it barely moves the bars (inner map mean 0.944: the hills are open at this scale)
                SkyOcclusion = false,
                HayMeadow = true,                         // Critics AR-1/2: the brief's upper-montane hay meadow, straw-gold, green in hollows
                GreyTrees = true,                         // crest silhouettes on the forest layer until Art's trees (the Team Lead, 2026-10-31)
                TrampledPaths = new[]
                {
                    new Vector3(235f, 95f, 4f), new Vector3(275f, 90f, 4f), new Vector3(185f, 90f, 4f),   // to the lanes L1, L2, L3
                    new Vector3(10f, 52f, 5f),                                                             // to the host line
                    new Vector3(255f, 120f, 3.5f),                                                         // up to Black Banner Rise
                    new Vector3(116f, 62f, 3f),                                                            // to the baggage and the cart
                },
                WindFrom = 70f,                           // from behind our line toward the coalition (P03 assault 2: smoke needs the wind toward them)
                Hdri = hdriOverride ?? "qwantani_dusk_2_puresky",         // CC0, Greg Zaal & Jarod Guest: clear, sun measured at 12.1° (1k file)
            };
            try { GKEnvironment.Build(); }
            finally { GKEnvironment.Current = new GKEnvironment.Site(); GKDem.Offset = Vector2.zero; }

            float floorY = H(0f, 0f);
            var root = new GameObject("Greybox").transform;
            BuildBoundary(root, enemyPrefab);
            BuildLanes(root);
            BuildCrowds(root);
            BuildShoulder(root);
            BuildBaggage(root);
            BuildMeadowStacks(root);
            BuildBanners(root);
            BuildGuides(root);
            BuildMetricsZoo();

            // ---- navigation (built before the characters exist)
            var navGo = new GameObject("NavMesh");
            var surface = navGo.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Volume;
            surface.center = new Vector3(0f, floorY, 0f);
            surface.size = new Vector3(300f, 120f, 300f);   // reaches Black Banner Rise (118 m) for assault 3
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = (1 << GK.LayerEnvironment) | (1 << GK.LayerTerrain);
            surface.BuildNavMesh();
            if (surface.navMeshData != null)
            {
                string np = GKPaths.Generated + "/Environment/Arena_DidgoriNavMesh.asset";
                if (AssetDatabase.LoadAssetAtPath<Object>(np) != null) AssetDatabase.DeleteAsset(np);
                AssetDatabase.CreateAsset(surface.navMeshData, np);
            }

            // ---- the knight at P, the camera rig, the game systems (GKGameBuilder's own, unchanged)
            Vector3 spawn = Pol(PlayerBearing, PlayerRadius);
            spawn.y = H(spawn.x, spawn.z) + 0.05f;
            var facing = Quaternion.Euler(0f, PlayerFacing, 0f);
            var player = (GameObject)PrefabUtility.InstantiatePrefab(playerPrefab);
            // no clip sampled into the bones (it saved ~400 pose overrides on the instance; Combat's heroreset, 2026-10-30):
            // the hero stands in his prefab pose in edit mode and the Animator poses him in Play
            player.transform.SetPositionAndRotation(spawn, facing);
            var cam = GKGameBuilder.BuildCameraRig(player, spawn);
            cam.transform.position = spawn - facing * Vector3.forward * 4f + Vector3.up * 2f;
            cam.transform.LookAt(spawn + facing * Vector3.forward * 6f + Vector3.up * 1.4f);
            var waves = GKGameBuilder.BuildGameSystems(player, enemyPrefab, new Vector3(0f, floorY, 0f));
            WireRiseGate(waves);
            BuildLine(root, spawn, facing);
            // the scale figure the Critics check against (QUALITY: 1.70 m; Bakur is 1.78 m): 4 m ahead of P, EditorOnly, no collider
            Vector3 fp = spawn + facing * Vector3.forward * 4f;
            var figure = ShapeGenerator.GenerateCube(PivotLocation.Center, new Vector3(0.45f, 1.70f, 0.28f));
            Setup(figure, root, "ScaleFigure_1.70m", Mat("M_Grey_Figure", new Color(0.86f, 0.86f, 0.86f)));
            figure.transform.SetPositionAndRotation(new Vector3(fp.x, H(fp.x, fp.z) + 0.85f, fp.z), facing);
            figure.gameObject.tag = "EditorOnly";

            BuildSpawnMarkers();
            BuildQaViews();
            BuildAudioMarkers();
            BuildStoryMarkers(spawn);
            WireLineAdvance();
            MarkEditorAids();

            EditorSceneManager.SaveScene(scene, ScenePath);
            GKPreview.BakeEnvironmentLighting();   // ambient probe + reflections only (no lightmaps)
            EditorSceneManager.SaveScene(scene, ScenePath);
            Report(spawn);
            GKBridge.Log("world_greybox: saved " + ScenePath + " (not in Build Settings: Engine & Tools adds it, GK-4)");
        }

        // =================================================================== layout

        static Vector3 Pol(float bearing, float r)
        {
            float b = bearing * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(b) * r, 0f, Mathf.Cos(b) * r);
        }

        static float H(float x, float z) { return GKEnvironment.SampleHeight(new Vector3(x, 0f, z)); }

        static bool InLane(float bearing, float r, float margin)
        {
            foreach (var l in Lanes)
            {
                float half = Mathf.Asin(Mathf.Clamp01((l.width * 0.5f + margin) / r)) * Mathf.Rad2Deg;
                if (Mathf.Abs(Mathf.DeltaAngle(bearing, l.bearing)) < half) return true;
            }
            return false;
        }

        /// <summary>0 = Georgian host (NW..E), 1 = E shoulder, 2 = coalition side.</summary>
        static int Side(float bearing)
        {
            float b = Mathf.Repeat(bearing, 360f);
            if (b >= 300f || b < 90f) return 0;
            return b < 160f ? 1 : 2;
        }

        static void BuildBoundary(Transform root, GameObject enemyPrefab)
        {
            // the player's edge at r 60: the host's shield line, rocks and carts, the melee. On the PlayerBlocker layer
            // (contract C14: collides only with Character; left out of the NavMesh bake, whose mask is Default | Terrain).
            var parent = Group(root, "Boundary r60");
            int blocker = LayerMask.NameToLayer("PlayerBlocker");
            if (blocker < 0) GKBridge.Log("world_greybox: no PlayerBlocker layer (C14): the boundary stays on Default");
            var mats = new[] { Mat("M_Grey_Host", new Color(0.42f, 0.5f, 0.62f)), Mat("M_Grey_Rock", new Color(0.5f, 0.48f, 0.44f)), Mat("M_Grey_Foe", new Color(0.58f, 0.4f, 0.36f)) };
            var riseGroup = Group(parent, "Opens for assault 3 (Black Banner Rise)");
            const float step = 5f;
            for (float b = 0f; b < 360f; b += step)
            {
                float mid = b + step * 0.5f;
                if (InLane(mid, RBoundary, 1f)) continue;
                Vector3 p = Pol(mid, RBoundary);
                float len = 2f * Mathf.PI * RBoundary * step / 360f + 0.3f;
                // the edge in front of Black Banner Rise has its own group: assault 3 opens it when the line advances (P03)
                bool rise = mid > RiseFrom && mid < RiseTo;
                var edge = Block(rise ? riseGroup : parent, "Edge_" + Mathf.RoundToInt(mid).ToString("000"), p, mid, new Vector3(len, 1.9f, 0.8f), 0.65f, mats[Side(mid)], true);
                if (blocker >= 0) edge.layer = blocker;
            }
            // the pocket's own walls (radial at RiseFrom / RiseTo out to RiseOuter, an arc there): solid and in the NavMesh, so
            // once the gate opens the player can climb the rise but not leave it, and W3 comes up through the gate, uphill
            var pocket = Group(parent, "Pocket walls (Black Banner Rise)");
            foreach (float wb in new[] { RiseFrom, RiseTo })
                for (float r = RBoundary + 3f; r < RiseOuter; r += 5f)
                    Block(pocket, "Radial_" + Mathf.RoundToInt(wb) + "_" + Mathf.RoundToInt(r), Pol(wb, r + 2.5f), wb + 90f, new Vector3(5.3f, 1.9f, 0.8f), 0.65f, mats[2], true);
            for (float b = RiseFrom; b < RiseTo; b += 2f)
                Block(pocket, "Arc_" + Mathf.RoundToInt(b + 1f), Pol(b + 1f, RiseOuter), b + 1f, new Vector3(2f * Mathf.PI * RiseOuter * 2f / 360f + 0.3f, 1.9f, 0.8f), 0.65f, mats[2], true);
            // the lane mouths: an invisible blocker for the player only. Enemies are on Character too, so it goes in only
            // once their CharacterController ignores PlayerBlocker (Combat); otherwise the waves would stop here.
            var cc = enemyPrefab != null ? enemyPrefab.GetComponent<CharacterController>() : null;
            bool enemiesPass = blocker >= 0 && cc != null && (cc.excludeLayers.value & (1 << blocker)) != 0;
            if (!enemiesPass)
            {
                GKBridge.Log("world_greybox: lane mouths left open: the enemy's CharacterController does not exclude PlayerBlocker yet (Combat)");
                return;
            }
            foreach (var l in Lanes)
            {
                var go = new GameObject("LaneMouth_" + l.id);
                go.transform.SetParent(parent, false);
                Vector3 p = Pol(l.bearing, RBoundary);
                go.transform.SetPositionAndRotation(new Vector3(p.x, H(p.x, p.z) + 1.5f, p.z), Quaternion.Euler(0f, l.bearing, 0f));
                go.AddComponent<BoxCollider>().size = new Vector3(l.width + 3f, 4f, 0.8f);
                go.layer = blocker;
                go.isStatic = true;
            }
        }

        static void BuildLanes(Transform root)
        {
            var parent = Group(root, "Lanes");
            var rock = Mat("M_Grey_Rock", new Color(0.5f, 0.48f, 0.44f));
            foreach (var l in Lanes)
            {
                var lane = Group(parent, "Lane_" + l.id);
                // low rock lines along both edges, so the lane reads from the floor and the AI stays in it
                for (float r = RBoundary + 4f; r < l.spawn - 3f; r += 8f)
                    for (int s = -1; s <= 1; s += 2)
                    {
                        Vector3 c = Pol(l.bearing, r) + Quaternion.Euler(0f, l.bearing, 0f) * Vector3.right * (s * (l.width * 0.5f + 0.8f));
                        int k = lane.childCount + (int)l.bearing;
                        string id = LaneRocks[Mathf.Abs(k) % LaneRocks.Length];
                        // nb_02 (2.53 m) only to 1.15x: above that its 2k texture drops under ~500 px/m (Asset QA)
                        Rock(lane, "Edge", id, c, l.bearing + (k * 47 % 60) - 30f, id == "namaqualand_boulder_02" ? Mathf.Min(Jitter(k), 1.15f) : Jitter(k), rock, true);
                    }
            }
        }

        static void BuildCrowds(Transform root)
        {
            // stand-ins for the Georgian host (NE) and the coalition melee (S..W): two ranks of 1.8 m slabs at r 66 and 72
            var parent = Group(root, "Crowds");
            var host = Mat("M_Grey_Host", new Color(0.42f, 0.5f, 0.62f));
            var foe = Mat("M_Grey_Foe", new Color(0.58f, 0.4f, 0.36f));
            foreach (float r in new[] { 66f, 72f })
                for (float b = 0f; b < 360f; b += 8f)
                {
                    int side = Side(b);
                    if (side == 1 || InLane(b, r, 3f) || (b > RiseFrom - 4f && b < RiseTo + 4f)) continue;   // the rise pocket stays clear
                    float len = 2f * Mathf.PI * r * 8f / 360f - 1f;
                    Block(parent, (side == 0 ? "Host_" : "Melee_") + Mathf.RoundToInt(b).ToString("000"), Pol(b, r), b, new Vector3(len, 1.8f, 0.6f), 0.9f, side == 0 ? host : foe, false);
                }
        }

        static void BuildShoulder(Transform root)
        {
            // E shoulder (+5 m): the dead horses, an overturned cart and rocks; the fallen standard-bearer on the floor
            var parent = Group(root, "East Shoulder");
            var rock = Mat("M_Grey_Rock", new Color(0.5f, 0.48f, 0.44f));
            var wood = Mat("M_Grey_Wood", new Color(0.55f, 0.47f, 0.36f));
            var flesh = Mat("M_Grey_Flesh", new Color(0.46f, 0.38f, 0.34f));
            // real-size boulders (Asset QA: 0.8-1.3x); the bigger mass is two of them together, not one blown up
            Rock(parent, "Rock_1", "namaqualand_boulder_06", Pol(112f, 72f), 20f, 1.2f, rock, true);
            Rock(parent, "Rock_2", "boulder_01", Pol(134f, 70f), 70f, 1.25f, rock, true);
            Rock(parent, "Rock_2b", "namaqualand_boulder_02", Pol(136.5f, 71.5f), 160f, 1.1f, rock, true);
            Rock(parent, "Rock_3", "namaqualand_boulder_06", Pol(150f, 69f), 5f, 1.0f, rock, true);
            BuildGroundStones(root);
            ClearDetailsUnderRocks();
            // the story props stay grey stand-ins until Art's models (no honest CC0 dead horse or 12th-c. cart): blockers in Play,
            // never drawn there (EditorAid), so a frame never shows grey boxes on the shoulder
            var story = Group(parent, "Story props (stand-ins)");
            Block(story, "Cart_Overturned", Pol(118f, 64f), 30f, new Vector3(3.6f, 1.4f, 1.8f), 0.6f, wood, true);
            Block(story, "Horse_Dead_1", Pol(108f, 63f), 75f, new Vector3(2.3f, 0.8f, 0.9f), 0.35f, flesh, true);
            Block(story, "Horse_Dead_2", Pol(127f, 66f), 140f, new Vector3(2.3f, 0.8f, 0.9f), 0.35f, flesh, true);
            Block(story, "Horse_Dead_3", Pol(99f, 68f), 10f, new Vector3(2.3f, 0.8f, 0.9f), 0.35f, flesh, true);
            // inside the floor: low, no collider (camera and NavMesh stay clean)
            Block(story, "Fallen_StandardBearer", Pol(130f, 41f), 60f, new Vector3(1.8f, 0.35f, 0.6f), 0.15f, flesh, false);
            Block(story, "Fallen_Pole", Pol(131f, 39f), 15f, new Vector3(0.12f, 0.12f, 3.2f), 0.06f, wood, false);
        }

        static void MarkEditorAids()
        {
            // greybox aids and stand-ins render in the editor and in world_shots, never in Play (GK.World EditorAid): the
            // boundary and pocket blocks, crowds, the Georgian line, guide rings, spawn pillars, the scale figure, the zoo.
            // Colliders stay. What stays visible in Play: terrain, grass, the E shoulder props, banners, hay, lane rocks.
            var t = TypeByName("EditorAid");
            if (t == null) { GKBridge.Log("world_greybox: no EditorAid type (GK.World): aids will render in Play"); return; }
            int n = 0;
            foreach (var path in new[] { "Greybox/Boundary r60", "Greybox/Crowds", "Greybox/Georgian Line (P03)", "Greybox/Guides",
                                         "Greybox/ScaleFigure_1.70m", "Spawns", "MetricsZoo (EditorOnly)",
                                         "Greybox/East Shoulder/Story props (stand-ins)", "Greybox/Banners/Ilghazi's Ridge (smoke stand-ins)" })
            {
                var go = GameObject.Find(path);
                if (go == null) { GKBridge.Log("world_greybox: editor aid not found: " + path); continue; }
                go.AddComponent(t); n++;
            }
            GKBridge.Log("world_greybox: " + n + " editor-aid groups hidden in Play");
        }

        static void BuildStoryMarkers(Vector3 p)
        {
            // P03 assault 3 (C17 note): where the line stands for W1-W2 and where it holds for W3. HoldPoint = the highest
            // ground inside the Black Banner Rise pocket (the real ground keeps rising west of the banners, so the line
            // holds the pocket's top and W3 comes up at it from the bowl through the gate). Plain transforms, forward =
            // the way the line faces; Combat's allies and autopilot read them by name.
            var parent = new GameObject("Markers").transform;
            Vector3 best = RiseTop; float hi = float.MinValue;
            for (float b = RiseFrom + 3f; b <= RiseTo - 3f; b += 1f)
                for (float r = 100f; r <= RiseOuter - 6f; r += 2f)
                {
                    Vector3 q = Pol(b, r); float h = H(q.x, q.z);
                    if (h > hi) { hi = h; best = new Vector3(q.x, h, q.z); }
                }
            Vector3 toBowl = new Vector3(-best.x, 0f, -best.z).normalized;
            System.Action<string, Vector3, Quaternion> mark = (n, pos, rot) =>
            {
                var t = new GameObject(n).transform; t.SetParent(parent, false); t.SetPositionAndRotation(pos, rot);
            };
            mark("Line_W1", LineCentre(p, Quaternion.Euler(0f, PlayerFacing, 0f)), Quaternion.Euler(0f, PlayerFacing, 0f));   // the host line's centre
            mark("HoldPoint_BlackBannerRise", best, Quaternion.LookRotation(toBowl));
            mark("Line_W3", best + toBowl * 4f, Quaternion.LookRotation(toBowl));
            GKBridge.Log("world_greybox: hold point " + best.ToString("F1") + " (" + (best.y - H(0f, 0f)).ToString("+0.0;-0.0") + " m over the floor), facing the bowl");
        }

        static void WireLineAdvance()
        {
            // the Georgian line moves to the rise at W3 (GK.World LineAdvance; Team Lead: non-fighting allies for M1). Today the
            // line is greybox stand-ins (hidden in Play by EditorAid; `world_aids show` shows them for World's own test only)
            var line = GameObject.Find("Greybox/Georgian Line (P03)");
            var from = GameObject.Find("Markers/Line_W1"); var to = GameObject.Find("Markers/Line_W3");
            var t = TypeByName("LineAdvance");
            if (line == null || from == null || to == null || t == null) { GKBridge.Log("world_greybox: line advance not wired"); return; }
            var so = new SerializedObject(line.AddComponent(t));
            so.FindProperty("from").objectReferenceValue = from.transform;
            so.FindProperty("to").objectReferenceValue = to.transform;
            so.FindProperty("advanceAtWave").intValue = RiseOpensAtWave;
            so.ApplyModifiedPropertiesWithoutUndo();
            GKBridge.Log("world_greybox: the line advances Line_W1 -> Line_W3 at wave index " + RiseOpensAtWave);
        }

        [GKCommand("world_aids", "world_aids show|hide: in Play, show World's greybox aids and stand-ins (own tests only, never for the Director)")]
        static void Aids(string a)
        {
            if (!EditorApplication.isPlaying) { GKBridge.Log("world_aids: Play mode only"); return; }
            bool on = (a ?? "").Trim() == "show";
            int n = 0;
            var t = TypeByName("EditorAid");
            if (t == null) { GKBridge.Log("world_aids: no EditorAid type"); return; }
            var show = t.GetMethod("Show");
            foreach (var c in Object.FindObjectsByType(t, FindObjectsSortMode.None)) { show.Invoke(c, new object[] { on }); n++; }
            GKBridge.Log("world_aids: " + (on ? "showing " : "hiding ") + n + " groups");
        }

        static void WireRiseGate(WaveManager waves)
        {
            // GK.World's BoundaryGate (runtime) on the segment in front of the rise; added by type name so this builder
            // compiles whether or not GK.World is in the editor assembly's references
            var gate = GameObject.Find("Greybox/Boundary r60/Opens for assault 3 (Black Banner Rise)");
            var t = TypeByName("BoundaryGate");
            if (gate == null || t == null) { GKBridge.Log("world_greybox: rise gate not wired (" + (gate == null ? "no segment" : "no BoundaryGate type") + ")"); return; }
            var so = new SerializedObject(gate.AddComponent(t));
            so.FindProperty("opensAtWave").intValue = RiseOpensAtWave;
            so.FindProperty("waves").objectReferenceValue = waves;
            so.ApplyModifiedPropertiesWithoutUndo();
            GKBridge.Log("world_greybox: rise gate opens at wave index " + RiseOpensAtWave + " (" + gate.transform.childCount + " segments)");
        }

        static void BuildLine(Transform root, Vector3 p, Quaternion facing)
        {
            // P03: the Georgian host Bakur walked out of ("Hold your place!", CS-P03.2), 10 m behind P, Saba on his left in
            // its front rank ("Do not leave the man on your left"). Two ranks of 1.78 m allies (greybox stand-ins), square to
            // the facing; a gap at Saba's place. PlayerBlocker: the player cannot go back through the host. Not static: the
            // whole line moves to the rise at W3 (LineAdvance), Saba_Mark with it.
            var parent = Group(root, "Georgian Line (P03)");
            var host = Mat("M_Grey_Host", new Color(0.42f, 0.5f, 0.62f));
            int blocker = LayerMask.NameToLayer("PlayerBlocker");
            Vector3 c = LineCentre(p, facing), left = facing * Vector3.left, back = facing * Vector3.back;
            for (int rank = 0; rank < 2; rank++)
                for (int i = -10; i <= 10; i++)
                {
                    if (rank == 0 && i == SabaFile) continue;
                    Vector3 q = c + left * (i * FileSpacing) + back * (rank * 1.5f);
                    var m = Block(parent, "Ally_" + rank + "_" + i, q, PlayerFacing + 180f, new Vector3(0.55f, 1.78f, 0.35f), 0.89f, host, true);
                    m.isStatic = false;
                    if (blocker >= 0) m.layer = blocker;
                }
            BuildSabaMark(parent, c + left * (SabaFile * FileSpacing), facing, p);
        }

        static Vector3 LineCentre(Vector3 p, Quaternion facing)
        {
            Vector3 c = p + facing * Vector3.back * LineBehindP;
            return new Vector3(c.x, H(c.x, c.z), c.z);
        }

        static void BuildSabaMark(Transform line, Vector3 s, Quaternion facing, Vector3 p)
        {
            // Saba's fixed mark (bible §9 change 4, CB-10): a plain transform on the ground, forward = the host's facing, a
            // direct child of the line so LineAdvance carries it to Black Banner Rise and sets it back on the ground. Not
            // EditorOnly: Cinematics and Combat find "Saba_Mark" for M1's last beat (he walks forward to Bakur, 6 s). Under
            // it: a 1.60 m stand-in (he is 15; EditorOnly) and the bundle of spare lances he carries for Bakur (greybox,
            // ~3.6 m ash shafts leaning together, butts spread, at his left); both hidden in Play with the rest of the line.
            var mark = new GameObject("Saba_Mark").transform;
            mark.SetParent(line, false);
            mark.SetPositionAndRotation(new Vector3(s.x, H(s.x, s.z), s.z), facing);
            Local(mark, "Saba_StandIn_1.60m (EditorOnly)", new Vector3(0f, 0.8f, 0f), Vector3.zero, new Vector3(0.45f, 1.60f, 0.3f),
                  Mat("M_Grey_Saba", new Color(0.62f, 0.68f, 0.8f)));
            mark.GetChild(0).gameObject.tag = "EditorOnly";
            var bundle = new GameObject("LanceBundle").transform;
            bundle.SetParent(mark, false);
            bundle.localPosition = new Vector3(-0.55f, 0f, -0.1f);
            var ash = Mat("M_Grey_Wood", new Color(0.55f, 0.47f, 0.36f));
            for (int k = 0; k < 5; k++)
            {
                // butts on a 0.25 m circle, tops leaning in to cross near the top
                float a = k * 72f * Mathf.Deg2Rad, tilt = 4.5f;
                var foot = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 0.25f;
                var euler = new Vector3(Mathf.Sin(a) * -tilt, 0f, Mathf.Cos(a) * tilt);
                Local(bundle, "Lance_" + k, foot + Quaternion.Euler(euler) * new Vector3(0f, 1.8f, 0f), euler, new Vector3(0.035f, 3.6f, 0.035f), ash);
            }
            Vector3 d = mark.position - p; d.y = 0f;
            GKBridge.Log("world_greybox: Saba_Mark " + d.magnitude.ToString("F1") + " m from P at bearing " +
                         ((Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg + 360f) % 360f).ToString("F0") + "°, with 5 spare lances");
        }

        static void BuildBaggage(Transform root)
        {
            // P03 assault 2: the baggage brazier and hay by the overturned cart (stampede or smoke; the wind carries it west)
            var parent = Group(root, "Baggage (P03 assault 2)");
            var hay = Mat("M_Grey_Hay", new Color(0.36f, 0.31f, 0.21f));   // weathered cut grass: 0.72/0.62/0.38 and 0.52/0.45/0.30 read as pale eggs at exposure 1.85-2.2
            var iron = Mat("M_Grey_Iron", new Color(0.2f, 0.2f, 0.2f));
            var pole = Mat("M_Grey_Wood", new Color(0.55f, 0.47f, 0.36f));
            // by the overturned cart (118°, 64 m), clear of QAView_Q5's lens. What a camp a day or two old has (Research,
            // 2026-10-31): loose cut grass for the horse lines (the fuel for assault 2's fire), bundles and grain sacks, not
            // built stacks; a pole stack is a farmer's winter store (see BuildMeadowStacks)
            var sack = Mat("M_Grey_Sack", new Color(0.62f, 0.56f, 0.44f));
            Heap(parent, "Hay_1", Pol(113f, 61f), 1.4f, 0.75f, hay);
            Heap(parent, "Hay_2", Pol(116.5f, 62.5f), 1.1f, 0.6f, hay);
            Heap(parent, "Hay_3", Pol(114f, 64.5f), 1.25f, 0.7f, hay);
            for (int i = 0; i < 4; i++)
            {
                var bundle = Shape(parent, "Hay_Bundle_" + (i + 1), ShapeGenerator.GenerateCylinder(PivotLocation.Center, 8, 0.28f, 1.1f, 1), Pol(110.5f + i * 1.3f, 59f + (i % 2) * 1.1f), 0.24f, hay);
                bundle.transform.rotation = Quaternion.Euler(0f, 25f + i * 40f, 90f);   // lying
            }
            for (int i = 0; i < 4; i++)
                Block(parent, "Grain_Sack_" + (i + 1), Pol(121.5f + (i % 2) * 1.2f, 61.5f + (i / 2) * 0.9f), 20f + i * 17f, new Vector3(0.5f, 0.55f, 0.8f), 0.24f, sack, true);
            Shape(parent, "Brazier", ShapeGenerator.GenerateCylinder(PivotLocation.Center, 10, 0.32f, 0.8f, 1), Pol(120f, 60f), 0.4f, iron);
        }

        static void BuildBanners(Transform root)
        {
            var parent = Group(root, "Banners");
            var pole = Mat("M_Grey_Wood", new Color(0.55f, 0.47f, 0.36f));
            // the host's banner in the approved plain cross (GeorgianBanner.mat, DECISIONS 2026-10-20, [G]); a grey cloth read as a
            // stray white quad from QAView_Q8 (Team Lead, 2026-10-31). Greybox only if the material is missing.
            var cross = AssetDatabase.LoadAssetAtPath<Material>(GKPaths.Generated + "/Props/GeorgianBanner.mat");
            if (cross == null) cross = Mat("M_Grey_CrossBanner", new Color(0.86f, 0.84f, 0.8f));   // never the five-cross flag
            var black = Mat("M_Grey_BlackBanner", new Color(0.08f, 0.08f, 0.09f));
            Banner(parent, "Banner_Cross_Host", Pol(20f, 100f), 6.5f, pole, cross);
            Banner(parent, "Banner_Black_Rise", Pol(255f, 118f), 6f, pole, black);
            Banner(parent, "Banner_Black_Rise_2", Pol(250f, 124f), 5f, pole, black);
            // Ilghazi's Ridge, 250°, 2.65 km: three smoke columns as dark stand-ins, to check the sight line
            var smoke = Mat("M_Grey_Smoke", new Color(0.24f, 0.24f, 0.26f));
            var ridge = Group(parent, "Ilghazi's Ridge (smoke stand-ins)");
            int k = 0;
            foreach (var bd in new[] { new Vector2(248.5f, 2600f), new Vector2(250f, 2650f), new Vector2(251.8f, 2700f) })
            {
                Vector3 p = Pol(bd.x, bd.y);
                var c = ShapeGenerator.GenerateCylinder(PivotLocation.Center, 12, 9f, 140f, 1);
                Setup(c, ridge, "Smoke_" + (++k), smoke);
                c.transform.position = new Vector3(p.x, H(p.x, p.z) + 70f, p.z);
            }
        }

        static void BuildGuides(Transform root)
        {
            // floor rings for reading the space (EditorOnly: never in a player build)
            var parent = Group(root, "Guides");
            parent.gameObject.tag = "EditorOnly";
            Ring(parent, "Guide_Core_r15", RCore, new Color(0.69f, 0.49f, 0.07f), 0.12f);
            Ring(parent, "Guide_Playable_r45", RPlay, new Color(0.93f, 0.7f, 0.15f), 0.25f);
            Ring(parent, "Guide_Boundary_r60", RBoundary - 1.2f, new Color(0.75f, 0.2f, 0.16f), 0.2f);
        }

        static void BuildMetricsZoo()
        {
            // scale references (LEVELS.md §1) on a flat pad beyond the E shoulder, out of the player's reach
            var zoo = new GameObject("MetricsZoo (EditorOnly)").transform;
            zoo.gameObject.tag = "EditorOnly";
            Vector3 o = Pol(130f, 150f);
            float top = float.MinValue;
            for (float x = -16f; x <= 16f; x += 4f) for (float z = -13f; z <= 13f; z += 4f) top = Mathf.Max(top, H(o.x + x, o.z + z));
            zoo.position = new Vector3(o.x, top, o.z);
            var pad = Mat("M_Grey_Block", new Color(0.62f, 0.62f, 0.62f));
            var mark = Mat("M_Grey_Spawn", new Color(0.75f, 0.15f, 0.12f));
            var fig = Mat("M_Grey_Figure", new Color(0.86f, 0.86f, 0.86f));
            Local(zoo, "Pad_32x26", new Vector3(0f, -0.1f, 0f), Vector3.zero, new Vector3(32f, 0.2f, 26f), pad);
            Local(zoo, "ScaleFigure_1.70m", new Vector3(-13.5f, 0.85f, 10f), Vector3.zero, new Vector3(0.45f, 1.70f, 0.28f), fig);
            Local(zoo, "Bakur_1.78m", new Vector3(-12.5f, 0.89f, 10f), Vector3.zero, new Vector3(0.48f, 1.78f, 0.3f), fig);
            // stairs: 8 risers of 0.17 m, treads 0.30 m (QUALITY Scale & proportion: steps 15-18 cm)
            for (int i = 0; i < 8; i++)
                Local(zoo, "Stair_" + (i + 1), new Vector3(10f, 0.085f + i * 0.17f, -2f + i * 0.3f), Vector3.zero, new Vector3(1.6f, 0.17f, 0.3f), pad);
            Local(zoo, "Vault_1.0m", new Vector3(-8f, 0.5f, 10f), Vector3.zero, new Vector3(2f, 1f, 1f), pad);
            Local(zoo, "Climb_2.2m", new Vector3(-4f, 1.1f, 10f), Vector3.zero, new Vector3(2f, 2.2f, 1f), pad);
            Local(zoo, "Drop_3.5m", new Vector3(1f, 1.75f, 10f), Vector3.zero, new Vector3(2f, 3.5f, 2f), pad);
            Local(zoo, "Door_1.0x1.95_L", new Vector3(5.6f, 1.075f, 10f), Vector3.zero, new Vector3(0.2f, 2.15f, 0.4f), pad);
            Local(zoo, "Door_1.0x1.95_R", new Vector3(6.8f, 1.075f, 10f), Vector3.zero, new Vector3(0.2f, 2.15f, 0.4f), pad);
            Local(zoo, "Door_1.0x1.95_Top", new Vector3(6.2f, 2.05f, 10f), Vector3.zero, new Vector3(1.4f, 0.2f, 0.4f), pad);
            Local(zoo, "Lane_2.6m_L", new Vector3(10f, 1.5f, 8f), Vector3.zero, new Vector3(0.4f, 3f, 8f), pad);
            Local(zoo, "Lane_2.6m_R", new Vector3(13f, 1.5f, 8f), Vector3.zero, new Vector3(0.4f, 3f, 8f), pad);
            float x0 = -12f;
            foreach (float deg in new[] { 10f, 20f, 30f, 35f })
            {
                float len = 6f, rise = Mathf.Tan(deg * Mathf.Deg2Rad) * len;
                Local(zoo, "Ramp_" + deg + "deg", new Vector3(x0, rise * 0.5f, -2f), new Vector3(-deg, 0f, 0f), new Vector3(2.5f, 0.2f, len / Mathf.Cos(deg * Mathf.Deg2Rad)), pad);
                x0 += 4f;
            }
            // combat nodes: 8 x 8 (1 v 3) and 12 x 12 (1 v 5) outlines
            Outline(zoo, "Node_8x8", new Vector3(-7f, 0.02f, -8.5f), 8f, mark);
            Outline(zoo, "Node_12x12", new Vector3(7f, 0.02f, -6.5f), 12f, mark);
        }

        static void BuildSpawnMarkers()
        {
            // where each lane's enemies appear (contract C15 with Combat): a SpawnLane component (Gameplay/Encounters)
            // with its lane number; WaveManager finds them in the scene. Added by name, so this builder compiles and runs
            // before and after Combat's component exists; without it the markers stay plain transforms.
            var parent = new GameObject("Spawns").transform;
            var red = Mat("M_Grey_Spawn", new Color(0.75f, 0.15f, 0.12f));
            var laneType = TypeByName("SpawnLane");
            if (laneType == null) GKBridge.Log("world_greybox: no SpawnLane component yet (Combat, C15): markers are plain transforms");
            foreach (var l in Lanes)
            {
                Vector3 p = Pol(l.bearing, l.spawn);
                p.y = H(p.x, p.z);
                var m = new GameObject("SpawnLane_" + l.id).transform;
                m.SetParent(parent, false);
                m.position = p;
                m.rotation = Quaternion.Euler(0f, l.bearing + 180f, 0f);   // facing the bowl
                if (laneType != null)
                {
                    var c = m.gameObject.AddComponent(laneType);
                    var f = laneType.GetField("lane");
                    if (f != null) f.SetValue(c, int.Parse(l.id.Substring(1)));
                }
                var pillar = ShapeGenerator.GenerateCube(PivotLocation.Center, new Vector3(0.4f, 2.6f, 0.4f));
                Setup(pillar, m, "Marker", red);
                pillar.transform.localPosition = new Vector3(0f, 1.3f, 0f);
                pillar.gameObject.tag = "EditorOnly";
            }
        }

        static void BuildQaViews()
        {
            // Build & QA's format (qa.shots / run R1): QAView_* GameObjects with a DISABLED Camera for the FOV, not EditorOnly.
            // Compare_* views are World's own (EditorOnly), captured by world_shots only.
            var parent = new GameObject("QA").transform;
            foreach (var v in Views)
            {
                var go = new GameObject(v.name);
                go.transform.SetParent(parent, false);
                if (v.name.StartsWith("Compare_")) go.tag = "EditorOnly";
                go.transform.position = new Vector3(v.x, H(v.x, v.z) + v.h, v.z);
                if (float.IsNaN(v.pitch)) go.transform.LookAt(new Vector3(0f, H(0f, 0f), 0f));
                else go.transform.rotation = Quaternion.Euler(v.pitch, v.look, 0f);
                var c = go.AddComponent<Camera>();
                c.fieldOfView = v.fov; c.nearClipPlane = 0.05f; c.farClipPlane = 32000f;
                c.enabled = false;
            }
        }

        static void BuildAudioMarkers()
        {
            // Audio's AMB_<Cue>_<NN> spots (Audio makes the prefabs, World places them; contract C8)
            var parent = new GameObject("Audio").transform;
            System.Action<string, float, float> at = (n, b, r) =>
            {
                var m = new GameObject(n).transform;
                m.SetParent(parent, false);
                Vector3 p = Pol(b, r); p.y = H(p.x, p.z) + 1.5f;
                m.position = p;
            };
            at("AMB_HostGeo_01", 340f, 90f); at("AMB_HostGeo_02", 20f, 110f); at("AMB_HostGeo_03", 65f, 95f);
            at("AMB_Army_01", 170f, 100f); at("AMB_Army_02", 230f, 120f); at("AMB_Army_03", 290f, 105f);
            foreach (var l in Lanes) at("AMB_EnemySignal_" + l.id, l.bearing, l.spawn);
            at("AMB_Banner_Geo_01", 20f, 100f); at("AMB_Banner_Trk_01", 255f, 118f);
            at("AMB_Rocks_01", 118f, 70f);
        }

        // =================================================================== check

        static void Report(Vector3 playerSpawn)
        {
            var sb = new StringBuilder("world_greybox report:");
            float c = H(0f, 0f);
            float lo = float.MaxValue, hi = float.MinValue, maxSlope = 0f;
            for (float r = 0f; r <= RPlay; r += 5f)
                for (float b = 0f; b < 360f; b += 15f)
                {
                    Vector3 p = Pol(b, r);
                    float h = H(p.x, p.z) - c;
                    lo = Mathf.Min(lo, h); hi = Mathf.Max(hi, h);
                    float s = Mathf.Atan(Mathf.Sqrt(Sq(H(p.x + 1f, p.z) - H(p.x - 1f, p.z)) + Sq(H(p.x, p.z + 1f) - H(p.x, p.z - 1f))) / 2f) * Mathf.Rad2Deg;
                    maxSlope = Mathf.Max(maxSlope, s);
                }
            sb.Append("\n  floor y ").Append(c.ToString("F2")).Append(", r<=45 relief ").Append(lo.ToString("F2")).Append("..").Append(hi.ToString("F2"))
              .Append(" m, max slope ").Append(maxSlope.ToString("F1")).Append(" deg (bar: <= 3 deg)");
            sb.Append("\n  player P ").Append(playerSpawn.ToString("F1"));
            foreach (var l in Lanes)
            {
                Vector3 s = Pol(l.bearing, l.spawn); s.y = H(s.x, s.z);
                var path = new NavMeshPath();
                NavMeshHit a, b;
                bool okA = NavMesh.SamplePosition(s, out a, 4f, NavMesh.AllAreas), okB = NavMesh.SamplePosition(playerSpawn, out b, 4f, NavMesh.AllAreas);
                string st = okA && okB && NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path) ? path.status.ToString() : "no navmesh at " + (okA ? "P" : "spawn");
                float len = 0f;
                for (int i = 1; i < path.corners.Length; i++) len += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                sb.Append("\n  ").Append(l.id).Append(" spawn ").Append(s.ToString("F1")).Append(" (").Append((s.y - c).ToString("+0.0;-0.0")).Append(" m over the floor), path to P: ")
                  .Append(st).Append(", ").Append(len.ToString("F0")).Append(" m");
            }
            // assault 3: the rise (C17). Footing, and NavMesh paths up to its top from L1's spawn and from P (the gate is
            // PlayerBlocker, outside the bake, so these paths go through it; the pocket walls are in the bake)
            Vector3 top = RiseTop; top.y = H(top.x, top.z);
            float riseSlope = 0f;
            for (float r = 0f; r <= 10f; r += 2f)
                for (float b = 0f; b < 360f; b += 30f)
                {
                    Vector3 q = top + Pol(b, r);
                    riseSlope = Mathf.Max(riseSlope, Mathf.Atan(Mathf.Sqrt(Sq(H(q.x + 1f, q.z) - H(q.x - 1f, q.z)) + Sq(H(q.x, q.z + 1f) - H(q.x, q.z - 1f))) / 2f) * Mathf.Rad2Deg);
                }
            sb.Append("\n  Black Banner Rise top ").Append(top.ToString("F1")).Append(" (").Append((top.y - c).ToString("+0.0;-0.0")).Append(" m), max slope within 10 m ")
              .Append(riseSlope.ToString("F1")).Append(" deg");
            foreach (var from in new[] { new KeyValuePair<string, Vector3>("L1 spawn", Pol(Lanes[0].bearing, Lanes[0].spawn)), new KeyValuePair<string, Vector3>("P", playerSpawn) })
            {
                Vector3 f = from.Value; f.y = H(f.x, f.z);
                var path = new NavMeshPath();
                NavMeshHit a, b;
                bool ok = NavMesh.SamplePosition(f, out a, 4f, NavMesh.AllAreas) && NavMesh.SamplePosition(top, out b, 4f, NavMesh.AllAreas)
                          && NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path);
                float len = 0f;
                for (int i = 1; i < path.corners.Length; i++) len += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                sb.Append("\n  ").Append(from.Key).Append(" -> rise top: ").Append(ok ? path.status.ToString() : "no navmesh").Append(", ").Append(len.ToString("F0")).Append(" m");
            }
            GKBridge.Log(sb.ToString());
        }

        static float Sq(float v) { return v * v; }

        static System.Type TypeByName(string name)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }
                foreach (var t in types)
                    if (t != null && t.Name == name && typeof(Component).IsAssignableFrom(t)) return t;
            }
            return null;
        }

        /// <summary>Stills of the six QA views (1920 x 1080, post from the Main Camera) into GKBridge/world_Q*.png.</summary>
        [GKCommand("world_rockstill", "world_rockstill [close]: Asset QA stills (GKBridge/world_rock_lane.png, world_rock_shoulder.png): a mid-lane edge rock and the east-shoulder rocks beside a 1.70 m figure, editor aids hidden")]
        static void RockStill(string a) { bool close = a != null && a.Trim() == "close"; KeepClean(() => RockStillNow(close)); }

        /// <summary>close: from 3 m (Asset QA's texel check), else framed on the rocks' size.</summary>
        static void RockStillNow(bool close)
        {
            var lanes = GameObject.Find("Greybox/Lanes/Lane_L1");
            var shoulder = GameObject.Find("Greybox/East Shoulder/Rock_2");
            if (lanes == null || lanes.transform.childCount < 3 || shoulder == null) { GKBridge.Log("world_rockstill: open the arena (world_greybox) first"); return; }
            var figMat = Mat("M_Grey_Figure", new Color(0.86f, 0.86f, 0.86f));
            var temps = new List<GameObject>();
            var hidden = HideAids();
            try
            {
                foreach (var shot in new[] { new KeyValuePair<string, GameObject>("world_rock_lane", lanes.transform.GetChild(lanes.transform.childCount / 2).gameObject),
                                             new KeyValuePair<string, GameObject>("world_rock_shoulder", shoulder) })
                {
                    Bounds b = RendererBounds(shot.Value);
                    if (shot.Key == "world_rock_shoulder") { var b2 = GameObject.Find("Greybox/East Shoulder/Rock_2b"); if (b2 != null) b.Encapsulate(RendererBounds(b2)); }
                    Vector3 toBowl = new Vector3(-b.center.x, 0f, -b.center.z).normalized;
                    Vector3 side = Vector3.Cross(Vector3.up, toBowl);
                    // the 1.70 m figure beside the rocks, on the camera's side
                    Vector3 fp = b.center + side * (b.extents.magnitude + 0.6f);
                    var fig = ShapeGenerator.GenerateCube(PivotLocation.Center, new Vector3(0.45f, 1.70f, 0.28f));
                    Setup(fig, null, "~rockstill_figure", figMat);
                    fig.transform.position = new Vector3(fp.x, H(fp.x, fp.z) + 0.85f, fp.z);
                    temps.Add(fig.gameObject);
                    float dist = close ? 3f : Mathf.Max(5f, b.size.magnitude * 2.2f);
                    Vector3 cp = b.center + toBowl * dist + side * 1.2f;
                    var cam = new GameObject("~rockstill_cam").AddComponent<Camera>();
                    temps.Add(cam.gameObject);
                    cam.gameObject.AddComponent<UniversalAdditionalCameraData>().renderPostProcessing = true;
                    cam.transform.position = new Vector3(cp.x, H(cp.x, cp.z) + 1.6f, cp.z);
                    cam.transform.LookAt(new Vector3(b.center.x, H(b.center.x, b.center.z) + 0.9f, b.center.z));
                    cam.fieldOfView = 40f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 32000f;
                    GKPreview.Capture(cam, close ? shot.Key + "_close" : shot.Key);
                }
            }
            finally
            {
                foreach (var t in temps) if (t != null) Object.DestroyImmediate(t);
                foreach (var r in hidden) if (r != null) r.enabled = true;
            }
            GKBridge.Log("world_rockstill: world_rock_lane.png, world_rock_shoulder.png");
        }

        static List<Renderer> HideAids()
        {
            var hidden = new List<Renderer>();
            var aidType = TypeByName("EditorAid");
            if (aidType != null)
                foreach (var aid in Object.FindObjectsByType(aidType, FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    foreach (var r in ((Component)aid).GetComponentsInChildren<Renderer>(true))
                        if (r.enabled) { r.enabled = false; hidden.Add(r); }
            return hidden;
        }

        public static void CaptureViews(bool withAids = false)
        {
            // review stills show the frame the player gets: every EditorAid group's renderers off for the capture, then back
            var hidden = new List<Renderer>();
            var aidType = TypeByName("EditorAid");
            if (!withAids && aidType != null)
                foreach (var aid in Object.FindObjectsByType(aidType, FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    foreach (var r in ((Component)aid).GetComponentsInChildren<Renderer>(true))
                        if (r.enabled) { r.enabled = false; hidden.Add(r); }
            // edit-mode stills show the hero in his prefab (T) pose: the Animator only poses the humanoid rig in Play, and
            // AnimationMode sampling did not (2026-10-31). Hero-facing review views come from a Play run (gameshot).
            try { CaptureViewsNow(); }
            finally { foreach (var r in hidden) if (r != null) r.enabled = true; }
            GKBridge.Log("world_shots: " + (withAids ? "with the editor aids" : hidden.Count + " editor-aid renderers hidden, as in Play"));
        }

        static void CaptureViewsNow()
        {
            var main = Camera.main;
            var views = Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var list = new List<Camera>();
            foreach (var v in views) if (v.name.StartsWith("QAView") || v.name.StartsWith("Compare_")) list.Add(v);
            if (list.Count == 0) { GKBridge.Log("world_shots: no QAView cameras in the open scene"); return; }
            list.Sort((x, y) => string.CompareOrdinal(x.name, y.name));
            var tmp = new GameObject("~world_shot").AddComponent<Camera>();
            var data = tmp.gameObject.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            if (main != null)
            {
                var md = main.GetComponent<UniversalAdditionalCameraData>();
                if (md != null) { data.antialiasing = md.antialiasing; data.antialiasingQuality = md.antialiasingQuality; }
            }
            try
            {
                foreach (var v in list)
                {
                    tmp.transform.SetPositionAndRotation(v.transform.position, v.transform.rotation);
                    tmp.fieldOfView = v.fieldOfView; tmp.nearClipPlane = 0.05f; tmp.farClipPlane = 32000f;
                    GKPreview.Capture(tmp, "world_" + (v.name.StartsWith("QAView_") ? v.name.Substring("QAView_".Length) : v.name));
                }
            }
            finally { Object.DestroyImmediate(tmp.gameObject); }
        }

        // =================================================================== blocks

        static readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();

        static Material Mat(string name, Color c)
        {
            Material m;
            if (mats.TryGetValue(name, out m) && m != null) return m;
            string path = Dir + "/" + name + ".mat";
            m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", 0.1f);
            m.SetFloat("_Metallic", 0f);
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            mats[name] = m;
            return m;
        }

        static Material Unlit(string name, Color c)
        {
            string path = Dir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Unlit")); AssetDatabase.CreateAsset(m, path); }
            m.SetColor("_BaseColor", c);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Transform Group(Transform parent, string name)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            return t;
        }

        static void Setup(ProBuilderMesh pb, Transform parent, string name, Material mat)
        {
            pb.gameObject.name = name;
            pb.transform.SetParent(parent, false);
            pb.GetComponent<MeshRenderer>().sharedMaterial = mat;
            pb.ToMesh();
            pb.Refresh();
        }

        // Poly Haven CC0 scans at their real size (Asset QA intake, 2026-10-31): boulders for the lane edges and the shoulder,
        // the two pebble scans as ground stones. The warm South-African stone is greyed in GKProps.RockPrefab.
        static readonly string[] LaneRocks = { "namaqualand_boulder_06", "namaqualand_boulder_02", "boulder_01" };
        static readonly string[] Pebbles = { "rock_07", "rock_09" };
        static string RockRes(string id) { return id == "rock_07" || id == "rock_09" ? "1k" : "2k"; }
        static float Jitter(int k) { return 0.8f + (Mathf.Abs(k * 37) % 50) / 100f; }   // 0.8-1.3

        /// <summary>A scanned rock (GKProps.RockPrefab) at scale x its real size (0.8-1.3), centred on p, turned by yaw, sunk 12 %
        /// of its height into the lowest ground under it. Static; its one convex collider stays only with collider. Falls back
        /// to a grey block of about the rock's size if the model is missing.</summary>
        static GameObject Rock(Transform parent, string name, string id, Vector3 p, float yaw, float scale, Material fallback, bool collider)
        {
            var prefab = GKProps.RockPrefab(id, RockRes(id));
            if (prefab == null) return Block(parent, name, p, yaw, new Vector3(1.4f, 1.2f, 2f) * scale, 0.4f * scale, fallback, collider);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent.gameObject.scene);
            go.transform.SetParent(parent, true);
            go.name = name;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f) * prefab.transform.rotation;
            go.transform.localScale = prefab.transform.localScale * scale;
            Bounds b = RendererBounds(go);
            float ground = Mathf.Min(H(p.x, p.z), H(p.x - b.extents.x, p.z), H(p.x + b.extents.x, p.z), H(p.x, p.z - b.extents.z), H(p.x, p.z + b.extents.z));
            go.transform.position += new Vector3(p.x - b.center.x, ground - b.size.y * 0.12f - b.min.y, p.z - b.center.z);
            if (!collider) foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
            foreach (var t in go.GetComponentsInChildren<Transform>()) t.gameObject.isStatic = true;
            return go;
        }

        static void BuildGroundStones(Transform root)
        {
            // the small stones of a trampled upland floor (the Lead's ground brief): the pebble scans at their real 0.15-0.4 m,
            // scattered over the floor and the worn ring, no colliders (feet and the camera pass over them)
            var parent = Group(root, "Ground stones");
            var rnd = new System.Random(1121);
            for (int i = 0; i < 70; i++)
            {
                float bearing = (float)rnd.NextDouble() * 360f, r = 4f + (float)System.Math.Sqrt(rnd.NextDouble()) * 52f;
                Rock(parent, "Stone_" + i, Pebbles[i % 2], Pol(bearing, r), (float)rnd.NextDouble() * 360f, 0.8f + (float)rnd.NextDouble() * 0.5f, null, false);
            }
        }

        /// <summary>A ProBuilder shape standing on the terrain at p (its centre lift above the lowest ground under it), with a
        /// convex collider, static.</summary>
        static GameObject Shape(Transform parent, string name, ProBuilderMesh pb, Vector3 p, float lift, Material mat)
        {
            Setup(pb, parent, name, mat);
            pb.transform.position = new Vector3(p.x, H(p.x, p.z) + lift, p.z);
            pb.gameObject.isStatic = true;
            pb.gameObject.AddComponent<MeshCollider>().convex = true;
            return pb.gameObject;
        }

        /// <summary>A ზვინი: a tall rounded stack (radius at the widest, height above the ground) with its pole standing 0.5 m out of
        /// the top. Greybox until Art's model.</summary>
        /// <summary>A loose low heap of cut grass (radius, height): a flattened icosahedron, half in the ground.</summary>
        /// Three low overlapping lumps, sunk a third: one smooth flattened egg read as a mushroom (Critics, the Lead, 2026-10-31).
        static GameObject Heap(Transform parent, string name, Vector3 p, float radius, float height, Material mat)
        {
            int seed = 7; foreach (char ch in name) seed = seed * 31 + ch;
            var rnd = new System.Random(seed);
            GameObject first = null;
            for (int i = 0; i < 3; i++)
            {
                float r = radius * (i == 0 ? 1f : 0.5f + 0.25f * (float)rnd.NextDouble());
                float a = (float)rnd.NextDouble() * Mathf.PI * 2f, off = i == 0 ? 0f : radius * (0.55f + 0.3f * (float)rnd.NextDouble());
                float h = height * (i == 0 ? 1f : 0.6f + 0.3f * (float)rnd.NextDouble());
                var lump = Shape(parent, name + (i == 0 ? "" : "_" + i), ShapeGenerator.GenerateIcosahedron(PivotLocation.Center, r, 1, true, true),
                                 p + new Vector3(Mathf.Cos(a) * off, 0f, Mathf.Sin(a) * off), -h * 0.35f, mat);
                lump.transform.localScale = new Vector3(1f + 0.2f * (float)rnd.NextDouble(), h / r, 1.1f + 0.3f * (float)rnd.NextDouble());
                lump.transform.rotation = Quaternion.Euler(0f, (float)rnd.NextDouble() * 360f, 0f);
                if (first == null) first = lump;
            }
            return first;
        }

        static void BuildMeadowStacks(Transform root)
        {
            // the meadow's own hay: villagers' ზვინი at the NE meadow edge, out of the fight. A long-lived Georgian folk form
            // (19th-20th c. ethnography), plausible but unattested for 1121 (Research, 2026-10-31); local work, not the army's
            var parent = Group(root, "Meadow stacks (villagers')");
            var hay = Mat("M_Grey_HayStack", new Color(0.46f, 0.4f, 0.3f));   // weathered stack hay, darker than the meadow
            var pole = Mat("M_Grey_Wood", new Color(0.55f, 0.47f, 0.36f));
            // NNE behind the host line, so the turn-to-host view (QAView_Q7) shows them (Critics AR-6)
            Zvini(parent, "Zvini_1", Pol(24f, 74f), 1.15f, 3.4f, hay, pole);   // at 100 m+ they sank behind the N lip
            Zvini(parent, "Zvini_2", Pol(31f, 80f), 1.0f, 3.0f, hay, pole);
        }

        static GameObject Zvini(Transform parent, string name, Vector3 p, float radius, float height, Material hay, Material pole)
        {
            // a tall cone built round its pole, darker than the meadow (weathered stack hay); an egg read wrong (Critics, 2026-10-31)
            var go = Shape(parent, name, ShapeGenerator.GenerateCone(PivotLocation.Center, radius, height, 14), p, height * 0.5f - 0.15f, hay);
            var stick = ShapeGenerator.GenerateCylinder(PivotLocation.Center, 8, 0.05f, height + 0.6f, 1);
            Setup(stick, go.transform.parent, name + "_Pole", pole);
            stick.transform.position = new Vector3(p.x, H(p.x, p.z) + (height + 0.6f) * 0.5f - 0.15f, p.z);
            stick.gameObject.isStatic = true;
            return go;
        }

        /// <summary>No grass through the rocks (Asset QA, 2026-10-31): every detail layer zeroed under each scanned rock's
        /// footprint, plus 0.3 m.</summary>
        static void ClearDetailsUnderRocks()
        {
            var terrain = Terrain.activeTerrain;
            if (terrain == null) return;
            var td = terrain.terrainData;
            int res = td.detailResolution, layers = td.detailPrototypes.Length, cleared = 0;
            Vector3 origin = terrain.transform.position;
            foreach (var r in Object.FindObjectsByType<LODGroup>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!r.name.StartsWith("Edge") && !r.name.StartsWith("Rock_")) continue;
                Bounds b = RendererBounds(r.gameObject);
                int x0 = Mathf.Clamp(Mathf.FloorToInt((b.min.x - 0.3f - origin.x) / td.size.x * res), 0, res - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt((b.max.x + 0.3f - origin.x) / td.size.x * res), 0, res - 1);
                int z0 = Mathf.Clamp(Mathf.FloorToInt((b.min.z - 0.3f - origin.z) / td.size.z * res), 0, res - 1);
                int z1 = Mathf.Clamp(Mathf.CeilToInt((b.max.z + 0.3f - origin.z) / td.size.z * res), 0, res - 1);
                var zero = new int[z1 - z0 + 1, x1 - x0 + 1];
                for (int l = 0; l < layers; l++) td.SetDetailLayer(x0, z0, l, zero);
                cleared++;
            }
            GKBridge.Log("world_greybox: grass cleared under " + cleared + " rocks");
        }

        static Bounds RendererBounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.one);
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b;
        }

        /// <summary>A grey ProBuilder box standing on the terrain at p, turned to face the bowl along bearing,
        /// its centre lift above the ground (sunk a little on slopes so it never floats).</summary>
        static GameObject Block(Transform parent, string name, Vector3 p, float bearing, Vector3 size, float lift, Material mat, bool collider)
        {
            var pb = ShapeGenerator.GenerateCube(PivotLocation.Center, size);
            Setup(pb, parent, name, mat);
            float y = Mathf.Min(H(p.x - size.x * 0.5f, p.z), H(p.x + size.x * 0.5f, p.z), H(p.x, p.z - size.z * 0.5f), H(p.x, p.z + size.z * 0.5f));
            y = Mathf.Min(y, H(p.x, p.z));
            pb.transform.position = new Vector3(p.x, y + lift, p.z);
            pb.transform.rotation = Quaternion.Euler(0f, bearing, 0f);
            pb.gameObject.isStatic = true;
            if (collider) pb.gameObject.AddComponent<BoxCollider>();
            return pb.gameObject;
        }

        static void Local(Transform parent, string name, Vector3 pos, Vector3 euler, Vector3 size, Material mat)
        {
            var pb = ShapeGenerator.GenerateCube(PivotLocation.Center, size);
            Setup(pb, parent, name, mat);
            pb.transform.localPosition = pos;
            pb.transform.localRotation = Quaternion.Euler(euler);
        }

        static void Outline(Transform parent, string name, Vector3 centre, float side, Material mat)
        {
            var g = Group(parent, name);
            g.localPosition = centre;
            float h = side * 0.5f;
            Local(g, "N", new Vector3(0f, 0f, h), Vector3.zero, new Vector3(side, 0.04f, 0.15f), mat);
            Local(g, "S", new Vector3(0f, 0f, -h), Vector3.zero, new Vector3(side, 0.04f, 0.15f), mat);
            Local(g, "E", new Vector3(h, 0f, 0f), Vector3.zero, new Vector3(0.15f, 0.04f, side), mat);
            Local(g, "W", new Vector3(-h, 0f, 0f), Vector3.zero, new Vector3(0.15f, 0.04f, side), mat);
        }

        static void Banner(Transform parent, string name, Vector3 p, float height, Material pole, Material cloth)
        {
            var g = Group(parent, name);
            g.position = new Vector3(p.x, H(p.x, p.z) - 0.2f, p.z);
            var stick = ShapeGenerator.GenerateCylinder(PivotLocation.Center, 8, 0.07f, height, 0);
            Setup(stick, g, "Pole", pole);
            stick.transform.localPosition = new Vector3(0f, height * 0.5f, 0f);
            // a textured cloth needs 0-1 UVs (a ProBuilder box has metre UVs and would tile the cross): a quad, no collider
            GameObject flag;
            if (cloth.mainTexture != null)
            {
                flag = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Object.DestroyImmediate(flag.GetComponent<Collider>());
                flag.name = "Cloth";
                flag.transform.SetParent(g, false);
                flag.transform.localScale = new Vector3(1.2f, 1.6f, 1f);
                flag.GetComponent<MeshRenderer>().sharedMaterial = cloth;
            }
            else
            {
                var box = ShapeGenerator.GenerateCube(PivotLocation.Center, new Vector3(1.2f, 1.6f, 0.04f));
                Setup(box, g, "Cloth", cloth);
                flag = box.gameObject;
            }
            // the wind blows from 70°: the cloth streams toward the coalition, 250°
            g.rotation = Quaternion.Euler(0f, 250f - 90f, 0f);
            flag.transform.localPosition = new Vector3(0.65f, height - 1f, 0f);
        }

        static void Ring(Transform parent, string name, float r, Color c, float width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            const int n = 120;
            lr.positionCount = n;
            lr.loop = true;
            lr.useWorldSpace = true;
            lr.widthMultiplier = width;
            lr.alignment = LineAlignment.TransformZ;
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // lie flat on the ground
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.sharedMaterial = Unlit("M_Guide_" + name.Substring("Guide_".Length), c);
            for (int i = 0; i < n; i++)
            {
                Vector3 p = Pol(i * 360f / n, r);
                p.y = H(p.x, p.z) + 0.08f;
                lr.SetPosition(i, p);
            }
        }
    }
}
