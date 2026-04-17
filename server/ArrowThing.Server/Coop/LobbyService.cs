using System.Security.Cryptography;
using System.Text.Json;
using ArrowThing.Server;
using ArrowThing.Server.Auth;
using ArrowThing.Server.Data;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ArrowThing.Server.Coop;

public class LobbyService
{
    public const string GenerationQueueKey = "coop:gen:queue";

    private const int MaxActiveLobbiesPerOwner = 5;
    private const int CodeLength = 6;

    // Whitelist of safe code characters: letters only (no digits to avoid
    // culturally loaded numbers), excluding I/O (ambiguous with 1/0) and
    // E/F/G/K/M/N (commonly appear in slurs).
    // 18 chars × 6 positions ≈ 34M combinations.
    private const string CodeAlphabet = "ABCDHJLPQRSTUVWXYZ";
    private const int MaxCodeCollisionRetries = 10;
    private const string ShareUrlBase = "https://arrow-thing.com/?lobby=";

    private readonly AppDbContext _db;
    private readonly LobbyOptions _options;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<LobbyService> _logger;

    public LobbyService(
        AppDbContext db,
        IOptions<LobbyOptions> options,
        ILogger<LobbyService> logger,
        IConnectionMultiplexer? redis = null
    )
    {
        _db = db;
        _options = options.Value;
        _redis = redis;
        _logger = logger;
    }

    public async Task<(LobbyResponse? Response, int StatusCode, string? Error)> CreateAsync(
        Guid ownerUserId,
        CreateLobbyRequest request
    )
    {
        // Validate name
        if (request.Name == null)
            return (null, 400, "Lobby name is required.");

        var name = StripControlChars(request.Name).Trim();
        if (name.Length < 1 || name.Length > 40)
            return (null, 400, "Lobby name must be 1-40 characters.");

        // Look up owner for display name + cap check
        var owner = await _db.Users.FindAsync(ownerUserId);
        if (owner == null)
            return (null, 404, "Owner not found.");

        // Enforce per-account active lobby cap
        var activeCount = await _db
            .Lobbies.Where(l => l.OwnerUserId == ownerUserId && l.Status != LobbyStatus.Deleted)
            .CountAsync();
        if (activeCount >= MaxActiveLobbiesPerOwner)
            return (null, 429, $"You can own at most {MaxActiveLobbiesPerOwner} active lobbies.");

        // Fail fast if Redis is unavailable — a lobby created without a generation
        // job would be stuck in Generating indefinitely.
        if (!_redis.IsAvailable())
            return (null, 503, "Service temporarily unavailable.");

        // Generate unique code with collision retry
        string? code = null;
        for (int i = 0; i < MaxCodeCollisionRetries; i++)
        {
            var candidate = GenerateCode();
            var exists = await _db.Lobbies.AnyAsync(l => l.Code == candidate);
            if (!exists)
            {
                code = candidate;
                break;
            }
            _logger.LogInformation("Lobby code collision on {Code}, retrying", candidate);
        }
        if (code == null)
            return (null, 500, "Failed to generate a unique lobby code. Try again.");

        var now = DateTime.UtcNow;
        var lobby = new Lobby
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = name,
            OwnerUserId = ownerUserId,
            Width = _options.DefaultWidth,
            Height = _options.DefaultHeight,
            Seed = GenerateSeed(),
            MaxArrowLength = _options.MaxArrowLength,
            Status = LobbyStatus.Generating,
            CreatedAt = now,
            LastActivityAt = now,
        };

        _db.Lobbies.Add(lobby);
        await _db.SaveChangesAsync();

        await EnqueueGenerationJobAsync(lobby);

