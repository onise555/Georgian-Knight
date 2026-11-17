using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Didgori-style battlefield: a high grassland plateau (the arena) above a long valley that falls
    /// away to the south, ringed by ridged Caucasus ranges with snow on the peaks. Generates terrain,
    /// splat/grass, boulders, Georgian banners, HDRI sky aligned to the sun, fog and post-processing.
    /// World origin = centre of the arena (y = 0 on the plateau). The valley vista opens toward -Z.
    /// </summary>
    public static class GKEnvironment
    {
        public static float Size = 4000f;
        public static bool UseDem;
        public static float BaseY;
        public const int InnerZoom = 13, OuterZoom = 11;
        public const float OuterSize = 40000f;
        public const float MaxHeight = 1400f;
        public const float Plateau = 160f;
        public const int HeightRes = 2049;
        public const int SplatRes = 2048;
        public const int DetailRes = 2048;
        public const string Hdri = "kloofendal_48d_partly_cloudy_puresky";
        public const float DesiredSunAzimuth = 32f; // degrees, measured from +Z toward +X

        static readonly int Seed = 1121; // year of the Battle of Didgori

        /// <summary>Which level the environment is built for. The default is Didgori.unity exactly as before; a level
        /// re-centred elsewhere on the real field (Level_Didgori_Arena) gets its own assets, so building one never
        /// rewrites the other's terrain, sky or post.</summary>
        public class Site
        {
            public string TerrainAsset = "Didgori.asset", FarAsset = "DidgoriFar.asset", SkyMaterial = "Sky.mat", PostAsset = "DidgoriPost.asset";
            public string TerrainName = "Didgori Terrain";
            public Vector2 DemOffset;                        // metres E / N of GKDem.Lat0/Lon0 that become the origin
            public float FlattenInner = 45f, FlattenOuter = 130f; // the arena floor levelled inside, blended out to outer
            public float FloorRelief = 1f;                   // micro relief inside FlattenOuter (1 = as everywhere else)
            public bool Dressing = true;                     // the old layout's boulders and banners
            public bool Snow = true;                         // false: the snow layer stays empty (mid-August)
            public float SunAzimuth = DesiredSunAzimuth;     // compass degrees the light comes from
            public float SunElevation = float.NaN;           // NaN: from the HDRI, kept between 12 and 40 degrees
            public float SunTemperature = 5500f, SunIntensity = 2.8f;
            public float WindFrom = 20f;                     // compass degrees the wind blows from
            public string Hdri = GKEnvironment.Hdri;         // Poly Haven id of the sky (CC0)
            public float SkyExposure = 1f, AmbientIntensity = 0.85f, PostExposure = 0.05f;   // the lighting pass (world_light)
            public float GrassClumping = 1f;                 // 1 = clumps with bare gaps (as before), 0 = an even meadow
            public float FogStart = float.NaN, FogEnd = float.NaN;   // linear fog, metres; NaN: 220/21000 on the DEM, 90/2700 without
            public Color FogColor = new Color(0.69f, 0.76f, 0.84f);
            public float DirtInner = 6f, DirtOuter = 17f;    // bare centre radius, and where the meadow is full again (metres)
            public bool Stubble = false;                     // a short dry-grass layer that fades the meadow into the bare floor
            public float PostSaturation = 6f, PostContrast = 12f;   // the post grade (Color Adjustments)
            public float ShadowLift = -0.02f;                // Shadows/Midtones/Highlights: the shadows' offset (+ lifts only the darkest tones, cool)
            public float SkyFill = 0f;                       // a cool shadowless directional fill from the sky opposite the sun (0: none)
            public bool SkyOcclusion = false;                // GK/Terrain Lit (Sky Occlusion) + baked sky-visibility maps (GK.World SkyVisibility)
            public bool PhysicalAlbedo = false;              // terrain layers and grass baked to physical linear albedo (Docs/World/ALBEDO.md)
            public float SkyAwayGain = 1f, SkyGainPower = 1.5f;   // >1: GK/SkyPanoramicGraded lifts the sky opposite the sun (a graduated filter)
            public Color SkyAwayTint = Color.white;          // the colour the lifted anti-sun sky takes
            public float DetailDistance = 110f;              // terrain details (grass) drawn to, metres
            public bool GreyTrees = false;                   // greybox broadleaf trees on the forest layer at range (crest silhouettes)
            public float SunDisk = 0f, SunHalo = 0f;         // lens-flare sun disk and halo sizes on the Sun (0: none, the HDRI's own sun only)
            public Color DirtTint = Color.white;             // the bare-earth layer's tint
            public float FarEdgeFade = 0f;                   // metres: the far ranges sink to the valley floor this far inside their tile's edge (0: off)
            public bool MacroColour = false;                 // far slopes: forest on north faces, drier south faces, rock on ridges; a second dirt scale
            public Vector3[] TrampledPaths = null;           // worn tracks from the centre: (bearing deg, length m, width m)
            public float GrassDensity = 15f;                 // tall clumps per detail cell (~2.9 m) at full meadow
            public bool HayMeadow = false;                   // upper-montane hay meadow: dense low straw cover, straw-gold tall grass, green only in hollows
        }

        public static Site Current = new Site();

        public static GameObject Root;

        public static void Build()
        {
            GKPaths.Ensure(GKPaths.Generated + "/Environment");
            GKDem.Offset = Current.DemOffset;
            var old = GameObject.Find("Environment");
            if (old != null) Object.DestroyImmediate(old);
            Root = new GameObject("Environment");
            var terrain = BuildTerrain();
            terrain.transform.SetParent(Root.transform, true);
            if (UseDem)
            {
                var outer = BuildOuterTerrain(terrain.GetComponent<Terrain>().terrainData.terrainLayers);
                if (outer != null) outer.transform.SetParent(Root.transform, true);
            }
            if (Current.GreyTrees && Current.MacroColour) AddGreyForest();
            if (Current.SkyOcclusion) BakeSkyVisibility();
            BuildSkyAndLight();
            BuildPost();
            if (Current.Dressing)
            {
                ScatterBoulders(terrain);
                PlaceBanners(terrain);
            }
            EditorUtility.ClearProgressBar();
            GKBridge.Log("environment built (" + Current.TerrainAsset + ", DEM offset " + Current.DemOffset + ")");
        }

        // =================================================================== height field

        static float Fbm(float x, float z, int oct)
        {
            float a = 0.5f, f = 1f, s = 0f, n = 0f;
            for (int i = 0; i < oct; i++)
            {
                s += a * (Mathf.PerlinNoise(x * f + 31.7f * i + Seed * 0.013f, z * f - 17.3f * i) * 2f - 1f);
                n += a; a *= 0.5f; f *= 2.03f;
            }
            return s / n;
        }

        static float Ridged(float x, float z, int oct)
        {
            float a = 0.5f, f = 1f, s = 0f, n = 0f, w = 1f;
            for (int i = 0; i < oct; i++)
            {
                float v = 1f - Mathf.Abs(Mathf.PerlinNoise(x * f + 11.1f * i + Seed * 0.007f, z * f + 5.3f * i) * 2f - 1f);
                v *= v;
                v *= w;
                w = Mathf.Clamp01(v * 1.6f);
                s += a * v; n += a; a *= 0.5f; f *= 2.1f;
            }
            return s / n;
        }

        public static float RoadX(float z)
        {
            return 55f * Mathf.Sin(z * 0.0042f + 0.6f) + 18f * Mathf.Sin(z * 0.0127f) - 10f;
        }

        public static float RoadMask(float x, float z)
        {
            if (UseDem) return 0f;
            if (z > -60f || z < -1900f) return 0f;
            float d = Mathf.Abs(x - RoadX(z));
            float fadeIn = Mathf.Clamp01((-z - 60f) / 40f);
            return (1f - Mathf.SmoothStep(2.2f, 5.5f, d)) * fadeIn;
        }

        /// <summary>World height (metres, plateau = Plateau) at world x/z.</summary>
        public static float HeightAt(float x, float z)
        {
            float d = Mathf.Sqrt(x * x + z * z);
            float dv = z < 0f ? Mathf.Sqrt(x * x + z * z * 0.2f) : d;          // valley opens toward -Z
            float meadow = Plateau + Fbm(x * 0.0035f, z * 0.0035f, 5) * 22f + Fbm(x * 0.02f, z * 0.02f, 3) * 2.2f;
            float valleyBand = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(220f, 950f, Mathf.Abs(x)));
            float descent = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((-z - 140f) / 1500f)) * 135f * valleyBand;
            float wx = x + Fbm(x * 0.0007f + 3f, z * 0.0007f, 3) * 280f;
            float wz = z + Fbm(x * 0.0007f, z * 0.0007f + 9f, 3) * 280f;
            float ridge = Ridged(wx * 0.0007f, wz * 0.0007f, 4);
            float mountain = Mathf.Pow(ridge, 1.7f) * 980f + (Fbm(x * 0.0004f, z * 0.0004f, 3) * 0.5f + 0.5f) * 170f;
            float mMask = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(680f, 1950f, dv));
            float h = meadow - descent + mMask * mountain;

            // shoulders that frame the arena (open toward the valley)
            float ang = Mathf.Atan2(x, z) * Mathf.Rad2Deg;                       // 0 = +Z
            float gap = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(150f, 115f, Mathf.Abs(ang))); // 1 near ±180 (=-Z)
            float rim = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(62f, 92f, d)) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(120f, 210f, d)));
            h += rim * (1f - gap) * (4.5f + Fbm(x * 0.018f, z * 0.018f, 3) * 3f);

            // the arena: a broad, slightly dished, trampled plateau
            float aMask = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(40f, 100f, d));
            float arena = Plateau + Fbm(x * 0.05f, z * 0.05f, 3) * 0.35f + (d / 40f) * (d / 40f) * 0.6f;
            h = Mathf.Lerp(h, arena, aMask);

            // the road down the valley is slightly sunken and smoothed
            float r = RoadMask(x, z);
            if (r > 0f) h -= r * 0.35f;
            return Mathf.Clamp(h, 0f, MaxHeight - 1f);
        }

        /// <summary>Softens the mountain ranges (erosion-like) without touching the plateau.</summary>
        static void SmoothMountains(float[,] h)
        {
            int n = h.GetLength(0);
            var tmp = new float[n, n];
            const int R = 3;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int z = 0; z < n; z++)
                    for (int x = 0; x < n; x++)
                    {
                        float s = 0f; int c = 0;
                        for (int k = -R; k <= R; k++) { int xx = x + k; if (xx < 0 || xx >= n) continue; s += h[z, xx]; c++; }
                        tmp[z, x] = s / c;
                    }
                for (int z = 0; z < n; z++)
                    for (int x = 0; x < n; x++)
                    {
                        float s = 0f; int c = 0;
                        for (int k = -R; k <= R; k++) { int zz = z + k; if (zz < 0 || zz >= n) continue; s += tmp[zz, x]; c++; }
                        float blurred = s / c;
                        float wx = x / (float)(n - 1) * Size - Size * 0.5f, wz = z / (float)(n - 1) * Size - Size * 0.5f;
                        float d = Mathf.Sqrt(wx * wx + wz * wz);
                        float m = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(450f, 900f, d));
                        h[z, x] = Mathf.Lerp(h[z, x], blurred, m);
                    }
            }
        }

        // =================================================================== terrain

        /// <summary>bakeTint: URP's terrain shader does not apply diffuseRemapMax to the albedo, so a layer whose colour
        /// matters (the forest: the same meadow scan, dark green) gets the tint baked into its own copy of the diffuse.
        /// (The other layers' tints are no-ops in URP today; a later colour pass bakes them too.)</summary>
        static TerrainLayer MakeLayer(string name, GKDownloads.PbrSet set, float tile, Color tint, Texture2D fallback, bool bakeTint = false, bool rotate = false, float albedo = 0f)
        {
            string path = GKPaths.Generated + "/Environment/" + name + ".terrainlayer";
            var l = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (l == null) { l = new TerrainLayer(); AssetDatabase.CreateAsset(l, path); }
            l.diffuseTexture = set != null && set.diffuse != null ? set.diffuse : fallback;
            if (albedo > 0f) bakeTint = true;
            if (bakeTint && set != null && set.diffuse != null) l.diffuseTexture = BakedTint(name, set.diffuse, tint, rotate, albedo);
            l.normalMapTexture = set != null ? set.normal : null;
            l.maskMapTexture = set != null ? set.mask : null;
            l.tileSize = new Vector2(tile, tile);
            l.tileOffset = rotate ? new Vector2(tile * 0.43f, tile * 0.71f) : Vector2.zero;   // off the other copy's grid
            l.normalScale = 1f;
            l.diffuseRemapMin = Vector4.zero;
            l.diffuseRemapMax = bakeTint ? Vector4.one : new Vector4(tint.r, tint.g, tint.b, 1f);
            l.maskMapRemapMin = Vector4.zero;
            // the scans' smoothness (mask alpha) at a quarter: the low dawn sun's forward sheen lit the ground into the sun brighter
            // than any other view (Q2 p90 L* 76; 65 at smoothness 0, world_terraintest smooth=, 2026-10-31)
            l.maskMapRemapMax = new Vector4(1f, 1f, 1f, 0.25f);
            l.smoothness = 0.15f;
            l.metallic = 0f;
            EditorUtility.SetDirty(l);
            return l;
        }

        static Texture2D SolidTexture(string name, Color c)
        {
            string path = GKPaths.Generated + "/Environment/" + name + ".png";
            var t = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (t != null) return t;
            var tex = new Texture2D(64, 64, TextureFormat.RGBA32, true);
            var px = new Color[64 * 64];
            var rnd = new System.Random(5);
            for (int i = 0; i < px.Length; i++) { float n = (float)rnd.NextDouble() * 0.06f - 0.03f; px[i] = new Color(c.r + n, c.g + n, c.b + n, 1f); }
            tex.SetPixels(px); tex.Apply();
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), path), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static GameObject BuildTerrain()
        {
            EditorUtility.DisplayProgressBar("Georgian Knight", "Downloading terrain textures", 0.05f);
            var grass = GKDownloads.Texture("aerial_grass_rock");
            // dry trampled earth with grit, stones and foot prints (Poly Haven CC0). Not park_dirt: its autumn leaves are wrong for a
            // treeless saddle in mid-August (Team Lead's review, 2026-10-30). A base until Art's own ground set from the photos.
            var dirt = GKDownloads.Texture("rocks_ground_08");
            var rock = GKDownloads.Texture("aerial_rocks_02");
            var scree = GKDownloads.Texture("dry_ground_rocks");
            var snow = GKDownloads.Texture("snow_02");
            var fallback = SolidTexture("fallback_grey", new Color(0.5f, 0.5f, 0.5f));
            var snowFallback = SolidTexture("fallback_snow", new Color(0.92f, 0.94f, 0.97f));

            var layers = new[]
            {
                // tile sizes at the scans' real sizes, within ±25 % (Asset QA SCALE.TILE_SIZE, GK-21): aerial_grass_rock 15 m,
                // rocks_ground_08 3 m, aerial_rocks_02 50 m, dry_ground_rocks 4 m, snow_02 2 m
                // baked now (URP ignored the tints: L0 and L1 were the same scan, and their 15 / 17 m repeats beat into the stripes
                // across Q3's meadow, the Lead 2026-10-31). Less red: the straw hue from 76-82 toward the references' 84; L1 a
                // quarter-turned copy off L0's grid, the two alternating at ~20 m (dryW)
                // physical linear albedo where the site asks (Docs/World/ALBEDO.md; the Lead, 2026-10-31): dry meadow 0.22, earth 0.15,
                // rock 0.20, scree 0.18; the tints only set the hue then
                MakeLayer("L0_Meadow", grass, 15f, new Color(0.9f, 1f, 0.94f), fallback, true, false, Current.PhysicalAlbedo ? 0.22f : 0f),
                MakeLayer("L1_MeadowDry", grass, 17f, new Color(0.92f, 1f, 0.92f), fallback, true, true, Current.PhysicalAlbedo ? 0.22f : 0f),
                MakeLayer("L2_Dirt", dirt, 3f, Current.DirtTint, fallback, true, false, Current.PhysicalAlbedo ? 0.15f : 0f),   // baked: the raw scan read bleached grey into the sun (the Lead, Q2)
                MakeLayer("L3_Rock", rock, 42f, new Color(0.74f, 0.76f, 0.80f), fallback, false, false, Current.PhysicalAlbedo ? 0.20f : 0f),
                MakeLayer("L4_Scree", scree, 4.5f, new Color(0.6f, 0.58f, 0.54f), fallback, false, false, Current.PhysicalAlbedo ? 0.18f : 0f),   // pale cracked earth at 0.92 read as sand dunes on every slope (AR-1)
                MakeLayer("L5_Snow", snow.ok ? snow : null, 2.2f, Color.white, snowFallback),
            };
            if (Current.MacroColour)
            {
                // beech and hornbeam on the north slopes read as a dark green from the arena
                var list = new List<TerrainLayer>(layers);
                // a second, different earth at its own real size (3 m) breaks the repeat; the same set stretched broke the size rule
                // the floor under the canopy darker and cooler, so the wood reads as one dark mass at range, not tan between crowns
                // (Critics AR-1, 2026-10-31: forest reference L* 30, h 145, C* 10; 0.30/0.40/0.26 read as open woodland)
                // 0.19/0.25/0.20 still lit pale tan on the slopes facing the low sun at exposure 2.2 (forest 0.96-1.0 by world_splat):
                // a canopy stays dark in sun
                // 0.11/0.15/0.12 (baked in sRGB on the scan) went black in shade: 0.16/0.21/0.17
                // 0.16 x the scan (sRGB) is ~0.5 % linear: black in shade whatever the fill. A canopy floor at range ~3-4 %
                // forest litter 0.13 (the low end of 0.12-0.20: the floor between greybox crowns reads as the wood's mass at range)
                list.Add(MakeLayer("L6_Forest", grass, 18f, new Color(0.3f, 0.38f, 0.31f), fallback, true, false, Current.PhysicalAlbedo ? 0.13f : 0f));
                list.Add(MakeLayer("L7_DirtB", GKDownloads.Texture("dry_mud_field_001"), 3.4f, Current.DirtTint * 0.95f, fallback, false, false, Current.PhysicalAlbedo ? 0.15f : 0f));
                layers = list.ToArray();
                // only the first control map (layers 0-3) shows on these terrains in URP today; the second (4-7) never rendered
                // (world_splat found forest 0.45 on the rim, the frame showed none, 2026-10-31). The forest matters more at range
                // than bare rock, so it takes slot 3 and rock goes to 6, until the add-pass is understood
                var swap = layers[3]; layers[3] = layers[6]; layers[6] = swap;
            }
            int iRock = Current.MacroColour ? 6 : 3, iForest = 3;

            string tdPath = GKPaths.Generated + "/Environment/" + Current.TerrainAsset;
            var td = AssetDatabase.LoadAssetAtPath<TerrainData>(tdPath);
            if (td == null) { td = new TerrainData(); AssetDatabase.CreateAsset(td, tdPath); }
            td.heightmapResolution = HeightRes;
            td.size = new Vector3(Size, MaxHeight, Size);
            td.alphamapResolution = SplatRes;
            td.baseMapResolution = 1024;
            td.SetDetailResolution(DetailRes, 32);

            // ---- heights (metres): real Didgori elevation when the DEM is reachable, procedural otherwise
            EditorUtility.DisplayProgressBar("Georgian Knight", "Fetching Didgori elevation data", 0.08f);
            UseDem = GKDem.Prepare(InnerZoom, 3300f) && GKDem.Prepare(OuterZoom, OuterSize * 0.5f + 1500f);
            Size = UseDem ? 6000f : 4000f;
            float arenaH = UseDem ? ArenaElevation() : Plateau;
            var hm = new float[HeightRes, HeightRes];
            float minH = float.MaxValue, maxH = float.MinValue;
            for (int zi = 0; zi < HeightRes; zi++)
            {
                if ((zi & 63) == 0) EditorUtility.DisplayProgressBar("Georgian Knight", "Sculpting the Didgori plateau", 0.1f + 0.4f * zi / HeightRes);
                float z = zi / (float)(HeightRes - 1) * Size - Size * 0.5f;
                for (int xi = 0; xi < HeightRes; xi++)
                {
                    float x = xi / (float)(HeightRes - 1) * Size - Size * 0.5f;
                    float h = UseDem ? DemHeightAt(x, z, arenaH) : HeightAt(x, z);
                    hm[zi, xi] = h;
                }
            }
            if (!UseDem) SmoothMountains(hm);
            for (int zi = 0; zi < HeightRes; zi++)
                for (int xi = 0; xi < HeightRes; xi++) { minH = Mathf.Min(minH, hm[zi, xi]); maxH = Mathf.Max(maxH, hm[zi, xi]); }
            float range = Mathf.Max(60f, maxH - minH + 20f);
            td.size = new Vector3(Size, range, Size);
            var heights = new float[HeightRes, HeightRes];
            for (int zi = 0; zi < HeightRes; zi++)
                for (int xi = 0; xi < HeightRes; xi++) heights[zi, xi] = (hm[zi, xi] - minH) / range;
            BaseY = minH - arenaH;
            td.SetHeights(0, 0, heights);
            GKBridge.Log("terrain " + (UseDem ? "REAL Didgori DEM" : "procedural") + " size=" + Size + " arena=" + arenaH.ToString("F0") + "m range=" + minH.ToString("F0") + ".." + maxH.ToString("F0"));
            td.terrainLayers = layers;

            // ---- splat
            int L = layers.Length;
            var alpha = new float[SplatRes, SplatRes, L];
            var detail = new int[DetailRes, DetailRes];
            var stubble = new int[DetailRes, DetailRes];
            var shortGrass = new int[DetailRes, DetailRes];
            var strawTall = new int[DetailRes, DetailRes];
            var w = new float[L];
            for (int zi = 0; zi < SplatRes; zi++)
            {
                if ((zi & 63) == 0) EditorUtility.DisplayProgressBar("Georgian Knight", "Painting grass, rock and snow", 0.5f + 0.35f * zi / SplatRes);
                float nz = (zi + 0.5f) / SplatRes;
                float z = nz * Size - Size * 0.5f;
                for (int xi = 0; xi < SplatRes; xi++)
                {
                    float nx = (xi + 0.5f) / SplatRes;
                    float x = nx * Size - Size * 0.5f;
                    float slope = td.GetSteepness(nx, nz);
                    float h = td.GetInterpolatedHeight(nx, nz) + BaseY;
                    float d = Mathf.Sqrt(x * x + z * z);
                    float n1 = Fbm(x * 0.012f, z * 0.012f, 4);
                    float n2 = Fbm(x * 0.0021f + 40f, z * 0.0021f, 4);

                    float rockW = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(31f, 43f, slope + n1 * 4f));
                    float screeW = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(21f, 31f, slope + n1 * 5f)) * (1f - rockW);
                    float snowW = !Current.Snow ? 0f : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(UseDem ? 1150f : 640f, UseDem ? 1250f : 730f, h + n2 * 70f)) * (1f - 0.7f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(40f, 55f, slope)));
                    bool worn = Current.TrampledPaths != null;
                    float nHi = worn ? Fbm(x * 0.07f + 5f, z * 0.07f, 3) : 0f;   // a ragged edge, not a smooth blob
                    float arenaDirt = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(Current.DirtInner, Current.DirtOuter,
                                                                                         d + n1 * Mathf.Max(10f, (Current.DirtOuter - Current.DirtInner) * 0.6f) + nHi * 7f));
                    float trampled = (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(26f, 52f, d))) * Mathf.Clamp01(n1 * 1.8f + 0.2f);
                    float road = RoadMask(x, z);
                    float dirtW = Mathf.Max(Mathf.Max(arenaDirt, road), trampled * (worn ? 0.15f : 0.5f)) * (1f - rockW);
                    if (worn) dirtW = Mathf.Max(dirtW, PathMask(x, z, nHi) * 0.8f * (1f - rockW));
                    // the two meadow copies alternate at ~20 m so neither repeat grid reads (Q3 stripes)
                    float dryW = Mathf.Lerp(Mathf.Clamp01(n2 * 1.6f + 0.1f) * 0.45f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.12f, 0.12f, Fbm(x * 0.05f + 7f, z * 0.05f, 2))), 0.8f);
                    float forestW = 0f;
                    if (Current.MacroColour && d > 250f)
                    {
                        Vector3 nrm = td.GetInterpolatedNormal(nx, nz);
                        screeW *= 0.35f;   // vegetated slopes at range, not bare scree
                        // Trialeti cover (Critics AR-1 R2, 2026-10-31): dense in folds and on north faces, open on crests and south faces
                        // from 220 m (was 250-600: the near rim in Q3 had no woods in its folds)
                        forestW = ForestCover(x, z, slope, nrm, Concavity(td, nx, nz, 60f)) * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(220f, 380f, d)) * (1f - rockW);
                        // far meadow: mostly the green-brown layer, dry straw only on the sunniest south faces (the dry layer
                        // alone, sunlit and fogged, read as sand dunes: Critics AR-1)
                        dryW = Mathf.Lerp(dryW, Mathf.Clamp(0.1f - nrm.z * 0.45f + n2 * 0.35f, 0f, 0.5f), Mathf.InverseLerp(350f, 650f, d));
                    }

                    if (Current.HayMeadow) dirtW *= 0.55f;   // trampled hay meadow, not bare clay (Critics AR-1/AR-2)
                    float free = Mathf.Clamp01(1f - rockW - screeW - dirtW - forestW);
                    w[0] = free * (1f - dryW);
                    w[1] = free * dryW;
                    w[2] = dirtW;
                    w[iRock] = rockW;
                    w[4] = screeW;
                    w[5] = 0f;
                    if (Current.MacroColour)
                    {
                        w[iForest] = forestW;
                        float mixB = Mathf.Clamp01(Fbm(x * 0.045f + 3f, z * 0.045f, 2) * 1.2f + 0.5f) * 0.7f;
                        w[7] = dirtW * mixB; w[2] = dirtW * (1f - mixB);
                    }
                    // snow overrides at altitude
                    for (int i = 0; i < 5; i++) w[i] *= (1f - snowW);
                    w[5] = snowW;
                    float sum = 0f;
                    for (int i = 0; i < L; i++) sum += w[i];
                    if (sum < 1e-4f) { w[0] = 1f; sum = 1f; }
                    for (int i = 0; i < L; i++) alpha[zi, xi, i] = w[i] / sum;

                    // grass density (same resolution as the splat)
                    float grassAmt = (w[0] + w[1]) / sum;   // not the forest layer: trees, not meadow, from that far
                    if (h < (UseDem ? 1100f : 740f) && slope < 30f)
                    {
                        float clump = Mathf.Lerp(1f, Mathf.Clamp01(Fbm(x * 0.09f, z * 0.09f, 2) * 1.5f + 0.6f), Current.GrassClumping);
                        // with stubble the tall clumps thin out sooner and the short dry layer carries the edge, so the meadow fades
                        // into the trampled floor instead of standing as a band
                        float tall = Current.Stubble ? grassAmt * grassAmt * (worn ? 1f : Mathf.Sqrt(grassAmt)) : grassAmt * Mathf.Sqrt(grassAmt);
                        int dens = Mathf.RoundToInt(tall * clump * Current.GrassDensity);
                        float hollow = 0f;
                        if (Current.HayMeadow)
                        {
                            // August hay meadow (Critics AR-2): straw-gold, green only where water gathers. Hollow = a dip of more
                            // than ~15 cm against the ground 8 m around (at 18 m the whole bowl counted as one hollow: green everywhere)
                            float o = 8f / Size, around = 0.25f * (td.GetInterpolatedHeight(nx + o, nz) + td.GetInterpolatedHeight(nx - o, nz)
                                                                     + td.GetInterpolatedHeight(nx, nz + o) + td.GetInterpolatedHeight(nx, nz - o)) + BaseY;
                            hollow = Mathf.Clamp01((around - h - 0.15f) / 0.5f);
                            strawTall[zi, xi] = Mathf.Clamp(Mathf.RoundToInt(dens * 0.6f * (1f - hollow)), 0, 32);
                            dens = Mathf.RoundToInt(dens * 0.6f * (0.12f + 0.88f * hollow));
                        }
                        detail[zi, xi] = Mathf.Clamp(dens, 0, 32);
                        if (Current.Stubble)
                        {
                            float stub = Mathf.Clamp01(0.25f + 3f * grassAmt * (1f - grassAmt)) * (1f - grassAmt * grassAmt);
                            float patch = Mathf.Clamp01(Fbm(x * 0.21f + 7f, z * 0.21f, 2) * 1.4f + 0.55f);
                            // worn ground: straw through the whole meadow too, between the clumps, so nothing reads as planted
                            if (worn) stub = Mathf.Max(stub, grassAmt * 0.35f);
                            if (Current.HayMeadow) stub = Mathf.Max(stub, 0.6f + grassAmt * 0.4f);    // the dense low straw cover, floor included
                            stubble[zi, xi] = Mathf.Clamp(Mathf.RoundToInt(stub * patch * 16f), 0, Current.HayMeadow ? 10 : 16);   // hay meadow: the grass budget (Build & QA)
                            if (worn)
                            {
                                // short green grass under and between the tall clumps
                                float under = Mathf.Clamp01(Fbm(x * 0.13f + 21f, z * 0.13f, 2) * 1.3f + 0.6f);
                                float green = Current.HayMeadow ? hollow : 1f;
                                shortGrass[zi, xi] = Mathf.Clamp(Mathf.RoundToInt(grassAmt * under * green * 12f), 0, 8);
                            }
                        }
                    }
                }
            }
            td.SetAlphamaps(0, 0, alpha);
            if (Current.GreyTrees && Current.MacroColour)
                PlaceTrees(td, alpha, iForest, SplatRes, Size, 40f, 300f, Size * 0.5f, false);

            // ---- grass (instanced mesh details)
            var grassProto = GKProps.GrassClumpPrefab();
            if (grassProto != null)
            {
                var dp = new DetailPrototype
                {
                    prototype = grassProto,
                    usePrototypeMesh = true,
                    useInstancing = true,
                    renderMode = DetailRenderMode.VertexLit,
                    minWidth = 1.0f, maxWidth = 1.7f,
                    minHeight = 0.8f, maxHeight = 1.45f,
                    noiseSeed = Seed, noiseSpread = 0.35f,
                    healthyColor = Color.white, dryColor = new Color(0.86f, 0.82f, 0.62f),
                    alignToGround = 0.6f,
                    positionJitter = 1f,
                };
                if (Current.Stubble)
                {
                    // August stubble and grass litter: the same clump, ankle high, straw coloured
                    var sp = new DetailPrototype
                    {
                        prototype = GKProps.StubblePrefab(),
                        usePrototypeMesh = true,
                        useInstancing = true,
                        renderMode = DetailRenderMode.VertexLit,
                        minWidth = 0.45f, maxWidth = 0.9f,
                        minHeight = Current.HayMeadow ? 0.2f : 0.16f, maxHeight = Current.HayMeadow ? 0.45f : 0.34f,
                        noiseSeed = Seed + 1, noiseSpread = 0.5f,
                        healthyColor = new Color(0.84f, 0.76f, 0.52f), dryColor = new Color(0.68f, 0.6f, 0.42f),
                        alignToGround = 0.9f,
                        positionJitter = 1f,
                    };
                    if (Current.TrampledPaths != null)
                    {
                        var gp = new DetailPrototype
                        {
                            prototype = grassProto,
                            usePrototypeMesh = true,
                            useInstancing = true,
                            renderMode = DetailRenderMode.VertexLit,
                            minWidth = 0.6f, maxWidth = 1.1f,
                            minHeight = 0.3f, maxHeight = 0.55f,
                            noiseSeed = Seed + 2, noiseSpread = 0.4f,
                            healthyColor = Color.white, dryColor = Color.white,
                            alignToGround = 0.8f,
                            positionJitter = 1f,
                        };
                        if (Current.HayMeadow)
                        {
                            var tp = new DetailPrototype
                            {
                                prototype = GKProps.StubblePrefab(),
                                usePrototypeMesh = true,
                                useInstancing = true,
                                renderMode = DetailRenderMode.VertexLit,
                                minWidth = 0.8f, maxWidth = 1.3f,
                                minHeight = 0.55f, maxHeight = 0.95f,
                                noiseSeed = Seed + 3, noiseSpread = 0.35f,
                                healthyColor = Color.white, dryColor = Color.white,
                                alignToGround = 0.6f,
                                positionJitter = 1f,
                            };
                            td.detailPrototypes = new[] { dp, sp, gp, tp };
                        }
                        else td.detailPrototypes = new[] { dp, sp, gp };
                    }
                    else td.detailPrototypes = new[] { dp, sp };
                }
                else td.detailPrototypes = new[] { dp };
                td.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);
                td.SetDetailLayer(0, 0, 0, detail);
                if (Current.Stubble) td.SetDetailLayer(0, 0, 1, stubble);
                if (Current.Stubble && Current.TrampledPaths != null) td.SetDetailLayer(0, 0, 2, shortGrass);
                if (Current.Stubble && Current.TrampledPaths != null && Current.HayMeadow) td.SetDetailLayer(0, 0, 3, strawTall);
            }
            EditorUtility.SetDirty(td);
            AssetDatabase.SaveAssets();

            var go = Terrain.CreateTerrainGameObject(td);
            go.name = Current.TerrainName;
            go.layer = 10;
            go.isStatic = true;
            go.transform.position = new Vector3(-Size * 0.5f, BaseY, -Size * 0.5f);
            var t = go.GetComponent<Terrain>();
            ActiveTerrain = t;
            t.heightmapPixelError = 3f;
            t.basemapDistance = 30000f;   // the generated basemap reads smoothness from diffuse alpha and bleaches distant slopes
            t.drawInstanced = true;
            t.detailObjectDistance = Current.DetailDistance;
            t.treeDistance = 0f;   // the greybox trees are drawn by GK.World GreyForest, not the terrain's tree pass
            t.detailObjectDensity = 1f;
            t.shadowCastingMode = ShadowCastingMode.TwoSided;
            t.allowAutoConnect = false;
            t.materialTemplate = TerrainMaterial();
            return go;
        }

        static float ArenaElevation()
        {
            float s = 0f; int n = 0;
            for (float x = -40f; x <= 40f; x += 8f)
                for (float z = -40f; z <= 40f; z += 8f)
                    if (x * x + z * z <= 1600f) { s += GKDem.Elevation(InnerZoom, x, z); n++; }
            return s / Mathf.Max(1, n);
        }

        static float DemHeightAt(float x, float z, float arenaH)
        {
            float e = GKDem.Elevation(InnerZoom, x, z);
            float edge = Mathf.Max(Mathf.Abs(x), Mathf.Abs(z));
            float eb = Mathf.Clamp01(Mathf.InverseLerp(Size * 0.5f - 400f, Size * 0.5f - 4f, edge));
            eb = eb * eb * (3f - 2f * eb);
            if (eb > 0f) e = Mathf.Lerp(e, GKDem.Elevation(OuterZoom, x, z), eb);
            // micro relief the 15-30 m source can't hold (softer on a site's fight floor, whose slope bar is 3 degrees)
            float d = Mathf.Sqrt(x * x + z * z);
            float relief = Mathf.Lerp(Current.FloorRelief, 1f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(Current.FlattenOuter, Current.FlattenOuter + 40f, d)));
            e += (Fbm(x * 0.016f, z * 0.016f, 4) * 2.4f + Fbm(x * 0.07f, z * 0.07f, 3) * 0.45f) * (1f - eb) * relief;
            // the arena: a broad, nearly level tableland on the summit of Didgori
            float a = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(Current.FlattenInner, Current.FlattenOuter, d));
            float arena = arenaH + Fbm(x * 0.05f, z * 0.05f, 3) * 0.3f + (d / 45f) * (d / 45f) * 0.5f;
            return Mathf.Lerp(e, arena, a);
        }

        /// <summary>40 km of real landscape around the play area (coarse, lowered under the main terrain).</summary>
        /// <summary>1 on slopes of 6-35 degrees (where the woods stand), 0 on the flats and ridge tops (the meadows) and on cliffs.</summary>
        static float SlopeForest(float slope)
        {
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(4f, 9f, slope)) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(38f, 46f, slope)));
        }

        /// <summary>Forest cover on the slopes around Didgori: on north-facing slopes of 8-35 degrees, in patches, thinning above
        /// ~250 m over the arena and below ~900 m under it (h is relative to the arena floor).</summary>
        static float ForestWeight(float x, float z, float slope, Vector3 nrm, float h)
        {
            // the Trialeti references show forest over most slopes, open dry meadow on the ridge tops and the gentle ground;
            // north faces densest (REFERENCES_meadow.md, July 2023)
            float north = Mathf.Clamp01((nrm.z + 0.6f) / 0.9f);
            float slopeOk = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(4f, 10f, slope)) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(32f, 40f, slope)));
            float band = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-900f, -600f, h)) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(150f, 350f, h)));
            float patch = Fbm(x * 0.004f + 11f, z * 0.004f, 3);
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.42f, 0.7f, patch * 0.6f + north * 0.65f)) * slopeOk * band;
        }

        /// <summary>Metres the ground lies below the mean of a ring r metres round it: positive in folds and ravines, negative on
        /// crests and spurs.</summary>
        static float Concavity(TerrainData td, float nx, float nz, float r)
        {
            float sx = r / td.size.x, sz = r / td.size.z, s = 0f;
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI * 0.25f;
                s += td.GetInterpolatedHeight(Mathf.Clamp01(nx + Mathf.Cos(a) * sx), Mathf.Clamp01(nz + Mathf.Sin(a) * sz));
            }
            return s / 8f - td.GetInterpolatedHeight(nx, nz);
        }

        /// <summary>Trialeti forest cover (Critics AR-1 R2, 2026-10-31; REFERENCES_meadow.md §D): continuous dark woods in the
        /// folds, ravines and on north faces below the crests; crests, spurs and south faces open meadow; a ragged edge along the
        /// contour. concavity: Concavity() in metres (+ fold, - crest). 0..1.</summary>
        static float ForestCover(float x, float z, float slope, Vector3 nrm, float concavity)
        {
            float slopeOk = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(3f, 8f, slope)) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(40f, 48f, slope)));
            float north = Mathf.Clamp01((nrm.z + 0.6f) / 1.2f);           // 0 on a south face, 1 on a north face (+z)
            float fold = Mathf.Clamp(concavity / 18f, -1f, 1f);          // +1 in an 18 m fold, -1 on an 18 m crest (8 m: every rounded shoulder read as crest)
            float edge = Fbm(x * 0.02f + 3f, z * 0.02f, 3) * 0.25f;      // the ragged contour edge
            // first pass (0.45 + 0.35 north + 0.55 fold) left most slopes bare (23k trees, the hills tan): slopes wooded by default
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 0.6f, 0.6f + 0.3f * north + 0.6f * fold + edge)) * slopeOk;
        }

        /// <summary>The worn tracks of Site.TrampledPaths: from the centre along a bearing, narrowing to their far end, edges
        /// broken by noise. 1 on a track, 0 off it.</summary>
        static float PathMask(float x, float z, float noise)
        {
            float best = 0f;
            foreach (var p in Current.TrampledPaths)
            {
                float a = p.x * Mathf.Deg2Rad, len = p.y;
                float dx = Mathf.Sin(a), dz = Mathf.Cos(a);
                float t = Mathf.Clamp(x * dx + z * dz, 0f, len);
                float px = x - dx * t, pz = z - dz * t;
                float dist = Mathf.Sqrt(px * px + pz * pz) + noise * 1.5f;
                float half = p.z * 0.5f * (1f - 0.35f * t / len);
                float m = (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(half, half + 2.5f, dist))) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(len * 0.8f, len, t)));
                best = Mathf.Max(best, m);
            }
            return best;
        }

        /// albedo > 0: the result is scaled in linear space to that mean linear luminance (physical albedo, Docs/World/ALBEDO.md); the
        /// tint then only sets the hue. (A tint multiplied in sRGB went far darker than it read: 0.3 is ~0.07 linear.)
        static Texture2D BakedTint(string name, Texture2D src, Color tint, bool rotate = false, float albedo = 0f)
        {
            string path = GKPaths.Generated + "/Environment/T_" + name + "_BaseColor.png";
            var ti = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(src)) as TextureImporter;
            if (ti != null && !ti.isReadable) { ti.isReadable = true; ti.SaveAndReimport(); }
            var px = src.GetPixels();
            if (albedo > 0f)
            {
                Color tl = tint.linear;
                double sum = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    var c = px[i].linear;
                    px[i] = new Color(c.r * tl.r, c.g * tl.g, c.b * tl.b, px[i].a);
                    sum += 0.2126 * px[i].r + 0.7152 * px[i].g + 0.0722 * px[i].b;
                }
                float k = albedo / Mathf.Max(1e-5f, (float)(sum / px.Length));
                for (int i = 0; i < px.Length; i++)
                    px[i] = new Color(Mathf.Clamp01(px[i].r * k), Mathf.Clamp01(px[i].g * k), Mathf.Clamp01(px[i].b * k), px[i].a).gamma;
                GKBridge.Log("environment: " + name + " baked to linear albedo " + albedo.ToString("F2") + " (x" + k.ToString("F2") + ")");
            }
            else
                for (int i = 0; i < px.Length; i++) px[i] = new Color(px[i].r * tint.r, px[i].g * tint.g, px[i].b * tint.b, px[i].a);
            if (rotate && src.width == src.height)   // a quarter turn: the same scan no longer repeats on the same grid
            {
                int w = src.width; var rot = new Color[px.Length];
                for (int y = 0; y < w; y++) for (int x = 0; x < w; x++) rot[x * w + (w - 1 - y)] = px[y * w + x];
                px = rot;
            }
            var t = new Texture2D(src.width, src.height, TextureFormat.RGBA32, true);
            t.SetPixels(px); t.Apply();
            System.IO.File.WriteAllBytes(System.IO.Path.GetFullPath(path), t.EncodeToPNG());
            Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            return tex != null ? tex : src;
        }

        /// <summary>Greybox trees on the forest layer: a jittered grid (spacing metres), a tree where the forest weight is at
        /// least 0.6, between minR and maxR from the arena centre (square = the far tile, whose 0..size coordinates start at
        /// -size/2). Sizes 0.75-1.3, random turn, a slight shade variation. Logs the count.</summary>
        static void PlaceTrees(TerrainData td, float[,,] alpha, int layer, int res, float size, float spacing, float minR, float maxR, bool far)
        {
            var proto = GKProps.GreyTreePrefab();
            if (proto == null) return;
            td.treePrototypes = new[] { new TreePrototype { prefab = proto } };
            var trees = new List<TreeInstance>();
            var rnd = new System.Random(far ? 2207 : 1121);
            float half = size * 0.5f;
            for (float z = -maxR; z <= maxR; z += spacing)
                for (float x = -maxR; x <= maxR; x += spacing)
                {
                    float jx = x + ((float)rnd.NextDouble() - 0.5f) * spacing * 0.8f, jz = z + ((float)rnd.NextDouble() - 0.5f) * spacing * 0.8f;
                    float r = Mathf.Sqrt(jx * jx + jz * jz);
                    if (r < minR || r > maxR) continue;
                    if (far && Mathf.Abs(jx) < minR + 100f && Mathf.Abs(jz) < minR + 100f) continue;   // under the arena tile
                    float nx = (jx + half) / size, nz = (jz + half) / size;
                    if (nx <= 0f || nx >= 1f || nz <= 0f || nz >= 1f) continue;
                    int ax = Mathf.Clamp((int)(nx * res), 0, res - 1), az = Mathf.Clamp((int)(nz * res), 0, res - 1);
                    if (alpha[az, ax, layer] < 0.6f) continue;
                    float s = 0.75f + (float)rnd.NextDouble() * 0.55f, shade = 0.8f + (float)rnd.NextDouble() * 0.2f;
                    trees.Add(new TreeInstance
                    {
                        position = new Vector3(nx, td.GetInterpolatedHeight(nx, nz) / td.size.y, nz),
                        widthScale = s, heightScale = s * (0.9f + (float)rnd.NextDouble() * 0.3f),
                        rotation = (float)rnd.NextDouble() * Mathf.PI * 2f,
                        color = new Color(shade, shade, shade, 1f), lightmapColor = Color.white, prototypeIndex = 0,
                    });
                }
            td.SetTreeInstances(trees.ToArray(), true);
            GKBridge.Log((far ? "far ranges" : "arena terrain") + ": " + trees.Count + " greybox trees on the forest layer");
        }

        /// <summary>GK.World's GreyForest under the Environment root: it draws the terrains' greybox trees instanced in chunks
        /// (the terrain's own tree pass cost +28 ms CPU for 41k, 2026-10-31). Added by type name: GK.Editor does not reference GK.World.</summary>
        static void AddGreyForest()
        {
            System.Type type = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                if ((type = asm.GetType("GeorgianKnight.World.GreyForest")) != null) break;
            if (type == null) { GKBridge.Log("environment: GK.World GreyForest missing, the greybox trees are not drawn"); return; }
            var go = new GameObject("GreyForest");
            go.transform.SetParent(Root.transform, false);
            go.AddComponent(type);
        }

        /// <summary>The sky-visibility maps of the built terrains (inner and far), and GK.World's SkyVisibility under the root to
        /// publish them as shader globals (the Lead's decision, 2026-10-31: the terrain occludes the sky light).</summary>
        static void BakeSkyVisibility()
        {
            var terrains = Root.GetComponentsInChildren<Terrain>();
            if (terrains.Length == 0) return;
            System.Array.Sort(terrains, (a, b) => a.terrainData.size.x.CompareTo(b.terrainData.size.x));
            Vector4 innerST, farST = Vector4.zero;
            var inner = SkyVisibilityMap(terrains[0], 512, 1500f, "T_SkyVis_Inner", out innerST);
            Texture2D far = terrains.Length > 1 ? SkyVisibilityMap(terrains[terrains.Length - 1], 512, 8000f, "T_SkyVis_Far", out farST) : null;
            EditorUtility.ClearProgressBar();
            System.Type type = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                if ((type = asm.GetType("GeorgianKnight.World.SkyVisibility")) != null) break;
            if (type == null) { GKBridge.Log("environment: GK.World SkyVisibility missing, the sky occlusion maps are not published"); return; }
            var go = new GameObject("Sky Visibility");
            go.transform.SetParent(Root.transform, false);
            var c = go.AddComponent(type);
            var so = new SerializedObject(c);
            so.FindProperty("Inner").objectReferenceValue = inner;
            so.FindProperty("InnerST").vector4Value = innerST;
            so.FindProperty("Far").objectReferenceValue = far;
            so.FindProperty("FarST").vector4Value = farST;
            so.ApplyModifiedPropertiesWithoutUndo();
            type.GetMethod("Apply").Invoke(c, null);
        }

        /// <summary>Cosine-weighted sky visibility of the up normal per texel: V = 1 - mean(sin^2 h) over 16 azimuths, h the horizon
        /// angle marched out to maxDist (24 steps, geometric) from 1 m above the ground. Flat open ground = 1 (the meadow keeps its
        /// brightness); folds and ravines see less sky. Beyond the tile's edge the march stops (no horizon there). Linear R8, no
        /// mips, clamped; st maps world XZ to its UV (xy = 1 / size, zw = -origin / size).</summary>
        static Texture2D SkyVisibilityMap(Terrain t, int res, float maxDist, string name, out Vector4 st)
        {
            var td = t.terrainData;
            int hr = td.heightmapResolution;
            var hm = td.GetHeights(0, 0, hr, hr);
            float sx = td.size.x, sy = td.size.y, sz = td.size.z;
            System.Func<float, float, float> height = (u, v) =>
            {
                float fx = u * (hr - 1), fz = v * (hr - 1);
                int x0 = (int)fx, z0 = (int)fz, x1 = Mathf.Min(x0 + 1, hr - 1), z1 = Mathf.Min(z0 + 1, hr - 1);
                float tx = fx - x0, tz = fz - z0;
                return Mathf.Lerp(Mathf.Lerp(hm[z0, x0], hm[z0, x1], tx), Mathf.Lerp(hm[z1, x0], hm[z1, x1], tx), tz) * sy;
            };
            const int azimuths = 16, steps = 24;
            var dirs = new Vector2[azimuths];
            for (int a = 0; a < azimuths; a++) dirs[a] = new Vector2(Mathf.Cos(a * Mathf.PI * 2f / azimuths), Mathf.Sin(a * Mathf.PI * 2f / azimuths));
            var dist = new float[steps];
            float minD = Mathf.Max(sx / res, 4f);
            for (int k = 0; k < steps; k++) dist[k] = minD * Mathf.Pow(maxDist / minD, k / (float)(steps - 1));
            var px = new Color32[res * res];
            double sum = 0; float vmin = 1f;
            for (int z = 0; z < res; z++)
            {
                if ((z & 31) == 0) EditorUtility.DisplayProgressBar("Georgian Knight", "Baking " + name, (float)z / res);
                float v = (z + 0.5f) / res;
                for (int x = 0; x < res; x++)
                {
                    float u = (x + 0.5f) / res, h0 = height(u, v) + 1f, s2 = 0f;
                    for (int a = 0; a < azimuths; a++)
                    {
                        float best = 0f;
                        for (int k = 0; k < steps; k++)
                        {
                            float uu = u + dirs[a].x * dist[k] / sx, vv = v + dirs[a].y * dist[k] / sz;
                            if (uu < 0f || uu > 1f || vv < 0f || vv > 1f) break;
                            float tan = (height(uu, vv) - h0) / dist[k];
                            if (tan > best) best = tan;
                        }
                        s2 += best * best / (1f + best * best);   // sin^2 of the horizon angle
                    }
                    float vis = 1f - s2 / azimuths;
                    byte b = (byte)Mathf.RoundToInt(Mathf.Clamp01(vis) * 255f);
                    px[z * res + x] = new Color32(b, b, b, 255);
                    sum += vis; vmin = Mathf.Min(vmin, vis);
                }
            }
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false, true);
            tex.SetPixels32(px); tex.Apply();
            string path = GKPaths.Generated + "/Environment/" + name + ".png";
            System.IO.File.WriteAllBytes(System.IO.Path.GetFullPath(path), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = false;
            ti.mipmapEnabled = false;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Bilinear;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            var ps = ti.GetDefaultPlatformTextureSettings();
            ps.format = TextureImporterFormat.R8;
            ti.SetPlatformTextureSettings(ps);
            ti.SaveAndReimport();
            Vector3 o = t.transform.position;
            st = new Vector4(1f / sx, 1f / sz, -o.x / sx, -o.z / sz);
            GKBridge.Log("environment: " + name + " " + res + "x" + res + " over " + sx.ToString("F0") + " m, mean " + (sum / (res * res)).ToString("F3") + ", min " + vmin.ToString("F3"));
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static GameObject BuildOuterTerrain(TerrainLayer[] layers)
        {
            const int res = 1025;
            float arenaH = ArenaElevation();
            var hm = new float[res, res];
            float minH = float.MaxValue, maxH = float.MinValue;
            float inner = Size * 0.5f - 25f;
            for (int zi = 0; zi < res; zi++)
            {
                if ((zi & 31) == 0) EditorUtility.DisplayProgressBar("Georgian Knight", "Building the far ranges", 0.5f * zi / res);
                float z = zi / (float)(res - 1) * OuterSize - OuterSize * 0.5f;
                for (int xi = 0; xi < res; xi++)
                {
                    float x = xi / (float)(res - 1) * OuterSize - OuterSize * 0.5f;
                    float e = GKDem.Elevation(OuterZoom, x, z);
                    if (float.IsNaN(e)) e = arenaH - 300f;
                    if (Mathf.Abs(x) < inner && Mathf.Abs(z) < inner) e -= 40f;
                    if (Current.FarEdgeFade > 0f)
                    {
                        // the tile's cut edge stood against the sky as one straight pale line (a backdrop's edge): sink the last
                        // kilometres, measured from the centre so no square corner shows, and let nearer ridges make the skyline
                        float toEdge = OuterSize * 0.5f - Mathf.Sqrt(x * x + z * z);
                        e = Mathf.Lerp(arenaH - 900f, e, Mathf.SmoothStep(0f, 1f, toEdge / Current.FarEdgeFade));
                    }
                    hm[zi, xi] = e;
                    minH = Mathf.Min(minH, e); maxH = Mathf.Max(maxH, e);
                }
            }
            float range = Mathf.Max(60f, maxH - minH + 20f);
            string tdPath = GKPaths.Generated + "/Environment/" + Current.FarAsset;
            var td = AssetDatabase.LoadAssetAtPath<TerrainData>(tdPath);
            if (td == null) { td = new TerrainData(); AssetDatabase.CreateAsset(td, tdPath); }
            td.heightmapResolution = res;
            td.size = new Vector3(OuterSize, range, OuterSize);
            var heights = new float[res, res];
            for (int zi = 0; zi < res; zi++) for (int xi = 0; xi < res; xi++) heights[zi, xi] = (hm[zi, xi] - minH) / range;
            td.SetHeights(0, 0, heights);
            td.terrainLayers = layers;
            const int ar = 1024;
            td.alphamapResolution = ar;
            td.baseMapResolution = 2048;
            var alpha = new float[ar, ar, layers.Length];
            for (int zi = 0; zi < ar; zi++)
            {
                float nz = (zi + 0.5f) / ar;
                for (int xi = 0; xi < ar; xi++)
                {
                    float nx = (xi + 0.5f) / ar;
                    float slope = td.GetSteepness(nx, nz);
                    float x = nx * OuterSize, z = nz * OuterSize;
                    float n1 = Fbm(x * 0.0015f, z * 0.0015f, 3);
                    float rockW = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(24f, 36f, slope + n1 * 6f));
                    float screeW = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(15f, 24f, slope + n1 * 6f)) * (1f - rockW);
                    float dry = Mathf.Clamp01(n1 * 1.4f + 0.35f);
                    float forestW = 0f;
                    if (Current.MacroColour)
                    {
                        // not dunes: dry-green and brown meadow, dark forest on the north faces, rock out on the ridges
                        Vector3 nrm = td.GetInterpolatedNormal(nx, nz);
                        float h = td.GetInterpolatedHeight(nx, nz) + minH - arenaH;
                        // rock only on cliffs: the Trialeti slopes are wooded to 35-40 deg; at 20-30 deg the tan rock layer covered every
                        // slope the arena sees and read as sand dunes (world_splat: rock 0.97 on a 29 deg horizon face, 2026-10-31)
                        rockW = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(34f, 44f, slope + n1 * 6f));
                        screeW = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(16f, 26f, slope + n1 * 6f)) * (1f - rockW) * 0.3f;
                        forestW = ForestCover(x, z, slope, nrm, Concavity(td, nx, nz, 150f)) * (1f - rockW);   // Trialeti cover: folds and north faces
                        dry = Mathf.Clamp(0.1f - nrm.z * 0.45f + n1 * 0.4f, 0f, 0.5f);
                    }
                    float free = Mathf.Clamp01(1f - rockW - screeW - forestW);
                    alpha[zi, xi, 0] = free * (1f - dry);
                    alpha[zi, xi, 1] = free * dry;
                    alpha[zi, xi, Current.MacroColour ? 6 : 3] = rockW;
                    alpha[zi, xi, 4] = screeW;
                    if (Current.MacroColour) alpha[zi, xi, 3] = forestW;   // slot 3: see BuildTerrain (only control map 1 renders)
                }
            }
            td.SetAlphamaps(0, 0, alpha);
            if (Current.GreyTrees && Current.MacroColour)
                PlaceTrees(td, alpha, 3, ar, OuterSize, 75f, Size * 0.5f - 100f, 8000f, true);
            EditorUtility.SetDirty(td);
            AssetDatabase.SaveAssets();
            var go = Terrain.CreateTerrainGameObject(td);
            go.name = "Didgori Far Ranges";
            go.layer = 10;
            go.isStatic = true;
            go.transform.position = new Vector3(-OuterSize * 0.5f, minH - arenaH, -OuterSize * 0.5f);
            var t = go.GetComponent<Terrain>();
            t.heightmapPixelError = 5f;
            t.basemapDistance = 60000f;
            t.treeDistance = 0f;   // the greybox trees are drawn by GK.World GreyForest, not the terrain's tree pass
            t.drawInstanced = true;
            t.shadowCastingMode = ShadowCastingMode.Off;
            t.allowAutoConnect = false;
            t.materialTemplate = TerrainMaterial();
            var col = go.GetComponent<TerrainCollider>();
            if (col != null) Object.DestroyImmediate(col);
            GKBridge.Log("far ranges " + minH.ToString("F0") + ".." + maxH.ToString("F0") + " m");
            return go;
        }

        static Material TerrainMaterial()
        {
            // a site with sky occlusion gets its own material on the GK shader; the others keep URP's TerrainLit
            var gk = Current.SkyOcclusion ? Shader.Find("GK/Terrain Lit (Sky Occlusion)") : null;
            if (Current.SkyOcclusion && gk == null) GKBridge.Log("environment: GK/Terrain Lit (Sky Occlusion) missing, URP TerrainLit used");
            string path = GKPaths.Generated + "/Environment/" + (gk != null ? "TerrainLit_SkyOcclusion.mat" : "TerrainLit.mat");
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                var sh = gk != null ? gk : Shader.Find("Universal Render Pipeline/Terrain/Lit");
                m = new Material(sh);
                AssetDatabase.CreateAsset(m, path);
            }
            // URP's terrain height blend works for four layers or fewer; with more (the arena has 8, Didgori 6) the layers past
            // the first control map never showed (the darker scree, the forest, the second earth: 2026-10-31). Off.
            m.SetFloat("_EnableHeightBlend", 0f);
            m.DisableKeyword("_TERRAIN_BLEND_HEIGHT");
            m.SetFloat("_HeightTransition", 0.35f);
            m.SetFloat("_EnableInstancedPerPixelNormal", 1f);
            m.EnableKeyword("_TERRAIN_INSTANCED_PERPIXEL_NORMAL");
            m.EnableKeyword("_MASKMAP");
            m.EnableKeyword("_NORMALMAP");
            EditorUtility.SetDirty(m);
            return m;
        }

        public static Terrain ActiveTerrain;

        public static float SampleHeight(Vector3 p)
        {
            var t = ActiveTerrain != null ? ActiveTerrain : Terrain.activeTerrain;
            if (t == null) return 0f;
            return t.SampleHeight(p) + t.transform.position.y;
        }

        // =================================================================== sky, sun, fog

        public static Light Sun;

        /// <summary>The sun as the eye sees it at a low dawn (the HDRI's own sun is a broad glow, no disk): a URP lens flare on
        /// the Sun light, a small bright disk and a warm halo, occluded by terrain and figures. Sizes from the Site.</summary>
        static void AddSunDisk(GameObject sunGo)
        {
            string path = GKPaths.Generated + "/Environment/LF_SunDisk.asset";
            var data = AssetDatabase.LoadAssetAtPath<LensFlareDataSRP>(path);
            if (data == null) { data = ScriptableObject.CreateInstance<LensFlareDataSRP>(); AssetDatabase.CreateAsset(data, path); }
            data.elements = new[]
            {
                SunElement(Current.SunDisk, 6f, new Color(1f, 0.93f, 0.78f), 0.9f, 0.85f),     // the disk: bright enough to read on the dawn sky, a soft rim
                SunElement(Current.SunHalo, 0.6f, new Color(1f, 0.66f, 0.42f), 0.3f, 1f),      // the halo: warm, all gradient (edge 0 drew a flat disc)
            };
            EditorUtility.SetDirty(data);
            var lf = sunGo.AddComponent<LensFlareComponentSRP>();
            lf.lensFlareData = data;
            lf.intensity = 1f; lf.scale = 1f;
            lf.useOcclusion = true; lf.occlusionRadius = 0.05f;
            lf.attenuationByLightShape = false; lf.allowOffScreen = false;
        }

        static LensFlareDataElementSRP SunElement(float size, float intensity, Color tint, float fallOff, float edge)
        {
            return new LensFlareDataElementSRP
            {
                flareType = SRPLensFlareType.Circle, position = 0f, uniformScale = size, sizeXY = Vector2.one,
                localIntensity = intensity, tint = tint, fallOff = fallOff, edgeOffset = edge,
                blendMode = SRPLensFlareBlendMode.Additive, modulateByLightColor = false, preserveAspectRatio = true,
            };
        }

        /// <summary>World direction of a sky-space direction once Skybox/Panoramic is rotated by degrees around Y. The shader
        /// rotates the sky mesh and samples with the unrotated vertex, so world = R(+deg) * sample. (GKDownloads.SkyToWorld,
        /// Engine &amp; Tools', has R(-deg): it drew the HDRI's sun at az 171 in the Didgori arena while the light came from 83.)</summary>
        static Vector3 SkyToWorld(Vector3 s, float degrees)
        {
            float a = degrees * Mathf.Deg2Rad;
            float c = Mathf.Cos(a), si = Mathf.Sin(a);
            return new Vector3(c * s.x - si * s.z, s.y, si * s.x + c * s.z);
        }

        internal static void BuildSkyAndLight()
        {
            EditorUtility.DisplayProgressBar("Georgian Knight", "Downloading sky", 0.86f);
            string hdr = GKDownloads.Hdri(Current.Hdri, "4k");
            var skyPath = GKPaths.Generated + "/Environment/" + Current.SkyMaterial;
            var sky = AssetDatabase.LoadAssetAtPath<Material>(skyPath);
            if (sky == null) { sky = new Material(Shader.Find("Skybox/Panoramic")); AssetDatabase.CreateAsset(sky, skyPath); }
            float rotation = 0f;
            Vector3 sunDir = Quaternion.Euler(-38f, Current.SunAzimuth, 0f) * Vector3.forward;
            if (hdr != null)
            {
                sky.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture>(hdr));
                Vector2 uv;
                if (GKDownloads.FindSunUV(hdr, out uv))
                {
                    Vector3 s = GKDownloads.PanoramicDir(uv);
                    float best = 1e9f;
                    for (float deg = 0f; deg < 360f; deg += 0.25f)
                    {
                        Vector3 wd = SkyToWorld(s, deg);
                        float az = Mathf.Atan2(wd.x, wd.z) * Mathf.Rad2Deg;
                        float err = Mathf.Abs(Mathf.DeltaAngle(az, Current.SunAzimuth));
                        if (err < best) { best = err; rotation = deg; }
                    }
                    sunDir = SkyToWorld(s, rotation).normalized;
                    GKBridge.Log("HDRI sun uv=" + uv + " elevation=" + (Mathf.Asin(sunDir.y) * Mathf.Rad2Deg).ToString("F1") + " sky rotation=" + rotation);
                }
            }
            sky.SetFloat("_Mapping", 1f);
            sky.SetFloat("_ImageType", 0f);
            sky.SetFloat("_Exposure", Current.SkyExposure);
            sky.SetFloat("_Rotation", rotation);
            var graded = Current.SkyAwayGain > 1f ? Shader.Find("GK/SkyPanoramicGraded") : null;
            sky.shader = graded != null ? graded : Shader.Find("Skybox/Panoramic");
            if (graded != null) { sky.SetFloat("_AwayGain", Current.SkyAwayGain); sky.SetFloat("_GainPower", Current.SkyGainPower); sky.SetColor("_AwayTint", Current.SkyAwayTint); }
            sky.SetColor("_Tint", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            EditorUtility.SetDirty(sky);
            RenderSettings.skybox = sky;

            var sunGo = new GameObject("Sun");
            sunGo.transform.SetParent(Root.transform, false);
            Sun = sunGo.AddComponent<Light>();
            Sun.type = LightType.Directional;
            // keep the shadows long and readable: never let the key light get steeper than 40 degrees
            Vector3 lightDir = sunDir;
            float elev = Mathf.Asin(Mathf.Clamp(lightDir.y, -1f, 1f)) * Mathf.Rad2Deg;
            if (!float.IsNaN(Current.SunElevation))
                // a site with a set time of day (the arena's dawn): the light comes from exactly there, low sun included
                lightDir = Quaternion.Euler(-Current.SunElevation, Current.SunAzimuth, 0f) * Vector3.forward;
            else if (elev > 40f || elev < 12f)
            {
                Vector3 flat = new Vector3(lightDir.x, 0f, lightDir.z).normalized;
                float e = Mathf.Clamp(elev, 12f, 40f) * Mathf.Deg2Rad;
                lightDir = flat * Mathf.Cos(e) + Vector3.up * Mathf.Sin(e);
            }
            sunGo.transform.rotation = Quaternion.LookRotation(-lightDir);
            if (sky.HasProperty("_SunDir")) { sky.SetVector("_SunDir", lightDir); EditorUtility.SetDirty(sky); }
            if (Current.SunDisk > 0f) AddSunDisk(sunGo);
            Sun.useColorTemperature = true;
            // a clear Caucasus midday: the sun clearly dominates the sky light, so the shade side reads cool but never
            // turns the whole frame blue-grey (at 1.9 against a full-strength sky the image looked like dusk)
            Sun.colorTemperature = Current.SunTemperature;
            Sun.color = new Color(1f, 0.97f, 0.93f);
            Sun.intensity = Current.SunIntensity;
            Sun.shadows = LightShadows.Soft;
            Sun.shadowStrength = 0.92f;
            Sun.shadowBias = 0.04f;
            Sun.shadowNormalBias = 0.35f;
            var lad = sunGo.AddComponent<UniversalAdditionalLightData>();
            lad.softShadowQuality = SoftShadowQuality.High;
            RenderSettings.sun = Sun;
            if (Current.SkyFill > 0f) AddSkyFill(lightDir);

            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = Current.AmbientIntensity;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = 0.9f;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = Current.FogColor;
            RenderSettings.fogStartDistance = float.IsNaN(Current.FogStart) ? (UseDem ? 220f : 90f) : Current.FogStart;
            RenderSettings.fogEndDistance = float.IsNaN(Current.FogEnd) ? (UseDem ? 21000f : 2700f) : Current.FogEnd;

            var wind = new GameObject("Wind").AddComponent<WindZone>();
            wind.transform.SetParent(Root.transform, false);
            wind.transform.rotation = Quaternion.Euler(0f, Current.WindFrom + 180f, 0f);   // a WindZone blows along its forward
            wind.windMain = 0.6f; wind.windTurbulence = 0.4f; wind.windPulseMagnitude = 0.4f; wind.windPulseFrequency = 0.12f;
        }

        // =================================================================== post-processing

        /// <summary>The open sky's light on the side turned from a low sun (the Lead, Critics R2, 2026-10-31: a dawn valley in shade
        /// read as a hole at L* ~3; flat ambient lifted every face alike and flattened the frame): a cool directional light from
        /// the low sky opposite the sun, 4 degrees up, no shadows: slopes turned from the sun get most of it, flat ground little (at
        /// 50 degrees it out-lit the 9 degree sun on the flats: Q1 p50 49 -> 60, the frame flat again; 12 degrees still lifted Q1).</summary>
        internal static void AddSkyFill(Vector3 sunDir)
        {
            var go = new GameObject("Sky Fill");
            go.transform.SetParent(Root.transform, false);
            var flat = new Vector3(-sunDir.x, 0f, -sunDir.z).normalized;
            var dir = flat * Mathf.Cos(4f * Mathf.Deg2Rad) + Vector3.up * Mathf.Sin(4f * Mathf.Deg2Rad);
            go.transform.rotation = Quaternion.LookRotation(-dir);
            var fill = go.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.color = new Color(0.9f, 0.92f, 0.97f);   // the sky's blue-grey: 0.72/0.8/1 and 0.84/0.88/1 turned the shaded crowns teal (Critics)
            fill.intensity = Current.SkyFill;
            fill.shadows = LightShadows.None;
            go.AddComponent<UniversalAdditionalLightData>();
        }

        internal static void BuildPost()
        {
            string path = GKPaths.Generated + "/Environment/" + Current.PostAsset;
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (profile == null) { profile = ScriptableObject.CreateInstance<VolumeProfile>(); AssetDatabase.CreateAsset(profile, path); }
            foreach (var c in profile.components.ToArray()) { profile.components.Remove(c); Object.DestroyImmediate(c, true); }

            var tm = profile.Add<Tonemapping>(true); tm.mode.Override(TonemappingMode.ACES);
            var bloom = profile.Add<Bloom>(true);
            // bloom only on what is really bright (sun on steel, sky): a wide low-threshold bloom laid a milky veil over everything
            bloom.threshold.Override(1.25f); bloom.intensity.Override(0.22f); bloom.scatter.Override(0.5f); bloom.highQualityFiltering.Override(true);
            var ca = profile.Add<ColorAdjustments>(true);
            ca.postExposure.Override(Current.PostExposure); ca.contrast.Override(Current.PostContrast); ca.saturation.Override(Current.PostSaturation); ca.colorFilter.Override(new Color(1f, 1f, 0.99f));
            var wb = profile.Add<WhiteBalance>(true); wb.temperature.Override(1f); wb.tint.Override(0f);
            var smh = profile.Add<ShadowsMidtonesHighlights>(true);
            smh.shadows.Override(new Vector4(0.96f, 0.99f, 1.06f, Current.ShadowLift));
            smh.highlights.Override(new Vector4(1.04f, 1.0f, 0.95f, 0f));
            var vig = profile.Add<Vignette>(true); vig.intensity.Override(0.16f); vig.smoothness.Override(0.42f);
            // no film grain: on a clean digital image it read as noise and haze over the faces
            foreach (var c in profile.components) AssetDatabase.AddObjectToAsset(c, profile);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            var vgo = new GameObject("Post Volume");
            vgo.transform.SetParent(Root.transform, false);
            var vol = vgo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 0;
            vol.sharedProfile = profile;
        }

        // =================================================================== dressing

        static void ScatterBoulders(GameObject terrainGo)
        {
            var parent = new GameObject("Boulders").transform;
            parent.SetParent(Root.transform, false);
            var lichen = GKDownloads.Texture("lichen_rock");
            var mat = GKProps.RockMaterial(lichen);
            var meshes = GKProps.BoulderMeshes(8);
            var rnd = new System.Random(Seed);
            System.Func<float, float, float> R = (a, b) => a + (float)rnd.NextDouble() * (b - a);
            int placed = 0;
            // rim outcrops that frame the arena (leave the southern mouth toward the valley open)
            for (int i = 0; i < 46; i++)
            {
                float ang = R(-150f, 150f);
                float dist = R(56f, 96f);
                Vector3 p = new Vector3(Mathf.Sin(ang * Mathf.Deg2Rad) * dist, 0f, Mathf.Cos(ang * Mathf.Deg2Rad) * dist);
                float s = R(1.4f, 4.8f) * (dist > 80f ? 1.3f : 1f);
                Place(parent, meshes[rnd.Next(meshes.Count)], mat, p, s, rnd, true); placed++;
                // small companions
                for (int k = 0; k < 3; k++)
                {
                    Vector3 q = p + new Vector3(R(-5f, 5f), 0f, R(-5f, 5f));
                    Place(parent, meshes[rnd.Next(meshes.Count)], mat, q, R(0.3f, 1.1f), rnd, false); placed++;
                }
            }
            // field stones in the meadow and along the road
            for (int i = 0; i < 260; i++)
            {
                float ang = R(0f, 360f);
                float dist = R(105f, 700f);
                Vector3 p = new Vector3(Mathf.Sin(ang * Mathf.Deg2Rad) * dist, 0f, Mathf.Cos(ang * Mathf.Deg2Rad) * dist);
                if (RoadMask(p.x, p.z) > 0.05f) continue;
                Place(parent, meshes[rnd.Next(meshes.Count)], mat, p, R(0.4f, 3.2f), rnd, true); placed++;
            }
            // a few loose stones on the arena floor
            for (int i = 0; i < 18; i++)
            {
                Vector3 p = new Vector3(R(-38f, 38f), 0f, R(-38f, 38f));
                if (p.magnitude < 9f) continue;
                Place(parent, meshes[rnd.Next(meshes.Count)], mat, p, R(0.12f, 0.35f), rnd, false); placed++;
            }
            GKBridge.Log("placed " + placed + " boulders");
        }

        static void Place(Transform parent, Mesh mesh, Material mat, Vector3 p, float scale, System.Random rnd, bool collider)
        {
            p.y = SampleHeight(p) - scale * 0.28f;
            var go = new GameObject("Rock");
            go.transform.SetParent(parent, false);
            go.transform.position = p;
            go.transform.rotation = Quaternion.Euler((float)rnd.NextDouble() * 14f - 7f, (float)rnd.NextDouble() * 360f, (float)rnd.NextDouble() * 14f - 7f);
            float sx = 0.8f + (float)rnd.NextDouble() * 0.5f, sz = 0.8f + (float)rnd.NextDouble() * 0.5f;
            go.transform.localScale = new Vector3(scale * sx, scale * (0.6f + (float)rnd.NextDouble() * 0.35f), scale * sz);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            go.isStatic = true;
            if (collider && scale > 0.8f) go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        static void PlaceBanners(GameObject terrainGo)
        {
            var parent = new GameObject("Banners").transform;
            parent.SetParent(Root.transform, false);
            // two banners flank the mouth of the arena toward the valley, two stand on the northern rise
            var spots = new[]
            {
                new Vector3(-11f, 0f, -47f), new Vector3(11f, 0f, -47f),
                new Vector3(-24f, 0f, 44f), new Vector3(26f, 0f, 41f),
                new Vector3(-46f, 0f, 6f),
            };
            foreach (var s in spots)
            {
                var b = GKProps.Banner();
                b.transform.SetParent(parent, false);
                Vector3 p = s; p.y = SampleHeight(p) - 0.15f;
                b.transform.position = p;
                b.transform.rotation = Quaternion.Euler(0f, 200f + Random.Range(-15f, 15f), 0f);
            }
        }
    }
}
