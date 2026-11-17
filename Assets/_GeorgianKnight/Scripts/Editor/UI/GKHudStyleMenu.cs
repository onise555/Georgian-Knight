using GeorgianKnight.UI;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Creates or refreshes <c>Assets/_GeorgianKnight/UI/Resources/GKHudStyle.asset</c> from the shipped files:
    /// the serif already in the project (Cinematics, OFL), the sans from UI/Fonts once Asset QA has passed it, and
    /// the vine cross from UI/Textures. Missing files are reported, not invented.
    /// </summary>
    public static class GKHudStyleMenu
    {
        const string StylePath = "Assets/_GeorgianKnight/UI/Resources/GKHudStyle.asset";
        const string SerifPath = "Assets/_GeorgianKnight/Cinematics/Intro/Fonts/NotoSerifGeorgian.ttf";
        const string SansPath = "Assets/_GeorgianKnight/UI/Fonts/NotoSansGeorgian-Regular.ttf";
        const string EmblemPath = "Assets/_GeorgianKnight/UI/Textures/emblem_vinecross_hud.png";
        const string PlatePath = "Assets/_GeorgianKnight/UI/Textures/plate_didgori_dawn.jpg";
        const string CreditsPath = "Assets/_GeorgianKnight/CREDITS.txt";

        [MenuItem("GK/UI/Create HUD Style")]
        public static void Create()
        {
            var style = AssetDatabase.LoadAssetAtPath<GKHudStyle>(StylePath);
            if (style == null)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StylePath));
                style = ScriptableObject.CreateInstance<GKHudStyle>();
                AssetDatabase.CreateAsset(style, StylePath);
            }
            style.serif = AssetDatabase.LoadAssetAtPath<Font>(SerifPath);
            style.sans = AssetDatabase.LoadAssetAtPath<Font>(SansPath);
            style.emblem = AssetDatabase.LoadAssetAtPath<Texture2D>(EmblemPath);
            style.plate = AssetDatabase.LoadAssetAtPath<Texture2D>(PlatePath);
            style.credits = AssetDatabase.LoadAssetAtPath<TextAsset>(CreditsPath);
            EditorUtility.SetDirty(style);
            AssetDatabase.SaveAssets();
            Debug.Log("[GKHud] style: serif " + (style.serif != null ? "ok" : "MISSING " + SerifPath)
                + ", sans " + (style.sans != null ? "ok" : "missing (the serif is used) " + SansPath)
                + ", emblem " + (style.emblem != null ? "ok" : "MISSING " + EmblemPath)
                + ", plate " + (style.plate != null ? "ok" : "MISSING " + PlatePath)
                + ", credits " + (style.credits != null ? "ok" : "MISSING " + CreditsPath));
        }
    }
}