        return (BuildResponse(lobby, owner.DisplayName), 201, null);
    }

    /// <summary>
    /// Re-enqueues a failed lobby's generation job. Owner-only.
    /// Resets status from <see cref="LobbyStatus.GenerationFailed"/> back to
    /// <see cref="LobbyStatus.Generating"/>.
    /// </summary>
    public async Task<(
        MessageResponse? Response,
        int StatusCode,
        string? Error
    )> RetryGenerationAsync(Guid lobbyId, Guid requesterUserId)
    {
        var lobby = await _db.Lobbies.FindAsync(lobbyId);
        if (lobby == null || lobby.Status == LobbyStatus.Deleted)
            return (null, 404, "Lobby not found.");
        if (lobby.OwnerUserId != requesterUserId)
            return (null, 403, "Only the owner can retry generation.");
        if (lobby.Status != LobbyStatus.GenerationFailed)
            return (null, 400, "Lobby is not in a failed state.");

        if (!_redis.IsAvailable())
            return (null, 503, "Service temporarily unavailable.");

        lobby.Status = LobbyStatus.Generating;
        lobby.LastActivityAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await EnqueueGenerationJobAsync(lobby);

        return (new MessageResponse("Generation re-enqueued."), 200, null);
    }

    private async Task EnqueueGenerationJobAsync(Lobby lobby)
    {
        // Callers have already verified _redis.IsAvailable() at the entry point;
        // this double-check is belt-and-braces in case Redis drops between the
        // gate and here. If it does, log and continue — the lobby is already
        // persisted, and the user will see it stuck in Generating (better than
        // a 500 mid-request).
        if (!_redis.IsAvailable())
        {
            _logger.LogWarning(
                "Redis unavailable — generation job for lobby {Id} will not be processed.",
                lobby.Id
            );
            return;
        }

        var job = new GenerationJob
        {
            LobbyId = lobby.Id,
            OwnerUserId = lobby.OwnerUserId,
            Code = lobby.Code,
            EnqueuedAt = DateTime.UtcNow.ToString("O"),
        };
        var json = JsonSerializer.Serialize(job);
        var db = _redis!.GetDatabase();
        await db.ListLeftPushAsync(GenerationQueueKey, json);
    }

    public async Task<(LobbyResponse? Response, int StatusCode, string? Error)> GetByCodeAsync(
        string code
    )
    {
        var normalized = (code ?? "").Trim().ToUpperInvariant();
        if (normalized.Length != CodeLength)
            return (null, 404, "Lobby not found.");

        var lobby = await _db.Lobbies.FirstOrDefaultAsync(l =>
            l.Code == normalized && l.Status != LobbyStatus.Deleted
        );
        if (lobby == null)
            return (null, 404, "Lobby not found.");

        var owner = await _db.Users.FindAsync(lobby.OwnerUserId);
        return (BuildResponse(lobby, owner?.DisplayName ?? ""), 200, null);
    }

    public async Task<(LobbyListResponse? Response, int StatusCode, string? Error)> ListMineAsync(
        Guid userId,
        string? filter,
        string? sort,
        int page
    )
    {
        const int PageSize = 20;
        if (page < 0)
            page = 0;

        // Phase 3: only owned lobbies. Phase 5+ will broaden to "owned OR registered to".
        IQueryable<Lobby> query = _db.Lobbies.Where(l =>
            l.OwnerUserId == userId && l.Status != LobbyStatus.Deleted
        );

        if (string.Equals(filter, "active", StringComparison.OrdinalIgnoreCase))
            query = query.Where(l =>
                l.Status == LobbyStatus.Active || l.Status == LobbyStatus.Generating
            );
        else if (string.Equals(filter, "completed", StringComparison.OrdinalIgnoreCase))
            query = query.Where(l => l.Status == LobbyStatus.Completed);

        query = string.Equals(sort, "alphabetical", StringComparison.OrdinalIgnoreCase)
            ? query.OrderBy(l => l.Name)
            : query.OrderByDescending(l => l.LastActivityAt);

        var ownerName = (await _db.Users.FindAsync(userId))?.DisplayName ?? "";
        var items = await query.Skip(page * PageSize).Take(PageSize + 1).ToListAsync();
        var hasMore = items.Count > PageSize;
        if (hasMore)
            items.RemoveAt(PageSize);

        var entries = items
            .Select(l => new LobbyListEntry(
                l.Id,
                l.Code,
                l.Name,
                ownerName,
                l.Width,
                l.Height,
                (short)l.Status,
                l.CreatedAt,
                l.LastActivityAt
            ))
            .ToList();

        return (new LobbyListResponse(entries, page, hasMore), 200, null);
    }

    public async Task<(MessageResponse? Response, int StatusCode, string? Error)> SoftDeleteAsync(
        Guid lobbyId,
        Guid requesterUserId
    )
    {
        var lobby = await _db.Lobbies.FindAsync(lobbyId);
        if (lobby == null || lobby.Status == LobbyStatus.Deleted)
            return (null, 404, "Lobby not found.");

        if (lobby.OwnerUserId != requesterUserId)
            return (null, 403, "Only the owner can delete this lobby.");

        lobby.Status = LobbyStatus.Deleted;
        lobby.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return (new MessageResponse("Lobby deleted."), 200, null);
    }

    /// <summary>Look up a lobby by code (case-insensitive). Returns null if missing or deleted.</summary>
    public Task<Lobby?> FindActiveByCodeAsync(string code)
    {
        var normalized = (code ?? "").Trim().ToUpperInvariant();
        return _db.Lobbies.FirstOrDefaultAsync(l =>
            l.Code == normalized && l.Status != LobbyStatus.Deleted
        );
    }

    // -- Helpers -------------------------------------------------------------

    private static LobbyResponse BuildResponse(Lobby lobby, string ownerDisplayName) =>
        new(
            lobby.Id,
            lobby.Code,
            lobby.Name,
            lobby.OwnerUserId,
            ownerDisplayName,
            lobby.Width,
            lobby.Height,
            (short)lobby.Status,
            lobby.CreatedAt,
            lobby.LastActivityAt,
            ShareUrlBase + lobby.Code
        );

    private static string GenerateCode()
    {
        var chars = new char[CodeLength];
        var bytes = new byte[CodeLength];
        RandomNumberGenerator.Fill(bytes);
        for (int i = 0; i < CodeLength; i++)
            chars[i] = CodeAlphabet[bytes[i] % CodeAlphabet.Length];
        return new string(chars);
    }

    private static long GenerateSeed()
    {
        var bytes = new byte[8];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToInt64(bytes, 0);
    }

    private static string StripControlChars(string s)
    {
        var chars = s.Where(c => !char.IsControl(c) || c == ' ').ToArray();
        return new string(chars);
    }
}

/// <summary>Payload pushed to the <c>coop:gen:queue</c> Redis list.</summary>
public class GenerationJob
{
    public Guid LobbyId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Code { get; set; } = "";
    public string EnqueuedAt { get; set; } = "";
}
