using UnityEngine;
using UnityEngine.InputSystem;

namespace GeorgianKnight
{
    /// <summary>
    /// Keyboard/mouse + gamepad bindings built in code (no generated asset to keep in sync).
    ///   Move WASD / LS · Look mouse / RS · Light LMB / X · Heavy (hold = charge) RMB / Y
    ///   Block-Parry Q / LB · Dodge Space / A · Jump Left Alt / RT · Sprint Shift / L3 · Lock-on MMB, Tab / R3
    ///   Style 1 / d-pad left (sword and shield), 2 / d-pad right (sword alone)
    ///   Kick F / RB · Interact E / B · War Cry R / LT · Pause Esc / Start
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class GKInput : MonoBehaviour
    {
        public const float DefaultMouseSensitivity = 0.11f;
        public const float DefaultStickSensitivity = 160f;
        public float mouseSensitivity = DefaultMouseSensitivity;
        public float stickSensitivity = DefaultStickSensitivity;
        public bool invertY;

        public Vector2 Move { get; private set; }
        public Vector2 Look { get; private set; }
        public bool BlockHeld { get; private set; }
        public bool SprintHeld { get; private set; }
        public bool HeavyHeld { get; private set; }
        public bool UsingGamepad { get; private set; }

        public bool LightPressed { get; private set; }
        public bool HeavyPressed { get; private set; }
        public bool DodgePressed { get; private set; }
        public bool JumpPressed { get; private set; }
        public bool LockOnPressed { get; private set; }
        public bool KickPressed { get; private set; }
        public bool WarCryPressed { get; private set; }
        public bool PausePressed { get; private set; }
        public bool InteractPressed { get; private set; }
        public bool InteractHeld { get; private set; }
        public bool InventoryPressed { get; private set; }
        public bool MapPressed { get; private set; }
        public bool JournalPressed { get; private set; }
        public bool SkillsPressed { get; private set; }
        public bool UseItemPressed { get; private set; }
        /// <summary>Sheathe / draw this frame: hold 1, or hold d-pad down, for <see cref="holdToSheathe"/> s (H is free).</summary>
        public bool HolsterPressed { get; private set; }
        /// <summary>Kept for old callers; the weapon slots replaced the style keys (S1), so it stays 0.</summary>
        public int StylePressed { get; private set; }
        /// <summary>A weapon slot picked this frame (C18 / CONTROLS §1a): 1 melee A (1, on release of a tap), 2 melee B
        /// (2), 3 bow stance (3), 4 cycle A / B (d-pad down, on release of a tap); 0 none.</summary>
        public int SlotPressed { get; private set; }
        /// <summary>The kick button (RB) is held on the pad: X pressed while it is held is the War Cry chord.</summary>
        public bool KickHeldPad { get; private set; }
        [Tooltip("Seconds 1 or d-pad down must be held to sheathe / draw (a shorter tap picks the slot).")]
        public float holdToSheathe = 0.4f;
        /// <summary>-1 / +1 when the quick-item selection is cycled (mouse wheel, d-pad left/right).</summary>
        public int CycleItem { get; private set; }
        /// <summary>Horizontal flick used to switch lock-on targets (-1, 0, +1).</summary>
        public int SwitchTarget { get; private set; }

        InputActionMap map;
        InputAction move, lookMouse, lookStick, lightAtk, heavy, block, dodge, jump, sprint, lockOn, kick, warCry, pause;
        InputAction interact, inventory, mapA, journal, skills, useItem, cycleWheel, cycleLeft, cycleRight, holster;
        InputAction slotA, slotB, bowStance, padSlot;
        float slotAHeld = -1f, padSlotHeld = -1f; bool slotAUsed, padSlotUsed;
        float switchCooldown;
        float flickAccum;

        void Awake()
        {
            map = new InputActionMap("Gameplay");
            move = map.AddAction("Move", InputActionType.Value);
            move.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s").With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            move.AddBinding("<Gamepad>/leftStick");

            lookMouse = map.AddAction("LookMouse", InputActionType.Value, "<Mouse>/delta");
            lookStick = map.AddAction("LookStick", InputActionType.Value, "<Gamepad>/rightStick");

            lightAtk = Button("Light", "<Mouse>/leftButton", "<Gamepad>/buttonWest");
            heavy = Button("Heavy", "<Mouse>/rightButton", "<Gamepad>/buttonNorth");
            block = Button("Block", "<Keyboard>/q", "<Gamepad>/leftShoulder");
            // Space jumps (as in Assassin's Creed); the dodge moved to Left Alt. Gamepad: A dodges, right trigger jumps.
            // dodge on Space (Alt sits on Alt+Tab / Alt+F4); jump, unused in the M1 slice, on Left Alt (UI controls spec)
            dodge = Button("Dodge", "<Keyboard>/space", "<Gamepad>/buttonSouth");
            jump = Button("Jump", "<Keyboard>/leftAlt", "<Gamepad>/rightTrigger");
            sprint = Button("Sprint", "<Keyboard>/leftShift", "<Gamepad>/leftStickPress");
            lockOn = Button("LockOn", "<Mouse>/middleButton", "<Gamepad>/rightStickPress");
            lockOn.AddBinding("<Keyboard>/tab");
            kick = Button("Kick", "<Keyboard>/f", "<Gamepad>/rightShoulder");
            interact = Button("Interact", "<Keyboard>/e", "<Gamepad>/buttonEast");
            inventory = map.AddAction("Inventory", InputActionType.Button, "<Keyboard>/i");
            mapA = Button("Map", "<Keyboard>/m", "<Gamepad>/select");
            journal = map.AddAction("Journal", InputActionType.Button, "<Keyboard>/j");
            skills = map.AddAction("Skills", InputActionType.Button, "<Keyboard>/k");
            useItem = Button("UseItem", "<Keyboard>/x", "<Gamepad>/dpad/up");
            // sheathe / draw is a hold of 1 or d-pad down now (CONTROLS §1a): this action is left unbound (H is free)
            holster = map.AddAction("Holster", InputActionType.Button);
            cycleWheel = map.AddAction("CycleWheel", InputActionType.Value, "<Mouse>/scroll/y");
            // M1: the d-pad's left / right pick the fighting style; item cycling (M2) keeps Z / C until it gets pad keys
            cycleLeft = Button("CycleLeft", "<Keyboard>/z", "<Gamepad>/dpad/left");
            cycleRight = Button("CycleRight", "<Keyboard>/c", "<Gamepad>/dpad/right");
            // the weapon slots (S1, C18 MainA / MainB / Bow; CONTROLS §1a): 1 / 2 / 3, the pad cycles A / B on d-pad down
            slotA = map.AddAction("SlotA", InputActionType.Button, "<Keyboard>/1");
            slotB = map.AddAction("SlotB", InputActionType.Button, "<Keyboard>/2");
            bowStance = map.AddAction("BowStance", InputActionType.Button, "<Keyboard>/3");
            padSlot = map.AddAction("SlotCycle", InputActionType.Button, "<Gamepad>/dpad/down");
            // War Cry: R on the keyboard; on the pad the chord RB + X (PlayerController), so LT is free for the bow
            warCry = map.AddAction("WarCry", InputActionType.Button, "<Keyboard>/r");
            pause = Button("Pause", "<Keyboard>/escape", "<Gamepad>/start");
        }

        /// <summary>A key that is a tap or a hold: held for holdToSheathe it is the sheathe (true, once); let go sooner it
        /// picks <paramref name="slot"/> on release (a tap cannot be told from the start of a hold at the press).</summary>
        bool HoldOrTap(InputAction a, ref float heldSince, ref bool used, int slot)
        {
            if (a.WasPressedThisFrame()) { heldSince = Time.unscaledTime; used = false; }
            bool sheathe = false;
            if (heldSince >= 0f && a.IsPressed() && !used && Time.unscaledTime - heldSince >= holdToSheathe) { used = true; sheathe = true; }
            if (heldSince >= 0f && a.WasReleasedThisFrame())
            {
                if (!used && SlotPressed == 0) SlotPressed = slot;
                heldSince = -1f;
            }
            return sheathe;
        }

        InputAction Button(string name, string kb, string pad)
        {
            InputAction a = map.AddAction(name, InputActionType.Button, kb);
            a.AddBinding(pad);
            return a;
        }

        void OnEnable() { if (map != null) map.Enable(); }
        void OnDisable() { if (map != null) map.Disable(); }
        void OnDestroy() { if (map != null) map.Dispose(); }

        // Gamepad: Start opens the pause menu, whose tabs hold inventory / skills / journal / map (Select opens the map).
        void Update()
        {
            // the swing-speed A/B (SwingTempo) for the Director: F8, in the test sandboxes only
            if (!ignoreDevices && Keyboard.current != null && Keyboard.current.f8Key.wasPressedThisFrame && gameObject.scene.name.Contains("Sandbox"))
                SwingTempo.Set(!SwingTempo.B);
            Move = Vector2.ClampMagnitude(move.ReadValue<Vector2>(), 1f);

            Vector2 m = lookMouse.ReadValue<Vector2>() * mouseSensitivity;
            Vector2 s = lookStick.ReadValue<Vector2>();
            if (s.sqrMagnitude > 0.02f) UsingGamepad = true;
            if (m.sqrMagnitude > 0.5f) UsingGamepad = false;
            Vector2 l = m + s * stickSensitivity * Time.unscaledDeltaTime;
            if (invertY) l.y = -l.y;
            Look = l;

            BlockHeld = block.IsPressed();
            SprintHeld = sprint.IsPressed();
            HeavyHeld = heavy.IsPressed();
            LightPressed = lightAtk.WasPressedThisFrame();
            HeavyPressed = heavy.WasPressedThisFrame();
            DodgePressed = dodge.WasPressedThisFrame();
            JumpPressed = jump.WasPressedThisFrame();
            LockOnPressed = lockOn.WasPressedThisFrame();
            KickPressed = kick.WasPressedThisFrame();
            WarCryPressed = warCry.WasPressedThisFrame();
            PausePressed = pause.WasPressedThisFrame();
            InteractPressed = interact.WasPressedThisFrame();
            InteractHeld = interact.IsPressed();
            InventoryPressed = inventory.WasPressedThisFrame();
            MapPressed = mapA.WasPressedThisFrame();
            JournalPressed = journal.WasPressedThisFrame();
            SkillsPressed = skills.WasPressedThisFrame();
            UseItemPressed = useItem.WasPressedThisFrame();
            StylePressed = 0;
            SlotPressed = bowStance.WasPressedThisFrame() ? 3 : slotB.WasPressedThisFrame() ? 2 : 0;
            HolsterPressed = HoldOrTap(slotA, ref slotAHeld, ref slotAUsed, 1) | HoldOrTap(padSlot, ref padSlotHeld, ref padSlotUsed, 4);
            var kc = kick.activeControl;
            KickHeldPad = kick.IsPressed() && kc != null && kc.device is Gamepad;
            if (ignoreDevices) ClearDevices();
            ApplyInjection();
            float wheel = cycleWheel.ReadValue<float>();
            CycleItem = cycleLeft.WasPressedThisFrame() || wheel < -0.1f ? -1 : (cycleRight.WasPressedThisFrame() || wheel > 0.1f ? 1 : 0);

            // target switching: mouse flick or right-stick flick while locked on
            SwitchTarget = 0;
            switchCooldown -= Time.unscaledDeltaTime;
            float x = UsingGamepad ? s.x : m.x * 0.12f;
            flickAccum = Mathf.Lerp(flickAccum, x, 0.5f);
            if (switchCooldown <= 0f && Mathf.Abs(flickAccum) > 0.75f)
            {
                SwitchTarget = flickAccum > 0f ? 1 : -1;
                switchCooldown = 0.35f;
                flickAccum = 0f;
            }
        }

        // ------------------------------------------------------------------ bot / QA injection
        /// <summary>Buttons a bot or QA probe can hold through the same path the player's input takes.</summary>
        public enum Injected { None, Light, Heavy, Block, Dodge, Sprint, Kick, Interact, LockOn, Jump, SlotA, SlotB, BowStance, Holster }
        Injected injectedHeld; bool injectedFresh;

        /// <summary>Presses (held=true) or releases an action as if the player did it.</summary>
        public void Inject(Injected button, bool held)
        {
            if (held) { injectedHeld = button; injectedFresh = true; }
            else if (injectedHeld == button) injectedHeld = Injected.None;
        }

        Vector2 injectedMove; bool moveInjected;

        /// <summary>Holds the move stick at <paramref name="v"/> (QA climbs, bots) until called with on = false.</summary>
        public void InjectMove(Vector2 v, bool on) { injectedMove = Vector2.ClampMagnitude(v, 1f); moveInjected = on; }

        /// <summary>Scripted takes (QA films): only injected input drives the player, so a stray click or key while
        /// the editor has focus cannot start an attack mid-test.</summary>
        [System.NonSerialized] public bool ignoreDevices;

        void ClearDevices()
        {
            Move = Vector2.zero; Look = Vector2.zero;
            BlockHeld = SprintHeld = HeavyHeld = InteractHeld = false;
            LightPressed = HeavyPressed = DodgePressed = JumpPressed = LockOnPressed = KickPressed = WarCryPressed = false;
            PausePressed = InteractPressed = InventoryPressed = MapPressed = JournalPressed = SkillsPressed = UseItemPressed = HolsterPressed = false;
            StylePressed = 0; SlotPressed = 0; KickHeldPad = false;
        }

        void ApplyInjection()
        {
            if (moveInjected) Move = injectedMove;
            if (injectedHeld == Injected.None) return;
            bool fresh = injectedFresh; injectedFresh = false;
            switch (injectedHeld)
            {
                case Injected.Light: if (fresh) LightPressed = true; break;
                case Injected.Heavy: HeavyHeld = true; if (fresh) HeavyPressed = true; break;
                case Injected.Block: BlockHeld = true; break;
                case Injected.Dodge: if (fresh) DodgePressed = true; break;
                case Injected.Sprint: SprintHeld = true; break;
                case Injected.Kick: if (fresh) KickPressed = true; break;
                case Injected.Jump: if (fresh) JumpPressed = true; break;
                case Injected.Interact: InteractHeld = true; if (fresh) InteractPressed = true; break;
                case Injected.LockOn: if (fresh) LockOnPressed = true; break;
                case Injected.SlotA: if (fresh) SlotPressed = 1; break;
                case Injected.SlotB: if (fresh) SlotPressed = 2; break;
                case Injected.BowStance: if (fresh) SlotPressed = 3; break;
                case Injected.Holster: if (fresh) HolsterPressed = true; break;
            }
        }

        public void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}
