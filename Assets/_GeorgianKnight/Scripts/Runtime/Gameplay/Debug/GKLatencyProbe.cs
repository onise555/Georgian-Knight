using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace GeorgianKnight
{
    /// <summary>
    /// Input-to-response QA. Presses a key / mouse button on a virtual device (exactly like a player would) and
    /// measures, in frames and milliseconds of game time: when the gameplay logic reacts (state change) and when the
    /// player can SEE it (animation weight / sword motion). Started by the bridge command "latency &lt;action&gt;".
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class GKLatencyProbe : MonoBehaviour
    {
        public enum Action { None, Block, Light, Heavy, Dodge, Kick }
        [Tooltip("Inject through GKInput (bot path). Device events are not processed while the editor steps frames remotely.")]
        public bool useInjection = true;

        Keyboard kb; Mouse mouse;
        Combatant self; Animator anim;
        Transform tip, hips;
        Action action;
        int frame; float elapsed;
        Vector3 tipStart;
        int logicFrame = -1, visibleFrame = -1, fullFrame = -1;
        float logicMs, visibleMs, fullMs;
        int holdFrames;
        readonly StringBuilder results = new StringBuilder();

        public string Results { get { return results.ToString(); } }
        public bool Busy { get { return action != Action.None; } }

        InputSettings.BackgroundBehavior prevBackground;
#if UNITY_EDITOR
        InputSettings.EditorInputBehaviorInPlayMode prevEditor;
#endif

        void Awake()
        {
            // remote play-tests run with the editor unfocused: let virtual device input reach the game regardless
            prevBackground = InputSystem.settings.backgroundBehavior;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
            prevEditor = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
            self = GetComponent<Combatant>();
            anim = GetComponent<Animator>();
            hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            kb = InputSystem.GetDevice<Keyboard>("GKVirtualKeyboard") ?? InputSystem.AddDevice<Keyboard>("GKVirtualKeyboard");
            mouse = InputSystem.GetDevice<Mouse>("GKVirtualMouse") ?? InputSystem.AddDevice<Mouse>("GKVirtualMouse");
        }

        void OnDestroy()
        {
            InputSystem.settings.backgroundBehavior = prevBackground;
#if UNITY_EDITOR
            InputSystem.settings.editorInputBehaviorInPlayMode = prevEditor;
#endif
            if (kb != null) InputSystem.RemoveDevice(kb);
            if (mouse != null) InputSystem.RemoveDevice(mouse);
        }

        public void Begin(Action a)
        {
            if (self == null) return;
            if (action != Action.None) results.AppendLine(action + " interrupted (measurement still running when the next one started)");
            action = a;
            frame = 0; elapsed = 0f; logicFrame = visibleFrame = fullFrame = -1;
            holdFrames = a == Action.Block || a == Action.Heavy ? 40 : 2;
            var weapon = self.weapon;
            tip = weapon != null ? weapon.bladeTip : null;
            tipStart = tip != null ? hips.InverseTransformPoint(tip.position) : Vector3.zero;
            Press(true);
        }

        GKInput input;

        void Press(bool down)
        {
            if (input == null) input = GetComponent<GKInput>();
            if (useInjection && input != null)
            {
                GKInput.Injected b = action == Action.Block ? GKInput.Injected.Block : action == Action.Light ? GKInput.Injected.Light :
                                     action == Action.Heavy ? GKInput.Injected.Heavy : action == Action.Dodge ? GKInput.Injected.Dodge : GKInput.Injected.Kick;
                input.Inject(b, down);
                return;
            }
            switch (action)
            {
                // InputState.Change applies immediately (queued events are not processed while the editor steps frames)
                case Action.Block: InputState.Change(kb, down ? new KeyboardState(Key.Q) : new KeyboardState()); break;
                case Action.Dodge: InputState.Change(kb, down ? new KeyboardState(Key.Space) : new KeyboardState()); break;
                case Action.Kick: InputState.Change(kb, down ? new KeyboardState(Key.F) : new KeyboardState()); break;
                case Action.Light: InputState.Change(mouse, new MouseState { buttons = (ushort)(down ? 1 : 0) }); break;
                case Action.Heavy: InputState.Change(mouse, new MouseState { buttons = (ushort)(down ? 2 : 0) }); break;
            }
        }

        void LateUpdate()
        {
            if (action == Action.None) return;
            frame++;
            elapsed += Time.deltaTime * 1000f;
            if (frame == holdFrames) Press(false);

            bool logic = false, visible = false, full = false;
            switch (action)
            {
                case Action.Block:
                    logic = self.IsBlocking;
                    int bl = anim.GetLayerIndex(GK.LayerBlock);
                    float w = bl >= 0 ? anim.GetLayerWeight(bl) : 0f;
                    visible = w >= 0.5f; full = w >= 0.95f;
                    break;
                case Action.Light: case Action.Heavy: case Action.Kick: case Action.Dodge:
                    logic = self.CurrentState == (action == Action.Dodge ? Combatant.State.Dodging : Combatant.State.Attacking);
                    float moved = tip != null ? (hips.InverseTransformPoint(tip.position) - tipStart).magnitude : 0f;
                    visible = action == Action.Dodge ? GK.Flat(self.Motor.Velocity).magnitude > 0.5f || anim.IsInTransition(0) : moved > 0.05f;
                    full = action == Action.Dodge ? GK.Flat(self.Motor.Velocity).magnitude > 2f : moved > 0.25f;
                    break;
            }
            if (logic && logicFrame < 0) { logicFrame = frame; logicMs = elapsed; }
            if (visible && visibleFrame < 0) { visibleFrame = frame; visibleMs = elapsed; }
            if (full && fullFrame < 0) { fullFrame = frame; fullMs = elapsed; }

            if ((logicFrame >= 0 && visibleFrame >= 0 && fullFrame >= 0) || frame > 120)
            {
                if (frame <= holdFrames) Press(false);
                results.AppendLine(string.Format("{0,-6} logic {1} visible {2} full {3}   (avg frame {4:F1} ms)", action,
                    Fmt(logicFrame, logicMs), Fmt(visibleFrame, visibleMs), Fmt(fullFrame, fullMs), elapsed / Mathf.Max(1, frame)));
                action = Action.None;
            }
        }

        static string Fmt(int f, float ms) { return f < 0 ? "  never  " : string.Format("{0,2}f/{1,4:F0}ms", f, ms); }
    }
}
