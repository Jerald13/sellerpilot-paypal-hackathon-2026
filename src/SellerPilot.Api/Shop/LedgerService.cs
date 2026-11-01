using Microsoft.EntityFrameworkCore;
using SellerPilot.Api.Data;

namespace SellerPilot.Api.Shop;

public interface ILedgerService
{
    /// <summary>
    /// Books a refund against the shop order paid with <paramref name="captureId"/> (or <paramref name="shopOrderId"/>).
    /// Returns null when no shop order is linked. Idempotent: a refund ID that is already booked is returned as is.
    /// </summary>
    Task<LedgerEntry?> RecordRefundAsync(string captureId, int? shopOrderId, string refundId, decimal amount, string currency, string source, string? note, CancellationToken ct);

    /// <summary>Books a payment or refund for a known order. Idempotent on <paramref name="paypalTransactionId"/>.</summary>
    Task<LedgerEntry> RecordAsync(ShopOrder order, LedgerEntryType type, decimal amount, string currency, string paypalTransactionId, string source, string? note, CancellationToken ct);
}

public sealed class LedgerService(AppDbContext db, TimeProvider time) : ILedgerService
{
    public async Task<LedgerEntry?> RecordRefundAsync(string captureId, int? shopOrderId, string refundId, decimal amount, string currency, string source, string? note, CancellationToken ct)
    {
        var order = shopOrderId is { } id
            ? await db.ShopOrders.FirstOrDefaultAsync(o => o.Id == id, ct)
            : await db.ShopOrders.FirstOrDefaultAsync(o => o.PayPalCaptureId == captureId, ct);
        return order is null ? null : await RecordAsync(order, LedgerEntryType.Refund, amount, currency, refundId, source, note, ct);
    }

    public async Task<LedgerEntry> RecordAsync(ShopOrder order, LedgerEntryType type, decimal amount, string currency, string paypalTransactionId, string source, string? note, CancellationToken ct)
    {
        var existing = await db.LedgerEntries.FirstOrDefaultAsync(e => e.PayPalTransactionId == paypalTransactionId, ct);
        if (existing is not null)
        {
            return existing;
        }

        var entry = new LedgerEntry
        {
            ShopOrderId = order.Id,
            Type = type,
            Amount = amount,
            Currency = currency,
            PayPalTransactionId = paypalTransactionId,
            Source = source,
            RecordedAt = time.GetUtcNow().UtcDateTime,
            Note = note,
        };
        db.LedgerEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        return entry;
    }
}
