using System;
using UnityEngine;

namespace GeorgianKnight
{
    public enum AttackKind { Light, Heavy, Charged, Sprint, Riposte, Execution, Kick, Skill }
    public enum HitReaction { None, Light, Heavy, Stagger, Knockdown }
    public enum HitResult { Ignored, Hit, Blocked, GuardBroken, Parried, Dodged, Killed }
    /// <summary>How the knight fights: each style is its own move set, guard and carry of the gear.</summary>
    public enum WeaponStyle { SwordShield, Sword, DualSwords }
    /// <summary>Which blade cuts in a swing: the sword in the right hand, the second sword in the left, or both.</summary>
    public enum WeaponHand { Main, Off, Both }

    /// <summary>Everything a defender needs to resolve one weapon contact.</summary>
    public struct HitData
    {
        public Combatant attacker;
        public AttackData attack;
        public float damage;
        public float poiseDamage;
        public bool unblockable;
        public bool guardBreak;
        public HitReaction reaction;
        public float knockback;
        public Vector3 point;       // world contact point
        public Vector3 direction;   // blade travel direction at contact (world)
        public float armorPenetration;  // 0..1 fraction of the defender's armour ignored
        public bool critical;       // rolled a critical (bigger feedback)
        public bool execution;      // finishing move (no armour, no reaction)
        public bool ranged;         // an arrow / thrown missile: guarded by its flight direction, a parry deflects it
    }

    /// <summary>A non-attack animation the character can play (dodge, hit reaction, death...).</summary>
    [Serializable]
    public class ActionClip
    {
        public string state;                // Animator state name on the base layer
        public AnimationClip clip;          // source clip (length + editor analysis)
        public float speed = 1f;
        public float fadeIn = 0.08f;
        [Range(0f, 1f)] public float end = 0.92f;          // normalized time we hand control back
        [Range(0f, 1f)] public float cancelFrom = 0.75f;   // normalized time other actions may interrupt
        public bool rootMotion = true;
        [Range(0f, 1f)] public float iFrameStart;
        [Range(0f, 1f)] public float iFrameEnd;
        [Tooltip("Weight of the OffHand layer (relaxed left arm) while this clip plays — hides shield poses.")]
        [Range(0f, 1f)] public float offHand;

        public bool IsValid { get { return !string.IsNullOrEmpty(state) && clip != null; } }
        public float Duration { get { return clip == null ? 0.5f : clip.length / Mathf.Max(0.01f, speed); } }
    }

    [Serializable]
    public class DirectionalClips
    {
        public ActionClip front = new ActionClip();
        public ActionClip back = new ActionClip();
        public ActionClip left = new ActionClip();
        public ActionClip right = new ActionClip();

        /// <summary>Pick a clip by signed angle (degrees) of the direction relative to the character's forward.</summary>
        public ActionClip Pick(float signedAngle)
        {
            ActionClip c;
            float a = Mathf.Abs(signedAngle);
            if (a <= 45f) c = front;
            else if (a >= 135f) c = back;
            else c = signedAngle > 0f ? right : left;
            if (c != null && c.IsValid) return c;
            if (front != null && front.IsValid) return front;
            if (back != null && back.IsValid) return back;
            return null;
        }
    }
}
