using System;
using System.Collections.Generic;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// Project-wide map of declared types by simple name with inheritance queries ("is this a MonoBehaviour?",
    /// "which fields of this ScriptableObject are serialized?"). Names are resolved without namespaces, which is
    /// precise enough for lint rules.
    /// </summary>
    public sealed class CsTypeIndex
    {
        readonly Dictionary<string, List<CsType>> byName = new Dictionary<string, List<CsType>>(StringComparer.Ordinal);
        readonly Dictionary<string, bool> unityCache = new Dictionary<string, bool>(StringComparer.Ordinal);

        /// <summary>Engine/package types deriving from UnityEngine.Object (used when a declared type is not in the project).</summary>
        public static readonly HashSet<string> EngineObjectTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Object", "GameObject", "Transform", "RectTransform", "Component", "Behaviour", "MonoBehaviour", "ScriptableObject",
            "StateMachineBehaviour", "Camera", "Light", "Animator", "Animation", "AnimationClip", "RuntimeAnimatorController",
            "AnimatorOverrideController", "AnimatorController", "Avatar", "AvatarMask", "Rigidbody", "Rigidbody2D", "Collider",
            "Collider2D", "BoxCollider", "SphereCollider", "CapsuleCollider", "MeshCollider", "TerrainCollider", "WheelCollider",
            "CharacterController", "Renderer", "MeshRenderer", "SkinnedMeshRenderer", "LineRenderer", "TrailRenderer",
            "SpriteRenderer", "ParticleSystem", "ParticleSystemRenderer", "MeshFilter", "Mesh", "Material", "Shader", "Texture",
            "Texture2D", "Texture3D", "Texture2DArray", "RenderTexture", "Cubemap", "Sprite", "Font", "AudioSource", "AudioClip",
            "AudioListener", "AudioMixer", "AudioMixerGroup", "AudioMixerSnapshot", "AudioReverbZone", "TextAsset", "Terrain",
            "TerrainData", "TerrainLayer", "NavMeshAgent", "NavMeshObstacle", "NavMeshSurface", "NavMeshLink", "NavMeshModifier",
            "OffMeshLink", "Canvas", "CanvasGroup", "CanvasRenderer", "Image", "RawImage", "Text", "Button", "Slider", "Toggle",
            "Scrollbar", "ScrollRect", "Dropdown", "InputField", "UIDocument", "PanelSettings", "VisualTreeAsset", "StyleSheet",
            "ThemeStyleSheet", "TMP_Text", "TextMeshProUGUI", "TextMeshPro", "TMP_FontAsset", "PlayableDirector", "TimelineAsset",
            "PlayableAsset", "TrackAsset", "Volume", "VolumeProfile", "VolumeComponent", "ReflectionProbe", "LightProbeGroup",
            "LODGroup", "Joint", "CharacterJoint", "ConfigurableJoint", "HingeJoint", "FixedJoint", "SpringJoint", "Cloth",
            "WindZone", "Tilemap", "EventSystem", "PhysicMaterial", "PhysicsMaterial", "CinemachineCamera", "CinemachineBrain",
            "CinemachineVirtualCamera", "CinemachineImpulseSource", "CinemachineVirtualCameraBase", "InputActionAsset",
            "PlayerInput", "MonoScript", "EditorWindow", "Editor", "AssetImporter", "TextureImporter", "ModelImporter",
            "AudioImporter", "SceneAsset", "DefaultAsset", "LightingSettings", "Flare", "LensFlare", "Projector", "Skybox",
            "OcclusionArea", "OcclusionPortal", "RigBuilder", "Rig", "TwoBoneIKConstraint", "MultiAimConstraint",
            "UniversalAdditionalCameraData", "UniversalAdditionalLightData", "DecalProjector", "SplineContainer"
        };

        static readonly HashSet<string> ComponentRoots = new HashSet<string>(StringComparer.Ordinal) { "MonoBehaviour" };
        static readonly HashSet<string> AssetRoots = new HashSet<string>(StringComparer.Ordinal) { "ScriptableObject", "StateMachineBehaviour" };
        static readonly HashSet<string> EditorBases = new HashSet<string>(StringComparer.Ordinal)
        {
            "Editor", "EditorWindow", "ScriptableWizard", "ScriptableSingleton", "EditorTool", "SettingsProvider", "AssetPostprocessor",
            "ScriptedImporter", "PropertyDrawer", "DecoratorDrawer"
        };

        public void Add(CSharpFile file)
        {
            foreach (var t in file.Types)
            {
                List<CsType> list;
                if (!byName.TryGetValue(t.Name, out list)) { list = new List<CsType>(); byName.Add(t.Name, list); }
                list.Add(t);
            }
            unityCache.Clear();
        }

        public bool Declares(string name) { return byName.ContainsKey(CSharpFile.SimpleTypeName(name ?? "")); }

        /// <summary>True when <paramref name="typeName"/> derives (transitively) from any of <paramref name="roots"/>.</summary>
        public bool DerivesFrom(string typeName, ICollection<string> roots)
        {
            return Walk(CSharpFile.SimpleTypeName(typeName ?? ""), roots, new HashSet<string>(StringComparer.Ordinal));
        }

        bool Walk(string name, ICollection<string> roots, HashSet<string> seen)
        {
            if (name.Length == 0 || !seen.Add(name)) return false;
            if (roots.Contains(name)) return true;
            List<CsType> decls;
            if (!byName.TryGetValue(name, out decls)) return false;
            foreach (var d in decls)
                foreach (var b in d.Bases)
                    if (Walk(b, roots, seen)) return true;
            return false;
        }

        /// <summary>MonoBehaviour-derived (components that live on GameObjects).</summary>
        public bool IsComponent(string typeName) { return DerivesFrom(typeName, ComponentRoots); }

        /// <summary>ScriptableObject-derived, excluding editor windows/inspectors.</summary>
        public bool IsScriptableObject(string typeName)
        {
            return DerivesFrom(typeName, AssetRoots) && !DerivesFrom(typeName, EditorBases);
        }

        /// <summary>Script types Unity binds by file name (MonoBehaviour / ScriptableObject, not editor UI classes).</summary>
        public bool IsSerializedScript(string typeName) { return IsComponent(typeName) || IsScriptableObject(typeName); }

        /// <summary>Any UnityEngine.Object (engine type or project type deriving from one).</summary>
        public bool IsUnityObject(string typeName)
        {
            string n = CSharpFile.SimpleTypeName(typeName ?? "");
            if (n.Length == 0) return false;
            bool r;
            if (unityCache.TryGetValue(n, out r)) return r;
            r = EngineObjectTypes.Contains(n) || DerivesFrom(n, EngineObjectTypes);
            unityCache[n] = r;
            return r;
        }

        /// <summary>Serialized fields of a type and its project base types (public instance or [SerializeField]).</summary>
        public HashSet<string> SerializedFields(string typeName)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            Collect(CSharpFile.SimpleTypeName(typeName ?? ""), result, new HashSet<string>(StringComparer.Ordinal));
            return result;
        }

        void Collect(string name, HashSet<string> result, HashSet<string> seen)
        {
            if (!seen.Add(name)) return;
            List<CsType> decls;
            if (!byName.TryGetValue(name, out decls)) return;
            foreach (var t in decls)
            {
                foreach (var f in t.Fields)
                {
                    if (f.Has("static") || f.Has("const") || f.Has("readonly") || f.IsEvent || f.HasAttribute("NonSerialized")) continue;
                    if (f.Has("public") || f.HasAttribute("SerializeField") || f.HasAttribute("SerializeReference")) result.Add(f.Name);
                }
                foreach (var b in t.Bases) Collect(b, result, seen);
            }
        }
    }
}
