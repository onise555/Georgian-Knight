using GeorgianKnight.Core;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Where gameplay systems find the player (which is spawned per scene). Systems must not cache components across
    /// scene loads; read through this service, and listen to <see cref="PlayerSpawned"/> to rebind.
    /// </summary>
    public interface IPlayerContext
    {
        bool HasPlayer { get; }
        GameObject Player { get; }
        Transform Transform { get; }
        Combatant Combatant { get; }
        GKInput Input { get; }
        Animator Animator { get; }
        void Bind(GameObject player);
        void Unbind(GameObject player);
    }

    public sealed class PlayerContext : IPlayerContext
    {
        public bool HasPlayer { get { return Player != null; } }
        public GameObject Player { get; private set; }
        public Transform Transform { get { return Player != null ? Player.transform : null; } }
        public Combatant Combatant { get; private set; }
        public GKInput Input { get; private set; }
        public Animator Animator { get; private set; }

        public void Bind(GameObject player)
        {
            if (player == null || player == Player) return;
            Player = player;
            Combatant = player.GetComponent<Combatant>();
            Input = player.GetComponent<GKInput>();
            Animator = player.GetComponent<Animator>();
            EventBus.Publish(new PlayerSpawned(player));
        }

        public void Unbind(GameObject player)
        {
            if (player != Player) return;
            Player = null; Combatant = null; Input = null; Animator = null;
        }
    }
}
