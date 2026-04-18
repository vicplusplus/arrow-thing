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
/// Phase 7: roster broadcast + timer_update + extended cleared payload tests.
/// </summary>
public class CoopRosterTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public CoopRosterTests(TestFactory factory)
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
                displayName = "Roster",
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

    private async Task<(AuthResponse Auth, LobbyResponse Lobby, Board Board)> SetupActiveLobbyAsync(
        string prefix
    )
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@example.com";
        var auth = await RegisterAndVerifyAsync(email);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );
        var create = await _client.PostAsJsonAsync("/api/lobbies", new { name = "Roster Test" });
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

        // Fetch the board layout once (separate socket, closed immediately).
        using var snapSocket = await ConnectAsync(lobby.Code, auth.Token);
        await SendAsync(snapSocket, new CoopMessage { Type = "hello", Seq = 1 });
        await ReceiveTextAsync(snapSocket); // welcome
        await ReceiveTextAsync(snapSocket); // snapshot meta
        var bin = await ReceiveBinaryAsync(snapSocket);
        await snapSocket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "done",
            CancellationToken.None
        );
        var board = BinarySnapshot.DecodeFull(bin!);
        return (auth, lobby, board);
    }

    private async Task<WebSocket> ConnectAsync(string code, string token)
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        return await wsClient.ConnectAsync(
            new Uri($"ws://localhost/ws/coop/{code}?token={token}"),
            CancellationToken.None
        );
    }

    private async Task<WebSocket> ConnectAndHandshakeAsync(string code, string token)
    {
        var socket = await ConnectAsync(code, token);
        await SendAsync(socket, new CoopMessage { Type = "hello", Seq = 1 });
        await ReceiveTextAsync(socket); // welcome
        await ReceiveTextAsync(socket); // snapshot meta
        await ReceiveBinaryAsync(socket); // binary snapshot
        // Next text message is the roster_full (sent by hub right after Ready).
        return socket;
    }

    private static async Task<CoopMessage> ReceiveUntilAsync(
        WebSocket socket,
        string type,
        int attempts = 20
    )
    {
        for (var i = 0; i < attempts; i++)
        {
            var msg = await ReceiveTextAsync(socket);
            if (msg.Type == type)
                return msg;
        }
        throw new Xunit.Sdk.XunitException($"Never observed message type {type}");
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
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
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
        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        return ms.ToArray();
    }

    private static async Task SendClearAttemptAsync(WebSocket socket, Cell head, long clientSeq)
    {
        await SendAsync(
            socket,
            new CoopMessage
            {
                Type = "clear_attempt",
                Payload = JsonSerializer.SerializeToElement(
                    new ClearAttemptPayload(
                        head.X,
                        head.Y,
                        DateTime.UtcNow.ToString("O"),
                        clientSeq
                    ),
                    JsonOptions
                ),
            }
        );
    }

    private static Arrow FindClearableArrow(Board board)
    {
        foreach (var a in board.Arrows)
            if (board.IsClearable(a))
                return a;
        throw new InvalidOperationException("Board has no clearable arrows");
    }

    // ── Tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hello_SendsRosterFullWithCreatorEntry()
    {
        var (auth, lobby, _) = await SetupActiveLobbyAsync("roster-full");
        using var socket = await ConnectAndHandshakeAsync(lobby.Code, auth.Token);

        var roster = await ReceiveUntilAsync(socket, "roster_full");
        var payload = roster.Payload!.Value.Deserialize<RosterFullPayload>(JsonOptions)!;

        Assert.NotEmpty(payload.Players);
        // The lobby creator is auto-registered at POST /api/lobbies, so the
        // roster snapshot contains exactly one entry when alice is the only
        // participant. Her Online flag may be true or false depending on
        // whether the room tracker already registered her connection at the
        // moment the roster was built.
        var self = Assert.Single(payload.Players);
        Assert.NotEqual(Guid.Empty, self.UserId);
        Assert.False(string.IsNullOrEmpty(self.Color));
        Assert.Equal(0, self.ClearCount);
    }

    [Fact]
    public async Task AcceptedClear_CarriesNewCountAndColorOnClearedPayload()
    {
        var (auth, lobby, board) = await SetupActiveLobbyAsync("cleared-fields");
        var arrow = FindClearableArrow(board);

        using var socket = await ConnectAndHandshakeAsync(lobby.Code, auth.Token);
        await ReceiveUntilAsync(socket, "roster_full"); // drain
        await SendClearAttemptAsync(socket, arrow.HeadCell, clientSeq: 1);

        var cleared = await ReceiveUntilAsync(socket, "cleared");
        var payload = cleared.Payload!.Value.Deserialize<ClearedPayload>(JsonOptions)!;

        Assert.Equal(1, payload.NewClearCount);
        Assert.False(string.IsNullOrEmpty(payload.Color));
        Assert.False(string.IsNullOrEmpty(payload.DisplayName));
    }

    [Fact]
    public async Task TimerUpdate_PersistsAndTriggersRosterPatch()
    {
        var (auth, lobby, _) = await SetupActiveLobbyAsync("timer-update");
        using var socket = await ConnectAndHandshakeAsync(lobby.Code, auth.Token);
        await ReceiveUntilAsync(socket, "roster_full");

        await SendAsync(
            socket,
            new CoopMessage
            {
                Type = "timer_update",
                Payload = JsonSerializer.SerializeToElement(
                    new TimerUpdatePayload(12345),
                    JsonOptions
                ),
            }
        );

        var patch = await ReceiveUntilAsync(socket, "roster_patch");
        var body = patch.Payload!.Value.Deserialize<RosterPatchPayload>(JsonOptions)!;
        Assert.NotEmpty(body.Upsert);
        var entry = body.Upsert[0];
        Assert.Equal(12345, entry.AccumulatedMillis);
    }

    [Fact]
    public async Task TimerUpdate_IgnoresBackwardValues()
    {
        var (auth, lobby, _) = await SetupActiveLobbyAsync("timer-back");
        using var socket = await ConnectAndHandshakeAsync(lobby.Code, auth.Token);
        await ReceiveUntilAsync(socket, "roster_full");

        // Push to 10000, then try to drop to 5000. Subsequent roster patch
        // should still report 10000.
        await SendAsync(
            socket,
            new CoopMessage
            {
                Type = "timer_update",
                Payload = JsonSerializer.SerializeToElement(
                    new TimerUpdatePayload(10000),
                    JsonOptions
                ),
            }
        );
        var patch1 = await ReceiveUntilAsync(socket, "roster_patch");
        Assert.Equal(
            10000,
            patch1
                .Payload!.Value.Deserialize<RosterPatchPayload>(JsonOptions)!
                .Upsert[0]
                .AccumulatedMillis
        );

        await SendAsync(
            socket,
            new CoopMessage
            {
                Type = "timer_update",
                Payload = JsonSerializer.SerializeToElement(
                    new TimerUpdatePayload(5000),
                    JsonOptions
                ),
            }
        );

        // Give the server time to reject the backward value. Then send a
        // monotone tick and verify the persisted value stayed at 10000+.
        // Wait past ConnectionEntry.TimerUpdateMinInterval (2 s) so the next
        // send isn't dropped by the per-connection timer_update rate limit.
        await Task.Delay(TimeSpan.FromSeconds(2.1));
        await SendAsync(
            socket,
            new CoopMessage
            {
                Type = "timer_update",
                Payload = JsonSerializer.SerializeToElement(
                    new TimerUpdatePayload(15000),
                    JsonOptions
                ),
            }
        );
        // Drain patches until we see the monotone tick land. Earlier
        // patches may carry 10000 (the previous steady state); what we care
        // about is that 5000 never appears and 15000 eventually does.
        long lastSeen = 10000;
        for (var i = 0; i < 10; i++)
        {
            var patch = await ReceiveUntilAsync(socket, "roster_patch");
            var entry = patch.Payload!.Value.Deserialize<RosterPatchPayload>(JsonOptions)!.Upsert[
                0
            ];
            Assert.True(
                entry.AccumulatedMillis >= lastSeen,
                $"Timer moved backward: {lastSeen} -> {entry.AccumulatedMillis}"
            );
            lastSeen = entry.AccumulatedMillis;
            if (lastSeen >= 15000)
                return;
        }
        throw new Xunit.Sdk.XunitException($"Never observed timer >= 15000; last seen {lastSeen}");
    }

    [Fact]
    public async Task TimerUpdate_RateLimitedPerConnection()
    {
        // Phase 5: a malicious client can't amplify timer_update into a DB
        // write + roster broadcast every WS frame — the server drops any
        // update that arrives within TimerUpdateMinInterval (2s) of the last
        // accepted one. First value lands, burst values are dropped; only
        // after the window expires does a new value land.
        var (auth, lobby, _) = await SetupActiveLobbyAsync("timer-rate");
        using var socket = await ConnectAndHandshakeAsync(lobby.Code, auth.Token);
        await ReceiveUntilAsync(socket, "roster_full");

        await SendAsync(
            socket,
            new CoopMessage
            {
                Type = "timer_update",
                Payload = JsonSerializer.SerializeToElement(
                    new TimerUpdatePayload(1000),
                    JsonOptions
                ),
            }
        );
        var firstPatch = await ReceiveUntilAsync(socket, "roster_patch");
        Assert.Equal(
            1000,
            firstPatch
                .Payload!.Value.Deserialize<RosterPatchPayload>(JsonOptions)!
                .Upsert[0]
                .AccumulatedMillis
        );

        // Immediately burst 5 more monotone values. All should be dropped
        // by the rate limit — no roster_patch lands and no DB write fires.
        for (long v = 2000; v <= 6000; v += 1000)
        {
            await SendAsync(
                socket,
                new CoopMessage
                {
                    Type = "timer_update",
                    Payload = JsonSerializer.SerializeToElement(
                        new TimerUpdatePayload(v),
                        JsonOptions
                    ),
                }
            );
        }

        // Brief window to let any errant broadcast land, then assert none did.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        try
        {
            var buffer = new byte[4096];
            await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            throw new Xunit.Sdk.XunitException(
                "Received a burst roster_patch — rate limit didn't kick in"
            );
        }
        catch (OperationCanceledException)
        {
            // Expected: no broadcast within 400ms.
        }

        // Wait past the throttle window. The next update should land.
        await Task.Delay(TimeSpan.FromSeconds(2.1));
        await SendAsync(
            socket,
            new CoopMessage
            {
                Type = "timer_update",
                Payload = JsonSerializer.SerializeToElement(
                    new TimerUpdatePayload(7000),
                    JsonOptions
                ),
            }
        );
        var afterPatch = await ReceiveUntilAsync(socket, "roster_patch");
        Assert.Equal(
            7000,
            afterPatch
                .Payload!.Value.Deserialize<RosterPatchPayload>(JsonOptions)!
                .Upsert[0]
                .AccumulatedMillis
        );
    }

    [Fact]
    public async Task SecondPlayerJoin_BroadcastsRosterPatchToExistingClients()
    {
        var (aliceAuth, lobby, _) = await SetupActiveLobbyAsync("two-join");
        using var aliceSocket = await ConnectAndHandshakeAsync(lobby.Code, aliceAuth.Token);
        await ReceiveUntilAsync(aliceSocket, "roster_full");

        var bobAuth = await RegisterAndVerifyAsync($"two-join-b-{Guid.NewGuid():N}@example.com");
        using var bobSocket = await ConnectAndHandshakeAsync(lobby.Code, bobAuth.Token);

        // Alice should receive a roster_patch showing bob (either upsert only
        // or both players) within ~1 s.
        var patch = await ReceiveUntilAsync(aliceSocket, "roster_patch");
        var body = patch.Payload!.Value.Deserialize<RosterPatchPayload>(JsonOptions)!;
        Assert.NotEmpty(body.Upsert);
        Assert.Contains(body.Upsert, p => p.DisplayName == "Roster");
    }
}
