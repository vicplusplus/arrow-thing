using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using ArrowThing.Server.Auth;
using ArrowThing.Server.Coop;
using ArrowThing.Server.Models;

namespace ArrowThing.Server.Tests;

/// <summary>
/// Phase 8: co-op replay REST + v6 format tests.
/// </summary>
public class CoopReplayTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public CoopReplayTests(TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Device-Id", Guid.NewGuid().ToString("N"));
    }

    public void Dispose() => _client.Dispose();

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task<AuthResponse> RegisterAndVerifyAsync(string email)
    {
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                password = "password123",
                displayName = "ReplayTest",
            }
        );
        var code = _factory.FakeEmail.SentEmails.FindLast(e =>
            e.To == email && e.Type == "verification"
        );
        var verifyResponse = await _client.PostAsJsonAsync(
            "/api/auth/verify-code",
            new { email, code = code!.Token }
        );
        return (await verifyResponse.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private async Task<(AuthResponse Auth, LobbyResponse Lobby)> CreateLobbyAsync(string prefix)
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@example.com";
        var auth = await RegisterAndVerifyAsync(email);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );
        var create = await _client.PostAsJsonAsync("/api/lobbies", new { name = "Replay" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var lobby = (await create.Content.ReadFromJsonAsync<LobbyResponse>())!;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var get = await _client.GetAsync($"/api/lobbies/{lobby.Code}");
            if (get.IsSuccessStatusCode)
            {
                var body = await get.Content.ReadFromJsonAsync<LobbyResponse>();
                if (body?.Status == (short)LobbyStatus.Active)
                    break;
            }
            await Task.Delay(200);
        }
        return (auth, lobby);
    }

    // ── Tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetReplay_ReturnsV6WithRosterAndEvents()
    {
        var (auth, lobby) = await CreateLobbyAsync("replay");

        var resp = await _client.GetAsync($"/api/lobbies/{lobby.Code}/replay");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();

        // The ReplayData is serialized with default System.Text.Json camelCase —
        // parse as a document so we can assert on the shape without pulling
        // in ReplayData here.
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(6, root.GetProperty("version").GetInt32());
        Assert.True(root.TryGetProperty("roster", out var roster));
        Assert.Equal(1, roster.GetArrayLength()); // creator is auto-registered
        var self = roster[0];
        Assert.False(string.IsNullOrEmpty(self.GetProperty("displayName").GetString()));
        Assert.False(string.IsNullOrEmpty(self.GetProperty("color").GetString()));

        // Events: at least session_start + start_solve. finalTime == -1 since
        // the lobby hasn't been cleared.
        var events = root.GetProperty("events");
        Assert.True(events.GetArrayLength() >= 2);
        Assert.Equal(-1, root.GetProperty("finalTime").GetDouble());
    }

    [Fact]
    public async Task GetReplay_DeniesNonParticipants()
    {
        var (_, lobby) = await CreateLobbyAsync("replay-auth-a");

        var otherAuth = await RegisterAndVerifyAsync(
            $"replay-auth-b-{Guid.NewGuid():N}@example.com"
        );
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            otherAuth.Token
        );

        var resp = await _client.GetAsync($"/api/lobbies/{lobby.Code}/replay");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task GetReplay_ReturnsCompletedLobbyWithFinalTime()
    {
        // Create + clear every arrow via WS, then fetch the replay and assert
        // finalTime is set and clear events carry playerIds.
        var (auth, lobby) = await CreateLobbyAsync("replay-done");

        // Decode snapshot to pick arrows.
        using (
            var snap = await _factory
                .Server.CreateWebSocketClient()
                .ConnectAsync(
                    new Uri($"ws://localhost/ws/coop/{lobby.Code}?token={auth.Token}"),
                    CancellationToken.None
                )
        )
        {
            await SendAsync(snap, new CoopMessage { Type = "hello", Seq = 1 });
            await ReceiveTextAsync(snap); // welcome
            await ReceiveTextAsync(snap); // snapshot meta
            var bin = await ReceiveBinaryAsync(snap);
            await snap.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "fetch",
                CancellationToken.None
            );
            var board = BinarySnapshot.DecodeFull(bin);

            // Reconnect for play.
            using var play = await _factory
                .Server.CreateWebSocketClient()
                .ConnectAsync(
                    new Uri($"ws://localhost/ws/coop/{lobby.Code}?token={auth.Token}"),
                    CancellationToken.None
                );
            await SendAsync(play, new CoopMessage { Type = "hello", Seq = 1 });
            await ReceiveTextAsync(play); // welcome
            await ReceiveTextAsync(play); // snapshot meta
            await ReceiveBinaryAsync(play); // snapshot bin

            // Clear until completion (follows the pattern in CoopGameplayTests).
            int safety = 2000;
            bool completed = false;
            while (safety-- > 0 && board.Arrows.Count > 0)
            {
                Arrow? clearable = null;
                foreach (var a in board.Arrows)
                    if (board.IsClearable(a))
                    {
                        clearable = a;
                        break;
                    }
                if (clearable == null)
                    break;
                await SendAsync(
                    play,
                    new CoopMessage
                    {
                        Type = "clear_attempt",
                        Payload = JsonSerializer.SerializeToElement(
                            new ClearAttemptPayload(
                                clearable.HeadCell.X,
                                clearable.HeadCell.Y,
                                DateTime.UtcNow.ToString("O"),
                                safety
                            ),
                            JsonOptions
                        ),
                    }
                );
                while (true)
                {
                    var msg = await ReceiveTextAsync(play);
                    if (msg.Type == "cleared")
                    {
                        board.RemoveArrow(clearable);
                        break;
                    }
                    if (msg.Type == "lobby_completed")
                    {
                        completed = true;
                        break;
                    }
                    if (msg.Type == "rejected_rate")
                    {
                        await Task.Delay(200);
                        safety++;
                        break;
                    }
                }
                if (completed)
                    break;
                await Task.Delay(110);
            }
        }

        // Poll for lobby status.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var get = await _client.GetAsync($"/api/lobbies/{lobby.Code}");
            if (get.IsSuccessStatusCode)
            {
                var body = await get.Content.ReadFromJsonAsync<LobbyResponse>();
                if (body?.Status == (short)LobbyStatus.Completed)
                    break;
            }
            await Task.Delay(200);
        }

        var resp = await _client.GetAsync($"/api/lobbies/{lobby.Code}/replay");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("finalTime").GetDouble() > 0);
        var events = root.GetProperty("events");
        int clearCount = 0;
        for (int i = 0; i < events.GetArrayLength(); i++)
        {
            var evt = events[i];
            if (evt.GetProperty("type").GetString() == "clear")
            {
                clearCount++;
                // Every co-op clear carries a playerId.
                Assert.True(
                    evt.TryGetProperty("playerId", out var pid),
                    "co-op clear events must carry playerId"
                );
                Assert.False(string.IsNullOrEmpty(pid.GetString()));
            }
        }
        Assert.True(clearCount > 0);
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

    private static async Task<CoopMessage> ReceiveTextAsync(WebSocket socket)
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
        return JsonSerializer.Deserialize<CoopMessage>(ms.ToArray(), JsonOptions)!;
    }

    private static async Task<byte[]> ReceiveBinaryAsync(WebSocket socket)
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
        return ms.ToArray();
    }
}
