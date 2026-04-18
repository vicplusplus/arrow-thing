using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using ArrowThing.Server.Auth;
using ArrowThing.Server.Coop;
using Microsoft.Extensions.DependencyInjection;

namespace ArrowThing.Server.Tests;

/// <summary>
/// Phase 1F: OpenTelemetry counters for the co-op subsystem. Uses a
/// <see cref="MeterListener"/> to observe the custom <c>ArrowThing.Coop</c>
/// meter without standing up a full OTel exporter. Each test records the
/// deltas emitted during its flow and asserts the expected counters fired.
/// </summary>
public class CoopMetricsTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public CoopMetricsTests(TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Device-Id", Guid.NewGuid().ToString("N"));
    }

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Test harness for observing the co-op Meter. Captures (instrumentName,
    /// reasonTag-or-null, delta) tuples. Callers filter or sum as needed.
    /// </summary>
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener;
        public List<(string Name, string? Reason, long Delta)> LongSamples { get; } = new();
        public List<(string Name, double Value)> DoubleSamples { get; } = new();

        public Recorder()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (inst, l) =>
                {
                    if (inst.Meter.Name == CoopMetrics.MeterName)
                        l.EnableMeasurementEvents(inst);
                },
            };
            _listener.SetMeasurementEventCallback<long>(
                (inst, value, tags, _) =>
                {
                    string? reason = null;
                    foreach (var kv in tags)
                        if (kv.Key == "reason")
                            reason = kv.Value?.ToString();
                    lock (LongSamples)
                        LongSamples.Add((inst.Name, reason, value));
                }
            );
            _listener.SetMeasurementEventCallback<double>(
                (inst, value, _, _) =>
                {
                    lock (DoubleSamples)
                        DoubleSamples.Add((inst.Name, value));
                }
            );
            _listener.Start();
        }

        public long Sum(string name, string? reason = null)
        {
            lock (LongSamples)
                return LongSamples
                    .Where(s => s.Name == name && (reason == null || s.Reason == reason))
                    .Sum(s => s.Delta);
        }

        public void Dispose() => _listener.Dispose();
    }

    private async Task<(AuthResponse Auth, string LobbyCode)> SetupLobbyAsync()
    {
        var email = $"metrics-{Guid.NewGuid():N}@example.com";
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                password = "password123",
                displayName = "MetricsTest",
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
        var create = await _client.PostAsJsonAsync(
            "/api/lobbies",
            new { name = "Metrics Test Lobby" }
        );
        var lobby = (await create.Content.ReadFromJsonAsync<LobbyResponse>())!;
        return (auth, lobby.Code);
    }

    [Fact]
    public async Task CreateLobby_IncrementsLobbiesCreated()
    {
        using var recorder = new Recorder();
        await SetupLobbyAsync();
        Assert.True(recorder.Sum("coop.lobbies.created") >= 1);
    }

    [Fact]
    public async Task WsConnect_IncrementsConnectCounter()
    {
        var (auth, lobbyCode) = await SetupLobbyAsync();
        using var recorder = new Recorder();

        var wsClient = _factory.Server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/ws/coop/{lobbyCode}?token={auth.Token}"),
            CancellationToken.None
        );
        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "test done",
            CancellationToken.None
        );

        Assert.True(recorder.Sum("coop.ws.connects") >= 1);
    }

    [Fact]
    public async Task RejectedDep_IncrementsRejectedCounterWithReasonTag()
    {
        var (auth, lobbyCode) = await SetupLobbyAsync();

        // Wait for lobby to reach Active so clear_attempt reaches the game-
        // state path instead of being silently dropped.
        await WaitForLobbyActiveAsync(lobbyCode);

        var wsClient = _factory.Server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/ws/coop/{lobbyCode}?token={auth.Token}"),
            CancellationToken.None
        );

        // Hello → welcome.
        await SendAsync(socket, new CoopMessage { Type = "hello", Seq = 1 });
        // Drain welcome + any server-initiated post-welcome frames (roster_full
        // etc.). Stop draining once nothing has arrived for 200ms.
        await DrainAsync(socket, TimeSpan.FromMilliseconds(200));

        using var recorder = new Recorder();

        // clear_attempt wildly out of bounds (board is at most 400×400, so
        // -1000,-1000 is guaranteed out_of_bounds → rejected_race(oob)).
        await SendAsync(
            socket,
            new CoopMessage
            {
                Type = "clear_attempt",
                Seq = 2,
                Payload = JsonSerializer.SerializeToElement(
                    new
                    {
                        tapX = -1000f,
                        tapY = -1000f,
                        clientTsUtc = DateTime.UtcNow.ToString("O"),
                        clientSeq = 1L,
                    },
                    JsonOptions
                ),
            }
        );

        // Wait for the reject to land before asserting the counter.
        await DrainAsync(socket, TimeSpan.FromMilliseconds(500));

        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "test done",
            CancellationToken.None
        );

        Assert.True(
            recorder.Sum("coop.clears.rejected", reason: "oob") >= 1,
            $"Expected at least one rejected(oob); got samples: "
                + string.Join(
                    ",",
                    recorder.LongSamples.Select(s => $"{s.Name}:{s.Reason}:{s.Delta}")
                )
        );
    }

    private async Task WaitForLobbyActiveAsync(string code)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!cts.IsCancellationRequested)
        {
            var resp = await _client.GetAsync($"/api/lobbies/{code}");
            var body = await resp.Content.ReadFromJsonAsync<LobbyResponse>();
            // 1 == LobbyStatus.Active (short-serialized over the wire).
            if (body != null && body.Status == 1)
                return;
            await Task.Delay(100, cts.Token);
        }
        throw new TimeoutException("Lobby did not reach Active within 10s");
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

    private static async Task DrainAsync(WebSocket socket, TimeSpan idleGap)
    {
        var buffer = new byte[4096];
        while (true)
        {
            using var cts = new CancellationTokenSource(idleGap);
            try
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
