#if UNITY_EDITOR || DEVELOPMENT_BUILD
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GeorgianKnight
{
    /// <summary>
    /// Effects review (QUALITY 3b): a camera that holds a set distance from the clash (the player and the nearest
    /// enemy), side-on by default, drawn over the game camera. Added and removed by the editor command `feel_cam`;
    /// never part of a scene or a release build.
    /// </summary>
    public sealed class FeelReviewCam : MonoBehaviour
    {
        public float distance = 5f;
        public string view = "side";
        Camera cam;

        void Start()
        {
            var main = Camera.main;
            cam = gameObject.AddComponent<Camera>();
            if (main != null)
            {
                cam.CopyFrom(main);
                cam.depth = main.depth + 10f;
            }
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 0.05f;
            var data = cam.GetUniversalAdditionalCameraData();
            var mainData = main != null ? main.GetUniversalAdditionalCameraData() : null;
            data.renderPostProcessing = mainData == null || mainData.renderPostProcessing;
            data.antialiasing = mainData != null ? mainData.antialiasing : AntialiasingMode.None;
        }

        void LateUpdate()
        {
            var director = CombatDirector.Existing;
            var p = director != null ? director.Player : null;
            if (p == null) return;
            var e = director.FindTarget(p, p.transform.forward, 30f, 180f);
            Vector3 a = Chest(p), b = e != null ? Chest(e) : a + p.transform.forward;
            Vector3 mid = Vector3.Lerp(a, b, 0.5f);
            Vector3 line = b - a; line.y = 0f;
            if (line.sqrMagnitude < 1e-4f) line = p.transform.forward;
            line.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, line);
            Vector3 off = view == "front" ? -line : view == "back" ? line : side;
            transform.position = mid + off * distance + Vector3.up * Mathf.Lerp(0.1f, 1.2f, Mathf.InverseLerp(1f, 15f, distance));
            transform.rotation = Quaternion.LookRotation(mid - transform.position, Vector3.up);
        }

        static Vector3 Chest(Combatant c)
        {
            var t = c.Anim != null && c.Anim.isHuman ? c.Anim.GetBoneTransform(HumanBodyBones.Chest) : null;
            return t != null ? t.position : c.transform.position + Vector3.up * 1.3f;
        }
    }
}
#endif
