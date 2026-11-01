using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SellerPilot.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<PendingAction> PendingActions => Set<PendingAction>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PendingAction>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.TargetId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.Summary).HasMaxLength(1000);
            e.Property(x => x.PayPalResultId).HasMaxLength(64);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.TargetId, x.Status });
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(16).IsRequired();
            e.Property(x => x.Event).HasMaxLength(64).IsRequired();
            e.Property(x => x.Details).IsRequired();
            e.HasIndex(x => x.Timestamp);
        });

        // SQLite drops DateTimeKind; everything is stored as UTC, so mark it UTC on the way out.
        var utc = new ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetValueConverter(utc);
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(utcNullable);
                }
            }
        }
    }
}
