using Microsoft.Extensions.Options;
using SellerPilot.Api.Configuration;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;

namespace SellerPilot.Api.Guardrails;

public sealed record GuardrailDecision(bool Allowed, IReadOnlyList<string> Reasons)
{
    public static readonly GuardrailDecision Allow = new(true, []);

    public static GuardrailDecision Block(params string[] reasons) => new(false, reasons);
}

public sealed record RefundRecord(string RefundId, string Status, decimal Amount, DateTimeOffset? CreatedAt);

/// <summary>Everything needed to decide whether a refund on one capture is allowed, loaded fresh from PayPal.</summary>
public sealed record CaptureRefundState(
    string CaptureId,
    string? OrderId,
    string CaptureStatus,
    string Currency,
    decimal CapturedAmount,
    decimal RefundedAmount,
    bool RefundHistoryKnown,
    DateTimeOffset? CapturedAt,
    IReadOnlyList<RefundRecord> Refunds,
    PayPalOrder? Order)
{
    public decimal RemainingRefundable =>
        CaptureStatus == CaptureStatuses.Refunded ? 0m : Math.Max(0m, CapturedAmount - RefundedAmount);
}

public interface IRefundStateLoader
{
    /// <summary>Loads the capture and its order's refund history. Throws <see cref="PayPalApiException"/> if the capture does not exist.</summary>
    Task<CaptureRefundState> LoadAsync(string captureId, CancellationToken ct);
}

public sealed class RefundStateLoader(IPayPalReadClient paypal, ILogger<RefundStateLoader> logger) : IRefundStateLoader
{
    public async Task<CaptureRefundState> LoadAsync(string captureId, CancellationToken ct)
    {
        var capture = await paypal.GetCaptureAsync(captureId, ct);
        PayPalOrder? order = null;
        if (capture.RelatedOrderId is { } orderId)
        {
            try
            {
                order = await paypal.GetOrderAsync(orderId, ct);
            }
            catch (PayPalApiException ex) when (ex.IsNotFound)
            {
                logger.LogWarning("Order {OrderId} for capture {CaptureId} was not found; refund history unknown", orderId, captureId);
            }
        }

        return Build(capture, order);
    }

    public static CaptureRefundState Build(PayPalCapture capture, PayPalOrder? order)
    {
        var currency = capture.Amount?.CurrencyCode ?? "";
        // An unreadable amount becomes 0, which blocks any refund (fail closed).
        var captured = capture.Amount is not null && capture.Amount.TryGetAmount(out var value) ? value : 0m;
        var status = capture.Status ?? "UNKNOWN";

        var units = order?.PurchaseUnits ?? [];
        var captures = units.SelectMany(u => u.Payments?.Captures ?? []).ToList();
        var onlyCapture = captures.Count == 1 && captures[0].Id == capture.Id;
        var refunds = units
            .SelectMany(u => u.Payments?.Refunds ?? [])
            .Where(r => r.CaptureId == capture.Id || (r.CaptureId is null && onlyCapture))
            .Select(r => new RefundRecord(
                r.Id,
                r.Status ?? "UNKNOWN",
                r.Amount is not null && r.Amount.TryGetAmount(out var amount) ? amount : 0m,
                PayPalDates.Parse(r.CreateTime)))
            .ToList();

        var refunded = refunds
            .Where(r => r.Status is RefundStatuses.Completed or RefundStatuses.Pending)
            .Sum(r => r.Amount);

        // Without the order we only know the history when PayPal says nothing was refunded yet.
        var historyKnown = order is not null || status is CaptureStatuses.Completed or CaptureStatuses.Refunded;

        return new CaptureRefundState(
            capture.Id,
            capture.RelatedOrderId,
            status,
            currency,
            captured,
            refunded,
            historyKnown,
            PayPalDates.Parse(capture.CreateTime),
            refunds,
            order);
    }
}

public interface IRefundPolicy
{
    /// <param name="awaitingApprovalAmount">Sum of other refund proposals for this capture that are still waiting for approval.</param>
    GuardrailDecision Evaluate(CaptureRefundState state, decimal amount, string? requestedCurrency, decimal awaitingApprovalAmount);
}

