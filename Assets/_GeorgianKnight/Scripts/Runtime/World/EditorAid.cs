using UnityEngine;

namespace GeorgianKnight.World
{
    /// <summary>
    /// A level-design aid that shows in the editor (and in World's edit-mode captures) but never in Play: guide rings,
    /// spawn pillars, the invisible boundary's greybox blocks, stand-ins for the host and the melee, the scale figure,
    /// the metrics zoo. On entering Play its renderers switch off; colliders stay, so a boundary still blocks. EditorOnly
    /// tags keep the same objects out of player builds; this covers Play in the editor, where the Director play-tests.
    /// </summary>
    public class EditorAid : MonoBehaviour
    {
        void Awake() { Show(false); }

        /// <summary>World's own Play tests only (bridge `world_aids show`): never left on for the Director's play-tests.</summary>
        public void Show(bool on)
        {
            var renderers = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++) renderers[i].enabled = on;
        }
    }
}
