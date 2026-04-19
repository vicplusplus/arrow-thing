using ArrowThing.Server.Data;
using ArrowThing.Server.Models;

namespace ArrowThing.Server.Auth;

public class AuditLogService
{
    readonly AppDbContext _db;
    readonly ILogger<AuditLogService> _logger;

    public AuditLogService(AppDbContext db, ILogger<AuditLogService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task LogAsync(
        string eventType,
        Guid? userId,
        string? email,
        string? ipAddress,
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

        // Emit only the event + UserId to structured logs — the AuditLogs DB
        // table is the authoritative record for email / IP / detail. Keeping
        // raw PII out of Loki/Grafana limits the blast radius if the logging
        // pipeline or its storage is ever compromised; the audit trail in
        // Postgres is still queryable for support + compliance.
        _logger.LogInformation("Audit: {AuditEvent} UserId={UserId}", eventType, userId);
    }
}
