using System;
using System.Collections.Generic;
using GeorgianKnight.Core;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// What the knight carries in his slots (C18) and the switch between his two melee slots and the bow (S1; GDD §10.2,
    /// CONTROLS §1a). UI and Gameplay Systems ask (<see cref="RequestEquip"/>, <see cref="RequestSwitch"/>) and listen
    /// (<see cref="Equipped"/>, <see cref="SwitchStarted"/>, <see cref="ActiveWeaponChanged"/>); only Combat moves the gear.
    /// A melee slot holds a weapon and whether the shield goes with it: for the sword that is the sword-and-shield or the
    /// sword-alone style of <see cref="FightingStyles"/>, whose arm moves carry the shield between arm and back. A switch
    /// asked for mid-blow waits for the blow to end (the chain breaks; the Unbroken Chain skill will keep it, GDD §10.2).
    /// </summary>
    public class Equipment : MonoBehaviour
    {
        [Serializable]
        public class Slot
        {
            public EquipSlot slot;
            public ItemData item;
            [Tooltip("Melee slots: the shield goes with this weapon (on the arm while it is out).")]
            public bool shieldOn;
        }

        public List<Slot> loadout = new List<Slot>();
        [Tooltip("Seconds a switch asked for mid-blow waits for the blow to end before it is dropped.")]
        public float queueFor = 1.2f;

        public EquipSlot ActiveMelee { get; private set; }
        /// <summary>The bow is up (stance): the melee slot waits behind it.</summary>
        public bool BowStance { get; private set; }
        public bool Switching { get { return switchingTo.HasValue; } }

        public event Action<EquipSlot, ItemData> Equipped;
        /// <summary>A switch has begun: the slot it goes to and how long it takes (s), for the quick bar's draw.</summary>
        public event Action<EquipSlot, float> SwitchStarted;
        public event Action<ItemData, ItemData> ActiveWeaponChanged;

        Combatant self;
        FightingStyles styles;
        GKInput input;
        EquipSlot? pending, switchingTo;
        float pendingAt;

        void Awake()
        {
            self = GetComponent<Combatant>();
            styles = GetComponent<FightingStyles>();
            input = GetComponent<GKInput>();
            if (loadout.Count == 0) DefaultLoadout();
        }

        void Start()
        {
            ActiveMelee = SlotForStyle(styles != null ? styles.Style : WeaponStyle.SwordShield);
            if (styles != null) styles.StyleChanged += OnStyleChanged;
        }

        void OnDestroy() { if (styles != null) styles.StyleChanged -= OnStyleChanged; }

        /// <summary>The M1 knight before any item data exists: his arming sword with the shield (A) and alone (B), no bow.</summary>
        void DefaultLoadout()
        {
            var sword = ScriptableObject.CreateInstance<WeaponData>();
            sword.id = "w_arming_sword"; sword.name = sword.id; sword.category = ItemCategory.Weapon; sword.weaponClass = WeaponClass.Sword;
            sword.sellable = false;
            loadout.Add(new Slot { slot = EquipSlot.MainA, item = sword, shieldOn = true });
            loadout.Add(new Slot { slot = EquipSlot.MainB, item = sword, shieldOn = false });
        }

        public ItemData Get(EquipSlot slot) { var s = Find(slot); return s != null ? s.item : null; }

        Slot Find(EquipSlot slot)
        {
            for (int i = 0; i < loadout.Count; i++) if (loadout[i].slot == slot) return loadout[i];
            return null;
        }

        /// <summary>Puts <paramref name="item"/> in <paramref name="slot"/> (null empties it). The weapon in hand changes
        /// at once only through a switch, so equipping the active melee slot re-draws it.</summary>
        public bool RequestEquip(EquipSlot slot, ItemData item)
        {
            var s = Find(slot);
            if (s == null) { s = new Slot { slot = slot }; loadout.Add(s); }
            if (s.item == item) return false;
            var was = s.item;
            s.item = item;
            if (Equipped != null) Equipped(slot, item);
            EventBus.Publish(new EquipmentChanged(slot, item != null ? item.id : "", was != null ? was.id : ""));
            if (slot == ActiveMelee && !BowStance && ActiveWeaponChanged != null) ActiveWeaponChanged(was, item);
            return true;
        }

        /// <summary>Melee A / B or the bow stance. True when the switch began or waits for the blow to end.</summary>
        public bool RequestSwitch(EquipSlot slot)
        {
            if (self == null || self.IsDead) return false;
            if (slot == EquipSlot.Bow)
            {
                if (Get(EquipSlot.Bow) == null) return false;     // no bow carried (the hero's bow is M3)
                BowStance = !BowStance;
                if (SwitchStarted != null) SwitchStarted(EquipSlot.Bow, 0f);
                if (ActiveWeaponChanged != null) ActiveWeaponChanged(BowStance ? Get(ActiveMelee) : Get(EquipSlot.Bow), BowStance ? Get(EquipSlot.Bow) : Get(ActiveMelee));
                return true;
            }
            if (slot != EquipSlot.MainA && slot != EquipSlot.MainB) return false;
            if (Get(slot) == null) return false;
            if (slot == ActiveMelee && !BowStance && !Switching) return false;
            if (!CanSwitchNow()) { pending = slot; pendingAt = Time.time; return true; }
            return Begin(slot);
        }

        bool CanSwitchNow()
        {
            return self.CurrentState == Combatant.State.Free && (styles == null || !styles.Busy) && (self.Holster == null || !self.Holster.IsBusy);
        }

        bool Begin(EquipSlot slot)
        {
            pending = null;
            if (BowStance) BowStance = false;
            var target = StyleFor(slot);
            if (styles == null || styles.Style == target)
            {
                // the same style (two slots alike): nothing to move, the slot changes at once
                var from = Get(ActiveMelee);
                ActiveMelee = slot;
                if (SwitchStarted != null) SwitchStarted(slot, 0f);
                if (ActiveWeaponChanged != null) ActiveWeaponChanged(from, Get(slot));
                return true;
            }
            float duration = styles.SwitchDuration(target);
            if (!styles.Select(target)) { pending = slot; pendingAt = Time.time; return true; }
            switchingTo = slot;
            if (SwitchStarted != null) SwitchStarted(slot, duration);
            return true;
        }

        WeaponStyle StyleFor(EquipSlot slot)
        {
            var s = Find(slot);
            return s != null && s.shieldOn ? WeaponStyle.SwordShield : WeaponStyle.Sword;
        }

        EquipSlot SlotForStyle(WeaponStyle style)
        {
            var a = Find(EquipSlot.MainA);
            if (a != null && a.item != null && StyleFor(EquipSlot.MainA) == style) return EquipSlot.MainA;
            var b = Find(EquipSlot.MainB);
            if (b != null && b.item != null && StyleFor(EquipSlot.MainB) == style) return EquipSlot.MainB;
            return EquipSlot.MainA;
        }

        // the style is applied when its move begins, but the weapon counts as changed when the move is done (the shield
        // is where the new slot wants it): ActiveWeaponChanged waits for the arm (switchqa: B -> A fired at 0.02 s)
        bool styleApplied;

        void OnStyleChanged(WeaponStyle style)
        {
            if (!switchingTo.HasValue) switchingTo = SlotForStyle(style);
            styleApplied = true;
        }

        void FinishSwitch()
        {
            var from = Get(ActiveMelee);
            ActiveMelee = switchingTo.Value;
            switchingTo = null; styleApplied = false;
            if (ActiveWeaponChanged != null) ActiveWeaponChanged(from, Get(ActiveMelee));
        }

        void Update()
        {
            if (self == null || self.IsDead) return;
            if (input != null && input.SlotPressed > 0)
            {
                switch (input.SlotPressed)
                {
                    case 1: RequestSwitch(EquipSlot.MainA); break;
                    case 2: RequestSwitch(EquipSlot.MainB); break;
                    case 3: RequestSwitch(EquipSlot.Bow); break;
                    case 4: RequestSwitch(ActiveMelee == EquipSlot.MainA ? EquipSlot.MainB : EquipSlot.MainA); break;
                }
            }
            if (pending.HasValue)
            {
                if (Time.time - pendingAt > queueFor) pending = null;
                else if (CanSwitchNow()) Begin(pending.Value);
            }
            if (switchingTo.HasValue && styles != null && !styles.Busy)
            {
                if (styleApplied && styles.Style == StyleFor(switchingTo.Value)) FinishSwitch();
                // a switch the style refused or cut short (a hit mid-move) clears itself once the arm is free again
                else if (styles.Style != StyleFor(switchingTo.Value)) { switchingTo = null; styleApplied = false; }
            }
        }
    }
}
