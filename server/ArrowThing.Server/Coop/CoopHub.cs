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
/// session state. Phase 3 implements the bare wiring: JWT auth via query
/// string, hello/welcome handshake, heartbeat, goodbye, and a debug echo
/// handler for verification. No gameplay logic yet.
/// </summary>
public class CoopHub
{
    private readonly ILogger<CoopHub> _logger;
    private readonly ConcurrentDictionary<string, LobbyRoom> _rooms = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public CoopHub(ILogger<CoopHub> logger)
    {
        _logger = logger;
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
                await SendAsync(
                    socket,
                    new CoopMessage
                    {
                        Type = "welcome",
                        Seq = 0,
                        Payload = ToJsonElement(
                            new WelcomePayload(userId, lobby.Code, lobby.Name, (short)lobby.Status)
                        ),
                    },
                    ct
                );
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
