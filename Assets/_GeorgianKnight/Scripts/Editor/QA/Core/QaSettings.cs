using System;
using System.Collections.Generic;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// Every threshold the asset/scene/data validators use, with defaults that encode the art bible and the layer table.
    /// Optional overrides live in &lt;project&gt;/Docs/qa_config.json (same field names, any subset), applied with
    /// JsonUtility.FromJsonOverwrite — so budgets can be tuned without touching code. Plain data (no engine types) so the
    /// pure rule classes and the container harness can use it. Documented in Docs/QA.md.
    /// </summary>
    [Serializable]
    public sealed class QaSettings
    {
        /// <summary>A real-world size range for assets whose name or folder carries <see cref="token"/> (metres).</summary>
        [Serializable]
        public sealed class SizeBand
        {
            public string token = "";
            public float min;
            public float max;
            public SizeBand() { }
            public SizeBand(string token, float min, float max) { this.token = token; this.min = min; this.max = max; }
        }

        /// <summary>A value band for materials whose name carries any of <see cref="tokens"/> (smoothness by class).</summary>
        [Serializable]
        public sealed class TokenBand
        {
            public List<string> tokens = new List<string>();
            public float min;
            public float max;
            public TokenBand() { }
            public TokenBand(float min, float max, params string[] tokens) { this.min = min; this.max = max; this.tokens.AddRange(tokens); }
        }

        /// <summary>Texel-density target of one asset class in px/m (the check allows <see cref="TexelLimits.tolerance"/> around it; max 0 = no ceiling).</summary>
        [Serializable]
        public sealed class TexelTier
        {
            public string kind = "";
            public float min;
            public float max;
            public TexelTier() { }
            public TexelTier(string kind, float min, float max) { this.kind = kind; this.min = min; this.max = max; }
        }

        /// <summary>A known number for a named source: a texture set's real tile size, a sky's sun elevation.</summary>
        [Serializable]
        public sealed class NamedValue
        {
            public string match = "";
            public float value;
            public NamedValue() { }
            public NamedValue(string match, float value) { this.match = match; this.value = value; }
        }

        /// <summary>Texture import limits.</summary>
        [Serializable]
        public sealed class TextureLimits
        {
            public int maxSize = 4096;
            public int heroMinSize = 1024;
            public int uncompressedMinSize = 512;
            public int minAniso = 4;
            public List<string> groundTokens = new List<string>
            { "ground", "terrain", "floor", "road", "path", "cobble", "cobblestone", "paving", "pavement", "dirt", "mud", "grass", "soil", "sand", "gravel", "street", "tiles" };
            public List<string> readWriteAllowed = new List<string>();
            /// <summary>TEX.NORMAL_BIAS: mean R and G of a tangent-space normal map within 0.5 ± this; mean B at least normalBlueMin.</summary>
            public float normalMeanTolerance = 0.04f;
            public float normalBlueMin = 0.85f;
            /// <summary>TEX.METAL_GREYS: allowed share of metal-channel texels between metalGreyLow and metalGreyHigh.</summary>
            public float metalGreyLow = 0.15f;
            public float metalGreyHigh = 0.85f;
            public float metalGreyShareMax = 0.10f;
            /// <summary>Pixels sampled from a source image for the statistics above (evenly strided).</summary>
            public int analysisSamples = 262144;
        }

        /// <summary>PBR plausibility bounds for materials.</summary>
        [Serializable]
        public sealed class MaterialLimits
        {
            public float albedoMin = 30f;
            public float albedoMax = 240f;
            public float smoothnessMax = 0.85f;
            public float metallicLow = 0.05f;
            public float metallicHigh = 0.95f;
            /// <summary>MAT.METAL_ALBEDO: a metal's albedo luminance (sRGB 0..255) must reach this.</summary>
            public float metalAlbedoMin = 150f;
            /// <summary>MAT.SMOOTHNESS_BY_CLASS (ART_BIBLE §5.1); the first band whose token is in the material name wins.</summary>
            public List<TokenBand> smoothnessClasses = new List<TokenBand>
            {
                new TokenBand(0f, 0.25f, "wool", "felt"), new TokenBand(0f, 0.30f, "linen"), new TokenBand(0.30f, 0.50f, "leather"),
                new TokenBand(0.20f, 0.45f, "wood"), new TokenBand(0.35f, 0.55f, "iron"), new TokenBand(0.65f, 0.80f, "steel", "blade"),
                new TokenBand(0f, 0.30f, "stone", "rock", "soil", "dirt", "grass", "ground", "mud")
            };
            public float texelRatioMax = 4f;
            public List<string> emitterTokens = new List<string>
            { "fire", "torch", "lamp", "lantern", "candle", "ember", "embers", "glow", "lava", "magic", "rune", "brazier", "hearth", "coal", "spark", "flame", "emissive", "window", "sky" };
            public List<string> albedoExempt = new List<string> { "water", "glass", "sky", "decal", "shadow", "blob", "ui", "debug", "overlay" };
        }

        /// <summary>Model scale and triangle budgets.</summary>
        [Serializable]
        public sealed class ModelLimits
        {
            public float characterMinHeight = 1.60f;
            public float characterMaxHeight = 2.00f;
            public float weaponMinLength = 0.3f;
            public float weaponMaxLength = 2.5f;
            public float propMinSize = 0.02f;
            public float propMaxSize = 50f;
            public int heroTriangles = 120000;
            public int npcTriangles = 60000;
            public int weaponTriangles = 8000;
            public int shieldTriangles = 6000;
            public int bannerTriangles = 4000;
            public int rockTriangles = 12000;
            public int propTriangles = 15000;
            public int environmentTriangles = 50000;
            public int heroMinTriangles = 15000;
            public int weaponMinTriangles = 300;
            public float uvOverlapMax = 0.6f;
            /// <summary>Hero models by folder or file-name prefix ("Knight" is the enemy mesh, never a hero).</summary>
            public List<string> heroNames = new List<string> { "Bakur", "CC_HeroOutfit", "CC_Hero", "Hero_V2", "GK_HeroV2_GarmentTest" };
            /// <summary>MODEL.IMPORT_SCALE: allowed deviation of the importer's globalScale from 1.</summary>
            public float importScaleTolerance = 0.0001f;
            /// <summary>MODEL.NO_LODS: characters and meshes above this many LOD0 triangles need LODs at these ratios (± tolerance).</summary>
            public int lodTriangles = 5000;
            public List<float> lodRatios = new List<float> { 0.5f, 0.25f, 0.1f };
            public float lodRatioTolerance = 0.10f;
            /// <summary>MODEL.OPEN_EDGES / NONMANIFOLD: vertices closer than this (m) are one vertex when edges are counted.</summary>
            public float weldDistance = 0.0001f;
            /// <summary>MODEL.FACETED_WEAPON: LOD0 triangles per metre of a held weapon's longest axis.</summary>
            public float facetedTrisPerMeter = 2000f;
            /// <summary>SCALE.WEAPON_CLASS: longest axis by class; the first token found in the file or folder name wins.</summary>
            public List<SizeBand> weaponClasses = new List<SizeBand>
            {
                new SizeBand("scabbard", 0.80f, 0.95f), new SizeBand("sabre", 0.85f, 1.00f), new SizeBand("saber", 0.85f, 1.00f),
                new SizeBand("sword", 0.90f, 1.00f), new SizeBand("mace", 0.50f, 0.80f), new SizeBand("lance", 3.0f, 4.0f),
                new SizeBand("kiteshield", 0.95f, 1.25f), new SizeBand("shieldkite", 0.95f, 1.25f),
                new SizeBand("domedshield", 0.55f, 0.80f), new SizeBand("shielddomed", 0.55f, 0.80f),
                new SizeBand("roundshield", 0.69f, 0.75f), new SizeBand("shieldround", 0.69f, 0.75f)
            };
            public List<string> shieldTokens = new List<string> { "shield" };
            public List<string> bannerTokens = new List<string> { "banner", "flag" };
            public List<string> rockTokens = new List<string> { "rock", "boulder", "cliff" };
        }

        /// <summary>Texel density (ART_BIBLE §6): px/m = texture width × tiling × √(UV area / world area), per material of LOD0.</summary>
        [Serializable]
        public sealed class TexelLimits
        {
            /// <summary>Allowed deviation from a tier: 0.5 = from half its minimum to 1.5× its maximum.</summary>
            public float tolerance = 0.5f;
            public List<TexelTier> tiers = new List<TexelTier>
            {
                new TexelTier("Hero", 1024f, 1536f), new TexelTier("Npc", 512f, 1024f), new TexelTier("Weapon", 1024f, 2048f),
                new TexelTier("Shield", 1024f, 1024f), new TexelTier("Banner", 512f, 1024f), new TexelTier("Prop", 512f, 1024f),
                new TexelTier("Rock", 512f, 0f), new TexelTier("Environment", 512f, 0f)
            };
            /// <summary>Faces of heroes and enemies may go up to this many px/m.</summary>
            public float faceMax = 2048f;
            public List<string> faceTokens = new List<string> { "head", "face" };
            /// <summary>Cards, tiny parts and deliberately tiled materials whose density says nothing.</summary>
            public List<string> exemptTokens = new List<string>
            { "hair", "beard", "brow", "lash", "eye", "teeth", "tongue", "tear", "occlusion", "cornea", "scalp", "transparency", "mail", "border", "band" };
            /// <summary>MODEL.TEXEL_NEIGHBOUR: highest / lowest density inside one prefab.</summary>
            public float neighbourRatio = 4f;
        }

        /// <summary>Terrain layers: texel density and true tile size.</summary>
        [Serializable]
        public sealed class TerrainLimits
        {
            public float minTexel = 512f;
            /// <summary>Layers for far / macro blending only (token in the layer name) are not held to minTexel.</summary>
            public List<string> farTokens = new List<string> { "far", "macro", "distant", "vista" };
            public float tileTolerance = 0.25f;
            /// <summary>Real size (m) of scanned texture sets; match = a substring of the diffuse texture's file name.
            /// Defaults: Poly Haven API "dimensions" of the sets in use, read by the Asset Quality Gate on 2026-10-27.</summary>
            public List<NamedValue> tileSources = new List<NamedValue>
            {
                new NamedValue("aerial_grass_rock", 15f), new NamedValue("aerial_rocks_02", 50f), new NamedValue("dry_ground_rocks", 4f),
                new NamedValue("snow_02", 2f), new NamedValue("park_dirt", 3f),
                new NamedValue("rocks_ground_08", 3f), new NamedValue("dry_mud_field_001", 3f)   // World, Poly Haven API, 2026-10-31
            };
        }

        /// <summary>Audio import and level analysis thresholds.</summary>
        [Serializable]
        public sealed class AudioLimits
        {
            public float longClipSeconds = 10f;
            public float shortClipSeconds = 1f;
            public float leadingSilenceMs = 50f;
            public float silenceDb = -50f;
            public float headroomDb = -0.3f;
            public float quietRmsDb = -40f;
            public float loudRmsDb = -8f;
            public float dcOffsetMax = 0.01f;
            public List<string> positionalFolders = new List<string> { "/Audio/SFX/" };
            public List<string> nonPositionalFolders = new List<string> { "/Audio/SFX/UI/", "/Audio/SFX/2D/", "/Audio/Music/", "/Audio/Ambience/", "/Audio/Voice/" };
        }

        /// <summary>Prefab sanity thresholds (layer table: Character 8, Hurtbox 9, Terrain 10).</summary>
        [Serializable]
        public sealed class PrefabLimits
        {
            public int characterLayer = 8;
            public int hurtboxLayer = 9;
            public int terrainLayer = 10;
            public float ccMinHeight = 1.5f;
            public float ccMaxHeight = 2.2f;
            public float ccMinRadius = 0.2f;
            public float ccMaxRadius = 0.6f;
            public int lodTriangles = 5000;
            public float lodSize = 10f;
            /// <summary>SCALE.ART_RESCALED: a model instance from Art/ may differ from scale 1 by this fraction.</summary>
            public float rescaleTolerance = 0.01f;
            public float tinyShadowSize = 0.25f;
        }

        /// <summary>Scene placement, lighting and post-processing bounds.</summary>
        [Serializable]
        public sealed class SceneLimits
        {
            public float floatTolerance = 0.05f;
            public float sunkFraction = 0.3f;
            public float probeHeight = 500f;
            public float duplicateEpsilon = 0.001f;
            public float bloomIntensityMax = 1.5f;
            public float bloomThresholdMin = 0.8f;
            public float postExposureMax = 2f;
            public float saturationMax = 30f;
            public float vignetteMax = 0.45f;
            public float chromaticMax = 0.3f;
            public float nearClipMin = 0.05f;
            public float farNearRatioMax = 100000f;
            public float navMeshSnap = 1.0f;
            public float bigLightRange = 10f;
            public int maxListedPerRule = 25;
            public List<string> floatingAllowed = new List<string>
            { "banner", "flag", "lamp", "lantern", "chandelier", "bird", "cloud", "sky", "hang", "hanging", "sconce", "rope", "chain", "sign", "qaview", "spawn", "trigger", "volume", "marker", "fx", "vfx", "light", "probe", "camera" };
            public List<string> environmentRoots = new List<string> { "Environment", "Props", "Architecture", "World", "Level", "Static", "Terrain", "Buildings" };
            public List<string> spawnComponents = new List<string> { "SpawnPoint", "PlayerSpawnPoint", "Checkpoint", "FastTravelPoint" };
            public List<string> questComponents = new List<string> { "DiscoveryVolume", "QuestMarker", "QuestLocation", "MapLocation", "TalkInteractable", "DialogueActor", "InteractableBase" };
            /// <summary>SCENE.SKY_SUN_MATCH: sun elevation (degrees) of known sky textures; match = a substring of the texture name.</summary>
            public List<NamedValue> skySuns = new List<NamedValue> { new NamedValue("kloofendal_48d", 48f) };
            public float skySunTolerance = 15f;
        }

        /// <summary>Screenshot exposure metrics (qa.shots).</summary>
        [Serializable]
        public sealed class VisualLimits
        {
            public float meanLumMin = 0.18f;
            public float meanLumMax = 0.70f;
            public float clippedMax = 0.05f;
            public float crushedMax = 0.10f;
            public float saturationMax = 0.6f;
            public float contrastMin = 0.08f;
            public int width = 1920;
            public int height = 1080;
            public float eyeHeight = 1.7f;
            public float followDistance = 4.5f;
        }

        /// <summary>Balance envelope for AttackData (per kind; [min, max]).</summary>
        [Serializable]
        public sealed class CombatLimits
        {
            public float lightDamageMin = 5f, lightDamageMax = 40f;
            public float heavyDamageMin = 12f, heavyDamageMax = 90f;
            public float staminaMax = 60f;
            public float poiseMax = 150f;
            public float unblockableWindupMin = 0.5f;
            public float activeWindowMinSeconds = 1f / 30f;
            public float activeWindowMaxSeconds = 0.6f;
        }

        /// <summary>Which definitions need which localization keys.</summary>
        [Serializable]
        public sealed class LocalizationLimits
        {
            public List<string> nonPlayerFacingPrefixes = new List<string> { "loot" };
            public List<string> descriptionRequired = new List<string> { "item", "skill", "quest" };
            public List<string> languages = new List<string> { "ka", "en" };
        }

        public TextureLimits texture = new TextureLimits();
        public MaterialLimits material = new MaterialLimits();
        public ModelLimits model = new ModelLimits();
        public TexelLimits texel = new TexelLimits();
        public TerrainLimits terrain = new TerrainLimits();
        public AudioLimits audio = new AudioLimits();
        public PrefabLimits prefab = new PrefabLimits();
        public SceneLimits scene = new SceneLimits();
        public VisualLimits visual = new VisualLimits();
        public CombatLimits combat = new CombatLimits();
        public LocalizationLimits localization = new LocalizationLimits();
    }
}
