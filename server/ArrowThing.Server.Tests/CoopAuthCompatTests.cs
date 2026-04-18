using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using ArrowThing.Server.Auth;
using ArrowThing.Server.Coop;
using Microsoft.Extensions.DependencyInjection;

namespace ArrowThing.Server.Tests;

/// <summary>
/// Phase 1E: co-op WebSocket auth compat for Phase 1C (15-min access JWT) and
/// Phase 1D (HttpOnly cookie). Covers the <c>arrow_access</c> cookie fallback
/// on the WS upgrade and the server-side sanitization of generation-failure
/// reasons broadcast to clients.
/// </summary>
public class CoopAuthCompatTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public CoopAuthCompatTests(TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Device-Id", Guid.NewGuid().ToString("N"));
    }

    public void Dispose() => _client.Dispose();

    private async Task<(AuthResponse Auth, string LobbyCode)> SetupLobbyAsync()
    {
        var email = $"ws-auth-{Guid.NewGuid():N}@example.com";
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                password = "password123",
                displayName = "WsAuthTest",
            }
        );
        var code = _factory.FakeEmail.SentEmails.FindLast(e =>
            e.To == email && e.Type == "verification"
        );
        var verifyResponse = await _client.PostAsJsonAsync(
            "/api/auth/verify-code",
            new { email, code = code!.Token }
        );
        var auth = (await verifyResponse.Content.ReadFromJsonAsync<AuthResponse>())!;

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );
        var create = await _client.PostAsJsonAsync("/api/lobbies", new { name = "Ws Auth Lobby" });
        var lobby = (await create.Content.ReadFromJsonAsync<LobbyResponse>())!;
        return (auth, lobby.Code);
    }

    // -- Cookie fallback on WS upgrade ---------------------------------------

    [Fact]
    public async Task Connect_CookieFallback_NoQueryToken_Succeeds()
    {
        var (auth, lobbyCode) = await SetupLobbyAsync();

        // WebGL cookie-mode client never sets ?token= (it has no access to the
        // HttpOnly cookie from JS). The browser attaches arrow_access to the
        // upgrade request automatically; the server must accept that.
        var wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = req =>
        {
            req.Headers["Cookie"] = $"{CookieIssuer.AccessCookieName}={auth.Token}";
        };

        using var socket = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/ws/coop/{lobbyCode}"),
            CancellationToken.None
        );

        Assert.Equal(WebSocketState.Open, socket.State);
        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "test done",
            CancellationToken.None
        );
    }

    [Fact]
    public async Task Connect_CookieFallback_InvalidCookie_Fails()
    {
        var (_, lobbyCode) = await SetupLobbyAsync();

        var wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = req =>
        {
            req.Headers["Cookie"] = $"{CookieIssuer.AccessCookieName}=not-a-jwt";
        };

        // Kestrel-in-memory returns InvalidOperationException on non-101
        // upgrade, matching the shape used by the sibling CoopHubTests.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await wsClient.ConnectAsync(
                new Uri($"ws://localhost/ws/coop/{lobbyCode}"),
                CancellationToken.None
            );
        });
    }

    [Fact]
    public async Task Connect_QueryTokenStillWorks_AlongsideCookie()
    {
        // Regression guard: the bearer path (editor / native clients) must
        // keep working after the cookie fallback was added.
        var (auth, lobbyCode) = await SetupLobbyAsync();
        var wsClient = _factory.Server.CreateWebSocketClient();

        using var socket = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/ws/coop/{lobbyCode}?token={auth.Token}"),
            CancellationToken.None
        );

        Assert.Equal(WebSocketState.Open, socket.State);
        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "test done",
            CancellationToken.None
        );
    }

    // -- Generation-failure reason sanitization ------------------------------

    [Fact]
    public async Task LobbyFailed_DoesNotLeakRawReason()
    {
        var (auth, lobbyCode) = await SetupLobbyAsync();
        var wsClient = _factory.Server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/ws/coop/{lobbyCode}?token={auth.Token}"),
            CancellationToken.None
        );

        // Establish the room: the hub only routes bus messages to rooms whose
        // lobby code is present in `_rooms`, which is populated on `hello`.
        var helloBytes = JsonSerializer.SerializeToUtf8Bytes(
            new CoopMessage { Type = "hello", Seq = 1 },
            JsonOptions
        );
        await socket.SendAsync(
            new ArraySegment<byte>(helloBytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None
        );
        await ReceiveAsync(socket); // welcome

        // Drain any post-welcome server-initiated messages (roster_full,
        // lobby_full, etc. emitted by later phases) until the bus publish
        // arrives. We cap the drain with a short timeout so a test regression
        // shows up as a test failure instead of a hang.
        using var scope = _factory.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<GenerationProgressBus>();
        await bus.PublishFailedAsync(
            lobbyCode,
            "NullReferenceException at BoardGeneration.cs:123 (secret leak)"
        );

        CoopMessage failed;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var next = await ReceiveAsync(socket, cts.Token);
            if (next.Type == "lobby_failed")
            {
                failed = next;
                break;
            }
        }
        Assert.NotNull(failed.Payload);
        var payload = failed.Payload!.Value.Deserialize<DisconnectPayload>(JsonOptions);
        Assert.NotNull(payload);
        // The closed-set reason is broadcast; the raw exception message stays
        // in the server logs and never reaches untrusted clients.
        Assert.Equal("generation_failed", payload!.Reason);
        Assert.DoesNotContain("secret", payload.Reason);
        Assert.DoesNotContain("BoardGeneration", payload.Reason);

        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "test done",
            CancellationToken.None
        );
    }

    private static async Task<CoopMessage> ReceiveAsync(
        WebSocket socket,
        CancellationToken ct = default
    )
    {
        var ms = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonSerializer.Deserialize<CoopMessage>(ms.ToArray(), JsonOptions)!;
    }
}
