using System.Text.Json;
using ArrowThing.Server.Data;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;
using Serilog.Context;
using StackExchange.Redis;

namespace ArrowThing.Server.Coop;

/// <summary>
/// Background service that consumes the <c>coop:gen:queue</c> Redis list and
/// generates boards for new lobbies. Mirrors the <c>VerificationWorker</c>
/// pattern: long-poll loop, scoped DbContext per job, generic try/catch
/// transitions failed lobbies to <see cref="LobbyStatus.GenerationFailed"/>.
/// </summary>
public class LobbyGenerationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectionMultiplexer _redis;
    private readonly AccountConcurrencyLimiter _limiter;
    private readonly GenerationProgressBus _progressBus;
    private readonly CoopMetrics _metrics;
    private readonly ILogger<LobbyGenerationWorker> _logger;

    public LobbyGenerationWorker(
        IServiceScopeFactory scopeFactory,
        IConnectionMultiplexer redis,
        AccountConcurrencyLimiter limiter,
        GenerationProgressBus progressBus,
        CoopMetrics metrics,
        ILogger<LobbyGenerationWorker> logger
    )
    {
        _scopeFactory = scopeFactory;
        _redis = redis;
        _limiter = limiter;
        _progressBus = progressBus;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Lobby generation worker started");
        var redis = _redis.GetDatabase();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var raw = await redis.ListRightPopAsync(LobbyService.GenerationQueueKey);
                if (raw.IsNullOrEmpty)
                {
                    await Task.Delay(500, stoppingToken);
                    continue;
                }

                // Process jobs concurrently up to the global cap so a single
                // long job doesn't block the queue. Fire and forget; the
                // limiter inside ProcessJob enforces concurrency.
                _ = ProcessJobAsync((string)raw!, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling generation queue");
                await Task.Delay(1000, stoppingToken);
            }
        }

        _logger.LogInformation("Lobby generation worker stopped");
    }

    private async Task ProcessJobAsync(string jobJson, CancellationToken ct)
    {
        GenerationJob? job;
        try
        {
            job = JsonSerializer.Deserialize<GenerationJob>(jobJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize generation job");
            return;
        }
        if (job == null)
            return;

        await _limiter.WaitAsync(job.OwnerUserId, ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var snapshots = scope.ServiceProvider.GetRequiredService<LobbySnapshotRepository>();

            var lobby = await db.Lobbies.FindAsync(job.LobbyId);
            if (lobby == null)
            {
                _logger.LogWarning("[Gen] Lobby {Id} not found, skipping job", job.LobbyId);
                return;
            }
            // Stamp the lobby code for the rest of this job so Grafana can
            // line up [Gen] worker logs with [CoopHub] per-connection logs.
            using var _lobbyScope = LogContext.PushProperty("LobbyCode", lobby.Code);

            if (lobby.Status != LobbyStatus.Generating)
            {
                _logger.LogInformation(
                    "[Gen] Lobby {Id} is in state {State}, skipping job",
                    job.LobbyId,
                    lobby.Status
                );
                return;
            }

            _logger.LogInformation(
                "[Gen] Generating board for lobby {Id} ({W}x{H}, seed={Seed})",
                lobby.Id,
                lobby.Width,
                lobby.Height,
                lobby.Seed
            );

            var genStart = DateTime.UtcNow;
            try
            {
                var board = new Board(lobby.Width, lobby.Height);
                var iter = BoardGeneration.FillBoardIncremental(
                    board,
                    lobby.MaxArrowLength,
                    (int)lobby.Seed
                );

                // Phase-weighted progress (shared with client GameController via
                // GenerationProgress in the domain layer). Server omits the
                // client-only view-rebuild phase.
                int yields = 0;
                int lastReportedPct = -1;
                var phase = GenerationPhase.Generating;
                int arrowsBeforeCompaction = 0;
                var compactStart = DateTime.UtcNow;
                var weights = GenerationProgress.ServerWeights;

                while (iter.MoveNext())
                {
                    if (ct.IsCancellationRequested)
                        throw new OperationCanceledException();

                    // Phase transitions come through iter.Current as GenerationPhase.
                    if (iter.Current is GenerationPhase nextPhase)
                    {
                        if (nextPhase == GenerationPhase.Compacting)
                        {
                            arrowsBeforeCompaction = board.Arrows.Count;
                            compactStart = DateTime.UtcNow;
                        }
                        phase = nextPhase;
                    }

                    yields++;
                    if (yields % 100 != 0)
                        continue;

                    float raw = phase switch
                    {
                        GenerationPhase.Generating => GenerationProgress.ForGenerating(
                            board.InitialCandidateCount > 0
                                ? 1f
                                    - (float)board.RemainingCandidateCount
                                        / board.InitialCandidateCount
                                : 0f,
                            weights
                        ),
                        GenerationPhase.Compacting => GenerationProgress.ForCompacting(
                            mergesCompleted: arrowsBeforeCompaction - board.Arrows.Count,
                            arrowsBeforeCompaction: arrowsBeforeCompaction,
                            timeRatio: GenerationProgress.ExpectedCompactSeconds(
                                arrowsBeforeCompaction
                            ) > 0.001f
                                ? (float)(
                                    (DateTime.UtcNow - compactStart).TotalSeconds
                                    / GenerationProgress.ExpectedCompactSeconds(
                                        arrowsBeforeCompaction
                                    )
                                )
                                : 1f,
                            w: weights
                        ),
                        GenerationPhase.Finalizing => GenerationProgress.ForFinalizing(
                            iter.Current is int finalized && board.Arrows.Count > 0
                                ? (float)finalized / board.Arrows.Count
                                : 0f,
                            weights
                        ),
                        _ => 0f,
                    };

                    // Cap at 95 until gen_complete; keep monotone across phase
                    // boundaries in case two samples disagree.
                    int pct = Math.Min(95, (int)(raw * 100f));
                    pct = Math.Max(lastReportedPct, pct);
                    if (pct != lastReportedPct)
                    {
                        lastReportedPct = pct;
                        await _progressBus.PublishProgressAsync(lobby.Code, pct);
                    }
                }

                var bytes = BinarySnapshot.EncodeFull(
                    board,
                    (int)lobby.Seed,
                    lobby.MaxArrowLength,
                    gzip: true
                );

                await snapshots.SaveAsync(lobby.Id, bytes, LobbySnapshotFormat.BinaryV1);

                // Atomic transition: only promote to Active if the row is still
                // Generating. Using ExecuteUpdate lets Postgres enforce the Status
                // check in the same UPDATE; EF Core's tracked-entity path issues a
                // full UPDATE keyed only on Id, which would overwrite Status=Deleted
                // and undelete the lobby under a concurrent soft-delete.
                var now = DateTime.UtcNow;
                var rowsAffected = await db
                    .Lobbies.Where(l => l.Id == lobby.Id && l.Status == LobbyStatus.Generating)
                    .ExecuteUpdateAsync(
                        s =>
                            s.SetProperty(l => l.Status, LobbyStatus.Active)
                                .SetProperty(l => l.GeneratedAt, now)
                                .SetProperty(l => l.LastActivityAt, now),
                        ct
                    );

                if (rowsAffected == 0)
                {
                    _logger.LogInformation(
                        "[Gen] Lobby {Id} transitioned out of Generating during generation; discarding result",
                        lobby.Id
                    );
                    return;
                }

                await _progressBus.PublishCompleteAsync(lobby.Code);

                var elapsed = (DateTime.UtcNow - genStart).TotalSeconds;
                _metrics.GenerationDurationSeconds.Record(elapsed);
                _logger.LogInformation(
                    "[Gen] Lobby {Id} generated: {Count} arrows, snapshot {Bytes} bytes in {Elapsed:F2}s",
                    lobby.Id,
                    board.Arrows.Count,
                    bytes.Length,
                    elapsed
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Gen] Generation failed for lobby {Id}", lobby.Id);
                lobby.Status = LobbyStatus.GenerationFailed;
                lobby.LastActivityAt = DateTime.UtcNow;
                try
                {
                    await db.SaveChangesAsync();
                }
                catch (Exception saveEx)
                {
                    _logger.LogError(saveEx, "[Gen] Failed to save GenerationFailed status");
                }
                try
                {
                    await _progressBus.PublishFailedAsync(lobby.Code, ex.Message);
                }
                catch { }
            }
        }
        finally
        {
            _limiter.Release(job.OwnerUserId);
        }
    }
}
