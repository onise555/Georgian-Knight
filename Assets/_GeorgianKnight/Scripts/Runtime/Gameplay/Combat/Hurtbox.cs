using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>Marks a bone collider as belonging to a combatant. Lives on the Hurtbox layer.</summary>
    public class Hurtbox : MonoBehaviour
    {
        public Combatant owner;
        [Tooltip("Head shots and the like can scale damage.")]
        public float damageMultiplier = 1f;
    }
}
