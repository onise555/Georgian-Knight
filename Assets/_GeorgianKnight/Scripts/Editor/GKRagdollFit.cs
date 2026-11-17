using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Builds the hurtbox / ragdoll bodies fitted to the actual skinned mesh:
    ///  * every vertex is assigned to the ragdoll body owning its dominant bone,
    ///  * capsule radii / centre offsets, box extents and the head sphere come from robust percentiles
    ///    of those vertices (so flaring cloth does not inflate them),
    ///  * joint limits are anatomical; elbow / knee hinge axes are read from the avatar's own muscle
    ///    space (HumanPoseHandler), and the CharacterJoint twist sign is calibrated with a physics test.
    /// </summary>
    public static class GKRagdollFit
    {
        public class Report { public List<string> lines = new List<string>(); }

        static int twistSign;   // +1: CharacterJoint twist follows Quaternion.AngleAxis, -1: opposite

        // ------------------------------------------------------------------ vertex ownership

        public static Dictionary<Transform, List<Vector3>> OwnedVertices(GameObject root, ICollection<Transform> bodies)
        {
            var result = new Dictionary<Transform, List<Vector3>>();
            foreach (var b in bodies) result[b] = new List<Vector3>();
            var owner = new Dictionary<Transform, Transform>();
            System.Func<Transform, Transform> own = t =>
            {
                Transform o;
                if (owner.TryGetValue(t, out o)) return o;
                var p = t;
                while (p != null && !result.ContainsKey(p)) p = p.parent;
                owner[t] = p;
                return p;
            };
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr.sharedMesh == null) continue;
                var baked = new Mesh();
                smr.BakeMesh(baked, true);
                var vs = baked.vertices;
                var bw = smr.sharedMesh.boneWeights;
                var bones = smr.bones;
                Vector3 o = smr.transform.position; Quaternion q = smr.transform.rotation;
                for (int i = 0; i < vs.Length && i < bw.Length; i++)
                {
                    var w = bw[i];
                    int bi = w.boneIndex0; float best = w.weight0;
                    if (w.weight1 > best) { best = w.weight1; bi = w.boneIndex1; }
                    if (w.weight2 > best) { best = w.weight2; bi = w.boneIndex2; }
                    if (w.weight3 > best) { best = w.weight3; bi = w.boneIndex3; }
                    if (bi < 0 || bi >= bones.Length || bones[bi] == null) continue;
                    var ob = own(bones[bi]);
                    if (ob == null) continue;
                    result[ob].Add(o + q * vs[i]);
                }
                Object.DestroyImmediate(baked);
            }
            return result;
        }

        /// <summary>All skinned vertices in world space (current pose).</summary>
        public static List<Vector3> BakedVertices(GameObject root)
        {
            var list = new List<Vector3>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr.sharedMesh == null) continue;
                var baked = new Mesh();
                smr.BakeMesh(baked, true);
                Vector3 o = smr.transform.position; Quaternion q = smr.transform.rotation;
                foreach (var v in baked.vertices) list.Add(o + q * v);
                Object.DestroyImmediate(baked);
            }
            return list;
        }

        /// <summary>
        /// Outer radius of the clothing around the body's vertical axis through the hips, per height bin and angular
        /// sector (97th percentile of the mesh), from just above the hips down to the coat hem. Used by ScabbardMotion.
        /// </summary>
        public static void SkirtProfile(GameObject go, Transform hips, Vector3 up, Vector3 fwd, int sectors, float top, float step, float bottom,
                                        out float[] radii, Report rep)
        {
            Vector3 right = Vector3.Cross(up, fwd);
            int bins = Mathf.CeilToInt((top - bottom) / step);
            var cells = new List<float>[bins * sectors];
            for (int i = 0; i < cells.Length; i++) cells[i] = new List<float>();
            // only clothing / body around the pelvis and legs: arms (A-pose bind hands hang at hip height) are excluded
            var map = GKCharacters.MapBones(go.transform);
            var owners = new List<Transform> { hips };
            foreach (var n in new[] { "Spine", "Chest", "LeftUpperLeg", "LeftLowerLeg", "RightUpperLeg", "RightLowerLeg", "LeftUpperArm", "RightUpperArm" })
            { Transform t; if (map.TryGetValue(n, out t) && !owners.Contains(t)) owners.Add(t); }
            var owned = OwnedVertices(go, owners);
            var pts = new List<Vector3>();
            foreach (var kv in owned)
            {
                if (kv.Key == map["LeftUpperArm"] || kv.Key == map["RightUpperArm"]) continue;   // whole arms belong to these owners
                pts.AddRange(kv.Value);
            }
            foreach (var p in pts)
            {
                Vector3 d = p - hips.position;
                float h = Vector3.Dot(d, up);
                int bin = Mathf.FloorToInt((top - h) / step);
                if (bin < 0 || bin >= bins) continue;
                Vector3 radial = d - up * h;
                float r = radial.magnitude;
                if (r > 0.7f) continue;                                  // stray geometry (sword etc.)
                float ang = Mathf.Atan2(Vector3.Dot(radial, right), Vector3.Dot(radial, fwd)) * Mathf.Rad2Deg;
                if (ang < 0f) ang += 360f;
                int sector = Mathf.Clamp(Mathf.FloorToInt(ang / (360f / sectors)), 0, sectors - 1);
                cells[bin * sectors + sector].Add(r);
            }
            radii = new float[cells.Length];
            for (int i = 0; i < cells.Length; i++) radii[i] = cells[i].Count > 8 ? Mathf.Min(0.45f, Percentile(cells[i], 0.97f)) : 0f;
            if (rep != null)
            {
                var sb = new System.Text.StringBuilder("skirt profile (max radius per height, m):");
                for (int b = 0; b < bins; b++)
                {
                    float mx = 0f; for (int k = 0; k < sectors; k++) mx = Mathf.Max(mx, radii[b * sectors + k]);
                    sb.Append(' ').Append((top - b * step).ToString("F2")).Append(':').Append(mx.ToString("F2"));
                }
                rep.lines.Add(sb.ToString());
            }
        }

        static float Percentile(List<float> v, float p)
        {
            if (v.Count == 0) return 0f;
            v.Sort();
            float f = Mathf.Clamp01(p) * (v.Count - 1);
            int i = Mathf.FloorToInt(f);
            int j = Mathf.Min(i + 1, v.Count - 1);
            return Mathf.Lerp(v[i], v[j], f - i);
        }

        // ------------------------------------------------------------------ shapes

        static CapsuleCollider FitCapsule(Transform bone, Vector3 a, Vector3 b, List<Vector3> pts, float pct, float minR, float maxR,
                                          float endPct, Report rep, string label)
        {
            Vector3 dir = b - a; float L = dir.magnitude; Vector3 n = dir / L;
            var rs = new List<float>();
            Vector3 offSum = Vector3.zero; int offN = 0;
            var ts = new List<float>();
            foreach (var p in pts)
            {
                Vector3 d = p - a;
                float t = Vector3.Dot(d, n) / L;
                ts.Add(t);
                if (t < 0.1f || t > 0.9f) continue;
                Vector3 perp = d - n * (t * L);
                rs.Add(perp.magnitude);
                offSum += perp; offN++;
            }
            float r = rs.Count > 20 ? Percentile(rs, pct) : (minR + maxR) * 0.5f;
            r = Mathf.Clamp(r, minR, maxR);
            Vector3 shift = offN > 20 ? offSum / offN : Vector3.zero;
            if (shift.magnitude > 0.035f) shift = shift.normalized * 0.035f;
            // capsule end: where the owned geometry really ends (hand / foot) when asked
            float tEnd = 1f;
            if (endPct > 0f && ts.Count > 20) tEnd = Mathf.Clamp(Percentile(ts, endPct), 1f, 1.8f);
            Vector3 a2 = a + shift, b2 = a + n * (L * tEnd) + shift;
            // the capsule includes its caps: pull the caps inside the segment so the ends are not overshot
            Vector3 ca = a2 + n * Mathf.Min(r * 0.6f, L * 0.2f), cb = b2 - n * Mathf.Min(r * 0.6f, L * 0.2f);

            var col = bone.gameObject.AddComponent<CapsuleCollider>();
            Vector3 la = bone.InverseTransformPoint(ca), lb = bone.InverseTransformPoint(cb);
            Vector3 dl = lb - la;
            int axis = Mathf.Abs(dl.x) > Mathf.Abs(dl.y) ? (Mathf.Abs(dl.x) > Mathf.Abs(dl.z) ? 0 : 2) : (Mathf.Abs(dl.y) > Mathf.Abs(dl.z) ? 1 : 2);
            float s = bone.lossyScale.x;
            col.direction = axis;
            col.center = (la + lb) * 0.5f;
            col.radius = r / s;
            col.height = dl.magnitude + 2f * r / s;
            float misalign = Vector3.Angle(dl, axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward);
            if (misalign > 90f) misalign = 180f - misalign;
            rep.lines.Add(string.Format("{0,-14} capsule r={1:F3} len={2:F3} shift={3:F3} n={4} axisErr={5:F1}deg", label, r, (cb - ca).magnitude + 2f * r, shift.magnitude, pts.Count, misalign));
            return col;
        }

        static BoxCollider FitBox(Transform bone, List<Vector3> pts, Vector3 upFrom, Vector3 upTo, float lo, float hi, Report rep, string label)
        {
            var col = bone.gameObject.AddComponent<BoxCollider>();
            var xs = new List<float>(); var ys = new List<float>(); var zs = new List<float>();
            Vector3 lf = bone.InverseTransformPoint(upFrom), lt = bone.InverseTransformPoint(upTo);
            Vector3 lu = lt - lf;
            int ua = Mathf.Abs(lu.x) > Mathf.Abs(lu.y) ? (Mathf.Abs(lu.x) > Mathf.Abs(lu.z) ? 0 : 2) : (Mathf.Abs(lu.y) > Mathf.Abs(lu.z) ? 1 : 2);
            float uMin = Mathf.Min(lf[ua], lt[ua]), uMax = Mathf.Max(lf[ua], lt[ua]);
            foreach (var p in pts)
            {
                Vector3 l = bone.InverseTransformPoint(p);
                if (l[ua] < uMin || l[ua] > uMax) continue;      // only the slab between the two joints
                xs.Add(l.x); ys.Add(l.y); zs.Add(l.z);
            }
            Vector3 mn, mx;
            if (xs.Count > 30)
            {
                mn = new Vector3(Percentile(xs, lo), Percentile(ys, lo), Percentile(zs, lo));
                mx = new Vector3(Percentile(xs, hi), Percentile(ys, hi), Percentile(zs, hi));
            }
            else { mn = Vector3.Min(lf, lt) - Vector3.one * 0.1f / bone.lossyScale.x; mx = Vector3.Max(lf, lt) + Vector3.one * 0.1f / bone.lossyScale.x; }
            mn[ua] = uMin; mx[ua] = uMax;
            col.center = (mn + mx) * 0.5f;
            col.size = mx - mn;
            float s = bone.lossyScale.x;
            rep.lines.Add(string.Format("{0,-14} box {1:F3} x {2:F3} x {3:F3} m  centre off-bone {4:F3} n={5}", label, col.size.x * s, col.size.y * s, col.size.z * s,
                (bone.TransformPoint(col.center) - (upFrom + upTo) * 0.5f).magnitude, xs.Count));
            return col;
        }

        static SphereCollider FitHead(Transform head, List<Vector3> pts, Vector3 right, Vector3 fwd, Report rep)
        {
            var col = head.gameObject.AddComponent<SphereCollider>();
            Vector3 up = Vector3.up;
            var xs = new List<float>(); var ys = new List<float>(); var zs = new List<float>();
            foreach (var p in pts)
            {
                Vector3 d = p - head.position;
                if (Vector3.Dot(d, up) < -0.02f) continue;                // ignore the aventail / collar below the skull
                xs.Add(Vector3.Dot(d, right)); ys.Add(Vector3.Dot(d, up)); zs.Add(Vector3.Dot(d, fwd));
            }
            Vector3 c; float r;
            if (xs.Count > 30)
            {
                float x0 = Percentile(xs, 0.03f), x1 = Percentile(xs, 0.97f);
                float y0 = Percentile(ys, 0.03f), y1 = Percentile(ys, 0.97f);
                float z0 = Percentile(zs, 0.03f), z1 = Percentile(zs, 0.97f);
                c = head.position + right * (x0 + x1) * 0.5f + up * (y0 + y1) * 0.5f + fwd * (z0 + z1) * 0.5f;
                r = Mathf.Max(x1 - x0, Mathf.Max(y1 - y0, z1 - z0)) * 0.5f * 0.93f;
                r = Mathf.Clamp(r, 0.09f, 0.16f);
            }
            else { c = head.position + up * 0.09f; r = 0.115f; }
            col.center = head.InverseTransformPoint(c);
            col.radius = r / head.lossyScale.x;
            rep.lines.Add(string.Format("{0,-14} sphere r={1:F3} centre above head bone {2:F3} n={3}", "Head", r, Vector3.Dot(c - head.position, up), xs.Count));
            return col;
        }

        // ------------------------------------------------------------------ bodies and joints

        static Rigidbody Body(Ragdoll rag, Transform bone, float mass, float dmgMul, Collider col)
        {
            bone.gameObject.layer = GK.LayerHurtbox;
            var rb = bone.gameObject.AddComponent<Rigidbody>();
            rb.mass = mass; rb.isKinematic = true; rb.useGravity = true;
            rb.linearDamping = 0.05f; rb.angularDamping = 0.8f;
            rb.maxAngularVelocity = 30f;
            col.isTrigger = true;
            var hb = bone.gameObject.AddComponent<Hurtbox>();
            hb.damageMultiplier = dmgMul;
            rag.bodies.Add(rb);
            rag.colliders.Add(col);
            return rb;
        }

        /// <summary>
        /// Joint whose allowed rotation about <paramref name="A"/> (world, Quaternion.AngleAxis sense) is
        /// [-ext, +flex]; symmetric swing limits about <paramref name="S"/> and A x S.
        /// </summary>
        static CharacterJoint Joint(Rigidbody child, Rigidbody parent, Vector3 A, Vector3 S, float flex, float ext, float swing1, float swing2)
        {
            var t = child.transform;
            var j = t.gameObject.AddComponent<CharacterJoint>();
            j.connectedBody = parent;
            j.autoConfigureConnectedAnchor = true;
            j.anchor = Vector3.zero;
            j.axis = t.InverseTransformDirection(A.normalized);
            Vector3 s = (S - Vector3.Dot(S, A.normalized) * A.normalized).normalized;
            j.swingAxis = t.InverseTransformDirection(s);
            float lo, hi;
            if (twistSign >= 0) { lo = -ext; hi = flex; } else { lo = -flex; hi = ext; }
            var low = j.lowTwistLimit; low.limit = lo; low.bounciness = 0f; low.contactDistance = 2f; j.lowTwistLimit = low;
            var high = j.highTwistLimit; high.limit = hi; high.bounciness = 0f; high.contactDistance = 2f; j.highTwistLimit = high;
            var s1 = j.swing1Limit; s1.limit = Mathf.Max(3f, swing1); s1.contactDistance = 2f; j.swing1Limit = s1;
            var s2 = j.swing2Limit; s2.limit = Mathf.Max(3f, swing2); s2.contactDistance = 2f; j.swing2Limit = s2;
            // NB: any damper > 0 turns a PhysX limit soft (isSoft = stiffness > 0 || damping > 0): keep both 0 for hard limits
            j.twistLimitSpring = new SoftJointLimitSpring();
            j.swingLimitSpring = new SoftJointLimitSpring();
            j.enablePreprocessing = false;
            j.enableProjection = true;
            j.projectionDistance = 0.05f;
            j.projectionAngle = 20f;
            return j;
        }

        /// <summary>Direction the child bone travels when the avatar's "<paramref name="muscle"/>" bends it (world, from the current pose).</summary>
        static bool MuscleHinge(GameObject go, string muscle, Transform parentBone, Transform bone, Transform childTip, out Vector3 axis)
        {
            axis = Vector3.zero;
            var anim = go.GetComponent<Animator>();
            if (anim == null || anim.avatar == null || !anim.avatar.isHuman) return false;
            int mi = System.Array.IndexOf(HumanTrait.MuscleName, muscle);
            if (mi < 0) return false;
            var all = go.GetComponentsInChildren<Transform>(true);
            var lp = all.Select(t => t.localPosition).ToArray();
            var lr = all.Select(t => t.localRotation).ToArray();
            Vector3 rootPos = go.transform.position; Quaternion rootRot = go.transform.rotation;
            Quaternion parentRot0 = parentBone.rotation;
            Vector3 c0 = (childTip.position - bone.position).normalized;
            Vector3 c0Local = Quaternion.Inverse(parentRot0) * c0;
            try
            {
                var h = new HumanPoseHandler(anim.avatar, go.transform);
                var pose = new HumanPose();
                h.GetHumanPose(ref pose);
                Vector3 best = Vector3.zero; float bestBend = -1f;
                foreach (float v in new[] { -1f, 1f })
                {
                    var p2 = pose; p2.muscles = (float[])pose.muscles.Clone();
                    p2.muscles[mi] = v;
                    h.SetHumanPose(ref p2);
                    Vector3 c1 = (childTip.position - bone.position).normalized;
                    Vector3 c1Local = Quaternion.Inverse(parentBone.rotation) * c1;    // relative to the parent segment
                    float bend = Vector3.Angle(c0Local, c1Local);
                    Vector3 parentDir = (bone.position - parentBone.position).normalized;
                    // flexion folds the child back towards the parent segment
                    float fold = Vector3.Angle(Quaternion.Inverse(parentBone.rotation) * parentDir, c1Local);
                    if (fold > bestBend) { bestBend = fold; best = c1Local; }
                }
                Vector3 cr = Vector3.Cross(c0Local, best);
                if (cr.sqrMagnitude < 1e-6f) return false;
                axis = (parentRot0 * cr).normalized;
                h.Dispose();
                return true;
            }
            finally
            {
                for (int i = 0; i < all.Length; i++) { all[i].localPosition = lp[i]; all[i].localRotation = lr[i]; }
                go.transform.position = rootPos; go.transform.rotation = rootRot;
            }
        }

        /// <summary>Measures once whether CharacterJoint twist follows Quaternion.AngleAxis about the joint axis.</summary>
        static int CalibrateTwistSign()
        {
            var prevMode = Physics.simulationMode;
            var a = new GameObject("gk_cal_parent"); var b = new GameObject("gk_cal_child");
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                a.transform.position = new Vector3(0f, 9000f, 0f);
                b.transform.position = a.transform.position + Vector3.down * 0.5f;
                var ra = a.AddComponent<Rigidbody>(); ra.isKinematic = true;
                var rb = b.AddComponent<Rigidbody>(); rb.useGravity = false; rb.angularDamping = 0f; rb.mass = 1f;
                var sc = b.AddComponent<SphereCollider>(); sc.isTrigger = true; sc.radius = 0.05f;
                var j = b.AddComponent<CharacterJoint>();
                j.connectedBody = ra; j.anchor = new Vector3(0f, 0.5f, 0f);
                j.axis = Vector3.right; j.swingAxis = Vector3.up;
                var low = j.lowTwistLimit; low.limit = -5f; j.lowTwistLimit = low;
                var high = j.highTwistLimit; high.limit = 70f; j.highTwistLimit = high;
                var s1 = j.swing1Limit; s1.limit = 3f; j.swing1Limit = s1;
                var s2 = j.swing2Limit; s2.limit = 3f; j.swing2Limit = s2;
                Physics.SyncTransforms();
                Vector3 d0 = b.transform.position - a.transform.position;
                rb.angularVelocity = Vector3.right * 3f;      // +AngleAxis sense about +X
                for (int i = 0; i < 60; i++) Physics.Simulate(0.01f);
                Vector3 d1 = b.transform.position - a.transform.position;
                float ang = Vector3.SignedAngle(d0, d1, Vector3.right);
                int sign = ang > 25f ? 1 : -1;
                GKBridge.Log("ragdoll: twist calibration angle " + ang.ToString("F1") + " -> sign " + sign);
                return sign;
            }
            finally
            {
                Object.DestroyImmediate(a); Object.DestroyImmediate(b);
                Physics.simulationMode = prevMode;
            }
        }

        public static Ragdoll Build(GameObject go, Dictionary<string, Transform> m, Report rep)
        {
            if (twistSign == 0) twistSign = CalibrateTwistSign();
            var rag = go.AddComponent<Ragdoll>();
            Vector3 right, fwd;
            GKCharacters.CharacterFrame(m, out right, out fwd);
            Vector3 up = Vector3.up;

            Transform hips = m["Hips"], chest = m.ContainsKey("Chest") ? m["Chest"] : m["Spine"], head = m["Head"];
            Transform neck = m.ContainsKey("Neck") ? m["Neck"] : head;
            var bodyBones = new List<Transform> { hips, chest, head };
            foreach (var side in new[] { "Left", "Right" })
                foreach (var part in new[] { "UpperArm", "LowerArm", "UpperLeg", "LowerLeg" })
                    bodyBones.Add(m[side + part]);
            var owned = OwnedVertices(go, bodyBones);

            // ---- torso
            Vector3 crotch = (m["LeftUpperLeg"].position + m["RightUpperLeg"].position) * 0.5f - up * 0.07f;
            var colHips = FitBox(hips, owned[hips], crotch, chest.position, 0.04f, 0.96f, rep, "Hips");
            var rbHips = Body(rag, hips, 12f, 1f, colHips);
            // top of the chest box sits just under the neck so its corners do not stick out above sloping shoulders
            var colChest = FitBox(chest, owned[chest], chest.position, neck.position - up * 0.02f, 0.04f, 0.96f, rep, "Chest");
            var rbChest = Body(rag, chest, 14f, 1f, colChest);
            Joint(rbChest, rbHips, Vector3.Cross(up, fwd), fwd, 30f, 20f, 20f, 20f);
            var colHead = FitHead(head, owned[head], right, fwd, rep);
            var rbHead = Body(rag, head, 5f, 1.6f, colHead);
            Joint(rbHead, rbChest, Vector3.Cross(up, fwd), fwd, 45f, 35f, 30f, 35f);

            foreach (var side in new[] { "Left", "Right" })
            {
                Vector3 outDir = side == "Left" ? -right : right;
                var ua = m[side + "UpperArm"]; var la = m[side + "LowerArm"]; var hand = m[side + "Hand"];
                var colUA = FitCapsule(ua, ua.position, la.position, owned[ua], 0.8f, 0.04f, 0.09f, 0f, rep, side + "UpperArm");
                var rbUA = Body(rag, ua, 2.5f, 0.85f, colUA);
                // arm swings forward (+) / back (-) in the horizontal plane; raise/lower is swing about fwd
                Joint(rbUA, rbChest, Vector3.Cross(outDir, fwd), fwd, 95f, 35f, 80f, 40f);

                var colLA = FitCapsule(la, la.position, hand.position, owned[la], 0.8f, 0.035f, 0.075f, 0.97f, rep, side + "LowerArm");
                var rbLA = Body(rag, la, 2f, 0.8f, colLA);
                Vector3 elbowAxis;
                if (!MuscleHinge(go, side + " Forearm Stretch", ua, la, hand, out elbowAxis))
                    elbowAxis = Vector3.Cross(outDir, fwd);
                rep.lines.Add(string.Format("{0,-14} elbow hinge axis (r,u,f)=({1:F2},{2:F2},{3:F2})", side + "Elbow",
                    Vector3.Dot(elbowAxis, right), Vector3.Dot(elbowAxis, up), Vector3.Dot(elbowAxis, fwd)));
                Joint(rbLA, rbUA, elbowAxis, (hand.position - la.position).normalized, 140f, 2f, 50f, 4f);

                var ul = m[side + "UpperLeg"]; var ll = m[side + "LowerLeg"]; var foot = m[side + "Foot"];
                var colUL = FitCapsule(ul, ul.position, ll.position, owned[ul], 0.65f, 0.065f, 0.12f, 0f, rep, side + "UpperLeg");
                var rbUL = Body(rag, ul, 7.5f, 0.85f, colUL);
                Joint(rbUL, rbHips, Vector3.Cross(-up, fwd), fwd, 100f, 25f, 35f, 25f);

                var colLL = FitCapsule(ll, ll.position, foot.position, owned[ll], 0.7f, 0.045f, 0.085f, 0f, rep, side + "LowerLeg");
                var rbLL = Body(rag, ll, 4f, 0.8f, colLL);
                Vector3 kneeAxis;
                if (!MuscleHinge(go, side + " Lower Leg Stretch", ul, ll, foot, out kneeAxis))
                    kneeAxis = Vector3.Cross(-up, -fwd);
                rep.lines.Add(string.Format("{0,-14} knee hinge axis (r,u,f)=({1:F2},{2:F2},{3:F2})", side + "Knee",
                    Vector3.Dot(kneeAxis, right), Vector3.Dot(kneeAxis, up), Vector3.Dot(kneeAxis, fwd)));
                Joint(rbLL, rbUL, kneeAxis, (foot.position - ll.position).normalized, 135f, 2f, 8f, 4f);
            }
            rag.hips = rbHips;
            return rag;
        }

        // ------------------------------------------------------------------ verification

        /// <summary>Signed rotation of q about unit axis a (swing-twist decomposition), degrees in [-180, 180].</summary>
        static float TwistAngle(Quaternion q, Vector3 a)
        {
            float p = q.x * a.x + q.y * a.y + q.z * a.z;
            float ang = 2f * Mathf.Atan2(p, q.w) * Mathf.Rad2Deg;
            if (ang > 180f) ang -= 360f;
            if (ang < -180f) ang += 360f;
            return ang;
        }

        /// <summary>Fraction of each body's owned vertices that lie inside (or within tol of) its collider.</summary>
        public static void Coverage(GameObject go, Ragdoll rag, Report rep, float tol = 0.02f)
        {
            var bones = rag.bodies.Select(b => b.transform).ToList();
            var owned = OwnedVertices(go, bones);
            Physics.SyncTransforms();
            foreach (var col in rag.colliders)
            {
                var pts = owned[col.transform];
                if (pts.Count == 0) continue;
                int inside = 0;
                foreach (var p in pts)
                {
                    Vector3 c = col.ClosestPoint(p);
                    if ((c - p).sqrMagnitude <= tol * tol) inside++;
                }
                rep.lines.Add(string.Format("coverage {0,-24} {1,5:P0} of {2} verts within {3:F2} m", col.transform.name, inside / (float)pts.Count, pts.Count, tol));
            }
        }

        /// <summary>Physically drives each limb in its anatomical bend direction and the opposite one and reports the reached angles.</summary>
        public static void TestJoints(GameObject source, Report rep)
        {
            var go = Object.Instantiate(source);
            go.transform.position = new Vector3(0f, 9000f, 0f);
            var prevMode = Physics.simulationMode;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                var rag = go.GetComponent<Ragdoll>();
                var anim = go.GetComponent<Animator>(); if (anim != null) anim.enabled = false;
                var cc = go.GetComponent<CharacterController>(); if (cc != null) cc.enabled = false;
                var all = go.GetComponentsInChildren<Transform>(true);
                var lp = all.Select(t => t.localPosition).ToArray();
                var lr = all.Select(t => t.localRotation).ToArray();
                foreach (var j in go.GetComponentsInChildren<CharacterJoint>())
                {
                    var child = j.GetComponent<Rigidbody>(); var parent = j.connectedBody;
                    Vector3 Aw = child.transform.TransformDirection(j.axis);
                    float[] reached = new float[2];
                    for (int k = 0; k < 2; k++)
                    {
                        for (int i = 0; i < all.Length; i++) { all[i].localPosition = lp[i]; all[i].localRotation = lr[i]; }
                        foreach (var b in rag.bodies) { b.isKinematic = true; }
                        // the tested limb and everything below it is free, the rest is pinned
                        var free = child.GetComponentsInChildren<Rigidbody>();
                        foreach (var b in free)
                        {
                            b.isKinematic = false; b.useGravity = false; b.angularDamping = 0.05f;
                            b.linearVelocity = Vector3.zero; b.angularVelocity = Vector3.zero;
                        }
                        Physics.SyncTransforms();
                        Quaternion r0 = Quaternion.Inverse(parent.transform.rotation) * child.transform.rotation;
                        Vector3 aP = Quaternion.Inverse(parent.transform.rotation) * Aw;
                        // Joint() lays A out so that +AngleAxis about A is flexion; angularVelocity uses the same sense.
                        // One kick, then free motion: the extreme angle reached is the effective limit.
                        float sgn = k == 0 ? 1f : -1f;
                        child.angularVelocity = Aw * 7f * sgn;
                        float extreme = 0f;
                        for (int s = 0; s < 120; s++)
                        {
                            Physics.Simulate(0.005f);
                            Quaternion r1 = Quaternion.Inverse(parent.transform.rotation) * child.transform.rotation;
                            float a = TwistAngle(r1 * Quaternion.Inverse(r0), aP);
                            if (a * sgn > extreme * sgn) extreme = a;
                        }
                        reached[k] = extreme;
                        foreach (var b in free) { b.isKinematic = true; }
                    }
                    rep.lines.Add(string.Format("joint {0,-24} flex {1,6:F1}  extend {2,6:F1}  (twist limits {3:F0}..{4:F0}, sign {5})",
                        j.name, reached[0], reached[1], j.lowTwistLimit.limit, j.highTwistLimit.limit, twistSign));
                }
            }
            finally
            {
                Physics.simulationMode = prevMode;
                Object.DestroyImmediate(go);
            }
        }
    
        // ------------------------------------------------------------------ hurtbox against the drawn body (§3c)

        /// <summary>
        /// hurtfit (Team Lead, §3c): each hurtbox of a prefab against its baked skinned body (all visible layers: skin,
        /// clothes, hair), in the prefab's own pose. Per collider: OUT, how far its surface stands outside the body (a blade
        /// stopped there reads as an air-hit), from surface samples signed by the nearest skin vertex's normal; IN, how far
        /// its own bone's skin stands outside the collider (a blade there passes into flesh unreported). p95 and max, cm;
        /// bar 1 cm each (IN at head, shoulders and chest). Stills front and side with the colliders drawn over the body.
        /// Edit mode; nothing is changed.
        /// </summary>
        [GKCommand("hurtfit", "hurtfit [hero|warrior|veteran|archer|all|prefab path] [noshots]: each hurtbox against the baked body: how far it stands outside (air-hits) and how far the skin stands outside it (unreported cuts), p95/max cm, with stills (edit mode)")]
        static void HurtFit(string arg)
        {
            var p = (arg ?? "").Trim().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries).ToList();
            bool shots = !p.Remove("noshots");
            string which = p.Count > 0 ? p[0] : "all";
            var map = new Dictionary<string, string> {
                { "hero", GKHeroCC.CleanPrefabPath }, { "warrior", GKGameBuilder.EnemyPrefabPath },
                { "veteran", GKCombatBuilder.VeteranPrefabPath }, { "archer", GKCombatBuilder.ArcherPrefabPath } };
            var paths = which == "all" ? map.Values.ToList() : map.ContainsKey(which) ? new List<string> { map[which] } : new List<string> { which };
            foreach (var path in paths)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) { GKBridge.Log("hurtfit: no prefab " + path); continue; }
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                try
                {
                    go.transform.SetPositionAndRotation(new Vector3(0f, -300f, 0f), Quaternion.identity);
                    foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false;
                    var boxes = go.GetComponentsInChildren<Hurtbox>(true).Select(h => h.GetComponent<Collider>()).Where(c => c != null).ToList();
                    // with a hit-only set, that set is what hits: measure it (the ragdoll's colliders are off while alive)
                    if (boxes.Any(c => c.name.StartsWith("Hit_"))) boxes = boxes.Where(c => c.name.StartsWith("Hit_")).ToList();
                    // the drawn body: every skinned vertex with its normal, in world space
                    var vs = new List<Vector3>(); var ns = new List<Vector3>();
                    foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>())
                    {
                        if (smr.sharedMesh == null || !smr.gameObject.activeInHierarchy) continue;
                        var baked = new Mesh(); smr.BakeMesh(baked, true);
                        Vector3 o = smr.transform.position; Quaternion q = smr.transform.rotation;
                        var bv = baked.vertices; var bn = baked.normals;
                        for (int i = 0; i < bv.Length; i++) { vs.Add(o + q * bv[i]); ns.Add(i < bn.Length ? q * bn[i] : Vector3.up); }
                        Object.DestroyImmediate(baked);
                    }
                    var owned = OwnedVertices(go, boxes.Select(c => c.transform).ToList());
                    var sb = new System.Text.StringBuilder("hurtfit " + prefab.name + " (" + boxes.Count + " hurtboxes, " + vs.Count + " body vertices), OUT = stands outside the body, IN = body outside it, p95/max cm:");
                    int outBad = 0, inBad = 0;
                    foreach (var c in boxes)
                    {
                        // OUT: surface samples, each signed by its nearest body vertex's normal
                        var outs = new List<float>();
                        foreach (var s in SurfaceSamples(c))
                        {
                            int bi = -1; float bd = float.MaxValue;
                            for (int i = 0; i < vs.Count; i++) { float d = (vs[i] - s).sqrMagnitude; if (d < bd) { bd = d; bi = i; } }
                            if (bi < 0) continue;
                            outs.Add(Mathf.Max(0f, Vector3.Dot(s - vs[bi], ns[bi])));
                        }
                        // IN: the bone's own body vertices outside the collider
                        var ins = new List<float>();
                        List<Vector3> mine;
                        if (owned.TryGetValue(c.transform, out mine))
                            foreach (var v in mine) ins.Add((c.ClosestPoint(v) - v).magnitude);
                        float o95 = outs.Count > 0 ? Percentile(outs, 0.95f) * 100f : 0f, oMax = outs.Count > 0 ? outs.Max() * 100f : 0f;
                        float i95 = ins.Count > 0 ? Percentile(ins, 0.95f) * 100f : 0f, iMax = ins.Count > 0 ? ins.Max() * 100f : 0f;
                        string bone = c.transform.name;
                        bool main = bone.Contains("Head") || bone.Contains("Neck") || bone.Contains("Spine") || bone.Contains("Chest") || bone.Contains("UpperArm") || bone.Contains("Upperarm") || bone.Contains("Clavicle") || bone.Contains("Shoulder");
                        bool oFail = o95 > 1f, iFail = main && i95 > 1f;
                        if (oFail) outBad++; if (iFail) inBad++;
                        sb.Append(" | ").Append(bone).Append(' ').Append(c.GetType().Name.Replace("Collider", "")).Append(" OUT ").Append(o95.ToString("F1")).Append('/').Append(oMax.ToString("F1"))
                          .Append(oFail ? "!" : "").Append(" IN ").Append(i95.ToString("F1")).Append('/').Append(iMax.ToString("F1")).Append(iFail ? "!" : "").Append(main ? " (main)" : "").Append(" n=").Append(ins.Count);
                    }
                    sb.Insert(sb.ToString().IndexOf(':') + 1, " " + outBad + " over 1 cm outside, " + inBad + " main areas over 1 cm inside;");
                    GKBridge.Log(sb.ToString());
                    if (shots) HurtStills(go, boxes, prefab.name);
                }
                finally { Object.DestroyImmediate(go); }
            }
        }

        /// <summary>Points on a collider's surface (world): capsule rings and caps, sphere directions, box face grids.</summary>
        static IEnumerable<Vector3> SurfaceSamples(Collider c)
        {
            var t = c.transform; Vector3 s = t.lossyScale;
            var cap = c as CapsuleCollider; var sph = c as SphereCollider; var box = c as BoxCollider;
            var dirs = new List<Vector3>();
            for (int a = 0; a < 26; a++)
            {
                float u = (a + 0.5f) / 26f, th = Mathf.Acos(1f - 2f * u), ph = Mathf.PI * (1f + Mathf.Sqrt(5f)) * a;
                dirs.Add(new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph)));
            }
            if (sph != null)
            {
                float r = sph.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                Vector3 c0 = t.TransformPoint(sph.center);
                foreach (var d in dirs) yield return c0 + d * r;
            }
            else if (cap != null)
            {
                Vector3 ax = Vector3.zero; ax[cap.direction] = 1f;
                Vector3 c0 = t.TransformPoint(cap.center), axW = t.TransformDirection(ax).normalized;
                float sAx = Mathf.Abs(s[cap.direction]), sR = 0f;
                for (int k = 0; k < 3; k++) if (k != cap.direction) sR = Mathf.Max(sR, Mathf.Abs(s[k]));
                float r = cap.radius * sR, half = Mathf.Max(0f, cap.height * sAx * 0.5f - r);
                Vector3 p1 = Vector3.Cross(axW, Mathf.Abs(Vector3.Dot(axW, Vector3.up)) < 0.9f ? Vector3.up : Vector3.right).normalized, p2 = Vector3.Cross(axW, p1);
                for (int i = 0; i <= 4; i++)
                    for (int a = 0; a < 12; a++)
                    {
                        float ang = a * Mathf.PI * 2f / 12f;
                        yield return c0 + axW * Mathf.Lerp(-half, half, i / 4f) + (p1 * Mathf.Cos(ang) + p2 * Mathf.Sin(ang)) * r;
                    }
                foreach (var d in dirs)
                {
                    float along = Vector3.Dot(d, axW);
                    if (Mathf.Abs(along) < 0.2f) continue;
                    yield return c0 + axW * (Mathf.Sign(along) * half) + d * r;
                }
            }
            else if (box != null)
            {
                Vector3 h = box.size * 0.5f;
                for (int f = 0; f < 3; f++)
                    for (int sg = -1; sg <= 1; sg += 2)
                        for (int i = -1; i <= 1; i++)
                            for (int j = -1; j <= 1; j++)
                            {
                                Vector3 l = Vector3.zero; l[f] = sg * h[f]; l[(f + 1) % 3] = i * h[(f + 1) % 3]; l[(f + 2) % 3] = j * h[(f + 2) % 3];
                                yield return t.TransformPoint(box.center + l);
                            }
            }
        }

        /// <summary>Front and side stills with each hurtbox drawn as a translucent shape over the body.</summary>
        static void HurtStills(GameObject go, List<Collider> boxes, string name)
        {
            var shapes = new List<GameObject>();
            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            mat.SetFloat("_Surface", 1f); mat.SetFloat("_Blend", 0f);
            mat.SetOverrideTag("RenderType", "Transparent"); mat.renderQueue = 3000;
            mat.SetFloat("_ZWrite", 0f); mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha); mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.SetColor("_BaseColor", new Color(1f, 0.15f, 0.1f, 0.35f));
            var cam = new GameObject("~hurtcam").AddComponent<Camera>();
            try
            {
                foreach (var c in boxes)
                {
                    GameObject g = null; var t = c.transform; Vector3 s = t.lossyScale;
                    var cap = c as CapsuleCollider; var sph = c as SphereCollider; var box = c as BoxCollider;
                    if (sph != null)
                    {
                        g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        g.transform.position = t.TransformPoint(sph.center);
                        g.transform.localScale = Vector3.one * sph.radius * 2f * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                    }
                    else if (cap != null)
                    {
                        g = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                        Vector3 ax = Vector3.zero; ax[cap.direction] = 1f;
                        float sR = 0f; for (int k = 0; k < 3; k++) if (k != cap.direction) sR = Mathf.Max(sR, Mathf.Abs(s[k]));
                        float r = cap.radius * sR, hgt = Mathf.Max(cap.height * Mathf.Abs(s[cap.direction]), 2f * r);
                        g.transform.position = t.TransformPoint(cap.center);
                        g.transform.rotation = Quaternion.FromToRotation(Vector3.up, t.TransformDirection(ax));
                        g.transform.localScale = new Vector3(r * 2f, hgt * 0.5f, r * 2f);
                    }
                    else if (box != null)
                    {
                        g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        g.transform.position = t.TransformPoint(box.center); g.transform.rotation = t.rotation;
                        g.transform.localScale = Vector3.Scale(box.size, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));
                    }
                    if (g == null) continue;
                    Object.DestroyImmediate(g.GetComponent<Collider>());
                    g.GetComponent<Renderer>().sharedMaterial = mat;
                    shapes.Add(g);
                }
                var anim = go.GetComponentInChildren<Animator>();
                Transform hips = anim != null && anim.isHuman ? anim.GetBoneTransform(HumanBodyBones.Hips) : go.transform;
                Vector3 look = hips.position + Vector3.up * 0.3f;
                cam.nearClipPlane = 0.05f; cam.fieldOfView = 30f; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.55f, 0.6f, 0.68f);
                foreach (var view in new[] { "front", "side" })
                {
                    Vector3 dir = view == "front" ? go.transform.forward : go.transform.right;
                    cam.transform.position = look + dir * 3.6f;
                    cam.transform.rotation = Quaternion.LookRotation(look - cam.transform.position, Vector3.up);
                    GKPreview.Capture(cam, "hurtfit_" + name + "_" + view, 900, 1200);
                }
            }
            finally
            {
                foreach (var g in shapes) Object.DestroyImmediate(g);
                Object.DestroyImmediate(cam.gameObject);
                Object.DestroyImmediate(mat);
            }
        }
    
        // ------------------------------------------------------------------ the hit-only set (§3c, the Team Lead)

        /// <summary>
        /// hitset: hit-only shapes fitted to the drawn body, separate from the ragdoll (whose colliders stay as tested for
        /// death physics and are switched off while alive). Per character, from each body part's own skinned vertices (all
        /// visible layers; weapons and the bow left out): the head a vertical capsule with hair and helmet; the trunk three
        /// capsules across the body (chest, belly, hips); the arms and legs capsules along the bone. Each shrinks until its
        /// surface stands no more than the bar outside the body at p95 (1 cm at the head, chest and shoulders; 2 cm
        /// elsewhere). Trigger colliders on the hurtbox layer, Hurtbox components, children of their bones ("Hit_*").
        /// One builder for every character.
        /// </summary>
        [GKCommand("hitset", "hitset [hero|warrior|veteran|archer|all|prefab path]: build the hit-only shapes fitted to the drawn body (OUT p95 <= 1 cm at head/chest/shoulders, 2 cm elsewhere) and save the prefab (edit mode)")]
        static void HitSetCmd(string arg)
        {
            string which = string.IsNullOrEmpty((arg ?? "").Trim()) ? "all" : arg.Trim();
            var map = new Dictionary<string, string> {
                { "hero", GKHeroCC.CleanPrefabPath }, { "warrior", GKGameBuilder.EnemyPrefabPath },
                { "veteran", GKCombatBuilder.VeteranPrefabPath }, { "archer", GKCombatBuilder.ArcherPrefabPath } };
            var paths = which == "all" ? map.Values.ToList() : map.ContainsKey(which) ? new List<string> { map[which] } : new List<string> { which };
            foreach (var path in paths)
            {
                var root = PrefabUtility.LoadPrefabContents(path);
                if (root == null) { GKBridge.Log("hitset: no prefab " + path); continue; }
                try
                {
                    string rep = BuildHitSet(root);
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    GKBridge.Log("hitset " + System.IO.Path.GetFileNameWithoutExtension(path) + ": " + rep);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            AssetDatabase.SaveAssets();
        }

        class Part { public string name; public HumanBodyBones bone, to; public bool across, main; public float dmg = 1f; }

        /// <summary>Builds (or rebuilds) the hit-only set on a character root; returns a one-line report.</summary>
        public static string BuildHitSet(GameObject root)
        {
            var anim = root.GetComponentInChildren<Animator>();
            if (anim == null || !anim.isHuman) return "no humanoid animator";
            foreach (var old in root.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("Hit_")).ToList()) Object.DestroyImmediate(old.gameObject);
            // the ragdoll's own colliders keep their Hurtbox for nothing: while alive they are off (Ragdoll), so the hits
            // come from this set only
            var rag = root.GetComponent<Ragdoll>();
            if (rag != null) rag.collidersOffWhileAlive = true;
            var parts = new List<Part> {
                new Part { name = "Head", bone = HumanBodyBones.Head, to = HumanBodyBones.Head, main = true, dmg = 1.5f },
                new Part { name = "Chest", bone = HumanBodyBones.UpperChest, to = HumanBodyBones.UpperChest, across = true, main = true },
                new Part { name = "Belly", bone = HumanBodyBones.Spine, to = HumanBodyBones.Spine, across = true, main = true },
                new Part { name = "Hips", bone = HumanBodyBones.Hips, to = HumanBodyBones.Hips, across = true },
                new Part { name = "L_UpperArm", bone = HumanBodyBones.LeftUpperArm, to = HumanBodyBones.LeftLowerArm, main = true },
                new Part { name = "R_UpperArm", bone = HumanBodyBones.RightUpperArm, to = HumanBodyBones.RightLowerArm, main = true },
                new Part { name = "L_Forearm", bone = HumanBodyBones.LeftLowerArm, to = HumanBodyBones.LeftHand },
                new Part { name = "R_Forearm", bone = HumanBodyBones.RightLowerArm, to = HumanBodyBones.RightHand },
                new Part { name = "L_Thigh", bone = HumanBodyBones.LeftUpperLeg, to = HumanBodyBones.LeftLowerLeg },
                new Part { name = "R_Thigh", bone = HumanBodyBones.RightUpperLeg, to = HumanBodyBones.RightLowerLeg },
                new Part { name = "L_Calf", bone = HumanBodyBones.LeftLowerLeg, to = HumanBodyBones.LeftFoot },
                new Part { name = "R_Calf", bone = HumanBodyBones.RightLowerLeg, to = HumanBodyBones.RightFoot } };
            // which bone each vertex belongs to: the part bones (UpperChest falls back to Chest, then Spine)
            var boneOf = new Dictionary<Part, Transform>();
            foreach (var p in parts)
            {
                var t = anim.GetBoneTransform(p.bone);
                if (t == null && p.bone == HumanBodyBones.UpperChest) t = anim.GetBoneTransform(HumanBodyBones.Chest);
                if (t != null) boneOf[p] = t;
            }
            // the drawn body without the weapons (their meshes hang under a WeaponHitbox or the bow)
            var weaponRoots = root.GetComponentsInChildren<WeaponHitbox>(true).Select(w => w.transform).Concat(root.GetComponentsInChildren<BowRig>(true).Select(b => b.transform)).ToList();
            System.Func<Transform, bool> isWeapon = t => weaponRoots.Any(w => t == w || t.IsChildOf(w));
            var vs = new List<Vector3>(); var ns = new List<Vector3>(); var vb = new List<Transform>();
            var bodySet = new HashSet<Transform>(boneOf.Values);
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr.sharedMesh == null || isWeapon(smr.transform)) continue;
                var baked = new Mesh(); smr.BakeMesh(baked, true);
                Vector3 o = smr.transform.position; Quaternion q = smr.transform.rotation;
                var bv = baked.vertices; var bn = baked.normals; var bw = smr.sharedMesh.boneWeights; var bones = smr.bones;
                for (int i = 0; i < bv.Length; i++)
                {
                    Transform own = null;
                    if (i < bw.Length)
                    {
                        var w = bw[i]; int bi = w.boneIndex0; float best = w.weight0;
                        if (w.weight1 > best) { best = w.weight1; bi = w.boneIndex1; }
                        if (w.weight2 > best) { best = w.weight2; bi = w.boneIndex2; }
                        if (w.weight3 > best) { bi = w.boneIndex3; }
                        var bt = bi >= 0 && bi < bones.Length ? bones[bi] : null;
                        while (bt != null && !bodySet.Contains(bt)) bt = bt.parent;
                        own = bt;
                    }
                    vs.Add(o + q * bv[i]); ns.Add(i < bn.Length ? q * bn[i] : Vector3.up); vb.Add(own);
                }
                Object.DestroyImmediate(baked);
            }
            if (vs.Count == 0) return "no body vertices";
            Vector3 up = Vector3.up, right = root.transform.right, fwd = root.transform.forward;
            var sb = new System.Text.StringBuilder(); int made = 0, fails = 0;
            foreach (var p in parts)
            {
                Transform bone;
                if (!boneOf.TryGetValue(p, out bone)) continue;
                var pts = new List<Vector3>();
                for (int i = 0; i < vs.Count; i++) if (vb[i] == bone) pts.Add(vs[i]);
                if (pts.Count < 20) continue;
                // the capsule's axis: across the body (trunk), up (head), along the bone (limbs)
                Vector3 a0, a1; float r;
                if (p.across || p.name == "Head")
                {
                    Vector3 ax = p.across ? right : up, d1 = p.across ? fwd : right, d2 = p.across ? up : fwd;
                    var la = pts.Select(v => Vector3.Dot(v, ax)).ToList(); var l1 = pts.Select(v => Vector3.Dot(v, d1)).ToList(); var l2 = pts.Select(v => Vector3.Dot(v, d2)).ToList();
                    float lo = Percentile(la, 0.04f), hi = Percentile(la, 0.96f);
                    float c1 = (Percentile(l1, 0.05f) + Percentile(l1, 0.95f)) * 0.5f, c2 = (Percentile(l2, 0.05f) + Percentile(l2, 0.95f)) * 0.5f;
                    r = Mathf.Min(Percentile(l1, 0.95f) - Percentile(l1, 0.05f), Percentile(l2, 0.95f) - Percentile(l2, 0.05f)) * 0.5f;
                    Vector3 c = d1 * c1 + d2 * c2;
                    a0 = c + ax * (lo + r); a1 = c + ax * (hi - r);
                    if (Vector3.Dot(a1 - a0, ax) < 0f) { Vector3 m = (a0 + a1) * 0.5f; a0 = m; a1 = m; }
                }
                else
                {
                    var tb = anim.GetBoneTransform(p.to);
                    Vector3 s = bone.position, e = tb != null ? tb.position : bone.position + bone.up * 0.25f, ax = (e - s).normalized;
                    var rad = new List<float>();
                    foreach (var v in pts) { float k = Vector3.Dot(v - s, ax); Vector3 onAx = s + ax * k; rad.Add((v - onAx).magnitude); }
                    r = Percentile(rad, 0.6f);
                    a0 = s + ax * Mathf.Min(r, (e - s).magnitude * 0.5f); a1 = e - ax * Mathf.Min(r, (e - s).magnitude * 0.5f);
                }
                float bar = p.main ? 0.01f : 0.02f, out95 = 0f;
                // only the body near this capsule is searched (the whole body is ~100k vertices)
                var cv = new List<Vector3>(); var cn = new List<Vector3>();
                float reach = r + 0.12f;
                for (int i = 0; i < vs.Count; i++)
                {
                    Vector3 ab = a1 - a0; float k = Mathf.Clamp01(Vector3.Dot(vs[i] - a0, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude));
                    if ((vs[i] - (a0 + ab * k)).sqrMagnitude <= reach * reach) { cv.Add(vs[i]); cn.Add(ns[i]); }
                }
                // shrink until the surface stands no more than the bar outside the body (p95)
                for (int it = 0; it < 30; it++)
                {
                    out95 = CapsuleOut(a0, a1, r, cv, cn);
                    if (out95 <= bar || r < 0.025f) break;
                    r *= 0.93f;
                }
                var go = new GameObject("Hit_" + p.name);
                go.transform.SetParent(bone, false);
                go.layer = GK.LayerHurtbox;
                var col = go.AddComponent<CapsuleCollider>();
                col.isTrigger = true;
                Vector3 mid = (a0 + a1) * 0.5f, axisW = a1 - a0;
                go.transform.position = mid;
                go.transform.rotation = axisW.sqrMagnitude > 1e-8f ? Quaternion.FromToRotation(Vector3.up, axisW.normalized) : Quaternion.identity;
                float sc = Mathf.Max(1e-4f, go.transform.lossyScale.y), sr = Mathf.Max(1e-4f, Mathf.Max(go.transform.lossyScale.x, go.transform.lossyScale.z));
                col.direction = 1; col.center = Vector3.zero;
                col.radius = r / sr; col.height = (axisW.magnitude + 2f * r) / sc;
                var hb = go.AddComponent<Hurtbox>(); hb.damageMultiplier = p.dmg;
                made++; if (out95 > bar) fails++;
                sb.Append(p.name).Append(" r ").Append((r * 100f).ToString("F1")).Append(" L ").Append(((axisW.magnitude + 2f * r) * 100f).ToString("F0")).Append(" OUT95 ").Append((out95 * 100f).ToString("F1")).Append(out95 > bar ? "!" : "").Append("; ");
            }
            return made + " shapes, " + fails + " over their bar: " + sb;
        }

        /// <summary>A capsule's surface outside the body: p95 of its surface samples' distance outside (signed by the nearest
        /// body vertex's normal), m.</summary>
        static float CapsuleOut(Vector3 a0, Vector3 a1, float r, List<Vector3> vs, List<Vector3> ns)
        {
            Vector3 ax = a1 - a0; float len = ax.magnitude; Vector3 axN = len > 1e-6f ? ax / len : Vector3.up;
            Vector3 p1 = Vector3.Cross(axN, Mathf.Abs(Vector3.Dot(axN, Vector3.up)) < 0.9f ? Vector3.up : Vector3.right).normalized, p2 = Vector3.Cross(axN, p1);
            var outs = new List<float>();
            System.Action<Vector3> sample = s =>
            {
                int bi = -1; float bd = float.MaxValue;
                for (int i = 0; i < vs.Count; i++) { float d = (vs[i] - s).sqrMagnitude; if (d < bd) { bd = d; bi = i; } }
                if (bi >= 0) outs.Add(Mathf.Max(0f, Vector3.Dot(s - vs[bi], ns[bi])));
            };
            for (int i = 0; i <= 3; i++)
                for (int a = 0; a < 10; a++)
                {
                    float ang = a * Mathf.PI * 2f / 10f;
                    sample(a0 + ax * (i / 3f) + (p1 * Mathf.Cos(ang) + p2 * Mathf.Sin(ang)) * r);
                }
            sample(a0 - axN * r); sample(a1 + axN * r);
            return outs.Count > 0 ? Percentile(outs, 0.95f) : 0f;
        }
    }
}
