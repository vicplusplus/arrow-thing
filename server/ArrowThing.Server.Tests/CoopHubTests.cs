using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ArrowThing.Server.Auth;
using ArrowThing.Server.Coop;
using Microsoft.Extensions.DependencyInjection;

namespace ArrowThing.Server.Tests;

public class CoopHubTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public CoopHubTests(TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    private async Task<(AuthResponse Auth, string LobbyCode)> SetupLobbyAsync()
    {
        var email = $"hub-{Guid.NewGuid():N}@example.com";
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                password = "password123",
                displayName = "HubTest",
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
        var create = await _client.PostAsJsonAsync("/api/lobbies", new { name = "Hub Test Lobby" });
        var lobby = (await create.Content.ReadFromJsonAsync<LobbyResponse>())!;
        return (auth, lobby.Code);
    }

    private async Task<WebSocket> ConnectAsync(string lobbyCode, string token)
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        var uri = new Uri($"ws://localhost/ws/coop/{lobbyCode}?token={token}");
        return await wsClient.ConnectAsync(uri, CancellationToken.None);
    }

    private static async Task SendAsync(WebSocket socket, CoopMessage msg)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None
        );
    }

    private static async Task<CoopMessage> ReceiveAsync(WebSocket socket)
    {
        var ms = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                CancellationToken.None
            );
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonSerializer.Deserialize<CoopMessage>(ms.ToArray(), JsonOptions)!;
    }

    [Fact]
    public async Task Connect_NoToken_ReturnsUnauthorized()
    {
        var (_, lobbyCode) = await SetupLobbyAsync();
        var wsClient = _factory.Server.CreateWebSocketClient();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await wsClient.ConnectAsync(
                new Uri($"ws://localhost/ws/coop/{lobbyCode}"),
                CancellationToken.None
            );
        });
    }

    [Fact]
    public async Task Connect_InvalidToken_ReturnsUnauthorized()
    {
        var (_, lobbyCode) = await SetupLobbyAsync();
        var wsClient = _factory.Server.CreateWebSocketClient();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await wsClient.ConnectAsync(
                new Uri($"ws://localhost/ws/coop/{lobbyCode}?token=garbage"),
                CancellationToken.None
            );
        });
    }

    [Fact]
    public async Task Connect_LobbyNotFound_ReturnsNotFound()
    {
        var (auth, _) = await SetupLobbyAsync();
        var wsClient = _factory.Server.CreateWebSocketClient();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await wsClient.ConnectAsync(
                new Uri($"ws://localhost/ws/coop/ZZZZZZ?token={auth.Token}"),
                CancellationToken.None
            );
        });
    }

    [Fact]
    public async Task Hello_ReturnsWelcome()
    {
        var (auth, lobbyCode) = await SetupLobbyAsync();
        using var socket = await ConnectAsync(lobbyCode, auth.Token);

        await SendAsync(socket, new CoopMessage { Type = "hello", Seq = 1 });
        var reply = await ReceiveAsync(socket);

        Assert.Equal("welcome", reply.Type);
        Assert.NotNull(reply.Payload);
        var payload = reply.Payload!.Value.Deserialize<WelcomePayload>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(lobbyCode, payload!.LobbyCode);
        Assert.Equal("Hub Test Lobby", payload.LobbyName);

        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "test done",
            CancellationToken.None
        );
    }

    [Fact]
    public async Task Echo_RoundTrip()
    {
        var (auth, lobbyCode) = await SetupLobbyAsync();
        using var socket = await ConnectAsync(lobbyCode, auth.Token);

        var payload = JsonSerializer.SerializeToElement(new EchoPayload("ping"), JsonOptions);
        await SendAsync(
            socket,
            new CoopMessage
            {
                Type = "echo",
                Seq = 42,
                Payload = payload,
            }
        );
        var reply = await ReceiveAsync(socket);

        Assert.Equal("echo_reply", reply.Type);
        Assert.Equal(42, reply.Seq);
        Assert.NotNull(reply.Payload);
        var echoed = reply.Payload!.Value.Deserialize<EchoPayload>(JsonOptions);
        Assert.Equal("ping", echoed!.Message);

        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "test done",
            CancellationToken.None
        );
    }

    [Fact]
    public async Task Disconnect_RemovesFromRoom()
    {
        var (auth, lobbyCode) = await SetupLobbyAsync();
        var hub = _factory.Services.GetRequiredService<CoopHub>();

        using (var socket = await ConnectAsync(lobbyCode, auth.Token))
        {
            await SendAsync(socket, new CoopMessage { Type = "hello", Seq = 1 });
            await ReceiveAsync(socket);

            // Connection registered
            Assert.Equal(1, hub.ConnectedCount(lobbyCode));

            await socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "bye",
                CancellationToken.None
            );
        }

        // Give the server loop a moment to clean up.
        for (int i = 0; i < 20 && hub.ConnectedCount(lobbyCode) > 0; i++)
            await Task.Delay(50);

        Assert.Equal(0, hub.ConnectedCount(lobbyCode));
    }
}
