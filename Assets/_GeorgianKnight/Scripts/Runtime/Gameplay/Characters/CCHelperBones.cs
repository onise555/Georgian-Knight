using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Drives the Character Creator helper bones that Mecanim leaves at their bind pose. On the CC rig the skin of the
    /// arms and legs is not on the limb bones at all (they carry zero weight): it is on two twist bones per segment
    /// (one at the joint, one half way down) and on a share bone at the elbow and knee. The Humanoid retarget only
    /// poses the limb bones, so every twist bone used to turn rigidly with its limb: a roll of the upper arm twisted
    /// the whole shoulder with it (cloth and skin tearing at the deltoid in attacks), the forearm never pronated
    /// between elbow and wrist, and the elbow and knee folded without volume. Each frame, after the animation and the
    /// pose fixes, this measures each limb's roll about its own axis and spreads it the way the anatomy does:
    /// the joint end stays put, the middle takes half, the far end all of it. The share bones take half the bend, and
    /// the second neck bone takes part of the head's turn so the neck bends as a curve instead of at one ring.
    /// </summary>
    [DefaultExecutionOrder(170)]   // after PoseInertializer (150), PostureCorrector (160), HandGrip (165); before cloth (190)
    public sealed class CCHelperBones : MonoBehaviour
    {
        [Header("Roll spread (share of the limb's roll each helper ends up with in the world)")]
        [Tooltip("Shoulder end of the upper arm (UpperarmTwist01): 0 = stays with the clavicle, 1 = turns with the arm.")]
        [Range(0f, 1f)] public float shoulderRoll = 0.2f;
        [Tooltip("Middle of the upper arm (UpperarmTwist02).")]
        [Range(0f, 1f)] public float midUpperArmRoll = 0.55f;
        [Tooltip("Middle of the forearm (ForearmTwist02), share of the whole pronation (forearm + hand).")]
        [Range(0f, 1f)] public float midForearmRoll = 0.5f;
        [Tooltip("Hip end of the thigh (ThighTwist01).")]
        [Range(0f, 1f)] public float hipRoll = 0.2f;
        [Range(0f, 1f)] public float midThighRoll = 0.55f;
        [Range(0f, 1f)] public float midCalfRoll = 0.5f;
        [Header("Joints")]
        [Tooltip("Share of the elbow / knee bend the share bone takes back (keeps the joint's volume).")]
        [Range(0f, 1f)] public float shareBend = 0.5f;
        [Tooltip("Share of the head's turn and nod taken by the upper neck bone.")]
        [Range(0f, 1f)] public float neckShare = 0.4f;
        [Range(10f, 170f)] public float maxRoll = 130f;

        /// <summary>Largest roll measured on any limb last frame (deg), for QA.</summary>
        public float LastMaxRoll { get; private set; }

        sealed class Limb
        {
            public Transform bone, child;                  // upper arm + forearm, thigh + calf, forearm + hand, calf + foot
            public Transform near, mid;                    // twist bones at the joint end and half way down
            public Transform share;                        // elbow / knee share bone (on the child segment), or null
            public Quaternion boneBind, childBind, nearBind, midBind, shareBind;
            public bool endSegment;                        // forearm / calf: the roll comes from the hand / foot too
            public int kind;                               // 0 upper arm, 1 forearm, 2 thigh, 3 calf (from the mid twist bone's name, once)
            public bool knee;                              // the share bone is a knee (the hip's roll, not the shoulder's)
        }

        Limb[] limbs;
        Transform neck2, head;
        Quaternion neck2Bind, headBind;
        bool ready;

        void Awake() { Setup(); }

        void Setup()
        {
            var map = new System.Collections.Generic.Dictionary<string, Transform>();
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("CC_Base_") && !map.ContainsKey(t.name)) map[t.name] = t;
            System.Func<string, Transform> B = n => { Transform t; return map.TryGetValue("CC_Base_" + n, out t) ? t : null; };
            var list = new System.Collections.Generic.List<Limb>();
            foreach (string s in new[] { "L_", "R_" })
            {
                Add(list, B(s + "Upperarm"), B(s + "Forearm"), B(s + "UpperarmTwist01"), B(s + "UpperarmTwist02"), B(s + "ElbowShareBone"), false);
                Add(list, B(s + "Forearm"), B(s + "Hand"), B(s + "ForearmTwist01"), B(s + "ForearmTwist02"), null, true);
                Add(list, B(s + "Thigh"), B(s + "Calf"), B(s + "ThighTwist01"), B(s + "ThighTwist02"), B(s + "KneeShareBone"), false);
                Add(list, B(s + "Calf"), B(s + "Foot"), B(s + "CalfTwist01"), B(s + "CalfTwist02"), null, true);
            }
            limbs = list.ToArray();
            neck2 = B("NeckTwist02"); head = B("Head");
            if (neck2 != null) neck2Bind = neck2.localRotation;
            if (head != null) headBind = head.localRotation;
            ready = limbs.Length > 0 || neck2 != null;
        }

        static void Add(System.Collections.Generic.List<Limb> list, Transform bone, Transform child, Transform near, Transform mid, Transform share, bool end)
        {
            if (bone == null || child == null || near == null || mid == null) return;
            list.Add(new Limb
            {
                bone = bone, child = child, near = near, mid = mid, share = share, endSegment = end,
                // the names are read here, once: in LateUpdate they cost 12 string allocations a frame (GK-26)
                kind = mid.name.Contains("Upperarm") ? 0 : mid.name.Contains("Forearm") ? 1 : mid.name.Contains("Thigh") ? 2 : 3,
                knee = share != null && share.name.Contains("Knee"),
                boneBind = bone.localRotation, childBind = child.localRotation, nearBind = near.localRotation,
                midBind = mid.localRotation, shareBind = share != null ? share.localRotation : Quaternion.identity
            });
        }

        bool reported;

        void LateUpdate()
        {
            if (!ready) return;
            if (limbs == null)
            {
                // seen in play: an instance whose set-up had not run (or was lost): set it up now rather than leave
                // every twist bone at its bind pose (the arms then twist as rigid tubes)
                if (!reported) { reported = true; Debug.LogWarning("[CCHelperBones] " + name + ": limbs were not set up, setting up now"); }
                Setup();
                if (limbs == null) return;
            }
            float maxSeen = 0f;
            for (int i = 0; i < limbs.Length; i++)
            {
                var L = limbs[i];
                if (L == null || L.bone == null || L.child == null || L.near == null || L.mid == null)
                {
                    if (!reported) { reported = true; Debug.LogWarning("[CCHelperBones] " + name + ": a limb bone is gone (limb " + i + ")"); }
                    continue;
                }
                // the segment's own roll about its axis (+Y on the CC rig), relative to the bind pose
                Quaternion own = Quaternion.Inverse(L.boneBind) * L.bone.localRotation;
                float ownRoll = Twist(own);
                float near, mid;
                if (!L.endSegment)
                {
                    // upper arm / thigh: the joint end stays with the parent, the middle takes part
                    float sNear = shoulderOrHip(L), sMid = midShare(L);
                    near = (sNear - 1f) * ownRoll;              // local: cancel what the parent bone already turned
                    mid = (sMid - sNear) * ownRoll;             // local to the near bone
                }
                else
                {
                    // forearm / calf: the elbow / knee end does not roll; the middle takes half of the whole
                    // pronation (the segment's own roll plus the hand's / foot's roll about this segment's axis)
                    Quaternion rel = L.child.localRotation * Quaternion.Inverse(L.childBind);   // in this segment's frame
                    float endRoll = Twist(rel);
                    float total = ownRoll + endRoll;
                    near = -ownRoll;
                    mid = midShare(L) * total;
                }
                near = Mathf.Clamp(near, -maxRoll, maxRoll);
                mid = Mathf.Clamp(mid, -maxRoll, maxRoll);
                L.near.localRotation = Quaternion.AngleAxis(near, Vector3.up) * L.nearBind;
                L.mid.localRotation = Quaternion.AngleAxis(mid, Vector3.up) * L.midBind;
                maxSeen = Mathf.Max(maxSeen, Mathf.Abs(ownRoll));

                if (L.share != null)
                {
                    // half the elbow / knee bend, without its roll (the share bone sits on the child segment)
                    Quaternion bend = Quaternion.Inverse(L.childBind) * L.child.localRotation;
                    float r = Twist(bend);
                    Quaternion tw = Quaternion.AngleAxis(r, Vector3.up);
                    Quaternion swing = bend * Quaternion.Inverse(tw);
                    // world = parent * bind * swing^(1-share): undo the child's roll, then the share of its swing
                    L.share.localRotation = Quaternion.Inverse(tw) * Quaternion.Slerp(Quaternion.identity, Quaternion.Inverse(swing), shareBend) * L.shareBind;
                }
            }
            LastMaxRoll = maxSeen;

            if (neck2 != null && head != null && neckShare > 0f)
            {
                // move part of the head's rotation down to the upper neck bone, head keeps its world rotation
                Quaternion headWorld = head.rotation;
                Quaternion rel = head.localRotation * Quaternion.Inverse(headBind);
                neck2.localRotation = Quaternion.Slerp(Quaternion.identity, rel, neckShare) * neck2Bind;
                head.rotation = headWorld;
            }
        }

        float shoulderOrHip(Limb L) { return L.knee ? hipRoll : shoulderRoll; }
        float midShare(Limb L)
        {
            switch (L.kind)
            {
                case 0: return midUpperArmRoll;
                case 1: return midForearmRoll;
                case 2: return midThighRoll;
                default: return midCalfRoll;
            }
        }

        /// <summary>Signed rotation of q about +Y (swing-twist decomposition), degrees in (-180, 180].</summary>
        static float Twist(Quaternion q)
        {
            float a = 2f * Mathf.Atan2(q.y, q.w) * Mathf.Rad2Deg;
            if (a > 180f) a -= 360f; else if (a < -180f) a += 360f;
            return a;
        }
    }
}
