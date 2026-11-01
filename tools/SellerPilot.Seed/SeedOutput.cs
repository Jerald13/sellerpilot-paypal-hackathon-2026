using System.Text.Json;
using System.Text.Json.Serialization;

namespace SellerPilot.Seed;

/// <summary>seed-output.json: the IDs the seed created, read by the eval runner and useful for the demo.</summary>
public sealed class SeedOutput
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public DateTimeOffset GeneratedAt { get; set; }

    public string RunTag { get; set; } = "";

    /// <summary>"card" (fully scripted) or "buyer-approval" (orders approved by hand in the sandbox).</summary>
    public string OrderFlow { get; set; } = "";

    public Dictionary<string, SeededOrder> Orders { get; set; } = [];

    public Dictionary<string, SeededInvoice> Invoices { get; set; } = [];

    public List<string> Warnings { get; set; } = [];
}

public sealed class SeededOrder
{
    public string? OrderId { get; set; }
    public string? CaptureId { get; set; }

    /// <summary>CAPTURED, AWAITING_BUYER_APPROVAL, REFUNDED, PARTIALLY_REFUNDED or FAILED.</summary>
    public string Status { get; set; } = "";
    public string Buyer { get; set; } = "";
    public string Currency { get; set; } = "";
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
    public string Reference { get; set; } = "";
    public decimal? PlannedRefund { get; set; }
    public string? RefundId { get; set; }
    public decimal? RemainingRefundable { get; set; }
    public string? ApproveUrl { get; set; }
    public string? Error { get; set; }
}

public sealed class SeededInvoice
{
    public string? InvoiceId { get; set; }
    public string? InvoiceNumber { get; set; }
    public string Status { get; set; } = "";
    public string Buyer { get; set; } = "";
    public string Currency { get; set; } = "";
    public decimal Amount { get; set; }
    public string? DueDate { get; set; }
    public string? Error { get; set; }
}
