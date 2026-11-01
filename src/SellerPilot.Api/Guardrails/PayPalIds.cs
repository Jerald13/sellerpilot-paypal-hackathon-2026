using System.Text.RegularExpressions;

namespace SellerPilot.Api.Guardrails;

/// <summary>
/// Validates IDs coming from the LLM before they are used in a PayPal URL.
/// Rejects anything that is not plain letters/digits, so a hallucinated or malicious value
/// (e.g. "../oauth2/token" or "X?foo=bar") never reaches PayPal.
/// </summary>
public static partial class PayPalIds
{
    // Orders, captures, refunds and transactions: e.g. 5O190127TN364715T (17 chars in practice).
    [GeneratedRegex("^[A-Z0-9]{10,30}$")]
    private static partial Regex ResourceIdPattern();

    // Invoices: e.g. INV2-Z56S-5LLA-Q52L-CPZ5.
    [GeneratedRegex("^INV2-[A-Z0-9-]{8,40}$")]
    private static partial Regex InvoiceIdPattern();

    public static bool TryNormalizeResourceId(string? raw, out string id) =>
        TryNormalize(raw, ResourceIdPattern(), out id);

    public static bool TryNormalizeInvoiceId(string? raw, out string id) =>
        TryNormalize(raw, InvoiceIdPattern(), out id);

    private static bool TryNormalize(string? raw, Regex pattern, out string id)
    {
        id = (raw ?? "").Trim().TrimStart('#').ToUpperInvariant();
        return pattern.IsMatch(id);
    }
}
