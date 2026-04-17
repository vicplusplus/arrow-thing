using StackExchange.Redis;

namespace ArrowThing.Server;

/// <summary>
/// Availability helpers for the optional <see cref="IConnectionMultiplexer"/>.
/// The multiplexer is registered with <c>AbortOnConnectFail=false</c>, so it
/// survives a Redis outage at startup and reconnects in the background. Use
/// <see cref="IsAvailable"/> before touching Redis so a disconnected cluster
/// turns into a clean 503 rather than an unhandled exception.
/// </summary>
public static class RedisExtensions
{
    public static bool IsAvailable(this IConnectionMultiplexer? mux) =>
        mux != null && mux.IsConnected;
}
