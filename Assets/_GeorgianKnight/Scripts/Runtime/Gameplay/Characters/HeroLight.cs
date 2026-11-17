using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// The hero's own light, as third-person games keep their lead readable in any weather (a soft fill from the
    /// camera's side and a rim from behind him): two spot lights that touch only renderers on the hero rendering layer
    /// (<see cref="HeroLayerBit"/>), so the world keeps its own lighting. Sits on the gameplay camera; follows the
    /// target every frame, also in the editor.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(250)]
    public sealed class HeroLight : MonoBehaviour
    {
        /// <summary>Rendering layer bit of the hero (bit 0 is the default layer everything else is on).</summary>
        public const int HeroLayerBit = 1;

        public Transform target;              // the hero's upper chest
        public Light fill, rim;
        [Tooltip("Fill: from the camera, a little to its right and above.")]
        public Vector3 fillOffset = new Vector3(0.7f, 0.35f, 0f);
        [Tooltip("Rim: behind the hero as seen from the camera, to the side and above (m).")]
        public float rimBehind = 2.0f, rimSide = 0.9f, rimUp = 1.3f;
        [Range(0f, 3f)] public float fillIntensity = 0.8f, rimIntensity = 1.8f;

        void LateUpdate() { Place(); }

        /// <summary>Puts the fill and the rim around the target for the camera's current view.</summary>
        public void Place()
        {
            if (target == null || fill == null || rim == null) return;
            Vector3 aim = target.position;
            Vector3 cam = transform.position;
            Vector3 away = Vector3.ProjectOnPlane(aim - cam, Vector3.up);
            if (away.sqrMagnitude < 1e-6f) away = transform.forward;
            away.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, away);
            rim.transform.position = aim + away * rimBehind - side * rimSide + Vector3.up * rimUp;
            rim.transform.rotation = Quaternion.LookRotation(aim - rim.transform.position, Vector3.up);
            fill.transform.position = cam + transform.TransformVector(fillOffset);
            fill.transform.rotation = Quaternion.LookRotation(aim - fill.transform.position, Vector3.up);
            fill.intensity = fillIntensity; rim.intensity = rimIntensity;
        }

        /// <summary>Puts every renderer under <paramref name="root"/> on the hero layer as well as the default one.</summary>
        public static void MarkHero(GameObject root)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true)) r.renderingLayerMask |= 1u << HeroLayerBit;
        }
    }
}
