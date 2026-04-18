using ArrowThing.Server.Data;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace ArrowThing.Server.Coop;

/// <summary>
/// Builds a v6 <see cref="ReplayData"/> for a finished (or in-progress) co-op
/// lobby by combining the stored initial snapshot with the persisted event
/// log and the current roster.
///
/// Replays are constructed on demand — there is no cached output. The query
/// volume is bounded by board size (one row per accepted clear), and the
/// board snapshot is already persisted in <see cref="LobbySnapshot"/> so
/// this never re-runs generation.
/// </summary>
public class LobbyReplayService
{
    private readonly AppDbContext _db;

    public LobbyReplayService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Load the lobby + events + registrations and project them into a
    /// client-ready <see cref="ReplayData"/>. Returns null if the lobby is
    /// missing, deleted without snapshot, or had its snapshot stripped by
    /// the retention job.
    /// </summary>
    public async Task<(ReplayData? Replay, int StatusCode, string? Error)> BuildAsync(
        string code,
        Guid requesterUserId
    )
    {
        var normalized = (code ?? "").Trim().ToUpperInvariant();
        var lobby = await _db.Lobbies.FirstOrDefaultAsync(l => l.Code == normalized);
        if (lobby == null || lobby.Status == LobbyStatus.Deleted)
            return (null, 404, "Lobby not found.");

        // Authorization: only players who registered in this lobby can fetch
        // the replay. Prevents drive-by scraping of lobby codes.
        var isRegistered = await _db.LobbyRegistrations.AnyAsync(r =>
            r.LobbyId == lobby.Id && r.UserId == requesterUserId
        );
        if (!isRegistered)
            return (null, 403, "You are not a participant in this lobby.");

        if (lobby.SnapshotStrippedAt != null)
            return (null, 410, "Replay is no longer available (retention).");

        var snapshot = await _db.LobbySnapshots.FindAsync(lobby.Id);
        if (snapshot == null || snapshot.Data.Length == 0)
            return (null, 404, "Snapshot missing.");

        var registrations = await _db
            .LobbyRegistrations.AsNoTracking()
            .Where(r => r.LobbyId == lobby.Id)
            .ToListAsync();

        var events = await _db
            .LobbyEvents.AsNoTracking()
            .Where(e => e.LobbyId == lobby.Id && e.Type == LobbyEventType.ClearAccepted)
            .OrderBy(e => e.Seq)
            .ToListAsync();

        // Build ReplayData. Use the lobby's initial board layout (rehydrated
        // from the BinarySnapshot blob) and map each ClearAccepted row to a
        // clear event in wall-clock order.
        var replay = new ReplayData
        {
            version = 6,
            gameId = lobby.Id.ToString(),
            // ReplayData.seed is int32; lobby seeds fit by convention
            // (generated from RNG ranges of int size).
            seed = unchecked((int)lobby.Seed),
            boardWidth = lobby.Width,
            boardHeight = lobby.Height,
            maxArrowLength = lobby.MaxArrowLength,
            inspectionDuration = 0f, // co-op has no inspection phase
            gameVersion = null,
        };

        // Re-encode the snapshot's arrow paths as the replay's boardSnapshot
        // base64 blob. `LobbySnapshots.Data` is a full BinarySnapshot frame
        // (header + gzipped arrow table); decode it to a Board and re-project
        // the arrows into the replay's arrows-only format.
        var board = BinarySnapshot.DecodeFull(snapshot.Data);
        var arrowPaths = new List<IReadOnlyList<Cell>>(board.Arrows.Count);
        foreach (var a in board.Arrows)
            arrowPaths.Add(a.Cells);
        replay.SetSnapshotArrows(arrowPaths);

        // Roster: every registered player, ordered by JoinedAt so the header
        // is stable across repeated fetches.
        replay.roster = registrations
            .OrderBy(r => r.JoinedAt)
            .Select(r => new ReplayRosterEntry
            {
                playerId = r.UserId,
                displayName = r.DisplayNameAtJoin,
                color = r.ColorAtJoin,
            })
            .ToList();

        // Events. Prepend a session_start so downstream replay playback has
        // the anchor it expects. Each ClearAccepted becomes a clear event
        // with playerId attribution.
        var createdAtUtc = lobby.CreatedAt.ToUniversalTime().ToString("O");
        replay.events.Add(
            new ReplayEvent
            {
                seq = 0,
                type = ReplayEventType.SessionStart,
                timestamp = createdAtUtc,
            }
        );
        replay.events.Add(
            new ReplayEvent
            {
                seq = 1,
                type = ReplayEventType.StartSolve,
                timestamp = createdAtUtc,
            }
        );

        int nextSeq = 2;
        foreach (var evt in events)
        {
            replay.events.Add(
                new ReplayEvent
                {
                    seq = nextSeq++,
                    type = ReplayEventType.Clear,
                    posX = evt.TapX,
                    posY = evt.TapY,
                    timestamp = evt.CreatedAt.ToUniversalTime().ToString("O"),
                    playerId = evt.UserId,
                }
            );
        }

        if (lobby.Status == LobbyStatus.Completed && lobby.CompletedAt != null)
        {
            replay.events.Add(
                new ReplayEvent
                {
                    seq = nextSeq++,
                    type = ReplayEventType.EndSolve,
                    timestamp = lobby.CompletedAt.Value.ToUniversalTime().ToString("O"),
                }
            );
            replay.finalTime = (lobby.CompletedAt.Value - lobby.CreatedAt).TotalSeconds;
        }
        else
        {
            replay.finalTime = -1.0;
        }

        return (replay, 200, null);
    }
}
