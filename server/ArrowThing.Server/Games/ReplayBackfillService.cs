using ArrowThing.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace ArrowThing.Server.Games;

/// <summary>
/// One-shot startup job that gzips legacy <see cref="Models.Score.ReplayJson"/>
/// text rows into <see cref="Models.Score.ReplayJsonGz"/>. Idempotent — re-runs
/// pick up only rows that haven't been migrated. Runs in batches so a large
/// historical table doesn't block startup.
///
/// After every existing row has been migrated, a follow-up PR can drop the
/// legacy text column entirely.
/// </summary>
public class ReplayBackfillService : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(100);

    // Wait a bit after startup so the API can serve traffic before we start
    // pulling rows out of the DB pool.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReplayBackfillService> _logger;

    public ReplayBackfillService(
        IServiceScopeFactory scopeFactory,
        ILogger<ReplayBackfillService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        long migrated = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            int batchCount;
            try
            {
                batchCount = await MigrateBatchAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Replay backfill batch failed; will retry");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            if (batchCount == 0)
            {
                if (migrated > 0)
                    _logger.LogInformation(
                        "Replay backfill complete; migrated {Count} legacy rows",
                        migrated
                    );
                return;
            }

            migrated += batchCount;
            try
            {
                await Task.Delay(BatchDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<int> MigrateBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var batch = await db
            .Scores.Where(s => s.ReplayJsonGz == null && s.ReplayJson != "")
            .OrderBy(s => s.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (batch.Count == 0)
            return 0;

        foreach (var score in batch)
        {
            ReplayStorage.Set(score, score.ReplayJson);
        }
        await db.SaveChangesAsync(ct);
        return batch.Count;
    }
}
