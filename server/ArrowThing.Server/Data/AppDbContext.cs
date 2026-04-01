using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace ArrowThing.Server.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

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

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Timestamp).IsRequired();
            entity.Property(e => e.Event).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(254);
            entity.Property(e => e.IpAddress).HasMaxLength(45);
            entity.Property(e => e.Detail).HasMaxLength(512);

            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.Event);
            entity.HasIndex(e => e.UserId);
        });
    }
}
