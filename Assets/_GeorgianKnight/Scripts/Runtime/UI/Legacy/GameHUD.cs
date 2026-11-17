using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace GeorgianKnight
{
    /// <summary>
    /// LEGACY. Minimal combat HUD built in code for bare test scenes without the UI module. A newer HUD that replaces
    /// it sets <see cref="Suppressed"/> and calls <see cref="DestroyAll"/> (at install and after each scene load), so
    /// the two HUDs never render together. Depends on nothing but gameplay.
    /// </summary>
    public class GameHUD : MonoBehaviour
    {
        static readonly List<GameHUD> live = new List<GameHUD>();

        /// <summary>Set by a replacement HUD: new instances remove themselves in Awake.</summary>
        public static bool Suppressed { get; set; }

        /// <summary>QA only: shows the wave / enemy / timer line (Critics AC-05: never on screen in normal play).
        /// On with the command-line flag <c>-gkhuddebug</c>, or set by QA code at runtime.</summary>
        public static bool ShowDebugLine { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Suppressed = false; live.Clear();
            ShowDebugLine = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-gkhuddebug") >= 0;
        }

        /// <summary>Destroys every live legacy HUD (called by the UI module at install and on each scene load).</summary>
        public static void DestroyAll()
        {
            for (int i = live.Count - 1; i >= 0; i--)
            {
                var h = live[i];
                live.RemoveAt(i);
                if (h == null) continue;
                h.enabled = false;
                Destroy(h.gameObject);
            }
        }

        void Awake()
        {
            if (Suppressed) { enabled = false; Destroy(gameObject); return; }
            live.Add(this);
        }

        void OnDestroy() { live.Remove(this); }

        public Combatant player;
        public WaveManager waves;

        Font font;
        RectTransform root;
        Image hpFill, hpLag, stFill;
        Image[] adr = new Image[3];
        Text waveText, msgText, subText, hintText, promptText;
        LadderClimber climber;
        RectTransform targetPlate;
        Image targetHp;
        Text targetName;
        LockOnSystem lockOn;
        SkillSystem skills;
        Camera cam;
        float msgUntil, hintUntil;
        float hpLagValue = 1f;
        // last values shown, so LateUpdate builds a string only when one changes (QA GK-26: no per-frame garbage)
        string shownPrompt = "";
        int shownWave = int.MinValue, shownWaveCount = -1, shownAlive = -1, shownSeconds = -1, shownMinutes = -1;
        Combatant namedTarget;
        bool debugLineShown = true;
        // the timer ticks every second: two texts fed from strings built once, so a tick allocates nothing
        Text timerMinText, timerSecText;
        static readonly string[] MinuteStr = BuildStrings(100, "", "0");
        static readonly string[] SecondStr = BuildStrings(60, ":", "00");

        static string[] BuildStrings(int n, string prefix, string format)
        {
            var s = new string[n];
            for (int i = 0; i < n; i++) s[i] = prefix + i.ToString(format);
            return s;
        }

        static readonly Color Gold = new Color(0.86f, 0.71f, 0.38f);
        static readonly Color Blood = new Color(0.62f, 0.07f, 0.07f);

        void Start()
        {
            if (Suppressed) { Destroy(gameObject); return; }
            if (player == null) player = CombatDirector.I.Player;
            if (waves == null) waves = FindAnyObjectByType<WaveManager>();
            if (player != null)
            {
                lockOn = player.GetComponent<LockOnSystem>();
                skills = player.GetComponent<SkillSystem>();
                climber = player.GetComponent<LadderClimber>();
                player.ParrySucceeded += a => Flash("პარირება!", 0.9f);
                player.PerfectDodge += () => Flash("ზუსტი აცილება", 0.8f);
            }
            font = Font.CreateDynamicFontFromOSFont(new[] { "Sylfaen", "Segoe UI", "Arial" }, 32);
            Build();
            if (waves != null)
            {
                waves.WaveAnnounced += (i, w) => { Flash("ტალღა " + (i + 1) + " / " + waves.WaveCount, 3f, w.title); };
                waves.WaveCleared += i => Flash("ტალღა მოგერიებულია", 2.5f, i + 1 < waves.WaveCount ? "მომდევნო ტალღა მოდის…" : "");
                waves.Victory += () => Flash("გამარჯვება!", 999f, "დიდგორი შენია.  R — თავიდან");
                waves.Defeat += () => Flash("დაეცი", 999f, "R — თავიდან");
            }
            hintUntil = Time.time + 14f;
        }

        Text MakeText(string name, Transform parent, int size, TextAnchor anchor, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = font; t.fontSize = size; t.alignment = anchor; t.color = color;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            var sh = go.AddComponent<Shadow>(); sh.effectColor = new Color(0f, 0f, 0f, 0.85f); sh.effectDistance = new Vector2(2f, -2f);
            return t;
        }

        Image MakeImage(string name, Transform parent, Color c)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var i = go.AddComponent<Image>();
            i.color = c;
            return i;
        }

        static void Place(RectTransform r, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            r.anchorMin = r.anchorMax = anchor; r.pivot = pivot; r.anchoredPosition = pos; r.sizeDelta = size;
        }

        Image Bar(string name, Transform parent, Vector2 pos, Vector2 size, Color fill, out Image lag)
        {
            var frame = MakeImage(name, parent, new Color(0f, 0f, 0f, 0.55f));
            Place(frame.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), pos, size);
            var border = frame.gameObject.AddComponent<Outline>(); border.effectColor = new Color(Gold.r, Gold.g, Gold.b, 0.55f); border.effectDistance = new Vector2(1.5f, 1.5f);
            lag = MakeImage("Lag", frame.transform, new Color(0.9f, 0.85f, 0.75f, 0.6f));
            Stretch(lag.rectTransform, 3f);
            var f = MakeImage("Fill", frame.transform, fill);
            Stretch(f.rectTransform, 3f);
            return f;
        }

        static void Stretch(RectTransform r, float inset)
        {
            r.anchorMin = new Vector2(0f, 0f); r.anchorMax = new Vector2(1f, 1f); r.pivot = new Vector2(0f, 0.5f);
            r.offsetMin = new Vector2(inset, inset); r.offsetMax = new Vector2(-inset, -inset);
        }

        static void SetFill(Image i, float v)
        {
            var r = i.rectTransform;
            r.anchorMax = new Vector2(Mathf.Clamp01(v), 1f);
        }

        void Build()
        {
            var canvasGo = new GameObject("HUD Canvas", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            root = canvasGo.GetComponent<RectTransform>();
            // no hurt flash or low-health tint here: full-screen effects are Feel & VFX's (photosensitivity rules)

            // vitals, bottom-left

            hpFill = Bar("Health", root, new Vector2(60f, 92f), new Vector2(520f, 22f), Blood, out hpLag);
            Image dummy;
            stFill = Bar("Stamina", root, new Vector2(60f, 62f), new Vector2(380f, 12f), Gold, out dummy);
            dummy.color = new Color(0f, 0f, 0f, 0f);
            for (int i = 0; i < 3; i++)
            {
                adr[i] = MakeImage("Adrenaline" + i, root, new Color(1f, 1f, 1f, 0.15f));
                Place(adr[i].rectTransform, Vector2.zero, Vector2.zero, new Vector2(60f + i * 44f, 128f), new Vector2(36f, 10f));
            }
            var nameText = MakeText("Name", root, 26, TextAnchor.LowerLeft, Gold);
            Place(nameText.rectTransform, Vector2.zero, Vector2.zero, new Vector2(62f, 146f), new Vector2(600f, 40f));
            nameText.text = "ქართველი რაინდი";

            waveText = MakeText("Wave", root, 26, TextAnchor.UpperCenter, new Color(0.95f, 0.92f, 0.85f));
            Place(waveText.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -28f), new Vector2(900f, 40f));
            // the timer on its own line under the wave line, minutes ending and ":ss" starting at the centre
            timerMinText = MakeText("TimerMin", root, 26, TextAnchor.UpperRight, new Color(0.95f, 0.92f, 0.85f));
            Place(timerMinText.rectTransform, new Vector2(0.5f, 1f), new Vector2(1f, 1f), new Vector2(0f, -64f), new Vector2(200f, 40f));
            timerSecText = MakeText("TimerSec", root, 26, TextAnchor.UpperLeft, new Color(0.95f, 0.92f, 0.85f));
            Place(timerSecText.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, 1f), new Vector2(0f, -64f), new Vector2(200f, 40f));

            msgText = MakeText("Message", root, 64, TextAnchor.MiddleCenter, Gold);
            Place(msgText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 190f), new Vector2(1400f, 90f));
            subText = MakeText("Sub", root, 30, TextAnchor.MiddleCenter, new Color(0.95f, 0.92f, 0.85f));
            Place(subText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 128f), new Vector2(1400f, 50f));

            hintText = MakeText("Controls", root, 20, TextAnchor.LowerRight, new Color(0.95f, 0.92f, 0.85f, 0.9f));
            Place(hintText.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-50f, 50f), new Vector2(700f, 260f));
            hintText.text = "მარცხენა ღილაკი — მსუბუქი დარტყმა (კომბო)\nმარჯვენა ღილაკი — მძიმე  (დაჭერით — დამუხტვა)\nQ — ბლოკი  ·  ზუსტ დროს — პარირება\nSpace — აცილება / გორება\nShift — სირბილი  ·  F — წიხლი  ·  E — მოქმედება (კიბე)\nR — საბრძოლო ყიჟინა  ·  Tab / შუა ღილაკი — მიზანზე დამაგრება";

            // context prompt (ladders now; doors, NPCs and loot once the interaction module replaces this HUD)
            promptText = MakeText("Prompt", root, 26, TextAnchor.MiddleCenter, new Color(0.96f, 0.9f, 0.74f, 1f));
            Place(promptText.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 190f), new Vector2(700f, 40f));
            promptText.text = "";

            // lock-on plate
            var plate = new GameObject("TargetPlate", typeof(RectTransform)).GetComponent<RectTransform>();
            plate.SetParent(root, false);
            Place(plate, Vector2.zero, new Vector2(0.5f, 0f), Vector2.zero, new Vector2(160f, 40f));
            var diamond = MakeImage("Marker", plate, Gold);
            Place(diamond.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f), new Vector2(0f, -8f), new Vector2(12f, 12f));
            diamond.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 45f);
            Image tl;
            var thp = Bar("TargetHealth", plate, new Vector2(0f, 6f), new Vector2(160f, 8f), Blood, out tl);
            tl.color = new Color(0f, 0f, 0f, 0f);
            thp.transform.parent.GetComponent<RectTransform>().anchorMin = new Vector2(0.5f, 0f);
            thp.transform.parent.GetComponent<RectTransform>().anchorMax = new Vector2(0.5f, 0f);
            thp.transform.parent.GetComponent<RectTransform>().pivot = new Vector2(0.5f, 0f);
            thp.transform.parent.GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, 6f);
            targetHp = thp;
            targetName = MakeText("TargetName", plate, 18, TextAnchor.LowerCenter, new Color(0.95f, 0.92f, 0.85f));
            Place(targetName.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 18f), new Vector2(300f, 26f));
            targetPlate = plate;
            targetPlate.gameObject.SetActive(false);
        }

        public void Flash(string msg, float seconds, string sub = "")
        {
            msgText.text = msg;
            subText.text = sub;
            msgUntil = Time.unscaledTime + seconds;
        }

        void LateUpdate()
        {
            if (cam == null) cam = Camera.main;
            if (promptText != null)
            {
                string p = climber != null ? climber.PromptText : "";
                if (p == null) p = "";
                if (p != shownPrompt) { shownPrompt = p; promptText.text = p; }
            }
            if (player != null)
            {
                float hp = player.Stats.HealthNormalized;
                SetFill(hpFill, hp);
                hpLagValue = hp > hpLagValue ? hp : Mathf.MoveTowards(hpLagValue, hp, Time.unscaledDeltaTime * 0.35f);
                SetFill(hpLag, hpLagValue);
                SetFill(stFill, player.Stats.StaminaNormalized);
                float a = player.Stats.Adrenaline;
                for (int i = 0; i < 3; i++)
                {
                    float seg = Mathf.Clamp01((a - i * player.Stats.adrenalinePerSegment) / player.Stats.adrenalinePerSegment);
                    adr[i].color = seg >= 1f ? new Color(1f, 0.82f, 0.4f, 1f) : new Color(1f, 1f, 1f, 0.12f + seg * 0.35f);
                }
            }
            if (ShowDebugLine != debugLineShown)
            {
                debugLineShown = ShowDebugLine;
                waveText.gameObject.SetActive(debugLineShown);
                timerMinText.gameObject.SetActive(debugLineShown);
                timerSecText.gameObject.SetActive(debugLineShown);
            }
            if (waves != null && debugLineShown)
            {
                int total = (int)waves.ElapsedTime;
                int wi = waves.WaveIndex, wc = waves.WaveCount, al = waves.AliveCount;
                if (total != shownSeconds)
                {
                    shownSeconds = total;
                    int mins = Mathf.Min(total / 60, MinuteStr.Length - 1);
                    if (mins != shownMinutes) { shownMinutes = mins; timerMinText.text = MinuteStr[mins]; }
                    timerSecText.text = SecondStr[total % 60];
                }
                // rebuilt only on a wave change or a death, not on a timer tick
                if (wi != shownWave || wc != shownWaveCount || al != shownAlive)
                {
                    shownWave = wi; shownWaveCount = wc; shownAlive = al;
                    waveText.text = wi >= 0 ? "ტალღა " + (wi + 1) + "/" + wc + "   ·   მტერი: " + al : "მოემზადე…";
                }
            }
            bool showMsg = Time.unscaledTime < msgUntil;
            float fade = Mathf.Clamp01((msgUntil - Time.unscaledTime) * 2f);
            msgText.color = new Color(Gold.r, Gold.g, Gold.b, showMsg ? fade : 0f);
            subText.color = new Color(0.95f, 0.92f, 0.85f, showMsg ? fade : 0f);
            hintText.color = new Color(0.95f, 0.92f, 0.85f, Mathf.Clamp01(hintUntil - Time.unscaledTime) * 0.9f);

            Combatant t = lockOn != null ? lockOn.Target : null;
            if (t != null && !t.IsDead && cam != null)
            {
                Vector3 sp = cam.WorldToScreenPoint(t.transform.position + Vector3.up * 2.15f * t.transform.localScale.y);
                if (sp.z > 0f)
                {
                    targetPlate.gameObject.SetActive(true);
                    float k = root.rect.width / Screen.width;
                    targetPlate.anchoredPosition = new Vector2(sp.x * k, sp.y * k);
                    SetFill(targetHp, t.Stats.HealthNormalized);
                    if (t != namedTarget)
                    {
                        // .name builds a new string on every read: read it once per new target
                        namedTarget = t;
                        string n = t.name;
                        targetName.text = n.StartsWith("Seljuk Champion", System.StringComparison.Ordinal) ? "სელჩუკთა ჩემპიონი" : (n.StartsWith("Seljuk Veteran", System.StringComparison.Ordinal) ? "სელჩუკი ვეტერანი" : "სელჩუკი მეომარი");
                    }
                }
                else targetPlate.gameObject.SetActive(false);
            }
            else targetPlate.gameObject.SetActive(false);
        }
    }
}
