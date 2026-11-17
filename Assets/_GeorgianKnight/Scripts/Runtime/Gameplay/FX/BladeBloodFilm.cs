using UnityEngine;
using UnityEngine.Rendering;

namespace GeorgianKnight
{
    /// <summary>
    /// Blood on a sword as a film that lives on the steel (QUALITY 3b, blood on the blade; design in the notes,
    /// feel-blade-blood-plan.md). Each cut lays a new, seeded splotch where the edge went in, on the side that went in.
    /// A small grid in blade space (along x across x the two flats) holds the film's thickness and age; at 30 Hz the
    /// part of gravity along the blade moves it downstream (to the tip when the tip is low, to the guard when raised),
    /// leaving a thinner film behind; where it gathers at the tip or the guard a drop forms and falls (a real particle
    /// that lands and marks the ground). It dries from the thin edges in, ~20 s wet, dry by ~90 s, and is wiped when the
    /// sword is sheathed. Drawn with Graphics.RenderMesh: the sword's own mesh again, at the sword's own matrix, with the
    /// film material (shader GK/Feel/BladeBlood) in the transparent queue, so the steel's material is untouched and any
    /// sword works. (An injected render pass issued its draws but nothing reached the screen, D25.) Owned by Game Feel &
    /// VFX; HitFX feeds it.
    /// </summary>
    public sealed class BladeBloodFilm
    {
        public const int Along = 128, Across = 16;   // ~6 mm along, ~1 mm across on the arming sword
        const float Tick = 1f / 30f;
        const float MaxThickness = 2f;          // mm, = 1.0 in the texture
        const float FlowMin = 0.08f;             // mm: thinner than this a film clings and does not run (a run's trail)
        const float DropAt = 1.25f;              // mm gathered at an end before a drop lets go
        const float WetTime = 20f, DryTime = 90f;
        const float FlowSpeed = 0.02f;           // m/s a film above FlowMin runs down a vertical blade (it ran 0.37 m/s)

        readonly WeaponHitbox blade;
        readonly Renderer steel;
        readonly int[] submeshes;
        readonly Mesh mesh;
        RenderParams rp;
        readonly float[] thick = new float[Along * Across * 2];
        readonly float[] age = new float[Along * Across * 2];
        readonly float[] next = new float[Along * Across * 2];
        readonly Texture2D tex;
        readonly Color32[] texels = new Color32[Along * Across * 2];
        readonly Material mat;
        readonly System.Action<Vector3, Vector3, float> drop;
        Vector3 axisL, sideL, flatL;           // blade frame in the renderer's local space
        Vector3 centreL;                       // the blade's centre line off the base -> tip line (local)
        float halfWidth;
        float tickAcc;
        bool any;
        int drawnFrames;

        public bool Wet { get { return any; } }
        public Renderer Steel { get { return steel; } }
        public Material Material { get { return mat; } }

        static readonly int FilmId = Shader.PropertyToID("_Film"), BaseId = Shader.PropertyToID("_BladeBase"),
                            AxisId = Shader.PropertyToID("_BladeAxis"), SideId = Shader.PropertyToID("_BladeSide"),
                            FlatId = Shader.PropertyToID("_BladeFlat");

        public static BladeBloodFilm Create(WeaponHitbox w, Material template, System.Action<Vector3, Vector3, float> drop)
        {
            if (w == null || w.bladeBase == null || w.bladeTip == null || template == null) return null;
            var r = FindSteel(w);
            if (r == null) return null;
            return new BladeBloodFilm(w, r, template, drop);
        }