/// <summary>
/// Hard refund limits, enforced in code (not by the prompt). Runs when the agent proposes a refund
/// and again when the seller approves it, because the capture may have changed in between.
/// </summary>
public sealed class RefundPolicy(IOptions<GuardrailOptions> options, TimeProvider time) : IRefundPolicy
{
    public GuardrailDecision Evaluate(CaptureRefundState state, decimal amount, string? requestedCurrency, decimal awaitingApprovalAmount)
    {
        var reasons = new List<string>();
        var currency = state.Currency;

        switch (state.CaptureStatus)
        {
            case CaptureStatuses.Completed:
            case CaptureStatuses.PartiallyRefunded:
                break;
            case CaptureStatuses.Refunded:
                var last = state.Refunds.Where(r => r.CreatedAt is not null).MaxBy(r => r.CreatedAt);
                reasons.Add(last?.CreatedAt is { } at
                    ? $"Capture {state.CaptureId} was already fully refunded (last refund {last.RefundId} on {at:yyyy-MM-dd})."
                    : $"Capture {state.CaptureId} was already fully refunded.");
                break;
            case CaptureStatuses.Pending:
                reasons.Add("The payment is still pending at PayPal, so it cannot be refunded yet.");
                break;
            case CaptureStatuses.Declined:
            case CaptureStatuses.Failed:
                reasons.Add("No money was captured for this payment, so there is nothing to refund.");
                break;
            default:
                reasons.Add($"A capture with status {state.CaptureStatus} cannot be refunded.");
                break;
        }

        if (!Money.IsCurrencyCode(currency) || state.CapturedAmount <= 0)
        {
            reasons.Add("PayPal returned no valid captured amount for this payment.");
            return GuardrailDecision.Block([.. reasons]);
        }

        if (!state.RefundHistoryKnown)
        {
            reasons.Add("The refund history of this payment could not be loaded, so the remaining refundable amount is unknown.");
        }

        if (requestedCurrency is not null && !string.Equals(requestedCurrency, currency, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"Currency mismatch: the payment was captured in {currency}, not {requestedCurrency.ToUpperInvariant()}.");
        }

        if (amount <= 0)
        {
            reasons.Add("The refund amount must be greater than zero.");
        }
        else if (!Money.HasValidPrecision(amount, currency))
        {
            reasons.Add($"{currency} amounts can have at most {Money.Decimals(currency)} decimal places.");
        }

        var remaining = state.RemainingRefundable;
        if (amount > remaining)
        {
            reasons.Add(
                $"{Money.Display(amount, currency)} is more than the remaining refundable amount of {Money.Display(remaining, currency)} " +
                $"(captured {Money.Display(state.CapturedAmount, currency)}, already refunded {Money.Display(state.RefundedAmount, currency)}).");
        }
        else if (awaitingApprovalAmount > 0 && amount > remaining - awaitingApprovalAmount)
        {
            reasons.Add(
                $"Refund proposals for {Money.Display(awaitingApprovalAmount, currency)} on this payment are already waiting for approval; " +
                $"at most {Money.Display(Math.Max(0, remaining - awaitingApprovalAmount), currency)} more can be proposed.");
        }

        var limits = options.Value.MaxRefundAmount;
        if (!limits.TryGetValue(currency, out var max))
        {
            reasons.Add($"No refund limit is configured for {currency}, so SellerPilot will not refund in this currency.");
        }
        else if (amount > max)
        {
            reasons.Add($"{Money.Display(amount, currency)} is above the per-refund limit of {Money.Display(max, currency)}.");
        }

        var windowDays = options.Value.RefundWindowDays;
        if (state.CapturedAt is { } capturedAt && time.GetUtcNow() - capturedAt > TimeSpan.FromDays(windowDays))
        {
            reasons.Add($"The payment was captured on {capturedAt:yyyy-MM-dd}, more than {windowDays} days ago; PayPal no longer accepts refunds for it.");
        }

        return reasons.Count == 0 ? GuardrailDecision.Allow : GuardrailDecision.Block([.. reasons]);
    }
}
