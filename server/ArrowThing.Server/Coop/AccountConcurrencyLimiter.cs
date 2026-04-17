using System.Collections.Concurrent;

namespace ArrowThing.Server.Coop;

/// <summary>
/// In-process concurrency limiter for board generation jobs. Caps:
/// 1 concurrent globally, 1 concurrent per account. Generation at the
/// 200–400 board range is CPU-heavy; overlapping jobs starve each other
/// and make wall-clock times unpredictable for all users.
///
/// Per-process scope: when scaled horizontally the global cap becomes
/// "1 per worker pod". A future Redis-backed limiter can replace this
/// without touching the worker logic.
/// </summary>
public class AccountConcurrencyLimiter
{
    public const int GlobalMax = 1;

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
