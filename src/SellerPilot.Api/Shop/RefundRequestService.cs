using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SellerPilot.Api.Agent.Tools;
using SellerPilot.Api.Audit;
using SellerPilot.Api.Configuration;
using SellerPilot.Api.Data;
using SellerPilot.Api.Guardrails;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;

namespace SellerPilot.Api.Shop;

/// <summary>What the shop can offer a customer for one order, decided in code from the books, the shop policy and live PayPal data.</summary>
public sealed record RefundEligibility(
    string OrderNumber,
    bool Eligible,
    string Currency,
    decimal MaxRefundable,
    decimal? OfferedAmount,
    bool ReturnRequired,
    DateOnly? ReturnWindowEnds,
    IReadOnlyList<string> Reasons);

public sealed record RefundRequestResult(PendingAction? Action, string? Error, bool NotFound = false);

public interface IRefundRequestService
{
    /// <summary>Null when the order does not exist on this customer's account.</summary>
    Task<RefundEligibility?> CheckAsync(int customerId, string orderNumber, decimal? requestedAmount, CancellationToken ct);

    /// <summary>
    /// Creates a refund request for the seller to approve (status AwaitingReturn when the item must come back first).
    /// Re-checks everything; nothing from the client or the assistant is trusted.
    /// </summary>
    Task<RefundRequestResult> CreateAsync(int customerId, string orderNumber, decimal amount, string? reason, CancellationToken ct);
}

