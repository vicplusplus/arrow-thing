using System.Text.Json;
using ArrowThing.Server.Data;
using ArrowThing.Server.Models;
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
    private readonly ILogger<LobbyGenerationWorker> _logger;

    public LobbyGenerationWorker(
        IServiceScopeFactory scopeFactory,
        IConnectionMultiplexer redis,
        AccountConcurrencyLimiter limiter,
        GenerationProgressBus progressBus,
        ILogger<LobbyGenerationWorker> logger
    )
    {
        _scopeFactory = scopeFactory;
        _redis = redis;
        _limiter = limiter;
        _progressBus = progressBus;
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

            try
            {
                var board = new Board(lobby.Width, lobby.Height);
                var iter = BoardGeneration.FillBoardIncremental(
                    board,
                    lobby.MaxArrowLength,
                    (int)lobby.Seed
                );

                int yields = 0;
                int lastReportedPct = -1;
                int maxPossible = Math.Max(1, lobby.Width * lobby.Height / 2);
                while (iter.MoveNext())
                {
                    if (ct.IsCancellationRequested)
                        throw new OperationCanceledException();
                    yields++;
                    if (yields % 100 == 0)
                    {
                        int pct = Math.Min(95, board.Arrows.Count * 100 / maxPossible);
                        if (pct != lastReportedPct)
                        {
                            lastReportedPct = pct;
                            await _progressBus.PublishProgressAsync(lobby.Code, pct);
                        }
                    }
                }

                var bytes = BinarySnapshot.EncodeFull(
                    board,
                    (int)lobby.Seed,
                    lobby.MaxArrowLength,
                    gzip: true
                );

                await snapshots.SaveAsync(lobby.Id, bytes, LobbySnapshotFormat.BinaryV1);

                lobby.Status = LobbyStatus.Active;
                lobby.GeneratedAt = DateTime.UtcNow;
                lobby.LastActivityAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                await _progressBus.PublishCompleteAsync(lobby.Code);

                _logger.LogInformation(
                    "[Gen] Lobby {Id} generated: {Count} arrows, snapshot {Bytes} bytes",
                    lobby.Id,
                    board.Arrows.Count,
                    bytes.Length
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
