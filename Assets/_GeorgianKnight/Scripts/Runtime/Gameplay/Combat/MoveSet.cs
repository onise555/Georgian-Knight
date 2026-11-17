using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>All animations/attacks a combatant can perform. Shared by player and enemies.</summary>
    [CreateAssetMenu(menuName = "Georgian Knight/Move Set", fileName = "MoveSet")]
    public class MoveSet : ScriptableObject
    {
        [Header("Combo entry points")]
        public AttackData lightStart;
        public AttackData heavyStart;
        [Tooltip("The blow that reaches a foe lying on the ground (any blade press on a downed foe becomes it). Empty = heavyStart.")]
        public AttackData groundBlow;
        public AttackData sprintAttack;
        public AttackData riposte;
        public AttackData kick;
        public AttackData execution;
        [Tooltip("Attacks the AI chooses from (each may chain through its combo graph).")]
        public AttackData[] aiAttacks;
        [Tooltip("Unblockable, telegraphed attacks used by elites / bosses.")]
        public AttackData[] aiUnblockables;

        [Header("Defense")]
        public DirectionalClips dodge = new DirectionalClips();
        public ActionClip blockHit = new ActionClip();
        public ActionClip guardBreak = new ActionClip();
        public ActionClip parried = new ActionClip();
        [Tooltip("The attacker's own swing met a guard: the blade rebounds off it with weight instead of passing through "
                 + "or stopping dead (QUALITY 3b rhythm, Feel D20). Empty: the light front hit reaction.")]
        public ActionClip blockRecoil = new ActionClip();

        [Header("Reactions")]
        public DirectionalClips hitLight = new DirectionalClips();
        public ActionClip hitHeavy = new ActionClip();
        public ActionClip stagger = new ActionClip();
        public ActionClip knockdown = new ActionClip();
        public ActionClip getUp = new ActionClip();
        public ActionClip executionVictim = new ActionClip();
        public ActionClip[] deaths;

        [Header("Skills & flavour")]
        public ActionClip warCry = new ActionClip();
        [Tooltip("Standing jump and running jump (feet-based vertical root motion moves the capsule).")]
        public ActionClip jump = new ActionClip();
        public ActionClip jumpRun = new ActionClip();
        public ActionClip drawSword = new ActionClip();
        public ActionClip sheathSword = new ActionClip();
        [Tooltip("Normalized time in the draw clip when the hand closes on the hilt (computed by the editor).")]
        [Range(0f, 1f)] public float drawGrab = 0.35f;
        [Tooltip("Normalized time in the sheathe clip when the sword is fully home (computed by the editor).")]
        [Range(0f, 1f)] public float sheatheRelease = 0.65f;
        public ActionClip victory = new ActionClip();

        [Header("Bow (archers)")]
        [Tooltip("Nock and draw to full: the shot's tell, seen and heard before the arrow flies.")]
        public ActionClip bowDraw = new ActionClip();
        [Tooltip("Hold at full draw while the aim settles (loops).")]
        public ActionClip bowHold = new ActionClip();
        [Tooltip("Loose and recoil.")]
        public ActionClip bowLoose = new ActionClip();
        [Tooltip("Normalized time of bowDraw at which the string is at the cheek (from here the arrow is on aim).")]
        [Range(0f, 1f)] public float bowFullDraw = 0.85f;

        [Header("Style")]
        public WeaponStyle style = WeaponStyle.SwordShield;
        [Tooltip("Guard pose on the Block layer while the block is held (its hit reaction is blockHit.state).")]
        public string blockIdleState = "BlockIdle";
        [Tooltip("What the left arm does on the OffHand layer: OffHandPose (free arm) or OffHandDual (the second sword's guard).")]
        public string offHandState = "OffHandPose";
        [Tooltip("OffHand layer weight standing / moving, and while the guard is up.")]
        [Range(0f, 1f)] public float offHandFree;
        [Range(0f, 1f)] public float offHandBlock;
    }
}
