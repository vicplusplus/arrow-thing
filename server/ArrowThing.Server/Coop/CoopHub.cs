using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ArrowThing.Server.Auth;
using ArrowThing.Server.Data;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace ArrowThing.Server.Coop;

/// <summary>
/// WebSocket hub for co-op lobbies. Singleton — owns the per-lobby in-memory
/// session state. Dispatches messages by type and broadcasts progress /
/// completion events from the generation worker via Redis pub/sub.
/// </summary>
public class CoopHub
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GenerationProgressBus _progressBus;
    private readonly ILogger<CoopHub> _logger;
    private readonly ConcurrentDictionary<string, LobbyRoom> _rooms = new();
    private bool _busSubscribed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public CoopHub(
        IServiceScopeFactory scopeFactory,
        GenerationProgressBus progressBus,
        ILogger<CoopHub> logger
    )
    {
        _scopeFactory = scopeFactory;
        _progressBus = progressBus;
        _logger = logger;
    }

    /// <summary>
    /// Subscribes to the generation progress bus. Call once at startup.
    /// Safe to call multiple times — only the first call subscribes.
    /// </summary>
    public async Task EnsureSubscribedAsync()
    {
        if (_busSubscribed)
            return;
        _busSubscribed = true;
        await _progressBus.SubscribeAsync(OnBusMessageAsync);
    }

    private async Task OnBusMessageAsync(string lobbyCode, GenerationProgressBus.BusMessage msg)
    {
        var code = NormalizeCode(lobbyCode);
        if (!_rooms.TryGetValue(code, out var room))
            return;

        CoopMessage? envelope = msg.Type switch
        {
            "gen_progress" => new CoopMessage
            {
                Type = "gen_progress",
                Payload = ToJsonElement(new GenProgressPayload(msg.Pct ?? 0)),
            },
            "gen_complete" => new CoopMessage { Type = "gen_complete" },
            "lobby_failed" => new CoopMessage
            {
                Type = "lobby_failed",
                Payload = ToJsonElement(new DisconnectPayload(msg.Reason ?? "unknown")),
            },
            _ => null,
        };
        if (envelope == null)
            return;

        await BroadcastAsync(code, envelope);

        // On gen_complete, push the snapshot to all connected clients so they
        // don't have to manually re-send hello.
        if (msg.Type == "gen_complete")
        {
            await SendSnapshotToRoomAsync(room);
        }
    }

    private async Task SendSnapshotToRoomAsync(LobbyRoom room)
    {
        Lobby? lobby;
        byte[]? snapshotBytes;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            lobby = await db.Lobbies.FirstOrDefaultAsync(l => l.Code == room.Code);
            if (lobby == null)
                return;
            var snapRepo = scope.ServiceProvider.GetRequiredService<LobbySnapshotRepository>();
            var snap = await snapRepo.LoadAsync(lobby.Id);
            snapshotBytes = snap?.Data;
        }

        if (snapshotBytes == null)
            return;

        var meta = new CoopMessage
        {
            Type = "snapshot",
            Payload = ToJsonElement(new SnapshotMetaPayload(1, snapshotBytes.Length)),
        };

        foreach (var kvp in room.Connections)
        {
            try
            {
                await SendAsync(kvp.Value, meta, CancellationToken.None);
                await SendBinaryAsync(kvp.Value, snapshotBytes, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[CoopHub] Failed to send snapshot to user {UserId}",
                    kvp.Key
                );
            }
        }
    }

    private async Task BroadcastAsync(string normalizedCode, CoopMessage msg)
    {
        if (!_rooms.TryGetValue(normalizedCode, out var room))
            return;
        foreach (var kvp in room.Connections)
        {
            try
            {
                await SendAsync(kvp.Value, msg, CancellationToken.None);
            }
            catch
            {
                // Failed sockets get cleaned up by their own receive loops.
            }
        }
    }

    private static async Task SendBinaryAsync(WebSocket socket, byte[] data, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
            return;
        await socket.SendAsync(
            new ArraySegment<byte>(data),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            ct
        );
    }

    /// <summary>Total active rooms (for diagnostics/tests).</summary>
    public int RoomCount => _rooms.Count;

    /// <summary>Connected user count for a lobby (for diagnostics/tests). 0 if no room.</summary>
    public int ConnectedCount(string code) =>
        _rooms.TryGetValue(NormalizeCode(code), out var room) ? room.Connections.Count : 0;

    /// <summary>
    /// Handle a single incoming WebSocket connection lifecycle. Caller must
    /// have already accepted the WebSocket via <c>HttpContext.WebSockets.AcceptWebSocketAsync</c>.
    /// </summary>
    public async Task HandleConnectionAsync(
        WebSocket socket,
        Lobby lobby,
        Guid userId,
        CancellationToken ct
    )
    {
        var code = NormalizeCode(lobby.Code);
        var room = _rooms.GetOrAdd(code, _ => new LobbyRoom(code));
        room.Connections[userId] = socket;

        _logger.LogInformation(
            "[CoopHub] User {UserId} connected to lobby {Code} (now {Count} connected)",
            userId,
            code,
            room.Connections.Count
        );

        try
        {
            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "client closed",
                        CancellationToken.None
                    );
                    break;
                }

                if (ms.Length == 0)
                    continue;

                CoopMessage? msg;
                try
                {
                    msg = JsonSerializer.Deserialize<CoopMessage>(ms.ToArray(), JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "[CoopHub] Bad JSON from {UserId}", userId);
                    continue;
                }
                if (msg == null || string.IsNullOrEmpty(msg.Type))
                    continue;

                await HandleMessageAsync(msg, socket, lobby, userId, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _logger.LogInformation(
                "[CoopHub] WebSocket error for user {UserId} on lobby {Code}: {Message}",
                userId,
                code,
                ex.Message
            );
        }
        finally
        {
            room.Connections.TryRemove(userId, out _);
            if (room.Connections.IsEmpty)
                _rooms.TryRemove(code, out _);

            _logger.LogInformation(
                "[CoopHub] User {UserId} disconnected from lobby {Code}",
                userId,
                code
            );
        }
    }

    private async Task HandleMessageAsync(
        CoopMessage msg,
        WebSocket socket,
        Lobby lobby,
        Guid userId,
        CancellationToken ct
    )
    {
        switch (msg.Type)
        {
            case "hello":
                await HandleHelloAsync(socket, lobby, userId, ct);
                break;

            case "heartbeat":
                // Phase 3: receipt only, no reply. Phase 6+ will track AFK state.
                break;

            case "echo":
                // Debug round-trip — returns the same payload tagged echo_reply.
                await SendAsync(
                    socket,
                    new CoopMessage
                    {
                        Type = "echo_reply",
                        Seq = msg.Seq,
                        Payload = msg.Payload,
                    },
                    ct
                );
                break;

            case "goodbye":
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "goodbye",
                    CancellationToken.None
                );
                break;

            default:
                _logger.LogWarning(
                    "[CoopHub] Unknown message type {Type} from user {UserId}",
                    msg.Type,
                    userId
                );
                break;
        }
    }

    private async Task HandleHelloAsync(
        WebSocket socket,
        Lobby lobbyAtConnect,
        Guid userId,
        CancellationToken ct
    )
    {
        // Refetch current lobby state — it may have transitioned (Generating
        // → Active, etc.) since the connection was accepted.
        Lobby? current;
        byte[]? snapshotBytes = null;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            current = await db.Lobbies.FindAsync(lobbyAtConnect.Id);
            if (current == null)
            {
                await SendAsync(
                    socket,
                    new CoopMessage
                    {
                        Type = "disconnect",
                        Payload = ToJsonElement(new DisconnectPayload("lobby_not_found")),
                    },
                    ct
                );
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "lobby_not_found",
                    CancellationToken.None
                );
                return;
            }
            if (current.Status == LobbyStatus.Active)
            {
                var snapRepo = scope.ServiceProvider.GetRequiredService<LobbySnapshotRepository>();
                var snap = await snapRepo.LoadAsync(current.Id);
                snapshotBytes = snap?.Data;
            }
        }

        // Always send welcome first.
        await SendAsync(
            socket,
            new CoopMessage
            {
                Type = "welcome",
                Seq = 0,
                Payload = ToJsonElement(
                    new WelcomePayload(userId, current.Code, current.Name, (short)current.Status)
                ),
            },
            ct
        );

        switch (current.Status)
        {
            case LobbyStatus.Generating:
                // Subscriber will deliver progress events via the bus.
                break;

            case LobbyStatus.Active:
                if (snapshotBytes != null)
                {
                    await SendAsync(
                        socket,
                        new CoopMessage
                        {
                            Type = "snapshot",
                            Payload = ToJsonElement(
                                new SnapshotMetaPayload(1, snapshotBytes.Length)
                            ),
                        },
                        ct
                    );
                    await SendBinaryAsync(socket, snapshotBytes, ct);
                }
                break;

            case LobbyStatus.GenerationFailed:
                await SendAsync(
                    socket,
                    new CoopMessage
                    {
                        Type = "lobby_failed",
                        Payload = ToJsonElement(new DisconnectPayload("generation_failed")),
                    },
                    ct
                );
                break;

            case LobbyStatus.Completed:
                await SendAsync(
                    socket,
                    new CoopMessage
                    {
                        Type = "disconnect",
                        Payload = ToJsonElement(new DisconnectPayload("lobby_completed")),
                    },
                    ct
                );
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "completed",
                    CancellationToken.None
                );
                break;

            case LobbyStatus.Deleted:
                await SendAsync(
                    socket,
                    new CoopMessage
                    {
                        Type = "disconnect",
                        Payload = ToJsonElement(new DisconnectPayload("lobby_deleted")),
                    },
                    ct
                );
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "deleted",
                    CancellationToken.None
                );
                break;
        }
    }

    private static async Task SendAsync(WebSocket socket, CoopMessage msg, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
            return;
        var json = JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
        await socket.SendAsync(
            new ArraySegment<byte>(json),
            WebSocketMessageType.Text,
            endOfMessage: true,
            ct
        );
    }

    private static JsonElement ToJsonElement<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    private static string NormalizeCode(string code) => (code ?? "").Trim().ToUpperInvariant();

    // -- JWT validation for WebSocket query string --------------------------

    /// <summary>
    /// Validates a JWT from the WebSocket query string. Returns the user ID on
    /// success, or null on failure. Also verifies the security stamp matches
    /// the database (matches the existing API auth middleware).
    /// </summary>
    public static async Task<Guid?> ValidateTokenAsync(
        string? token,
        JwtHelper jwt,
        AppDbContext db,
        ILogger logger
    )
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var handler = new JwtSecurityTokenHandler();
        try
        {
            var principal = handler.ValidateToken(token, jwt.GetValidationParameters(), out _);
            // JwtBearer maps `sub` to ClaimTypes.NameIdentifier on validation; check both.
            var sub =
                principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
            var stamp = principal.FindFirstValue("security_stamp");
            if (sub == null || stamp == null)
                return null;

            if (!Guid.TryParse(sub, out var userId))
                return null;

            var user = await db.Users.FindAsync(userId);
            if (user == null || user.SecurityStamp != stamp)
                return null;

            return userId;
        }
        catch (SecurityTokenException ex)
        {
            logger.LogInformation("[CoopHub] Invalid JWT: {Message}", ex.Message);
            return null;
        }
    }
}

internal class LobbyRoom
{
    public string Code { get; }
    public ConcurrentDictionary<Guid, WebSocket> Connections { get; } = new();

    public LobbyRoom(string code)
    {
        Code = code;
    }
}
