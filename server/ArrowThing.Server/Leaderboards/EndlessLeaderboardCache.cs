using System.Text.Json;
using StackExchange.Redis;

namespace ArrowThing.Server.Leaderboards;

/// <summary>
/// Sibling of <see cref="LeaderboardCache"/> for endless-mode leaderboard
/// responses. Separate keyspace (<c>endless-leaderboard:*</c>) so classic
/// + endless invalidations don't trample each other.
/// </summary>
public class EndlessLeaderboardCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly IDatabase? _redis;

    public EndlessLeaderboardCache(IConnectionMultiplexer? redis = null)
    {
        _redis = redis?.GetDatabase();
    }

    public EndlessLeaderboardResponse? Get(int width, int height)
    {
        if (_redis == null)
            return null;
        var value = _redis.StringGet(Key(width, height));
        if (value.IsNullOrEmpty)
            return null;
        return JsonSerializer.Deserialize<EndlessLeaderboardResponse>((string)value!);
    }

    public void Set(int width, int height, EndlessLeaderboardResponse response)
    {
        if (_redis == null)
            return;
        var json = JsonSerializer.Serialize(response);
        _redis.StringSet(Key(width, height), json, Ttl);
    }

    public void Invalidate(int width, int height)
    {
        if (_redis == null)
            return;
        _redis.KeyDelete(Key(width, height));
        _redis.KeyDelete("endless-leaderboard:all");
    }

    public void InvalidateAll()
    {
        if (_redis == null)
            return;
        var server = _redis.Multiplexer.GetServers()[0];
        foreach (var key in server.Keys(pattern: "endless-leaderboard:*"))
            _redis.KeyDelete(key);
    }

    private static string Key(int width, int height) =>
        width == 0 && height == 0
            ? "endless-leaderboard:all"
            : $"endless-leaderboard:{width}x{height}";
}
