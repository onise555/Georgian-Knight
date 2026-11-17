using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// The shield arm makes way for its own blade. The sword-and-shield captures were taken with a small round shield;
    /// the knight's kite shield reaches the knee, so a cut sweeping across the body passed through its lower half
    /// (up to 49 cm, measured). When the blade is in or about to enter the plate during an attack, the whole arm swings
    /// from the shoulder to carry the shield clear (at most <see cref="maxAngle"/>), as a swordsman draws his shield
    /// aside for his own cut, and settles back when the blade has passed. Only the upper arm turns: the forearm, wrist
    /// and grip keep the capture's shape. Runs before the CC twist bones (170) so the shoulder helpers follow it.
    /// </summary>
    [DefaultExecutionOrder(162)]
    public class ShieldYield : MonoBehaviour
    {
        public Transform upperArm;
        public Transform bladeBase, bladeTip;
        public Collider plate;
        [Range(0f, 60f)] public float maxAngle = 45f;
        public float riseRate = 16f, settleRate = 3.5f;
        [Tooltip("how far ahead (s) the blade's motion is extrapolated to see a crossing coming")]
        public float lookAhead = 0.18f;

        public float Weight { get; private set; }
        float weightVel, holdUntil;
        Vector3 awayLocal;
        Combatant self;
        FightingStyles styles;
        WeaponHolster holster;
        Vector3 lastBase, lastTip, away;
        bool haveLast;

        public void Init(Transform arm, Transform bBase, Transform bTip, Collider shieldPlate)
        {
            upperArm = arm; bladeBase = bBase; bladeTip = bTip; plate = shieldPlate;
        }

        void Awake()
        {
            self = GetComponent<Combatant>();
            styles = GetComponent<FightingStyles>();
            holster = GetComponent<WeaponHolster>();
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (upperArm == null || bladeBase == null || bladeTip == null || plate == null || dt <= 0f) return;
            // in a swing, and while the sword is drawn or sheathed past the shield (the draw swept 70 cm through it)
            bool holstering = holster != null && (holster.State == WeaponHolster.HolsterState.Drawing || holster.State == WeaponHolster.HolsterState.Sheathing);
            bool active = plate.enabled && plate.gameObject.activeInHierarchy && (styles == null || styles.ShieldOnArm) &&
                          self != null && (self.CurrentState == Combatant.State.Attacking || self.CurrentState == Combatant.State.Executing || holstering);
            Vector3 b0 = bladeBase.position, b1 = bladeTip.position;
            Vector3 hitPoint;
            bool crossing = false;
            if (active)
            {
                crossing = Inside(b0, b1, out hitPoint);
                if (!crossing && haveLast)
                {
                    // where the blade will be a moment from now at its present speed
                    float k = lookAhead / dt;
                    crossing = Inside(b0 + (b0 - lastBase) * k, b1 + (b1 - lastTip) * k, out hitPoint);
                }
                if (crossing)
                {
                    // carry the plate away from the blade and up over it. The direction is chosen once, when the
                    // shield starts to give way, and kept in the knight's frame: recomputed every frame from a contact
                    // point that jumps along the blade it flipped about and shook the arm (measured: 110 zigzag frames)
                    if (Weight < 0.05f || awayLocal == Vector3.zero)
                    {
                        Vector3 c = PlateCentre();
                        Vector3 d = (c - hitPoint);
                        if (d.sqrMagnitude < 1e-4f) d = -transform.right;
                        d = d.normalized + Vector3.up * 0.35f - transform.right * 0.25f;
                        awayLocal = transform.InverseTransformDirection(d.normalized);
                    }
                    holdUntil = Time.time + 0.12f;      // a crossing seen this frame keeps the arm out a moment
                }
            }
            lastBase = b0; lastTip = b1; haveLast = true;
            bool want = crossing || Time.time < holdUntil;
            // a critically damped spring toward 0 or 1: no step in speed, so no jolt and no back-and-forth
            float target = want ? 1f : 0f, omega = want ? riseRate * 0.6f : settleRate * 1.2f;
            float x = Weight - target;
            float tmp = (weightVel + omega * x) * dt;
            weightVel = (weightVel - omega * tmp) * Mathf.Exp(-omega * dt);
            Weight = Mathf.Clamp01(target + (x + tmp) * Mathf.Exp(-omega * dt));
            if (Weight <= 0.001f) { Weight = 0f; weightVel = 0f; awayLocal = Vector3.zero; return; }
            away = transform.TransformDirection(awayLocal);

            Vector3 shoulder = upperArm.position;
            Vector3 centre = PlateCentre();
            Vector3 from = centre - shoulder;
            Vector3 to = (centre + away * 0.6f) - shoulder;
            Quaternion full = Quaternion.FromToRotation(from, to);
            float ang = Quaternion.Angle(Quaternion.identity, full);
            if (ang > maxAngle) full = Quaternion.Slerp(Quaternion.identity, full, maxAngle / ang);
            // ease in and out: the arm accelerates and slows like a limb, not like a hinge
            float w = Weight;
            upperArm.rotation = Quaternion.Slerp(Quaternion.identity, full, w) * upperArm.rotation;
        }

        // the plate from its transform THIS frame: the collider's physics bounds and nearest-point query sync a frame
        // late, so a correction read back its own previous turn and the arm shook back and forth
        Vector3 PlateCentre()
        {
            var box = plate as BoxCollider;
            return box != null ? box.transform.TransformPoint(box.center) : plate.transform.position;
        }

        bool Inside(Vector3 a, Vector3 b, out Vector3 point)
        {
            point = a;
            var box = plate as BoxCollider;
            if (box == null) return false;
            var t = box.transform;
            Vector3 half = Vector3.Scale(box.size * 0.5f, t.lossyScale) + Vector3.one * 0.02f;   // within 2 cm of the plate
            const int N = 10;
            for (int i = 0; i <= N; i++)
            {
                Vector3 p = Vector3.Lerp(a, b, i / (float)N);
                Vector3 l = Vector3.Scale(t.InverseTransformPoint(p) - box.center, t.lossyScale);
                if (Mathf.Abs(l.x) <= half.x && Mathf.Abs(l.y) <= half.y && Mathf.Abs(l.z) <= half.z) { point = p; return true; }
            }
            return false;
        }
    }
}
