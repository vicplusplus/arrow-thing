namespace ArrowThing.Server.Coop;

// -- REST request/response ---------------------------------------------------

public record CreateLobbyRequest(string Name);

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

public record LobbyListEntry(
    Guid Id,
    string Code,
    string Name,
    string OwnerDisplayName,
    int Width,
    int Height,
    short Status,
    DateTime CreatedAt,
    DateTime LastActivityAt
);

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
