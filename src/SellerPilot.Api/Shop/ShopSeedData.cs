using System.Text.Json;
using SellerPilot.Api.Data;

namespace SellerPilot.Api.Shop;

/// <summary>seed-output.json as written by tools/SellerPilot.Seed (only the fields the shop needs).</summary>
public sealed record SeedOutputFile(DateTimeOffset GeneratedAt, string RunTag, Dictionary<string, SeedOutputOrder> Orders)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed record SeedOutputOrder(
    string? OrderId,
    string? CaptureId,
    string Status,
    string Currency,
    decimal Amount,
    string Description,
    string Reference,
    string? RefundId,
    decimal? PlannedRefund);

/// <summary>
/// The demo shop's books, built on top of the real sandbox orders from seed-output.json.
/// A few records deliberately disagree with PayPal, so reconciliation has real problems to find (see <see cref="OrderSeed.Planted"/>).
/// </summary>
public static class ShopSeedData
{
    public sealed record CustomerSeed(string Key, string Name, string Email, string Country);

    public sealed record ProductSeed(string Sku, string Name, decimal Price, string Currency, int ReturnWindowDays);

    /// <param name="SeedKey">Key in seed-output.json "orders".</param>
    /// <param name="BookPayment">Whether the books recorded the customer's payment.</param>
    /// <param name="BookSeededRefund">Whether the books recorded the refund the seed tool made in PayPal.</param>
    /// <param name="Planted">Why this record disagrees with PayPal on purpose (null = books are correct).</param>
    public sealed record OrderSeed(
        string SeedKey,
        string CustomerKey,
        string Sku,
        ShopOrderStatus Status,
        string? DiscountCode = null,
        decimal Discount = 0m,
        bool BookPayment = true,
        bool BookSeededRefund = true,
        string? Planted = null);

    public static readonly IReadOnlyList<CustomerSeed> Customers =
    [
        new("john", "John Doe", "john.doe@example.com", "US"),
        new("alice", "Alice Carter", "alice.carter@example.com", "US"),
        new("ben", "Ben Miller", "ben.miller@example.com", "SG"),
        new("chloe", "Chloe Bennett", "chloe.bennett@example.com", "MY"),
    ];

    // Prices match the items in the sandbox PayPal orders.
    public static readonly IReadOnlyList<ProductSeed> Products =
    [
        new("SCARF-BLUE", "Blue silk scarf", 45.00m, "USD", 30),
        new("RUNNER-SET", "Linen table runner set (2 pcs)", 120.00m, "USD", 30),
        new("WRAP", "Hand-stamped cotton wrap", 68.00m, "SGD", 30),
        new("SHIRT-L", "Cotton shirt, size L (clearance, final sale)", 150.00m, "MYR", 0),
        new("COASTERS", "Coaster set", 25.00m, "USD", 30),
        new("CUSHIONS", "Cushion covers (pair)", 80.00m, "USD", 30),
        new("TOTE", "Canvas tote bag", 60.00m, "USD", 30),
        new("WALL-L", "Wall hanging, large", 150.00m, "USD", 30),
        new("KEYCHAIN", "Fabric keychain", 15.00m, "USD", 30),
        new("BOOKMARKS", "Bookmark set", 32.00m, "SGD", 30),
    ];

    /// <summary>SKUs renamed since earlier demo databases were seeded (old -> new).</summary>
    public static readonly IReadOnlyDictionary<string, string> RenamedSkus = new Dictionary<string, string> { ["SARONG"] = "WRAP" };

    // small_order (USD 15 keychain) is left out on purpose: PayPal has the money, the shop has no order.
    public static readonly IReadOnlyList<OrderSeed> Orders =
    [
        new("full_refund", "john", "SCARF-BLUE", ShopOrderStatus.Delivered),
        new("partial_refund", "john", "RUNNER-SET", ShopOrderStatus.Delivered, DiscountCode: "WELCOME10", Discount: 12.00m,
            Planted: "The WELCOME10 discount was not applied at checkout, so PayPal charged the full USD 120.00 instead of USD 108.00."),
        new("sgd_order", "ben", "WRAP", ShopOrderStatus.Shipped,
            Planted: "The books say the payment arrived and the order shipped, but the PayPal payment is still pending."),
        new("myr_order", "chloe", "SHIRT-L", ShopOrderStatus.Delivered),
        new("already_refunded", "john", "COASTERS", ShopOrderStatus.Delivered, BookSeededRefund: false,
            Planted: "The order was refunded in the PayPal dashboard, and nobody recorded it in the books."),
        new("partially_refunded", "john", "CUSHIONS", ShopOrderStatus.Delivered),
        new("injection", "alice", "TOTE", ShopOrderStatus.Delivered),
        new("big_order", "john", "WALL-L", ShopOrderStatus.Delivered),
        new("sgd_small", "ben", "BOOKMARKS", ShopOrderStatus.AwaitingPayment, BookPayment: false),

        // Fresh copies of "partially_refunded" (USD 80, USD 30 refunded): one for the eval, one for the demo video, one spare.
        new("eval_partial", "alice", "CUSHIONS", ShopOrderStatus.Delivered),
        new("video_partial", "john", "CUSHIONS", ShopOrderStatus.Delivered),
        new("spare_partial", "john", "CUSHIONS", ShopOrderStatus.Delivered),
    ];

    /// <summary>An older order paid by bank transfer, to show a return window that has ended.</summary>
    public static readonly (string OrderNumber, string CustomerKey, string Sku, DateTime PlacedAt, DateTime DeliveredAt) HistoricalOrder =
        ("SP-HIST-0830", "john", "SCARF-BLUE", new DateTime(2026, 8, 30, 9, 15, 0, DateTimeKind.Utc), new DateTime(2026, 9, 4, 14, 0, 0, DateTimeKind.Utc));
}
