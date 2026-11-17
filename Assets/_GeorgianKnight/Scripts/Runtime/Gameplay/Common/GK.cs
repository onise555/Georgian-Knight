using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>Project-wide constants: layers, animator parameters, tuning shared by many systems.</summary>
    public static class GK
    {
        // Physics layers (configured by the editor builder: Georgian Knight > Build Everything)
        public const int LayerCharacter = 8;   // CharacterController capsules
        public const int LayerHurtbox = 9;     // bone colliders (hit detection + ragdoll)
        public const int LayerEnvironment = 0; // Default
        public const int LayerTerrain = 10;    // terrain + walkable ground meshes
        /// <summary>Query-only trigger volumes of interactables (Interactor overlap); collides with nothing physically.
        /// Solid props keep their blocking collider on Default and add a trigger child on this layer.</summary>
        public const int LayerInteractable = 11;
        public static int InteractableMask { get { return 1 << LayerInteractable; } }
        /// <summary>What blocks line of sight to an interactable (world geometry, not characters or hurtboxes).</summary>
        public static int SightBlockMask { get { return (1 << LayerEnvironment) | (1 << LayerTerrain); } }

        public static int HurtboxMask { get { return 1 << LayerHurtbox; } }
        public static int GroundMask { get { return (1 << 0) | (1 << 4) | (1 << 10); } }

        // Animator parameters
        public static readonly int P_Speed = Animator.StringToHash("Speed");
        public static readonly int P_MoveX = Animator.StringToHash("MoveX");
        public static readonly int P_MoveY = Animator.StringToHash("MoveY");
        public static readonly int P_Strafe = Animator.StringToHash("Strafe");
        public static readonly int P_Armed = Animator.StringToHash("Armed");

        // Animator layer names
        public const string LayerBase = "Base Layer";
        public const string LayerBlock = "Block";
        public const string LayerOffHand = "OffHand";
        public const string LayerUpperBody = "UpperBody";
        public const string S_UBDraw = "UB_Draw", S_UBSheathe = "UB_Sheathe", S_UBEmpty = "UB_Empty";
        public const string LayerFullBodyAction = "FullBodyAction";
        public const string S_FBDraw = "FB_Draw", S_FBSheathe = "FB_Sheathe", S_FBEmpty = "FB_Empty";

        // Base-layer locomotion states
        public static readonly int S_Locomotion = Animator.StringToHash("Locomotion");
        public static readonly int S_Strafe = Animator.StringToHash("Strafe");

        public static float Remap(float v, float a, float b, float c, float d)
        {
            if (Mathf.Approximately(a, b)) return c;
            return Mathf.Lerp(c, d, Mathf.InverseLerp(a, b, v));
        }

        public static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        public static float FlatAngle(Vector3 from, Vector3 to)
        {
            from.y = 0f; to.y = 0f;
            if (from.sqrMagnitude < 1e-6f || to.sqrMagnitude < 1e-6f) return 0f;
            return Vector3.SignedAngle(from, to, Vector3.up);
        }

        /// <summary>Exponential smoothing that is frame-rate independent.</summary>
        public static float Damp(float current, float target, float sharpness, float dt)
        {
            return Mathf.Lerp(current, target, 1f - Mathf.Exp(-sharpness * dt));
        }

        public static Vector3 Damp(Vector3 current, Vector3 target, float sharpness, float dt)
        {
            return Vector3.Lerp(current, target, 1f - Mathf.Exp(-sharpness * dt));
        }

        public static Quaternion Damp(Quaternion current, Quaternion target, float sharpness, float dt)
        {
            return Quaternion.Slerp(current, target, 1f - Mathf.Exp(-sharpness * dt));
        }
    }

    public enum Team { Player, Enemy }
}
