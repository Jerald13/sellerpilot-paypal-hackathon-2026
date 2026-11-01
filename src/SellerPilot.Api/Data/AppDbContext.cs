using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SellerPilot.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<PendingAction> PendingActions => Set<PendingAction>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ShopOrder> ShopOrders => Set<ShopOrder>();

    public DbSet<ShopOrderLine> ShopOrderLines => Set<ShopOrderLine>();

    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();

    public DbSet<ReconciliationFinding> ReconciliationFindings => Set<ReconciliationFinding>();

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
            e.Property(x => x.RequestedBy).HasMaxLength(16).HasDefaultValue(ActionRequesters.Agent);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.TargetId, x.Status });
            e.HasIndex(x => x.CustomerId);
        });

        modelBuilder.Entity<Customer>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Email).HasMaxLength(200).IsRequired();
            e.Property(x => x.Country).HasMaxLength(2);
            e.Property(x => x.PasswordHash).HasMaxLength(200);
            e.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<Product>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Sku).HasMaxLength(32).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.HasIndex(x => x.Sku).IsUnique();
        });

        modelBuilder.Entity<ShopOrder>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.OrderNumber).HasMaxLength(32).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.PaymentMethod).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.DiscountCode).HasMaxLength(32);
            e.Property(x => x.PayPalOrderId).HasMaxLength(64);
            e.Property(x => x.PayPalCaptureId).HasMaxLength(64);
            e.HasIndex(x => x.OrderNumber).IsUnique();
            e.HasIndex(x => x.PayPalOrderId).IsUnique().HasFilter("\"PayPalOrderId\" IS NOT NULL");
            e.HasIndex(x => x.PayPalCaptureId);
            e.HasOne(x => x.Customer).WithMany(c => c.Orders).HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.ShopOrderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ShopOrderLine>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Sku).HasMaxLength(32).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<LedgerEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.PayPalTransactionId).HasMaxLength(64);
            e.Property(x => x.Source).HasMaxLength(16).IsRequired();
            e.Property(x => x.Note).HasMaxLength(500);
            // One PayPal movement can be booked only once, whoever tries to post it.
            e.HasIndex(x => x.PayPalTransactionId).IsUnique().HasFilter("\"PayPalTransactionId\" IS NOT NULL");
            e.HasOne(x => x.ShopOrder).WithMany(o => o.LedgerEntries).HasForeignKey(x => x.ShopOrderId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReconciliationRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Trigger).HasMaxLength(16);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasMany(x => x.Findings).WithOne().HasForeignKey(f => f.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReconciliationFinding>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.OrderNumber).HasMaxLength(32);
            e.Property(x => x.PayPalOrderId).HasMaxLength(64);
            e.Property(x => x.PayPalTransactionId).HasMaxLength(64);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Message).HasMaxLength(1000).IsRequired();
            e.Property(x => x.SuggestedFix).HasMaxLength(500);
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
