using System;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Player abilities.
    ///  • Kick (E): guard-breaking shove on a short cooldown.
    ///  • War Cry (R): costs one adrenaline segment — terrifies nearby enemies (stagger) and empowers
    ///    the knight for a few seconds (damage, poise damage, stamina regen).
    /// </summary>
    [RequireComponent(typeof(Combatant))]
    public class SkillSystem : MonoBehaviour
    {
        [Header("Kick")]
        public float kickCooldown = 4.5f;
        [Header("War Cry")]
        public float warCryCooldown = 18f;
        public float warCryDuration = 10f;
        public float warCryRadius = 7.5f;
        public float warCryStagger = 1.8f;
        public float warCryDamageBonus = 1.35f;
        public float warCryPoiseBonus = 1.6f;
        public float warCryStaminaBonus = 2f;
        public float warCryShoutDelay = 0.35f;

        public float KickCooldownLeft { get { return Mathf.Max(0f, kickReadyAt - Time.time); } }
        public float WarCryCooldownLeft { get { return Mathf.Max(0f, warCryReadyAt - Time.time); } }
        public bool WarCryActive { get { return Time.time < warCryUntil; } }
        public bool CanWarCry { get { return WarCryCooldownLeft <= 0f && self.Stats.AdrenalineSegmentsFull >= 1; } }

        public event Action WarCryStarted;
        public event Action WarCryShout;
        public event Action WarCryEnded;

        Combatant self;
        float kickReadyAt, warCryReadyAt, warCryUntil = -1f;
        bool shoutPending, buffOn;
        float shoutAt;

        void Awake() { self = GetComponent<Combatant>(); }

        public bool TryKick()
        {
            if (Time.time < kickReadyAt) return false;
            if (!self.RequestKick()) return false;
            kickReadyAt = Time.time + kickCooldown;
            return true;
        }

        public bool TryWarCry()
        {
            if (!CanWarCry) return false;
            if (self.moves == null || !self.PlaySkill(self.moves.warCry)) return false;
            self.Stats.SpendAdrenalineSegment();
            warCryReadyAt = Time.time + warCryCooldown;
            shoutPending = true;
            shoutAt = Time.time + warCryShoutDelay;
            if (WarCryStarted != null) WarCryStarted();
            return true;
        }

        void Update()
        {
            if (shoutPending && Time.time >= shoutAt)
            {
                shoutPending = false;
                Shout();
            }
            if (buffOn && Time.time >= warCryUntil)
            {
                buffOn = false;
                self.Stats.damageMultiplier = 1f;
                self.Stats.poiseDamageMultiplier = 1f;
                self.Stats.staminaRegenMultiplier = 1f;
                if (WarCryEnded != null) WarCryEnded();
            }
        }

        void Shout()
        {
            warCryUntil = Time.time + warCryDuration;
            buffOn = true;
            self.Stats.damageMultiplier = warCryDamageBonus;
            self.Stats.poiseDamageMultiplier = warCryPoiseBonus;
            self.Stats.staminaRegenMultiplier = warCryStaminaBonus;
            var all = CombatDirector.I.all;
            for (int i = 0; i < all.Count; i++)
            {
                Combatant c = all[i];
                if (c == null || c.team == self.team || c.IsDead) continue;
                if (Vector3.Distance(c.transform.position, transform.position) <= warCryRadius) c.ApplyStagger(warCryStagger);
            }
            CombatFeel.I.Shake(transform.position, 0.5f);
            if (WarCryShout != null) WarCryShout();
        }
    }
}
