using SellerPilot.Api.Data;

namespace SellerPilot.Api.Contracts;

public sealed record LedgerEntryDto(long Id, LedgerEntryType Type, decimal Amount, string Currency, string? PayPalTransactionId, string Source, DateTime RecordedAt, string? Note)
{
    public static LedgerEntryDto From(LedgerEntry e) => new(e.Id, e.Type, e.Amount, e.Currency, e.PayPalTransactionId, e.Source, e.RecordedAt, e.Note);
}

public sealed record OrderLineDto(string Sku, string Name, int Quantity, decimal UnitPrice, int ReturnWindowDays);

/// <summary>A shop order as the seller sees it in the back office (full customer details are fine here).</summary>
public sealed record ShopOrderDto(
    int Id,
    string OrderNumber,
    string CustomerName,
    string CustomerEmail,
    string CustomerCountry,
    ShopOrderStatus Status,
    PaymentMethod PaymentMethod,
    string Currency,
    decimal Subtotal,
    decimal Discount,
    string? DiscountCode,
    decimal Total,
    decimal PaymentsRecorded,
    decimal RefundsRecorded,
    DateTime PlacedAt,
    DateTime? ShippedAt,
    DateTime? DeliveredAt,
    string? PayPalOrderId,
    string? PayPalCaptureId,
    IReadOnlyList<OrderLineDto> Lines,
    IReadOnlyList<LedgerEntryDto> Ledger)
{
    public static ShopOrderDto From(ShopOrder o) => new(
        o.Id,
        o.OrderNumber,
        o.Customer?.Name ?? "",
        o.Customer?.Email ?? "",
        o.Customer?.Country ?? "",
        o.Status,
        o.PaymentMethod,
        o.Currency,
        o.Subtotal,
        o.Discount,
        o.DiscountCode,
        o.Total,
        o.LedgerEntries.Where(e => e.Type == LedgerEntryType.Payment).Sum(e => e.Amount),
        o.LedgerEntries.Where(e => e.Type == LedgerEntryType.Refund).Sum(e => e.Amount),
        o.PlacedAt,
        o.ShippedAt,
        o.DeliveredAt,
        o.PayPalOrderId,
        o.PayPalCaptureId,
        o.Lines.Select(l => new OrderLineDto(l.Sku, l.Name, l.Quantity, l.UnitPrice, l.ReturnWindowDays)).ToList(),
        o.LedgerEntries.OrderBy(e => e.RecordedAt).Select(LedgerEntryDto.From).ToList());
}

public sealed record FindingDto(
    int Id,
    FindingKind Kind,
    FindingSeverity Severity,
    string? OrderNumber,
    string? PayPalOrderId,
    string? PayPalTransactionId,
    string? Currency,
    decimal? BooksAmount,
    decimal? PayPalAmount,
    decimal? Difference,
    string Message,
    string? SuggestedFix)
{
    public static FindingDto From(ReconciliationFinding f) => new(
        f.Id, f.Kind, f.Severity, f.OrderNumber, f.PayPalOrderId, f.PayPalTransactionId, f.Currency, f.BooksAmount, f.PayPalAmount, f.Difference, f.Message, f.SuggestedFix);
}

public sealed record ReconciliationRunDto(
    int Id,
    DateTime StartedAt,
    DateTime? FinishedAt,
    ReconciliationStatus Status,
    string Trigger,
    int OrdersChecked,
    int OrdersMatched,
    int PayPalPaymentsChecked,
    int IssuesFound,
    string? Note,
    string? Error,
    IReadOnlyList<FindingDto> Findings)
{
    public static ReconciliationRunDto From(ReconciliationRun r) => new(
        r.Id, r.StartedAt, r.FinishedAt, r.Status, r.Trigger, r.OrdersChecked, r.OrdersMatched, r.PayPalPaymentsChecked, r.IssuesFound, r.Note, r.Error,
        r.Findings.OrderBy(f => f.Severity).ThenBy(f => f.OrderNumber).Select(FindingDto.From).ToList());
}
