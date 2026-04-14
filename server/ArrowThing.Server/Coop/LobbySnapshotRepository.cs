using ArrowThing.Server.Data;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace ArrowThing.Server.Coop;

/// <summary>
/// DB I/O for <see cref="LobbySnapshot"/>. Stores the raw binary blob
/// produced by <c>BinarySnapshot.EncodeFull</c> (gzip is handled by the
/// encoder via the format flag). Scoped lifetime so each call gets a
/// fresh DbContext.
/// </summary>
public class LobbySnapshotRepository
{
    private readonly AppDbContext _db;

    public LobbySnapshotRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task SaveAsync(Guid lobbyId, byte[] data, LobbySnapshotFormat format)
    {
        var existing = await _db.LobbySnapshots.FindAsync(lobbyId);
        if (existing == null)
        {
            _db.LobbySnapshots.Add(
                new LobbySnapshot
                {
                    LobbyId = lobbyId,
                    Format = format,
                    Data = data,
                    UpdatedAt = DateTime.UtcNow,
                }
            );
        }
        else
        {
            existing.Format = format;
            existing.Data = data;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
    }

    public async Task<LobbySnapshot?> LoadAsync(Guid lobbyId)
    {
        return await _db.LobbySnapshots.FindAsync(lobbyId);
    }
}
