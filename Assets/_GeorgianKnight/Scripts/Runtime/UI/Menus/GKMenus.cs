using System;
using System.Collections;
using System.Collections.Generic;
using GeorgianKnight.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace GeorgianKnight.UI
{
    /// <summary>
    /// The M1 menu layer (Docs/UI/MENUS_M1.md): title, main menu, pause, settings, controls, credits, confirm, the end
    /// card, the loading screen and the intro's skip prompt, built in UI Toolkit from code in the design system's
    /// palette v2. Implements <see cref="IMenuController"/> (so GameBootstrap's prototype pause and R-restart step
    /// aside) and <see cref="ILoadingScreen"/> (SceneFlow fades through it).
    ///
    /// <para>Flow (§7): MainMenu → Intro_Cutscene (skip Esc / B) → Level_Didgori_Arena (HUD) → victory / defeat →
    /// fight again or main menu. Pause from the battle: resume, restart (confirm), settings, controls, main menu
    /// (confirm), quit (confirm).</para>
    ///
    /// <para>Input is read straight from the devices (keyboard, mouse, gamepad), so it works with time stopped and
    /// with the player's action map disabled; the mouse picks through the panel. Focus shows by inversion, never by
    /// hue alone. <c>-gknomenus</c> on the command line leaves the menus out (the prototype pause comes back).</para>
    /// </summary>
    public sealed class GKMenus : MonoBehaviour, IMenuController, ILoadingScreen
    {
        public const string IntroScene = "Intro_Cutscene";
        public const string BattleScene = "Level_Didgori_Arena";

        // palette v2 (MENUS_M1 §3)
        static readonly Color Ink950 = new Color32(0x0B, 0x0E, 0x10, 0xFF);
        static readonly Color Ink900 = new Color32(0x12, 0x17, 0x1A, 0xFF);
        static readonly Color PlateInk = new Color32(0x11, 0x15, 0x18, 0xFF);
        static readonly Color Bone50 = new Color32(0xF2, 0xEE, 0xE6, 0xFF);
        static readonly Color Bone200 = new Color32(0xC8, 0xC3, 0xB9, 0xFF);
        static readonly Color Bone400 = new Color32(0x96, 0x91, 0x8A, 0xFF);
        static readonly Color Crimson400 = new Color32(0xC9, 0x3A, 0x40, 0xFF);
        static readonly Color Crimson700 = new Color32(0x8A, 0x1C, 0x24, 0xFF);
        static readonly Color GiltHi = new Color32(0xD8, 0xC0, 0x8C, 0xFF);
        static readonly Color Gold = new Color32(0x9A, 0x7B, 0x3C, 0xFF);

        const float ScreenIn = 0.4f, LoadFadeIn = 0.3f, LoadFadeOut = 0.4f, EndDelay = 2.5f, IntroHold = 1.5f;

        enum Page { Title, Main, Pause, Settings, Controls, Credits, Confirm, End }

        sealed class Item
        {
            public VisualElement el;
            public Action submit;
            public Action<int> adjust;
            public VisualElement track;          // sliders: a click sets the value from the pointer
            public Action<float> setFraction;
            public Action<bool> paint;
        }

        static GKMenus instance;
        static bool sceneHooked;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            instance = null;
            if (sceneHooked) SceneManager.sceneLoaded -= OnSceneLoaded;
            sceneHooked = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-gknomenus") >= 0) return;
            if (instance != null) return;
            var go = new GameObject("[GKMenus]");
            go.SetActive(false);
            DontDestroyOnLoad(go);
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = MakePanel();
            instance = go.AddComponent<GKMenus>();
            Services.Register<IMenuController>(instance);
            Services.Register<ILoadingScreen>(instance);
            go.SetActive(true);
            SceneManager.sceneLoaded += OnSceneLoaded;
            sceneHooked = true;
        }

        static void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
            if (instance != null && m == LoadSceneMode.Single) instance.EnterScene(s.name);
        }

        static PanelSettings MakePanel()
        {
            // PanelSettings.OnEnable warns "No Theme Style Sheet" before the theme below can be set: mute that instant
            var filter = Debug.unityLogger.filterLogType;
            Debug.unityLogger.filterLogType = LogType.Error;
            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            Debug.unityLogger.filterLogType = filter;
            ps.name = "GKMenus Panel";
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(1920, 1080);
            ps.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            ps.match = 0.5f;
            ps.sortingOrder = 50;                                 // over the HUD (10)
            ps.themeStyleSheet = ScriptableObject.CreateInstance<ThemeStyleSheet>();   // every style is set in code
            return ps;
        }

        // ---------- state ----------
        Font serif, sans;
        Texture2D emblem, plate;
        TextAsset creditsText;
        VisualElement root, pages, loading, loadingBar, skip;
        Label skipKey, skipText, loadingText;

        readonly List<Page> stack = new List<Page>();
        readonly List<Item> items = new List<Item>();
        int focus = -1;
        int settingsTab;
        bool titleSeen, usingPad, ending, endWin, leaving;
        float endAt = -1f, animSince, introDoneAt = -1f, introSince, navNext, adjNext, creditsScroll;
        int navDir, adjDir, closedFrame = -1;
        Vector2 lastMouse;
        VisualElement anim, creditsList, langKa, langEn;
        string confirmTitle, confirmBody;
        Action confirmYes;
        string sceneName = "";

        WaveManager waves;
        PlayableDirector intro;
        IDisposable langSub;

        // ---------- IMenuController ----------
        public bool IsAnyMenuOpen { get { return stack.Count > 0 && stack[0] != Page.Title && stack[0] != Page.Main; } }

        public void Open(string screen)
        {
            if (screen == "pause") OpenPause();
            else if (screen == "death") OpenEnd(false);
            else Debug.LogWarning("[GKMenus] no screen '" + screen + "' in M1");
        }

        public void CloseAll()
        {
            if (stack.Count == 0) return;
            IGameStateMachine states;
            if (Services.TryGet(out states))
            {
                if (stack[0] == Page.Pause && states.Is(GameMode.Paused)) states.Pop(GameMode.Paused);
                if (stack[0] == Page.End) states.Pop(GameMode.Menu);
            }
            stack.Clear();
            closedFrame = Time.frameCount;
            Rebuild();
        }

        /// <summary>QA (bridge <c>ui_nav</c>): drives the menus without a device. down / up / left / right / submit /
        /// back / pause / tab &lt;n&gt; / lang. Returns what is open and focused afterwards.</summary>
        public static string Drive(string cmd)
        {
            var m = instance;
            if (m == null) return "no menus";
            var p = (cmd ?? "").Trim().Split(' ');
            if (p[0] == "skip" && m.intro != null) { m.skip.style.display = DisplayStyle.None; m.Load(BattleScene); return "skipping the intro"; }
            if (p[0] == "end") { m.ScheduleEnd(p.Length < 2 || p[1] != "defeat"); m.endAt = Time.unscaledTime; return "end card next frame"; }
            if (m.stack.Count == 0) { if (p[0] == "pause" && m.CanPause()) m.OpenPause(); return m.Describe(); }
            var top = m.stack[m.stack.Count - 1];
            switch (p[0])
            {
                case "down": if (m.items.Count > 0) m.SetFocus((Mathf.Max(m.focus, 0) + 1) % m.items.Count); break;
                case "up": if (m.items.Count > 0) m.SetFocus((Mathf.Max(m.focus, 0) - 1 + m.items.Count) % m.items.Count); break;
                case "left": case "right": if (m.focus >= 0 && m.items[m.focus].adjust != null) m.items[m.focus].adjust(p[0] == "left" ? -1 : 1); break;
                case "submit":
                    if (top == Page.Title) { m.titleSeen = true; m.stack[0] = Page.Main; m.Rebuild(); break; }
                    if (m.focus >= 0) Activate(m.items[m.focus]);
                    break;
                case "back": m.Back(); break;
                case "tab": int t; if (p.Length > 1 && int.TryParse(p[1], out t)) { m.settingsTab = Mathf.Clamp(t, 0, SettingsTabs.Length - 1); m.Rebuild(); } break;
                case "lang": m.SetLanguage(Loc.Language == "ka" ? "en" : "ka"); break;
            }
            return m.Describe();
        }

        string Describe()
        {
            if (stack.Count == 0) return "closed (scene " + sceneName + ")";
            var names = new List<string>();
            foreach (var s in stack) names.Add(s.ToString());
            return string.Join(" > ", names.ToArray()) + ", focus " + focus + "/" + items.Count;
        }

        // ---------- ILoadingScreen ----------
        public IEnumerator Show(string scene)
        {
            stack.Clear();                                    // SceneFlow sets the base to Loading, which clears overlays
            Rebuild();
            loading.style.display = DisplayStyle.Flex;
            loadingText.text = Loc.Get("ui.loading");
            loadingBar.style.width = Length.Percent(0f);
            for (float t = 0f; t < LoadFadeIn; t += Time.unscaledDeltaTime)
            {
                loading.style.opacity = Ease(t / LoadFadeIn);
                yield return null;
            }
            loading.style.opacity = 1f;
        }

        public void SetProgress(float p) { if (loadingBar != null) loadingBar.style.width = Length.Percent(Mathf.Clamp01(p) * 100f); }

        public IEnumerator Hide()
        {
            for (float t = 0f; t < LoadFadeOut; t += Time.unscaledDeltaTime)
            {
                loading.style.opacity = 1f - Ease(t / LoadFadeOut);
                yield return null;
            }
            loading.style.opacity = 0f;
            loading.style.display = DisplayStyle.None;
            leaving = false;
        }

        // ---------- life ----------
        void Start()
        {
            var style = Resources.Load<GKHudStyle>(GKHudStyle.ResourcePath);
            serif = style != null && style.serif != null ? style.serif : Font.CreateDynamicFontFromOSFont("Sylfaen", 32);
            sans = style != null && style.sans != null ? style.sans : serif;
            if (style != null) { emblem = style.emblem; plate = style.plate; creditsText = style.credits; }

            root = GetComponent<UIDocument>().rootVisualElement;
            Fill(root);
            root.pickingMode = PickingMode.Ignore;
            pages = Box(root);
            Fill(pages);
            BuildSkip();
            BuildLoading();
            langSub = EventBus.Subscribe<LanguageChanged>(e => { Rebuild(); RefreshSkip(); });
            EnterScene(SceneManager.GetActiveScene().name);
        }

        void OnDestroy()
        {
            if (langSub != null) langSub.Dispose();
            Unbind();
            if (instance == this)
            {
                Services.Unregister<IMenuController>(this);
                Services.Unregister<ILoadingScreen>(this);
                instance = null;
            }
        }

        void EnterScene(string name)
        {
            if (root == null) return;                         // Start runs EnterScene itself
            Unbind();
            sceneName = name;
            stack.Clear();
            lastFocus.Clear();
            sameTop = (Page)(-1);
            focus = -1;
            ending = false; endAt = -1f; introDoneAt = -1f; leaving = false;
            intro = null;
            skip.style.display = DisplayStyle.None;

            IGameStateMachine states;
            Services.TryGet(out states);
            string menuScene = MainMenuScene();
            if (name == menuScene)
            {
                if (states != null) states.SetBase(GameMode.MainMenu);
                stack.Add(titleSeen ? Page.Main : Page.Title);
            }
            else if (name == IntroScene)
            {
                intro = FindAnyObjectByType<PlayableDirector>();
                introSince = Time.unscaledTime;
                RefreshSkip();
            }
            else
            {
                // the battle's waves: found once per scene (sceneLoaded runs after the scene's Awake / OnEnable)
                waves = FindAnyObjectByType<WaveManager>();
                if (waves != null) { waves.Victory += OnVictory; waves.Defeat += OnDefeat; }
            }
            Rebuild();
        }

        void Unbind()
        {
            if (waves != null) { waves.Victory -= OnVictory; waves.Defeat -= OnDefeat; }
            waves = null;
        }

        void OnVictory() { ScheduleEnd(true); }
        void OnDefeat() { ScheduleEnd(false); }

        void ScheduleEnd(bool win)
        {
            if (ending) return;
            ending = true; endWin = win;
            endAt = Time.unscaledTime + EndDelay;            // let the last blow or the fall play out
        }

        static string MainMenuScene()
        {
            var cfg = GameRoot.Instance != null ? GameRoot.Instance.Config : null;
            return cfg != null && !string.IsNullOrEmpty(cfg.mainMenuScene) ? cfg.mainMenuScene : "MainMenu";
        }

        void Load(string scene)
        {
            if (leaving) return;
            leaving = true;
            ISceneFlow flow;
            if (Services.TryGet(out flow)) flow.Load(scene);
            else
            {
                IGameStateMachine states;
                if (Services.TryGet(out states)) states.ClearOverlays();
                stack.Clear();
                Rebuild();
                SceneManager.LoadScene(scene);
            }
        }

        static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ---------- per frame ----------
        void Update()
        {
            if (root == null) return;
            float now = Time.unscaledTime;
            UpdateDevice();

            if (intro != null) UpdateIntro(now);

            if (ending && endAt > 0f && now >= endAt && !leaving)
            {
                endAt = -1f;
                if (stack.Count > 0 && stack[0] == Page.Pause) CloseAll();
                OpenEnd(endWin);
            }

            if (stack.Count == 0)
            {
                if (!ending && !leaving && intro == null && closedFrame != Time.frameCount && PausePressed() && CanPause()) OpenPause();
                return;
            }

            Animate(now);
            Navigate(now);
        }

        bool CanPause()
        {
            IGameStateMachine states;
            return Services.TryGet(out states) && states.InGameplay;
        }

        static bool PausePressed()
        {
            var kb = Keyboard.current; var gp = Gamepad.current;
            return (kb != null && kb.escapeKey.wasPressedThisFrame) || (gp != null && gp.startButton.wasPressedThisFrame);
        }

        void UpdateDevice()
        {
            var gp = Gamepad.current;
            bool padNow = usingPad;
            if (gp != null && (gp.buttonSouth.wasPressedThisFrame || gp.buttonEast.wasPressedThisFrame || gp.startButton.wasPressedThisFrame ||
                               gp.dpad.ReadValue().sqrMagnitude > 0.25f || gp.leftStick.ReadValue().sqrMagnitude > 0.25f)) padNow = true;
            var kb = Keyboard.current; var ms = Mouse.current;
            if ((kb != null && kb.anyKey.wasPressedThisFrame) || (ms != null && (ms.delta.ReadValue().sqrMagnitude > 4f || ms.leftButton.wasPressedThisFrame))) padNow = false;
            if (padNow == usingPad) return;
            usingPad = padNow;
            if (stack.Count > 0) Rebuild();
            RefreshSkip();
        }

        // ---------- the intro ----------
        void UpdateIntro(float now)
        {
            if (leaving) return;
            float shown = Mathf.Clamp01((now - introSince - 1f) / 0.4f);   // the prompt comes in after a second
            skip.style.opacity = shown;
            var kb = Keyboard.current; var gp = Gamepad.current;
            bool skipPressed = (kb != null && (kb.escapeKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame)) ||
                               (gp != null && (gp.buttonEast.wasPressedThisFrame || gp.startButton.wasPressedThisFrame));
            if (skipPressed && now - introSince > 0.5f) { skip.style.display = DisplayStyle.None; Load(BattleScene); return; }
            if (intro.duration > 0.0 && intro.time >= intro.duration - 0.05)
            {
                if (introDoneAt < 0f) introDoneAt = now;
                else if (now - introDoneAt >= IntroHold) { skip.style.display = DisplayStyle.None; Load(BattleScene); }
            }
        }

        void BuildSkip()
        {
            skip = Box(root);
            // above the film's letterbox: the intro's uGUI overlay (bars, fade) draws over this panel
            Place(skip, float.NaN, float.NaN, 96f, 168f);
            skip.style.flexDirection = FlexDirection.Row;
            skip.style.alignItems = Align.Center;
            skipKey = Key(skip);
            skipKey.style.marginRight = 12f;
            skipText = Text(skip, sans, 24f, Bone200);
            skip.style.display = DisplayStyle.None;
        }

        void RefreshSkip()
        {
            if (skip == null) return;
            bool on = sceneName == IntroScene && intro != null && !leaving;
            skip.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            if (!on) return;
            skipKey.text = usingPad ? (Gamepad.current is DualShockGamepad ? "○" : "B") : "Esc";
            skipText.text = StripKey(Loc.Get("ui.prompt.skip"));
        }

        void BuildLoading()
        {
            loading = Box(root);
            Fill(loading);
            loading.style.backgroundColor = Ink950;
            loading.style.justifyContent = Justify.FlexEnd;
            var col = Box(loading);
            col.style.marginLeft = 160f; col.style.marginBottom = 120f;
            var eyebrow = Text(col, sans, 24f, GiltHi);
            eyebrow.text = "";
            loadingText = Text(col, serif, 40f, Bone50);
            loadingText.style.marginTop = 8f;
            var track = Box(col);
            track.style.width = 320f; track.style.height = 2f; track.style.marginTop = 24f;
            track.style.backgroundColor = new Color(Bone50.r, Bone50.g, Bone50.b, 0.14f);
            loadingBar = Box(track);
            Place(loadingBar, 0f, 0f, float.NaN, 0f);
            loadingBar.style.backgroundColor = Crimson400;
            loading.style.display = DisplayStyle.None;
            loading.style.opacity = 0f;
        }

        // ---------- opening pages ----------
        void OpenPause()
        {
            if (stack.Count > 0) return;
            IGameStateMachine states;
            if (Services.TryGet(out states)) states.Push(GameMode.Paused);     // time stops at once (C5)
            stack.Add(Page.Pause);
            Rebuild();
        }

        void OpenEnd(bool win)
        {
            if (stack.Count > 0 && stack[0] == Page.End) return;
            CloseAll();
            endWin = win;
            IGameStateMachine states;
            if (Services.TryGet(out states)) states.Push(GameMode.Menu);
            stack.Add(Page.End);
            Rebuild();
        }

        void Push(Page p) { stack.Add(p); Rebuild(); }

        void Back()
        {
            if (stack.Count == 0) return;
            var top = stack[stack.Count - 1];
            if (top == Page.Title || top == Page.Main || top == Page.End) return;    // roots are left through a button
            if (stack.Count == 1 && top == Page.Pause) { CloseAll(); return; }
            stack.RemoveAt(stack.Count - 1);
            Rebuild();
        }

        void Confirm(string titleKey, string bodyKey, Action yes)
        {
            confirmTitle = titleKey; confirmBody = bodyKey; confirmYes = yes;
            Push(Page.Confirm);
        }

        // ---------- navigation ----------
        void Navigate(float now)
        {
            var kb = Keyboard.current; var gp = Gamepad.current; var ms = Mouse.current;
            var top = stack[stack.Count - 1];
            if (top == Page.Title) { LeaveTitle(kb, gp, ms); return; }
            if (Shortcuts(top, kb, gp)) return;
            if (top == Page.Credits) ScrollCredits(kb, gp);
            if (ms != null && Pointer(ms)) return;
            if (items.Count > 0) Keys(kb, gp, now);
        }

        void LeaveTitle(Keyboard kb, Gamepad gp, Mouse ms)
        {
            bool any = (kb != null && kb.anyKey.wasPressedThisFrame) || (ms != null && ms.leftButton.wasPressedThisFrame) ||
                       Pressed(null, gp != null ? gp.buttonSouth : null) || Pressed(null, gp != null ? gp.startButton : null);
            if (!any) return;
            titleSeen = true;
            stack[0] = Page.Main;
            Rebuild();
        }

        // back, the language (Tab / Y on the main menu, §7) and the settings tabs (Q / E, LB / RB); true = handled
        bool Shortcuts(Page top, Keyboard kb, Gamepad gp)
        {
            if (Pressed(kb != null ? kb.escapeKey : null, gp != null ? gp.buttonEast : null) ||
                (top == Page.Pause && Pressed(null, gp != null ? gp.startButton : null)))
            {
                Back();
                return true;
            }
            if (top == Page.Main && Pressed(kb != null ? kb.tabKey : null, gp != null ? gp.buttonNorth : null))
            {
                SetLanguage(Loc.Language == "ka" ? "en" : "ka");
                return true;
            }
            if (top != Page.Settings) return false;
            int tab = Pressed(kb != null ? kb.qKey : null, gp != null ? gp.leftShoulder : null) ? -1
                    : (Pressed(kb != null ? kb.eKey : null, gp != null ? gp.rightShoulder : null) ? 1 : 0);
            if (tab == 0) return false;
            SelectTab((settingsTab + tab + SettingsTabs.Length) % SettingsTabs.Length);
            return true;
        }

        static bool Pressed(ButtonControl a, ButtonControl b)
        {
            return (a != null && a.wasPressedThisFrame) || (b != null && b.wasPressedThisFrame);
        }

        void SelectTab(int tab)
        {
            settingsTab = tab;
            focus = 0;
            Rebuild();
        }

        // the credits roll on their own; up / down scroll them by hand
        void ScrollCredits(Keyboard kb, Gamepad gp)
        {
            if (creditsList == null) return;
            float v = Axis(kb, gp, true);
            creditsScroll += (v != 0f ? -v * 600f : 40f) * Time.unscaledDeltaTime;
            float h = creditsList.resolvedStyle.height;
            if ((!float.IsNaN(h) && h > 0f && creditsScroll > h) || creditsScroll < -600f) creditsScroll = -600f;
            creditsList.style.translate = new Translate(0f, -creditsScroll);
        }

        // mouse: hover focuses, a click activates (the pointer is picked through the panel); true = a click was used
        bool Pointer(Mouse ms)
        {
            Vector2 pos = ms.position.ReadValue();
            bool moved = (pos - lastMouse).sqrMagnitude > 4f;
            bool click = ms.leftButton.wasPressedThisFrame;
            lastMouse = pos;
            if (!moved && !click) return false;
            VisualElement hit;
            Vector2 pp;
            int i = PickItem(pos, out hit, out pp);
            if (i >= 0 && i != focus) SetFocus(i);
            if (!click) return false;
            if (i >= 0) { Click(items[i], pp); return true; }
            if (IsUnder(hit, langKa)) { SetLanguage("ka"); return true; }
            if (IsUnder(hit, langEn)) { SetLanguage("en"); return true; }
            int tab = TabUnder(hit);
            if (tab < 0) return false;
            SelectTab(tab);
            return true;
        }

        // a slider takes the value under the pointer; anything else activates
        static void Click(Item it, Vector2 panelPos)
        {
            if (it.track == null || it.setFraction == null) { Activate(it); return; }
            float w = it.track.resolvedStyle.width;
            if (w > 0f) it.setFraction(Mathf.Clamp01(it.track.WorldToLocal(panelPos).x / w));
        }

        static void Activate(Item it)
        {
            if (it.submit != null) it.submit();
            else if (it.adjust != null) it.adjust(1);
        }

        // up / down move the focus, left / right change a value (both repeat while held), submit activates
        void Keys(Keyboard kb, Gamepad gp, float now)
        {
            int dir = -Step(Axis(kb, gp, true));
            if (Repeat(dir, ref navDir, ref navNext, now)) SetFocus((Mathf.Max(focus, 0) + dir + items.Count) % items.Count);
            int adj = Step(Axis(kb, gp, false));
            if (Repeat(adj, ref adjDir, ref adjNext, now) && focus >= 0 && items[focus].adjust != null) items[focus].adjust(adj);
            bool submit = (kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame)) ||
                          Pressed(null, gp != null ? gp.buttonSouth : null);
            if (submit && focus >= 0) Activate(items[focus]);
        }

        static int Step(float axis) { return axis > 0f ? 1 : (axis < 0f ? -1 : 0); }

        // -1..1: vertical is up-positive, horizontal right-positive
        static float Axis(Keyboard kb, Gamepad gp, bool vertical)
        {
            float v = 0f;
            if (kb != null)
            {
                if (vertical)
                {
                    if (kb.upArrowKey.isPressed || kb.wKey.isPressed) v += 1f;
                    if (kb.downArrowKey.isPressed || kb.sKey.isPressed) v -= 1f;
                }
                else
                {
                    if (kb.rightArrowKey.isPressed || kb.dKey.isPressed) v += 1f;
                    if (kb.leftArrowKey.isPressed || kb.aKey.isPressed) v -= 1f;
                }
            }
            if (gp != null)
            {
                Vector2 d = gp.dpad.ReadValue() + gp.leftStick.ReadValue();
                float a = vertical ? d.y : d.x;
                if (Mathf.Abs(a) > 0.5f) v += Mathf.Sign(a);
            }
            return Mathf.Clamp(v, -1f, 1f);
        }

        static bool Repeat(int dir, ref int last, ref float next, float now)
        {
            if (dir == 0) { last = 0; return false; }
            if (dir != last) { last = dir; next = now + 0.4f; return true; }
            if (now < next) return false;
            next = now + 0.12f;
            return true;
        }

        int PickItem(Vector2 screen, out VisualElement hit, out Vector2 panelPos)
        {
            hit = null; panelPos = Vector2.zero;
            var panel = root.panel;
            if (panel == null) return -1;
            panelPos = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(screen.x, Screen.height - screen.y));
            hit = panel.Pick(panelPos);
            for (var e = hit; e != null; e = e.parent)
            {
                var it = e.userData as Item;
                if (it != null) return items.IndexOf(it);
            }
            return -1;
        }

        static bool IsUnder(VisualElement e, VisualElement target)
        {
            if (target == null) return false;
            for (; e != null; e = e.parent) if (e == target) return true;
            return false;
        }

        int TabUnder(VisualElement e)
        {
            for (; e != null; e = e.parent) if (e.userData is int) return (int)e.userData;
            return -1;
        }

        void SetFocus(int i)
        {
            if (focus >= 0 && focus < items.Count && items[focus].paint != null) items[focus].paint(false);
            focus = i;
            if (focus >= 0 && focus < items.Count && items[focus].paint != null) items[focus].paint(true);
        }

        void Animate(float now)
        {
            if (anim == null) return;
            float t = Mathf.Clamp01((now - animSince) / ScreenIn);
            float e = Ease(t);
            anim.style.opacity = e;
            anim.style.translate = new Translate(-16f * (1f - e), 0f);
            if (t >= 1f) anim = null;
        }

        static float Ease(float t) { t = Mathf.Clamp01(t); return 1f - (1f - t) * (1f - t) * (1f - t); }

        // ---------- settings ----------
        static ISettingsService Settings() { ISettingsService s; return Services.TryGet(out s) ? s : null; }

        void Change(Action<GameSettings> edit)
        {
            var svc = Settings();
            if (svc == null) return;
            var s = svc.Current.Clone();
            edit(s);
            svc.Commit(s);
        }

        void SetLanguage(string lang)
        {
            if (Loc.Language == lang) return;
            if (Settings() != null) Change(s => s.language = lang);     // Apply sets Loc and publishes LanguageChanged
            else Loc.SetLanguage(lang);
        }

        static readonly string[] SettingsTabs = { "ui.settings.tab.game", "ui.settings.tab.display", "ui.settings.tab.audio", "ui.settings.tab.controls", "ui.settings.tab.access" };

        // ---------- building ----------
        void Rebuild()
        {
            if (pages == null) return;
            if ((int)sameTop >= 0 && focus >= 0) lastFocus[sameTop] = focus;      // back returns to the entry you left
            pages.Clear();
            items.Clear();
            focus = -1;
            anim = null;
            creditsList = null; langKa = null; langEn = null;
            if (stack.Count == 0) return;

            var top = stack[stack.Count - 1];
            if (top == Page.Confirm && stack.Count > 1)
            {
                BuildPage(stack[stack.Count - 2]);           // the page under the dialog stays visible, not live
                items.Clear();
                creditsList = null;
                if (anim != null) { anim.style.opacity = 1f; anim.style.translate = new Translate(0f, 0f); anim = null; }
            }
            BuildPage(top);

            int f = top == Page.Confirm ? 1 : 0;              // a confirm starts on the safe choice ("No")
            int remembered;
            if (top != Page.Confirm && top != Page.End && lastFocus.TryGetValue(top, out remembered)) f = remembered;
            sameTop = top;
            if (items.Count > 0) SetFocus(Mathf.Clamp(f, 0, items.Count - 1));
        }

        Page sameTop = (Page)(-1);
        readonly Dictionary<Page, int> lastFocus = new Dictionary<Page, int>();

        void BuildPage(Page p)
        {
            switch (p)
            {
                case Page.Title: BuildTitle(); break;
                case Page.Main: BuildMain(); break;
                case Page.Pause: BuildPause(); break;
                case Page.Settings: BuildSettings(); break;
                case Page.Controls: BuildControls(); break;
                case Page.Credits: BuildCredits(); break;
                case Page.Confirm: BuildConfirm(); break;
                case Page.End: BuildEnd(); break;
            }
        }

        bool InMenuScene { get { return sceneName == MainMenuScene(); } }

        // the painted plate with a left falloff (main menu), or the game behind a dark scrim (in battle)
        VisualElement Backdrop(float scrim)
        {
            var bg = Box(pages);
            Fill(bg);
            if (InMenuScene)
            {
                bg.style.backgroundColor = Ink950;
                if (plate != null)
                {
                    var img = Box(bg);
                    Fill(img);
                    img.style.backgroundImage = new StyleBackground(plate);
                    img.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
                    img.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Right);
                }
                // the left falloff: strips of ink, a soft gradient without a texture
                var fall = Box(bg);
                Fill(fall);
                fall.style.flexDirection = FlexDirection.Row;
                for (int i = 0; i < 12; i++)
                {
                    var s = Box(fall);
                    s.style.width = Length.Percent(i < 11 ? 5f : 45f);
                    float a = i < 11 ? Mathf.Lerp(0.86f, 0.05f, i / 10f) : 0f;
                    s.style.backgroundColor = new Color(Ink950.r, Ink950.g, Ink950.b, a);
                }
                if (scrim > 0f) { var sc = Box(bg); Fill(sc); sc.style.backgroundColor = new Color(Ink950.r, Ink950.g, Ink950.b, scrim); }
            }
            else
            {
                bg.style.backgroundColor = new Color(Ink950.r, Ink950.g, Ink950.b, Mathf.Max(scrim, 0.72f));
            }
            return bg;
        }

        VisualElement Column(VisualElement parent, float top)
        {
            var col = Box(parent);
            Place(col, 160f, top, float.NaN, float.NaN);
            anim = col; animSince = Time.unscaledTime;
            col.style.opacity = 0f;
            return col;
        }

        void Logo(VisualElement parent, bool large)
        {
            if (emblem != null)
            {
                var em = Box(parent);
                em.style.width = 48f; em.style.height = 64f;
                em.style.backgroundImage = new StyleBackground(emblem);
                em.style.marginBottom = 40f;
            }
            var title = Text(parent, serif, large ? (Loc.Language == "ka" ? 104f : 112f) : 72f, Bone50);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.text = Loc.Get("game.title");
            var sub = Text(parent, serif, Loc.Language == "ka" ? 24f : 32f, GiltHi);
            sub.text = Loc.Language == "ka" ? "GEORGIAN KNIGHT" : "ქართველი რაინდი";
            if (Loc.Language == "ka") sub.style.letterSpacing = 8f;          // Latin caps only, tracked (§2)
            sub.style.marginTop = 4f;
            var rule = Box(parent);
            rule.style.flexDirection = FlexDirection.Row; rule.style.alignItems = Align.Center; rule.style.marginTop = 32f;
            var dot = Box(rule); dot.style.width = 8f; dot.style.height = 8f; dot.style.rotate = new Rotate(45f);
            Border(dot, 1f, Gold, 0f);
            var line = Box(rule); line.style.width = 440f; line.style.height = 1f; line.style.marginLeft = 12f;
            line.style.backgroundColor = new Color(Gold.r, Gold.g, Gold.b, 0.6f);
        }

        void BuildTitle()
        {
            Backdrop(0f);
            var col = Column(pages, 200f);
            Logo(col, true);
            var place = Text(col, sans, 24f, GiltHi);
            place.text = Loc.Get("ui.end.place");
            place.style.marginTop = 24f;
            var press = Text(pages, sans, 24f, Bone200);
            Place(press, 160f, float.NaN, float.NaN, 96f);
            press.text = Loc.Get("ui.title.press");
            Version();
        }

        void BuildMain()
        {
            Backdrop(0f);
            var col = Column(pages, 120f);
            Logo(col, true);
            var list = Box(col);
            list.style.marginTop = 96f;
            MenuEntry(list, Loc.Get("ui.main.start"), () => Load(IntroScene));
            MenuEntry(list, Loc.Get("ui.settings.title"), () => Push(Page.Settings));
            MenuEntry(list, Loc.Get("ui.main.credits"), () => { creditsScroll = -600f; Push(Page.Credits); });
            MenuEntry(list, Loc.Get("ui.end.quit"), () => Confirm("ui.confirm.quit.title", null, Quit));
            LanguageSwitch();
            Prompts(true, false, true, false);
            Version();
        }

        void Version()
        {
            var v = Text(pages, sans, 20f, Bone400);
            Place(v, float.NaN, float.NaN, 96f, 56f);
            v.text = "M1 · " + Loc.Format("ui.main.version", Application.version);
        }

        void LanguageSwitch()
        {
            var sw = Box(pages);
            Place(sw, float.NaN, 56f, 96f, float.NaN);
            sw.style.flexDirection = FlexDirection.Row; sw.style.alignItems = Align.Center;
            langKa = LangLabel(sw, "ka");
            var bar = Box(sw); bar.style.width = 1f; bar.style.height = 28f; bar.style.marginLeft = 24f; bar.style.marginRight = 24f;
            bar.style.backgroundColor = new Color(Bone50.r, Bone50.g, Bone50.b, 0.26f);
            langEn = LangLabel(sw, "en");
        }

        VisualElement LangLabel(VisualElement parent, string lang)
        {
            bool on = Loc.Language == lang;
            var box = Box(parent);
            box.pickingMode = PickingMode.Position;
            box.style.alignItems = Align.Center;
            var l = Text(box, sans, 24f, on ? Bone50 : Bone400);
            l.text = Loc.Get("ui.lang." + lang);                // each language in its own script (§7)
            var mark = Box(box); mark.style.width = 6f; mark.style.height = 6f; mark.style.rotate = new Rotate(45f); mark.style.marginTop = 6f;
            mark.style.backgroundColor = on ? Crimson400 : Color.clear;
            return box;
        }

        void BuildPause()
        {
            Backdrop(0.72f);
            var col = Column(pages, 160f);
            var eyebrow = Text(col, sans, 24f, GiltHi);
            eyebrow.text = Loc.Get("ui.end.place");
            var title = Text(col, serif, 40f, Bone50);
            title.text = Loc.Get("ui.pause.title");
            title.style.marginTop = 8f; title.style.marginBottom = 48f;
            MenuEntry(col, Loc.Get("ui.pause.resume"), CloseAll);
            MenuEntry(col, Loc.Get("ui.pause.restart"), () => Confirm("ui.confirm.restart.title", "ui.confirm.restart.body", () => Load(SceneManager.GetActiveScene().name)));
            MenuEntry(col, Loc.Get("ui.settings.title"), () => Push(Page.Settings));
            MenuEntry(col, Loc.Get("ui.pause.controls"), () => Push(Page.Controls));
            MenuEntry(col, Loc.Get("ui.menu.main_menu"), () => Confirm("ui.confirm.main_menu.title", "ui.confirm.main_menu.body", () => Load(MainMenuScene())));
            MenuEntry(col, Loc.Get("ui.pause.quit"), () => Confirm("ui.confirm.quit.title", null, Quit));

            // the battle card (right): the assault, its name, foes, time
            if (waves != null)
            {
                var card = Box(pages);
                Place(card, float.NaN, 160f, 160f, float.NaN);
                card.style.alignItems = Align.FlexEnd;
                card.style.borderRightWidth = 2f; card.style.borderRightColor = new Color(Gold.r, Gold.g, Gold.b, 0.7f);
                card.style.paddingRight = 24f;
                int wi = Mathf.Clamp(waves.WaveIndex, 0, Mathf.Max(0, waves.WaveCount - 1));
                var a = Text(card, sans, 24f, GiltHi);
                a.text = Loc.Format("ui.hud.wave", wi + 1, waves.WaveCount);
                var n = Text(card, serif, 40f, Bone50);
                n.text = Loc.GetOrNull("wave.didgori." + (wi + 1) + ".title") ?? "";
                n.style.marginTop = 8f;
                var f = Text(card, sans, 24f, Bone200);
                f.text = Loc.Format("ui.hud.foes", waves.AliveCount);
                f.style.marginTop = 16f;
                var t = Text(card, sans, 24f, Bone200);
                t.text = Loc.Get("ui.end.stat.time") + "  " + Clock(waves.ElapsedTime);
                t.style.marginTop = 4f;
            }
            Prompts(true, true, false, false);
        }

        void BuildSettings()
        {
            Backdrop(0.82f);
            var col = Column(pages, 120f);
            var title = Text(col, serif, 40f, Bone50);
            title.text = Loc.Get("ui.settings.title");
            title.style.marginBottom = 32f;

            // tabs
            var tabs = Box(col);
            tabs.style.flexDirection = FlexDirection.Row;
            tabs.style.marginBottom = 32f;
            for (int i = 0; i < SettingsTabs.Length; i++)
            {
                bool on = i == settingsTab;
                var tab = Box(tabs);
                tab.pickingMode = PickingMode.Position;
                tab.userData = i;
                tab.style.marginRight = 40f;
                var l = Text(tab, sans, 24f, on ? Bone50 : Bone400);
                l.text = Loc.Get(SettingsTabs[i]);
                var u = Box(tab); u.style.height = 2f; u.style.marginTop = 8f;
                u.style.backgroundColor = on ? Crimson400 : Color.clear;
            }

            var svc = Settings();
            if (svc == null)
            {
                var none = Text(col, sans, 24f, Bone400);
                none.text = "—";
                Prompts(true, true, false, true);
                return;
            }
            var s = svc.Current;
            var list = Box(col);
            list.style.width = 1000f;
            switch (settingsTab)
            {
                case 0: TabGame(list, s); break;
                case 1: TabDisplay(list, s); break;
                case 2: TabAudio(list, s); break;
                case 3: TabControls(list, s); break;
                default: TabAccess(list, s); break;
            }
            var defaults = Box(col);
            defaults.style.marginTop = 32f;
            defaults.style.flexDirection = FlexDirection.Row;
            Button(defaults, Loc.Get("ui.settings.defaults"), () => { var svc2 = Settings(); if (svc2 != null) svc2.ResetToDefaults(); Rebuild(); }, false);
            Prompts(true, true, false, true);
        }

        void TabGame(VisualElement list, GameSettings s)
        {
            string on_ = Loc.Get("ui.settings.on"), off_ = Loc.Get("ui.settings.off");
            Choice(list, Loc.Get("ui.settings.language.text"), new[] { Loc.Get("ui.lang.ka"), Loc.Get("ui.lang.en") },
                Loc.Language == "en" ? 1 : 0, v => SetLanguage(v == 1 ? "en" : "ka"));
            Choice(list, Loc.Get("ui.settings.difficulty"),
                new[] { Loc.Get("ui.settings.difficulty.pilgrim"), Loc.Get("ui.settings.difficulty.aznauri"), Loc.Get("ui.settings.difficulty.monaspa") },
                (int)s.difficulty, v => Change(x => x.difficulty = (Difficulty)v));
            Slider(list, Loc.Get("ui.settings.camera_shake"), s.cameraShake / 1.5f, v => Change(x => x.cameraShake = v * 1.5f));
            Choice(list, Loc.Get("ui.settings.slow_motion"), new[] { on_, off_ }, s.cinematicFinishers ? 0 : 1, v => Change(x => x.cinematicFinishers = v == 0));
        }

        void TabDisplay(VisualElement list, GameSettings s)
        {
            string on_ = Loc.Get("ui.settings.on"), off_ = Loc.Get("ui.settings.off");
            Choice(list, Loc.Get("ui.settings.quality"),
                new[] { Loc.Get("ui.settings.quality.low"), Loc.Get("ui.settings.quality.medium"), Loc.Get("ui.settings.quality.high"), Loc.Get("ui.settings.quality.ultra") },
                (int)s.quality, v => Change(x => x.quality = (QualityPreset)v));
            Choice(list, Loc.Get("ui.settings.fullscreen"), new[] { on_, off_ }, s.fullScreenMode == FullScreenMode.Windowed ? 1 : 0,
                v => Change(x => x.fullScreenMode = v == 0 ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed));
            Choice(list, Loc.Get("ui.settings.vsync"), new[] { on_, off_ }, s.vSync ? 0 : 1, v => Change(x => x.vSync = v == 0));
            Choice(list, Loc.Get("ui.settings.motion_blur"), new[] { on_, off_ }, s.motionBlur ? 0 : 1, v => Change(x => x.motionBlur = v == 0));
        }

        void TabAudio(VisualElement list, GameSettings s)
        {
            Slider(list, Loc.Get("ui.settings.volume.master"), s.masterVolume, v => Change(x => x.masterVolume = v));
            Slider(list, Loc.Get("ui.settings.volume.music"), s.musicVolume, v => Change(x => x.musicVolume = v));
            Slider(list, Loc.Get("ui.settings.volume.effects"), s.sfxVolume, v => Change(x => x.sfxVolume = v));
            Slider(list, Loc.Get("ui.settings.volume.dialogue"), s.voiceVolume, v => Change(x => x.voiceVolume = v));
            Slider(list, Loc.Get("ui.settings.volume.ambience"), s.ambienceVolume, v => Change(x => x.ambienceVolume = v));
        }

        void TabControls(VisualElement list, GameSettings s)
        {
            string on_ = Loc.Get("ui.settings.on"), off_ = Loc.Get("ui.settings.off");
            Slider(list, Loc.Get("ui.settings.sensitivity"), (s.mouseSensitivity - 0.1f) / 2.9f,
                v => Change(x => { x.mouseSensitivity = 0.1f + v * 2.9f; x.gamepadSensitivity = 0.1f + v * 2.9f; }));
            Choice(list, Loc.Get("ui.settings.invert_y"), new[] { on_, off_ }, s.invertY ? 0 : 1, v => Change(x => x.invertY = v == 0));
            Choice(list, Loc.Get("ui.action.sprint") + " · " + Loc.Get("ui.settings.hold_toggle"),
                new[] { Loc.Get("ui.settings.hold"), Loc.Get("ui.settings.toggle") }, s.toggleSprint ? 1 : 0, v => Change(x => x.toggleSprint = v == 1));
            Choice(list, Loc.Get("ui.action.block") + " · " + Loc.Get("ui.settings.hold_toggle"),
                new[] { Loc.Get("ui.settings.hold"), Loc.Get("ui.settings.toggle") }, s.toggleBlock ? 1 : 0, v => Change(x => x.toggleBlock = v == 1));
        }

        void TabAccess(VisualElement list, GameSettings s)
        {
            string on_ = Loc.Get("ui.settings.on"), off_ = Loc.Get("ui.settings.off");
            Choice(list, Loc.Get("ui.settings.subtitles"), new[] { on_, off_ }, s.subtitles ? 0 : 1, v => Change(x => x.subtitles = v == 0));
            Choice(list, Loc.Get("ui.settings.subtitles.size"),
                new[] { Loc.Get("ui.settings.size.small"), Loc.Get("ui.settings.size.medium"), Loc.Get("ui.settings.size.large") },
                s.subtitleScale < 0.9f ? 0 : (s.subtitleScale < 1.25f ? 1 : 2), v => Change(x => x.subtitleScale = v == 0 ? 0.8f : (v == 1 ? 1f : 1.5f)));
            Choice(list, Loc.Get("ui.settings.flashes"), new[] { Loc.Get("ui.settings.full"), Loc.Get("ui.settings.reduced") },
                s.reduceFlashes ? 1 : 0, v => Change(x => x.reduceFlashes = v == 1));
            Choice(list, Loc.Get("ui.settings.gore"), new[] { Loc.Get("ui.settings.full"), Loc.Get("ui.settings.reduced"), off_ },
                (int)s.gore, v => Change(x => x.gore = (GoreLevel)v));
        }

        void BuildControls()
        {
            Backdrop(0.82f);
            var col = Column(pages, 120f);
            var title = Text(col, serif, 40f, Bone50);
            title.text = Loc.Get("ui.pause.controls");
            var dev = Text(col, sans, 24f, GiltHi);
            dev.text = Loc.Get(usingPad ? "ui.controls.device.pad" : "ui.controls.device.kbm");
            dev.style.marginTop = 8f; dev.style.marginBottom = 32f;
            for (int i = 0; i < ControlRows.GetLength(0); i++)
            {
                var row = Box(col);
                row.style.flexDirection = FlexDirection.Row; row.style.alignItems = Align.Center;
                row.style.height = 52f;
                var k = Key(row);
                k.style.minWidth = 96f;
                k.text = ControlRows[i, 0] == "Move" ? (usingPad ? "L" : "WASD") : (ControlRows[i, 0] == "Look" ? (usingPad ? "R" : "Mouse") : KeyName(ControlRows[i, 0]));
                k.style.marginRight = 24f;
                var v = Text(row, sans, 24f, Bone50);
                v.text = Loc.Get(ControlRows[i, 1]);
            }
            Prompts(false, true, false, false);
        }

        static readonly string[,] ControlRows =
        {
            { "Move", "ui.action.move" }, { "Look", "ui.action.camera" }, { "Light", "ui.action.light" }, { "Heavy", "ui.action.heavy" },
            { "Block", "ui.action.block" }, { "Dodge", "ui.action.dodge" }, { "Sprint", "ui.action.sprint" }, { "Jump", "ui.action.jump" },
            { "LockOn", "ui.action.lock" }, { "Kick", "ui.action.kick" }, { "WarCry", "ui.action.warcry" }, { "Interact", "ui.action.interact" },
            { "Pause", "ui.action.pause" },
        };

        void BuildCredits()
        {
            Backdrop(0.86f);
            var head = Column(pages, 96f);
            var title = Text(head, serif, 40f, Bone50);
            title.text = Loc.Get("ui.main.credits");
            var clip = Box(pages);
            Place(clip, 160f, 200f, 160f, 160f);
            clip.style.overflow = Overflow.Hidden;
            creditsList = Box(clip);
            creditsList.style.translate = new Translate(0f, -creditsScroll);

            var g = Text(creditsList, serif, 32f, Bone50);
            g.text = Loc.Get("game.title");
            var gk = Text(creditsList, sans, 24f, Bone200);
            gk.text = "GK · " + Loc.Get("ui.end.place");
            gk.style.marginBottom = 48f;

            if (creditsText == null) return;
            var seen = new HashSet<string>();
            foreach (var raw in creditsText.text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    var h = Text(creditsList, sans, 24f, GiltHi);
                    h.text = line.Substring(3);
                    h.style.marginTop = 32f; h.style.marginBottom = 16f;
                    continue;
                }
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split('|');
                if (f.Length < 4) continue;
                string source = f[1].Trim(), author = f[2].Trim(), licence = f[3].Trim();
                string status = f.Length > 5 ? f[5].Trim() : "";
                if (licence.StartsWith("ours", StringComparison.OrdinalIgnoreCase) || status.StartsWith("NO", StringComparison.Ordinal)) continue;
                if (!seen.Add(author + "|" + source)) continue;
                var a = Text(creditsList, serif, 28f, Bone50);
                a.text = author;
                a.style.whiteSpace = WhiteSpace.Normal;
                var s = Text(creditsList, sans, 20f, Bone200);
                s.text = source;
                s.style.whiteSpace = WhiteSpace.Normal;
                var l = Text(creditsList, sans, 18f, Bone400);
                l.text = licence;
                l.style.whiteSpace = WhiteSpace.Normal;
                l.style.marginBottom = 24f;
            }
            Prompts(false, true, false, false);
        }

        void BuildConfirm()
        {
            var scrim = Box(pages);
            Fill(scrim);
            scrim.style.backgroundColor = new Color(Ink950.r, Ink950.g, Ink950.b, 0.74f);
            scrim.style.alignItems = Align.Center;
            scrim.style.justifyContent = Justify.Center;
            var card = Box(scrim);
            card.style.backgroundColor = Ink900;
            Border(card, 1f, new Color(Bone50.r, Bone50.g, Bone50.b, 0.13f), 0f);
            card.style.paddingLeft = 64f; card.style.paddingRight = 64f; card.style.paddingTop = 56f; card.style.paddingBottom = 56f;
            card.style.alignItems = Align.Center;
            anim = card; animSince = Time.unscaledTime; card.style.opacity = 0f;
            var t = Text(card, serif, 40f, Bone50);
            t.text = Loc.Get(confirmTitle);
            if (!string.IsNullOrEmpty(confirmBody))
            {
                var b = Text(card, sans, 24f, Bone200);
                b.text = Loc.Get(confirmBody);
                b.style.marginTop = 16f;
            }
            var row = Box(card);
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 48f;
            var yes = confirmYes;
            Button(row, Loc.Get("ui.common.yes"), () => { if (yes != null) yes(); }, true);
            Box(row).style.width = 32f;
            Button(row, Loc.Get("ui.common.no"), Back, false);
            Prompts(true, true, false, false);
        }

        void BuildEnd()
        {
            var bg = Box(pages);
            Fill(bg);
            bg.style.backgroundColor = new Color(Ink950.r, Ink950.g, Ink950.b, 0.78f);
            bg.style.alignItems = Align.Center;
            bg.style.justifyContent = Justify.Center;
            var card = Box(bg);
            card.style.alignItems = Align.Center;
            anim = card; animSince = Time.unscaledTime; card.style.opacity = 0f;

            var place = Text(card, sans, 24f, GiltHi);
            place.text = Loc.Get("ui.end.place");
            var word = Text(card, serif, 96f, endWin ? Bone50 : Bone200);
            word.style.unityFontStyleAndWeight = FontStyle.Bold;
            word.text = Loc.Get(endWin ? "ui.end.victory.title" : "ui.end.defeat.title");
            var rule = Box(card);
            rule.style.width = 240f; rule.style.height = 1f; rule.style.marginTop = 16f; rule.style.marginBottom = 24f;
            rule.style.backgroundColor = endWin ? Gold : Crimson700;
            var line = Text(card, serif, 32f, Bone200);
            line.text = Loc.Get(endWin ? "ui.end.victory.line" : "ui.end.defeat.line");

            var stats = Box(card);
            stats.style.flexDirection = FlexDirection.Row;
            stats.style.marginTop = 56f; stats.style.marginBottom = 64f;
            int count = waves != null ? waves.WaveCount : 0;
            int done = waves == null ? 0 : (endWin ? count : Mathf.Clamp(waves.WaveIndex, 0, count));
            Stat(stats, done + " / " + count, Loc.Get("ui.end.stat.waves"));
            Stat(stats, waves != null ? waves.Kills.ToString() : "0", Loc.Get("ui.end.stat.kills"));
            Stat(stats, waves != null ? Clock(waves.ElapsedTime) : "0:00", Loc.Get("ui.end.stat.time"));

            var row = Box(card);
            row.style.flexDirection = FlexDirection.Row;
            Button(row, Loc.Get("ui.end.restart"), () => Load(SceneManager.GetActiveScene().name), false);
            Box(row).style.width = 32f;
            Button(row, Loc.Get("ui.menu.main_menu"), () => Load(MainMenuScene()), false);
            Prompts(true, false, false, false);
        }

        void Stat(VisualElement parent, string value, string label)
        {
            var c = Box(parent);
            c.style.alignItems = Align.Center;
            c.style.width = 220f;
            var v = Text(c, serif, 56f, Bone50);
            v.text = value;
            var l = Text(c, sans, 24f, Bone400);
            l.text = label;
        }

        static string Clock(float seconds)
        {
            int s = Mathf.Max(0, (int)seconds);
            return (s / 60) + ":" + (s % 60).ToString("00");
        }

        // ---------- components (MENUS_M1 §4) ----------
        void MenuEntry(VisualElement parent, string text, Action submit)
        {
            var row = Box(parent);
            row.style.height = 64f;
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.pickingMode = PickingMode.Position;
            var plateEl = Box(row);
            plateEl.style.height = 48f;
            plateEl.style.flexDirection = FlexDirection.Row;
            plateEl.style.alignItems = Align.Center;
            plateEl.style.paddingRight = 40f;
            var rubric = Box(plateEl);
            rubric.style.width = 4f; rubric.style.height = 24f; rubric.style.marginRight = 20f;
            var label = Text(plateEl, serif, 32f, Bone200);
            label.text = text;
            var it = new Item { el = row, submit = submit };
            it.paint = f =>
            {
                plateEl.style.backgroundColor = f ? Bone50 : Color.clear;
                label.style.color = f ? PlateInk : Bone200;
                rubric.style.backgroundColor = f ? Crimson400 : Color.clear;
            };
            it.paint(false);
            row.userData = it;
            items.Add(it);
        }

        void Button(VisualElement parent, string text, Action submit, bool irreversible)
        {
            var b = Box(parent);
            b.pickingMode = PickingMode.Position;
            b.style.height = 72f; b.style.minWidth = 288f;
            b.style.paddingLeft = 48f; b.style.paddingRight = 48f;
            b.style.alignItems = Align.Center; b.style.justifyContent = Justify.Center;
            var label = Text(b, serif, 32f, Bone50);
            label.text = text;
            var it = new Item { el = b, submit = submit };
            it.paint = f =>
            {
                Color fill = f ? (irreversible ? Crimson700 : Bone50) : Ink950;
                b.style.backgroundColor = fill;
                Border(b, 1f, f ? fill : new Color(Bone50.r, Bone50.g, Bone50.b, 0.26f), 0f);
                label.style.color = f ? (irreversible ? Bone50 : PlateInk) : Bone50;
            };
            it.paint(false);
            b.userData = it;
            items.Add(it);
        }

        VisualElement SettingsRow(VisualElement parent, string label, out VisualElement value, out Action<bool> paintRow)
        {
            var row = Box(parent);
            row.pickingMode = PickingMode.Position;
            row.style.height = 72f;
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 32f; row.style.paddingRight = 24f;
            row.style.borderBottomWidth = 1f; row.style.borderBottomColor = new Color(Bone50.r, Bone50.g, Bone50.b, 0.13f);
            var bar = Box(row);
            Place(bar, 0f, 16f, float.NaN, float.NaN);
            bar.style.width = 4f; bar.style.height = 40f;
            var l = Text(row, sans, 24f, Bone200);
            l.text = label;
            l.style.flexGrow = 1f;
            value = Box(row);
            value.style.width = 320f;
            value.style.flexDirection = FlexDirection.Row;
            value.style.alignItems = Align.Center;
            value.style.justifyContent = Justify.Center;
            paintRow = f =>
            {
                row.style.backgroundColor = f ? new Color(Bone50.r, Bone50.g, Bone50.b, 0.06f) : Color.clear;
                bar.style.backgroundColor = f ? Crimson400 : Color.clear;
                l.style.color = f ? Bone50 : Bone200;
            };
            return row;
        }

        void Choice(VisualElement parent, string label, string[] options, int current, Action<int> set)
        {
            VisualElement value; Action<bool> paintRow;
            var row = SettingsRow(parent, label, out value, out paintRow);
            var left = Text(value, sans, 24f, Bone50); left.text = "‹";
            var mid = Box(value); mid.style.alignItems = Align.Center; mid.style.width = 240f;
            var v = Text(mid, serif, 26f, Bone50);
            var pips = Box(mid); pips.style.flexDirection = FlexDirection.Row; pips.style.marginTop = 4f;
            var dots = new VisualElement[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                var d = Box(pips); d.style.width = 6f; d.style.height = 6f; d.style.marginLeft = 3f; d.style.marginRight = 3f;
                dots[i] = d;
            }
            var right = Text(value, sans, 24f, Bone50); right.text = "›";
            int idx = Mathf.Clamp(current, 0, options.Length - 1);
            Action show = () =>
            {
                v.text = options[idx];
                for (int i = 0; i < dots.Length; i++) dots[i].style.backgroundColor = i == idx ? Crimson400 : new Color(Bone50.r, Bone50.g, Bone50.b, 0.26f);
            };
            show();
            var it = new Item { el = row };
            it.adjust = d =>
            {
                idx = (idx + d + options.Length) % options.Length;
                show();
                set(idx);
            };
            it.paint = f =>
            {
                paintRow(f);
                left.style.opacity = f ? 1f : 0f;
                right.style.opacity = f ? 1f : 0f;
            };
            it.paint(false);
            row.userData = it;
            items.Add(it);
        }

        void Slider(VisualElement parent, string label, float current, Action<float> set)
        {
            VisualElement value; Action<bool> paintRow;
            var row = SettingsRow(parent, label, out value, out paintRow);
            var track = Box(value);
            track.style.width = 240f; track.style.height = 4f;
            track.style.backgroundColor = new Color(Bone50.r, Bone50.g, Bone50.b, 0.18f);
            var fill = Box(track); Place(fill, 0f, 0f, float.NaN, 0f);
            var knob = Box(track); knob.style.width = 16f; knob.style.height = 16f; knob.style.position = Position.Absolute; knob.style.top = -6f;
            Border(knob, 2f, Bone200, 8f);
            knob.style.backgroundColor = Bone50;
            var pct = Text(value, sans, 20f, Bone200);
            pct.style.width = 64f; pct.style.unityTextAlign = TextAnchor.MiddleRight;
            float val = Mathf.Clamp01(current);
            bool focused = false;
            Action show = () =>
            {
                fill.style.width = Length.Percent(val * 100f);
                knob.style.left = Length.Percent(val * 100f);
                knob.style.translate = new Translate(-8f, 0f);
                fill.style.backgroundColor = focused ? Crimson400 : Bone200;
                knob.style.borderTopColor = knob.style.borderBottomColor = knob.style.borderLeftColor = knob.style.borderRightColor = focused ? Crimson400 : Bone200;
                pct.text = Mathf.RoundToInt(val * 100f) + "%";
            };
            show();
            var it = new Item { el = row, track = track };
            it.adjust = d => { val = Mathf.Clamp01(Mathf.Round((val + d * 0.05f) * 20f) / 20f); show(); set(val); };
            it.setFraction = f => { val = Mathf.Clamp01(f); show(); set(val); };
            it.paint = f => { focused = f; paintRow(f); show(); };
            it.paint(false);
            row.userData = it;
            items.Add(it);
        }

        // prompts, bottom-left: the last device used decides (§4)
        void Prompts(bool select, bool back, bool language, bool change)
        {
            var bar = Box(pages);
            Place(bar, 160f, float.NaN, float.NaN, 56f);
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bool ps = Gamepad.current is DualShockGamepad;
            if (select) Prompt(bar, usingPad ? (ps ? "✕" : "A") : "Enter", Loc.Get("ui.prompt.select"));
            if (back) Prompt(bar, usingPad ? (ps ? "○" : "B") : "Esc", Loc.Get("ui.prompt.back"));
            if (change) Prompt(bar, usingPad ? "◀ ▶" : "← →", Loc.Get("ui.prompt.change"));
            if (change) Prompt(bar, usingPad ? (ps ? "L1 R1" : "LB RB") : "Q E", Loc.Get("ui.prompt.tabs"));
            if (language) Prompt(bar, usingPad ? (ps ? "△" : "Y") : "Tab", Loc.Get("ui.lang.label"));
        }

        void Prompt(VisualElement bar, string key, string text)
        {
            var k = Key(bar);
            k.text = key;
            k.style.marginRight = 12f;
            var t = Text(bar, sans, 24f, Bone200);
            t.text = text;
            t.style.marginRight = 48f;
        }

        // ---------- small builders ----------
        static VisualElement Box(VisualElement parent)
        {
            var e = new VisualElement { pickingMode = PickingMode.Ignore };
            if (parent != null) parent.Add(e);
            return e;
        }

        static void Fill(VisualElement e)
        {
            e.style.position = Position.Absolute;
            e.style.left = 0; e.style.top = 0; e.style.right = 0; e.style.bottom = 0;
        }

        static void Place(VisualElement e, float left, float top, float right, float bottom)
        {
            e.style.position = Position.Absolute;
            if (!float.IsNaN(left)) e.style.left = left;
            if (!float.IsNaN(top)) e.style.top = top;
            if (!float.IsNaN(right)) e.style.right = right;
            if (!float.IsNaN(bottom)) e.style.bottom = bottom;
        }

        static void Border(VisualElement e, float w, Color c, float radius)
        {
            e.style.borderTopWidth = w; e.style.borderBottomWidth = w; e.style.borderLeftWidth = w; e.style.borderRightWidth = w;
            e.style.borderTopColor = c; e.style.borderBottomColor = c; e.style.borderLeftColor = c; e.style.borderRightColor = c;
            e.style.borderTopLeftRadius = radius; e.style.borderTopRightRadius = radius;
            e.style.borderBottomLeftRadius = radius; e.style.borderBottomRightRadius = radius;
        }

        static Label Text(VisualElement parent, Font font, float size, Color color)
        {
            var l = new Label { pickingMode = PickingMode.Ignore };
            if (font != null) l.style.unityFontDefinition = new StyleFontDefinition(FontDefinition.FromFont(font));
            l.style.fontSize = size;
            l.style.color = color;
            l.style.whiteSpace = WhiteSpace.NoWrap;
            l.style.marginLeft = 0; l.style.marginRight = 0; l.style.marginTop = 0; l.style.marginBottom = 0;
            l.style.paddingLeft = 0; l.style.paddingRight = 0; l.style.paddingTop = 0; l.style.paddingBottom = 0;
            parent.Add(l);
            return l;
        }

        Label Key(VisualElement parent)
        {
            var k = Text(parent, sans, 20f, Bone50);
            k.style.unityFontStyleAndWeight = FontStyle.Bold;
            k.style.unityTextAlign = TextAnchor.MiddleCenter;
            k.style.minWidth = 40f; k.style.height = 40f;
            k.style.paddingLeft = 10f; k.style.paddingRight = 10f;
            Border(k, 2f, new Color(Bone50.r, Bone50.g, Bone50.b, 0.5f), 6f);
            return k;
        }

        // "{0} — Skip" → "Skip": the key is drawn as a cap
        static string StripKey(string s)
        {
            int dash = s.IndexOf(" — ", StringComparison.Ordinal);
            return s.StartsWith("{0}", StringComparison.Ordinal) && dash >= 0 ? s.Substring(dash + 3) : s.Replace("{0}", "").Trim();
        }

        static readonly List<InputAction> enabledActions = new List<InputAction>();

        // the live binding of the player's Gameplay map for the device in use (CONTROLS §4)
        string KeyName(string action)
        {
            enabledActions.Clear();
            InputSystem.ListEnabledActions(enabledActions);
            for (int a = 0; a < enabledActions.Count; a++)
            {
                var act = enabledActions[a];
                if (act.name != action || act.actionMap == null || act.actionMap.name != "Gameplay") continue;
                for (int b = 0; b < act.bindings.Count; b++)
                {
                    string path = act.bindings[b].effectivePath ?? "";
                    bool isPad = path.StartsWith("<Gamepad>", StringComparison.Ordinal);
                    bool isKbm = path.StartsWith("<Keyboard>", StringComparison.Ordinal) || path.StartsWith("<Mouse>", StringComparison.Ordinal);
                    if (usingPad ? !isPad : !isKbm) continue;
                    return usingPad ? path.Substring(path.IndexOf('/') + 1) : act.GetBindingDisplayString(b, InputBinding.DisplayStringOptions.DontIncludeInteractions);
                }
            }
            return "—";
        }
    }
}
