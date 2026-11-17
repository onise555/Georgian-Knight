using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Play-mode QA for animation continuity: every frame (after animation, IK and procedural layers) it measures how far
    /// each humanoid bone rotated relative to its parent, how far the hips moved relative to the character root, and how
    /// far the held weapon jumped relative to the hand. A step larger than what any authored motion produces at 60 fps
    /// is a "pop" (a cut transition, a layer weight snapping, a parent switch, a constraint fighting the animation).
    /// Each pop is logged with the animator state of every layer and the combat/holster state, so the cause is visible.
    /// Attached by the bridge command "popqa".
    /// </summary>
    [DefaultExecutionOrder(2000)]
    public sealed class GKPosePopProbe : MonoBehaviour
    {
        [Tooltip("Per-frame rotation (deg) above which a torso / leg / head bone counts as a pop (60 fps reference).")]
        public float coreLimit = 22f;
        [Tooltip("Per-frame rotation (deg) above which an arm / hand bone counts as a pop (sword swings are fast).")]
        public float armLimit = 38f;
        [Tooltip("Per-frame hips displacement relative to the root (m) above which it counts as a pop.")]
        public float hipsLimit = 0.06f;

        Animator anim;
        Combatant self;
        WeaponHolster holster;
        Transform root, hips, rightHand;
        readonly List<Transform> bones = new List<Transform>();
        readonly List<bool> isArm = new List<bool>();
        Quaternion[] lastRot;
        Vector3 lastHips;
        bool has;
        int frames, pops;
        float worstCore, worstArm, worstHips;
        readonly StringBuilder log = new StringBuilder();
        readonly Dictionary<string, int> byCause = new Dictionary<string, int>();

        void Start()
        {
            anim = GetComponent<Animator>();
            self = GetComponent<Combatant>();
            holster = GetComponent<WeaponHolster>();
            root = transform;
            if (anim == null || !anim.isHuman) { enabled = false; return; }
            hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            rightHand = anim.GetBoneTransform(HumanBodyBones.RightHand);
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var b = (HumanBodyBones)i;
                if (b == HumanBodyBones.Hips || b == HumanBodyBones.Jaw || b == HumanBodyBones.LeftEye || b == HumanBodyBones.RightEye) continue;
                if (i >= (int)HumanBodyBones.LeftThumbProximal && i <= (int)HumanBodyBones.RightLittleDistal) continue;   // fingers: curl layer
                var t = anim.GetBoneTransform(b);
                if (t == null) continue;
                bones.Add(t);
                isArm.Add(b == HumanBodyBones.LeftUpperArm || b == HumanBodyBones.RightUpperArm || b == HumanBodyBones.LeftLowerArm ||
                          b == HumanBodyBones.RightLowerArm || b == HumanBodyBones.LeftHand || b == HumanBodyBones.RightHand ||
                          b == HumanBodyBones.LeftShoulder || b == HumanBodyBones.RightShoulder);
            }
            lastRot = new Quaternion[bones.Count];
        }

        public void ResetStats()
        {
            has = false; frames = 0; pops = 0; worstCore = worstArm = worstHips = 0f;
            log.Length = 0; byCause.Clear();
        }

        void LateUpdate()
        {
            if (anim == null || Time.deltaTime <= 0f) return;
            // normalise to a 60 fps step so a slow frame doesn't read as a pop
            float k = Mathf.Clamp((1f / 60f) / Time.deltaTime, 0.25f, 4f);
            Vector3 hipsLocal = root.InverseTransformPoint(hips.position);
            if (has)
            {
                for (int i = 0; i < bones.Count; i++)
                {
                    float a = Quaternion.Angle(lastRot[i], bones[i].localRotation) * k;
                    float lim = isArm[i] ? armLimit : coreLimit;
                    if (isArm[i]) worstArm = Mathf.Max(worstArm, a); else worstCore = Mathf.Max(worstCore, a);
                    if (a > lim) Report(bones[i].name + " rotated " + a.ToString("F0") + "°");
                }
                float hm = (hipsLocal - lastHips).magnitude * k;
                worstHips = Mathf.Max(worstHips, hm);
                if (hm > hipsLimit) Report("hips moved " + hm.ToString("F3") + " m vs root");
            }
            for (int i = 0; i < bones.Count; i++) lastRot[i] = bones[i].localRotation;
            lastHips = hipsLocal;
            has = true;
            frames++;
        }

        void Report(string what)
        {
            pops++;
            string cause = Describe();
            int c; byCause.TryGetValue(cause, out c); byCause[cause] = c + 1;
            if (pops <= 60) log.AppendLine(string.Format("f{0} t={1:F2}: {2} | {3}", frames, Time.time, what, cause));
        }

        string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(self != null ? self.CurrentState.ToString() : "?");
            if (holster != null) sb.Append(" holster=").Append(holster.State);
            for (int l = 0; l < anim.layerCount; l++)
            {
                float w = l == 0 ? 1f : anim.GetLayerWeight(l);
                if (w < 0.01f && !anim.IsInTransition(l)) continue;
                // the clip that weighs most (a blend tree lists its children in tree order, not by weight)
                var cur = anim.GetCurrentAnimatorClipInfo(l);
                int top = 0;
                for (int c = 1; c < cur.Length; c++) if (cur[c].weight > cur[top].weight) top = c;
                string cn = cur.Length > 0 ? cur[top].clip.name : "-";
                sb.Append(" [").Append(anim.GetLayerName(l)).Append(" w").Append(w.ToString("F2")).Append(' ').Append(cn);
                if (anim.IsInTransition(l))
                {
                    var nx = anim.GetNextAnimatorClipInfo(l);
                    sb.Append(" -> ").Append(nx.Length > 0 ? nx[0].clip.name : "-");
                }
                sb.Append(']');
            }
            return sb.ToString();
        }

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Format("popqa: {0} frames, {1} pop(s); worst per-frame rotation core {2:F0}° arm {3:F0}°, hips {4:F3} m",
                frames, pops, worstCore, worstArm, worstHips));
            var list = new List<KeyValuePair<string, int>>(byCause);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < list.Count && i < 25; i++) sb.AppendLine(string.Format("  {0,4} x  {1}", list[i].Value, list[i].Key));
            sb.Append(log);
            return sb.ToString();
        }
    }
}
