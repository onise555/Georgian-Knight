using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Visual + numeric inspection of a character prefab: collider overlays, sword grip close-ups,
    /// finger-to-handle distances. "inspect [clip] [time]" from the bridge.
    /// </summary>
    public static class GKInspect
    {
        static Material Overlay(Color c)
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            m.SetFloat("_Surface", 1f); m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One); m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f); m.SetFloat("_Cull", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = 3100;
            m.SetColor("_BaseColor", c);
            return m;
        }

        static void Viz(Collider col, Material mat, List<GameObject> temps)
        {
            GameObject g = null;
            var cap = col as CapsuleCollider; var sph = col as SphereCollider; var box = col as BoxCollider;
            if (cap != null)
            {
                g = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                g.transform.SetParent(col.transform, false);
                g.transform.localPosition = cap.center;
                g.transform.localRotation = cap.direction == 0 ? Quaternion.Euler(0f, 0f, 90f) : cap.direction == 2 ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
                g.transform.localScale = new Vector3(cap.radius * 2f, cap.height * 0.5f, cap.radius * 2f);
            }
            else if (sph != null)
            {
                g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                g.transform.SetParent(col.transform, false);
                g.transform.localPosition = sph.center;
                g.transform.localScale = Vector3.one * sph.radius * 2f;
            }
            else if (box != null)
            {
                g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                g.transform.SetParent(col.transform, false);
                g.transform.localPosition = box.center;
                g.transform.localScale = box.size;
            }
            if (g == null) return;
            Object.DestroyImmediate(g.GetComponent<Collider>());
            var r = g.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat; r.shadowCastingMode = ShadowCastingMode.Off;
            temps.Add(g);
        }

        static Camera MakeCam(List<GameObject> temps)
        {
            var go = new GameObject("gk_inspect_cam");
            temps.Add(go);
            var cam = go.AddComponent<Camera>();
            cam.nearClipPlane = 0.02f; cam.farClipPlane = 30000f;
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            return cam;
        }

        static void Shot(Camera cam, Vector3 pos, Vector3 look, float fov, string name)
        {
            cam.transform.position = pos;
            cam.transform.rotation = Quaternion.LookRotation(look - pos, Vector3.up);
            cam.fieldOfView = fov;
            GKPreview.Capture(cam, name, 1280, 720);
        }

        /// <summary>Drops the player prefab as a ragdoll onto the terrain (hit from the front) and photographs the result.</summary>
        public static void RagdollDrop(string arg)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKGameBuilder.PlayerPrefabPath);
            var temps = new List<GameObject>();
            var hidden = new List<GameObject>();
            foreach (var c in Object.FindObjectsByType<Combatant>(FindObjectsSortMode.None)) { if (c.gameObject.activeSelf) { c.gameObject.SetActive(false); hidden.Add(c.gameObject); } }
            var prevMode = Physics.simulationMode;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                Vector3 p = new Vector3(0f, 0f, 4f);
                p.y = GKEnvironment.SampleHeight(p) + 0.02f;
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                temps.Add(inst);
                inst.transform.SetPositionAndRotation(p, Quaternion.Euler(0f, 180f, 0f));
                var clip = GKPreview.Clip("AX_standing_idle");
                if (clip != null) { clip.SampleAnimation(inst, 0.6f); inst.transform.SetPositionAndRotation(p, Quaternion.Euler(0f, 180f, 0f)); }
                var cc = inst.GetComponent<CharacterController>(); if (cc != null) cc.enabled = false;
                var rag = inst.GetComponent<Ragdoll>();
                Physics.SyncTransforms();
                float strength = 3.5f;
                if (!string.IsNullOrEmpty(arg)) float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out strength);
                Vector3 hitDir = -inst.transform.forward;
                rag.Activate(hitDir * strength + Vector3.up * 0.6f, rag.colliders[1].bounds.center);
                var cam = MakeCam(temps);
                Vector3 F = inst.transform.forward, R = inst.transform.right, U = Vector3.up;
                for (int frame = 0; frame < 4; frame++)
                {
                    int steps = frame == 0 ? 30 : frame == 1 ? 50 : frame == 2 ? 120 : 400;
                    for (int s = 0; s < steps; s++) Physics.Simulate(0.005f);
                    Vector3 c = rag.hips.position;
                    Shot(cam, c + R * 3.2f + F * 0.6f + U * 1.0f, c, 40f, "rag_" + frame);
                }
                Vector3 hc = rag.hips.position;
                Shot(cam, hc + U * 3.2f + F * 0.3f, hc, 50f, "rag_top");
                GKBridge.Log("ragdoll rest: hips " + (rag.hips.position.y - GKEnvironment.SampleHeight(rag.hips.position)).ToString("F2") + " m above ground");
                foreach (var b in rag.bodies)
                {
                    float gy = GKEnvironment.SampleHeight(b.position);
                    if (b.position.y < gy - 0.03f) GKBridge.Log("ragdoll WARNING: " + b.name + " below ground by " + (gy - b.position.y).ToString("F2"));
                }
            }
            finally
            {
                Physics.simulationMode = prevMode;
                foreach (var t in temps) if (t != null) Object.DestroyImmediate(t);
                foreach (var g in hidden) if (g != null) g.SetActive(true);
            }
        }

        public static void Run(string arg)
        {
            string clipName = "AX_standing_idle"; float time = 0.6f; bool overlay = true;
            if (!string.IsNullOrEmpty(arg))
            {
                var parts = arg.Split(' ');
                clipName = parts[0];
                if (parts.Length > 1) float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out time);
                if (parts.Length > 2) overlay = !arg.Contains("nocol");
            }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKGameBuilder.PlayerPrefabPath);
            if (prefab == null) { GKBridge.Log("inspect: no player prefab"); return; }
            var temps = new List<GameObject>();
            var hidden = new List<GameObject>();
            foreach (var c in Object.FindObjectsByType<Combatant>(FindObjectsSortMode.None)) { if (c.gameObject.activeSelf) { c.gameObject.SetActive(false); hidden.Add(c.gameObject); } }
            try
            {
                Vector3 p = new Vector3(0f, 0f, 4f);
                p.y = GKEnvironment.SampleHeight(p) + 0.02f;
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                temps.Add(inst);
                inst.transform.SetPositionAndRotation(p, Quaternion.Euler(0f, 180f, 0f));
                var clip = GKPreview.Clip(clipName);
                if (clip != null)
                {
                    clip.SampleAnimation(inst, Mathf.Min(time, clip.length));
                    inst.transform.SetPositionAndRotation(p, Quaternion.Euler(0f, 180f, 0f));
                }
                else GKBridge.Log("inspect: clip not found " + clipName);

                var map = GKCharacters.MapBones(inst.transform);
                var rag = inst.GetComponent<Ragdoll>();
                bool sheathed = arg != null && arg.Contains("sheathed");
                var holster = inst.GetComponent<WeaponHolster>();
                if (holster != null && sheathed) { holster.InitializeNow(); holster.ForceState(false); }
                var scab = inst.GetComponent<ScabbardMotion>();
                if (scab != null) { scab.Init(); scab.Solve(0f); GKBridge.Log("inspect: scabbard clearance " + scab.LastClearance.ToString("F3") + " m"); }
                if (overlay && rag != null)
                {
                    var torso = Overlay(new Color(1f, 0.25f, 0.1f, 0.33f));
                    var limb = Overlay(new Color(0.1f, 0.8f, 1f, 0.33f));
                    var headM = Overlay(new Color(1f, 0.9f, 0.1f, 0.33f));
                    foreach (var col in rag.colliders)
                        Viz(col, col is BoxCollider ? torso : col is SphereCollider ? headM : limb, temps);
                }

                // ---- grip numbers
                var sword = inst.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Sword");
                if (sword != null)
                {
                    Transform grip = sword.Find("Grip"), bb = sword.Find("BladeBase"), bt = sword.Find("BladeTip");
                    Vector3 axis = (bt.position - bb.position).normalized;
                    System.Func<Vector3, float> lineDist = q => Vector3.Cross(q - grip.position, axis).magnitude;
                    System.Func<Vector3, float> along = q => Vector3.Dot(q - grip.position, axis);
                    var lines = new List<string>();
                    foreach (var f in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
                    {
                        Transform d; Transform px;
                        map.TryGetValue("Right " + f + " Distal", out d);
                        map.TryGetValue("Right " + f + " Proximal", out px);
                        if (d == null) continue;
                        Vector3 tip = d.childCount > 0 ? d.GetChild(0).position : d.position;
                        lines.Add(string.Format("{0} tip {1:F3} m from handle axis (along {2:+0.000;-0.000}), proximal {3:F3}", f, lineDist(tip), along(tip), px != null ? lineDist(px.position) : -1f));
                    }
                    var hand = map["RightHand"];
                    Transform mid1; map.TryGetValue("Right Middle Proximal", out mid1);
                    Vector3 palm = mid1 != null ? Vector3.Lerp(hand.position, mid1.position, 0.55f) : hand.position;
                    GKBridge.Log("grip: palm centre " + lineDist(palm).ToString("F3") + " m from handle axis, along " + along(palm).ToString("+0.000;-0.000") +
                                 " | blade length " + Vector3.Distance(bb.position, bt.position).ToString("F2"));
                    foreach (var l in lines) GKBridge.Log("grip: " + l);
                }

                // ---- shots
                var cam = MakeCam(temps);
                Vector3 F = inst.transform.forward, R = inst.transform.right, U = Vector3.up;
                Vector3 c = p + U * 0.95f;
                string tag = clipName.Replace(" ", "_");
                Shot(cam, c + F * 3.4f + U * 0.15f, c, 36f, "insp_front");
                Shot(cam, c + R * 3.4f + U * 0.15f, c, 36f, "insp_side");
                Shot(cam, c - F * 2.6f - R * 1.8f + U * 0.35f, c, 36f, "insp_back");
                if (sheathed)
                {
                    var hipP = map["Hips"].position - inst.transform.right * 0.2f;
                    Shot(cam, hipP - inst.transform.right * 1.1f + F * 0.35f + U * 0.1f, hipP - U * 0.15f, 40f, "insp_sheath_side");
                    Shot(cam, hipP + F * 1.2f - inst.transform.right * 0.5f + U * 0.2f, hipP - U * 0.15f, 40f, "insp_sheath_front");
                    Shot(cam, hipP - F * 1.2f - inst.transform.right * 0.4f + U * 0.3f, hipP - U * 0.15f, 40f, "insp_sheath_back");
                }
                var h = map["RightHand"].position;
                Shot(cam, h + R * 0.42f + F * 0.18f + U * 0.08f, h, 32f, "insp_hand_out");
                Shot(cam, h + F * 0.45f - R * 0.08f + U * 0.05f, h, 32f, "insp_hand_front");
                Shot(cam, h - R * 0.35f + F * 0.25f + U * 0.25f, h, 34f, "insp_hand_in");
            }
            finally
            {
                foreach (var t in temps) if (t != null) Object.DestroyImmediate(t);
                foreach (var g in hidden) if (g != null) g.SetActive(true);
            }
        }
    }
}
