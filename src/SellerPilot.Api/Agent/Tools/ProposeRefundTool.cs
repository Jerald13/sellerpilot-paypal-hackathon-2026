using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SellerPilot.Api.Audit;
using SellerPilot.Api.Data;
using SellerPilot.Api.Guardrails;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;

namespace SellerPilot.Api.Agent.Tools;

/// <summary>
/// Write tool: creates a PendingAction only. It has no access to <see cref="IPayPalWriteClient"/>,
/// so it cannot refund anything; the seller's approval does that.
/// </summary>
public sealed class ProposeRefundTool(
    IPayPalReadClient paypal,
    IRefundStateLoader stateLoader,
    IRefundPolicy policy,
    IUntrustedTextSanitizer sanitizer,
    AppDbContext db,
    IAuditService audit,
    TimeProvider time) : IAgentTool
{
    public string Name => "ProposeRefund";

    public string Description =>
        "Propose a full or partial refund of a captured payment. This does NOT refund anything: it creates a proposal " +
        "that the seller must approve. SellerPilot checks the amount against what was captured, what was already refunded " +
        "and the shop's refund limit, and rejects proposals that break those rules. Get the capture_id from GetOrder first.";

    public JsonElement InputSchema { get; } = ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "capture_id": { "type": "string", "description": "The capture_id of the payment to refund (from GetOrder), not the order ID." },
            "amount": { "type": "number", "description": "Refund amount in the payment's currency, e.g. 20.00. Omit for a full refund of the remaining refundable amount." },
            "currency": { "type": "string", "description": "3-letter currency code of the payment, e.g. USD. Optional; it must match the payment." },
            "reason": { "type": "string", "description": "One or two sentences for the seller explaining why this refund is proposed." }
          },
          "required": ["capture_id", "reason"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(ToolArguments args, ToolContext context, CancellationToken ct)
    {
        var captureId = args.RequiredResourceId("capture_id");
        var requestedAmount = args.OptionalDecimal("amount", 0.01m, 1_000_000m);
        var currency = args.OptionalCurrency("currency");
        var reason = args.RequiredString("reason", 500);

        CaptureRefundState state;
        try
        {
            state = await stateLoader.LoadAsync(captureId, ct);
        }
        catch (PayPalApiException ex) when (ex.IsNotFound)
        {
            return await NotACaptureAsync(captureId, ct);
        }

        var awaitingApproval = (await db.PendingActions.AsNoTracking()
                .Where(a => a.Type == ActionType.Refund && a.TargetId == captureId && (a.Status == ActionStatus.Pending || a.Status == ActionStatus.AwaitingReturn))
                .Select(a => a.Amount)
                .ToListAsync(ct))
            .Sum(a => a ?? 0m);

        var amount = requestedAmount ?? Math.Max(0m, state.RemainingRefundable - awaitingApproval);
        var decision = policy.Evaluate(state, amount, currency, awaitingApproval);
        if (!decision.Allowed)
        {
            await audit.LogAsync(AuditActors.Agent, AuditEvents.GuardrailBlocked, new
            {
                tool = Name,
                captureId,
                orderId = state.OrderId,
                amount,
                currency = currency ?? state.Currency,
                reasons = decision.Reasons,
                conversationId = context.ConversationId,
            }, ct);
            return ToolResult.Error("SellerPilot blocked this refund proposal. Explain the reasons to the seller; do not retry with the same values.", decision.Reasons);
        }

        // Re-scan the order's customer-written text, so the approval card can warn about it.
        var warnings = new SecurityFlagCollector();
        if (state.Order is not null)
        {
            _ = PayPalViews.Order(state.Order, sanitizer, warnings);
        }

        foreach (var flag in warnings.Flags)
        {
            context.SecurityFlags.Add(flag);
        }

        var customer = state.Order is not null ? PayPalViews.CustomerName(state.Order) : null;
        var isFull = amount == state.RemainingRefundable;
        var action = new PendingAction
        {
            Id = Guid.NewGuid(),
            Type = ActionType.Refund,
            TargetId = captureId,
            Amount = amount,
            Currency = state.Currency,
            Reason = reason,
            Summary = $"{(isFull ? "Full" : "Partial")} refund of {Money.Display(amount, state.Currency)}"
                      + (customer is null ? "" : $" to {Truncate(customer, 60)}")
                      + $" (order {state.OrderId ?? "unknown"}, capture {captureId}; captured {Money.Display(state.CapturedAmount, state.Currency)}"
                      + (state.RefundedAmount > 0 ? $", already refunded {Money.Display(state.RefundedAmount, state.Currency)})" : ")"),
            Payload = JsonSerializer.Serialize(new RefundPayload(
                state.OrderId,
                customer,
                state.CapturedAmount,
                state.RefundedAmount,
                state.RemainingRefundable,
                warnings.Flags), ActionPayloads.Json),
            Status = ActionStatus.Pending,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };

        db.PendingActions.Add(action);
        await db.SaveChangesAsync(ct);
        context.ProposedActions.Add(action);
        await audit.LogAsync(AuditActors.Agent, AuditEvents.ActionProposed, new
        {
            actionId = action.Id,
            type = action.Type,
            captureId,
            orderId = state.OrderId,
            amount,
            currency = state.Currency,
            reason,
            securityFlags = warnings.Flags.Count,
            conversationId = context.ConversationId,
        }, ct);

        return ToolResult.Ok(new
        {
            Status = "awaiting_seller_approval",
            ActionId = action.Id,
            CaptureId = captureId,
            state.OrderId,
            Amount = new PayPalViews.MoneyView(amount, state.Currency),
            IsFullRefund = isFull,
            Note = "Nothing has been refunded yet. The seller must click Approve on the proposal card.",
        });
    }

    private async Task<ToolResult> NotACaptureAsync(string id, CancellationToken ct)
    {
        try
        {
            var order = await paypal.GetOrderAsync(id, ct);
            var captureIds = order.PurchaseUnits?.SelectMany(u => u.Payments?.Captures ?? []).Select(c => c.Id).ToList() ?? [];
            return ToolResult.Error(captureIds.Count > 0
                ? $"{id} is an order ID, not a capture ID. Call ProposeRefund again with capture_id {string.Join(" or ", captureIds)}."
                : $"{id} is an order ID, and the order has no captured payment, so there is nothing to refund.");
        }
        catch (PayPalApiException ex) when (ex.IsNotFound)
        {
            return ToolResult.Error($"No captured payment exists with ID {id}. Use GetOrder to find the capture_id.");
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
