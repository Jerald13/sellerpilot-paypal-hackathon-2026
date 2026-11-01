namespace SellerPilot.Api.Data;

// The shop's own records ("the books"). PayPal stays the source of truth for money movements;
// these tables are what the shop believes happened, and reconciliation compares the two.

public enum ShopOrderStatus
{
    AwaitingPayment,
    Paid,
    Shipped,
    Delivered,
    Cancelled,
}

public enum PaymentMethod
{
    PayPal,
    BankTransfer,
}

public enum LedgerEntryType
{
    /// <summary>Money received from the customer.</summary>
    Payment,

    /// <summary>Money returned to the customer.</summary>
    Refund,
}

public static class LedgerSources
{
    /// <summary>Recorded by the shop's checkout when the payment came in.</summary>
    public const string Checkout = "checkout";

    /// <summary>Posted automatically after SellerPilot executed an approved refund.</summary>
    public const string SellerPilot = "sellerpilot";

    /// <summary>Added by an approved books correction (copied from PayPal after reconciliation).</summary>
    public const string Correction = "correction";

    /// <summary>A payment outside PayPal, e.g. a bank transfer.</summary>
    public const string Bank = "bank";
}

public class Customer
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Stored lowercase; unique. Used to sign in to the storefront.</summary>
    public string Email { get; set; } = "";

    /// <summary>ISO country code, e.g. US.</summary>
    public string Country { get; set; } = "";

    /// <summary>ASP.NET Core Identity password hash; null until a demo password is configured.</summary>
    public string? PasswordHash { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<ShopOrder> Orders { get; set; } = [];
}

public class Product
{
    public int Id { get; set; }

    public string Sku { get; set; } = "";

    public string Name { get; set; } = "";

    public decimal Price { get; set; }

    public string Currency { get; set; } = "";

    /// <summary>Days after delivery in which the customer can ask for a refund; 0 means final sale.</summary>
    public int ReturnWindowDays { get; set; }

    public bool Active { get; set; } = true;
}

public class ShopOrder
{
    public int Id { get; set; }

    /// <summary>The shop's order number, also sent to PayPal as the invoice_id (e.g. SP-DVAN-1001).</summary>
    public string OrderNumber { get; set; } = "";

    public int CustomerId { get; set; }

    public Customer? Customer { get; set; }

    public ShopOrderStatus Status { get; set; }

    public PaymentMethod PaymentMethod { get; set; }

    public string Currency { get; set; } = "";

    public decimal Subtotal { get; set; }

    public decimal Discount { get; set; }

    public string? DiscountCode { get; set; }

    /// <summary>What the customer should have been charged: subtotal minus discount.</summary>
    public decimal Total { get; set; }

    public DateTime PlacedAt { get; set; }

    public DateTime? ShippedAt { get; set; }

    public DateTime? DeliveredAt { get; set; }

    public string? PayPalOrderId { get; set; }

    public string? PayPalCaptureId { get; set; }

    public List<ShopOrderLine> Lines { get; set; } = [];

    public List<LedgerEntry> LedgerEntries { get; set; } = [];
}

public class ShopOrderLine
{
    public int Id { get; set; }

    public int ShopOrderId { get; set; }

    public string Sku { get; set; } = "";

    public string Name { get; set; } = "";

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    /// <summary>The product's return window when the order was placed (policy changes don't apply retroactively).</summary>
    public int ReturnWindowDays { get; set; }
}

/// <summary>One money movement as recorded in the shop's books.</summary>
public class LedgerEntry
{
    public long Id { get; set; }

    public int? ShopOrderId { get; set; }

    public ShopOrder? ShopOrder { get; set; }

    public LedgerEntryType Type { get; set; }

    /// <summary>Always positive; <see cref="Type"/> says which way the money went.</summary>
    public decimal Amount { get; set; }

    public string Currency { get; set; } = "";

    /// <summary>PayPal capture ID (payment) or refund ID (refund). Unique, so the same PayPal movement can't be booked twice.</summary>
    public string? PayPalTransactionId { get; set; }

    /// <summary>See <see cref="LedgerSources"/>.</summary>
    public string Source { get; set; } = "";

    public DateTime RecordedAt { get; set; }

    public string? Note { get; set; }
}
