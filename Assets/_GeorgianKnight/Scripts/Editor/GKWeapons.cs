using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Fits the Meshy sword into the knight's right hand and hangs the scabbard on the left hip.
    /// Geometry is analysed (principal axes, cross-guard, handle) so no hand-tuned offsets are needed.
    /// </summary>
    public static class GKWeapons
    {
        public const float SwordLength = 0.98f;     // overall length of a 12th c. arming sword
        public const float ScabbardLength = 0.846f;    // the Blender scabbard (throat to finial) at its true size: no rescale
        /// <summary>A wooden core under leather, over a 75 cm blade: 2.6 cm thick at the mouth (the Blender model is 1.4 cm
        /// thick and read as a thin rod from the side).</summary>
        public const float ScabbardThickness = 0.026f;

        public struct Shape
        {
            public Vector3 axis;        // unit, local: from guard/throat toward tip
            public Vector3 wide;        // unit, local: cross-guard / scabbard width axis
            public Vector3 grip;        // local point (sword): middle of the handle
            public Vector3 guard;       // local point: centre of the cross-guard / scabbard throat
            public Vector3 tip;         // local point: blade tip / chape
            public float length;        // local units
        }

        public static List<Vector3> CollectVertices(GameObject root)
        {
            var pts = new List<Vector3>();
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var m = mf.sharedMesh;
                if (m == null) continue;
                var vs = m.vertices;
                int step = Mathf.Max(1, vs.Length / 60000);
                for (int i = 0; i < vs.Length; i += step)
                    pts.Add(root.transform.InverseTransformPoint(mf.transform.TransformPoint(vs[i])));
            }
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var m = smr.sharedMesh;
                if (m == null) continue;
                var vs = m.vertices;
                int step = Mathf.Max(1, vs.Length / 60000);
                for (int i = 0; i < vs.Length; i += step)
                    pts.Add(root.transform.InverseTransformPoint(smr.transform.TransformPoint(vs[i])));
            }
            return pts;
        }

        static void Pca(List<Vector3> pts, out Vector3 mean, out Vector3 e1, out Vector3 e2)
        {
            mean = Vector3.zero;
            foreach (var p in pts) mean += p;
            mean /= Mathf.Max(1, pts.Count);
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var p0 in pts)
            {
                Vector3 p = p0 - mean;
                xx += p.x * p.x; xy += p.x * p.y; xz += p.x * p.z; yy += p.y * p.y; yz += p.y * p.z; zz += p.z * p.z;
            }
            System.Func<Vector3, Vector3> mul = v => new Vector3(
                (float)(xx * v.x + xy * v.y + xz * v.z),
                (float)(xy * v.x + yy * v.y + yz * v.z),
                (float)(xz * v.x + yz * v.y + zz * v.z));
            e1 = new Vector3(0.3f, 0.9f, 0.2f).normalized;
            for (int i = 0; i < 80; i++) e1 = mul(e1).normalized;
            e2 = Vector3.Cross(e1, Mathf.Abs(e1.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            for (int i = 0; i < 80; i++)
            {
                Vector3 v = mul(e2);
                v -= Vector3.Dot(v, e1) * e1;
                e2 = v.normalized;
            }
        }

        public static Shape Analyse(GameObject root, bool isScabbard)
        {
            var pts = CollectVertices(root);
            Vector3 mean, a, u;
            Pca(pts, out mean, out a, out u);
            Vector3 w = Vector3.Cross(a, u).normalized;
            float tmin = float.MaxValue, tmax = float.MinValue;
            foreach (var p in pts) { float t = Vector3.Dot(p - mean, a); tmin = Mathf.Min(tmin, t); tmax = Mathf.Max(tmax, t); }
            const int N = 90;
            var umin = new float[N]; var umax = new float[N]; var wmin = new float[N]; var wmax = new float[N];
            var usum = new float[N]; var wsum = new float[N]; var cnt = new int[N];
            for (int i = 0; i < N; i++) { umin[i] = wmin[i] = float.MaxValue; umax[i] = wmax[i] = float.MinValue; }
            float len = Mathf.Max(1e-5f, tmax - tmin);
            foreach (var p0 in pts)
            {
                Vector3 p = p0 - mean;
                int b = Mathf.Clamp((int)((Vector3.Dot(p, a) - tmin) / len * N), 0, N - 1);
                float pu = Vector3.Dot(p, u), pw = Vector3.Dot(p, w);
                umin[b] = Mathf.Min(umin[b], pu); umax[b] = Mathf.Max(umax[b], pu);
                wmin[b] = Mathf.Min(wmin[b], pw); wmax[b] = Mathf.Max(wmax[b], pw);
                usum[b] += pu; wsum[b] += pw; cnt[b]++;
            }
            System.Func<int, float> width = i => cnt[i] > 0 ? umax[i] - umin[i] : 0f;
            System.Func<int, float> area = i => cnt[i] > 0 ? (umax[i] - umin[i]) * (wmax[i] - wmin[i]) : 0f;
            System.Func<int, float> tAt = i => tmin + (i + 0.5f) * len / N;

            var s = new Shape();
            s.length = len;
            if (!isScabbard)
            {
                int guard = 2;
                for (int i = 2; i < N - 2; i++) if (width(i) > width(guard)) guard = i;
                bool handleAtMin = guard < N / 2;
                int pommel = handleAtMin ? 0 : N - 1;
                float tGuard = tAt(guard);
                float tPommel = handleAtMin ? tmin : tmax;
                float tTip = handleAtMin ? tmax : tmin;
                // handle centreline = average of the slices between guard and pommel
                float uc = 0f, wc = 0f; int n = 0;
                int from = Mathf.Min(guard, pommel), to = Mathf.Max(guard, pommel);
                for (int i = from + 2; i <= to - 2; i++) if (cnt[i] > 0) { uc += (umin[i] + umax[i]) * 0.5f; wc += (wmin[i] + wmax[i]) * 0.5f; n++; }
                if (n > 0) { uc /= n; wc /= n; }
                Vector3 off = u * uc + w * wc;
                s.axis = handleAtMin ? a : -a;
                s.wide = u;
                s.guard = mean + a * tGuard + off;
                s.grip = mean + a * Mathf.Lerp(tGuard, tPommel, 0.42f) + off;
                s.tip = mean + a * tTip + off;
            }
            else
            {
                int k = Mathf.Max(2, N / 10);
                float aMin = 0f, aMax = 0f;
                for (int i = 0; i < k; i++) { aMin += area(i); aMax += area(N - 1 - i); }
                bool throatAtMin = aMin >= aMax;
                int ti = throatAtMin ? 1 : N - 2;
                Vector3 off = u * ((umin[ti] + umax[ti]) * 0.5f) + w * ((wmin[ti] + wmax[ti]) * 0.5f);
                s.axis = throatAtMin ? a : -a;
                s.wide = u;
                s.guard = mean + a * (throatAtMin ? tmin : tmax) + off;
                s.tip = mean + a * (throatAtMin ? tmax : tmin) + off;
                s.grip = s.guard;
            }
            return s;
        }

        static GameObject Spawn(string path, Material mat, string name)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (model == null) return null;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
            go.name = name;
            // every submesh: a baked asset exported with several material slots must not keep importer defaults
            if (mat != null)
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                {
                    var mats = new Material[Mathf.Max(1, r.sharedMaterials.Length)];
                    for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                    r.sharedMaterials = mats;
                }
            return go;
        }

        /// <summary>The scabbard's thickness at the mouth (its first 8 cm), across its width, in the pivot's frame.</summary>
        static float Thickness(GameObject scab, Transform pivot)
        {
            float dMin = float.MaxValue; var pts = new System.Collections.Generic.List<Vector3>();
            foreach (var mf in scab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                foreach (var v in mf.sharedMesh.vertices) { var l = pivot.InverseTransformPoint(mf.transform.TransformPoint(v)); pts.Add(l); dMin = Mathf.Min(dMin, l.z); }
            }
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var l in pts) if (l.z < dMin + 0.08f) { lo = Mathf.Min(lo, l.x); hi = Mathf.Max(hi, l.x); }
            return hi > lo ? hi - lo : 0f;
        }

        /// <summary>Adds Sword (with BladeBase / BladeTip points) and Scabbard to a knight instance in its bind pose.</summary>
        public static void Attach(GameObject knight)
        {
            var map = GKCharacters.MapBones(knight.transform);
            Transform hand, hips;
            if (!map.TryGetValue("RightHand", out hand) || !map.TryGetValue("Hips", out hips)) { GKBridge.Log("Attach: bones missing"); return; }

            // ---------- hand frame measured in the avatar's T-pose (the frame retargeting animates)
            var clone = Object.Instantiate(knight);
            Vector3 gripLocal, bladeLocalDir, guardLocalDir;
            try
            {
                var cmap = GKCharacters.MapBones(clone.transform);
                GKCharacters.EnforceTPose(cmap, clone.transform);
                Vector3 right, forward;
                GKCharacters.CharacterFrame(cmap, out right, out forward);
                var ch = cmap["RightHand"];
                Transform end = null, knuckle = null;
                for (int i = 0; i < ch.childCount; i++)
                {
                    var c = ch.GetChild(i);
                    string cn = c.name.ToLowerInvariant();
                    if (cn.Contains("middle1") || cn.EndsWith("middle")) knuckle = c;
                    if (end == null || cn.Contains("middle")) end = c;
                }
                Vector3 F = right, T = forward, N = Vector3.down;
                Vector3 gripWorld;
                if (knuckle != null)
                {
                    // real finger bones: the handle sits just past the knuckle line, under the palm
                    gripWorld = ch.position + (knuckle.position - ch.position) * 0.92f + N * 0.03f;
                }
                else
                {
                    float handLen = end != null ? Vector3.Distance(end.position, ch.position) : 0.18f;
                    if (handLen < 0.06f || handLen > 0.3f) handLen = 0.18f;
                    gripWorld = ch.position + F * handLen * 0.40f + N * 0.028f;
                }
                Vector3 B = (T + 0.32f * F).normalized;
                Vector3 G = (F - Vector3.Dot(F, B) * B).normalized;
                gripLocal = ch.InverseTransformPoint(gripWorld);
                bladeLocalDir = ch.InverseTransformDirection(B).normalized;
                guardLocalDir = ch.InverseTransformDirection(G).normalized;
            }
            finally { Object.DestroyImmediate(clone); }

            // ---------- sword
            var old = hand.Find("Sword"); if (old != null) Object.DestroyImmediate(old.gameObject);
            var sword = Spawn(GKPaths.SwordFbx, GKCharacters.SwordMaterial, "Sword");
            if (sword != null)
            {
                var sh = Analyse(sword, false);
                float k = SwordLength / sh.length;
                float hs = hand.lossyScale.x;
                Quaternion R = Quaternion.LookRotation(bladeLocalDir, guardLocalDir) * Quaternion.Inverse(Quaternion.LookRotation(sh.axis, sh.wide));
                sword.transform.SetParent(hand, false);
                sword.transform.localScale = Vector3.one * (k / hs);
                sword.transform.localRotation = R;
                sword.transform.localPosition = gripLocal - R * (sh.grip * (k / hs));
                var bb = new GameObject("BladeBase").transform;
                bb.SetParent(sword.transform, false);
                bb.localPosition = sh.guard + sh.axis * (0.07f / k);
                var bt = new GameObject("BladeTip").transform;
                bt.SetParent(sword.transform, false);
                bt.localPosition = sh.tip - sh.axis * (0.01f / k);
                var gp = new GameObject("Grip").transform;
                gp.SetParent(sword.transform, false);
                gp.localPosition = sh.grip;
                // reference frame for sheathing: at the guard, +Z along the blade, +Y along the blade width
                var sr = new GameObject("SheathRef").transform;
                sr.SetParent(sword.transform, false);
                sr.localPosition = sh.guard;
                sr.localRotation = Quaternion.LookRotation(sh.axis, sh.wide);
                foreach (var r in sword.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }

            // ---------- scabbard on the left hip, tip down and back
            var oldS = hips.Find("Scabbard"); if (oldS != null) Object.DestroyImmediate(oldS.gameObject);
            var oldP = hips.Find("ScabbardPivot"); if (oldP != null) Object.DestroyImmediate(oldP.gameObject);
            var scab = Spawn(GKPaths.ScabbardFbx, GKCharacters.ScabbardMaterial, "Scabbard");
            if (scab != null)
            {
                var sh = Analyse(scab, true);
                float k = ScabbardLength / sh.length;
                Vector3 right, forward;
                GKCharacters.CharacterFrame(map, out right, out forward);
                Vector3 left = -right, up = Vector3.up;
                Vector3 throat = hips.position + left * 0.18f + forward * 0.06f - up * 0.04f;
                Vector3 D = (-up * 0.80f - forward * 0.55f + left * 0.16f).normalized;
                // a sword belt's hang: two belt points carry it ~20 deg back from vertical, along the thigh and only a
                // little out from it, ~24 deg back and 9 out (30 back read as a stick, 45 as a tail): the tip rides at mid-calf, ~25 cm up
                D = (-up * 0.905f - forward * 0.40f + left * 0.145f).normalized;
                // keep the hilt exactly where the draw / sheathe animations put the hand (GKCombatBuilder fit),
                // and derive the throat from it along the hang direction
                string fitPath = GKPaths.Generated + "/holster_fit.json";
                float gripAbove = 0.11f;
                var swordNow = hand.Find("Sword");
                if (swordNow != null)
                {
                    Transform gpT = swordNow.Find("Grip"), srT = swordNow.Find("SheathRef");
                    if (gpT != null && srT != null) gripAbove = Vector3.Distance(gpT.position, srT.position) + 0.012f;
                }
                if (System.IO.File.Exists(fitPath))
                {
                    var fit = JsonUtility.FromJson<GKCombatBuilder.HolsterFit>(System.IO.File.ReadAllText(fitPath));
                    Vector3 gripW = hips.TransformPoint(fit.grip);
                    Vector3 t2 = gripW + D * gripAbove;
                    float lateral = Vector3.Dot(t2 - hips.position, left);
                    if (lateral > 0.08f && lateral < 0.35f) throat = t2;
                    else GKBridge.Log("scabbard: fitted grip rejected (lateral " + lateral.ToString("F2") + ") — using the default throat");
                }
                Vector3 W = Vector3.Cross(left, D).normalized;
                Quaternion R = Quaternion.LookRotation(D, W) * Quaternion.Inverse(Quaternion.LookRotation(sh.axis, sh.wide));
                scab.transform.localScale = Vector3.one * k;
                scab.transform.rotation = R;
                scab.transform.position = throat - R * (sh.guard * k);
                // pivot at the throat, +Z along the hang direction: ScabbardMotion swings it and keeps it off the leg
                var pivot = new GameObject("ScabbardPivot").transform;
                pivot.SetPositionAndRotation(throat, Quaternion.LookRotation(D, W));
                pivot.SetParent(hips, true);
                scab.transform.SetParent(pivot, true);
                // thickness to a real scabbard's: a fit node in the pivot's frame (z along the hang, y across the width,
                // x through the thickness) scales x only; the scabbard keeps its pose relative to the pivot
                float thick = Thickness(scab, pivot);
                if (thick > 0.002f && Mathf.Abs(thick - ScabbardThickness) > 0.002f)
                {
                    var fitT = new GameObject("ScabbardFit").transform;
                    fitT.SetParent(pivot, false);
                    fitT.localScale = new Vector3(ScabbardThickness / thick, 1f, 1f);
                    scab.transform.SetParent(fitT, false);
                }
                var motion = knight.GetComponent<ScabbardMotion>();
                if (motion == null) motion = knight.AddComponent<ScabbardMotion>();
                motion.hips = hips;
                motion.scabbard = pivot;
                motion.length = ScabbardLength;
            }
        }
    }
}
