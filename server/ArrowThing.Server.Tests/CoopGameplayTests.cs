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
/// Phase 6: co-op gameplay sync end-to-end tests. Covers clear attempts,
/// race resolution, rejected-dep broadcasts, rate limit, and lobby completion.
/// Follows the same TestFactory + WebSocket pattern as LobbyGenerationTests.
/// </summary>
public class CoopGameplayTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public CoopGameplayTests(TestFactory factory)
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
                displayName = "PlayTest",
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
        var create = await _client.PostAsJsonAsync("/api/lobbies", new { name = "Gameplay Test" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var lobby = (await create.Content.ReadFromJsonAsync<LobbyResponse>())!;

        // Wait for generation.
        var active = await PollUntilStatusAsync(
            lobby.Code,
            (short)LobbyStatus.Active,
            TimeSpan.FromSeconds(30)
        );
        Assert.NotNull(active);

        // Connect once to receive the initial snapshot and get the board layout.
        var board = await FetchSnapshotAsync(lobby.Code, auth.Token);
        return (auth, lobby, board);
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

    private async Task<WebSocket> ConnectAsync(string code, string token)
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        return await wsClient.ConnectAsync(
            new Uri($"ws://localhost/ws/coop/{code}?token={token}"),
            CancellationToken.None
        );
    }

    /// <summary>
    /// Connect, say hello, drain welcome + snapshot + binary, close, return
    /// decoded board. Use this to inspect the layout before picking arrows
    /// to tap.
    /// </summary>
    private async Task<Board> FetchSnapshotAsync(string code, string token)
    {
        using var socket = await ConnectAsync(code, token);
        await SendAsync(socket, new CoopMessage { Type = "hello", Seq = 1 });
        var welcome = await ReceiveTextAsync(socket);
        Assert.Equal("welcome", welcome.Type);
        var snapMeta = await ReceiveTextAsync(socket);
        Assert.Equal("snapshot", snapMeta.Type);
        var bin = await ReceiveBinaryAsync(socket);
        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "fetch done",
            CancellationToken.None
        );
        return BinarySnapshot.DecodeFull(bin!);
    }

    /// <summary>
    /// Connect + hello + drain snapshot + roster_full, returning the live
    /// socket ready for gameplay messages. Roster patches that arrive during
    /// the test (e.g. from the player's own online-flip) are filtered out
    /// by <see cref="ReceiveClassifiedAsync"/>.
    /// </summary>
    private async Task<WebSocket> ConnectAndHandshakeAsync(string code, string token)
    {
        var socket = await ConnectAsync(code, token);
        await SendAsync(socket, new CoopMessage { Type = "hello", Seq = 1 });
        await ReceiveTextAsync(socket); // welcome
        await ReceiveTextAsync(socket); // snapshot meta
        await ReceiveBinaryAsync(socket); // snapshot binary
        await ReceiveTextAsync(socket); // roster_full
        return socket;
    }

    /// <summary>
    /// Drain text messages until we see one whose Type is NOT in the ignore
    /// list. Use to skip throttled roster patches that arrive unpredictably
    /// alongside the message a test actually cares about.
    /// </summary>
    private static async Task<CoopMessage> ReceiveIgnoringAsync(
        WebSocket socket,
        params string[] ignoreTypes
    )
    {
        while (true)
        {
            var msg = await ReceiveTextAsync(socket);
            if (Array.IndexOf(ignoreTypes, msg.Type) < 0)
                return msg;
        }
    }

    private static Arrow FindClearableArrow(Board board)
    {
        foreach (var a in board.Arrows)
            if (board.IsClearable(a))
                return a;
        throw new InvalidOperationException("Board has no clearable arrows");
    }

    private static Arrow? FindNonClearableArrow(Board board)
    {
        foreach (var a in board.Arrows)
            if (!board.IsClearable(a))
                return a;
        return null;
    }

    private static async Task SendClearAttemptAsync(WebSocket socket, Cell head, long clientSeq)
    {
        var msg = new CoopMessage
        {
            Type = "clear_attempt",
            Payload = JsonSerializer.SerializeToElement(
                new ClearAttemptPayload(head.X, head.Y, DateTime.UtcNow.ToString("O"), clientSeq),
                JsonOptions
            ),
        };
        await SendAsync(socket, msg);
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
            if (result.MessageType == WebSocketMessageType.Close)
                return (ms.ToArray(), result.MessageType);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (ms.ToArray(), result.MessageType);
    }

    // ── Tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClearAttempt_ValidClearable_BroadcastsCleared()
    {
        var (auth, lobby, board) = await SetupActiveLobbyAsync("clear-ok");
        var arrow = FindClearableArrow(board);

        using var socket = await ConnectAndHandshakeAsync(lobby.Code, auth.Token);
        await SendClearAttemptAsync(socket, arrow.HeadCell, clientSeq: 1);

        var reply = await ReceiveTextAsync(socket);
        Assert.Equal("cleared", reply.Type);
        var payload = reply.Payload!.Value.Deserialize<ClearedPayload>(JsonOptions)!;
        Assert.Equal(arrow.HeadCell.X, (int)Math.Round(payload.TapX));
        Assert.Equal(arrow.HeadCell.Y, (int)Math.Round(payload.TapY));
    }

    [Fact]
    public async Task ClearAttempt_NonClearable_PrivateRejectToSenderOnly()
    {
        // Non-clearable taps should be filtered client-side, but if a
        // misbehaving or cheating client sends one anyway the server rejects
        // it privately — never broadcast, never persisted.
        var (aliceAuth, lobby, board) = await SetupActiveLobbyAsync("clear-dep");
        var blocked = FindNonClearableArrow(board);
        if (blocked == null)
            return; // board has no blocked arrows — skip

        // Bob joins as a second player so we can confirm he receives no
        // broadcast when Alice sends a non-clearable clear_attempt.
        var bobAuth = await RegisterAndVerifyAsync($"clear-dep-b-{Guid.NewGuid():N}@example.com");
        using var bobSocket = await ConnectAndHandshakeAsync(lobby.Code, bobAuth.Token);

        using var aliceSocket = await ConnectAndHandshakeAsync(lobby.Code, aliceAuth.Token);
        await SendClearAttemptAsync(aliceSocket, blocked.HeadCell, clientSeq: 1);

        var aliceReply = await ReceiveTextAsync(aliceSocket);
        Assert.Equal("rejected_dep", aliceReply.Type);

        // Bob should see no gameplay broadcast for this attempt. Drain any
        // roster patches (from alice's online flip) and assert that no
        // cleared/rejected-anything message arrives within the window.
        using var silenceCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        var gotGameplayBroadcast = false;
        try
        {
            while (!silenceCts.IsCancellationRequested)
            {
                var (data, type) = await ReceiveWithTokenAsync(bobSocket, silenceCts.Token);
                if (type != WebSocketMessageType.Text)
                    continue;
                var msg = JsonSerializer.Deserialize<CoopMessage>(data, JsonOptions)!;
                if (
                    msg.Type != "roster_patch"
                    && msg.Type != "roster_full"
                    && msg.Type != "heartbeat"
                )
                {
                    gotGameplayBroadcast = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: only roster/heartbeat traffic in the window.
        }
        catch (System.Net.WebSockets.WebSocketException)
        {
            // Socket cancelled — treat as silence.
        }
        Assert.False(
            gotGameplayBroadcast,
            "Non-clearable taps must not broadcast to other players"
        );
    }

    private static async Task<(byte[] Data, WebSocketMessageType Type)> ReceiveWithTokenAsync(
        WebSocket socket,
        CancellationToken ct
    )
    {
        var ms = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return (ms.ToArray(), result.MessageType);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (ms.ToArray(), result.MessageType);
    }

    [Fact]
    public async Task ClearAttempt_SecondPlayerRaceLoss_GetsPrivateRejectedRace()
    {
        var (aliceAuth, lobby, board) = await SetupActiveLobbyAsync("race-a");
        var arrow = FindClearableArrow(board);

        // Alice clears the arrow first.
        using var aliceSocket = await ConnectAndHandshakeAsync(lobby.Code, aliceAuth.Token);
        await SendClearAttemptAsync(aliceSocket, arrow.HeadCell, clientSeq: 1);
        var aliceReply = await ReceiveTextAsync(aliceSocket);
        Assert.Equal("cleared", aliceReply.Type);

        // Bob registers as a second player and tries the same arrow — race lost.
        var bobAuth = await RegisterAndVerifyAsync($"race-b-{Guid.NewGuid():N}@example.com");
        using var bobSocket = await ConnectAndHandshakeAsync(lobby.Code, bobAuth.Token);
        await SendClearAttemptAsync(bobSocket, arrow.HeadCell, clientSeq: 42);

        // Drain Bob's socket until we see the private rejected_race reply
        // (tolerate any leading roster/player_joined style broadcasts).
        RejectedRacePayload? race = null;
        for (var i = 0; i < 8; i++)
        {
            var bobMsg = await ReceiveTextAsync(bobSocket);
            if (bobMsg.Type == "rejected_race")
            {
                race = bobMsg.Payload!.Value.Deserialize<RejectedRacePayload>(JsonOptions);
                break;
            }
        }
        Assert.NotNull(race);
        Assert.Equal(42, race!.ClientSeq);
    }

    [Fact]
    public async Task LastArrow_Cleared_BroadcastsLobbyCompleted()
    {
        // Use the decoded board from setup and mirror every accepted clear
        // locally so we never re-open a second socket as the same user (which
        // would overwrite the main Connections entry in the hub).
        var (auth, lobby, board) = await SetupActiveLobbyAsync("complete");

        using var socket = await ConnectAndHandshakeAsync(lobby.Code, auth.Token);

        bool completed = false;
        int safety = 2000;
        while (safety-- > 0 && board.Arrows.Count > 0)
        {
            var arrow = FindClearableArrow(board);
            await SendClearAttemptAsync(socket, arrow.HeadCell, clientSeq: safety);

            // Drain until we see `cleared` for our tap or `lobby_completed`.
            // Handle rate limiting by backing off and retrying the same cell.
            bool retry = false;
            while (true)
            {
                var msg = await ReceiveTextAsync(socket);
                if (msg.Type == "cleared")
                {
                    board.RemoveArrow(arrow);
                    break;
                }
                if (msg.Type == "lobby_completed")
                {
                    completed = true;
                    break;
                }
                if (msg.Type == "rejected_rate")
                {
                    retry = true;
                    break;
                }
                // Ignore other events (roster, player_joined, etc.).
            }
            if (completed)
                break;
            if (retry)
            {
                await Task.Delay(200);
                // Don't advance — re-try the same arrow next iteration.
                safety++;
                continue;
            }
            // Paced just under the 10/sec refill to avoid bursts.
            await Task.Delay(110);
        }

        // After the last clear the server should push `lobby_completed`
        // (usually as a separate message after the final `cleared`; roster
        // patches may interleave, which we ignore).
        if (!completed && board.Arrows.Count == 0)
        {
            var msg = await ReceiveIgnoringAsync(socket, "roster_patch", "roster_full");
            completed = msg.Type == "lobby_completed";
        }

        Assert.True(completed, "Expected lobby_completed broadcast after clearing last arrow");

        var final = await PollUntilStatusAsync(
            lobby.Code,
            (short)LobbyStatus.Completed,
            TimeSpan.FromSeconds(5)
        );
        Assert.NotNull(final);
    }
}
