using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace ArrowThing.Server.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<UserDevice> UserDevices => Set<UserDevice>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Score> Scores => Set<Score>();
    public DbSet<Lobby> Lobbies => Set<Lobby>();
    public DbSet<LobbyRegistration> LobbyRegistrations => Set<LobbyRegistration>();
    public DbSet<LobbyEvent> LobbyEvents => Set<LobbyEvent>();
    public DbSet<LobbySnapshot> LobbySnapshots => Set<LobbySnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Email).HasMaxLength(254).IsRequired();
            entity.Property(u => u.DisplayName).HasMaxLength(24).IsRequired();
            entity.Property(u => u.PasswordHash).IsRequired();
            entity.Property(u => u.CreatedAt).IsRequired();

            // Email is stored lowercase; app layer normalizes on write.
            entity.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<UserDevice>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.Property(d => d.DeviceIdHash).IsRequired();
            entity.Property(d => d.FirstSeenAt).IsRequired();
            entity.Property(d => d.LastSeenAt).IsRequired();
            entity.Property(d => d.UserAgent).HasMaxLength(512);

            entity
                .HasOne(d => d.User)
                .WithMany()
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(d => d.UserId);
        });

        modelBuilder.Entity<Score>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Time).IsRequired();
            entity.Property(s => s.ReplayJson).IsRequired();
            entity.Property(s => s.CreatedAt).IsRequired();
            entity.Property(s => s.UpdatedAt).IsRequired();

            entity
                .HasOne(s => s.User)
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // One best score per player per board size.
            entity
                .HasIndex(s => new
                {
                    s.UserId,
                    s.BoardWidth,
                    s.BoardHeight,
                })
                .IsUnique();

            // Fast top-N leaderboard queries.
            entity.HasIndex(s => new
            {
                s.BoardWidth,
                s.BoardHeight,
                s.Time,
            });
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Timestamp).IsRequired();
            entity.Property(e => e.Event).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(254);
            entity.Property(e => e.IpAddress).HasMaxLength(45);
            entity.Property(e => e.Detail).HasMaxLength(500);

            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.Event);
            entity.HasIndex(e => e.UserId);
        });

        modelBuilder.Entity<Lobby>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Code).HasMaxLength(6).IsRequired();
            entity.Property(l => l.Name).HasMaxLength(40).IsRequired();
            entity.Property(l => l.OwnerUserId).IsRequired();
            entity.Property(l => l.CreatedAt).IsRequired();
            entity.Property(l => l.LastActivityAt).IsRequired();

            entity.HasIndex(l => l.Code).IsUnique();
            entity.HasIndex(l => new { l.OwnerUserId, l.Status });
            entity.HasIndex(l => l.LastActivityAt);
        });

        modelBuilder.Entity<LobbyRegistration>(entity =>
        {
            entity.HasKey(r => new { r.LobbyId, r.UserId });
            entity.Property(r => r.DisplayNameAtJoin).HasMaxLength(24).IsRequired();
            entity.Property(r => r.ColorAtJoin).HasMaxLength(7).IsRequired();
            entity.Property(r => r.JoinedAt).IsRequired();
            entity.Property(r => r.LastActivityAt).IsRequired();

            entity.HasIndex(r => r.UserId);
        });

        modelBuilder.Entity<LobbyEvent>(entity =>
        {
            entity.HasKey(e => new { e.LobbyId, e.Seq });
            entity.Property(e => e.Type).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
        });

        modelBuilder.Entity<LobbySnapshot>(entity =>
        {
            entity.HasKey(s => s.LobbyId);
            entity.Property(s => s.Format).IsRequired();
            entity.Property(s => s.Data).IsRequired();
            entity.Property(s => s.UpdatedAt).IsRequired();
        });
    }
}
