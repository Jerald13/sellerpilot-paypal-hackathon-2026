using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SellerPilot.Api.Agent.Tools;
using SellerPilot.Api.Audit;
using SellerPilot.Api.Data;
using SellerPilot.Api.Guardrails;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;
using SellerPilot.Api.Shop;

namespace SellerPilot.Api.Approvals;

public enum DecisionOutcome
{
    /// <summary>The decision was applied; see the action's status (Executed, Failed or Rejected).</summary>
    Done,
    NotFound,

    /// <summary>The action was no longer Pending (already decided, or another click won the race).</summary>
    Conflict,
}

public sealed record DecisionResult(DecisionOutcome Outcome, PendingAction? Action, string? Message = null);

public interface IApprovalService
{
    Task<DecisionResult> ApproveAsync(Guid actionId, CancellationToken ct);

    Task<DecisionResult> RejectAsync(Guid actionId, CancellationToken ct);

    /// <summary>The customer's item came back: AwaitingReturn -> Pending, so the refund can be approved.</summary>
    Task<DecisionResult> MarkReturnReceivedAsync(Guid actionId, CancellationToken ct);
}

/// <summary>
/// The only code path that calls PayPal write endpoints, and only after a seller's click.
/// Claim (Pending -> Approved, atomically) -> re-validate against live PayPal data -> execute with
/// PayPal-Request-Id = action ID -> record Executed or Failed -> audit every step.
/// </summary>
public sealed class ApprovalService(
    AppDbContext db,
    IPayPalReadClient paypalRead,
    IPayPalWriteClient paypalWrite,
    IRefundStateLoader refundStateLoader,
    IRefundPolicy refundPolicy,
    IAuditService audit,
    ILedgerService ledger,
    TimeProvider time,
    ILogger<ApprovalService> logger) : IApprovalService
{
    public async Task<DecisionResult> ApproveAsync(Guid actionId, CancellationToken ct)
    {
        var claim = await ClaimAsync(actionId, [ActionStatus.Pending], ActionStatus.Approved, ct);
        if (claim.Outcome != DecisionOutcome.Done)
        {
            return claim.Action?.Status == ActionStatus.AwaitingReturn
                ? claim with { Message = "This refund is waiting for the customer's return. Mark the return as received first." }
                : claim;
        }

        // Once claimed, the approval always runs to the end (PayPal calls have their own timeouts):
        // a browser disconnect must not leave the action stuck in Approved.
        var done = CancellationToken.None;
        var action = claim.Action!;
        await audit.LogAsync(AuditActors.Seller, AuditEvents.ActionApproved, new { actionId, type = action.Type, action.TargetId, action.Amount, action.Currency }, done);

        try
        {
            var check = await RevalidateAsync(action, done);
            if (!check.Allowed)
            {
                await audit.LogAsync(AuditActors.System, AuditEvents.GuardrailBlocked, new { actionId, stage = "approval", reasons = check.Reasons }, done);
                return await FinishAsync(action, ActionStatus.Failed, null, "Blocked at approval: " + string.Join(" ", check.Reasons));
            }

            switch (action.Type)
            {
                case ActionType.Refund:
                    var refund = await paypalWrite.RefundCaptureAsync(
                        action.TargetId,
                        new PayPalRefundRequest(PayPalMoney.From(action.Amount!.Value, action.Currency!), CustomId: action.Id.ToString()),
                        requestId: action.Id.ToString(),
                        done);
                    if (refund.Status is not (RefundStatuses.Completed or RefundStatuses.Pending))
                    {
                        return await FinishAsync(action, ActionStatus.Failed, refund.Id, $"PayPal returned refund status {refund.Status}.");
                    }

                    var executed = await FinishAsync(action, ActionStatus.Executed, refund.Id, refund.Status == RefundStatuses.Pending ? "PayPal accepted the refund; it is still pending." : null);
                    await PostRefundToBooksAsync(action, refund);
                    return executed;

                case ActionType.InvoiceReminder:
                    var payload = JsonSerializer.Deserialize<ReminderPayload>(action.Payload ?? "{}", ActionPayloads.Json);
                    if (payload is null || string.IsNullOrWhiteSpace(payload.Note))
                    {
                        return await FinishAsync(action, ActionStatus.Failed, null, "The reminder proposal has no message.");
                    }

                    await paypalWrite.SendInvoiceReminderAsync(
                        action.TargetId,
                        new PayPalInvoiceReminderRequest(payload.Subject, payload.Note),
                        requestId: action.Id.ToString(),
                        done);
                    return await FinishAsync(action, ActionStatus.Executed, null, null);

                case ActionType.BooksCorrection:
                    return await ExecuteBooksCorrectionAsync(action, done);

                default:
                    return await FinishAsync(action, ActionStatus.Failed, null, $"Unsupported action type {action.Type}.");
            }
        }
        catch (PayPalApiException ex)
        {
            logger.LogWarning(ex, "Executing action {ActionId} failed", actionId);
            // No answer, or a success answer we couldn't read: PayPal may have executed it.
            var unknown = ex.IsNoResponse || ex.ErrorName == "INVALID_RESPONSE";
            var error = unknown
                ? "PayPal's answer was not received, so the outcome is unknown. Check the order in PayPal before proposing this again."
                : ex.Message;
            return await FinishAsync(action, ActionStatus.Failed, null, error);
        }
        catch (PayPalConfigurationException ex)
        {
            return await FinishAsync(action, ActionStatus.Failed, null, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Unexpected error while executing action {ActionId}", actionId);
            return await FinishAsync(action, ActionStatus.Failed, null, "Unexpected error while executing; check PayPal before proposing this again.");
        }
    }

    public async Task<DecisionResult> RejectAsync(Guid actionId, CancellationToken ct)
    {
        var claim = await ClaimAsync(actionId, [ActionStatus.Pending, ActionStatus.AwaitingReturn], ActionStatus.Rejected, ct);
        if (claim.Outcome == DecisionOutcome.Done)
        {
            await audit.LogAsync(AuditActors.Seller, AuditEvents.ActionRejected, new { actionId, type = claim.Action!.Type, claim.Action.TargetId }, ct);
        }

        return claim;
    }

    public async Task<DecisionResult> MarkReturnReceivedAsync(Guid actionId, CancellationToken ct)
    {
        // Not a decision yet: DecidedAt stays empty until the refund itself is approved or rejected.
        var updated = await db.PendingActions
            .Where(a => a.Id == actionId && a.Status == ActionStatus.AwaitingReturn)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, ActionStatus.Pending), ct);
        var action = await db.PendingActions.FirstOrDefaultAsync(a => a.Id == actionId, ct);
        if (action is null)
        {
            return new DecisionResult(DecisionOutcome.NotFound, null, $"Action {actionId} does not exist.");
        }

        await db.Entry(action).ReloadAsync(ct);
        if (updated == 0)
        {
            return new DecisionResult(DecisionOutcome.Conflict, action, $"Action {actionId} is {action.Status}, not waiting for a return.");
        }

        await audit.LogAsync(AuditActors.Seller, AuditEvents.ReturnReceived, new { actionId, action.TargetId, action.ShopOrderId }, ct);
        return new DecisionResult(DecisionOutcome.Done, action);
    }

    /// <summary>Atomic from -> newStatus transition; a second click (or a parallel request) gets Conflict.</summary>
    private async Task<DecisionResult> ClaimAsync(Guid actionId, ActionStatus[] from, ActionStatus newStatus, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var updated = await db.PendingActions
            .Where(a => a.Id == actionId && from.Contains(a.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, newStatus).SetProperty(a => a.DecidedAt, now), ct);

        var action = await db.PendingActions.FirstOrDefaultAsync(a => a.Id == actionId, ct);
        if (action is null)
        {
            return new DecisionResult(DecisionOutcome.NotFound, null, $"Action {actionId} does not exist.");
        }

        // ExecuteUpdate bypasses the change tracker; make sure we see the row as it is now.
        await db.Entry(action).ReloadAsync(ct);
        if (updated == 0)
        {
            return new DecisionResult(DecisionOutcome.Conflict, action, $"Action {actionId} is already {action.Status}.");
        }

        return new DecisionResult(DecisionOutcome.Done, action);
    }

    /// <summary>The world may have changed since the proposal (e.g. refunded in the PayPal dashboard), so check again.</summary>
    private async Task<GuardrailDecision> RevalidateAsync(PendingAction action, CancellationToken ct)
    {
        switch (action.Type)
        {
            case ActionType.Refund:
                if (action.Amount is not { } amount || action.Currency is null)
                {
                    return GuardrailDecision.Block("The refund proposal has no amount.");
                }

                CaptureRefundState state;
                try
                {
                    state = await refundStateLoader.LoadAsync(action.TargetId, ct);
                }
                catch (PayPalApiException ex) when (ex.IsNotFound)
                {
                    return GuardrailDecision.Block($"Capture {action.TargetId} no longer exists at PayPal.");
                }

                // Only live PayPal state counts here; other proposals are re-checked when they are approved.
                return refundPolicy.Evaluate(state, amount, action.Currency, awaitingApprovalAmount: 0m);

            case ActionType.InvoiceReminder:
                PayPalInvoice invoice;
                try
                {
                    invoice = await paypalRead.GetInvoiceAsync(action.TargetId, ct);
                }
                catch (PayPalApiException ex) when (ex.IsNotFound)
                {
                    return GuardrailDecision.Block($"Invoice {action.TargetId} no longer exists at PayPal.");
                }

                return InvoiceReminderPolicy.CheckStatus(invoice);

            case ActionType.BooksCorrection:
                // Checked in ExecuteBooksCorrectionAsync against live PayPal data, right before the books change.
                return GuardrailDecision.Allow;

            default:
                return GuardrailDecision.Block($"Unsupported action type {action.Type}.");
        }
    }

    /// <summary>Copies a PayPal payment or refund into the books. Uses the amount PayPal reports now, never the proposal's.</summary>
    private async Task<DecisionResult> ExecuteBooksCorrectionAsync(PendingAction action, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<BooksCorrectionPayload>(action.Payload ?? "{}", ActionPayloads.Json);
        if (payload is null || string.IsNullOrWhiteSpace(payload.OrderNumber))
        {
            return await FinishAsync(action, ActionStatus.Failed, null, "The books correction has no order number.");
        }

        var check = await BooksCorrections.CheckAsync(db, paypalRead, payload.OrderNumber, action.TargetId, ct);
        if (check.Error is not null || check.Amount != action.Amount)
        {
            var reason = check.Error ?? $"PayPal now reports {Money.Display(check.Amount, action.Currency ?? "")} instead of {Money.Display(action.Amount ?? 0m, action.Currency ?? "")}.";
            await audit.LogAsync(AuditActors.System, AuditEvents.GuardrailBlocked, new { actionId = action.Id, stage = "approval", reasons = new[] { reason } }, ct);
            return await FinishAsync(action, ActionStatus.Failed, null, "Blocked at approval: " + reason);
        }

        var entry = await ledger.RecordAsync(check.Order!, check.EntryType, check.Amount, check.Order!.Currency, action.TargetId, LedgerSources.Correction, $"Books correction {action.Id}", ct);
        var result = await FinishAsync(action, ActionStatus.Executed, null, null);
        await audit.LogAsync(AuditActors.System, AuditEvents.LedgerPosted, new { actionId = action.Id, entry.ShopOrderId, type = entry.Type, entry.Amount, entry.Currency, paypalId = action.TargetId }, ct);
        return result;
    }

    /// <summary>
    /// Keeps the shop's books in step with PayPal. The refund already happened, so a failure here is logged
    /// and audited (reconciliation will flag it) but never turns the executed action into a failure.
    /// </summary>
    private async Task PostRefundToBooksAsync(PendingAction action, PayPalRefund refund)
    {
        var amount = refund.Amount is not null && refund.Amount.TryGetAmount(out var refunded) ? refunded : action.Amount!.Value;
        try
        {
            var entry = await ledger.RecordRefundAsync(
                action.TargetId, action.ShopOrderId, refund.Id, amount, action.Currency!, LedgerSources.SellerPilot, $"Approved action {action.Id}", CancellationToken.None);
            if (entry is not null)
            {
                await audit.LogAsync(AuditActors.System, AuditEvents.LedgerPosted, new { actionId = action.Id, entry.ShopOrderId, type = entry.Type, entry.Amount, entry.Currency, refundId = refund.Id }, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Refund {RefundId} for action {ActionId} could not be posted to the books", refund.Id, action.Id);
            await audit.LogAsync(AuditActors.System, AuditEvents.LedgerPostingFailed, new { actionId = action.Id, refundId = refund.Id, error = ex.GetType().Name }, CancellationToken.None);
        }
    }

    private async Task<DecisionResult> FinishAsync(PendingAction action, ActionStatus status, string? paypalResultId, string? error)
    {
        action.Status = status;
        action.PayPalResultId = paypalResultId;
        action.Error = error;
        action.DecidedAt = time.GetUtcNow().UtcDateTime;

        // CancellationToken.None: once PayPal has (maybe) moved money, the record must be saved even if the client disconnected.
        await db.SaveChangesAsync(CancellationToken.None);
        await audit.LogAsync(
            AuditActors.System,
            status == ActionStatus.Executed ? AuditEvents.ActionExecuted : AuditEvents.ActionFailed,
            new { actionId = action.Id, type = action.Type, action.TargetId, action.Amount, action.Currency, paypalResultId, error },
            CancellationToken.None);
        return new DecisionResult(DecisionOutcome.Done, action);
    }
}
