using System.Text.Json;
using StackExchange.Redis;

namespace ArrowThing.Server.Coop;

/// <summary>
/// Redis pub/sub bridge between the generation worker and any subscribed
/// <see cref="CoopHub"/> instance. Worker publishes progress / completion /
/// failure events; the API host's <see cref="CoopHub"/> subscribes to
/// <c>coop:lobby:*</c> and broadcasts to connected sockets in the matching
/// room. Allows the worker and the API to run as separate processes.
/// </summary>
public class GenerationProgressBus
{
    public const string ChannelPrefix = "coop:lobby:";

    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<GenerationProgressBus> _logger;

    public GenerationProgressBus(
        ILogger<GenerationProgressBus> logger,
        IConnectionMultiplexer? redis = null
    )
    {
        _redis = redis;
        _logger = logger;
    }

    public bool IsEnabled => _redis != null;

    public async Task PublishProgressAsync(string lobbyCode, int pct)
    {
        await PublishAsync(lobbyCode, new BusMessage { Type = "gen_progress", Pct = pct });
    }

    public async Task PublishCompleteAsync(string lobbyCode)
    {
        await PublishAsync(lobbyCode, new BusMessage { Type = "gen_complete" });
    }

    public async Task PublishFailedAsync(string lobbyCode, string reason)
    {
        await PublishAsync(lobbyCode, new BusMessage { Type = "lobby_failed", Reason = reason });
    }

    /// <summary>
    /// Subscribes to all lobby progress events. The handler receives
    /// (lobbyCode, busMessage). Returns when the subscription is established.
    /// </summary>
    public async Task SubscribeAsync(Func<string, BusMessage, Task> handler)
    {
        if (_redis == null)
            return;

        var subscriber = _redis.GetSubscriber();
        var channel = RedisChannel.Pattern(ChannelPrefix + "*");
        await subscriber.SubscribeAsync(
            channel,
            (ch, value) =>
            {
                try
                {
                    var code = ch.ToString().Substring(ChannelPrefix.Length);
                    var msg = JsonSerializer.Deserialize<BusMessage>((string)value!);
                    if (msg != null)
                        _ = handler(code, msg);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[GenerationProgressBus] Failed to dispatch message");
                }
            }
        );
    }

    private async Task PublishAsync(string lobbyCode, BusMessage msg)
    {
        if (_redis == null)
            return;

        var json = JsonSerializer.Serialize(msg);
        var channel = RedisChannel.Literal(ChannelPrefix + lobbyCode);
        await _redis.GetSubscriber().PublishAsync(channel, json);
    }

    public class BusMessage
    {
        public string Type { get; set; } = "";
        public int? Pct { get; set; }
        public string? Reason { get; set; }
    }
}
