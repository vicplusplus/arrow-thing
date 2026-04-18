namespace ArrowThing.Server.Coop;

// -- REST request/response ---------------------------------------------------

public record CreateLobbyRequest(string Name, int? Width = null, int? Height = null);

public record RenameLobbyRequest(string Name);

public record LobbyResponse(
    Guid Id,
    string Code,
    string Name,
    Guid OwnerUserId,
    string OwnerDisplayName,
    int Width,
    int Height,
    short Status,
    DateTime CreatedAt,
    DateTime LastActivityAt,
    string ShareUrl
);

public record LobbyListEntry
{
    public Guid Id { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string OwnerDisplayName { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public short Status { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastActivityAt { get; init; }
    public bool YouAreOwner { get; init; }
    public int YourClearCount { get; init; }
    public string ShareUrl { get; init; } = "";
}

public record LobbyListResponse(IReadOnlyList<LobbyListEntry> Entries, int Page, bool HasMore);

// -- WebSocket envelope ------------------------------------------------------

/// <summary>
/// All WebSocket messages share this envelope shape.
/// `Type` is the discriminator; `Seq` is monotonic (server-assigned for
/// server-origin messages, client-assigned for client-origin); `Payload` is
/// type-specific JSON.
/// </summary>
public class CoopMessage
{
    public string Type { get; set; } = "";
    public long Seq { get; set; }
    public System.Text.Json.JsonElement? Payload { get; set; }
}

// -- WebSocket payloads (Phase 3 minimal set) --------------------------------

public record WelcomePayload(Guid YourUserId, string LobbyCode, string LobbyName, short Status);

public record EchoPayload(string Message);

// -- Phase 4 payloads --------------------------------------------------------

public record GenProgressPayload(int Pct);

public record GenCompletePayload();

public record SnapshotMetaPayload(int Version, int SizeBytes);

public record DisconnectPayload(string Reason);

// -- Phase 6 payloads --------------------------------------------------------

public record ClearAttemptPayload(float TapX, float TapY, string ClientTsUtc, long ClientSeq);

public record ClearedPayload(
    Guid PlayerId,
    float TapX,
    float TapY,
    string TsUtc,
    long Seq,
    int NewClearCount,
    string Color,
    string DisplayName
);

public record RejectedDepPayload(Guid PlayerId, float TapX, float TapY, string TsUtc, long Seq);

public record RejectedRacePayload(long ClientSeq, string Reason);

public record RejectedRatePayload(long ClientSeq, int RetryAfterMs);

public record LobbyCompletedPayload(string CompletedAtUtc);

public record HeartbeatPayload(bool Focused, string LastInputTsUtc);

// -- Phase 7 payloads --------------------------------------------------------

/// <summary>
/// One roster entry. Sent in `roster_full` and `roster_patch` messages.
/// `AccumulatedMillis` is the player's own per-player solve time (Phase 7:
/// client-reported via <c>timer_update</c>). `Online` is false for players
/// whose connection dropped but whose registration is still alive.
/// </summary>
public record RosterEntryPayload(
    Guid UserId,
    string DisplayName,
    string Color,
    int ClearCount,
    long AccumulatedMillis,
    bool Online
);

/// <summary>
/// Full roster snapshot. Sent once after <c>snapshot</c> on each connection.
/// </summary>
public record RosterFullPayload(IReadOnlyList<RosterEntryPayload> Players);

/// <summary>
/// Incremental roster diff. <c>Upsert</c> carries added or changed entries;
/// <c>Remove</c> carries the user ids of entries removed (unused today —
/// kept for future expiry semantics).
/// </summary>
public record RosterPatchPayload(
    IReadOnlyList<RosterEntryPayload> Upsert,
    IReadOnlyList<Guid> Remove
);

/// <summary>
/// Client-side report of the local player's per-player timer. Server
/// validates <c>AccumulatedMillis</c> is monotonically non-decreasing and
/// persists it to <c>LobbyRegistrations</c>.
/// </summary>
public record TimerUpdatePayload(long AccumulatedMillis);
