using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// One attack in a combo graph. Timings are normalized (0..1) over the clip; the editor builder
    /// fills them automatically by analysing the sword-hand speed curve of the clip.
    /// </summary>
    [CreateAssetMenu(menuName = "Georgian Knight/Attack", fileName = "Attack")]
    public class AttackData : ScriptableObject
    {
        public AttackKind kind = AttackKind.Light;
        public string state;
        public AnimationClip clip;
        [Tooltip("Which blade cuts: the right-hand sword, the left-hand one (a mirrored swing, state name ending in @M) or both.")]
        public WeaponHand hand = WeaponHand.Main;
        [Tooltip("Strikes with the shield's face (the Combatant's shieldEdge segment) instead of the blade or the legs.")]
        public bool shieldBash;
        public float speed = 1f;
        public float fadeIn = 0.08f;

        [Header("Timing (normalized)")]
        [Range(0f, 1f)] public float hitStart = 0.35f;
        [Tooltip("Animation speed multiplier until hitStart: compresses Mixamo's long anticipation so the blade lands when " +
                 "the design wants (player lights ~0.18 s after the press). 1 = as authored.")]
        public float windupSpeed = 1f;
        [Tooltip("Where the clip begins when this attack is chained out of another (normalized; 0 = its start): the frame whose "
                 + "sword arm meets the previous cut's exit (chainmatch), so the arm does not jump between the blows.")]
        [Range(0f, 0.5f)] public float chainEntry;
        [Tooltip("An authored chain transition (chainvia): the clip's first part carries the arm from the previous cut into this "
                 + "one, and the wrist solver stands down until here (normalized), so one system owns the arm. 0 = none.")]
        [Range(0f, 0.5f)] public float viaEnd;
        [Range(0f, 1f)] public float hitEnd = 0.5f;
        [Tooltip("The way the blade travels at the strike, in the fighter's frame (x right, y up, z forward), from the swing "
                 + "analysis: the AI never throws two blows the same way in a row.")]
        public Vector3 cutDir;

        /// <summary>Two blows the same way (both downward, or nearly the same line).</summary>
        public static bool SameWay(AttackData a, AttackData b)
        {
            if (a == null || b == null || a.cutDir == Vector3.zero || b.cutDir == Vector3.zero) return false;
            return a == b || (a.cutDir.y < -0.6f && b.cutDir.y < -0.6f) || Vector3.Dot(a.cutDir, b.cutDir) > 0.85f;
        }
        [Tooltip("One clip, several cuts (a combo): the blade cuts only inside these windows (normalized start, end), each "
                 + "with its own one-blow-per-target; hitStart / hitEnd span them all. Empty = one cut, hitStart to hitEnd.")]
        public Vector2[] cuts;
        [Tooltip("From here the next attack in the combo may start (input is buffered before this).")]
        [Range(0f, 1f)] public float comboWindow = 0.55f;
        [Tooltip("From here a dodge may cancel the recovery.")]
        [Range(0f, 1f)] public float dodgeCancel = 0.6f;
        [Tooltip("Control returns to locomotion here.")]
        [Range(0f, 1f)] public float end = 0.9f;
        [Tooltip("Steering toward input/target is allowed until this point.")]
        [Range(0f, 1f)] public float steerUntil = 0.3f;

        [Header("Damage")]
        public float damage = 18f;
        public float poiseDamage = 18f;
        public float staminaCost = 10f;
        public bool unblockable;
        public bool guardBreak;
        public HitReaction reaction = HitReaction.Light;
        public float knockback = 1.2f;
        [Tooltip("Attacker ignores light hit reactions while this attack is active.")]
        public bool hyperArmor;

        [Header("Charge (heavy attacks)")]
        public bool chargeable;
        public float maxChargeTime = 1.1f;
        public float chargeDamageMultiplier = 1.8f;

        [Header("Feel")]
        public float hitStop = 0.06f;
        public float shake = 0.25f;

        [Header("Target assist (motion warping)")]
        public float warpMaxDistance = 2.6f;
        public float idealRange = 1.35f;
        public float maxWarpSpeed = 9f;

        [Header("Combo graph")]
        public AttackData nextLight;
        public AttackData nextHeavy;
        [Tooltip("The next light attack through an authored transition clip (viaEnd > 0), played instead of nextLight while "
                 + "UseVia is on.")]
        public AttackData nextLightVia;

        /// <summary>The A/B switch for the authored chain transitions ('chainvia on|off'). Off until the Director has seen
        /// old against new (Team Lead: the old chain stays live until then).</summary>
        public static bool UseVia = false;

        /// <summary>The light attack that follows this one (the authored transition when it is switched on).</summary>
        public AttackData NextLight { get { return UseVia && nextLightVia != null ? nextLightVia : nextLight; } }

        [Header("Animation layers")]
        [Tooltip("Weight of the OffHand layer (relaxed left arm) during this attack — hides shield poses.")]
        [Range(0f, 1f)] public float offHand;

        [Header("Execution")]
        [Tooltip("Executions only: the victim plays its paired execution clip (authored with this one). Off: the victim "
                 + "holds its stagger and the blow itself kills, for finishers without a matching victim clip.")]
        public bool pairedVictim = true;

        public float Duration { get { return clip == null ? 1f : clip.length / Mathf.Max(0.01f, speed); } }
    }
}
