using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Face and figure shots of Hero V2 next to the CC Unity Tools import of the same character (CC_Hero), under the
    /// scene's own light and under a portrait rig (key, fill, rim), to tell material from lighting differences.
    /// Run in the sandbox scene: herocompare. Writes GKBridge/cmp_*.png.
    /// </summary>
    public static class GKHeroCompare
    {
        const string CCPrefab = GKPaths.Characters + "/CC_Hero/Prefabs/CC_Hero.prefab";

        [GKCommand("herocompare", "herocompare: Hero V2 vs the CC Unity Tools import, scene light and portrait light (sandbox)")]
        static void Run(string arg)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("herocompare: stop play mode first"); return; }
            var temps = new System.Collections.Generic.List<GameObject>();
            try
            {
                var a = Place(AssetDatabase.LoadAssetAtPath<GameObject>(GKHeroV2.PrefabPath), new Vector3(-30f, 0f, 30f), temps);
                var b = Place(AssetDatabase.LoadAssetAtPath<GameObject>(CCPrefab), new Vector3(-28f, 0f, 30f), temps);
                var camGo = new GameObject("~cmp_cam"); temps.Add(camGo);
                var cam = camGo.AddComponent<Camera>(); cam.fieldOfView = 24f; cam.nearClipPlane = 0.02f; cam.farClipPlane = 2000f;
                var cd = cam.GetUniversalAdditionalCameraData(); cd.renderPostProcessing = true;
                cd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing; cd.antialiasingQuality = AntialiasingQuality.High;
                foreach (var pair in new[] { new { t = a, n = "herov2" }, new { t = b, n = "cchero" } })
                {
                    if (pair.t == null) continue;
                    var anim = pair.t.GetComponent<Animator>();
                    var head = anim != null && anim.avatar != null && anim.avatar.isHuman ? anim.GetBoneTransform(HumanBodyBones.Head) : null;
                    if (head == null) { GKBridge.Log("herocompare: " + pair.n + " has no humanoid head"); continue; }
                    Vector3 hp = head.position + Vector3.up * 0.05f;
                    Vector3 fwd = pair.t.transform.forward;
                    cam.transform.position = hp + fwd * 0.85f + pair.t.transform.right * 0.2f + Vector3.up * 0.02f; cam.transform.LookAt(hp);
                    GKPreview.Capture(cam, "cmp_" + pair.n + "_face_scene", 1000, 1000);
                }
                // portrait rig: warm key 45 degrees up-left, cool soft fill, rim from behind; the sun dimmed meanwhile
                var sun = RenderSettings.sun; float sunI = sun != null ? sun.intensity : 0f;
                if (sun != null) sun.intensity = sunI * 0.25f;
                var rig = new GameObject("~cmp_rig"); temps.Add(rig);
                Light L(string n, Color c, float i, Vector3 euler) { var g = new GameObject(n); g.transform.SetParent(rig.transform); g.transform.rotation = Quaternion.Euler(euler); var l = g.AddComponent<Light>(); l.type = LightType.Directional; l.color = c; l.intensity = i; l.shadows = LightShadows.Soft; return l; }
                L("key", new Color(1f, 0.95f, 0.88f), 1.6f, new Vector3(28f, 150f, 0f));
                L("fill", new Color(0.78f, 0.86f, 1f), 0.45f, new Vector3(10f, 230f, 0f)).shadows = LightShadows.None;
                L("rim", new Color(1f, 0.97f, 0.92f), 1.3f, new Vector3(18f, 20f, 0f)).shadows = LightShadows.None;
                foreach (var pair in new[] { new { t = a, n = "herov2" }, new { t = b, n = "cchero" } })
                {
                    if (pair.t == null) continue;
                    var anim = pair.t.GetComponent<Animator>();
                    var head = anim != null && anim.avatar != null && anim.avatar.isHuman ? anim.GetBoneTransform(HumanBodyBones.Head) : null;
                    if (head == null) continue;
                    Vector3 hp = head.position + Vector3.up * 0.05f;
                    Vector3 fwd = pair.t.transform.forward;
                    cam.transform.position = hp + fwd * 0.85f + pair.t.transform.right * 0.2f + Vector3.up * 0.02f; cam.transform.LookAt(hp);
                    GKPreview.Capture(cam, "cmp_" + pair.n + "_face_portrait", 1000, 1000);
                }
                if (sun != null) sun.intensity = sunI;
                GKBridge.Log("herocompare: done (cmp_*_face_scene / _face_portrait)");
            }
            finally { if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode(); foreach (var g in temps) if (g != null) Object.DestroyImmediate(g); }
        }

        static GameObject Place(GameObject prefab, Vector3 at, System.Collections.Generic.List<GameObject> temps)
        {
            if (prefab == null) return null;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            temps.Add(go);
            go.transform.SetPositionAndRotation(at, Quaternion.identity);
            var idle = GKPreview.Clip("AX_standing_idle");
            var anim = go.GetComponent<Animator>();
            if (idle != null && anim != null && anim.avatar != null && anim.avatar.isHuman)
            {
                if (!AnimationMode.InAnimationMode()) AnimationMode.StartAnimationMode();
                AnimationMode.BeginSampling(); AnimationMode.SampleAnimationClip(go, idle, 0.6f); AnimationMode.EndSampling();
                go.transform.SetPositionAndRotation(at, Quaternion.identity);
            }
            return go;
        }
    }
}
