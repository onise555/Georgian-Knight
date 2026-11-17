using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Blade ribbon: a fading mesh swept between the blade base and tip while the swing is announced.
    /// Built in world space every frame with Catmull-Rom subdivision so fast swings stay smooth.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class WeaponTrail : MonoBehaviour
    {
        public Transform bladeBase, bladeTip;
        public Combatant owner;
        [Tooltip("Which blade this trail follows: it draws only for a swing that cuts with that hand (two swords).")]
        public WeaponHand hand = WeaponHand.Main;
        public float lifetime = 0.13f;
        public int subdivisions = 4;
        public Color color = new Color(0.86f, 0.92f, 1f, 0.38f);
        [Tooltip("Where along the blade the ribbon begins (0 = hilt, 1 = tip): the outer part only, where the edge moves fastest.")]
        [Range(0f, 0.9f)] public float from = 0.55f;

        struct Sample { public Vector3 a, b; public float t; }
        // the ribbon's longest life (heavy, ~0.2 s) at a high frame rate stays well under this; the lists are sized for it
        // at start, so the first swings of a fight do not grow them (GK-26)
        const int MaxSamples = 64;
        readonly List<Sample> samples = new List<Sample>(MaxSamples);
        Mesh mesh;
        bool emitting;
        bool built;     // the mesh holds a ribbon (an idle trail does not clear an empty mesh every frame)
        readonly List<Vector3> v = new List<Vector3>();
        readonly List<Color> c = new List<Color>();
        readonly List<Vector2> uv = new List<Vector2>();
        readonly List<int> tri = new List<int>();

#if UNITY_EDITOR
        /// <summary>
        /// Added in the editor (by hand or by a builder's AddComponent): the renderer gets Feel's trail material, so no
        /// prefab or scene carries an empty material slot (QA-0 PREFAB.MISSING_MATERIAL). Start still re-applies it.
        /// </summary>
        void Reset()
        {
            var r = GetComponent<MeshRenderer>();
            var look = Feel.FeelSettings.Active != null ? Feel.FeelSettings.Active.trail : null;
            if (r != null && r.sharedMaterial == null && look != null && look.material != null) r.sharedMaterial = look.material;
        }
#endif

        void Start()
        {
            int pairs = (MaxSamples - 1) * Mathf.Max(1, subdivisions) + 1;
            v.Capacity = c.Capacity = uv.Capacity = pairs * 2;
            tri.Capacity = (pairs - 1) * 6;
            mesh = new Mesh { name = "BladeTrail" };
            mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = GetComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            // the look is tuned in the one feel asset (Feel_Default.trail); the built-in ribbon stays the fallback
            var look = Feel.FeelSettings.Active.trail;
            if (look.material != null) r.sharedMaterial = look.material;
            if (r.sharedMaterial == null)
            {
                var m = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
                m.SetFloat("_Surface", 1f); m.SetFloat("_Blend", 2f);
                m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
                m.SetInt("_ZWrite", 0); m.SetFloat("_Cull", 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = 3100;
                m.SetTexture("_BaseMap", Streak());
                m.SetColor("_BaseColor", new Color(1.15f, 1.15f, 1.2f, 1f));  // a soft glint of the edge, not a laser line
                r.sharedMaterial = m;
            }
            if (owner != null)
            {
                owner.SwingStarted += a => { emitting = Cuts(a); if (emitting) Dress(a); };
                owner.SwingEnded += () => emitting = false;
                owner.StateChanged += s => { if (s != Combatant.State.Attacking && s != Combatant.State.Executing) emitting = false; };
            }
        }

        /// <summary>Colour, length and width of the ribbon for this swing, from Feel_Default.trail.</summary>
        void Dress(AttackData a)
        {
            var t = Feel.FeelSettings.Active.trail;
            bool heavy = a != null && a.kind != AttackKind.Light && a.kind != AttackKind.Riposte;
            bool special = a != null && (a.kind == AttackKind.Execution || a.kind == AttackKind.Charged || a.kind == AttackKind.Riposte);
            color = t.ColorFor(heavy, special, owner.team == Team.Player);
            lifetime = Mathf.Max(0.02f, heavy ? t.lifetime.y : t.lifetime.x);
            from = Mathf.Clamp(heavy ? t.from.y : t.from.x, 0f, 0.9f);
        }

        bool Cuts(AttackData a)
        {
            if (a == null) return hand == WeaponHand.Main;
            if (a.shieldBash || a.kind == AttackKind.Kick) return false;   // the shield's face or a boot, not an edge
            WeaponHand h = a.hand;
            return h == WeaponHand.Both || h == hand;
        }

        void LateUpdate()
        {
            transform.position = Vector3.zero;
            transform.rotation = Quaternion.identity;
            transform.localScale = Vector3.one;
            float now = Time.time;
            if (emitting && bladeBase != null && bladeTip != null)
            {
                if (samples.Count >= MaxSamples) samples.RemoveAt(0);
                samples.Add(new Sample { a = bladeBase.position, b = bladeTip.position, t = now });
            }
            // samples are in time order, so the expired ones are a prefix; dropped in place (GK-26: a RemoveAll lambda
            // captured `now` and allocated a closure and a delegate per trail every frame)
            int expired = 0;
            while (expired < samples.Count && now - samples[expired].t > lifetime) expired++;
            if (expired > 0) samples.RemoveRange(0, expired);
            Rebuild(now);
        }

        // u: age along the trail (0 = at the blade), v: across it (0 = inner end, 1 = the tip's path). A thin bright line
        // along the tip's path, a soft body streaked along the motion, both fading with age
        static Texture2D streak;
        static Texture2D Streak()
        {
            if (streak != null) return streak;
            const int W = 128, H = 64;
            streak = new Texture2D(W, H, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float u = x / (W - 1f), vv = y / (H - 1f);
                    float edge = 0.7f * Mathf.Exp(-Mathf.Pow((1f - vv) * 5f, 2f)) * Mathf.Pow(1f - u, 0.9f);
                    float lines = 0.55f + 0.45f * Mathf.PerlinNoise(vv * 22f, u * 2.5f);
                    float body = Mathf.Pow(vv, 2.2f) * Mathf.Pow(1f - u, 1.6f) * 0.45f * lines;
                    float a = Mathf.Clamp01(edge + body);
                    px[y * W + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            streak.SetPixels32(px);
            streak.Apply(true, true);
            return streak;
        }

        static Vector3 CR(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t * t + (-p0 + 3f * p1 - 3f * p2 + p3) * t * t * t);
        }

        void Rebuild(float now)
        {
            if (samples.Count < 2)
            {
                if (built) { mesh.Clear(); built = false; }
                return;
            }
            mesh.Clear();
            built = true;
            v.Clear(); c.Clear(); uv.Clear(); tri.Clear();
            int n = samples.Count;
            for (int i = 0; i < n - 1; i++)
            {
                var s0 = samples[Mathf.Max(0, i - 1)]; var s1 = samples[i]; var s2 = samples[i + 1]; var s3 = samples[Mathf.Min(n - 1, i + 2)];
                for (int k = 0; k < subdivisions; k++)
                {
                    float t = k / (float)subdivisions;
                    Vector3 a = CR(s0.a, s1.a, s2.a, s3.a, t);
                    Vector3 b = CR(s0.b, s1.b, s2.b, s3.b, t);
                    float age = Mathf.Lerp(now - s1.t, now - s2.t, t) / lifetime;
                    AddPair(a, b, age);
                }
            }
            AddPair(samples[n - 1].a, samples[n - 1].b, (now - samples[n - 1].t) / lifetime);
            for (int i = 0; i + 3 < v.Count; i += 2)
            {
                tri.Add(i); tri.Add(i + 1); tri.Add(i + 2);
                tri.Add(i + 1); tri.Add(i + 3); tri.Add(i + 2);
            }
            mesh.SetVertices(v); mesh.SetColors(c); mesh.SetUVs(0, uv); mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
        }

        void AddPair(Vector3 a, Vector3 b, float age)
        {
            float alpha = Mathf.Clamp01(1f - age);
            alpha *= alpha;
            Color ca = color; ca.a *= alpha * 0.15f;
            Color cb = color; cb.a *= alpha;
            v.Add(Vector3.Lerp(a, b, from)); c.Add(ca); uv.Add(new Vector2(age, 0f));
            v.Add(b); c.Add(cb); uv.Add(new Vector2(age, 1f));
        }
    }
}
