namespace GeorgianKnight
{
    // Presentation contracts between gameplay systems and the UI layer. The UI registers its implementations as
    // services; gameplay checks for them and changes its behaviour (never references the UI assembly).

    /// <summary>
    /// Registered by the UI when it can show dialogue (speaker, line, choices) and drive the dialogue runner
    /// (Advance / Choose / Skip). The dialogue runner refuses to start a conversation when none is registered,
    /// so a conversation can never soft-lock the game.
    /// </summary>
    public interface IDialoguePresenter { }

    /// <summary>
    /// Registered by the UI when it shows a death screen. While registered, the checkpoint service does not respawn
    /// on a timer; it waits for <see cref="RespawnRequested"/> (published by the death screen's Retry).
    /// </summary>
    public interface IDeathPresenter { }
}
