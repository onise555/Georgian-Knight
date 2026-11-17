using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// The bow in the left hand, its string and the arrow on it. The string runs nock to nock; from the moment the draw
    /// hand takes it (stringGrab of the draw clip) until the loose it runs nock - right hand - nock, and the nocked arrow
    /// lies from the string at the hand to the arrow rest. At the loose the string snaps back (a short damped quiver) and
    /// the nocked arrow is hidden: the flying one is <see cref="Arrow"/>.
    /// </summary>
    [DefaultExecutionOrder(170)]   // after the animator and the archer's chest bend (LateUpdate)
    public class BowRig : MonoBehaviour
    {
        public Transform nockTop, nockBottom, arrowRest;
        [Tooltip("The arrow shown on the string while drawing (pivot at its tip, shaft along -Z).")]
        public Transform nockedArrow;
        public LineRenderer stringLine;
        [Tooltip("Normalized time of the draw clip at which the right hand has the string.")]
        [Range(0f, 1f)] public float stringGrab = 0.45f;
        [Tooltip("Where the fingers hold the string, from the right hand bone (local).")]
        public Vector3 fingerOffset = new Vector3(0f, -0.02f, 0.06f);

        [Header("Limbs that bend (Art's composite bow: blendshapes Draw50, Draw100)")]
        [Tooltip("The string's travel from its braced line at full draw (m): 76 cm from the grip's back less the 16.8 cm brace.")]
        public float fullDrawTravel = 0.592f;
        [Tooltip("Where the string leaves the bow at half draw and at full draw (null: the braced nocks throughout).")]
        public Transform nockTopHalf, nockBottomHalf, nockTopDrawn, nockBottomDrawn;
        [Tooltip("The bow's skinned mesh carrying Draw50 / Draw100 (null: rigid limbs).")]
        public SkinnedMeshRenderer limbs;
        /// <summary>How far the bow is drawn (0 braced, 1 full draw), from the string hand's real travel.</summary>
        public float Draw { get; private set; }
        /// <summary>The string hand's distance from the braced string line (m; 0 while the string is not held).</summary>
        public float DrawTravel { get; private set; }
        /// <summary>The string is in the draw hand this frame.</summary>
        public bool StringHeld { get; private set; }
        int shape50 = -1, shape100 = -1;

        Combatant self;
        Transform drawHand;
        float quiver;
        bool drawn;

        void Start()
        {
            self = GetComponentInParent<Combatant>();
            var anim = self != null ? self.Anim : null;
            if (anim != null && anim.isHuman) drawHand = anim.GetBoneTransform(HumanBodyBones.RightHand);
            if (stringLine != null) { stringLine.positionCount = 3; stringLine.useWorldSpace = true; }
            if (limbs != null && limbs.sharedMesh != null)
            {
                shape50 = limbs.sharedMesh.GetBlendShapeIndex("Draw50");
                shape100 = limbs.sharedMesh.GetBlendShapeIndex("Draw100");
            }
        }

        /// <summary>The piecewise bend: Draw50 up to half, then over to Draw100 (two keys keep the rigid siyahs' length,
        /// which one linear blend of a 45 deg turn would shorten). Returns the half / full weights.</summary>
        static void Split(float w, out float half, out float full)
        {
            if (w <= 0.5f) { half = w / 0.5f; full = 0f; }
            else { full = (w - 0.5f) / 0.5f; half = 1f - full; }
        }

        Vector3 Nock(Transform braced, Transform halfT, Transform drawnT, float half, float full)
        {
            Vector3 p = braced.position;
            if (halfT == null || drawnT == null) return p;
            return p + (halfT.position - p) * half + (drawnT.position - p) * full;
        }

        void LateUpdate()
        {
            if (nockTop == null || nockBottom == null || stringLine == null) return;
            bool draw = false;
            if (self != null && self.moves != null && self.CurrentState == Combatant.State.Skill)
            {
                var a = self.CurrentAction;
                var m = self.moves;
                draw = (a == m.bowDraw && self.ActionProgress >= stringGrab) || a == m.bowHold;
            }
            if (drawn && !draw) quiver = 1f;            // loosed (or let down): the string snaps back
            drawn = draw;

            Vector3 top = nockTop.position, bottom = nockBottom.position, mid = (top + bottom) * 0.5f;
            // the bend: the string hand's distance from the braced string line, over the full draw's travel. The limbs bend
            // only while the hand holds the string, and are braced the frame it is let go: a bent bow with a straight
            // string cannot be (Art: the string's length never changes)
            DrawTravel = 0f;
            if (draw && drawHand != null)
            {
                Vector3 h = drawHand.TransformPoint(fingerOffset), axis = bottom - top;
                Vector3 onLine = top + axis * Mathf.Clamp01(Vector3.Dot(h - top, axis) / Mathf.Max(1e-6f, axis.sqrMagnitude));
                DrawTravel = Vector3.Distance(h, onLine);
            }
            StringHeld = draw && drawHand != null;
            Draw = StringHeld ? Mathf.Clamp01(DrawTravel / Mathf.Max(0.01f, fullDrawTravel)) : 0f;
            float wHalf, wFull; Split(Draw, out wHalf, out wFull);
            if (limbs != null)
            {
                if (shape50 >= 0) limbs.SetBlendShapeWeight(shape50, wHalf * 100f);
                if (shape100 >= 0) limbs.SetBlendShapeWeight(shape100, wFull * 100f);
            }
            top = Nock(nockTop, nockTopHalf, nockTopDrawn, wHalf, wFull);
            bottom = Nock(nockBottom, nockBottomHalf, nockBottomDrawn, wHalf, wFull);
            if (!draw) mid = (top + bottom) * 0.5f;
            if (draw && drawHand != null) mid = drawHand.TransformPoint(fingerOffset);
            else if (quiver > 0.01f)
            {
                // a damped shiver across the bow's plane
                quiver *= Mathf.Exp(-12f * Time.deltaTime);
                Vector3 across = arrowRest != null ? (arrowRest.position - mid).normalized : transform.forward;
                mid += across * Mathf.Sin(Time.time * 90f) * 0.012f * quiver;
            }
            stringLine.SetPosition(0, top);
            stringLine.SetPosition(1, mid);
            stringLine.SetPosition(2, bottom);

            if (nockedArrow != null)
            {
                nockedArrow.gameObject.SetActive(draw);
                if (draw && arrowRest != null)
                {
                    // the arrow lies on the rest with its nock on the string; 0.8 m long, so its tip is past the rest
                    Vector3 dir = (arrowRest.position - mid).normalized;
                    nockedArrow.SetPositionAndRotation(mid + dir * 0.8f, Quaternion.LookRotation(dir, transform.up));
                }
            }
        }
    }
}