        /// <summary>The renderer whose bounds hold the blade's middle (the sword, not the hand or the scabbard).</summary>
        static Renderer FindSteel(WeaponHitbox w)
        {
            Vector3 mid = Vector3.Lerp(w.bladeBase.position, w.bladeTip.position, 0.5f);
            Transform root = w.bladeTip.parent != null ? w.bladeTip.parent : w.transform;
            Renderer best = null; float bestVol = float.MaxValue;
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!r.bounds.Contains(mid)) continue;
                var s = r.bounds.size; float vol = s.x * s.y * s.z;
                if (vol < bestVol) { bestVol = vol; best = r; }
            }
            return best;
        }

        BladeBloodFilm(WeaponHitbox w, Renderer r, Material template, System.Action<Vector3, Vector3, float> drop)
        {
            blade = w; steel = r; this.drop = drop;
            var mf = r.GetComponent<MeshFilter>();
            mesh = mf != null ? mf.sharedMesh : null;
            int subs = mesh != null ? mesh.subMeshCount : 1;
            submeshes = new int[subs];
            for (int i = 0; i < subs; i++) submeshes[i] = i;
            // the blade's frame in the renderer's space: the axis from the blade's ends, the width as the wider of the
            // two remaining mesh extents, the flat normal as the thinner one
            var t = r.transform;
            axisL = t.InverseTransformDirection(w.bladeTip.position - w.bladeBase.position).normalized;
            Vector3 ext = mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds.extents : Vector3.one * 0.02f;
            Vector3 a = PerpAxis(axisL, 0), b = PerpAxis(axisL, 1);
            float ea = Extent(ext, a), eb = Extent(ext, b);
            // the mesh bounds include the guard and the pommel, so their extents cannot tell the blade's width from its
            // thickness: read it from the blade itself (the vertices in its middle third), falling back to the bounds
            if (!BladeCrossSection(mf != null ? mf.sharedMesh : null, a, b, out sideL, out flatL, out halfWidth, out centreL))
            {
                sideL = ea >= eb ? a : b; flatL = ea >= eb ? b : a;
                halfWidth = Mathf.Max(ea, eb);
            }
            halfWidth = Mathf.Max(0.008f, halfWidth * Mathf.Abs(t.lossyScale.x));
            // RGBA32 (8 bits a channel is plenty for 0-2 mm and 0-90 s; an RGHalf upload read back as zeros on the GPU, D25)
            tex = new Texture2D(Along, Across * 2, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "BladeFilm" };
            mat = new Material(template) { name = "BladeBlood (" + w.name + ")" };
            mat.SetTexture(FilmId, tex);
            rp = new RenderParams(mat)
            {
                layer = r.gameObject.layer, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = true,
                renderingLayerMask = r.renderingLayerMask, worldBounds = r.bounds
            };
        }

        /// <summary>
        /// The blade's cross-section from its own vertices between the blade's base and tip (the guard and the grip lie
        /// outside that span; the arming sword's blade is long quads, so there may be vertices only near its two ends).
        /// The width is the direction of their widest spread around the base -> tip line, searched over half a turn in
        /// 5 degree steps; the centre is the middle of that spread and of the spread across it (the hitbox's base and
        /// tip need not lie on the blade's centre line); the flat normal is square to the axis and the width.
        /// </summary>
        bool BladeCrossSection(Mesh m, Vector3 a, Vector3 b, out Vector3 side, out Vector3 flat, out float half, out Vector3 centre)
        {
            side = a; flat = b; half = 0f; centre = Vector3.zero;
            if (m == null || !m.isReadable) return false;
            var vs = m.vertices;
            var t = steel.transform;
            Vector3 b0 = t.InverseTransformPoint(blade.bladeBase.position), b1 = t.InverseTransformPoint(blade.bladeTip.position);
            Vector3 ax = b1 - b0; float len2 = ax.sqrMagnitude;
            if (len2 < 1e-8f) return false;
            var off = new System.Collections.Generic.List<Vector3>();
            for (int i = 0; i < vs.Length; i++)
            {
                float u = Vector3.Dot(vs[i] - b0, ax) / len2;
                if (u < 0f || u > 1f) continue;
                off.Add(vs[i] - (b0 + ax * u));
            }
            if (off.Count < 4) return false;
            float best = -1f, bestMid = 0f; Vector3 bestDir = a;
            for (int k = 0; k < 36; k++)
            {
                float ang = k * 5f * Mathf.Deg2Rad;
                Vector3 dir = a * Mathf.Cos(ang) + b * Mathf.Sin(ang);
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = 0; i < off.Count; i++) { float d = Vector3.Dot(off[i], dir); lo = Mathf.Min(lo, d); hi = Mathf.Max(hi, d); }
                if (hi - lo > best) { best = hi - lo; bestDir = dir; bestMid = 0.5f * (hi + lo); }
            }
            side = bestDir.normalized;
            flat = Vector3.Cross(ax.normalized, side).normalized;
            float flo = float.MaxValue, fhi = float.MinValue;
            for (int i = 0; i < off.Count; i++) { float d = Vector3.Dot(off[i], flat); flo = Mathf.Min(flo, d); fhi = Mathf.Max(fhi, d); }
            half = 0.5f * best;
            centre = side * bestMid + flat * (0.5f * (flo + fhi));
            return true;
        }

        static Vector3 PerpAxis(Vector3 axis, int which)
        {
            Vector3 k = Mathf.Abs(axis.x) < 0.9f ? Vector3.right : Vector3.up;
            Vector3 p = Vector3.Cross(axis, k).normalized;
            return which == 0 ? p : Vector3.Cross(axis, p).normalized;
        }

        static float Extent(Vector3 ext, Vector3 dir) { return Mathf.Abs(dir.x) * ext.x + Mathf.Abs(dir.y) * ext.y + Mathf.Abs(dir.z) * ext.z; }

        int Index(int face, int i, int j) { return (face * Across + j) * Along + i; }

        /// <summary>A seeded xorshift generator on the stack: a cut's pattern without a System.Random (and its 56-int
        /// table) allocated per hit (GK-26).</summary>
        struct FilmRng
        {
            uint s;
            public FilmRng(int seed)
            {
                // spread the seed (a cut's seed may be small or 0; xorshift must not start at 0)
                uint x = (uint)seed * 0x9E3779B9u + 0x7F4A7C15u;
                x ^= x >> 16; x *= 0x85EBCA6Bu; x ^= x >> 13;
                s = x != 0u ? x : 0x6D2B79F5u;
            }
            uint NextU() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s; }
            /// <summary>0 .. 1, never 1.</summary>
            public double NextDouble() { return (NextU() >> 8) * (1.0 / 16777216.0); }
            /// <summary>0 .. max - 1.</summary>
            public int Next(int max) { return (int)(NextDouble() * max); }
        }

        /// <summary>A cut: a new splotch around the contact, on the flat that faced the struck body.</summary>
        public void Cut(Vector3 contact, Vector3 towardTarget, float amount, int seed)
        {
            Vector3 a0 = blade.bladeBase.position, a1 = blade.bladeTip.position;
            Vector3 ax = a1 - a0; float len = ax.magnitude;
            if (len < 1e-3f) return;
            float uc = Mathf.Clamp01(Vector3.Dot(contact - a0, ax) / (len * len));
            Vector3 flatW = steel.transform.TransformDirection(flatL);
            int face = Vector3.Dot(flatW, towardTarget) >= 0f ? 0 : 1;
            var rnd = new FilmRng(seed);
            // narrow streaks smeared along the blade (the cut drags the blood outward), each in its own lane across the
            // width, broken up cell by cell so no two edges are straight; never a sheet over the whole width
            int streaks = 4 + rnd.Next(5);
            for (int b = 0; b < streaks; b++)
            {
                float cu = uc + (float)(rnd.NextDouble() * 0.2 - 0.05);
                float cv = (float)(rnd.NextDouble() * 1.8 - 0.9);
                float ru = 0.02f + (float)rnd.NextDouble() * 0.07f, rv = 0.06f + (float)rnd.NextDouble() * 0.2f;
                float depth = amount * (0.3f + (float)rnd.NextDouble() * 0.9f);
                Splat(face, cu, cv, ru, rv, depth, ref rnd);
            }
            // spatter: a few small round drops around the cut, 2-4 mm across in the world (a drop under one cell
            // along still takes that cell)
            int dots = 5 + rnd.Next(9);
            for (int b = 0; b < dots; b++)
            {
                float cu = uc + (float)(rnd.NextDouble() * 0.36 - 0.18);
                float cv = (float)(rnd.NextDouble() * 1.9 - 0.95);
                float r = 0.001f + (float)rnd.NextDouble() * 0.001f;            // m, radius
                float ru = Mathf.Max(r / len, 0.6f / Along), rv = Mathf.Max(r / Mathf.Max(halfWidth, 1e-3f), 0.6f * 2f / Across);
                Splat(face, cu, cv, ru, rv, amount * (0.2f + (float)rnd.NextDouble() * 0.5f), ref rnd);
            }
            any = true;
        }

        /// <summary>One elliptical deposit in film space (u along 0..1, v across -1..1), with a ragged edge.</summary>
        void Splat(int face, float cu, float cv, float ru, float rv, float depth, ref FilmRng rnd)
        {
            for (int i = 0; i < Along; i++)
            {
                float du = ((i + 0.5f) / Along - cu) / ru;
                if (du * du > 4f) continue;
                for (int j = 0; j < Across; j++)
                {
                    float dv = (((j + 0.5f) / Across) * 2f - 1f - cv) / rv;
                    float f = Mathf.Exp(-(du * du + dv * dv)) * (0.55f + 0.9f * (float)rnd.NextDouble());
                    if (f < 0.12f) continue;
                    int k = Index(face, i, j);
                    thick[k] = Mathf.Min(MaxThickness, thick[k] + depth * f);
                    age[k] = Mathf.Min(age[k], 0f);          // fresh blood over old wets it again
                }
            }
        }

        /// <summary>Review only: ages the blood as if this many seconds had passed (a time-lapse to dry).</summary>
        public void AgeBy(float seconds)
        {
            for (int k = 0; k < age.Length; k++) if (thick[k] > 0f) age[k] += seconds * (thick[k] < 0.3f ? 1.6f : 1f);
            Upload();
        }

        /// <summary>Review: what the film holds now (cells with blood, the thickest, the steel it is drawn on).</summary>
        public string Describe()
        {
            int cells = 0, at = -1; float max = 0f, maxAge = 0f;
            for (int k = 0; k < thick.Length; k++) if (thick[k] > 0.01f) { cells++; if (thick[k] > max) { max = thick[k]; at = k; } maxAge = Mathf.Max(maxAge, age[k]); }
            string texel = at < 0 ? "-" : tex.GetPixel(at % Along, at / Along).r.ToString("F3");
            string gpu = at < 0 ? "-" : GpuTexel(at % Along, at / Along).ToString("F3");
            return "cells " + cells + ", max " + max.ToString("F2") + " mm, oldest " + maxAge.ToString("F0") + " s, steel " + (steel != null ? steel.name : "none") +
                   ", half width " + halfWidth.ToString("F3") + " (mesh readable " + (mesh != null && mesh.isReadable) + ")" + " m, wet " + any + ", drawn " + drawnFrames +
                   ", max on face " + (at < 0 ? "-" : at >= Along * Across ? "B" : "A") +
                   ", texel at max " + texel + " (GPU " + gpu + "), texture bound " + (mat.GetTexture(FilmId) == tex) + ", shader " + mat.shader.name;
        }

        /// <summary>Review: the film texture's red at one texel as the GPU holds it (blit to a target, read back).</summary>
        float GpuTexel(int x, int y)
        {
            var rt = RenderTexture.GetTemporary(Along, Across * 2, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var probe = new Texture2D(Along, Across * 2, TextureFormat.RGBA32, false, true);
            probe.ReadPixels(new Rect(0, 0, Along, Across * 2), 0, 0, false);
            probe.Apply(false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            float v = probe.GetPixel(x, y).r;
            Object.Destroy(probe);
            return v;
        }

        public void Wipe()
        {
            System.Array.Clear(thick, 0, thick.Length);
            System.Array.Clear(age, 0, age.Length);
            any = false;
            Upload();
        }

        /// <summary>Ticks the flow at 30 Hz; drops fall where it gathers. Call every frame.</summary>
        public void Update(float dt)
        {
            if (!any || steel == null) return;
            tickAcc += dt;
            int steps = 0;
            while (tickAcc >= Tick && steps < 3) { tickAcc -= Tick; Step(Tick); steps++; }
            if (steps > 0) Upload();
            var t = steel.transform;
            Vector3 b0 = blade.bladeBase.position, b1 = blade.bladeTip.position;
            Vector3 axisW = (b1 - b0); float len = axisW.magnitude; axisW /= Mathf.Max(1e-4f, len);
            mat.SetVector(BaseId, b0 + t.TransformVector(centreL));
            mat.SetVector(AxisId, new Vector4(axisW.x, axisW.y, axisW.z, len));
            Vector3 sideW = t.TransformDirection(sideL).normalized;
            mat.SetVector(SideId, new Vector4(sideW.x, sideW.y, sideW.z, halfWidth));
            Vector3 flatW = t.TransformDirection(flatL).normalized;
            mat.SetVector(FlatId, new Vector4(flatW.x, flatW.y, flatW.z, 0f));
            Draw();
        }

        void Draw()
        {
            if (!any || mesh == null || steel == null || !steel.enabled || !steel.gameObject.activeInHierarchy) return;
            rp.worldBounds = steel.bounds;
            Matrix4x4 m = steel.localToWorldMatrix;
            for (int i = 0; i < submeshes.Length; i++) Graphics.RenderMesh(rp, mesh, submeshes[i], m);
            drawnFrames++;
        }

        void Step(float dt)
        {
            Vector3 b0 = blade.bladeBase.position, b1 = blade.bladeTip.position;
            Vector3 axisW = (b1 - b0).normalized;
            float cellLen = Mathf.Max(1e-3f, Vector3.Distance(b0, b1) / Along);
            float keep = Mathf.Pow(0.97f, cellLen / 0.0125f);   // 3% lost per 1.25 cm run, whatever the cell size
            // how hard gravity pulls along the blade: + toward the tip, - toward the guard
            float g = Vector3.Dot(Vector3.down, axisW);
            Vector3 sideW = steel.transform.TransformDirection(sideL).normalized;
            float gs = Vector3.Dot(Vector3.down, sideW);         // across: toward one edge when the blade lies flat
            System.Array.Copy(thick, next, thick.Length);
            bool wet = false;
            for (int face = 0; face < 2; face++)
                for (int j = 0; j < Across; j++)
                    for (int i = 0; i < Along; i++)
                    {
                        int k = Index(face, i, j);
                        float t = thick[k];
                        if (t <= 0f) continue;
                        wet = true;
                        age[k] += dt * (t < 0.3f ? 1.6f : 1f);    // thin film dries first
                        if (age[k] > WetTime || t <= FlowMin) continue;
                        // the mobile part runs downstream; the rest stays as a clinging film
                        float mobile = (t - FlowMin) * Mathf.Clamp01(Mathf.Abs(g) * FlowSpeed * dt / cellLen);
                        int di = g > 0.05f ? 1 : g < -0.05f ? -1 : 0;
                        if (di != 0 && mobile > 0f)
                        {
                            int ni = i + di;
                            if (ni < 0 || ni >= Along) { Gather(face, i, j); continue; }   // it pools at the end
                            next[k] -= mobile;
                            // a run keeps its lane; only a blade lying on its flat tips it toward the lower edge
                            int nj = j;
                            if (Mathf.Abs(gs) > 0.4f) nj = Mathf.Clamp(j + (gs > 0 ? 1 : -1), 0, Across - 1);
                            next[Index(face, ni, nj)] += mobile * keep;   // a little is lost on the way (soaks and smears)
                        }
                    }
            System.Array.Copy(next, thick, thick.Length);
            any = wet;
            if (!wet) return;
            // dry blood far past its time flakes away
            for (int k = 0; k < thick.Length; k++) if (age[k] > DryTime + 40f) thick[k] = 0f;
        }

        /// <summary>Blood that reached the tip or the guard pools there and lets go as a drop past a size.</summary>
        void Gather(int face, int i, int j)
        {
            int k = Index(face, i, j);
            if (next[k] < DropAt || drop == null) return;
            Vector3 b0 = blade.bladeBase.position, b1 = blade.bladeTip.position;
            Vector3 at = i >= Along - 1 ? b1 : Vector3.Lerp(b0, b1, 0.04f);
            float radius = 0.0022f + 0.001f * next[k];
            drop(at, Vector3.down * 0.15f, radius);
            next[k] *= 0.3f;                 // most of the pool went with the drop; a bead stays
        }

        void Upload()
        {
            for (int k = 0; k < thick.Length; k++)
                texels[k] = new Color32((byte)Mathf.RoundToInt(Mathf.Clamp01(thick[k] / MaxThickness) * 255f),
                                        (byte)Mathf.RoundToInt(Mathf.Clamp01(age[k] / DryTime) * 255f), 0, 255);
            tex.SetPixels32(texels);
            tex.Apply(false, false);
        }

        public void Dispose()
        {
            if (tex != null) Object.Destroy(tex);
            if (mat != null) Object.Destroy(mat);
        }
    }
}
