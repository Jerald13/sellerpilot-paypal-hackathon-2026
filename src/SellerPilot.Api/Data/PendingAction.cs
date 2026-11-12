namespace SellerPilot.Api.Data;

public enum ActionType
{
    Refund,
    InvoiceReminder,

    /// <summary>Copies a PayPal payment or refund that is missing from the shop's books into the ledger. Never calls PayPal write endpoints.</summary>
    BooksCorrection,
}

public enum ActionStatus
{
    Pending,
    Approved,
    Rejected,
    Executed,
    Failed,

    /// <summary>A customer refund that needs the item back first; the seller marks the return as received, which moves it to Pending.</summary>
    AwaitingReturn,

    /// <summary>Public-demo dry run: approved and re-checked against PayPal, but the PayPal call was not made.</summary>
    NotSent,
}

public static class ActionRequesters
{
    /// <summary>Proposed by the seller's assistant.</summary>
    public const string Agent = "agent";

    /// <summary>Requested by a signed-in customer on the storefront.</summary>
    public const string Customer = "customer";
}

/// <summary>
/// A money-moving or customer-contacting action proposed by the agent.
/// Nothing happens at PayPal until a seller approves it.
/// </summary>
public class PendingAction
{
    /// <summary>Also sent as PayPal-Request-Id, so executing the same action twice is idempotent.</summary>
    public Guid Id { get; set; }

    public ActionType Type { get; set; }

    /// <summary>Capture ID (refund) or invoice ID (reminder).</summary>
    public string TargetId { get; set; } = "";

    public decimal? Amount { get; set; }

    public string? Currency { get; set; }

    /// <summary>The AI's explanation shown to the seller.</summary>
    public string Reason { get; set; } = "";

    /// <summary>One-line description built by code from verified PayPal data (not written by the AI).</summary>
    public string Summary { get; set; } = "";

    /// <summary>Action-specific JSON (e.g. reminder subject and note, order ID, security flags).</summary>
    public string? Payload { get; set; }

    public ActionStatus Status { get; set; }

    /// <summary>e.g. the PayPal refund ID.</summary>
    public string? PayPalResultId { get; set; }

    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? DecidedAt { get; set; }

    /// <summary>See <see cref="ActionRequesters"/>.</summary>
    public string RequestedBy { get; set; } = ActionRequesters.Agent;

    /// <summary>The customer who asked, for customer requests.</summary>
    public int? CustomerId { get; set; }

    /// <summary>The shop order the action is about, when known.</summary>
    public int? ShopOrderId { get; set; }
}
