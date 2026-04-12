namespace ArrowThing.Server.Games;

/// <summary>
/// Gates score submission based on the <see cref="ReplayData.version"/> field.
/// Replay version is independent of the client's <see cref="ReplayData.gameVersion"/>
/// (which is informational). Bump <see cref="MinReplayVersion"/> whenever a change
/// to board generation, RNG, or verification semantics makes replays produced by
/// older clients unverifiable against the current server code — outdated clients
/// get a clear "please update" rejection instead of being flagged as cheaters.
/// </summary>
public static class ReplayVersionPolicy
{
    /// <summary>
    /// Minimum <see cref="ReplayData.version"/> accepted by the submit endpoint.
    /// History:
    /// <list type="bullet">
    ///   <item>v1 — initial replay schema.</item>
    ///   <item>v2 — added <c>boardSnapshot</c>.</item>
    ///   <item>v3 — added <c>gameVersion</c>.</item>
    ///   <item>v4 — post-RNG rewrite; replays from &lt;v4 clients cannot be regenerated
    ///   deterministically on the current server and must be rejected before verification.</item>
    /// </list>
    /// </summary>
    public const int MinReplayVersion = 4;

    /// <summary>
    /// Returns true if <paramref name="replayVersion"/> meets the server's minimum.
    /// </summary>
    public static bool IsAllowed(int replayVersion) => replayVersion >= MinReplayVersion;

    /// <summary>
    /// Human-readable rejection reason returned as the API error body when a client's
    /// replay version is below the minimum.
    /// </summary>
    public static string RejectionMessage(int replayVersion) =>
        $"Replay version {replayVersion} is no longer supported "
        + $"(minimum v{MinReplayVersion}). Please update the app to submit scores.";
}
