using System.Text;
using UnityEngine;
using UnityEngine.AI;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// world_agents: every NavMeshAgent in the open scenes (inactive ones too) with its object path, whether it would
    /// create an agent on load (active in hierarchy and enabled) and how far the nearest NavMesh is (none within 5 m = the
    /// Player.log "Failed to create agent because there is no valid NavMesh", GK-27). Read-only; edit mode or Play.
    /// </summary>
    static class GKAgentCheck
    {
        [GKCommand("world_agents", "world_agents: list every NavMeshAgent in the open scenes: live (active + enabled) or not, and the distance to the NavMesh")]
        static void Command(string a)
        {
            var sb = new StringBuilder();
            int live = 0, offMesh = 0, n = 0;
            foreach (var ag in Object.FindObjectsByType<NavMeshAgent>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                n++;
                bool on = ag.enabled && ag.gameObject.activeInHierarchy;
                string mesh = NavMesh.SamplePosition(ag.transform.position, out var hit, 5f, NavMesh.AllAreas)
                    ? hit.distance.ToString("0.00") + " m" : "none within 5 m";
                if (on) { live++; if (!hit.hit) offMesh++; }
                sb.Append("\n  ").Append(on ? "LIVE " : "off  ").Append(Path(ag.transform)).Append(" (").Append(ag.gameObject.scene.name)
                  .Append(") enabled=").Append(ag.enabled).Append(" active=").Append(ag.gameObject.activeInHierarchy).Append(" navmesh ").Append(mesh);
            }
            GKBridge.Log("world_agents: " + n + " agents, " + live + " live, " + offMesh + " live off the NavMesh" + sb);
        }

        static string Path(Transform t)
        {
            string p = t.name;
            for (t = t.parent; t != null; t = t.parent) p = t.name + "/" + p;
            return p;
        }
    }
}
