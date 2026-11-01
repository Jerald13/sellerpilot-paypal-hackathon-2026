using System.Net;
using System.Text.Json;
using SellerPilot.Api.Guardrails;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;

namespace SellerPilot.Api.Agent.Tools;

public sealed class SearchTransactionsTool(IPayPalReadClient paypal, IUntrustedTextSanitizer sanitizer, TimeProvider time) : IAgentTool
{
    private const int MaxResults = 25;
    private const int MaxPages = 3;

    public string Name => "SearchTransactions";

    public string Description =>
        "Search the shop's recent PayPal transactions (payments and refunds), optionally filtered by customer name or email, " +
        "by the shop's own order reference, or by transaction ID. Use it to find a customer's payments when no order ID is known. " +
        "Note: PayPal's transaction search can lag a few hours behind; for a known order ID use GetOrder instead.";

    public JsonElement InputSchema { get; } = ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "customer": { "type": "string", "description": "Part of the customer's name or email, e.g. \"Amy\". Optional." },
            "reference": { "type": "string", "description": "The shop's own order/invoice reference, if the seller mentions one. Optional." },
            "transaction_id": { "type": "string", "description": "A PayPal transaction ID (same as a capture ID). Optional." },
            "days": { "type": "integer", "description": "How many days back to search, 1-31. Default 30." }
          }
        }
        """);

    public async Task<ToolResult> ExecuteAsync(ToolArguments args, ToolContext context, CancellationToken ct)
    {
        var customer = args.OptionalString("customer", 100);
        var reference = args.OptionalString("reference", 64);
        var transactionIdRaw = args.OptionalString("transaction_id", 64);
        string? transactionId = null;
        if (transactionIdRaw is not null && !PayPalIds.TryNormalizeResourceId(transactionIdRaw, out transactionId))
        {
            throw new ToolArgumentException("'transaction_id' is not a valid PayPal transaction ID.");
        }

        var days = args.OptionalInt("days", 30, 1, 31);
        var end = time.GetUtcNow();
        var start = end.AddDays(-days);

        var matches = new List<PayPalTransactionDetail>();
        var scanned = 0;
        try
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                var response = await paypal.SearchTransactionsAsync(new TransactionSearchQuery(start, end, transactionId, page), ct);
                var details = response.TransactionDetails ?? [];
                scanned += details.Count;
                matches.AddRange(details.Where(d => Matches(d, customer, reference)));
                if (response.TotalPages is null || page >= response.TotalPages)
                {
                    break;
                }
            }
        }
        catch (PayPalApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            return ToolResult.Error(
                "Transaction Search is not enabled for this PayPal app. Use GetOrder with an order ID instead, " +
                "and tell the seller to enable 'Transaction search' in the PayPal developer dashboard.");
        }

        var newestFirst = matches
            .OrderByDescending(d => PayPalDates.Parse(d.TransactionInfo?.TransactionInitiationDate))
            .Take(MaxResults)
            .Select(d => PayPalViews.Transaction(d, sanitizer, context.SecurityFlags))
            .ToList();

        return ToolResult.Ok(new
        {
            SearchedFrom = start.UtcDateTime.ToString("yyyy-MM-dd"),
            SearchedTo = end.UtcDateTime.ToString("yyyy-MM-dd"),
            TransactionsScanned = scanned,
            Matches = matches.Count,
            Transactions = newestFirst,
            Note = "Transaction search data can lag behind PayPal by a few hours. Capture IDs here can be passed to GetOrder.",
        });
    }

    private static bool Matches(PayPalTransactionDetail detail, string? customer, string? reference)
    {
        if (customer is not null)
        {
            var name = detail.PayerInfo?.PayerName?.DisplayName ?? "";
            var email = detail.PayerInfo?.EmailAddress ?? "";
            if (!name.Contains(customer, StringComparison.OrdinalIgnoreCase) && !email.Contains(customer, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (reference is not null)
        {
            var info = detail.TransactionInfo;
            if (!string.Equals(info?.InvoiceId, reference, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(info?.CustomField, reference, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
