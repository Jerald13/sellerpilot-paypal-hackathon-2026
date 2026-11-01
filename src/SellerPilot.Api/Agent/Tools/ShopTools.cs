using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SellerPilot.Api.Audit;
using SellerPilot.Api.Data;
using SellerPilot.Api.Guardrails;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;
using SellerPilot.Api.Shop;

namespace SellerPilot.Api.Agent.Tools;

/// <summary>Read tool over the shop's own records: find orders when the seller has no PayPal order ID.</summary>
public sealed class FindShopOrdersTool(AppDbContext db, IUntrustedTextSanitizer sanitizer) : IAgentTool
{
    public string Name => "FindShopOrders";

    public string Description =>
        "Search the shop's own order records (the books) by customer name, customer email, shop order number (e.g. SP-DVAN-1001), " +
        "item name or PayPal ID. Returns the shop order with its PayPal order ID and capture ID and the payments and refunds recorded " +
        "in the books. Use it when the seller doesn't give a PayPal order ID, then call GetOrder for the live PayPal status.";

    public JsonElement InputSchema { get; } = ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Customer name or email, shop order number, item name, or PayPal order/capture ID." },
            "limit": { "type": "integer", "description": "Maximum number of orders to return, 1-20. Default 10." }
          },
          "required": ["query"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(ToolArguments args, ToolContext context, CancellationToken ct)
    {
        var query = args.RequiredString("query", 100).ToLowerInvariant();
        var limit = args.OptionalInt("limit", 10, 1, 20);

        var orders = await db.ShopOrders.AsNoTracking()
            .Include(o => o.Customer)
            .Include(o => o.Lines)
            .Include(o => o.LedgerEntries)
            .Where(o => o.OrderNumber.ToLower().Contains(query)
                        || o.Customer!.Name.ToLower().Contains(query)
                        || o.Customer.Email.Contains(query)
                        || (o.PayPalOrderId != null && o.PayPalOrderId.ToLower() == query)
                        || (o.PayPalCaptureId != null && o.PayPalCaptureId.ToLower() == query)
                        || o.Lines.Any(l => l.Name.ToLower().Contains(query)))
            .OrderByDescending(o => o.PlacedAt)
            .Take(limit)
            .ToListAsync(ct);

        return ToolResult.Ok(new
        {
            Query = query,
            Found = orders.Count,
            Orders = orders.Select(o => ShopViews.Order(o, sanitizer, context.SecurityFlags)).ToList(),
            Note = orders.Count == 0 ? "No shop order matches. The payment may exist only in PayPal: try SearchTransactions." : null,
        });
    }
}

/// <summary>Runs the books-vs-PayPal reconciliation. Reads PayPal and records the run; moves no money.</summary>
public sealed class RunReconciliationTool(IReconciliationService reconciliation) : IAgentTool
{
    public string Name => "RunReconciliation";

    public string Description =>
        "Check whether the shop's books match PayPal: payments recorded vs captured, refunds recorded vs made, what customers paid vs " +
        "order totals, and PayPal payments with no shop order. Returns the findings, most severe first, each with a suggested fix. " +
        "Takes a few seconds because it reads every order from PayPal.";

    public JsonElement InputSchema { get; } = ToolSchema.Parse("""{ "type": "object", "properties": {} }""");

    public async Task<ToolResult> ExecuteAsync(ToolArguments args, ToolContext context, CancellationToken ct)
    {
        var run = await reconciliation.RunAsync("agent", ct);
        if (run.Status == ReconciliationStatus.Failed)
        {
            return ToolResult.Error(run.Error ?? "The reconciliation could not be completed.");
        }

        return ToolResult.Ok(new
        {
            RunId = run.Id,
            run.OrdersChecked,
            run.OrdersMatched,
            run.PayPalPaymentsChecked,
            run.IssuesFound,
            Findings = run.Findings.Take(20).Select(f => new
            {
                Kind = f.Kind.ToString(),
                Severity = f.Severity.ToString(),
                f.OrderNumber,
                f.PayPalOrderId,
                f.PayPalTransactionId,
                Difference = f.Difference is { } d && f.Currency is { } c ? new PayPalViews.MoneyView(d, c) : null,
                f.Message,
                f.SuggestedFix,
            }).ToList(),
            run.Note,
        });
    }
}

