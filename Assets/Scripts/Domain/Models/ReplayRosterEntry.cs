using System;

/// <summary>
/// One entry in a v6+ co-op replay's roster header. Captures the player's
/// identity and color as they appeared in the lobby so the replay viewer
/// can reproduce per-player attribution deterministically even after
/// display-name or color changes in the user's profile.
/// </summary>
public sealed class ReplayRosterEntry
{
    public Guid playerId;
    public string displayName;

    /// <summary>Hex color string, e.g. <c>#FF3366</c>. Unquoted by convention.</summary>
    public string color;
}
