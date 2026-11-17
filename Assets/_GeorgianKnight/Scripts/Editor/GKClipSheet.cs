using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// clipsheet &lt;clip&gt;[,&lt;clip&gt;...] [frames]: the clean knight (sword, shield) posed by each clip at evenly spaced
    /// moments and photographed from a 3/4 front and from the side, one sheet per clip in GKBridge/clipsheets/. A clip
    /// name ending in "@M" plays mirrored (Mecanim's humanoid mirror, as the animator state will), which is how a
    /// right-handed Mixamo swing becomes the left hand's. For choosing clips by eye before they go into a move set.
    /// </summary>
    public static class GKClipSheet
    {
        const int W = 360, H = 480;

        [GKCommand("clipsheet", "clipsheet <clip>[@M][,<clip>...] [frames=8]: the clean knight posed by each clip, 3/4 front + side rows -> GKBridge/clipsheets/ (edit mode)")]
        static void Cmd(string arg)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("clipsheet: stop play mode first"); return; }
            var parts = (arg ?? "").Trim().Split(' ');
            int frames = parts.Length > 1 && int.TryParse(parts[1], out int f) ? Mathf.Clamp(f, 2, 16) : 8;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKHeroCC.CleanPrefabPath);
            if (prefab == null) { GKBridge.Log("clipsheet: no Knight_Clean"); return; }
            string outDir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "GKBridge", "clipsheets");
            Directory.CreateDirectory(outDir);

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.transform.SetPositionAndRotation(new Vector3(0f, -500f, 0f), Quaternion.identity);   // under everything in the open scene
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false;
            var anim = go.GetComponent<Animator>();
            var tmpPath = "Assets/_GeorgianKnight/Generated/~clipsheet.controller";
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(tmpPath);
            var camGo = new GameObject("~clipsheetCam");
            var lightGo = new GameObject("~clipsheetLight");
            var rt = new RenderTexture(W, H, 24) { antiAliasing = 4 };
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false; cam.fieldOfView = 34f; cam.nearClipPlane = 0.05f;
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.62f, 0.66f, 0.7f);
                cam.targetTexture = rt;
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional; light.intensity = 2.2f;
                lightGo.transform.rotation = Quaternion.Euler(40f, 150f, 0f);
                var sm = ctrl.layers[0].stateMachine;
                anim.runtimeAnimatorController = ctrl;
                anim.applyRootMotion = false;
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                foreach (var raw in parts[0].Split(','))
                {
                    string name = raw.Trim();
                    bool mirror = name.EndsWith("@M");
                    string clipName = mirror ? name.Substring(0, name.Length - 2) : name;
                    var clip = GKPreview.Clip(clipName);
                    if (clip == null) { GKBridge.Log("clipsheet: no clip " + clipName); continue; }
                    var st = sm.AddState(name);
                    st.motion = clip; st.mirror = mirror;
                    anim.Rebind();

                    var sheet = new Texture2D(W * frames, H * 2, TextureFormat.RGB24, false);
                    for (int i = 0; i < frames; i++)
                    {
                        float nt = frames == 1 ? 0f : i / (float)(frames - 1) * 0.98f;
                        anim.Play(name, 0, nt);
                        anim.Update(0f);
                        Vector3 hips = anim.GetBoneTransform(HumanBodyBones.Hips).position;
                        Vector3 look = new Vector3(hips.x, go.transform.position.y + 1.05f, hips.z);
                        for (int v = 0; v < 2; v++)
                        {
                            Vector3 dir = v == 0 ? Quaternion.Euler(0f, 30f, 0f) * Vector3.forward : Vector3.right;
                            cam.transform.position = look + dir * 4.2f + Vector3.up * 0.25f;
                            cam.transform.LookAt(look);
                            cam.Render();
                            RenderTexture.active = rt;
                            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                            tex.Apply(false);
                            RenderTexture.active = null;
                            sheet.SetPixels(i * W, (1 - v) * H, W, H, tex.GetPixels());
                        }
                    }
                    sheet.Apply(false);
                    string file = Path.Combine(outDir, name.Replace("@", "_") + ".png");
                    File.WriteAllBytes(file, sheet.EncodeToPNG());
                    Object.DestroyImmediate(sheet);
                    GKBridge.Log("clipsheet: " + name + " (" + clip.length.ToString("F2") + " s, " + frames + " frames) -> " + file);
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(lightGo);
                Object.DestroyImmediate(tex);
                rt.Release(); Object.DestroyImmediate(rt);
                AssetDatabase.DeleteAsset(tmpPath);
            }
        }
    }
}
