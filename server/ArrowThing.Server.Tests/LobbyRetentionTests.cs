using ArrowThing.Server.Coop;
using ArrowThing.Server.Data;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArrowThing.Server.Tests;

/// <summary>
/// Retention-job tests. Seeds the DB with lobbies at various ages and
/// runs the two jobs via the internal entry points that bypass the
/// 15-minute startup delay.
/// </summary>
public class LobbyRetentionTests : IClassFixture<TestFactory>
{
    private readonly TestFactory _factory;

    public LobbyRetentionTests(TestFactory factory)
    {
        _factory = factory;
    }

    private async Task<(Lobby lobby, LobbySnapshot snapshot)> SeedLobbyAsync(
        LobbyStatus status,
        DateTime? completedAt = null,
        DateTime? deletedAt = null,
        DateTime? lastActivityAt = null,
        DateTime? snapshotStrippedAt = null
    )
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var owner = new User
        {
            Id = Guid.NewGuid(),
            Email = $"retention-{Guid.NewGuid():N}@example.com",
            DisplayName = "Owner",
            PasswordHash = "x",
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(owner);

        var lobby = new Lobby
        {
            Id = Guid.NewGuid(),
            Code = "RET" + Guid.NewGuid().ToString("N").Substring(0, 3).ToUpper(),
            Name = "retention-test",
            OwnerUserId = owner.Id,
            Width = 20,
            Height = 20,
            MaxArrowLength = 40,
            Status = status,
            CompletedAt = completedAt,
            DeletedAt = deletedAt,
            LastActivityAt = lastActivityAt ?? DateTime.UtcNow,
            CreatedAt = (lastActivityAt ?? DateTime.UtcNow).AddDays(-1),
            SnapshotStrippedAt = snapshotStrippedAt,
        };
        db.Lobbies.Add(lobby);

        var snapshot = new LobbySnapshot
        {
            LobbyId = lobby.Id,
            Format = LobbySnapshotFormat.BinaryV1,
            Data = new byte[] { 1, 2, 3 },
            UpdatedAt = DateTime.UtcNow,
        };
        db.LobbySnapshots.Add(snapshot);

        await db.SaveChangesAsync();
        return (lobby, snapshot);
    }

    private LobbyRetentionService CreateService()
    {
        var scopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        return new LobbyRetentionService(
            scopeFactory,
            _factory.Services.GetRequiredService<ILogger<LobbyRetentionService>>()
        );
    }

    [Fact]
    public async Task SnapshotStripper_StripsOldCompletedLobbies()
    {
        var longAgo = DateTime.UtcNow.AddDays(-60);
        var (lobby, _) = await SeedLobbyAsync(status: LobbyStatus.Completed, completedAt: longAgo);

        var service = CreateService();
        int stripped = await service.RunSnapshotStripperAsync(CancellationToken.None);
        Assert.True(stripped >= 1);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reloaded = await db.Lobbies.FindAsync(lobby.Id);
        Assert.NotNull(reloaded!.SnapshotStrippedAt);

        var snap = await db.LobbySnapshots.FindAsync(lobby.Id);
        Assert.NotNull(snap);
        Assert.Empty(snap!.Data);
    }

    [Fact]
    public async Task SnapshotStripper_SkipsRecentCompletedLobbies()
    {
        var recent = DateTime.UtcNow.AddDays(-5);
        var (lobby, _) = await SeedLobbyAsync(status: LobbyStatus.Completed, completedAt: recent);

        var service = CreateService();
        await service.RunSnapshotStripperAsync(CancellationToken.None);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reloaded = await db.Lobbies.FindAsync(lobby.Id);
        Assert.Null(reloaded!.SnapshotStrippedAt);

        var snap = await db.LobbySnapshots.FindAsync(lobby.Id);
        Assert.NotEmpty(snap!.Data);
    }

    [Fact]
    public async Task SnapshotStripper_SkipsAlreadyStripped()
    {
        var longAgo = DateTime.UtcNow.AddDays(-60);
        var alreadyStripped = DateTime.UtcNow.AddDays(-40);
        var (lobby, _) = await SeedLobbyAsync(
            status: LobbyStatus.Completed,
            completedAt: longAgo,
            snapshotStrippedAt: alreadyStripped
        );

        var service = CreateService();
        int stripped = await service.RunSnapshotStripperAsync(CancellationToken.None);
        // The already-stripped row must not be double-counted. It may be 0
        // or the batch count for other seeded rows.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reloaded = await db.Lobbies.FindAsync(lobby.Id);
        Assert.Equal(
            alreadyStripped.ToUniversalTime().Date,
            reloaded!.SnapshotStrippedAt!.Value.ToUniversalTime().Date
        );
    }

    [Fact]
    public async Task IdleReaper_DeletesIdleActiveLobbies()
    {
        var longAgo = DateTime.UtcNow.AddDays(-40);
        var (lobby, _) = await SeedLobbyAsync(status: LobbyStatus.Active, lastActivityAt: longAgo);

        var service = CreateService();
        int reaped = await service.RunIdleReaperAsync(CancellationToken.None);
        Assert.True(reaped >= 1);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reloaded = await db.Lobbies.FindAsync(lobby.Id);
        Assert.Equal(LobbyStatus.Deleted, reloaded!.Status);
        Assert.NotNull(reloaded.DeletedAt);
    }

    [Fact]
    public async Task IdleReaper_SkipsRecentlyActiveLobbies()
    {
        var recent = DateTime.UtcNow.AddDays(-3);
        var (lobby, _) = await SeedLobbyAsync(status: LobbyStatus.Active, lastActivityAt: recent);

        var service = CreateService();
        await service.RunIdleReaperAsync(CancellationToken.None);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reloaded = await db.Lobbies.FindAsync(lobby.Id);
        Assert.Equal(LobbyStatus.Active, reloaded!.Status);
    }

    [Fact]
    public async Task IdleReaper_DeletesStaleGenerationFailedLobbies()
    {
        var longAgo = DateTime.UtcNow.AddDays(-40);
        var (lobby, _) = await SeedLobbyAsync(
            status: LobbyStatus.GenerationFailed,
            lastActivityAt: longAgo
        );

        var service = CreateService();
        int reaped = await service.RunIdleReaperAsync(CancellationToken.None);
        Assert.True(reaped >= 1);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reloaded = await db.Lobbies.FindAsync(lobby.Id);
        Assert.Equal(LobbyStatus.Deleted, reloaded!.Status);
    }
}
