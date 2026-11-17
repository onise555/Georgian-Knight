using System;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>Health, stamina, poise and adrenaline with regeneration rules.</summary>
    public class CombatStats : MonoBehaviour
    {
        [Header("Health")]
        public float maxHealth = 200f;
        [Header("Stamina")]
        public float maxStamina = 100f;
        public float staminaRegen = 32f;
        public float staminaRegenDelay = 0.7f;
        [Header("Poise (stagger resistance)")]
        public float maxPoise = 60f;
        public float poiseRegen = 25f;
        public float poiseRegenDelay = 2.2f;
        [Header("Adrenaline (skills)")]
        public int adrenalineSegments = 3;
        public float adrenalinePerSegment = 100f;

        public float Health { get; private set; }
        public float Stamina { get; private set; }
        public float Poise { get; private set; }
        public float Adrenaline { get; private set; }
        public bool IsDead { get { return Health <= 0f; } }
        public float HealthNormalized { get { return maxHealth <= 0f ? 0f : Health / maxHealth; } }
        public float StaminaNormalized { get { return maxStamina <= 0f ? 0f : Stamina / maxStamina; } }
        public float AdrenalineMax { get { return adrenalineSegments * adrenalinePerSegment; } }

        [NonSerialized] public float damageMultiplier = 1f;
        [NonSerialized] public float poiseDamageMultiplier = 1f;
        [NonSerialized] public float staminaRegenMultiplier = 1f;
        [NonSerialized] public float damageTakenMultiplier = 1f;

        public event Action<float, float> HealthChanged;   // (current, delta)
        public event Action Died;

        float staminaTimer, poiseTimer;

        void Awake() { ResetAll(); }

        public void ResetAll()
        {
            Health = maxHealth;
            Stamina = maxStamina;
            Poise = maxPoise;
            Adrenaline = 0f;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (IsDead) return;
            if (staminaTimer > 0f) staminaTimer -= dt;
            else Stamina = Mathf.Min(maxStamina, Stamina + staminaRegen * staminaRegenMultiplier * dt);
            if (poiseTimer > 0f) poiseTimer -= dt;
            else Poise = Mathf.Min(maxPoise, Poise + poiseRegen * dt);
        }

        public bool HasStamina(float amount) { return Stamina > 0.01f && Stamina >= amount * 0.35f; }

        public void UseStamina(float amount)
        {
            if (amount <= 0f) return;
            Stamina = Mathf.Max(0f, Stamina - amount);
            staminaTimer = staminaRegenDelay;
        }

        /// <summary>Returns true if poise broke.</summary>
        public bool DamagePoise(float amount)
        {
            poiseTimer = poiseRegenDelay;
            Poise -= amount;
            if (Poise <= 0f) { Poise = maxPoise; return true; }
            return false;
        }

        public void ResetPoise() { Poise = maxPoise; }

        /// <summary>Weapon / scripted damage. Multiplied by <see cref="damageTakenMultiplier"/> (difficulty, scripted
        /// invulnerability); armour is applied per hit by the combat code before this call.</summary>
        public void Damage(float amount)
        {
            if (IsDead || amount <= 0f) return;
            amount *= damageTakenMultiplier;
            if (amount <= 0f) return;
            ApplyHealth(Health - amount);
        }

        /// <summary>Heals up to max health; returns the amount actually restored.</summary>
        public float Heal(float amount)
        {
            if (IsDead || amount <= 0f) return 0f;
            float before = Health;
            Health = Mathf.Min(maxHealth, Health + amount);
            float delta = Health - before;
            if (delta > 0f && HealthChanged != null) HealthChanged(Health, delta);
            return delta;
        }

        /// <summary>Sets health directly (save restore, scripted events); clamped to [0, max]. Can kill, never revives.</summary>
        public void SetHealth(float value)
        {
            if (IsDead) return;
            ApplyHealth(Mathf.Clamp(value, 0f, maxHealth));
        }

        /// <summary>Kills regardless of armour, multipliers or invulnerability (executions, scripted deaths).</summary>
        public void Kill()
        {
            if (IsDead) return;
            ApplyHealth(0f);
        }

        /// <summary>Restores stamina immediately (items, skills); returns the amount actually restored.</summary>
        public float RestoreStamina(float amount)
        {
            if (IsDead || amount <= 0f) return 0f;
            float before = Stamina;
            Stamina = Mathf.Min(maxStamina, Stamina + amount);
            staminaTimer = 0f;
            return Stamina - before;
        }

        /// <summary>Brings a dead character back with the given fraction of max health (checkpoint respawn).</summary>
        public void Revive(float healthFraction = 1f)
        {
            Health = Mathf.Clamp(maxHealth * healthFraction, 1f, maxHealth);
            Stamina = maxStamina;
            Poise = maxPoise;
            staminaTimer = poiseTimer = 0f;
            if (HealthChanged != null) HealthChanged(Health, Health);
        }

        void ApplyHealth(float value)
        {
            float delta = value - Health;
            if (Mathf.Approximately(delta, 0f)) return;
            Health = value;
            if (HealthChanged != null) HealthChanged(Health, delta);
            if (Health <= 0f && Died != null) Died();
        }

        public void AddAdrenaline(float amount) { Adrenaline = Mathf.Clamp(Adrenaline + amount, 0f, AdrenalineMax); }

        public int AdrenalineSegmentsFull { get { return Mathf.FloorToInt(Adrenaline / adrenalinePerSegment); } }

        public bool SpendAdrenalineSegment()
        {
            if (Adrenaline < adrenalinePerSegment) return false;
            Adrenaline -= adrenalinePerSegment;
            return true;
        }
    }
}
