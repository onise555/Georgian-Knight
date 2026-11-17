using System;
using System.Collections.Generic;
using System.Reflection;
using S = GeorgianKnight.EditorTools.QA.QaSeverity;
using C = GeorgianKnight.EditorTools.QA.QaCategory;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// The rule catalog: every rule id the QA framework can emit, with severity, rationale and fix. This is the single
    /// source for the report and for the rule table in Docs/QA.md ("qa.rules" regenerates it). The Python linter in
    /// Tools/qa mirrors the CODE.* subset; Tools/qa/parity.sh fails when the two drift apart.
    /// </summary>
    public static class QaRules
    {
        // ------------------------------------------------------------------------------------------------ code
        public static readonly QaRule CodeCSharpVersion = new QaRule("CODE.CSHARP_VERSION", C.Code, S.Blocker,
            "C# 8+ syntax", "The container toolchain (mcs) and the team rules are C# 7.3; newer syntax breaks the pre-deploy compile.",
            "Rewrite with C# 7.3 constructs (explicit types, classic switch, `x = x ?? y`, no `using var`, no ranges).");
        public static readonly QaRule CodeTimeScaleWrite = new QaRule("CODE.TIMESCALE_WRITE", C.Code, S.Critical,
            "Time.timeScale written outside TimeService", "Two writers overwrite each other (a hit-stop ending un-pauses the pause menu).",
            "Request time through ITimeService.Hold/Pulse/Release.");
        public static readonly QaRule CodeFindInHotPath = new QaRule("CODE.FIND_IN_HOT_PATH", C.Code, S.Critical,
            "Scene search in a per-frame path", "Find*/FindObject* walk the whole scene every frame: a guaranteed frame-time spike that grows with content.",
            "Resolve the reference once (Services.Get, serialized field, PlayerSpawned event) and cache it.");
        public static readonly QaRule CodeFindRuntime = new QaRule("CODE.FIND_RUNTIME", C.Code, S.Major,
            "Scene search in runtime code", "ARCHITECTURE §9: no Find* at runtime outside bootstrap; hidden coupling to scene contents and names.",
            "Use Services / IPlayerContext / serialized references, or add the file to the allow-list with a reason.");
        public static readonly QaRule CodeGetComponentHot = new QaRule("CODE.GETCOMPONENT_HOT", C.Code, S.Major,
            "GetComponent in a per-frame path", "Component lookups every frame cost CPU and the array variants allocate.",
            "Cache the component in Awake/OnEnable.");
        public static readonly QaRule CodeLinqHot = new QaRule("CODE.LINQ_HOT", C.Code, S.Major,
            "LINQ in a per-frame path", "LINQ allocates enumerators and closures every call -> GC spikes (TEAM_BRIEF performance rule).",
            "Use a for loop over a cached list.");
        public static readonly QaRule CodeAllocHot = new QaRule("CODE.ALLOC_HOT", C.Code, S.Major,
            "Heap allocation in a per-frame path", "Per-frame garbage (collections, arrays, strings, closures, yield instructions) causes GC hitches.",
            "Preallocate and reuse buffers, cache yield instructions, build strings only on change.");
        public static readonly QaRule CodeStringAnimatorHot = new QaRule("CODE.STRING_ANIMATOR_HOT", C.Code, S.Major,
            "String-based Animator call in a per-frame path", "String lookups hash every call (ARCHITECTURE §9: hashes in AnimIds/GK).",
            "Use a static readonly int from Animator.StringToHash.");
        public static readonly QaRule CodeDebugLogHot = new QaRule("CODE.DEBUG_LOG_HOT", C.Code, S.Major,
            "Debug.Log in a per-frame path", "Logging every frame allocates, stalls the main thread and floods the console.",
            "Remove it, log on state change, or use LogWarning/LogError for real problems.");
        public static readonly QaRule CodeDebugLogRuntime = new QaRule("CODE.DEBUG_LOG_RUNTIME", C.Code, S.Info,
            "Debug.Log in runtime code", "Informational logs ship in player builds unless stripped.",
            "Use the categorised Log helper or guard with #if UNITY_EDITOR || DEVELOPMENT_BUILD.");
        public static readonly QaRule CodeCameraMainHot = new QaRule("CODE.CAMERA_MAIN_HOT", C.Code, S.Minor,
            "Camera.main in a per-frame path", "Hidden global lookup; breaks when cinematics swap cameras.",
            "Cache the camera (rebind on camera change) or take it from the camera service.");
        public static readonly QaRule CodePhysicsAlloc = new QaRule("CODE.PHYSICS_ALLOC", C.Code, S.Major,
            "Allocating physics query", "RaycastAll/Overlap*/...CastAll return new arrays each call.",
            "Use the NonAlloc variant with a preallocated buffer.");
        public static readonly QaRule CodeUnityNullOp = new QaRule("CODE.UNITY_NULL_OP", C.Code, S.Critical,
            "?. or ?? on a UnityEngine.Object", "Destroyed Unity objects are 'fake null': ?. and ?? bypass the overloaded == and call into a dead object.",
            "Use an explicit `if (x != null)` check.");
        public static readonly QaRule CodeEmptyCatch = new QaRule("CODE.EMPTY_CATCH", C.Code, S.Major,
            "Empty catch block", "Swallowed exceptions hide bugs and corrupt state silently.",
            "Log the exception (Debug.LogException) or handle it; if swallowing is truly correct, say why in a comment.");
        public static readonly QaRule CodeCatchNoLog = new QaRule("CODE.CATCH_NO_LOG", C.Code, S.Minor,
            "catch without logging or rethrow", "The failure leaves no trace for QA or crash reports.",
            "Log with context or rethrow; document intentional fallbacks.");
        public static readonly QaRule CodeHardcodedUiString = new QaRule("CODE.HARDCODED_UI_STRING", C.Code, S.Major,
            "Hard-coded user-facing text", "Untranslatable text; ARCHITECTURE §9: all player-facing strings are localization keys.",
            "Add a key to Resources/Localization/{ka,en} and use Loc.Get / Loc.Format.");
        public static readonly QaRule CodeGeorgianLiteral = new QaRule("CODE.GEORGIAN_LITERAL", C.Code, S.Major,
            "Georgian text in code", "Georgian strings belong in the ka string table so they can be proofread and translated.",
            "Move the text to Resources/Localization/ka (and en) and reference the key.");
        public static readonly QaRule CodeMagicNumber = new QaRule("CODE.MAGIC_NUMBER", C.Code, S.Minor,
            "Magic numbers in combat/balance code", "Tuning values buried in code cannot be balanced by design and drift between systems.",
            "Move them to the relevant data asset (AttackData, MoveSet, definitions) or to a named const with a comment.");
        public static readonly QaRule CodePublicFieldMb = new QaRule("CODE.PUBLIC_FIELD_MB", C.Code, S.Minor,
            "Public fields on a MonoBehaviour", "Any system can write them; ARCHITECTURE §9 wants [SerializeField] private + [Tooltip].",
            "Make them [SerializeField] private with a [Tooltip]; expose read-only properties.");
        public static readonly QaRule CodeMissingXmlSummary = new QaRule("CODE.MISSING_XML_SUMMARY", C.Code, S.Minor,
            "Public type without XML summary", "TEAM_BRIEF: every public type explains why it exists.",
            "Add a /// <summary> that states the purpose, not the name.");
        public static readonly QaRule CodeWeakXmlSummary = new QaRule("CODE.WEAK_XML_SUMMARY", C.Code, S.Info,
            "XML summary restates the name", "A summary like 'The Foo class.' is boilerplate and says nothing.",
            "Explain why the type exists and how it is meant to be used.");
        public static readonly QaRule CodeMultipleUnityObjects = new QaRule("CODE.MULTIPLE_UNITY_OBJECTS", C.Code, S.Critical,
            "Several MonoBehaviours/ScriptableObjects in one file", "Unity binds scripts by file; the extra classes cannot be added or serialized. Not applied to MonoBehaviours in editor assemblies (Scripts/Editor/**): see CODE.FILENAME_MISMATCH.",
            "One MonoBehaviour/ScriptableObject per file, file name == class name.");
        public static readonly QaRule CodeFileNameMismatch = new QaRule("CODE.FILENAME_MISMATCH", C.Code, S.Critical,
            "File name does not match the class", "MonoBehaviour/ScriptableObject scripts whose file name differs lose their serialized references. Not applied to MonoBehaviours in editor assemblies (Scripts/Editor/**): there a matching file makes Unity refuse AddComponent (\"editor script\"), so editor-only helper components must keep another file name.",
            "Rename the file (or the class) so they match.");
        public static readonly QaRule CodeNestedUnityObject = new QaRule("CODE.NESTED_UNITY_OBJECT", C.Code, S.Major,
            "Nested MonoBehaviour/ScriptableObject", "Nested Unity object classes cannot be added as components or saved as assets.",
            "Move the class to its own file at namespace level.");
        public static readonly QaRule CodeEventNoUnsubscribe = new QaRule("CODE.EVENT_NO_UNSUBSCRIBE", C.Code, S.Major,
            "Event subscribed without matching unsubscription", "Leaks the subscriber and fires handlers on destroyed objects (MissingReferenceException).",
            "Pair OnEnable(+=) with OnDisable(-=), Awake/Start(+=) with OnDestroy(-=); never subscribe lambdas to long-lived events.");
        public static readonly QaRule CodeSubscriptionNotDisposed = new QaRule("CODE.SUBSCRIPTION_NOT_DISPOSED", C.Code, S.Major,
            "EventBus subscription never disposed", "EventBus.Subscribe returns the only handle that can remove the handler.",
            "Store the token (or a Subscriptions group) and Dispose it in OnDisable/OnDestroy/Dispose.");
        public static readonly QaRule CodeCoroutineNoStop = new QaRule("CODE.COROUTINE_NO_STOP", C.Code, S.Minor,
            "Coroutines started but never stopped", "Disabling the component does not stop its coroutines; they keep running on stale state.",
            "Keep the Coroutine handle and StopCoroutine it (or StopAllCoroutines) in OnDisable.");
        public static readonly QaRule CodeAsyncVoid = new QaRule("CODE.ASYNC_VOID", C.Code, S.Major,
            "async void method", "Exceptions escape to the synchronization context and the work outlives the object.",
            "Return Task (or use a coroutine / timer service) and observe the result.");
        public static readonly QaRule CodeUnusedPrivateField = new QaRule("CODE.UNUSED_PRIVATE_FIELD", C.Code, S.Minor,
            "Unused private field", "Dead state confuses readers and hides unfinished work.",
            "Delete it or wire it up.");
        public static readonly QaRule CodeSerializedUnused = new QaRule("CODE.SERIALIZED_UNUSED", C.Code, S.Minor,
            "Serialized field never read", "Designers tune a value that does nothing.",
            "Use it or remove it (and its data).");
        public static readonly QaRule CodeTodo = new QaRule("CODE.TODO", C.Code, S.Minor,
            "TODO / FIXME / HACK left in code", "Unfinished work hidden from the backlog; FIXME/HACK mark known defects.",
            "Finish it, or move it to the task tracker and delete the marker.");
        public static readonly QaRule CodeCommentedCode = new QaRule("CODE.COMMENTED_CODE", C.Code, S.Minor,
            "Commented-out code", "TEAM_BRIEF: no commented-out code; version control keeps history.",
            "Delete it.");
        public static readonly QaRule CodeMethodTooLong = new QaRule("CODE.METHOD_TOO_LONG", C.Code, S.Major,
            "Method longer than the limit", "Long methods hide bugs and resist testing (logic belongs in small pure classes).",
            "Split into named steps; move rules into testable plain C# classes.");
        public static readonly QaRule CodeComplexity = new QaRule("CODE.COMPLEXITY", C.Code, S.Major,
            "Cyclomatic complexity too high", "Too many paths to test; a common source of state-machine bugs.",
            "Extract branches into methods or table-driven logic.");
        public static readonly QaRule CodeNamespaceFolder = new QaRule("CODE.NAMESPACE_FOLDER", C.Code, S.Minor,
            "Namespace does not match folder", "ARCHITECTURE §2: namespaces mirror folders so ownership and asmdefs stay obvious.",
            "Use GeorgianKnight.<Module> (Gameplay), GeorgianKnight.Core, GeorgianKnight.UI, GeorgianKnight.EditorTools, GeorgianKnight.Tests.");
        public static readonly QaRule CodeResourcesLoad = new QaRule("CODE.RESOURCES_LOAD", C.Code, S.Major,
            "Resources.Load outside the allowed roots", "ARCHITECTURE §9: Resources only for GameConfig and Localization; everything else is referenced data.",
            "Reference the asset from a definition/module or add the root to the allow-list.");
        public static readonly QaRule CodeSoMutation = new QaRule("CODE.SO_MUTATION", C.Code, S.Critical,
            "ScriptableObject definition mutated at runtime", "Definitions are immutable (ARCHITECTURE §1.2); in the editor the change persists into the asset.",
            "Copy the value into runtime state (a plain C# model) and mutate that.");
        public static readonly QaRule CodeSingleton = new QaRule("CODE.SINGLETON", C.Code, S.Major,
            "Static singleton", "TEAM_BRIEF: never singletons at runtime; use Services.",
            "Register an interface with Services in the module's Install.");
        public static readonly QaRule CodeSendMessage = new QaRule("CODE.SEND_MESSAGE", C.Code, S.Major,
            "String-based messaging / Invoke", "SendMessage/BroadcastMessage/Invoke(\"Name\") are slow, untyped and break silently on rename.",
            "Call the method directly, use an interface or an EventBus event.");
        public static readonly QaRule CodeTagCompare = new QaRule("CODE.TAG_COMPARE", C.Code, S.Minor,
            "tag compared with ==", "Reading .tag allocates a string.",
            "Use CompareTag(\"Tag\").");

        // ------------------------------------------------------------------------------------------------ textures
        public static readonly QaRule TexNpot = new QaRule("TEX.NPOT", C.Texture, S.Major,
            "Non-power-of-two compressed texture", "NPOT 3D textures waste memory, may lose mips/compression on some targets and tile badly.",
            "Resize the source to a power of two (or set Non-Power of 2 = ToNearest).");
        public static readonly QaRule TexTooLarge = new QaRule("TEX.TOO_LARGE", C.Texture, S.Major,
            "Texture above 4096", "Memory and streaming cost with no visible gain at game camera distances.",
            "Cap Max Size at 4096 (2048 for props).");
        public static readonly QaRule TexHeroLowRes = new QaRule("TEX.HERO_LOW_RES", C.Texture, S.Major,
            "Hero asset texture too small", "Characters and weapons are seen up close; sub-1K maps look blurry next to 2K/4K environment.",
            "Use >= 2048 for hero bodies, >= 1024 for weapons and accessories.");
        public static readonly QaRule TexNoMipmaps = new QaRule("TEX.NO_MIPMAPS", C.Texture, S.Major,
            "3D texture without mipmaps", "Shimmering/aliasing at distance and wasted bandwidth.",
            "Enable Generate Mip Maps (only UI sprites go without).");
        public static readonly QaRule TexNormalNotMarked = new QaRule("TEX.NORMAL_NOT_MARKED", C.Texture, S.Critical,
            "Normal map not imported as Normal Map", "Shading is wrong (sRGB + no unpacking) -> blotchy, inflated lighting.",
            "Set Texture Type = Normal map.");
        public static readonly QaRule TexSrgbData = new QaRule("TEX.SRGB_DATA", C.Texture, S.Critical,
            "Data map imported as sRGB", "Mask/roughness/AO/height values get gamma-decoded -> wrong metal/smoothness response.",
            "Untick sRGB (Color Texture) for mask, ARM, roughness, metallic, AO, height maps.");
        public static readonly QaRule TexSrgbAlbedo = new QaRule("TEX.SRGB_ALBEDO", C.Texture, S.Major,
            "Albedo imported as linear", "Colors render washed-out/too bright in linear color space.",
            "Tick sRGB (Color Texture) for base color maps.");
        public static readonly QaRule TexUncompressed = new QaRule("TEX.UNCOMPRESSED", C.Texture, S.Major,
            "Large uncompressed texture", "4-8x the memory of BC formats.",
            "Use Normal/High quality compression (BC7/BC5 on PC).");
        public static readonly QaRule TexNormalBias = new QaRule("TEX.NORMAL_BIAS", C.Texture, S.Major,
            "Biased normal map", "A tangent-space normal map whose mean R or G is off 0.5 (or whose B is low) tilts the lighting of the whole surface.",
            "Re-export from the source (nor_gl), check OpenGL vs DirectX, do not JPEG-recompress a normal map.");
        public static readonly QaRule TexMetalGreys = new QaRule("TEX.METAL_GREYS", C.Texture, S.Major,
            "Metal map full of greys", "Metalness is 0 or 1; a map with many texels in between is neither metal nor dielectric (ART_BIBLE §5.1).",
            "Threshold the metal channel to 0 / 1; keep in-between values only in a rust mask.");
        public static readonly QaRule TexHeroCapped = new QaRule("TEX.HERO_CAPPED", C.Texture, S.Info,
            "Hero texture imported below its source size", "QUALITY allows 4K hero sets; a cap is a choice and should be visible.",
            "Decide per map (face, hands): raise Max Size or keep the cap on purpose.");
        public static readonly QaRule TerrainTexel = new QaRule("TERRAIN.TEXEL", C.Texture, S.Major,
            "Terrain layer too soft", "The ground fills half of the third-person frame; under ~512 px/m it smears next to sharp characters.",
            "Use close-range 2-4 m scans at 2K-4K for near layers; keep aerial sets as far / macro layers only.");
        public static readonly QaRule ScaleTileSize = new QaRule("SCALE.TILE_SIZE", C.Texture, S.Major,
            "Texture tiled at the wrong real size", "A stretched scan makes pebbles fist-sized and grass blades thick (QUALITY: texture scale true to the material).",
            "Set the layer's tile size to the scanned source's real size (±25 %) or pick a set of the right size.");
        public static readonly QaRule TexAniso = new QaRule("TEX.ANISO", C.Texture, S.Minor,
            "Ground texture without anisotropic filtering", "Floors and terrain smear at grazing angles.",
            "Set Aniso Level >= 4 (or force anisotropic filtering in Quality settings).");
        public static readonly QaRule TexReadWrite = new QaRule("TEX.READ_WRITE", C.Texture, S.Minor,
            "Read/Write enabled", "Keeps a CPU copy: double memory.",
            "Disable Read/Write unless a script reads pixels at runtime.");

        // ------------------------------------------------------------------------------------------------ materials
        public static readonly QaRule MatShaderError = new QaRule("MAT.SHADER_ERROR", C.Material, S.Blocker,
            "Missing or broken shader (pink)", "Renders magenta in game.",
            "Assign a working URP shader; fix shader compile errors.");
        public static readonly QaRule MatLegacyShader = new QaRule("MAT.LEGACY_SHADER", C.Material, S.Critical,
            "Built-in pipeline shader", "Standard/Legacy shaders do not render under URP.",
            "Convert to Universal Render Pipeline/Lit (Edit > Rendering > Materials > Convert).");
        public static readonly QaRule MatMissingBaseMap = new QaRule("MAT.MISSING_BASEMAP", C.Material, S.Major,
            "Base map empty although an albedo texture exists", "Flat-colored surface next to its own unused texture: classic import slip.",
            "Assign the albedo texture to Base Map.");
        public static readonly QaRule MatMissingNormalMap = new QaRule("MAT.MISSING_NORMALMAP", C.Material, S.Minor,
            "Normal map available but not assigned", "Surface detail is lost; looks plastic.",
            "Assign it to Normal Map (and enable _NORMALMAP).");
        public static readonly QaRule MatAlbedoRange = new QaRule("MAT.ALBEDO_RANGE", C.Material, S.Major,
            "Albedo outside the PBR range", "Real albedo sits between ~30 and ~240 sRGB; darker/brighter reads as fake under physical lighting.",
            "Re-level the base map / base color into the PBR range (charcoal ~50, fresh snow ~240).");
        public static readonly QaRule MatSmoothnessRange = new QaRule("MAT.SMOOTHNESS_RANGE", C.Material, S.Minor,
            "Implausible smoothness", "Mirror-smooth dielectrics (> 0.85) look wet/plastic; medieval materials are mostly rough.",
            "Use a smoothness map or values in 0.1-0.7 for dielectrics.");
        public static readonly QaRule MatMetallicRange = new QaRule("MAT.METALLIC_RANGE", C.Material, S.Major,
            "Metallic neither 0 nor 1", "Metalness is binary in PBR; in-between constants look like grey plastic.",
            "Use 0 or 1, or drive it from a mask map.");
        public static readonly QaRule MatEmissionUnjustified = new QaRule("MAT.EMISSION_UNJUSTIFIED", C.Material, S.Minor,
            "Emission without an emissive source", "Glowing props without a fire/lamp/magic reason look like an import mistake.",
            "Disable emission or name the material after its emitter (Torch, Fire, Candle, Lamp, Ember, Rune).");
        public static readonly QaRule MatTexelMismatch = new QaRule("MAT.TEXEL_MISMATCH", C.Material, S.Minor,
            "Inconsistent map resolutions", "A 4K albedo with a 512 normal map (or vice versa) shows mismatched detail.",
            "Author the maps of one material at the same resolution.");
        public static readonly QaRule MatMetalAlbedo = new QaRule("MAT.METAL_ALBEDO", C.Material, S.Major,
            "Dark metal", "A metal's albedo is its reflectance; under ~150 sRGB it reads as plastic or soot (ART_BIBLE §5.1).",
            "Raise the base colour: worn iron 150-190, bright steel 180-220 sRGB.");
        public static readonly QaRule MatSmoothnessByClass = new QaRule("MAT.SMOOTHNESS_BY_CLASS", C.Material, S.Minor,
            "Smoothness wrong for the material", "Wool, leather, wood, iron and stone each have a smoothness range (ART_BIBLE §5.1); outside it they read as plastic or wet.",
            "Bring the smoothness (scalar or map) into the class range named in the message.");

        // ------------------------------------------------------------------------------------------------ models
        public static readonly QaRule ModelScale = new QaRule("MODEL.SCALE", C.Model, S.Major,
            "Implausible imported size", "Wrong unit/scale factor (cm vs m) makes physics, NavMesh and lighting wrong.",
            "Fix the size in the source (1 unit = 1 m); characters are 1.60-2.00 m with a helmet.");
        public static readonly QaRule ModelNoNormals = new QaRule("MODEL.NO_NORMALS", C.Model, S.Major,
            "Mesh without normals", "Lighting is undefined.",
            "Import or calculate normals.");
        public static readonly QaRule ModelNoTangents = new QaRule("MODEL.NO_TANGENTS", C.Model, S.Major,
            "Normal-mapped mesh without tangents", "Normal maps shade incorrectly.",
            "Set Tangents = Calculate Mikktspace.");
        public static readonly QaRule ModelNoUv0 = new QaRule("MODEL.NO_UV0", C.Model, S.Major,
            "Mesh without UV0", "Cannot be textured.",
            "Unwrap the mesh in the DCC tool.");
        public static readonly QaRule ModelUvOverlap = new QaRule("MODEL.UV_OVERLAP", C.Model, S.Minor,
            "Heavily overlapping UV0", "Stacked UVs repeat detail visibly (and break baked maps).",
            "Give unique UV space to visible unique surfaces; mirror only hidden parts.");
        public static readonly QaRule ModelPolyBudget = new QaRule("MODEL.POLY_BUDGET", C.Model, S.Major,
            "Triangle budget exceeded", "Frame time and memory; ceilings (QUALITY §2): hero 120k, enemy 60k, weapon 8k, shield 6k, banner 4k, rock 12k, prop 15k.",
            "Decimate/retopologize or add LODs.");
        public static readonly QaRule ModelPolyTooLow = new QaRule("MODEL.POLY_TOO_LOW", C.Model, S.Minor,
            "Suspiciously low triangle count for a hero asset", "Blocky silhouettes read as placeholder art.",
            "Use the final asset or add silhouette detail.");
        public static readonly QaRule ModelReadWrite = new QaRule("MODEL.READ_WRITE", C.Model, S.Minor,
            "Mesh Read/Write enabled", "Keeps a CPU copy of every mesh.",
            "Disable Read/Write unless runtime code reads vertices.");
        public static readonly QaRule ModelAvatarInvalid = new QaRule("MODEL.AVATAR_INVALID", C.Model, S.Critical,
            "Humanoid avatar invalid", "Retargeted animations cannot play.",
            "Fix the bone mapping in the rig tab (Configure Avatar).");
        public static readonly QaRule ModelImportScale = new QaRule("MODEL.IMPORT_SCALE", C.Model, S.Major,
            "Model rescaled by its importer", "1 unit = 1 m must hold in the source, or every density, collider and LOD distance is wrong.",
            "Rescale and apply transforms in Blender, re-export at 1.0; importer Scale Factor 1 with Use File Scale on.");
        public static readonly QaRule ScaleWeaponClass = new QaRule("SCALE.WEAPON_CLASS", C.Model, S.Major,
            "Weapon or shield not at its real size", "A two-metre arming sword passes a generic weapon band; each class has a period size (QUALITY Scale & proportion).",
            "Scale the source to the class range named in the message.");
        public static readonly QaRule ModelTexelDensity = new QaRule("MODEL.TEXEL_DENSITY", C.Model, S.Major,
            "Texel density outside its tier", "Blurred cloth next to a sharp face breaks the art bible's consistency pillar (ART_BIBLE §6).",
            "Re-UV or change the map size so the material reaches its tier.");
        public static readonly QaRule ModelNoLods = new QaRule("MODEL.NO_LODS", C.Model, S.Major,
            "Missing or wrong LODs", "Characters and meshes over 5k triangles need LOD1-3 at 50 / 25 / 10 % (QUALITY §2).",
            "Make LODs in Blender (decimate, then fix the silhouette by hand) or add a LODGroup.");
        public static readonly QaRule ModelOpenEdges = new QaRule("MODEL.OPEN_EDGES", C.Model, S.Major,
            "Holes in a closed mesh", "Weapons, shields, rocks and props are closed; open edges show as cracks and faceting.",
            "Weld by distance in Blender and close the mesh.");
        public static readonly QaRule ModelNonManifold = new QaRule("MODEL.NONMANIFOLD", C.Model, S.Major,
            "Non-manifold edges", "Edges shared by more than two triangles break normals, LODs and decimation.",
            "Select Non-Manifold in Blender and fix the geometry.");
        public static readonly QaRule ModelLooseVerts = new QaRule("MODEL.LOOSE_VERTS", C.Model, S.Minor,
            "Loose vertices", "Vertices no triangle uses are still skinned and uploaded.",
            "Delete Loose in Blender.");
        public static readonly QaRule ModelFacetedWeapon = new QaRule("MODEL.FACETED_WEAPON", C.Model, S.Minor,
            "Low-poly held weapon", "A blade with too few triangles shows flat facets in close-ups.",
            "Add edge loops / bevels (2-6k triangles) and bake normals.");
        public static readonly QaRule ModelGeneratedSignature = new QaRule("MODEL.GENERATED_SIGNATURE", C.Model, S.Info,
            "Looks like a generator export", "Generated characters carry baked views and symbols of the prompt in their atlas (AUDIT_M1 #1).",
            "Send it to the Asset Quality Gate before it is used anywhere.");

        // ------------------------------------------------------------------------------------------------ animation
        public static readonly QaRule AnimLoopFlag = new QaRule("ANIM.LOOP_FLAG", C.Animation, S.Major,
            "Wrong loop flag", "Locomotion that does not loop pops at the end; one-shot actions that loop never finish.",
            "Loop Time on idle/walk/run/strafe; off for attacks, hits, deaths, transitions.");
        public static readonly QaRule AnimRootMotion = new QaRule("ANIM.ROOT_MOTION_INCONSISTENT", C.Animation, S.Major,
            "Root motion import differs from its clip family", "Mixed bake settings in one set (AX_/SS_/GK_) make characters drift, slide or pop.",
            "Match Bake Into Pose / Based Upon settings with the rest of the family.");

        // ------------------------------------------------------------------------------------------------ audio
        public static readonly QaRule AudioSampleRate = new QaRule("AUDIO.SAMPLE_RATE", C.Audio, S.Minor,
            "Unusual sample rate", "Below 44.1 kHz sounds dull; above 48 kHz wastes memory.",
            "Use 48 kHz (or 44.1 kHz) sources.");
        public static readonly QaRule Audio3dStereo = new QaRule("AUDIO.3D_STEREO", C.Audio, S.Major,
            "Stereo 3D sound effect", "3D spatialization uses mono; stereo doubles memory and collapses unpredictably.",
            "Tick Force To Mono (or export mono) for positional SFX.");
        public static readonly QaRule AudioLoadType = new QaRule("AUDIO.LOAD_TYPE", C.Audio, S.Major,
            "Load type does not fit clip length", "Long clips decompressed on load eat memory; tiny clips streamed add latency.",
            "Streaming for music/ambience > 10 s, Decompress On Load / Compressed In Memory for short SFX.");
        public static readonly QaRule AudioClipping = new QaRule("AUDIO.CLIPPING", C.Audio, S.Major,
            "Digital clipping", "Flat-topped samples crackle and sound cheap.",
            "Re-export with a limiter at -1 dBFS.");
        public static readonly QaRule AudioNoHeadroom = new QaRule("AUDIO.NO_HEADROOM", C.Audio, S.Minor,
            "Peak above -0.3 dBFS", "Codec overshoot clips after compression.",
            "Normalize peaks to -1 dBFS.");
        public static readonly QaRule AudioLoudness = new QaRule("AUDIO.LOUDNESS", C.Audio, S.Minor,
            "Loudness out of range", "Clips far quieter/louder than the rest break the mix.",
            "Normalize SFX to roughly -20..-10 dBFS RMS.");
        public static readonly QaRule AudioLeadingSilence = new QaRule("AUDIO.LEADING_SILENCE", C.Audio, S.Major,
            "Silence at the start", "Sounds play late (hits feel laggy).",
            "Trim the head to < 50 ms.");
        public static readonly QaRule AudioDcOffset = new QaRule("AUDIO.DC_OFFSET", C.Audio, S.Minor,
            "DC offset", "Clicks at start/stop and reduced headroom.",
            "Apply a DC-offset / high-pass filter.");
        public static readonly QaRule AudioUnanalysed = new QaRule("AUDIO.UNANALYSED", C.Audio, S.Info,
            "Audio not analysed", "The source format could not be decoded for level analysis.",
            "Keep WAV sources in the project or check levels in the DAW.");

        // ------------------------------------------------------------------------------------------------ prefabs
        public static readonly QaRule PrefabMissingScript = new QaRule("PREFAB.MISSING_SCRIPT", C.Prefab, S.Blocker,
            "Missing script", "Component data is lost and behaviour is missing.",
            "Restore the script or remove the component.");
        public static readonly QaRule PrefabMissingReference = new QaRule("PREFAB.MISSING_REFERENCE", C.Prefab, S.Critical,
            "Broken object reference", "Points to a deleted asset/object: NullReference at runtime.",
            "Reassign the reference.");
        public static readonly QaRule PrefabMissingPrefab = new QaRule("PREFAB.MISSING_PREFAB", C.Prefab, S.Critical,
            "Missing nested prefab", "The nested prefab asset was deleted.",
            "Restore the asset or unpack/remove the instance.");
        public static readonly QaRule PrefabMissingMaterial = new QaRule("PREFAB.MISSING_MATERIAL", C.Prefab, S.Critical,
            "Renderer with empty material slot", "Renders magenta or not at all.",
            "Assign a material to every slot.");
        public static readonly QaRule PrefabZeroCollider = new QaRule("PREFAB.ZERO_COLLIDER", C.Prefab, S.Major,
            "Degenerate collider", "Zero-size colliders never collide (or explode in physics).",
            "Fit the collider to the mesh.");
        public static readonly QaRule PrefabRigidbodyNoCollider = new QaRule("PREFAB.RIGIDBODY_NO_COLLIDER", C.Prefab, S.Major,
            "Rigidbody without collider", "Falls through the world.",
            "Add a collider (or remove the Rigidbody).");
        public static readonly QaRule PrefabLayer = new QaRule("PREFAB.LAYER", C.Prefab, S.Major,
            "Wrong physics layer", "Layer table: Character 8 (CharacterController), Hurtbox 9 (ragdoll/hurtbox colliders), Terrain 10.",
            "Put the object on the layer its role requires.");
        public static readonly QaRule PrefabCcDimensions = new QaRule("PREFAB.CC_DIMENSIONS", C.Prefab, S.Major,
            "CharacterController dimensions implausible", "Wrong capsule size makes the character float, sink or snag on steps.",
            "Height 1.5-2.2 m, radius 0.2-0.6 m, center.y = height/2, skin width ~10% of radius.");
        public static readonly QaRule PrefabLodMissing = new QaRule("PREFAB.LOD_MISSING", C.Prefab, S.Minor,
            "Large mesh without LODGroup", "Full-detail geometry rendered at any distance.",
            "Add LODs (or a LODGroup with a cull level).");
        public static readonly QaRule ScaleArtRescaled = new QaRule("SCALE.ART_RESCALED", C.Prefab, S.Major,
            "Art rescaled in a prefab or scene", "A builder that rescales an asset hides a wrong source and changes its texel density.",
            "Fix the source size; set the instance's localScale to 1.");
        public static readonly QaRule ModelTexelNeighbour = new QaRule("MODEL.TEXEL_NEIGHBOUR", C.Prefab, S.Minor,
            "Texel density mismatch inside one prefab", "One sharp part beside a blurred one reads worse than two average ones.",
            "Bring both materials into their tiers.");
        public static readonly QaRule PrefabShadowTiny = new QaRule("PREFAB.SHADOW_TINY", C.Prefab, S.Minor,
            "Tiny renderer casts shadows", "Shadow draw calls for objects whose shadow is invisible.",
            "Set Cast Shadows = Off on small props (< 25 cm).");

        // ------------------------------------------------------------------------------------------------ scenes
        public static readonly QaRule SceneMissingScript = new QaRule("SCENE.MISSING_SCRIPT", C.Scene, S.Blocker,
            "Missing script in scene", "Lost behaviour and data.", "Restore the script or remove the component.");
        public static readonly QaRule SceneMissingReference = new QaRule("SCENE.MISSING_REFERENCE", C.Scene, S.Critical,
            "Broken object reference in scene", "Points to a deleted asset/object.", "Reassign the reference.");
        public static readonly QaRule SceneMissingPrefab = new QaRule("SCENE.MISSING_PREFAB", C.Scene, S.Critical,
            "Missing prefab instance", "The prefab asset was deleted.", "Restore it or remove the instance.");
        public static readonly QaRule SceneMissingMaterial = new QaRule("SCENE.MISSING_MATERIAL", C.Scene, S.Critical,
            "Renderer with empty material slot", "Magenta or invisible object.", "Assign a material.");
        public static readonly QaRule SceneVoid = new QaRule("SCENE.VOID", C.Scene, S.Critical,
            "Nothing underneath", "The object or spawn point has no ground below it: falls into the void.",
            "Move it onto the terrain/floor or add ground.");
        public static readonly QaRule SceneBuried = new QaRule("SCENE.BURIED", C.Scene, S.Major,
            "Object below the terrain", "Invisible clutter or a spawn inside the ground.", "Raise it above the terrain surface.");
        public static readonly QaRule SceneFloating = new QaRule("SCENE.FLOATING", C.Scene, S.Major,
            "Floating prop", "The bottom of the object hovers above the ground — the most visible set-dressing mistake.",
            "Snap it to the ground (V-snap / End key).");
        public static readonly QaRule SceneSunk = new QaRule("SCENE.SUNK", C.Scene, S.Major,
            "Prop sunk into the ground", "More than 30% of its height is below the surface.", "Raise it or scale it down.");
        public static readonly QaRule SceneDuplicateMesh = new QaRule("SCENE.DUPLICATE_MESH", C.Scene, S.Major,
            "Duplicate overlapping mesh", "Same mesh at the same transform: z-fighting and wasted draw calls.", "Delete the duplicate.");
        public static readonly QaRule SceneSunShadows = new QaRule("SCENE.SUN_SHADOWS", C.Scene, S.Major,
            "Directional light without shadows", "Unanchored, flat-looking scene.", "Enable soft shadows on the sun.");
        public static readonly QaRule SceneMultipleSuns = new QaRule("SCENE.MULTIPLE_SUNS", C.Scene, S.Major,
            "More than one active directional light", "Double shadows and blown-out lighting.", "Keep one sun; use fill via ambient/probes.");
        public static readonly QaRule SceneLightShadows = new QaRule("SCENE.LIGHT_SHADOWS", C.Scene, S.Minor,
            "Large light without shadows", "Light leaks through walls.", "Enable shadows or reduce the range.");
        public static readonly QaRule ScenePostFx = new QaRule("SCENE.POSTFX_RANGE", C.Scene, S.Major,
            "Post-processing outside sane bounds", "Over-blown bloom, heavy vignette/saturation/aberration read as 'AI look'.",
            "Bloom intensity <= 1.5 (threshold >= 0.8), |post exposure| <= 2, saturation <= +30, vignette <= 0.45, chromatic aberration <= 0.3.");
        public static readonly QaRule SceneNoTonemapping = new QaRule("SCENE.NO_TONEMAPPING", C.Scene, S.Major,
            "No tonemapping", "HDR lighting clips to white without a tonemapper.", "Add Tonemapping (ACES or Neutral) to the global volume.");
        public static readonly QaRule SceneCamera = new QaRule("SCENE.CAMERA", C.Scene, S.Minor,
            "Camera setup problem", "Post-processing disabled, extreme clip planes or competing cameras.",
            "Enable post-processing on the main camera, near >= 0.05 m, far/near ratio <= 1e5, one camera per depth.");
        public static readonly QaRule SceneNavMeshMissing = new QaRule("SCENE.NAVMESH_MISSING", C.Scene, S.Critical,
            "No NavMesh", "AI cannot move.", "Bake the NavMesh (NavMeshSurface) for the scene.");
        public static readonly QaRule SceneNavMeshCoverage = new QaRule("SCENE.NAVMESH_COVERAGE", C.Scene, S.Critical,
            "Point not on the NavMesh", "Spawn points, quest locations and agents must stand on walkable NavMesh.",
            "Move the point onto the NavMesh or re-bake.");
        public static readonly QaRule SceneAudioListeners = new QaRule("SCENE.AUDIO_LISTENERS", C.Scene, S.Critical,
            "AudioListener count is not 1", "No sound, or Unity warning spam and wrong panning.", "Exactly one active AudioListener (on the gameplay camera).");
        public static readonly QaRule SceneUiDuplicates = new QaRule("SCENE.UI_DUPLICATES", C.Scene, S.Major,
            "Duplicate EventSystem/UIDocument", "Double input handling and overlapping UI.", "Keep one EventSystem and one UIDocument per panel.");
        public static readonly QaRule SceneStaticFlags = new QaRule("SCENE.STATIC_FLAGS", C.Scene, S.Minor,
            "Environment not static", "No static batching, occlusion or baked GI for scenery.", "Mark scenery Static (Batching, Occluder/Occludee, Contribute GI).");
        public static readonly QaRule ScenePrimitiveMesh = new QaRule("SCENE.PRIMITIVE_MESH", C.Scene, S.Major,
            "Built-in primitive in a shipping scene", "Grey-box cubes/spheres/capsules look like placeholders.",
            "Replace with real art or tag the object EditorOnly.");
        public static readonly QaRule SceneTestAsset = new QaRule("SCENE.TEST_ASSET", C.Scene, S.Major,
            "Test or placeholder asset in a shipping scene", "M1 exit: no placeholder in view.",
            "Remove it or replace it with the real asset.");
        public static readonly QaRule SceneSkySunMatch = new QaRule("SCENE.SKY_SUN_MATCH", C.Scene, S.Minor,
            "Sky and sun disagree", "The sky texture's sun height and the directional light's must agree, or sky colour and ambient contradict the key light.",
            "Pick a sky shot at the scene's time of day, or set its sun elevation in Docs/qa_config.json (scene.skySuns).");
        public static readonly QaRule ScenePlaceholderName = new QaRule("SCENE.PLACEHOLDER_NAME", C.Scene, S.Minor,
            "Placeholder object name", "'GameObject (3)', 'Cube' make scenes unmaintainable and logs unreadable.",
            "Give the object a descriptive name.");
        public static readonly QaRule SceneSkipped = new QaRule("SCENE.SKIPPED", C.Scene, S.Critical,
            "Scene not validated", "The gate cannot pass without validating every build scene.",
            "Save or discard changes in the open scenes, leave play mode, and rerun qa.");
        public static readonly QaRule SceneNotInBuild = new QaRule("SCENE.NOT_IN_BUILD", C.Scene, S.Minor,
            "Scene not in Build Settings", "Not validated, not shipped — forgotten or missing from the build list.",
            "Add it to Build Settings or move it out of Scenes/.");

        // ------------------------------------------------------------------------------------------------ data
        public static readonly QaRule DataIdEmpty = new QaRule("DATA.ID_EMPTY", C.Data, S.Critical,
            "Definition without id", "Cannot be saved, referenced or localized.", "Set a stable lower_snake_case id.");
        public static readonly QaRule DataIdDuplicate = new QaRule("DATA.ID_DUPLICATE", C.Data, S.Blocker,
            "Duplicate definition id", "Saves and references resolve to the wrong content.", "Give every definition a unique id.");
        public static readonly QaRule DataIdFormat = new QaRule("DATA.ID_FORMAT", C.Data, S.Major,
            "Malformed id", "Ids are lower_snake_case ASCII (a-z 0-9 _ .); anything else breaks keys and file names.",
            "Rename the id (before it ships — ids never change afterwards).");
        public static readonly QaRule DataDatabaseStale = new QaRule("DATA.DATABASE_STALE", C.Data, S.Critical,
            "GameDatabase out of date", "Definitions missing from the index do not exist at runtime.", "Run the bridge command 'database'.");
        public static readonly QaRule DataDanglingId = new QaRule("DATA.DANGLING_ID", C.Data, S.Critical,
            "Reference to an unknown id", "An *Id field names content that does not exist.", "Fix the id or create the definition.");
        public static readonly QaRule DataAttackTiming = new QaRule("DATA.ATTACK_TIMING", C.Data, S.Critical,
            "Attack timing out of order", "Requires 0 <= hitStart < hitEnd < comboWindow <= end <= 1; otherwise hits never land or combos skip.",
            "Re-run the combat builder or fix the normalized timings.");
        public static readonly QaRule DataAttackValues = new QaRule("DATA.ATTACK_VALUES", C.Data, S.Major,
            "Attack numbers out of range", "Damage/stamina/poise outside the balance envelope for its kind.",
            "Bring the values into the documented range (Docs/QA.md) or justify with a suppression.");
        public static readonly QaRule DataUnblockableTelegraph = new QaRule("DATA.UNBLOCKABLE_TELEGRAPH", C.Data, S.Critical,
            "Unblockable without telegraph", "Unblockables need a readable wind-up (>= 0.5 s before the hit) or they feel unfair.",
            "Slow the wind-up (speed/hitStart) or remove unblockable.");
        public static readonly QaRule DataActionClip = new QaRule("DATA.ACTION_CLIP", C.Data, S.Major,
            "Action clip timings inconsistent", "cancelFrom after end, or i-frames out of order.", "Fix the normalized timings.");
        public static readonly QaRule DataDodgeIFrames = new QaRule("DATA.DODGE_IFRAMES", C.Data, S.Major,
            "Dodge without invulnerability", "A dodge that cannot avoid damage is broken game feel.", "Give dodges an i-frame window.");
        public static readonly QaRule DataMoveSetState = new QaRule("DATA.MOVESET_STATE", C.Data, S.Critical,
            "MoveSet state missing from the AnimatorController", "CrossFade to a missing state does nothing: the move silently fails.",
            "Rebuild the controller (combat builder) or fix the state name.");
        public static readonly QaRule DataMoveSetClip = new QaRule("DATA.MOVESET_CLIP", C.Data, S.Critical,
            "MoveSet entry without clip/state", "Required move has no animation.", "Assign the clip and state.");
        public static readonly QaRule DataPriceTier = new QaRule("DATA.PRICE_TIER", C.Data, S.Major,
            "Prices not monotonic with rarity", "Rarer items cheaper than common ones break the economy.", "Re-price the outliers.");
        public static readonly QaRule DataItemValue = new QaRule("DATA.ITEM_VALUE", C.Data, S.Major,
            "Sellable item worth nothing", "Value 0 on a non-quest item.", "Give it a value.");
        public static readonly QaRule DataShopStock = new QaRule("DATA.SHOP_STOCK", C.Data, S.Critical,
            "Invalid shop stock", "Stock references a missing item, a quest item or has a non-positive price multiplier.",
            "Fix the stock entry.");
        public static readonly QaRule DataShopArbitrage = new QaRule("DATA.SHOP_ARBITRAGE", C.Data, S.Critical,
            "Sell price >= buy price", "Infinite-money exploit (buy, sell, repeat).", "Keep sellMultiplier < buyMultiplier.");
        public static readonly QaRule DataLootTable = new QaRule("DATA.LOOT_TABLE", C.Data, S.Critical,
            "Invalid loot table", "Zero/negative weights, inverted ranges, empty or cyclic entries.", "Fix the entry.");

        // ------------------------------------------------------------------------------------------------ localization
        public static readonly QaRule LocBadJson = new QaRule("LOC.BAD_JSON", C.Localization, S.Blocker,
            "String table does not parse", "The whole table is dropped at runtime.", "Fix the JSON syntax.");
        public static readonly QaRule LocMissingKey = new QaRule("LOC.MISSING_KEY", C.Localization, S.Critical,
            "Definition text key missing", "The UI shows '#key' to the player.", "Add the key to Resources/Localization/{ka,en}/<module>.json.");
        public static readonly QaRule LocKeyParity = new QaRule("LOC.KEY_PARITY", C.Localization, S.Critical,
            "Key exists in only one language", "One language falls back or shows '#key'.", "Add the missing translation.");
        public static readonly QaRule LocDuplicateKey = new QaRule("LOC.DUPLICATE_KEY", C.Localization, S.Major,
            "Duplicate key", "The last one silently wins.", "Keep one entry per key.");
        public static readonly QaRule LocEmpty = new QaRule("LOC.EMPTY", C.Localization, S.Critical,
            "Empty translation", "Blank text in the UI.", "Write the text.");
        public static readonly QaRule LocNotGeorgian = new QaRule("LOC.NOT_GEORGIAN", C.Localization, S.Critical,
            "ka value is not Georgian", "English or transliterated text left in the Georgian table.", "Translate into Georgian (Mkhedruli).");
        public static readonly QaRule LocMixedScript = new QaRule("LOC.MIXED_SCRIPT", C.Localization, S.Major,
            "Mixed Georgian/Latin text", "Half-translated strings.", "Translate the Latin words (input glyph names excepted).");
        public static readonly QaRule LocMojibake = new QaRule("LOC.MOJIBAKE", C.Localization, S.Critical,
            "Encoding damage (mojibake)", "Text was saved in the wrong encoding.", "Re-save the file as UTF-8 and re-enter the text.");
        public static readonly QaRule LocPlaceholder = new QaRule("LOC.PLACEHOLDER_TEXT", C.Localization, S.Critical,
            "Placeholder text", "lorem/TODO/test/item1 visible to players.", "Write the final text.");
        public static readonly QaRule LocUntranslated = new QaRule("LOC.UNTRANSLATED", C.Localization, S.Critical,
            "Untranslated value", "Value equals the key or the other language.", "Translate it.");
        public static readonly QaRule LocFormatMismatch = new QaRule("LOC.FORMAT_MISMATCH", C.Localization, S.Critical,
            "Format placeholders differ between languages", "Loc.Format drops data or throws.", "Use the same {0}/{1} set in ka and en.");
        public static readonly QaRule LocTypography = new QaRule("LOC.TYPOGRAPHY", C.Localization, S.Minor,
            "Typography slip", "Leading/trailing/double spaces, straight quotes in Georgian (use „…“), '...' instead of '…'.",
            "Clean the string.");
        public static readonly QaRule LocCodeKeyMissing = new QaRule("LOC.CODE_KEY_MISSING", C.Localization, S.Critical,
            "Key used in code but missing from the tables", "Players see '#key'.", "Add the key to both tables.");
        public static readonly QaRule LocUxmlLiteral = new QaRule("LOC.UXML_LITERAL", C.Localization, S.Major,
            "Hard-coded text in UXML", "Untranslatable static text.", "Bind the element to a key (LocBinder) and leave text empty.");

        // ------------------------------------------------------------------------------------------------ visual
        public static readonly QaRule VisUnderexposed = new QaRule("VIS.UNDEREXPOSED", C.Visual, S.Major,
            "Frame too dark", "Mean luminance below 0.18.", "Raise exposure/sun/ambient; check the volume.");
        public static readonly QaRule VisOverexposed = new QaRule("VIS.OVEREXPOSED", C.Visual, S.Major,
            "Frame too bright", "Mean luminance above 0.70.", "Lower exposure or sun intensity.");
        public static readonly QaRule VisClipped = new QaRule("VIS.CLIPPED_HIGHLIGHTS", C.Visual, S.Major,
            "Clipped highlights", "More than 5% of pixels blown to white.", "Tonemap, lower bloom/exposure.");
        public static readonly QaRule VisCrushed = new QaRule("VIS.CRUSHED_BLACKS", C.Visual, S.Major,
            "Crushed blacks", "More than 10% of pixels are pure black.", "Lift shadows (ambient, probes, exposure).");
        public static readonly QaRule VisOversaturated = new QaRule("VIS.OVERSATURATED", C.Visual, S.Minor,
            "Oversaturated frame", "Mean saturation above 0.6 reads as cheap/'AI look'.", "Reduce saturation in grading or albedo.");
        public static readonly QaRule VisFlat = new QaRule("VIS.FLAT", C.Visual, S.Minor,
            "Flat, low-contrast frame", "Luminance std-dev below 0.08: hazy, no focal point.", "Add key/fill contrast, reduce fog.");
        public static readonly QaRule VisNoViews = new QaRule("VIS.NO_VIEWS", C.Visual, S.Minor,
            "Scene has no review viewpoints", "No spawn points or QAView markers to shoot from.", "Add QAView_* empties at representative spots.");
        public static readonly QaRule VisNotCaptured = new QaRule("VIS.NOT_CAPTURED", C.Visual, S.Info,
            "No screenshot metrics", "qa.shots has not been run (or its results are older than the scene), so exposure was not checked.",
            "Run the bridge command 'qa.shots' before 'qa'.");

        // ------------------------------------------------------------------------------------------------ meta
        public static readonly QaRule MetaValidatorCrash = new QaRule("QA.VALIDATOR_CRASH", C.Meta, S.Critical,
            "Validator crashed", "Part of the project was not validated.", "Fix the exception (see message) and rerun.");
        public static readonly QaRule MetaConfigInvalid = new QaRule("QA.CONFIG_INVALID", C.Meta, S.Major,
            "Invalid QA config override", "Docs/qa_config.json could not be applied; defaults are in use.", "Fix the JSON (field names as in QaSettings).");
        public static readonly QaRule MetaSuppressionInvalid = new QaRule("QA.SUPPRESSION_INVALID", C.Meta, S.Major,
            "Invalid suppression entry", "Suppressions need id, path and a reason so exceptions stay explicit.", "Complete or delete the entry.");
        public static readonly QaRule MetaSuppressionExpired = new QaRule("QA.SUPPRESSION_EXPIRED", C.Meta, S.Major,
            "Suppression expired", "The accepted exception's 'until' date has passed.", "Fix the issue or renew the date with a reason.");
        public static readonly QaRule MetaSuppressionStale = new QaRule("QA.SUPPRESSION_STALE", C.Meta, S.Minor,
            "Suppression matches nothing", "The issue was fixed; dead entries hide future regressions.", "Delete the entry.");

        static List<QaRule> all;
        static Dictionary<string, QaRule> byId;

        /// <summary>Every rule, in declaration order.</summary>
        public static IList<QaRule> All
        {
            get
            {
                if (all != null) return all;
                var list = new List<QaRule>();
                foreach (var f in typeof(QaRules).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    var r = f.GetValue(null) as QaRule;
                    if (r != null) list.Add(r);
                }
                all = list;
                return all;
            }
        }

        public static QaRule Find(string id)
        {
            if (byId == null)
            {
                var d = new Dictionary<string, QaRule>(StringComparer.Ordinal);
                foreach (var r in All) d[r.Id] = r;
                byId = d;
            }
            QaRule rule;
            return id != null && byId.TryGetValue(id, out rule) ? rule : null;
        }
    }
}
