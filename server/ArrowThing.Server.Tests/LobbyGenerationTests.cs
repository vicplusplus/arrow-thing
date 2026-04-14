using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using ArrowThing.Server.Auth;
using ArrowThing.Server.Coop;
using ArrowThing.Server.Data;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArrowThing.Server.Tests;

public class LobbyGenerationTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public LobbyGenerationTests(TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    private async Task<(AuthResponse Auth, LobbyResponse Lobby)> CreateLobbyAsync(
        string emailPrefix
    )
    {
        var email = $"{emailPrefix}-{Guid.NewGuid():N}@example.com";
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                password = "password123",
                displayName = "GenTest",
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
        var create = await _client.PostAsJsonAsync("/api/lobbies", new { name = "Gen Test Lobby" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var lobby = (await create.Content.ReadFromJsonAsync<LobbyResponse>())!;
        return (auth, lobby);
    }

    private async Task<LobbyResponse?> PollUntilStatusAsync(
        string code,
        short targetStatus,
        TimeSpan timeout
    )
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var get = await _client.GetAsync($"/api/lobbies/{code}");
            if (get.IsSuccessStatusCode)
            {
                var body = await get.Content.ReadFromJsonAsync<LobbyResponse>();
                if (body != null && body.Status == targetStatus)
                    return body;
            }
            await Task.Delay(200);
        }
        return null;
    }

    [Fact]
    public async Task CreateLobby_GeneratesAndTransitionsToActive()
    {
        var (auth, lobby) = await CreateLobbyAsync("gen-active");

        // Lobby starts in Generating; worker picks it up and transitions to Active.
        var active = await PollUntilStatusAsync(
            lobby.Code,
            (short)LobbyStatus.Active,
            TimeSpan.FromSeconds(30)
        );
        Assert.NotNull(active);
        Assert.Equal((short)LobbyStatus.Active, active!.Status);
    }

    [Fact]
    public async Task ConnectAfterGeneration_ReceivesSnapshot()
    {
        var (auth, lobby) = await CreateLobbyAsync("gen-snapshot");
        var active = await PollUntilStatusAsync(
            lobby.Code,
            (short)LobbyStatus.Active,
            TimeSpan.FromSeconds(30)
        );
        Assert.NotNull(active);

        var wsClient = _factory.Server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/ws/coop/{lobby.Code}?token={auth.Token}"),
            CancellationToken.None
        );

        // Send hello.
        await SendAsync(socket, new CoopMessage { Type = "hello", Seq = 1 });

        // Expect: welcome (text) → snapshot meta (text) → snapshot binary frame.
        var welcome = await ReceiveTextAsync(socket);
        Assert.Equal("welcome", welcome.Type);

        var snapshotMeta = await ReceiveTextAsync(socket);
        Assert.Equal("snapshot", snapshotMeta.Type);

        var binary = await ReceiveBinaryAsync(socket);
        Assert.NotNull(binary);
        Assert.NotEmpty(binary!);

        // Decode and verify.
        var board = BinarySnapshot.DecodeFull(binary);
        Assert.Equal(20, board.Width);
        Assert.Equal(20, board.Height);
        Assert.True(board.Arrows.Count > 0);

        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "test done",
            CancellationToken.None
        );
    }

    [Fact]
    public async Task RetryGen_FailedLobby_TransitionsBackToGenerating()
    {
        var (auth, lobby) = await CreateLobbyAsync("gen-retry");
        // Wait for it to become Active, then forcibly mark it as failed via DB
        // (no real way to make generation fail organically).
        var active = await PollUntilStatusAsync(
            lobby.Code,
            (short)LobbyStatus.Active,
            TimeSpan.FromSeconds(30)
        );
        Assert.NotNull(active);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var entity = await db.Lobbies.FindAsync(lobby.Id);
            entity!.Status = LobbyStatus.GenerationFailed;
            await db.SaveChangesAsync();
        }

        var retry = await _client.PostAsync($"/api/lobbies/{lobby.Id}/retry-gen", content: null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);

        // Worker should pick it up again and transition back to Active.
        var reactive = await PollUntilStatusAsync(
            lobby.Code,
            (short)LobbyStatus.Active,
            TimeSpan.FromSeconds(30)
        );
        Assert.NotNull(reactive);
    }

    [Fact]
    public async Task RetryGen_NonOwner_Returns403()
    {
        var (alice, lobby) = await CreateLobbyAsync("gen-owner");
        // Force into failed state.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var entity = await db.Lobbies.FindAsync(lobby.Id);
            entity!.Status = LobbyStatus.GenerationFailed;
            await db.SaveChangesAsync();
        }

        // Switch to a different user.
        var (bob, _) = await CreateLobbyAsync("gen-other");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            bob.Token
        );

        var retry = await _client.PostAsync($"/api/lobbies/{lobby.Id}/retry-gen", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, retry.StatusCode);
    }

    [Fact]
    public async Task RetryGen_NotFailedState_Returns400()
    {
        var (auth, lobby) = await CreateLobbyAsync("gen-nofail");
        var active = await PollUntilStatusAsync(
            lobby.Code,
            (short)LobbyStatus.Active,
            TimeSpan.FromSeconds(30)
        );
        Assert.NotNull(active);

        var retry = await _client.PostAsync($"/api/lobbies/{lobby.Id}/retry-gen", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
    }

    // -- WebSocket helpers ----------------------------------------------

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

    private static async Task<CoopMessage> ReceiveTextAsync(WebSocket socket)
    {
        var (data, type) = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Text, type);
        return JsonSerializer.Deserialize<CoopMessage>(data, JsonOptions)!;
    }

    private static async Task<byte[]?> ReceiveBinaryAsync(WebSocket socket)
    {
        var (data, type) = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Binary, type);
        return data;
    }

    private static async Task<(byte[] Data, WebSocketMessageType Type)> ReceiveAsync(
        WebSocket socket
    )
    {
        var ms = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                CancellationToken.None
            );
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (ms.ToArray(), result.MessageType);
    }
}
