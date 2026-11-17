namespace GeorgianKnight.Core
{
    /// <summary>
    /// Implemented by the UI layer when it owns in-game menus (pause, inventory, map …). Gameplay code uses it to open
    /// screens and to know whether a fallback is needed (e.g. a bare test scene without UI handles pause itself).
    /// </summary>
    public interface IMenuController
    {
        bool IsAnyMenuOpen { get; }
        /// <summary>Opens a named in-game screen: "pause", "inventory", "map", "journal", "skills", "shop:&lt;id&gt;", "death".</summary>
        void Open(string screen);
        void CloseAll();
    }
}
