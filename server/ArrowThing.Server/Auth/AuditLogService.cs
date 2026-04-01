using ArrowThing.Server.Data;
using ArrowThing.Server.Models;

namespace ArrowThing.Server.Auth;

public class AuditLogService
{
    private readonly AppDbContext _db;

    public AuditLogService(AppDbContext db)
    {
        _db = db;
    }

    public async Task LogAsync(
        string eventType,
        Guid? userId = null,
        string? email = null,
        string? ipAddress = null,
        string? detail = null
    )
    {
        _db.AuditLogs.Add(
            new AuditLog
            {
                Timestamp = DateTime.UtcNow,
                Event = eventType,
                UserId = userId,
                Email = email,
                IpAddress = ipAddress,
                Detail = detail,
            }
        );

        await _db.SaveChangesAsync();
    }
}