/// <summary>
/// Write tool: proposes copying a PayPal payment or refund that is missing from the books into the ledger.
/// Creates a PendingAction only; the books change after the seller approves. Never touches PayPal.
/// </summary>
public sealed class ProposeBooksCorrectionTool(
    AppDbContext db,
    IPayPalReadClient paypal,
    IAuditService audit,
    TimeProvider time) : IAgentTool
{
    public string Name => "ProposeBooksCorrection";

    public string Description =>
        "Propose recording a PayPal refund or payment that is missing from the shop's books (found by RunReconciliation). " +
        "This only fixes the shop's records, it moves no money, and it still needs the seller's approval. " +
        "SellerPilot copies the amount from PayPal itself; pass the shop order number and the PayPal refund or capture ID.";

    public JsonElement InputSchema { get; } = ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "order_number": { "type": "string", "description": "The shop order number, e.g. SP-DVAN-1005." },
            "paypal_transaction_id": { "type": "string", "description": "The PayPal refund ID or capture ID that is missing from the books." },
            "reason": { "type": "string", "description": "One sentence for the seller explaining the correction." }
          },
          "required": ["order_number", "paypal_transaction_id", "reason"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(ToolArguments args, ToolContext context, CancellationToken ct)
    {
        var orderNumber = ShopViews.NormalizeOrderNumber(args.RequiredString("order_number", 32));
        var transactionId = args.RequiredResourceId("paypal_transaction_id");
        var reason = args.RequiredString("reason", 500);

        var check = await BooksCorrections.CheckAsync(db, paypal, orderNumber, transactionId, ct);
        if (check.Error is not null)
        {
            await audit.LogAsync(AuditActors.Agent, AuditEvents.GuardrailBlocked, new { tool = Name, orderNumber, transactionId, reasons = new[] { check.Error }, conversationId = context.ConversationId }, ct);
            return ToolResult.Error("SellerPilot blocked this books correction.", [check.Error]);
        }

        var duplicate = await db.PendingActions.AnyAsync(a => a.Type == ActionType.BooksCorrection && a.TargetId == transactionId && a.Status == ActionStatus.Pending, ct);
        if (duplicate)
        {
            return ToolResult.Error($"A books correction for {transactionId} is already waiting for the seller's approval.");
        }

        var order = check.Order!;
        var action = new PendingAction
        {
            Id = Guid.NewGuid(),
            Type = ActionType.BooksCorrection,
            TargetId = transactionId,
            Amount = check.Amount,
            Currency = order.Currency,
            Reason = reason,
            Summary = $"Record PayPal {check.EntryType.ToString().ToLowerInvariant()} {transactionId} of {Money.Display(check.Amount, order.Currency)} "
                      + $"for order {order.OrderNumber} in the books (no money moves)",
            Payload = JsonSerializer.Serialize(new BooksCorrectionPayload(order.OrderNumber, check.EntryType.ToString(), order.PayPalOrderId, order.Customer?.Name), ActionPayloads.Json),
            Status = ActionStatus.Pending,
            CreatedAt = time.GetUtcNow().UtcDateTime,
            ShopOrderId = order.Id,
            CustomerId = order.CustomerId,
        };
        db.PendingActions.Add(action);
        await db.SaveChangesAsync(ct);
        context.ProposedActions.Add(action);
        await audit.LogAsync(AuditActors.Agent, AuditEvents.ActionProposed, new
        {
            actionId = action.Id,
            type = action.Type,
            orderNumber = order.OrderNumber,
            transactionId,
            amount = check.Amount,
            currency = order.Currency,
            reason,
            conversationId = context.ConversationId,
        }, ct);

        return ToolResult.Ok(new
        {
            Status = "awaiting_seller_approval",
            ActionId = action.Id,
            OrderNumber = order.OrderNumber,
            Records = new { Type = check.EntryType.ToString(), Amount = new PayPalViews.MoneyView(check.Amount, order.Currency), PayPalId = transactionId },
            Note = "The books are not changed yet. The seller must click Approve on the proposal card. No money moves either way.",
        });
    }
}

/// <summary>Validation shared by the proposal tool and the approval-time re-check.</summary>
public static class BooksCorrections
{
    public sealed record Check(ShopOrder? Order, LedgerEntryType EntryType, decimal Amount, string? Error);

