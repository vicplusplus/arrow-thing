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
