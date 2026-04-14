using System.Collections.Concurrent;

namespace ArrowThing.Server.Coop;

/// <summary>
/// In-process concurrency limiter for board generation jobs. Caps:
/// 3 concurrent globally, 1 concurrent per account.
///
/// Per-process scope: when scaled horizontally the global cap becomes
/// "3 per worker pod", which is acceptable for v1. A future Redis-backed
/// limiter can replace this without touching the worker logic.
/// </summary>
public class AccountConcurrencyLimiter
{
    public const int GlobalMax = 3;

    private readonly SemaphoreSlim _global = new(GlobalMax, GlobalMax);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _perAccount = new();

    public async Task WaitAsync(Guid userId, CancellationToken ct)
    {
        var sem = _perAccount.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            await _global.WaitAsync(ct);
        }
        catch
        {
            sem.Release();
            throw;
        }
    }

    public void Release(Guid userId)
    {
        _global.Release();
        if (_perAccount.TryGetValue(userId, out var sem))
            sem.Release();
    }
}
