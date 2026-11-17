using System.Linq;
using GeorgianKnight;
using Unity.Cinemachine;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>camstate: every camera, the Cinemachine brain and the PlayerCamera rig as they are now (edit or play).</summary>
    public static class GKCamDiag
    {
        [GKCommand("camstate", "camstate: cameras, brain, vcams and the PlayerCamera rig (edit or play mode)")]
        static void CamState(string arg)
        {
            foreach (var c in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                GKBridge.Log("cam " + Path(c.transform) + " tag " + c.tag + " enabled " + c.enabled + " active " + c.gameObject.activeInHierarchy +
                             " depth " + c.depth + " pos " + c.transform.position.ToString("F2") + " target " + (c.targetTexture != null ? c.targetTexture.name : "screen"));
            var main = Camera.main;
            GKBridge.Log("Camera.main = " + (main != null ? Path(main.transform) : "null"));
            foreach (var b in Object.FindObjectsByType<CinemachineBrain>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                GKBridge.Log("brain on " + Path(b.transform) + " enabled " + b.enabled + " active cam " + (b.ActiveVirtualCamera != null ? b.ActiveVirtualCamera.Name : "none"));
            foreach (var v in Object.FindObjectsByType<CinemachineCamera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var tpf = v.GetComponent<CinemachineThirdPersonFollow>();
                GKBridge.Log("vcam " + Path(v.transform) + " enabled " + v.enabled + " active " + v.gameObject.activeInHierarchy + " prio " + v.Priority.Value +
                             " follow " + (v.Follow != null ? Path(v.Follow) : "null") + " pos " + v.transform.position.ToString("F2") +
                             (tpf != null ? " tpf dist " + tpf.CameraDistance.ToString("F2") + " shoulder " + tpf.ShoulderOffset.ToString("F2") : " no tpf"));
            }
            foreach (var pc in Object.FindObjectsByType<PlayerCamera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var f = pc.CurrentFraming;
                GKBridge.Log("PlayerCamera " + Path(pc.transform) + " enabled " + pc.enabled + " active " + pc.gameObject.activeInHierarchy +
                             " player " + (pc.player != null ? Path(pc.player) : "null") + " pivot " + (pc.pivot != null ? pc.pivot.position.ToString("F2") : "null") +
                             " vcam " + (pc.vcam != null ? pc.vcam.name : "null") + " framing " + f.distance.ToString("F2") + "/" + f.fov.ToString("F1") +
                             " mode " + pc.CurrentMode);
            }
            var player = GameObject.FindWithTag("Player");
            GKBridge.Log("player " + (player != null ? Path(player.transform) + " pos " + player.transform.position.ToString("F2") : "none"));
            if (player != null)
                foreach (var c in player.GetComponentsInChildren<Camera>(true)) GKBridge.Log("  camera inside the player: " + Path(c.transform));
        }

        static string Path(Transform t)
        {
            string s = t.name; var p = t.parent; int k = 0;
            while (p != null && k++ < 5) { s = p.name + "/" + s; p = p.parent; }
            return s;
        }
    }
}
