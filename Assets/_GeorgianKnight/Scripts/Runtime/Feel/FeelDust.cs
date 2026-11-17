using UnityEngine;

namespace GeorgianKnight.Feel
{
    /// <summary>
    /// Footstep dust: a low puff where a foot lands, more and bigger the faster the walker moves. One pooled particle
    /// system (VFX/Prefabs/VFX_FootDust, tuned there) is moved to each step and emitted with per-particle overrides.
    /// Dust rises only from the ground layers, laid on the surface under the foot.
    /// </summary>
    public sealed class FeelDust
    {
        readonly ParticleSystem system;

        /// <summary>Steps that raised dust since start (tests).</summary>
        public int Puffs { get; private set; }

        public FeelDust(Transform owner)
        {
            var prefab = FeelSettings.Active.footDustPrefab;
            if (prefab == null) { Debug.LogWarning("[GKFeel] no footstep dust prefab in Feel_Default: footstep dust is off"); return; }
            system = Object.Instantiate(prefab, owner);
            system.name = "FootDust";
            var main = system.main;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        public void Step(FootstepProfile f, Vector3 foot, float speed)
        {
            if (system == null || speed < f.minSpeed) return;
            RaycastHit hit;
            if (!Physics.Raycast(foot + Vector3.up * 0.3f, Vector3.down, out hit, 0.6f, f.ground, QueryTriggerInteraction.Ignore)) return;
            float t = Mathf.InverseLerp(f.minSpeed, Mathf.Max(f.minSpeed + 0.01f, f.fullSpeed), speed);
            int count = Mathf.Max(1, Mathf.RoundToInt(Mathf.Lerp(f.puffs.x, f.puffs.y, t)));
            float size = Mathf.Lerp(f.size.x, f.size.y, t);

            var main = system.main;
            float baseSize = main.startSize.constantMax > 0f ? Mathf.Lerp(main.startSize.constantMin, main.startSize.constantMax, Random.value) : 0.3f;
            var ep = new ParticleSystem.EmitParams { applyShapeToPosition = true };
            system.transform.SetPositionAndRotation(hit.point + hit.normal * 0.03f, Quaternion.LookRotation(hit.normal));
            for (int i = 0; i < count; i++)
            {
                ep.startSize = baseSize * size * Random.Range(0.8f, 1.2f);
                ep.startColor = f.dustColor;
                system.Emit(ep, 1);
            }
            Puffs++;
        }
    }
}
