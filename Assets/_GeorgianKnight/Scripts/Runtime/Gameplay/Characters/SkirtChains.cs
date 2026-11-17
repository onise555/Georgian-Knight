using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Coat-skirt panels as bone chains (CHARACTERS.md §3: 6 panels x 4 bones; the mail hem and the arming-coat skirt are
    /// weighted to the same chains). Each chain is a Verlet rope hanging from the hips: it springs back to the pose the
    /// skeleton gives it, swings with the body's motion, falls with gravity, keeps every joint outside the leg capsules
    /// (plus the cloth's clearance) and never bends further than maxBend from its rest direction. The bones are then
    /// aimed along the simulated rope. Runs after animation, before the scabbard (which hangs outside the skirt).
    /// </summary>
    [DefaultExecutionOrder(190)]
    public sealed class SkirtChains : MonoBehaviour
    {
        [System.Serializable]
        public sealed class Chain
        {
            public string name;
            public Transform[] bones;      // top to bottom; the last entry is the non-deforming tip
        }

        public Chain[] chains;
        public CapsuleCollider[] colliders;
        [Tooltip("Distance kept between a joint and a leg capsule surface (m): cloth thickness plus the layers beneath.")]
        public float clearance = 0.035f;
        [Range(0f, 1f)] public float stiffness = 0.18f;
        [Range(0f, 1f)] public float damping = 0.12f;
        public float gravity = 9.81f;
        [Tooltip("Largest bend of a segment away from its animated direction (deg).")]
        public float maxBend = 70f;
        [Tooltip("The first segment (waist to seat) is sewn to the coat: it follows the pose this much (0..1).")]
        [Range(0f, 1f)] public float topRigidity = 0.85f;

        Quaternion[][] restLocal;
        Vector3[][] pos, prev;
        float[][] len;
        bool init;

        void Start() { Capture(); }

        /// <summary>Stores each bone's local rotation at the bind pose (the animator never writes these bones).</summary>
        public void Capture()
        {
            if (chains == null) { enabled = false; return; }
            restLocal = new Quaternion[chains.Length][];
            pos = new Vector3[chains.Length][]; prev = new Vector3[chains.Length][]; len = new float[chains.Length][];
            for (int c = 0; c < chains.Length; c++)
            {
                var b = chains[c].bones;
                restLocal[c] = new Quaternion[b.Length];
                pos[c] = new Vector3[b.Length]; prev[c] = new Vector3[b.Length]; len[c] = new float[b.Length];
                for (int i = 0; i < b.Length; i++)
                {
                    restLocal[c][i] = b[i].localRotation;
                    if (i > 0) len[c][i] = Vector3.Distance(b[i - 1].position, b[i].position);
                }
            }
            init = false;
        }

        public void ResetDynamics() { init = false; }

        void LateUpdate() { Solve(Mathf.Min(Time.deltaTime, 1f / 30f)); }

        public void Solve(float dt)
        {
            if (restLocal == null || dt <= 0f) return;
            for (int c = 0; c < chains.Length; c++)
            {
                var b = chains[c].bones;
                int n = b.Length;
                // the pose the skeleton gives: rest local rotations under the animated hips
                for (int i = 0; i < n; i++) b[i].localRotation = restLocal[c][i];
                var target = new Vector3[n];
                for (int i = 0; i < n; i++) target[i] = b[i].position;
                if (!init)
                {
                    for (int i = 0; i < n; i++) { pos[c][i] = target[i]; prev[c][i] = target[i]; }
                    continue;
                }
                pos[c][0] = target[0]; prev[c][0] = target[0];
                Vector3 g = Vector3.down * gravity * dt * dt;
                for (int i = 1; i < n; i++)
                {
                    Vector3 v = (pos[c][i] - prev[c][i]) * (1f - damping);
                    prev[c][i] = pos[c][i];
                    float k = i == 1 ? Mathf.Lerp(stiffness, 1f, topRigidity) : stiffness;
                    pos[c][i] += v + g + (target[i] - pos[c][i]) * k;
                }
                // constraints: segment length, bend limit, leg capsules; twice for stability
                for (int it = 0; it < 2; it++)
                {
                    for (int i = 1; i < n; i++)
                    {
                        Vector3 a = pos[c][i - 1];
                        Vector3 restDir = (target[i] - target[i - 1]).normalized;
                        Vector3 d = pos[c][i] - a;
                        if (d.sqrMagnitude < 1e-10f) d = restDir;
                        float ang = Vector3.Angle(restDir, d);
                        if (ang > maxBend) d = Vector3.Slerp(restDir, d.normalized, maxBend / ang);
                        Vector3 p = a + d.normalized * len[c][i];
                        if (colliders != null)
                            for (int k2 = 0; k2 < colliders.Length; k2++)
                                if (colliders[k2] != null) p = PushOut(p, colliders[k2], clearance);
                        // re-impose the length after the push (the joint slides round the capsule)
                        pos[c][i] = a + (p - a).normalized * len[c][i];
                    }
                }
                // aim the bones along the rope
                for (int i = 0; i < n - 1; i++)
                {
                    Vector3 cur = b[i + 1].position - b[i].position;
                    Vector3 want = pos[c][i + 1] - pos[c][i];
                    if (cur.sqrMagnitude < 1e-10f || want.sqrMagnitude < 1e-10f) continue;
                    b[i].rotation = Quaternion.FromToRotation(cur, want) * b[i].rotation;
                }
            }
            init = true;
        }

        static Vector3 PushOut(Vector3 p, CapsuleCollider cap, float extra)
        {
            Transform t = cap.transform;
            Vector3 axis = cap.direction == 0 ? Vector3.right : (cap.direction == 1 ? Vector3.up : Vector3.forward);
            Vector3 scale = t.lossyScale;
            float rScale = cap.direction == 0 ? Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z))
                         : cap.direction == 1 ? Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z))
                         : Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y));
            float r = cap.radius * rScale + extra;
            float half = Mathf.Max(0f, cap.height * 0.5f - cap.radius) * Mathf.Abs(Vector3.Scale(axis, scale).magnitude);
            Vector3 c = t.TransformPoint(cap.center);
            Vector3 ax = t.TransformDirection(axis).normalized;
            float s = Mathf.Clamp(Vector3.Dot(p - c, ax), -half, half);
            Vector3 q = c + ax * s;
            Vector3 d = p - q;
            float dist = d.magnitude;
            if (dist >= r) return p;
            if (dist < 1e-6f) d = Vector3.Cross(ax, Vector3.up).normalized; else d /= dist;
            return q + d * r;
        }
    }
}
