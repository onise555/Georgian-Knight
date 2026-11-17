using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Single owner of CharacterController.Move. Gameplay code feeds it a locomotion velocity (Update),
    /// root-motion deltas (OnAnimatorMove) and impulses; everything is applied once in LateUpdate.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [DefaultExecutionOrder(-200)]
    public class CharacterMotor : MonoBehaviour
    {
        public float gravity = -22f;
        public float groundStick = -2f;
        public float pushDecay = 9f;
        [Tooltip("Characters push each other apart so they never interpenetrate.")]
        public float separationRadius = 0.55f;

        public CharacterController Controller { get; private set; }
        public bool IsGrounded { get; private set; }
        public Vector3 Velocity { get; private set; }

        Vector3 locomotionVelocity;
        Vector3 rootDelta;
        float rootDeltaY;
        bool verticalRoot;
        Vector3 push;
        float verticalSpeed;
        bool frozen;

        static readonly Collider[] overlap = new Collider[16];

        void Awake()
        {
            Controller = GetComponent<CharacterController>();
        }

        public void SetLocomotionVelocity(Vector3 v) { locomotionVelocity = GK.Flat(v); }
        public void AddRootMotion(Vector3 delta) { rootDelta += GK.Flat(delta); }
        /// <summary>Vertical root motion for this frame (jumps: the clip's feet-based root carries the body up and down
        /// and the capsule follows it, so it clears obstacles and lands on what is under it). Replaces gravity for the
        /// frame; when it stops, gravity continues from the speed it left.</summary>
        public void AddVerticalRoot(float dy) { rootDeltaY += dy; verticalRoot = true; }
        /// <summary>Leaves the ground with this upward speed (m/s); gravity brings the body back down.</summary>
        public void Launch(float upSpeed) { verticalSpeed = upSpeed; IsGrounded = false; launched = true; }
        bool launched;
        /// <summary>Seconds since the capsule last touched ground (0 while grounded).</summary>
        public float AirTime { get; private set; }
        public void AddPush(Vector3 velocity) { push += GK.Flat(velocity); }
        public void SetFrozen(bool value) { frozen = value; if (value) { locomotionVelocity = Vector3.zero; push = Vector3.zero; } }

        public void Teleport(Vector3 position, Quaternion rotation)
        {
            bool was = Controller.enabled;
            Controller.enabled = false;
            transform.SetPositionAndRotation(position, rotation);
            Controller.enabled = was;
        }

        public void FaceDirection(Vector3 dir, float sharpness, float dt)
        {
            dir = GK.Flat(dir);
            if (dir.sqrMagnitude < 1e-4f) return;
            Quaternion target = Quaternion.LookRotation(dir.normalized, Vector3.up);
            transform.rotation = sharpness <= 0f ? target : GK.Damp(transform.rotation, target, sharpness, dt);
        }

        void LateUpdate()
        {
            if (!Controller.enabled) { rootDelta = Vector3.zero; return; }
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            Vector3 motion = frozen ? Vector3.zero : locomotionVelocity * dt + rootDelta + push * dt;
            motion += Separation() * dt;

            if (verticalRoot && !frozen)
            {
                motion.y = rootDeltaY;
                verticalSpeed = rootDeltaY / dt;
            }
            else
            {
                if (launched) { launched = false; verticalSpeed += gravity * dt; }
                else if (IsGrounded && verticalSpeed < 0f) verticalSpeed = groundStick;
                else verticalSpeed += gravity * dt;
                motion.y = verticalSpeed * dt;
            }
            rootDeltaY = 0f; verticalRoot = false;

            Vector3 before = transform.position;
            CollisionFlags flags = Controller.Move(motion);
            IsGrounded = (flags & CollisionFlags.Below) != 0 || Controller.isGrounded;
            AirTime = IsGrounded ? 0f : AirTime + dt;
            Velocity = (transform.position - before) / dt;

            rootDelta = Vector3.zero;
            push = GK.Damp(push, Vector3.zero, pushDecay, dt);
        }

        Vector3 Separation()
        {
            Vector3 p = transform.position + Vector3.up * 0.9f;
            int n = Physics.OverlapSphereNonAlloc(p, separationRadius, overlap, 1 << GK.LayerCharacter, QueryTriggerInteraction.Ignore);
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                if (overlap[i].transform == transform) continue;
                Vector3 d = GK.Flat(transform.position - overlap[i].transform.position);
                float dist = d.magnitude;
                float overlapAmount = separationRadius * 2f - dist;
                if (overlapAmount <= 0f) continue;
                if (dist < 1e-3f) d = transform.right; else d /= dist;
                sum += d * overlapAmount * 6f;
            }
            return sum;
        }
    }
}