    public static async Task<Check> CheckAsync(AppDbContext db, IPayPalReadClient paypal, string orderNumber, string transactionId, CancellationToken ct)
    {
        var order = await db.ShopOrders.Include(o => o.Customer).FirstOrDefaultAsync(o => o.OrderNumber == orderNumber, ct);
        if (order is null)
        {
            return Fail($"There is no shop order {orderNumber}.");
        }

        if (order.PayPalOrderId is null)
        {
            return Fail($"Order {orderNumber} was not paid through PayPal, so there is nothing to copy from PayPal.");
        }

        if (await db.LedgerEntries.AnyAsync(e => e.PayPalTransactionId == transactionId, ct))
        {
            return Fail($"{transactionId} is already recorded in the books.");
        }

        PayPalOrder paypalOrder;
        try
        {
            paypalOrder = await paypal.GetOrderAsync(order.PayPalOrderId, ct);
        }
        catch (PayPalApiException ex) when (ex.IsNotFound)
        {
            return Fail($"PayPal order {order.PayPalOrderId} no longer exists.");
        }

        var units = paypalOrder.PurchaseUnits ?? [];
        var refund = units.SelectMany(u => u.Payments?.Refunds ?? []).FirstOrDefault(r => r.Id == transactionId);
        if (refund is not null)
        {
            return refund.Status is RefundStatuses.Completed or RefundStatuses.Pending && TryAmount(refund.Amount, order.Currency, out var amount)
                ? new Check(order, LedgerEntryType.Refund, amount, null)
                : Fail($"Refund {transactionId} has status {refund.Status} or an unexpected currency, so it is not recorded.");
        }

        var capture = units.SelectMany(u => u.Payments?.Captures ?? []).FirstOrDefault(c => c.Id == transactionId);
        if (capture is not null)
        {
            return capture.Status is CaptureStatuses.Completed or CaptureStatuses.PartiallyRefunded or CaptureStatuses.Refunded && TryAmount(capture.Amount, order.Currency, out var amount)
                ? new Check(order, LedgerEntryType.Payment, amount, null)
                : Fail($"Payment {transactionId} has status {capture.Status}; only completed payments are recorded.");
        }

        return Fail($"{transactionId} is not a refund or payment of PayPal order {order.PayPalOrderId} (shop order {orderNumber}).");

        static Check Fail(string error) => new(null, default, 0m, error);
    }

    private static bool TryAmount(PayPalMoney? money, string currency, out decimal amount)
    {
        amount = 0m;
        return money is not null
               && string.Equals(money.CurrencyCode, currency, StringComparison.OrdinalIgnoreCase)
               && money.TryGetAmount(out amount)
               && amount > 0;
    }
}

/// <summary>What the assistants see of shop records: no full emails, customer-written text sanitized.</summary>
public static class ShopViews
{
    /// <summary>Order numbers are matched case-insensitively and without surrounding spaces or a leading '#'.</summary>
    public static string NormalizeOrderNumber(string value) => value.Trim().TrimStart('#').ToUpperInvariant();

    public static object Order(ShopOrder o, IUntrustedTextSanitizer sanitizer, SecurityFlagCollector flags)
    {
        var paid = o.LedgerEntries.Where(e => e.Type == LedgerEntryType.Payment).Sum(e => e.Amount);
        var refunded = o.LedgerEntries.Where(e => e.Type == LedgerEntryType.Refund).Sum(e => e.Amount);
        return new
        {
            o.OrderNumber,
            Customer = o.Customer is null ? null : new
            {
                Name = sanitizer.Sanitize(o.Customer.Name, $"shop order {o.OrderNumber} customer name", flags),
                Email = Pii.MaskEmail(o.Customer.Email),
                o.Customer.Country,
            },
            PlacedAt = o.PlacedAt.ToString("yyyy-MM-dd"),
            Status = o.Status.ToString(),
            DeliveredAt = o.DeliveredAt?.ToString("yyyy-MM-dd"),
            PaymentMethod = o.PaymentMethod.ToString(),
            Items = o.Lines.Select(l => new { l.Name, l.Quantity, UnitPrice = new PayPalViews.MoneyView(l.UnitPrice, o.Currency), ReturnWindowDays = l.ReturnWindowDays }).ToList(),
            Total = new PayPalViews.MoneyView(o.Total, o.Currency),
            Discount = o.Discount > 0 ? new { Code = o.DiscountCode, Amount = new PayPalViews.MoneyView(o.Discount, o.Currency) } : null,
            o.PayPalOrderId,
            o.PayPalCaptureId,
            Books = new
            {
                PaymentsRecorded = new PayPalViews.MoneyView(paid, o.Currency),
                RefundsRecorded = new PayPalViews.MoneyView(refunded, o.Currency),
            },
        };
    }
}
