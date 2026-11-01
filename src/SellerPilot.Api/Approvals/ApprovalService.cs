using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SellerPilot.Api.Agent.Tools;
using SellerPilot.Api.Audit;
using SellerPilot.Api.Data;
using SellerPilot.Api.Guardrails;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;

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
    TimeProvider time,
    ILogger<ApprovalService> logger) : IApprovalService
{
    public async Task<DecisionResult> ApproveAsync(Guid actionId, CancellationToken ct)
    {
        var claim = await ClaimAsync(actionId, ActionStatus.Approved, ct);
        if (claim.Outcome != DecisionOutcome.Done)
        {
            return claim;
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
                    return refund.Status is RefundStatuses.Completed or RefundStatuses.Pending
                        ? await FinishAsync(action, ActionStatus.Executed, refund.Id, refund.Status == RefundStatuses.Pending ? "PayPal accepted the refund; it is still pending." : null)
                        : await FinishAsync(action, ActionStatus.Failed, refund.Id, $"PayPal returned refund status {refund.Status}.");

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
        var claim = await ClaimAsync(actionId, ActionStatus.Rejected, ct);
        if (claim.Outcome == DecisionOutcome.Done)
        {
            await audit.LogAsync(AuditActors.Seller, AuditEvents.ActionRejected, new { actionId, type = claim.Action!.Type, claim.Action.TargetId }, ct);
        }

        return claim;
    }

    /// <summary>Atomic Pending -> newStatus transition; a second click (or a parallel request) gets Conflict.</summary>
    private async Task<DecisionResult> ClaimAsync(Guid actionId, ActionStatus newStatus, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var updated = await db.PendingActions
            .Where(a => a.Id == actionId && a.Status == ActionStatus.Pending)
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

            default:
                return GuardrailDecision.Block($"Unsupported action type {action.Type}.");
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
