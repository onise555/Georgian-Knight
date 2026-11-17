using UnityEngine;

namespace GeorgianKnight.UI
{
    /// <summary>
    /// The HUD's fonts and marks, loaded from <c>Resources/GKHudStyle</c> (<c>Assets/_GeorgianKnight/UI/Resources</c>).
    /// Created by <c>GK/UI/Create HUD Style</c>; without it the HUD falls back to OS fonts and no emblem.
    /// </summary>
    [CreateAssetMenu(menuName = "Georgian Knight/UI/HUD Style", fileName = "GKHudStyle")]
    public sealed class GKHudStyle : ScriptableObject
    {
        public const string ResourcePath = "GKHudStyle";

        [Tooltip("Names, messages, the book: Noto Serif Georgian (OFL).")]
        public Font serif;
        [Tooltip("Prompts, keys, numbers: Noto Sans Georgian (OFL). Empty = the serif.")]
        public Font sans;
        [Tooltip("Bakur's vine cross (CHARACTERS §2), 2x of 48 x 64.")]
        public Texture2D emblem;
        [Tooltip("The menus' painted plate: Didgori before sunrise (Docs/UI/Menus/plate_didgori_dawn_a.jpg).")]
        public Texture2D plate;
        [Tooltip("Assets/_GeorgianKnight/CREDITS.txt: the credits screen lists its third-party lines.")]
        public TextAsset credits;
    }
}
