using ArrowThing.Server.Data;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace ArrowThing.Server.Coop;

/// <summary>
/// Background service that runs co-op lobby retention jobs once per day.
///
/// Two jobs run sequentially:
///   1. <see cref="RunSnapshotStripperAsync"/> — nulls the snapshot blob on
///      lobbies that have been <c>Completed</c>/<c>Deleted</c> for longer
///      than the retention window, keeping the <c>Lobbies</c> + event
///      history rows intact for leaderboard/audit purposes.
///   2. <see cref="RunIdleReaperAsync"/> — soft-deletes <c>Active</c> or
///      <c>GenerationFailed</c> lobbies whose <c>LastActivityAt</c> is
///      older than the retention window.
///
/// First tick fires 15 minutes after startup so a restart doesn't skip a
/// day. Subsequent ticks are 24 h apart.
/// </summary>
public class LobbyRetentionService : BackgroundService
{
    private const int SnapshotStripAfterDays = 30;
    private const int IdleReapAfterDays = 30;
    private const int StripBatchSize = 50;

    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LobbyRetentionService> _logger;

    public LobbyRetentionService(
        IServiceScopeFactory scopeFactory,
        ILogger<LobbyRetentionService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TickInterval);
        // Fire immediately after the initial delay, then once per tick.
        await RunOnceAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await RunOnceAsync(stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await RunSnapshotStripperAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Retention] SnapshotStripper failed");
        }
        try
        {
            await RunIdleReaperAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Retention] IdleReaper failed");
        }
    }

    public async Task<int> RunSnapshotStripperAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(SnapshotStripAfterDays);
        int totalStripped = 0;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        while (!ct.IsCancellationRequested)
        {
            // Candidate lobbies: Completed or Deleted long enough ago, still
            // carrying a snapshot.
            var batch = await db
                .Lobbies.Where(l =>
                    (l.Status == LobbyStatus.Completed || l.Status == LobbyStatus.Deleted)
                    && (
                        (l.Status == LobbyStatus.Completed && l.CompletedAt < cutoff)
                        || (l.Status == LobbyStatus.Deleted && l.DeletedAt < cutoff)
                    )
                    && l.SnapshotStrippedAt == null
                )
                .OrderBy(l => l.CompletedAt ?? l.DeletedAt)
                .Take(StripBatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
                break;

            var ids = batch.Select(l => l.Id).ToList();
            var snapshots = await db
                .LobbySnapshots.Where(s => ids.Contains(s.LobbyId))
                .ToListAsync(ct);
            var now = DateTime.UtcNow;
            foreach (var s in snapshots)
            {
                s.Data = Array.Empty<byte>();
                s.UpdatedAt = now;
            }
            foreach (var l in batch)
            {
                l.SnapshotStrippedAt = now;
            }
            await db.SaveChangesAsync(ct);
            totalStripped += batch.Count;
        }

        if (totalStripped > 0)
            _logger.LogInformation(
                "[Retention] Stripped snapshots from {Count} lobbies",
                totalStripped
            );
        return totalStripped;
    }

    public async Task<int> RunIdleReaperAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(IdleReapAfterDays);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stale = await db
            .Lobbies.Where(l =>
                (l.Status == LobbyStatus.Active || l.Status == LobbyStatus.GenerationFailed)
                && l.LastActivityAt < cutoff
            )
            .ToListAsync(ct);

        if (stale.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        foreach (var lobby in stale)
        {
            lobby.Status = LobbyStatus.Deleted;
            lobby.DeletedAt = now;
            lobby.LastActivityAt = now;
        }
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[Retention] Reaped {Count} idle lobbies (> {Days}d)",
            stale.Count,
            IdleReapAfterDays
        );
        return stale.Count;
    }
}
