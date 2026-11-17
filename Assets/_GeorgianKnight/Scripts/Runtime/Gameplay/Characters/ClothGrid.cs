using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// The coat skirt as cloth (CHARACTERS.md §3, PERFORMANCE §9.3). Every panel is a row of hanging columns, each a
    /// chain of joints from the waist to the hem; the joints are particles of a position-based cloth:
    /// - columns keep their length (wool does not stretch), neighbours inside a panel may bunch to half their spacing
    ///   but never pull apart, diagonals resist shear and every second joint resists folding back on itself;
    /// - neighbouring panels are sewn together above the slits and part freely below them, the panel on top of each
    ///   overlap staying outside the one beneath;
    /// - the skirt keeps a light memory of its cut (stronger at the fitted seat, weak at the hem), so it swings, flares
    ///   on turns and settles back instead of hanging like a sack;
    /// - it stays outside the legs (each joint keeps the clearance it has at the bind pose: the layers beneath), above
    ///   the ground under the feet, and under the scabbard, which presses the cloth in against the thigh.
    /// The bones are then turned to follow the particles (down the column and across the panel), so the skinned skirt,
    /// its lining, its gold border and the mail and arming-coat skirts beneath all move as one cloth.
    /// Runs after animation and before the scabbard.
    /// </summary>
    [DefaultExecutionOrder(190)]
    public sealed class ClothGrid : MonoBehaviour
    {
        [System.Serializable]
        public sealed class Column
        {
            public string panel;
            public Transform[] bones;      // waist to hem; bone 0 is fixed to the hips
        }

        public Transform hips;
        public Column[] columns;           // in order around the body
        public int rows = 6;
        [Tooltip("Rows sewn to the neighbouring panels (above the slits).")]
        public int joinedRows = 2;
        [Tooltip("Within-panel neighbours, -1 at a panel edge.")]
        public int[] left, right;
        [Tooltip("Next column around the body when it belongs to another panel (a seam above, a slit below), else -1.")]
        public int[] boundaryNext;
        [Tooltip("True when this column lies over boundaryNext at the slit overlap.")]
        public bool[] overNext;

        [Header("Bind pose (baked by the editor)")]
        public Vector3[] restPos;          // particle positions in hips space [col * rows + row]
        public Quaternion[] restRot;       // bone rotations in hips space
        public float[] clearance;          // distance kept from the leg capsules

        [Header("Collision")]
        [Tooltip("Tapered capsules fitted to the leg skin (bone space): thighs and shins.")]
        public Transform[] coneBones;
        public Vector3[] coneA, coneB;
        public float[] coneRA, coneRB;
        [Tooltip("Fallback when no cones are baked (older characters).")]
        public CapsuleCollider[] colliders;
        public ScabbardMotion scabbard;
        [Tooltip("Coat thickness under the scabbard (m).")]
        public float scabbardGap = 0.008f;
        [Tooltip("Height of the hem above the feet it may not go below (m).")]
        public float floorOffset = 0.03f;

        [Header("Cloth")]
        public float gravity = 9.81f;
        [Range(0f, 0.5f)] public float damping = 0.05f;
        [Tooltip("Memory of the cut per 1/60 s at the seat (row 1) and at the hem.")]
        public float shapeSeat = 0.35f, shapeHem = 0.03f;
        public float stretchLimit = 1.03f, bunchLimit = 0.5f, shearLimit = 1.06f, foldLimit = 0.72f;
        public float overlapGap = 0.006f;
        [Tooltip("Furthest a joint may stray from its animated place, at the seat and at the hem (m) (Chaos/APEX max distance).")]
        public float maxDistanceTop = 0.03f, maxDistanceHem = 0.30f;
        [Tooltip("Share of the body's own motion the cloth feels as inertia (PhysX inertia scale): 1 = all of it.")]
        [Range(0f, 1f)] public float inertiaScale = 0.5f;
        [Tooltip("Solver rate (Hz): many small steps with few iterations beat few steps with many (Macklin et al. 2019).")]
        public float solverHz = 240f;
        [Range(1, 6)] public int iterations = 2;

        Vector3[] p, prev, target;
        float[] lenV, lenH, lenS, lenB, lenSeam, tether;
        Vector3 lastHips;
        Matrix4x4 lastHipsM;
        bool init;
        int n;

        void Start() { Prepare(); }

        /// <summary>Allocates the particles from the baked rest data (Start; tools call it directly).</summary>
        public void Prepare()
        {
            if (hips == null || columns == null || restPos == null || restPos.Length != columns.Length * rows) { enabled = false; return; }
            n = columns.Length * rows;
            p = new Vector3[n]; prev = new Vector3[n]; target = new Vector3[n];
            lenV = new float[n]; lenH = new float[n]; lenS = new float[n]; lenB = new float[n]; lenSeam = new float[n]; tether = new float[n];
            for (int c = 0; c < columns.Length; c++)
                for (int r = 0; r < rows; r++)
                {
                    int i = c * rows + r;
                    if (r > 0) lenV[i] = Vector3.Distance(restPos[i], restPos[i - 1]);
                    tether[i] = Vector3.Distance(restPos[i], restPos[c * rows]) * 1.02f;      // long-range attachment
                    if (r > 1) lenB[i] = Vector3.Distance(restPos[i], restPos[i - 2]);
                    if (right[c] >= 0)
                    {
                        lenH[i] = Vector3.Distance(restPos[i], restPos[right[c] * rows + r]);
                        if (r > 0) lenS[i] = Vector3.Distance(restPos[i], restPos[right[c] * rows + r - 1]);
                    }
                    if (boundaryNext[c] >= 0) lenSeam[i] = Vector3.Distance(restPos[i], restPos[boundaryNext[c] * rows + r]);
                }
            init = false;
        }

        public void ResetDynamics() { init = false; }

        void LateUpdate() { Simulate(Mathf.Min(Time.deltaTime, 1f / 30f)); }

        /// <summary>Advances the cloth by dt (fixed sub-steps keep it the same at any frame rate) and poses the bones.</summary>
        public void Simulate(float dt)
        {
            if (dt <= 0f || p == null) return;
            int steps = Mathf.Clamp(Mathf.CeilToInt(dt * solverHz), 1, 8);
            CarryWithBody();
            for (int s = 0; s < steps; s++) Step(dt / steps);
            WriteBones();
        }

        /// <summary>Largest stretch of a column or a panel row against its rest length after the last step (1 = none).</summary>
        public float MaxStrain()
        {
            if (p == null) return 1f;
            float m = 1f;
            for (int c = 0; c < columns.Length; c++)
                for (int r = 1; r < rows; r++)
                {
                    int i = c * rows + r;
                    if (lenV[i] > 1e-5f) m = Mathf.Max(m, Vector3.Distance(p[i], p[i - 1]) / lenV[i]);
                    if (right[c] >= 0 && lenH[i] > 1e-5f) m = Mathf.Max(m, Vector3.Distance(p[i], p[right[c] * rows + r]) / lenH[i]);
                }
            return m;
        }

        /// <summary>Where the largest strain is (tools): "column/row kind ratio".</summary>
        public string StrainWhere()
        {
            if (p == null) return "-";
            float m = 1f; string w = "-";
            for (int c = 0; c < columns.Length; c++)
                for (int r = 1; r < rows; r++)
                {
                    int i = c * rows + r;
                    float v = lenV[i] > 1e-5f ? Vector3.Distance(p[i], p[i - 1]) / lenV[i] : 1f;
                    if (v > m) { m = v; w = columns[c].panel + c + "/" + r + " col " + v.ToString("F2"); }
                    if (right[c] >= 0 && lenH[i] > 1e-5f)
                    {
                        float h = Vector3.Distance(p[i], p[right[c] * rows + r]) / lenH[i];
                        if (h > m) { m = h; w = columns[c].panel + c + "/" + r + " row " + h.ToString("F2") + " rest " + lenH[i].ToString("F3"); }
                    }
                }
            return w;
        }

        /// <summary>Particle position (tools).</summary>
        public Vector3 Particle(int column, int row) { return p[column * rows + row]; }

        /// <summary>The cloth rides along with part of the body's motion since the last frame (1 - inertia scale), so
        /// a sprint or a spin swings it as a real coat would instead of leaving it behind in world space.</summary>
        void CarryWithBody()
        {
            Matrix4x4 now = Matrix4x4.TRS(hips.position, hips.rotation, Vector3.one);
            if (init && inertiaScale < 1f)
            {
                Matrix4x4 delta = now * lastHipsM.inverse;
                float k = 1f - inertiaScale;
                for (int i = 0; i < n; i++)
                {
                    if (i % rows == 0) continue;
                    p[i] = Vector3.LerpUnclamped(p[i], delta.MultiplyPoint3x4(p[i]), k);
                    prev[i] = Vector3.LerpUnclamped(prev[i], delta.MultiplyPoint3x4(prev[i]), k);
                }
            }
            lastHipsM = now;
        }

        void Step(float dt)
        {
            Vector3 hp = hips.position;
            for (int i = 0; i < n; i++) target[i] = hips.TransformPoint(restPos[i]);
            if (!init || (hp - lastHips).sqrMagnitude > 1f)
            {
                for (int i = 0; i < n; i++) { p[i] = target[i]; prev[i] = target[i]; }
                init = true; lastHips = hp; lastHipsM = Matrix4x4.TRS(hp, hips.rotation, Vector3.one);
                return;
            }
            lastHips = hp;
            Vector3 g = Vector3.down * gravity * dt * dt;
            float keep = 1f - damping;
            float f60 = dt * 60f;
            for (int c = 0; c < columns.Length; c++)
                for (int r = 0; r < rows; r++)
                {
                    int i = c * rows + r;
                    if (r == 0) { prev[i] = p[i]; p[i] = target[i]; continue; }
                    Vector3 v = (p[i] - prev[i]) * keep;
                    prev[i] = p[i];
                    p[i] += v + g;
                    float k60 = Mathf.Lerp(shapeSeat, shapeHem, (r - 1f) / Mathf.Max(1, rows - 2));
                    float k = 1f - Mathf.Pow(1f - k60, f60);
                    p[i] += (target[i] - p[i]) * k;
                }

            Vector3 up = Vector3.up;        // overlaps are ordered radially about the vertical through the hips
            float floor = transform.position.y + floorOffset;

            for (int it = 0; it < iterations; it++)
            {
                for (int c = 0; c < columns.Length; c++)
                {
                    int rc = right[c];
                    int bn = boundaryNext[c];
                    for (int r = 1; r < rows; r++)
                    {
                        int i = c * rows + r;
                        // the column: fixed length (the joint above moves too unless it is the pinned waist)
                        Distance(i - 1, i, lenV[i], lenV[i], r == 1 ? 0f : 0.5f);
                        if (r > 1) MinDistance(i - 2, i, lenB[i] * foldLimit, r == 2 ? 0f : 0.5f);
                        if (rc >= 0)
                        {
                            int j = rc * rows + r;
                            Distance(i, j, lenH[i] * bunchLimit, lenH[i] * stretchLimit, 0.5f);
                            MaxDistance(i, j - 1, lenS[i] * shearLimit, r == 1 ? 1f : 0.5f);
                        }
                        if (bn >= 0)
                        {
                            int j = bn * rows + r;
                            if (r < joinedRows) Distance(i, j, lenSeam[i], lenSeam[i], 0.5f);
                            else Overlap(overNext[c] ? i : j, overNext[c] ? j : i, hp, up);
                        }
                    }
                }
                for (int i = 0; i < n; i++)
                {
                    int r = i % rows;
                    if (r == 0) continue;
                    // tether: never further from the sewn top of its column than at rest (no sag, no stretch)
                    Vector3 top = p[i - r], dt_ = p[i] - top;
                    float tl = dt_.magnitude;
                    if (tl > tether[i]) p[i] = top + dt_ * (tether[i] / tl);
                    // max distance from the animated place, growing from the seat to the hem
                    float maxD = Mathf.Lerp(maxDistanceTop, maxDistanceHem, (r - 1f) / Mathf.Max(1, rows - 2));
                    Vector3 d = p[i] - target[i];
                    float m = d.magnitude;
                    if (m > maxD) p[i] = target[i] + d * (maxD / m);
                    Collide(i);
                    if (p[i].y < floor) p[i].y = floor;
                }
            }
        }

        // ------------------------------------------------------------------ constraints

        /// <summary>Keeps |pi - pj| within [min, max]; w = share of the correction taken by pi (0 = pi is fixed).</summary>
        void Distance(int i, int j, float min, float max, float w)
        {
            Vector3 d = p[j] - p[i];
            float m = d.magnitude;
            if (m < 1e-7f) return;
            float goal = m < min ? min : (m > max ? max : m);
            if (goal == m) return;
            Vector3 corr = d * ((m - goal) / m);
            if (i % rows == 0) w = 0f;
            p[i] += corr * w;
            p[j] -= corr * (1f - w);
        }

        void MinDistance(int i, int j, float min, float w) { Distance(i, j, min, float.MaxValue, w); }

        void MaxDistance(int i, int j, float max, float w) { Distance(i, j, 0f, max, w); }

        /// <summary>The panel on top stays outside the one beneath (radially from the body axis), by the overlap gap.</summary>
        void Overlap(int over, int under, Vector3 hp, Vector3 up)
        {
            Vector3 ro = Vector3.ProjectOnPlane(p[over] - hp, up), ru = Vector3.ProjectOnPlane(p[under] - hp, up);
            float mo = ro.magnitude, mu = ru.magnitude;
            if (mo < 1e-4f || mu < 1e-4f) return;
            float need = mu + overlapGap - mo;
            if (need <= 0f) return;
            p[over] += ro / mo * (need * 0.5f);
            p[under] -= ru / mu * (need * 0.5f);
        }

        /// <summary>Deepest a world point lies inside the leg cones (m, 0 = outside), and which cone (tools).</summary>
        public float Inside(Vector3 w, out int cone)
        {
            float worst = 0f; cone = -1;
            if (coneBones == null) return 0f;
            for (int k = 0; k < coneBones.Length; k++)
            {
                Vector3 q; float r;
                ConeClosest(k, w, out q, out r);
                float d = r - (w - q).magnitude;
                if (d > worst) { worst = d; cone = k; }
            }
            return worst;
        }

        /// <summary>Closest point on cone k's axis and the radius there. Above the top of a thigh (inside the hips,
        /// which carry the sewn top of the skirt) the cone is open: r = 0, nothing is pushed.</summary>
        void ConeClosest(int k, Vector3 w, out Vector3 q, out float r)
        {
            var t = coneBones[k];
            Vector3 a = t.TransformPoint(coneA[k]), b = t.TransformPoint(coneB[k]);
            Vector3 ab = b - a;
            float u = Vector3.Dot(w - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude);
            if (u < 0f) { q = a; r = 0f; return; }
            u = Mathf.Min(u, 1f);
            q = a + ab * u;
            r = Mathf.Lerp(coneRA[k], coneRB[k], u);
        }

        void Collide(int i)
        {
            // the scabbard lies on the coat: cloth under it is pressed in toward the body, never pushed out over it.
            // Its upper third hangs at the belt beside the hip, where the coat is sewn close and cannot give way.
            if (scabbard != null && scabbard.scabbard != null)
            {
                Vector3 a = scabbard.scabbard.position + scabbard.scabbard.forward * (scabbard.length * 0.3f);
                Vector3 b = scabbard.scabbard.position + scabbard.scabbard.forward * scabbard.length;
                Vector3 ab = b - a;
                float t = Mathf.Clamp01(Vector3.Dot(p[i] - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude));
                Vector3 q = a + ab * t;
                Vector3 outward = Vector3.ProjectOnPlane(q - hips.position, Vector3.up);
                if (outward.sqrMagnitude > 1e-6f)
                {
                    outward.Normalize();
                    Vector3 d = p[i] - q;
                    float s = Vector3.Dot(d, outward);
                    Vector3 lat = d - outward * s;
                    float R = scabbard.radius + scabbardGap;
                    float l2 = lat.sqrMagnitude;
                    if (l2 < R * R)
                    {
                        float allowed = -Mathf.Sqrt(R * R - l2);
                        if (s > allowed) p[i] -= outward * (s - allowed);
                    }
                }
            }
            // the legs win: the cloth never goes into a leg, even under the scabbard
            if (coneBones != null && coneBones.Length > 0)
                for (int k = 0; k < coneBones.Length; k++)
                {
                    if (coneBones[k] == null) continue;
                    Vector3 q; float r;
                    ConeClosest(k, p[i], out q, out r);
                    Vector3 d = p[i] - q;
                    if (r <= 0f) continue;
                    float m = d.magnitude, need = r + clearance[i];
                    if (m >= need) continue;
                    Vector3 dir = m > 1e-6f ? d / m : Vector3.ProjectOnPlane(p[i] - hips.position, Vector3.up).normalized;
                    p[i] = q + dir * need;
                }
            else if (colliders != null)
                for (int k = 0; k < colliders.Length; k++)
                {
                    var cap = colliders[k];
                    if (cap == null) continue;
                    Vector3 a, b; float r;
                    CapsuleWorld(cap, out a, out b, out r);
                    Vector3 ab = b - a;
                    float t = Mathf.Clamp01(Vector3.Dot(p[i] - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude));
                    Vector3 q = a + ab * t;
                    Vector3 d = p[i] - q;
                    float m = d.magnitude, need = r + clearance[i];
                    if (m >= need) continue;
                    Vector3 dir = m > 1e-6f ? d / m : Vector3.ProjectOnPlane(p[i] - hips.position, Vector3.up).normalized;
                    p[i] = q + dir * need;
                }
        }

        // ------------------------------------------------------------------ bones

        void WriteBones()
        {
            Quaternion hr = hips.rotation;
            for (int c = 0; c < columns.Length; c++)
            {
                var bones = columns[c].bones;
                for (int r = 0; r < rows && r < bones.Length; r++)
                {
                    int i = c * rows + r;
                    Vector3 down = r < rows - 1 ? p[i + 1] - p[i] : p[i] - p[i - 1];
                    Vector3 downR = r < rows - 1 ? restPos[i + 1] - restPos[i] : restPos[i] - restPos[i - 1];
                    int lc = left[c] >= 0 ? left[c] : c, rc = right[c] >= 0 ? right[c] : c;
                    if (lc == rc) continue;
                    Vector3 lat = p[rc * rows + r] - p[lc * rows + r];
                    Vector3 latR = restPos[rc * rows + r] - restPos[lc * rows + r];
                    Quaternion now = Frame(down, lat), rest = hr * Frame(downR, latR);
                    if (r > 0) bones[r].position = p[i];
                    bones[r].rotation = now * Quaternion.Inverse(rest) * hr * restRot[i];
                }
            }
        }

        static Quaternion Frame(Vector3 down, Vector3 lat)
        {
            if (down.sqrMagnitude < 1e-10f) down = Vector3.down;
            Vector3 f = down.normalized;
            Vector3 u = lat - f * Vector3.Dot(lat, f);
            if (u.sqrMagnitude < 1e-10f) u = Vector3.Cross(f, Vector3.right);
            return Quaternion.LookRotation(f, u.normalized);
        }

        static void CapsuleWorld(CapsuleCollider c, out Vector3 a, out Vector3 b, out float r)
        {
            var t = c.transform;
            Vector3 axis = c.direction == 0 ? Vector3.right : c.direction == 2 ? Vector3.forward : Vector3.up;
            float s = t.lossyScale.x;
            r = c.radius * s;
            float half = Mathf.Max(0f, c.height * 0.5f * s - r);
            Vector3 center = t.TransformPoint(c.center);
            Vector3 w = t.TransformDirection(axis);
            a = center - w * half;
            b = center + w * half;
        }
    }
}
