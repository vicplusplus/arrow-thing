using System;
using Newtonsoft.Json;

/// <summary>
/// One entry in the replay/save event log. Unused fields are omitted from JSON per event type
/// and per game mode:
/// <list type="bullet">
///   <item>All events carry seq and type.</item>
///   <item>Classic / Coop modes: timestamp (wall-clock ISO) + posX/posY (world-space tap pos).</item>
///   <item>Endless mode: simTime (sim-clock seconds since run start) + cellX/cellY (board ints).
///         Endless writes timestamp too as a debug aid but the verifier reads simTime.</item>
///   <item>session_start, session_rejoin, start_solve, end_solve, topout — no spatial fields.</item>
///   <item>clear, reject, miss — spatial fields populated.</item>
///   <item>clear (co-op) — also carries playerId for per-player attribution.</item>
/// </list>
/// Classic solve-relative timing is derived from timestamps: subtract start_solve timestamp,
/// excluding any session_leave→session_rejoin gaps. Endless reads simTime directly.
/// </summary>
public sealed class ReplayEvent
{
    /// <summary>Monotonically increasing. Defines event order; timestamps can tie.</summary>
    public int seq;

    /// <summary>Event type — one of the <see cref="ReplayEventType"/> string constants.</summary>
    public string type;

    /// <summary>World-space X of the tap. Used by classic / coop clear and reject.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public float? posX;

    /// <summary>World-space Y of the tap. Used by classic / coop clear and reject.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public float? posY;

    /// <summary>Wall-clock time in ISO 8601 format (UTC). Present on all events.</summary>
    public string timestamp;

    /// <summary>
    /// Sim-time seconds since run start (v7+, endless). Server replay
    /// simulator uses this to reproduce push schedule + commit pipeline
    /// deterministically without wall-clock involvement.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public float? simTime;

    /// <summary>Cell X of the tap (v7+, endless). Avoids world↔cell rounding for verifier.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? cellX;

    /// <summary>Cell Y of the tap (v7+, endless). Avoids world↔cell rounding for verifier.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? cellY;

    /// <summary>
    /// Co-op attribution (v6+). Null in solo replays and in co-op events
    /// that aren't per-player (e.g. session_start). On cleared events,
    /// identifies which player cleared the arrow so the replay viewer can
    /// tint the animation in their color.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Guid? playerId;
}
