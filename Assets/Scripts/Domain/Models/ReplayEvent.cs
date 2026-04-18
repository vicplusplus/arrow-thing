using System;
using Newtonsoft.Json;

/// <summary>
/// One entry in the replay/save event log. Unused fields are omitted from JSON per event type:
/// <list type="bullet">
///   <item>All events carry seq, type, and timestamp.</item>
///   <item>session_start, session_rejoin, start_solve, end_solve — no extra fields.</item>
///   <item>clear, reject — posX/posY (world-space tap position).</item>
///   <item>clear (co-op) — also carries playerId for per-player attribution.</item>
/// </list>
/// Solve-relative timing is derived from timestamps: subtract start_solve timestamp,
/// excluding any session_leave→session_rejoin gaps.
/// </summary>
public sealed class ReplayEvent
{
    /// <summary>Monotonically increasing. Defines event order; timestamps can tie.</summary>
    public int seq;

    /// <summary>Event type — one of the <see cref="ReplayEventType"/> string constants.</summary>
    public string type;

    /// <summary>World-space X of the tap. Used by clear and reject.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public float? posX;

    /// <summary>World-space Y of the tap. Used by clear and reject.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public float? posY;

    /// <summary>Wall-clock time in ISO 8601 format (UTC). Present on all events.</summary>
    public string timestamp;

    /// <summary>
    /// Co-op attribution (v6+). Null in solo replays and in co-op events
    /// that aren't per-player (e.g. session_start). On cleared events,
    /// identifies which player cleared the arrow so the replay viewer can
    /// tint the animation in their color.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Guid? playerId;
}
