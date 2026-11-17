using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Where a wave comes in from (contract with World & Level): a marker the level designer places on a lane down from
    /// the enemy's side. A wave listing this lane spawns its fighters scattered within <see cref="radius"/> of it; they
    /// face along the marker (or toward the player when it is not set) and run in on the NavMesh.
    /// </summary>
    public class SpawnLane : MonoBehaviour
    {
        [Tooltip("Lane id the waves refer to (1, 2, 3 ...).")]
        public int lane = 1;
        [Tooltip("Fighters of one wave scatter within this radius of the marker (m).")]
        public float radius = 3f;
        [Tooltip("Face along the marker's forward when they appear; off = face the player.")]
        public bool useForward;

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.8f, 0.15f, 0.1f, 0.8f);
            Gizmos.DrawWireSphere(transform.position, radius);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * 2f);
        }
    }
}
