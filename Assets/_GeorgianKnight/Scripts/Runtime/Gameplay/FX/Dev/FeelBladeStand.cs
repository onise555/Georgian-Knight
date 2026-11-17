#if UNITY_EDITOR || DEVELOPMENT_BUILD
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GeorgianKnight
{
    /// <summary>
    /// Effects review of the blood film on a sword (QUALITY 3b, Director 2026-10-30): a copy of the hero's sword on a
    /// stand in the scene's own light, with its own camera, run through a fixed programme so every take is the same:
    /// three cuts' worth of blood, then a full turn about the vertical (both flats and both edges), a look down the tip,
    /// the tip raised and lowered (the film runs toward the guard, then back to the tip and drips), and a time-lapse to
    /// dry blood with a second turn. Started by the editor command `feel_blade`; never part of a scene or a build.
    /// </summary>
    public sealed class FeelBladeStand : MonoBehaviour
    {
        public WeaponHitbox source;
        public Material filmMaterial;
        public System.Action<Vector3, Vector3, float> drop;
        [Tooltip("0 = frame the whole blade with a margin.")]
        public float camDistance = 0f;

        GameObject copy;
        WeaponHitbox blade;
        BladeBloodFilm film;
        Camera cam;
        float t;
        bool dried;
        // the foes' AI, paused while the stand runs (the arena waves killed the hero mid-take, Team Lead 21:55)
        readonly System.Collections.Generic.List<Behaviour> pausedAI = new System.Collections.Generic.List<Behaviour>();
        float aiScan;
        public string Phase { get; private set; }
        /// <summary>Why the last stand gave up (shown by feel_blade status after it is gone).</summary>
        public static string LastError = "";
        public string Film { get { return film != null ? film.Describe() : "no film"; } }
        public bool Done { get; private set; }

        static FeelBladeStand current;

        void Start()
        {
            // one stand at a time: a second one would draw its own camera over this one
            if (current != null && current != this) Destroy(current.gameObject);
            current = this;
            if (source == null || filmMaterial == null) { LastError = "no sword or no film material"; Destroy(gameObject); return; }
            copy = Instantiate(source.gameObject);
            copy.name = "[FeelBladeCopy]";
            // the copy is a prop: no hitbox, no grip, no scripts (renderers are not Behaviours and stay on)
            foreach (var b in copy.GetComponentsInChildren<Behaviour>(true)) b.enabled = false;
            blade = copy.GetComponent<WeaponHitbox>();
            film = BladeBloodFilm.Create(blade, filmMaterial, drop);
            if (film == null)
            {
                LastError = "no steel renderer: blade " + (blade != null) + ", base " + (blade != null && blade.bladeBase != null) + ", renderers under the tip's parent " +
                            (blade != null && blade.bladeTip != null && blade.bladeTip.parent != null ? blade.bladeTip.parent.GetComponentsInChildren<MeshRenderer>(true).Length : -1);
                Destroy(copy); Destroy(gameObject); return;
            }
            LastError = "";
            // the stand: 6 m ahead of the main camera and 2 m above it, over the fighters' heads (2.5 m ahead put it
            // inside the hero, whom the third-person camera follows at that distance)
            var main = Camera.main;
            Vector3 at = main != null ? main.transform.position + Vector3.ProjectOnPlane(main.transform.forward, Vector3.up).normalized * 6f : Vector3.zero;
            at.y = (main != null ? main.transform.position.y : 1.6f) + 2f;
            transform.position = at;
            copy.transform.SetParent(transform, true);
            cam = new GameObject("[FeelBladeCam]").AddComponent<Camera>();

            if (main != null) { cam.CopyFrom(main); cam.depth = main.depth + 20f; }
            cam.fieldOfView = 35f; cam.nearClipPlane = 0.02f;
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            PlaceCam();
            Phase = "cuts";
        }

        /// <summary>The camera: level with the blade, the whole blade in frame, the sun behind its shoulder.</summary>
        void PlaceCam()
        {
            var sun = RenderSettings.sun;
            Vector3 side = sun != null ? Vector3.ProjectOnPlane(-sun.transform.forward, Vector3.up).normalized : Vector3.forward;
            if (side.sqrMagnitude < 1e-3f) side = Vector3.forward;
            float len = Vector3.Distance(blade.bladeBase.position, blade.bladeTip.position);
            float dist = camDistance > 0f ? camDistance : len * 1.35f / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            cam.transform.position = transform.position + Quaternion.Euler(0f, 35f, 0f) * side * dist;
            cam.transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position, Vector3.up);
        }

        Vector3 Mid { get { return Vector3.Lerp(blade.bladeBase.position, blade.bladeTip.position, 0.5f); } }

        /// <summary>Turns the sword so its blade points along 'dir' and spins it 'spin' degrees about that line.</summary>
        void Hold(Vector3 dir, float spin)
        {
            Vector3 cur = (blade.bladeTip.position - blade.bladeBase.position).normalized;
            Quaternion fix = Quaternion.FromToRotation(cur, dir.normalized);
            copy.transform.rotation = fix * copy.transform.rotation;
            copy.transform.RotateAround(Mid, dir.normalized, spin);
            copy.transform.position += transform.position - Mid;
        }

        void LateUpdate()
        {
            if (film == null) return;
            if ((aiScan -= Time.unscaledDeltaTime) <= 0f) { aiScan = 0.5f; PauseFoes(); }
            if (Done) { film.Update(Time.deltaTime); return; }     // the end state stays on screen
            float dt = Time.deltaTime;
            t += dt;
            Vector3 down = Vector3.down;
            if (t < 0.5f)
            {
                Hold(Vector3.Lerp(down, Vector3.right, 0.15f), 0f);
                if (Phase == "cuts")
                {
                    // three cuts at different places along the edge, each its own pattern
                    for (int i = 0; i < 3; i++)
                    {
                        Vector3 c = Vector3.Lerp(blade.bladeBase.position, blade.bladeTip.position, 0.45f + 0.18f * i);
                        film.Cut(c, cam.transform.position - c, 0.7f, 1121 + i * 7919);
                    }
                    Phase = "wet turn";
                }
            }
            else if (t < 6.5f) { Phase = dried ? "dry turn" : "wet turn"; Hold(down, 360f * dt / 6f); }
            else if (t < 8.5f) { Phase = "tip"; Hold(Vector3.Lerp(down, -cam.transform.forward, 0.85f), 0f); }
            else if (t < 11.5f) { Phase = "tip raised"; Hold(Vector3.Lerp(Vector3.up, Vector3.right, 0.5f), 0f); }
            else if (t < 15f) { Phase = "tip lowered"; Hold(Vector3.Lerp(down, Vector3.right, 0.35f), 0f); }
            else if (!dried) { Phase = "time-lapse to dry"; film.AgeBy(85f); dried = true; t = 0.5f; }
            else { Done = true; Phase = "done"; }
            film.Update(dt);
            PlaceCam();
        }

        /// <summary>Pauses every foe's EnemyAI, also the ones a wave spawns later; OnDestroy turns them back on.</summary>
        void PauseFoes()
        {
            foreach (var c in FindObjectsByType<Combatant>(FindObjectsSortMode.None))
                foreach (var b in c.GetComponents<Behaviour>())
                    if (b != null && b.enabled && b.GetType().Name == "EnemyAI") { b.enabled = false; pausedAI.Add(b); }
        }

        void OnDestroy()
        {
            foreach (var b in pausedAI) if (b != null) b.enabled = true;
            pausedAI.Clear();
            if (current == this) current = null;
            if (film != null) film.Dispose();
            if (copy != null) Destroy(copy);
            if (cam != null) Destroy(cam.gameObject);
        }
    }
}
#endif
