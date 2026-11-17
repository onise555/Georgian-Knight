using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// The true bend axes of every finger joint of this character, measured once from its own Humanoid avatar.
    /// Hand-made finger fixes used to bend joints about an axis computed from positions (segment x direction to a
    /// handle, or to the palm); on the Character Creator hand that axis is not the joint's anatomical one, so the
    /// fingers also bent sideways and looked twisted. Here each joint is moved through Unity's own finger muscles
    /// ("Index 2 Stretched", "Index Spread" ...), which follow the avatar's anatomy, and the resulting rotation axis is
    /// stored in the bone's own frame. Every finger fix then bends a joint only about that axis.
    /// The skeleton is restored exactly after the measurement.
    /// </summary>
    [DefaultExecutionOrder(-300)]   // before anything that reads the axes in its Awake / Start
    public sealed class FingerAxes : MonoBehaviour
    {
        public const int Fingers = 5, Joints = 3;
        // [side 0 = left, 1 = right][finger 0 thumb .. 4 little][joint 0 proximal .. 2 distal]
        Vector3[,,] curl = new Vector3[2, Fingers, Joints];
        Vector3[,] spread = new Vector3[2, Fingers];
        Transform[,,] bones = new Transform[2, Fingers, Joints];
        public bool Ready { get; private set; }

        static readonly string[] FingerNames = { "Thumb", "Index", "Middle", "Ring", "Little" };

        void Awake() { Measure(); }

        public void Measure()
        {
            var anim = GetComponent<Animator>();
            if (anim == null || anim.avatar == null || !anim.avatar.isHuman) return;
            // snapshot the whole skeleton: the muscle round trip must leave no trace
            var all = GetComponentsInChildren<Transform>(true);
            var lr = new Quaternion[all.Length]; var lp = new Vector3[all.Length];
            for (int i = 0; i < all.Length; i++) { lr[i] = all[i].localRotation; lp[i] = all[i].localPosition; }
            try
            {
                var hph = new HumanPoseHandler(anim.avatar, transform);
                var pose = new HumanPose();
                hph.GetHumanPose(ref pose);
                float[] basePose = (float[])pose.muscles.Clone();
                string[] muscles = HumanTrait.MuscleName;
                for (int side = 0; side < 2; side++)
                {
                    string s = side == 0 ? "Left " : "Right ";
                    for (int f = 0; f < Fingers; f++)
                    {
                        for (int j = 0; j < Joints; j++)
                        {
                            var hb = (HumanBodyBones)((side == 0 ? (int)HumanBodyBones.LeftThumbProximal : (int)HumanBodyBones.RightThumbProximal) + f * 3 + j);
                            Transform t = anim.GetBoneTransform(hb);
                            bones[side, f, j] = t;
                            if (t == null) continue;
                            int m = System.Array.IndexOf(muscles, s + FingerNames[f] + " " + (j + 1) + " Stretched");
                            curl[side, f, j] = Axis(hph, ref pose, basePose, m, t, true);
                            if (j == 0)
                            {
                                int ms = System.Array.IndexOf(muscles, s + FingerNames[f] + " Spread");
                                spread[side, f] = Axis(hph, ref pose, basePose, ms, t, false);
                            }
                        }
                    }
                }
            }
            finally
            {
                for (int i = 0; i < all.Length; i++) { all[i].localRotation = lr[i]; all[i].localPosition = lp[i]; }
            }
            // sign check on the bind hand: closing must move the next joint toward the palm side
            for (int side = 0; side < 2; side++)
            {
                Transform hand = anim.GetBoneTransform(side == 0 ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                Transform idx = bones[side, 1, 0], mid = bones[side, 2, 0], lit = bones[side, 4, 0];
                if (hand == null || idx == null || mid == null || lit == null) continue;
                Vector3 fw = (mid.position - hand.position).normalized;
                Vector3 palm = side == 1 ? Vector3.Cross(fw, idx.position - lit.position).normalized : Vector3.Cross(idx.position - lit.position, fw).normalized;
                for (int f = 0; f < Fingers; f++)
                    for (int j = 0; j < Joints; j++)
                    {
                        Transform t = bones[side, f, j];
                        if (t == null || curl[side, f, j] == Vector3.zero) continue;
                        Vector3 end = j < 2 && bones[side, f, j + 1] != null ? bones[side, f, j + 1].position
                                    : t.position + (t.position - (j > 0 && bones[side, f, j - 1] != null ? bones[side, f, j - 1].position : hand.position)).normalized * 0.02f;
                        Quaternion keep = t.localRotation;
                        Vector3 before = end;
                        t.localRotation = keep * Quaternion.AngleAxis(20f, curl[side, f, j]);
                        Vector3 after = j < 2 && bones[side, f, j + 1] != null ? bones[side, f, j + 1].position : t.position + t.rotation * (Quaternion.Inverse(keep) * Quaternion.Inverse(t.parent.rotation) * (before - t.position));
                        t.localRotation = keep;
                        if (Vector3.Dot(after - before, palm) < 0f) curl[side, f, j] = -curl[side, f, j];
                    }
            }
            Ready = true;
        }

        /// <summary>Local-frame axis of the rotation the muscle makes from +0.5 to -0.5 (for "Stretched": the curl).</summary>
        static Vector3 Axis(HumanPoseHandler hph, ref HumanPose pose, float[] basePose, int muscle, Transform t, bool curlDir)
        {
            if (muscle < 0) return Vector3.zero;
            System.Array.Copy(basePose, pose.muscles, basePose.Length);
            pose.muscles[muscle] = 0.5f; hph.SetHumanPose(ref pose); Quaternion open = t.localRotation;
            pose.muscles[muscle] = -0.5f; hph.SetHumanPose(ref pose); Quaternion closed = t.localRotation;
            System.Array.Copy(basePose, pose.muscles, basePose.Length);
            hph.SetHumanPose(ref pose);
            Quaternion d = Quaternion.Inverse(open) * closed;   // in the bone's own frame: open -> closed
            float ang; Vector3 ax;
            d.ToAngleAxis(out ang, out ax);
            if (float.IsNaN(ax.x) || Mathf.Abs(ang) < 0.5f) return Vector3.zero;
            if (ang > 180f) { ang = 360f - ang; ax = -ax; }
            return ax.normalized;
        }

        /// <summary>Curl axis of a finger joint in its own frame (finger 0 = thumb .. 4 = little; joint 0..2).
        /// Rotating by +angle about it (post-multiplied on localRotation) closes the joint.</summary>
        public Vector3 CurlAxis(bool right, int finger, int joint) { return curl[right ? 1 : 0, finger, joint]; }
        public Vector3 SpreadAxis(bool right, int finger) { return spread[right ? 1 : 0, finger]; }
        public Transform Bone(bool right, int finger, int joint) { return bones[right ? 1 : 0, finger, joint]; }

        /// <summary>Closes (positive) or opens (negative) one finger joint by <paramref name="degrees"/>, about its
        /// anatomical axis only.</summary>
        public void Bend(bool right, int finger, int joint, float degrees)
        {
            Transform t = bones[right ? 1 : 0, finger, joint];
            Vector3 ax = curl[right ? 1 : 0, finger, joint];
            if (t == null || ax == Vector3.zero || Mathf.Abs(degrees) < 1e-3f) return;
            t.localRotation = t.localRotation * Quaternion.AngleAxis(degrees, ax);
        }

        /// <summary>World direction of a joint's curl axis now.</summary>
        public Vector3 CurlAxisWorld(bool right, int finger, int joint)
        {
            Transform t = bones[right ? 1 : 0, finger, joint];
            return t == null ? Vector3.zero : t.rotation * curl[right ? 1 : 0, finger, joint];
        }
    }
}
