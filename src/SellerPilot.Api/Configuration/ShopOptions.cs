namespace SellerPilot.Api.Configuration;

public sealed class ShopOptions
{
    public const string SectionName = "Shop";

    public string ShopName { get; set; } = "Emma's Textile Studio";

    public string SellerName { get; set; } = "Emma";

    /// <summary>Sign-in email for the back office.</summary>
    public string SellerEmail { get; set; } = "emma@sellerpilot.test";

    /// <summary>
    /// Password for the demo seller and the demo customers. A secret: set it in .env (Shop__DemoPassword)
    /// or user-secrets, never in appsettings. Without it nobody can sign in.
    /// </summary>
    public string? DemoPassword { get; set; }

    /// <summary>seed-output.json from the seed tool, relative to the repository root. The demo shop data is built from it.</summary>
    public string SeedFile { get; set; } = "seed-output.json";

    /// <summary>
    /// Customer refund requests at or above this order total need the item back first (per currency).
    /// Below it, sending a cheap item back costs more than it is worth, so the shop refunds without a return.
    /// </summary>
    public Dictionary<string, decimal> ReturnRequiredFrom { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Hosted public demo: the sign-in page offers one-click sign-in as the demo accounts (no password), and chat is
    /// rate limited per client address. Sandbox money only. Off by default; never enable it on a shop with real customers.
    /// </summary>
    public bool PublicDemo { get; set; }

    /// <summary>Public demo only: chat messages allowed per client address per minute (both assistants together).</summary>
    public int PublicDemoChatPerMinute { get; set; } = 10;

    /// <summary>
    /// Hosted public demo: approving a refund or reminder runs every check against PayPal but skips the PayPal write,
    /// so the shared sandbox data stays the same for every visitor (status NotSent). Off by default.
    /// </summary>
    public bool DryRunApprovals { get; set; }

    public bool HasDemoPassword => !string.IsNullOrWhiteSpace(DemoPassword);
}
