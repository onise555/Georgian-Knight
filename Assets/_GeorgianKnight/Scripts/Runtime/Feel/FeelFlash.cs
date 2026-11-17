using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace GeorgianKnight.Feel
{
    /// <summary>
    /// A brief rim-lit flash over a struck body. The body is drawn a second time with an additive fresnel material by
    /// a render pass injected into each camera, so it works on any character shader (CC, URP Lit, custom) without
    /// touching its materials. Only the skinned, opaque parts flash: hair cards and other alpha-tested or transparent
    /// surfaces are skipped (their quads would light up), and so are rigid props like weapons.
    /// </summary>
    public sealed class FeelFlash
    {
        public const string ShaderName = "GK/Feel/HitFlash";
        const int MaxFlashes = 8;
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int IntensityId = Shader.PropertyToID("_Intensity");

        sealed class Part { public Renderer renderer; public int submesh; }

        sealed class Active
        {
            public GameObject target;
            public List<Part> parts;
            public Material material;
            public Color color;
            public float strength, left, length;
        }

        readonly Material template;
        readonly Active[] slots = new Active[MaxFlashes];
        readonly Dictionary<GameObject, List<Part>> partsCache = new Dictionary<GameObject, List<Part>>();
        readonly FlashPass pass;

        public bool Calm;
        /// <summary>Flashes drawn last frame (tests).</summary>
        public int Drawn { get { return pass.drawn; } }
        public int Running { get { int n = 0; for (int i = 0; i < MaxFlashes; i++) if (slots[i].left > 0f) n++; return n; } }

        public FeelFlash(Transform owner)
        {
            var mat = FeelSettings.Active.flashMaterial;
            if (mat == null)
            {
                var sh = Shader.Find(ShaderName);
                if (sh != null) mat = new Material(sh) { hideFlags = HideFlags.DontSave };
            }
            template = mat;
            for (int i = 0; i < MaxFlashes; i++)
            {
                slots[i] = new Active();
                if (template != null) slots[i].material = new Material(template) { hideFlags = HideFlags.DontSave, name = "HitFlash" + i };
            }
            pass = new FlashPass(this) { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing };
            RenderPipelineManager.beginCameraRendering += Enqueue;
            if (template == null) Debug.LogWarning("[GKFeel] no hit flash material (shader " + ShaderName + " missing): flashes are off");
        }

        public void Dispose()
        {
            RenderPipelineManager.beginCameraRendering -= Enqueue;
            for (int i = 0; i < MaxFlashes; i++) if (slots[i].material != null) Object.Destroy(slots[i].material);
        }

        public void Play(GameObject target, Color color, float strength, float seconds)
        {
            if (template == null || target == null || strength <= 0f || seconds <= 0f) return;
            if (Calm) strength *= 0.35f;
            var parts = PartsOf(target);
            if (parts.Count == 0) return;
            // the same body flashing again restarts its slot; otherwise take a free one, else the oldest
            Active slot = null;
            for (int i = 0; i < MaxFlashes && slot == null; i++) if (slots[i].target == target && slots[i].left > 0f) slot = slots[i];
            for (int i = 0; i < MaxFlashes && slot == null; i++) if (slots[i].left <= 0f) slot = slots[i];
            if (slot == null)
            {
                slot = slots[0];
                for (int i = 1; i < MaxFlashes; i++) if (slots[i].left < slot.left) slot = slots[i];
            }
            slot.target = target; slot.parts = parts; slot.color = color;
            slot.strength = Mathf.Max(strength, slot.left > 0f ? slot.strength : 0f);
            slot.left = slot.length = seconds;
        }

        public void Tick(float dt)
        {
            for (int i = 0; i < MaxFlashes; i++)
            {
                var s = slots[i];
                if (s.left <= 0f) continue;
                s.left -= dt;
                if (s.target == null) { s.left = 0f; continue; }
                // full at once, then out on a curve: the eye reads the peak as the impact
                float k = Mathf.Clamp01(s.left / s.length);
                s.material.SetColor(ColorId, s.color);
                s.material.SetFloat(IntensityId, s.strength * k * k);
            }
        }

        List<Part> PartsOf(GameObject target)
        {
            List<Part> parts;
            if (partsCache.TryGetValue(target, out parts) && parts.Count > 0 && parts[0].renderer != null) return parts;
            if (partsCache.Count > 64) Prune();
            parts = new List<Part>();
            foreach (var r in target.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (!r.enabled || r.sharedMesh == null) continue;
                var mats = r.sharedMaterials;
                int subs = Mathf.Min(mats.Length, r.sharedMesh.subMeshCount);
                for (int s = 0; s < subs; s++)
                    if (Opaque(mats[s])) parts.Add(new Part { renderer = r, submesh = s });
            }
            partsCache[target] = parts;
            return parts;
        }

        static bool Opaque(Material m)
        {
            if (m == null) return false;
            if (m.renderQueue >= (int)RenderQueue.AlphaTest) return false;
            if (m.IsKeywordEnabled("_ALPHATEST_ON") || m.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT")) return false;
            string n = m.name.ToLowerInvariant();
            return !(n.Contains("hair") || n.Contains("lash") || n.Contains("brow") || n.Contains("tearline") || n.Contains("occlusion"));
        }

        void Prune()
        {
            var dead = new List<GameObject>();
            foreach (var kv in partsCache) if (kv.Key == null) dead.Add(kv.Key);
            foreach (var d in dead) partsCache.Remove(d);
        }

        void Enqueue(ScriptableRenderContext ctx, Camera cam)
        {
            if (Running == 0) { pass.drawn = 0; return; }
            if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView) return;
            var data = cam.GetUniversalAdditionalCameraData();
            if (data == null || data.scriptableRenderer == null) return;
            data.scriptableRenderer.EnqueuePass(pass);
        }

        sealed class FlashPass : ScriptableRenderPass
        {
            readonly FeelFlash owner;
            public int drawn;

            sealed class PassData { public FeelFlash owner; public FlashPass pass; }

            public FlashPass(FeelFlash o) { owner = o; profilingSampler = new ProfilingSampler("GK Hit Flash"); }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frame)
            {
                var res = frame.Get<UniversalResourceData>();
                using (var b = graph.AddRasterRenderPass<PassData>("GK Hit Flash", out var d, profilingSampler))
                {
                    d.owner = owner; d.pass = this;
                    b.SetRenderAttachment(res.activeColorTexture, 0);
                    b.SetRenderAttachmentDepth(res.activeDepthTexture, AccessFlags.Read);
                    b.AllowPassCulling(false);
                    b.SetRenderFunc((PassData p, RasterGraphContext c) =>
                    {
                        int n = 0;
                        var slots = p.owner.slots;
                        for (int i = 0; i < slots.Length; i++)
                        {
                            var s = slots[i];
                            if (s.left <= 0f || s.target == null || s.material == null) continue;
                            for (int j = 0; j < s.parts.Count; j++)
                            {
                                var part = s.parts[j];
                                if (part.renderer == null || !part.renderer.enabled || !part.renderer.gameObject.activeInHierarchy) continue;
                                c.cmd.DrawRenderer(part.renderer, s.material, part.submesh, 0);
                            }
                            n++;
                        }
                        p.pass.drawn = n;
                    });
                }
            }
        }
    }
}
