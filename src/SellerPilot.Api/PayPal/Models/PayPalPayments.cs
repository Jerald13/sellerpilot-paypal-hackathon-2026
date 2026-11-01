namespace SellerPilot.Api.PayPal.Models;

// Payments v2: captures and refunds.

public static class CaptureStatuses
{
    public const string Completed = "COMPLETED";
    public const string PartiallyRefunded = "PARTIALLY_REFUNDED";
    public const string Refunded = "REFUNDED";
    public const string Pending = "PENDING";
    public const string Declined = "DECLINED";
    public const string Failed = "FAILED";
}

public static class RefundStatuses
{
    public const string Completed = "COMPLETED";
    public const string Pending = "PENDING";
    public const string Cancelled = "CANCELLED";
    public const string Failed = "FAILED";
}

public sealed record PayPalCapture(
    string Id,
    string? Status,
    PayPalMoney? Amount,
    bool? FinalCapture,
    string? InvoiceId,
    string? CustomId,
    string? CreateTime,
    string? UpdateTime,
    PayPalStatusDetails? StatusDetails,
    PayPalSupplementaryData? SupplementaryData,
    IReadOnlyList<PayPalLink>? Links)
{
    /// <summary>The order this capture belongs to, from supplementary_data or the "up" link.</summary>
    public string? RelatedOrderId =>
        SupplementaryData?.RelatedIds?.OrderId
        ?? Links?.FirstOrDefault(l => l.Rel == "up" && l.Href?.Contains("/checkout/orders/", StringComparison.Ordinal) == true)
            ?.Href?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
}

public sealed record PayPalSupplementaryData(PayPalRelatedIds? RelatedIds);

public sealed record PayPalRelatedIds(string? OrderId);

public sealed record PayPalRefund(
    string Id,
    string? Status,
    PayPalMoney? Amount,
    string? NoteToPayer,
    string? CustomId,
    string? InvoiceId,
    string? CreateTime,
    string? UpdateTime,
    PayPalStatusDetails? StatusDetails,
    IReadOnlyList<PayPalLink>? Links)
{
    /// <summary>The capture this refund belongs to, from its "up" link (null if PayPal didn't send one).</summary>
    public string? CaptureId =>
        Links?.FirstOrDefault(l => l.Rel == "up" && l.Href?.Contains("/payments/captures/", StringComparison.Ordinal) == true)
            ?.Href?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
}

/// <summary>Body of POST /v2/payments/captures/{id}/refund.</summary>
public sealed record PayPalRefundRequest(PayPalMoney Amount, string? CustomId = null, string? NoteToPayer = null);
