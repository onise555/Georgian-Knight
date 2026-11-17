using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// The outer surface of the clothed torso, as a table of radii around a vertical axis riding on one bone: per height
    /// band and per angle sector, how far out the coat reaches (fitted to the skinned meshes at build time, arms and head
    /// left out). The ragdoll's boxes are fitted for physics and the coat bulges past them, so a blade the solver thought
    /// clear of the body still went through the belly; this is what the blade is kept out of.
    /// </summary>
    public class BodyShell : MonoBehaviour
    {
        public Transform frame;            // the bone the shell rides with
        public Vector3 originLocal;        // axis origin in the frame's space
        public Vector3 upLocal = Vector3.up, forwardLocal = Vector3.forward;
        public float yMin, yStep = 0.04f;  // height of band 0 along the axis, band height (m)
        public int bins, sectors = 24;
        public float[] radius;             // [band * sectors + sector] (m); 0 = nothing there

        /// <summary>How far <paramref name="world"/> (padded by <paramref name="pad"/>) is inside the shell, and the way
        /// out (horizontal, away from the axis). False when outside or beyond the shell's height.</summary>
        public bool Depth(Vector3 world, float pad, out float depth, out Vector3 outward)
        {
            depth = 0f; outward = Vector3.zero;
            if (frame == null || radius == null || bins <= 0) return false;
            Vector3 o = frame.TransformPoint(originLocal);
            Vector3 up = frame.TransformDirection(upLocal).normalized;
            Vector3 fwd = Vector3.ProjectOnPlane(frame.TransformDirection(forwardLocal), up).normalized;
            Vector3 right = Vector3.Cross(up, fwd);
            Vector3 d = world - o;
            float h = Vector3.Dot(d, up);
            int bin = Mathf.FloorToInt((h - yMin) / yStep);
            if (bin < 0 || bin >= bins) return false;
            Vector3 flat = d - up * h;
            float r = flat.magnitude;
            float ang = Mathf.Atan2(Vector3.Dot(flat, right), Vector3.Dot(flat, fwd));
            int sec = Mathf.Clamp(Mathf.FloorToInt((ang + Mathf.PI) / (2f * Mathf.PI) * sectors), 0, sectors - 1);
            float R = radius[bin * sectors + sec];
            if (R <= 0f) return false;
            depth = R + pad - r;
            if (depth <= 0f) return false;
            outward = r > 1e-4f ? flat / r : fwd;
            return true;
        }
    }
}
