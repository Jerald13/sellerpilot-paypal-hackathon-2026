namespace SellerPilot.Api.Data;

public enum ReconciliationStatus
{
    Completed,
    Failed,
}

public enum FindingSeverity
{
    High,
    Medium,
    Low,
}

public enum FindingKind
{
    /// <summary>The books record a payment, but PayPal has not completed it (still pending).</summary>
    PaymentNotCompleted,

    /// <summary>Booked payment and PayPal capture disagree on the amount or currency.</summary>
    PaymentMismatch,

    /// <summary>PayPal captured money for a shop order, but the books have no payment for it.</summary>
    PaymentNotInBooks,

    /// <summary>PayPal shows a refund that the books don't have.</summary>
    RefundNotInBooks,

    /// <summary>The books show a refund that PayPal doesn't have.</summary>
    RefundNotInPayPal,

    /// <summary>After refunds, the customer still paid more than the order total.</summary>
    Overcharged,

    /// <summary>PayPal captured less than the order total.</summary>
    Undercharged,

    /// <summary>The PayPal order referenced by the books does not exist.</summary>
    PayPalOrderMissing,

    /// <summary>Money arrived in PayPal that no shop order accounts for.</summary>
    UnknownPayment,
}

/// <summary>One "books vs PayPal" check.</summary>
public class ReconciliationRun
{
    public int Id { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public DateTime PeriodStart { get; set; }

    public DateTime PeriodEnd { get; set; }

    public ReconciliationStatus Status { get; set; }

    /// <summary>"seller" (Reconciliation tab) or "agent" (asked in chat).</summary>
    public string Trigger { get; set; } = "";

    public int OrdersChecked { get; set; }

    public int OrdersMatched { get; set; }

    public int PayPalPaymentsChecked { get; set; }

    public int IssuesFound { get; set; }

    /// <summary>e.g. "Transaction Search data refreshed at …" or why unlinked payments could not be checked.</summary>
    public string? Note { get; set; }

    public string? Error { get; set; }

    public List<ReconciliationFinding> Findings { get; set; } = [];
}

public class ReconciliationFinding
{
    public int Id { get; set; }

    public int RunId { get; set; }

    public FindingKind Kind { get; set; }

    public FindingSeverity Severity { get; set; }

    public int? ShopOrderId { get; set; }

    public string? OrderNumber { get; set; }

    public string? PayPalOrderId { get; set; }

    /// <summary>The capture or refund ID the finding is about, when there is one.</summary>
    public string? PayPalTransactionId { get; set; }

    public string? Currency { get; set; }

    public decimal? BooksAmount { get; set; }

    public decimal? PayPalAmount { get; set; }

    public decimal? Difference { get; set; }

    /// <summary>Plain-language explanation, built by code from the numbers above.</summary>
    public string Message { get; set; } = "";

    public string? SuggestedFix { get; set; }
}
