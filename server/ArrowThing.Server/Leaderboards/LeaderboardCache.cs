using System.Text.Json;
using StackExchange.Redis;

namespace ArrowThing.Server.Leaderboards;

/// <summary>
/// Redis-backed cache for leaderboard responses.
/// Invalidated per board size when a top-50 score changes.
/// Falls back to no-op (no caching) when Redis is unavailable.
/// </summary>
public class LeaderboardCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly IDatabase? _redis;

    public LeaderboardCache(IConnectionMultiplexer? redis = null)
    {
        _redis = redis?.GetDatabase();
    }

    public LeaderboardResponse? Get(int width, int height)
    {
        if (_redis == null)
            return null;

        var value = _redis.StringGet(Key(width, height));
        if (value.IsNullOrEmpty)
            return null;

        return JsonSerializer.Deserialize<LeaderboardResponse>((string)value!);
    }

    public void Set(int width, int height, LeaderboardResponse response)
    {
        if (_redis == null)
            return;

        var json = JsonSerializer.Serialize(response);
        _redis.StringSet(Key(width, height), json, Ttl);
    }

    /// <summary>
    /// Invalidates the cache for a specific board size.
    /// Also invalidates the "all" leaderboard since it aggregates across sizes.
    /// </summary>
    public void Invalidate(int width, int height)
    {
        if (_redis == null)
            return;

        _redis.KeyDelete(Key(width, height));
        _redis.KeyDelete("leaderboard:all");
    }

    /// <summary>Invalidates all cached leaderboards.</summary>
    public void InvalidateAll()
    {
        if (_redis == null)
            return;

        var server = _redis.Multiplexer.GetServers()[0];
        foreach (var key in server.Keys(pattern: "leaderboard:*"))
            _redis.KeyDelete(key);
    }

    private static string Key(int width, int height) =>
        width == 0 && height == 0 ? "leaderboard:all" : $"leaderboard:{width}x{height}";
}
