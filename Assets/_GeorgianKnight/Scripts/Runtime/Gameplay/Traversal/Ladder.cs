using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// A climbable ladder, described by marker transforms exported with the model (positions only, so no axis
    /// convention can go wrong between Blender, FBX and Unity):
    ///   rungFirst / rungLast  centres of the lowest and highest rung (they define the climbing axis and the pitch)
    ///   foot                  rail centre where the ladder stands
    ///   standBottom           where a climber stands to mount, in front of the ladder
    ///   standTop              where a climber stands on the upper surface after stepping off
    /// Ladder space: Up runs along the rails, Forward points from the climbing side into the ladder (the direction the
    /// climber faces), Right completes the frame. A leaning ladder tilts the climber with it.
    /// </summary>
    [DisallowMultipleComponent]
    public class Ladder : MonoBehaviour
    {
        public static readonly List<Ladder> All = new List<Ladder>();

        public Transform rungFirst, rungLast, foot, standBottom, standTop;
        [Min(2)] public int rungCount = 2;
        [Tooltip("Half the distance between the rail centres; hands and feet stay inside it.")]
        public float halfWidth = 0.22f;
        public float rungRadius = 0.022f;

        public bool IsValid { get { return rungFirst != null && rungLast != null && foot != null && standBottom != null && standTop != null && rungCount >= 2; } }

        /// <summary>Unit vector along the rails, upward.</summary>
        public Vector3 Up { get { return (rungLast.position - rungFirst.position).normalized; } }

        /// <summary>Climbing direction into the ladder, perpendicular to <see cref="Up"/> (horizontal for a vertical ladder).</summary>
        public Vector3 Forward
        {
            get
            {
                // the top of a leaning ladder rests against the wall: the climber faces from the foot toward it
                Vector3 toWall = GK.Flat(standTop.position - standBottom.position);
                if (toWall.sqrMagnitude < 1e-4f) toWall = GK.Flat(rungLast.position - foot.position);
                Vector3 up = Up;
                Vector3 f = toWall - up * Vector3.Dot(toWall, up);
                return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
            }
        }

        public Vector3 Right { get { return Vector3.Cross(Up, Forward).normalized; } }

        /// <summary>Rotation of a climber on this ladder: facing <see cref="Forward"/>, body parallel to the rails.</summary>
        public Quaternion ClimbRotation { get { return Quaternion.LookRotation(Forward, Up); } }

        /// <summary>Distance between rung centres along the rails.</summary>
        public float Pitch { get { return Vector3.Distance(rungFirst.position, rungLast.position) / (rungCount - 1); } }

        /// <summary>Axis coordinate (metres along the rails from the foot) of a world point.</summary>
        public float Along(Vector3 p) { return Vector3.Dot(p - foot.position, Up); }

        public float RungAlong(int i) { return Along(rungFirst.position) + i * Pitch; }

        public Vector3 RungCenter(int i) { return rungFirst.position + Up * (i * Pitch); }

        /// <summary>Index of the rung nearest to the axis coordinate <paramref name="along"/>, clamped to the ladder.</summary>
        public int NearestRung(float along)
        {
            int i = Mathf.RoundToInt((along - Along(rungFirst.position)) / Pitch);
            return Mathf.Clamp(i, 0, rungCount - 1);
        }

        /// <summary>World point on the climbing line: <paramref name="along"/> up the rails, <paramref name="standOff"/>
        /// back toward the climber from the rung plane.</summary>
        public Vector3 Point(float along, float standOff)
        {
            return foot.position + Up * along - Forward * standOff;
        }

        /// <summary>World height of the upper walking surface (where <see cref="standTop"/> stands).</summary>
        public float TopHeight { get { return standTop.position.y; } }

        void OnEnable() { if (!All.Contains(this)) All.Add(this); }
        void OnDisable() { All.Remove(this); }

        void OnDrawGizmosSelected()
        {
            if (!IsValid) return;
            Gizmos.color = Color.yellow;
            for (int i = 0; i < rungCount; i++)
            {
                Vector3 c = RungCenter(i);
                Gizmos.DrawLine(c - Right * halfWidth, c + Right * halfWidth);
            }
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(standBottom.position, 0.15f);
            Gizmos.DrawWireSphere(standTop.position, 0.15f);
            Gizmos.DrawRay(foot.position, Forward * 0.5f);
        }
    }
}
