/// <summary>
/// Top-level game mode, chosen at the main menu and consumed by GameController
/// to branch setup logic. Defaults to <see cref="Classic"/> — the baseline
/// fixed-board speed-clear mode.
///
/// Co-op mode is still flagged separately via <see cref="GameSettings.ActiveLobbyCode"/>
/// for compatibility with existing flow; future cleanup may fold it into this enum.
/// </summary>
public enum GameMode
{
    /// <summary>Fixed-board speed-clear. Board generates once, run ends when board is empty.</summary>
    Classic,

    /// <summary>
    /// Endless mode: initial fill to ~70% occupancy, passive ghost spawn over time,
    /// topout = loss. Prototype for PvP pressure / readability subsystem.
    /// </summary>
    Endless,
}
