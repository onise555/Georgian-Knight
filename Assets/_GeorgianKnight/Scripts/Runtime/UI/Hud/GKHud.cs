using System.Collections.Generic;
using GeorgianKnight.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace GeorgianKnight.UI
{
    /// <summary>
    /// The M1 combat HUD in the design system (Docs/UI/MENUS_M1.md palette v2, the hudkit and quick bar frames in
    /// Docs/UI/Inventory; Critics AC-05). Built in UI Toolkit from code, one panel, no input.
    ///
    /// <para>Shows: vitals with the vine cross (health with a lag, stamina, three adrenaline pips), the lock-on mark with
    /// the foe's name for 1.5 s and his health, one context prompt, the first-fight hint card (15 s), wave messages and
    /// the end line. Hides: the wave / enemy / timer line unless <see cref="GameHUD.ShowDebugLine"/> (QA). No combat
    /// words over the fight (no "parry!"): the hit feedback is Feel's.</para>
    ///
    /// <para>It replaces the legacy <see cref="GameHUD"/> in every scene (sets <see cref="GameHUD.Suppressed"/>);
    /// <c>-gklegacyhud</c> on the command line keeps the legacy HUD instead. Per frame it allocates nothing: texts are
    /// set only when they change, the timer reads cached strings (QA GK-26).</para>
    /// </summary>
    public sealed class GKHud : MonoBehaviour
    {
        // palette v2 (MENUS_M1 §3)
        static readonly Color Ink = new Color32(0x0B, 0x0E, 0x10, 0xFF);
        static readonly Color Bone = new Color32(0xF2, 0xEE, 0xE6, 0xFF);
        static readonly Color Bone200 = new Color32(0xC8, 0xC3, 0xB9, 0xFF);
        static readonly Color Bone300 = new Color32(0x96, 0x91, 0x8A, 0xFF);
        static readonly Color Crimson = new Color32(0x8A, 0x1C, 0x24, 0xFF);
        static readonly Color CrimsonHi = new Color32(0xC9, 0x3A, 0x40, 0xFF);
        static readonly Color Gilt = new Color32(0xD8, 0xC0, 0x8C, 0xFF);
        static readonly Color Track = new Color(Ink.r, Ink.g, Ink.b, 0.6f);
        static readonly Color TrackLine = new Color(Bone.r, Bone.g, Bone.b, 0.18f);
        static readonly Color Halo = new Color(Ink.r, Ink.g, Ink.b, 0.9f);

        const float NameSeconds = 1.5f, NameFade = 0.3f, HintSeconds = 15f, DeviceSwitchDelay = 0.5f;

        static GKHud instance;
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
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-gklegacyhud") >= 0) return;
            GameHUD.Suppressed = true;                 // legacy HUDs remove themselves in Awake
            if (instance != null) return;
            var go = new GameObject("[GKHud]");
            go.SetActive(false);
            DontDestroyOnLoad(go);
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = MakePanel();
            instance = go.AddComponent<GKHud>();
            go.SetActive(true);
            SceneManager.sceneLoaded += OnSceneLoaded;
            sceneHooked = true;
        }

        static void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
            GameHUD.DestroyAll();
            if (instance != null) instance.Unbind();
        }

        static PanelSettings MakePanel()
        {
            // PanelSettings.OnEnable warns "No Theme Style Sheet" before the theme below can be set: mute that instant
            var filter = Debug.unityLogger.filterLogType;
            Debug.unityLogger.filterLogType = LogType.Error;
            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            Debug.unityLogger.filterLogType = filter;
            ps.name = "GKHud Panel";
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(1920, 1080);
            ps.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            ps.match = 0.5f;
            ps.sortingOrder = 10;
            ps.themeStyleSheet = ScriptableObject.CreateInstance<ThemeStyleSheet>();   // every style is set in code
            return ps;
        }

        // ---------- bound game objects ----------
        Combatant player;
        CombatStats stats;
        LockOnSystem lockOn;
        LadderClimber climber;
        FightingStyles styles;
        GKInput input;
        WaveManager waves;
        Camera cam;
        float nextFind;

        // ---------- elements ----------
        Font serif, sans;
        VisualElement root, vitals, hpLag, hpFill, stFill, lockGroup, lockBar, lockBarFill, prompt, hint, hintRows, msgBox;
        readonly VisualElement[] pips = new VisualElement[3];
        Label lockName, promptKey, promptText, msgTitle, msgSub, debugWave, debugMin, debugSec;
        // quick bar (Docs/UI/Inventory/frames_director/M1_quickbar_ka.png): the fighting styles the knight switches between
        VisualElement qbar;
        readonly VisualElement[] qWell = new VisualElement[2], qMark = new VisualElement[2];
        readonly Label[] qKey = new Label[2];
        Label qName;
        int shownStyle = -1;
        float qNameUntil, shownQName = -1f;
        bool shownQPad;
        VisualElement debugLine;

        // ---------- shown state (set only on change) ----------
        float hpLagValue = 1f, msgUntil, hintUntil, nameSince, deviceSince;
        float shownRoot = -1f, shownHp = -1f, shownLag = -1f, shownSt = -1f, shownAdr = -1f, shownFoeHp = -1f, shownMsg = -1f, shownName = -1f;
        bool usingPad, hintBuilt, ended;
        string shownPrompt = "";
        Combatant shownTarget;
        int shownWave = int.MinValue, shownWaveCount = -1, shownAlive = -1, shownSeconds = -1, shownMinutes = -1;
        static readonly string[] MinuteStr = BuildStrings(100, "", "0");
        static readonly string[] SecondStr = BuildStrings(60, ":", "00");
        System.IDisposable langSub;

        // a style is written only when its value moved, so a still bar costs no layout pass
        static bool Changed(ref float last, float v)
        {
            if (Mathf.Abs(last - v) < 0.0005f) return false;
            last = v;
            return true;
        }

        static string[] BuildStrings(int n, string prefix, string format)
        {
            var s = new string[n];
            for (int i = 0; i < n; i++) s[i] = prefix + i.ToString(format);
            return s;
        }

        void Start()
        {
            var style = Resources.Load<GKHudStyle>(GKHudStyle.ResourcePath);
            serif = style != null && style.serif != null ? style.serif : OsFont("Sylfaen");
            sans = style != null && style.sans != null ? style.sans : serif;
            root = GetComponent<UIDocument>().rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.style.position = Position.Absolute;
            root.style.left = 0; root.style.top = 0; root.style.right = 0; root.style.bottom = 0;
            Build(style != null ? style.emblem : null);
            BuildQuickBar(style);
            root.style.display = DisplayStyle.None;
            langSub = EventBus.Subscribe<LanguageChanged>(e => { hintBuilt = false; shownStyle = -1; });
        }

        void OnDestroy()
        {
            Unbind();
            if (langSub != null) langSub.Dispose();
            if (instance == this) instance = null;
        }

        static Font OsFont(string family) { return Font.CreateDynamicFontFromOSFont(family, 32); }

        // ---------- building ----------

        static VisualElement Box(VisualElement parent, float w, float h, Color bg)
        {
            var e = new VisualElement { pickingMode = PickingMode.Ignore };
            if (w > 0f) e.style.width = w;
            if (h > 0f) e.style.height = h;
            e.style.backgroundColor = bg;
            if (parent != null) parent.Add(e);
            return e;
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

        Label Text(VisualElement parent, Font font, float size, Color color, TextAnchor align)
        {
            var l = new Label { pickingMode = PickingMode.Ignore };
            if (font != null) l.style.unityFontDefinition = new StyleFontDefinition(FontDefinition.FromFont(font));
            l.style.fontSize = size;
            l.style.color = color;
            l.style.unityTextAlign = align;
            l.style.whiteSpace = WhiteSpace.NoWrap;
            l.style.marginLeft = 0; l.style.marginRight = 0; l.style.marginTop = 0; l.style.marginBottom = 0;
            l.style.paddingLeft = 0; l.style.paddingRight = 0; l.style.paddingTop = 0; l.style.paddingBottom = 0;
            l.style.textShadow = new TextShadow { offset = Vector2.zero, blurRadius = 4f, color = Halo };
            parent.Add(l);
            return l;
        }

        // a keycap: the key's live name in a thin bone frame (pad: the face letter or shape)
        Label Key(VisualElement parent)
        {
            var k = Text(parent, sans, 20f, Bone, TextAnchor.MiddleCenter);
            k.style.unityFontStyleAndWeight = FontStyle.Bold;
            k.style.minWidth = 36f; k.style.height = 32f;
            k.style.paddingLeft = 8f; k.style.paddingRight = 8f;
            k.style.backgroundColor = new Color(Bone.r, Bone.g, Bone.b, 0.08f);
            Border(k, 1.5f, new Color(Bone.r, Bone.g, Bone.b, 0.6f), 4f);
            return k;
        }

        void Build(Texture2D emblem)
        {
            // vitals, bottom-left (quick bar frame: left 96, the bars 320 x 8 and 160 x 6)
            vitals = Box(root, 0f, 0f, Color.clear);
            Place(vitals, 96f, float.NaN, float.NaN, 56f);
            vitals.style.flexDirection = FlexDirection.Row;
            vitals.style.alignItems = Align.Center;
            if (emblem != null)
            {
                var em = Box(vitals, 48f, 64f, Color.clear);
                em.style.backgroundImage = new StyleBackground(emblem);
                em.style.marginRight = 16f;
            }
            var bars = Box(vitals, 0f, 0f, Color.clear);
            var hp = Box(bars, 320f, 8f, Track);
            Border(hp, 1f, TrackLine, 0f);
            hpLag = Box(hp, 0f, 0f, new Color(Bone200.r, Bone200.g, Bone200.b, 0.45f));
            Place(hpLag, 0f, 0f, float.NaN, 0f);
            hpFill = Box(hp, 0f, 0f, Crimson);
            Place(hpFill, 0f, 0f, float.NaN, 0f);
            Box(bars, 0f, 8f, Color.clear);
            var st = Box(bars, 160f, 6f, Track);
            Border(st, 1f, TrackLine, 0f);
            stFill = Box(st, 0f, 0f, Bone200);
            Place(stFill, 0f, 0f, float.NaN, 0f);
            var pipRow = Box(bars, 0f, 0f, Color.clear);
            pipRow.style.flexDirection = FlexDirection.Row;
            pipRow.style.marginTop = 12f; pipRow.style.marginLeft = 2f;
            for (int i = 0; i < 3; i++)
            {
                var p = Box(pipRow, 8f, 8f, Color.clear);
                Border(p, 1f, new Color(Gilt.r, Gilt.g, Gilt.b, 0.55f), 0f);
                p.style.rotate = new Rotate(45f);
                p.style.marginRight = 12f;
                pips[i] = p;
            }

            // lock-on: a ring on the foe's chest, his name beside it for 1.5 s, his health under the name
            lockGroup = Box(root, 0f, 0f, Color.clear);
            Place(lockGroup, 0f, 0f, float.NaN, float.NaN);
            var ring = Box(lockGroup, 24f, 24f, Color.clear);
            Place(ring, -12f, -12f, float.NaN, float.NaN);
            Border(ring, 2f, new Color(Bone.r, Bone.g, Bone.b, 0.85f), 12f);
            var side = Box(lockGroup, 0f, 0f, Color.clear);
            Place(side, 28f, -20f, float.NaN, float.NaN);
            lockName = Text(side, serif, 24f, Bone, TextAnchor.MiddleLeft);
            lockBar = Box(side, 120f, 4f, Track);
            lockBar.style.marginTop = 4f;
            lockBarFill = Box(lockBar, 0f, 0f, CrimsonHi);
            Place(lockBarFill, 0f, 0f, float.NaN, 0f);
            lockGroup.style.display = DisplayStyle.None;

            // context prompt, bottom centre, only one
            prompt = Box(root, 0f, 0f, new Color(Ink.r, Ink.g, Ink.b, 0.55f));
            Place(prompt, 0f, float.NaN, 0f, 176f);
            prompt.style.alignSelf = Align.Center;
            prompt.style.flexDirection = FlexDirection.Row;
            prompt.style.justifyContent = Justify.Center;
            prompt.style.backgroundColor = Color.clear;
            var pinner = Box(prompt, 0f, 0f, new Color(Ink.r, Ink.g, Ink.b, 0.55f));
            pinner.style.flexDirection = FlexDirection.Row;
            pinner.style.alignItems = Align.Center;
            pinner.style.paddingLeft = 8f; pinner.style.paddingRight = 16f; pinner.style.paddingTop = 8f; pinner.style.paddingBottom = 8f;
            Border(pinner, 0f, Color.clear, 6f);
            promptKey = Key(pinner);
            promptKey.style.marginRight = 12f;
            promptText = Text(pinner, sans, 24f, Bone, TextAnchor.MiddleLeft);
            prompt.style.display = DisplayStyle.None;

            // first-fight hint card, bottom-right (CONTROLS §5)
            hint = Box(root, 0f, 0f, new Color(Ink.r, Ink.g, Ink.b, 0.5f));
            Place(hint, float.NaN, float.NaN, 96f, 232f);   // above the quick bar (right 96, bottom 56)
            hint.style.paddingLeft = 20f; hint.style.paddingRight = 24f; hint.style.paddingTop = 16f; hint.style.paddingBottom = 16f;
            hint.style.borderLeftWidth = 2f; hint.style.borderLeftColor = new Color(Gilt.r, Gilt.g, Gilt.b, 0.7f);
            hintRows = Box(hint, 0f, 0f, Color.clear);

            // messages: wave, the end line
            msgBox = Box(root, 0f, 0f, Color.clear);
            Place(msgBox, 0f, 300f, 0f, float.NaN);
            msgBox.style.alignItems = Align.Center;
            msgTitle = Text(msgBox, serif, 48f, Bone, TextAnchor.MiddleCenter);
            msgSub = Text(msgBox, sans, 26f, Bone200, TextAnchor.MiddleCenter);
            msgSub.style.marginTop = 8f;
            msgBox.style.opacity = 0f;

            // QA only: wave / foes, and the timer (AC-05)
            debugLine = Box(root, 0f, 0f, Color.clear);
            Place(debugLine, 0f, 28f, 0f, float.NaN);
            debugLine.style.alignItems = Align.Center;
            debugWave = Text(debugLine, sans, 20f, Bone300, TextAnchor.UpperCenter);
            var timer = Box(debugLine, 0f, 0f, Color.clear);
            timer.style.flexDirection = FlexDirection.Row;
            debugMin = Text(timer, sans, 20f, Bone300, TextAnchor.UpperRight);
            debugSec = Text(timer, sans, 20f, Bone300, TextAnchor.UpperLeft);
            debugLine.style.display = DisplayStyle.None;
        }

        // ---------- binding to the game ----------

        void Unbind()
        {
            if (waves != null)
            {
                waves.WaveAnnounced -= OnWave; waves.WaveCleared -= OnCleared; waves.Victory -= OnVictory; waves.Defeat -= OnDefeat;
            }
            player = null; stats = null; lockOn = null; climber = null; styles = null; input = null; waves = null; cam = null;
            if (qbar != null) qbar.style.display = DisplayStyle.None;
            shownTarget = null; ended = false; hintBuilt = false; nextFind = 0f; shownPrompt = null;
            shownRoot = shownHp = shownLag = shownSt = shownAdr = shownMsg = -1f;
            msgUntil = 0f;
            if (root != null) root.style.display = DisplayStyle.None;
        }

        void TryBind()
        {
            nextFind = Time.unscaledTime + 1f;
            var director = FindAnyObjectByType<CombatDirector>();      // never CombatDirector.I: it creates one
            if (director == null || director.Player == null) return;
            player = director.Player;
            stats = player.Stats;
            lockOn = player.GetComponent<LockOnSystem>();
            climber = player.GetComponent<LadderClimber>();
            styles = player.GetComponent<FightingStyles>();
            shownStyle = -1;
            input = player.GetComponent<GKInput>();
            waves = FindAnyObjectByType<WaveManager>();
            if (waves != null)
            {
                waves.WaveAnnounced += OnWave; waves.WaveCleared += OnCleared; waves.Victory += OnVictory; waves.Defeat += OnDefeat;
            }
            usingPad = input != null && input.UsingGamepad;
            hintUntil = Time.unscaledTime + HintSeconds;
            hpLagValue = stats != null ? stats.HealthNormalized : 1f;
            root.style.display = DisplayStyle.Flex;
        }

        void OnWave(int i, WaveManager.Wave w)
        {
            string sub = Loc.GetOrNull(w.titleKey);
            Message(Loc.Format("ui.hud.wave", i + 1, waves.WaveCount), sub ?? w.title, 3f);
        }

        void OnCleared(int i)
        {
            if (waves != null && i + 1 < waves.WaveCount) Message(Loc.Get("ui.hud.prepare"), "", 2.5f);
        }

        void OnVictory() { ended = true; Message(Loc.Get("ui.end.victory.title"), Loc.Get("ui.end.victory.line"), float.PositiveInfinity); }
        void OnDefeat() { ended = true; Message(Loc.Get("ui.end.defeat.title"), Loc.Get("ui.end.defeat.line"), float.PositiveInfinity); }

        void Message(string title, string sub, float seconds)
        {
            msgTitle.text = title;
            msgSub.text = sub ?? "";
            msgSub.style.display = string.IsNullOrEmpty(sub) ? DisplayStyle.None : DisplayStyle.Flex;
            msgUntil = Time.unscaledTime + seconds;
        }

        // ---------- per frame (no allocation) ----------

        void LateUpdate()
        {
            if (root == null) return;
            if (player == null)
            {
                if (stats != null || waves != null) Unbind();
                if (Time.unscaledTime >= nextFind) TryBind();
                if (player == null) return;
            }
            if (cam == null) cam = Camera.main;
            float now = Time.unscaledTime;

            // a menu (pause, the end card) covers the fight: the HUD steps aside rather than showing through
            IMenuController menus;
            float menuAlpha = Services.TryGet(out menus) && menus.IsAnyMenuOpen ? 0f : 1f;
            if (Changed(ref shownRoot, menuAlpha)) root.style.opacity = menuAlpha;

            // vitals
            float hp = stats.HealthNormalized;
            hpLagValue = hp > hpLagValue ? hp : Mathf.MoveTowards(hpLagValue, hp, Time.unscaledDeltaTime * 0.35f);
            if (Changed(ref shownHp, Mathf.Clamp01(hp))) hpFill.style.width = Length.Percent(shownHp * 100f);
            if (Changed(ref shownLag, Mathf.Clamp01(hpLagValue))) hpLag.style.width = Length.Percent(shownLag * 100f);
            if (Changed(ref shownSt, Mathf.Clamp01(stats.StaminaNormalized))) stFill.style.width = Length.Percent(shownSt * 100f);
            if (Changed(ref shownAdr, stats.Adrenaline))
                for (int i = 0; i < 3; i++)
                {
                    float seg = Mathf.Clamp01((shownAdr - i * stats.adrenalinePerSegment) / stats.adrenalinePerSegment);
                    pips[i].style.backgroundColor = seg >= 1f ? Gilt : new Color(Gilt.r, Gilt.g, Gilt.b, seg * 0.35f);
                }

            UpdateDevice(now);
            UpdateLock(now);
            UpdatePrompt();
            UpdateHint(now);
            UpdateQuickBar(now);
            UpdateDebug();

            float fade = float.IsPositiveInfinity(msgUntil) ? 1f : Mathf.Clamp01((msgUntil - now) * 2f);
            if (Changed(ref shownMsg, fade)) msgBox.style.opacity = fade;
        }

        void UpdateDevice(float now)
        {
            bool pad = input != null && input.UsingGamepad;
            if (pad == usingPad) { deviceSince = now; return; }
            if (now - deviceSince < DeviceSwitchDelay) return;     // a bumped mouse does not flicker the hints
            usingPad = pad;
            hintBuilt = false;
            shownPrompt = null;
        }

        void UpdateLock(float now)
        {
            Combatant t = lockOn != null ? lockOn.Target : null;
            if (t == null || t.IsDead || cam == null) { HideLock(); return; }
            Vector3 world = t.transform.position + Vector3.up * 1.35f * t.transform.localScale.y;   // chest, not the head
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0f) { HideLock(); return; }
            if (t != shownTarget)
            {
                shownTarget = t;
                nameSince = now;
                shownName = -1f; shownFoeHp = -1f;
                lockName.text = string.IsNullOrEmpty(t.displayNameKey) ? Loc.Get("enemy.generic.name") : Loc.Get(t.displayNameKey);
            }
            lockGroup.style.display = DisplayStyle.Flex;
            float k = root.resolvedStyle.width > 0f ? root.resolvedStyle.width / Screen.width : 1f;
            lockGroup.style.translate = new Translate(sp.x * k, (Screen.height - sp.y) * k);
            float since = now - nameSince;
            if (Changed(ref shownName, since < NameSeconds ? 1f : Mathf.Clamp01(1f - (since - NameSeconds) / NameFade))) lockName.style.opacity = shownName;
            if (Changed(ref shownFoeHp, Mathf.Clamp01(t.Stats.HealthNormalized))) lockBarFill.style.width = Length.Percent(shownFoeHp * 100f);
        }

        void HideLock()
        {
            if (shownTarget == null && lockGroup.style.display == DisplayStyle.None) return;
            shownTarget = null;
            lockGroup.style.display = DisplayStyle.None;
        }

        void UpdatePrompt()
        {
            // the end line's restart key (GameBootstrap reads R, only without a menu layer; with one, the end card
            // owns restart), else the ladder; one prompt at a time
            string p = ended ? (Services.Has<IMenuController>() ? "" : "restart") : (climber != null ? climber.PromptText : "");
            if (p == null) p = "";
            if (ReferenceEquals(p, shownPrompt)) return;
            shownPrompt = p;
            if (p.Length == 0) { prompt.style.display = DisplayStyle.None; return; }
            prompt.style.display = DisplayStyle.Flex;
            if (ended)
            {
                promptKey.text = "R";
                promptText.text = Loc.Get("ui.end.restart");
                return;
            }
            // LadderClimber writes its prompt in Georgian with E baked in: show the live key and the localized verb
            bool up = p.IndexOf("ასვლა", System.StringComparison.Ordinal) >= 0;
            promptKey.text = KeyName("Interact");
            promptText.text = StripKey(Loc.Get(up ? "ui.prompt.ladder_up" : "ui.prompt.ladder_down"));
        }

        // "{0} — Climb up" → "Climb up": the key is drawn as a cap, not as text
        static string StripKey(string s)
        {
            int dash = s.IndexOf(" — ", System.StringComparison.Ordinal);
            return s.StartsWith("{0}", System.StringComparison.Ordinal) && dash >= 0 ? s.Substring(dash + 3) : s.Replace("{0}", "").Trim();
        }

        // ---------- quick bar: sword and shield (1 / d-pad left), sword alone (2 / d-pad right) ----------

        void BuildQuickBar(GKHudStyle style)
        {
            Texture2D sword = style != null ? style.iconSword : null, shield = style != null ? style.iconShield : null;
            qbar = Box(root, 0f, 0f, Color.clear);
            Place(qbar, float.NaN, float.NaN, 96f, 56f);
            qbar.style.alignItems = Align.FlexEnd;
            qName = Text(qbar, serif, 32f, Bone, TextAnchor.MiddleRight);
            qName.style.marginBottom = 12f;
            var row = Box(qbar, 0f, 0f, Color.clear);
            row.style.flexDirection = FlexDirection.Row;
            for (int i = 0; i < 2; i++)
            {
                var col = Box(row, 0f, 0f, Color.clear);
                col.style.alignItems = Align.Center;
                col.style.marginLeft = i > 0 ? 24f : 0f;
                var well = Box(col, 88f, 88f, new Color(Ink.r, Ink.g, Ink.b, 0.35f));
                Border(well, 1f, TrackLine, 0f);
                if (i == 0 && shield != null)
                {
                    var sh = Box(well, 64f, 64f, Color.clear);
                    Place(sh, 4f, 4f, float.NaN, float.NaN);
                    sh.style.backgroundImage = new StyleBackground(shield);
                }
                if (sword != null)
                {
                    var sw = Box(well, i == 0 ? 64f : 80f, i == 0 ? 64f : 80f, Color.clear);
                    Place(sw, i == 0 ? 22f : 4f, i == 0 ? 20f : 4f, float.NaN, float.NaN);
                    sw.style.backgroundImage = new StyleBackground(sword);
                }
                var mark = Box(col, 24f, 3f, Color.clear);
                mark.style.marginTop = 8f;
                var key = Key(col);
                key.style.marginTop = 12f;
                qWell[i] = well; qMark[i] = mark; qKey[i] = key;
            }
            qbar.style.display = DisplayStyle.None;
        }

        void UpdateQuickBar(float now)
        {
            if (qbar == null) return;
            bool on = styles != null;
            var want = on ? DisplayStyle.Flex : DisplayStyle.None;
            if (qbar.style.display != want) qbar.style.display = want;
            if (!on) return;
            int cur = styles.Style == WeaponStyle.Sword ? 1 : 0;
            if (cur != shownStyle)
            {
                bool first = shownStyle < 0;
                shownStyle = cur;
                for (int i = 0; i < 2; i++)
                {
                    bool act = i == cur;
                    Border(qWell[i], act ? 1.5f : 1f, act ? Bone : TrackLine, 0f);
                    qWell[i].style.backgroundColor = new Color(Ink.r, Ink.g, Ink.b, act ? 0.55f : 0.3f);
                    qWell[i].style.opacity = act ? 1f : 0.7f;
                    qMark[i].style.backgroundColor = act ? CrimsonHi : Color.clear;
                }
                qName.text = Loc.Get(cur == 0 ? "ui.action.style_shield" : "ui.action.style_sword");
                qNameUntil = now + (first ? 4f : 2.5f);           // the name shows on a switch, then fades
                shownQPad = !usingPad;                            // keys repainted below
            }
            if (shownQPad != usingPad)
            {
                shownQPad = usingPad;
                qKey[0].text = usingPad ? "◀" : "1";
                qKey[1].text = usingPad ? "▶" : "2";
            }
            if (Changed(ref shownQName, Mathf.Clamp01((qNameUntil - now) / 0.4f))) qName.style.opacity = shownQName;
        }

        void UpdateHint(float now)
        {
            float left = hintUntil - now;
            if (left <= -1f || ended) { if (hint.style.display != DisplayStyle.None) hint.style.display = DisplayStyle.None; return; }
            if (!hintBuilt) BuildHint();
            hint.style.opacity = Mathf.Clamp01(left + 1f);
        }

        static readonly string[,] HintActions =
        {
            { "Light", "ui.action.light", "" },
            { "Heavy", "ui.action.heavy", "ui.action.heavy.hint" },
            { "Block", "ui.action.block", "ui.action.block.hint" },
            { "Dodge", "ui.action.dodge", "" },
            { "LockOn", "ui.action.lock", "" },
            { "Kick", "ui.action.kick", "" },
        };

        void BuildHint()
        {
            hintBuilt = true;
            hint.style.display = DisplayStyle.Flex;
            hintRows.Clear();
            for (int i = 0; i < HintActions.GetLength(0); i++)
            {
                var row = Box(hintRows, 0f, 0f, Color.clear);
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.marginBottom = i < HintActions.GetLength(0) - 1 ? 8f : 0f;
                var k = Key(row);
                k.text = KeyName(HintActions[i, 0]);
                k.style.marginRight = 12f;
                var v = Text(row, sans, 24f, Bone, TextAnchor.MiddleLeft);
                v.text = Loc.Get(HintActions[i, 1]);
                if (HintActions[i, 2].Length > 0)
                {
                    var h = Text(row, sans, 24f, Bone300, TextAnchor.MiddleLeft);
                    h.text = "  · " + Loc.Get(HintActions[i, 2]);
                }
            }
        }

        void UpdateDebug()
        {
            bool show = GameHUD.ShowDebugLine && waves != null;
            var want = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (debugLine.style.display != want) debugLine.style.display = want;
            if (!show) return;
            int total = (int)waves.ElapsedTime;
            if (total != shownSeconds)
            {
                shownSeconds = total;
                int mins = Mathf.Min(total / 60, MinuteStr.Length - 1);
                if (mins != shownMinutes) { shownMinutes = mins; debugMin.text = MinuteStr[mins]; }
                debugSec.text = SecondStr[total % 60];
            }
            int wi = waves.WaveIndex, wc = waves.WaveCount, al = waves.AliveCount;
            if (wi != shownWave || wc != shownWaveCount || al != shownAlive)
            {
                shownWave = wi; shownWaveCount = wc; shownAlive = al;
                debugWave.text = wi >= 0 ? Loc.Format("ui.hud.wave", wi + 1, wc) + "   ·   " + Loc.Format("ui.hud.foes", al) : Loc.Get("ui.hud.prepare");
            }
        }

        // ---------- glyphs: the live binding of the player's action map, for the device in use (CONTROLS §4) ----------

        static readonly List<InputAction> enabledActions = new List<InputAction>();

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
                    bool isPad = path.StartsWith("<Gamepad>", System.StringComparison.Ordinal);
                    bool isKbm = path.StartsWith("<Keyboard>", System.StringComparison.Ordinal) || path.StartsWith("<Mouse>", System.StringComparison.Ordinal);
                    if (usingPad ? !isPad : !isKbm) continue;
                    return usingPad ? PadName(path) : act.GetBindingDisplayString(b, InputBinding.DisplayStringOptions.DontIncludeInteractions);
                }
            }
            return "?";
        }

        // Xbox letters, or PlayStation shapes for a DualShock / DualSense (monochrome; Kenney glyphs replace these)
        static string PadName(string path)
        {
            bool ps = Gamepad.current is DualShockGamepad;
            switch (path.Substring(path.IndexOf('/') + 1))
            {
                case "buttonSouth": return ps ? "✕" : "A";
                case "buttonEast": return ps ? "○" : "B";
                case "buttonWest": return ps ? "□" : "X";
                case "buttonNorth": return ps ? "△" : "Y";
                case "leftShoulder": return ps ? "L1" : "LB";
                case "rightShoulder": return ps ? "R1" : "RB";
                case "leftTrigger": return ps ? "L2" : "LT";
                case "rightTrigger": return ps ? "R2" : "RT";
                case "leftStickPress": return "L3";
                case "rightStickPress": return "R3";
                case "dpad/up": return "▲";
                case "dpad/down": return "▼";
                case "dpad/left": return "◀";
                case "dpad/right": return "▶";
                case "start": return ps ? "Options" : "Start";
                default: return path.Substring(path.IndexOf('/') + 1);
            }
        }
    }
}