public sealed class RefundRequestService(
    AppDbContext db,
    IRefundStateLoader stateLoader,
    IRefundPolicy policy,
    IUntrustedTextSanitizer sanitizer,
    IAuditService audit,
    IOptions<ShopOptions> options,
    TimeProvider time) : IRefundRequestService
{
    public async Task<RefundEligibility?> CheckAsync(int customerId, string orderNumber, decimal? requestedAmount, CancellationToken ct)
    {
        var order = await LoadOrderAsync(customerId, orderNumber, ct);
        return order is null ? null : (await EvaluateAsync(order, requestedAmount, ct)).Eligibility;
    }

    public async Task<RefundRequestResult> CreateAsync(int customerId, string orderNumber, decimal amount, string? reason, CancellationToken ct)
    {
        var order = await LoadOrderAsync(customerId, orderNumber, ct);
        if (order is null)
        {
            return new RefundRequestResult(null, $"There is no order {ShopViews.NormalizeOrderNumber(orderNumber)} on your account.", NotFound: true);
        }

        var (eligibility, state) = await EvaluateAsync(order, amount, ct);
        if (!eligibility.Eligible || state is null)
        {
            return new RefundRequestResult(null, string.Join(" ", eligibility.Reasons));
        }

        if (amount != eligibility.OfferedAmount)
        {
            return new RefundRequestResult(null, $"The most that can be refunded for {order.OrderNumber} is {Money.Display(eligibility.MaxRefundable, order.Currency)}.");
        }

        var flags = new SecurityFlagCollector();
        var customerReason = sanitizer.Sanitize(reason?.Trim() is { Length: > 0 } r ? (r.Length > 300 ? r[..300] : r) : null, $"refund request for {order.OrderNumber}", flags);
        var customer = order.Customer!;
        var isFull = amount == state.RemainingRefundable;
        var action = new PendingAction
        {
            Id = Guid.NewGuid(),
            Type = ActionType.Refund,
            TargetId = state.CaptureId,
            Amount = amount,
            Currency = order.Currency,
            Reason = "Customer request: " + (customerReason ?? "no reason given"),
            Summary = $"{(isFull ? "Full" : "Partial")} refund of {Money.Display(amount, order.Currency)} to {customer.Name} for order {order.OrderNumber} "
                      + $"(requested by the customer; captured {Money.Display(state.CapturedAmount, order.Currency)}"
                      + (state.RefundedAmount > 0 ? $", already refunded {Money.Display(state.RefundedAmount, order.Currency)}" : "")
                      + (eligibility.ReturnRequired ? "; the item must be returned first)" : ")"),
            Payload = JsonSerializer.Serialize(new RefundPayload(
                order.PayPalOrderId, customer.Name, state.CapturedAmount, state.RefundedAmount, state.RemainingRefundable, flags.Flags,
                ReturnRequired: eligibility.ReturnRequired, OrderNumber: order.OrderNumber), ActionPayloads.Json),
            Status = eligibility.ReturnRequired ? ActionStatus.AwaitingReturn : ActionStatus.Pending,
            CreatedAt = time.GetUtcNow().UtcDateTime,
            RequestedBy = ActionRequesters.Customer,
            CustomerId = customer.Id,
            ShopOrderId = order.Id,
        };
        db.PendingActions.Add(action);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(AuditActors.Customer, AuditEvents.RefundRequested, new
        {
            actionId = action.Id,
            customerId = customer.Id,
            orderNumber = order.OrderNumber,
            amount,
            currency = order.Currency,
            returnRequired = eligibility.ReturnRequired,
            securityFlags = flags.Flags.Count,
        }, ct);
        if (flags.Flags.Count > 0)
        {
            await audit.LogAsync(AuditActors.System, AuditEvents.InjectionSuspected, new { actionId = action.Id, flags = flags.Flags }, ct);
        }

        return new RefundRequestResult(action, null);
    }

    private Task<ShopOrder?> LoadOrderAsync(int customerId, string orderNumber, CancellationToken ct)
    {
        var normalized = ShopViews.NormalizeOrderNumber(orderNumber);
        return db.ShopOrders
            .Include(o => o.Customer)
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.OrderNumber == normalized && o.CustomerId == customerId, ct);
    }

    private async Task<(RefundEligibility Eligibility, CaptureRefundState? State)> EvaluateAsync(ShopOrder order, decimal? requestedAmount, CancellationToken ct)
    {
        var reasons = new List<string>();
        var currency = order.Currency;
        var windowDays = order.Lines.Count > 0 ? order.Lines.Min(l => l.ReturnWindowDays) : 0;
        DateOnly? windowEnds = order.DeliveredAt is { } delivered && windowDays > 0
            ? DateOnly.FromDateTime(delivered).AddDays(windowDays)
            : null;
        var returnRequired = options.Value.ReturnRequiredFrom.TryGetValue(currency, out var threshold) && order.Total >= threshold;

        RefundEligibility No(decimal max = 0m) => new(order.OrderNumber, false, currency, max, null, returnRequired, windowEnds, reasons);

        if (order.Status is ShopOrderStatus.AwaitingPayment or ShopOrderStatus.Cancelled)
        {
            reasons.Add(order.Status == ShopOrderStatus.AwaitingPayment
                ? $"Order {order.OrderNumber} hasn't been paid yet, so there is nothing to refund."
                : $"Order {order.OrderNumber} was cancelled.");
            return (No(), null);
        }

        var finalSale = order.Lines.FirstOrDefault(l => l.ReturnWindowDays <= 0);
        if (finalSale is not null)
        {
            reasons.Add($"{finalSale.Name} was a final-sale item, so it can't be returned or refunded through the shop.");
            return (No(), null);
        }

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        if (windowEnds is { } ends && today > ends)
        {
            reasons.Add($"The {windowDays}-day return window for order {order.OrderNumber} ended on {ends:yyyy-MM-dd}.");
            return (No(), null);
        }

        if (order.PaymentMethod != PaymentMethod.PayPal || order.PayPalCaptureId is null)
        {
            reasons.Add($"Order {order.OrderNumber} was paid by {(order.PaymentMethod == PaymentMethod.BankTransfer ? "bank transfer" : "another method")}; refunds for it are handled by the shop directly.");
            return (No(), null);
        }

        var open = await db.PendingActions.AsNoTracking()
            .Where(a => a.Type == ActionType.Refund && a.TargetId == order.PayPalCaptureId && (a.Status == ActionStatus.Pending || a.Status == ActionStatus.AwaitingReturn))
            .Select(a => new { a.Amount, a.RequestedBy, a.CustomerId })
            .ToListAsync(ct);
        if (open.Any(a => a.RequestedBy == ActionRequesters.Customer && a.CustomerId == order.CustomerId))
        {
            reasons.Add($"There is already a refund request for order {order.OrderNumber} waiting for the shop.");
            return (No(), null);
        }

        CaptureRefundState state;
        try
        {
            state = await stateLoader.LoadAsync(order.PayPalCaptureId, ct);
        }
        catch (PayPalApiException ex) when (ex.IsNotFound)
        {
            reasons.Add("The payment for this order could not be found at PayPal; the shop will need to look into it.");
            return (No(), null);
        }

        var awaiting = open.Sum(a => a.Amount ?? 0m);
        var max = Math.Max(0m, state.RemainingRefundable - awaiting);
        var offer = Math.Min(requestedAmount ?? max, max);
        var decision = policy.Evaluate(state, offer > 0 ? offer : max, currency, awaiting);
        if (!decision.Allowed || offer <= 0)
        {
            reasons.AddRange(decision.Reasons);
            if (reasons.Count == 0)
            {
                reasons.Add($"Nothing is left to refund on order {order.OrderNumber}.");
            }

            return (No(max), state);
        }

        if (requestedAmount is { } asked && asked > max)
        {
            reasons.Add($"{Money.Display(asked, currency)} is more than can be refunded; the most for this order is {Money.Display(max, currency)}"
                        + (state.RefundedAmount > 0 ? $" because {Money.Display(state.RefundedAmount, currency)} was already refunded." : "."));
        }

        if (returnRequired)
        {
            reasons.Add($"Orders of {Money.Display(threshold, currency)} or more need the item back first; the refund is approved when the return arrives.");
        }

        return (new RefundEligibility(order.OrderNumber, true, currency, max, offer, returnRequired, windowEnds, reasons), state);
    }
}
