using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>A knocked-aside arrow tumbling to the ground (no more hits).</summary>
    public class SpentArrow : MonoBehaviour
    {
        Vector3 v, spin; float g; bool down;

        public void Init(Vector3 velocity, float gravity)
        {
            v = velocity; g = gravity;
            spin = new Vector3(Random.Range(-900f, 900f), Random.Range(-300f, 300f), 0f);
        }

        void Update()
        {
            if (down) return;
            float dt = Time.deltaTime;
            v += Vector3.down * g * dt;
            Vector3 to = transform.position + v * dt;
            RaycastHit h;
            if (Physics.Linecast(transform.position, to, out h, GK.GroundMask, QueryTriggerInteraction.Ignore))
            {
                Vector3 flat = Vector3.ProjectOnPlane(transform.forward, h.normal);
                transform.SetPositionAndRotation(h.point + h.normal * 0.01f, Quaternion.LookRotation(flat.sqrMagnitude > 1e-4f ? flat : Vector3.forward, h.normal));
                down = true;
                return;
            }
            transform.position = to;
            transform.Rotate(spin * dt, Space.Self);
        }
    }
}
