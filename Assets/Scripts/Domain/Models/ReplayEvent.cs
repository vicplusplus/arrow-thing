using System;
using Newtonsoft.Json;

/// <summary>
/// One entry in the replay/save event log. Unused fields are omitted from JSON per event type
/// and per game mode:
/// <list type="bullet">
///   <item>All events carry seq and type.</item>
///   <item>Tap events (clear / reject / miss) carry posX/posY — float grid coordinates
///         (1 unit per cell, origin at the board corner; identical to world space because
///         <see cref="BoardCoords"/> is 1:1). The cell the tap resolved to is derivable
///         on the verifier via <c>Math.Round(posX)</c>, <c>Math.Round(posY)</c>; recording
///         the float retains subcell precision for any future tap-forgiveness logic.</item>
///   <item>Classic / Coop modes: timestamp (wall-clock ISO).</item>
///   <item>Endless mode: simTime (sim-clock seconds since run start). Endless writes
///         timestamp too as a debug aid but the verifier reads simTime.</item>
///   <item>session_start, session_rejoin, start_solve, end_solve, topout — no spatial fields.</item>
///   <item>clear (co-op) — also carries playerId for per-player attribution.</item>
/// </list>
///
/// <para><b>Why two clocks (timestamp vs simTime)?</b> They measure different
/// things; one is not derivable from the other.</para>
/// <list type="bullet">
///   <item><c>timestamp</c> is wall-clock UTC. Continues advancing during
///   pauses, focus loss, alt-tab, framerate hitches.</item>
///   <item><c>simTime</c> is the game's deterministic clock — accumulated
///   <c>Time.deltaTime</c> on the client. Stops during pause / focus
///   loss because Unity stops calling Update.</item>
/// </list>
/// <para>Classic solve-relative timing comes from wall-clock timestamps
/// (subtract <c>start_solve</c>, exclude session_leave→session_rejoin gaps);
/// game state in classic only advances on player taps so wall-clock deltas
/// faithfully represent in-game time.</para>
/// <para>Endless's game loop is time-driven (push schedule, commit deadlines,
/// danger pulse, run duration). A wall-clock-replay would fire push ticks
/// during pauses and produce different clears/combo/duration than the
/// client recorded — verification would fail. Sim-time strips out pauses,
/// so the server-side simulator reproduces the run exactly.</para>
/// </summary>
public sealed class ReplayEvent
{
    /// <summary>Monotonically increasing. Defines event order; timestamps can tie.</summary>
    public int seq;

    /// <summary>Event type — one of the <see cref="ReplayEventType"/> string constants.</summary>
    public string type;

    /// <summary>
    /// Float grid X of the tap (1 unit per cell, origin at the board corner).
    /// Identical numerically to the legacy world-space X used by classic / coop
    /// because <see cref="BoardCoords"/> is 1:1; old replays read straight through.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public float? posX;

    /// <summary>
    /// Float grid Y of the tap (1 unit per cell, origin at the board corner).
    /// Identical numerically to the legacy world-space Y; old replays read straight through.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public float? posY;

    /// <summary>Wall-clock time in ISO 8601 format (UTC). Present on all events.</summary>
    public string timestamp;

    /// <summary>
    /// Sim-time seconds since run start (v7+, endless). Distinct from
    /// <see cref="timestamp"/>: sim time stops advancing during pause /
    /// alt-tab / focus loss because Unity stops calling Update, while
    /// wall-clock keeps ticking. Endless's game loop (push schedule,
    /// commit deadlines, run duration) is sim-time-driven, so the server
    /// replay simulator reads simTime directly to reproduce the same
    /// outcomes — a wall-clock-driven replay would fire phantom pushes
    /// during a player's pause and mismatch the recorded stats. Null on
    /// classic / coop events where wall-clock is sufficient.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public float? simTime;

    /// <summary>
    /// Co-op attribution (v6+). Null in solo replays and in co-op events
    /// that aren't per-player (e.g. session_start). On cleared events,
    /// identifies which player cleared the arrow so the replay viewer can
    /// tint the animation in their color.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Guid? playerId;
}
