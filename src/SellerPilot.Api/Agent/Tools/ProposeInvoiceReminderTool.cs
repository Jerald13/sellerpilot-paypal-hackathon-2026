using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SellerPilot.Api.Audit;
using SellerPilot.Api.Configuration;
using SellerPilot.Api.Data;
using SellerPilot.Api.Guardrails;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;

namespace SellerPilot.Api.Agent.Tools;

/// <summary>Write tool: creates a PendingAction only; nothing is sent to the customer until the seller approves.</summary>
public sealed class ProposeInvoiceReminderTool(
    IPayPalReadClient paypal,
    AppDbContext db,
    IAuditService audit,
    IOptions<GuardrailOptions> guardrails,
    TimeProvider time) : IAgentTool
{
    public string Name => "ProposeInvoiceReminder";

    public string Description =>
        "Propose sending a payment reminder email for one unpaid invoice. This does NOT send anything: the seller must approve it first. " +
        "Write a short, polite note to the customer that mentions the invoice number and the amount due. Make one call per invoice.";

    public JsonElement InputSchema { get; } = ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "invoice_id": { "type": "string", "description": "PayPal invoice ID from ListUnpaidInvoices, e.g. INV2-Z56S-5LLA-Q52L-CPZ5." },
            "note": { "type": "string", "description": "The reminder message to the customer: polite, short, max 1000 characters." },
            "subject": { "type": "string", "description": "Email subject, max 150 characters. Optional." },
            "reason": { "type": "string", "description": "One sentence for the seller explaining why the reminder is proposed. Optional." }
          },
          "required": ["invoice_id", "note"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(ToolArguments args, ToolContext context, CancellationToken ct)
    {
        var invoiceId = args.RequiredInvoiceId("invoice_id");
        var note = args.RequiredString("note", 1000);
        var subject = args.OptionalString("subject", 150);
        var reason = args.OptionalString("reason", 500) ?? "The invoice is still unpaid.";

        PayPalInvoice invoice;
        try
        {
            invoice = await paypal.GetInvoiceAsync(invoiceId, ct);
        }
        catch (PayPalApiException ex) when (ex.IsNotFound)
        {
            return ToolResult.Error($"No invoice exists with ID {invoiceId}. Use ListUnpaidInvoices to get invoice IDs.");
        }

        var decision = InvoiceReminderPolicy.CheckStatus(invoice);
        if (decision.Allowed && await SentRecentlyAsync(invoiceId, ct))
        {
            decision = GuardrailDecision.Block(
                $"A reminder for this invoice was already sent in the last {guardrails.Value.ReminderCooldownHours} hours.");
        }

        if (!decision.Allowed)
        {
            await audit.LogAsync(AuditActors.Agent, AuditEvents.GuardrailBlocked, new
            {
                tool = Name,
                invoiceId,
                status = invoice.Status,
                reasons = decision.Reasons,
                conversationId = context.ConversationId,
            }, ct);
            return ToolResult.Error("SellerPilot blocked this reminder proposal. Explain the reasons to the seller.", decision.Reasons);
        }

        var existing = await db.PendingActions.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Type == ActionType.InvoiceReminder && a.TargetId == invoiceId && a.Status == ActionStatus.Pending, ct);
        if (existing is not null)
        {
            return ToolResult.Ok(new
            {
                Status = "already_awaiting_seller_approval",
                ActionId = existing.Id,
                InvoiceId = invoiceId,
                Note = "A reminder for this invoice is already waiting for the seller's approval; no new proposal was created.",
            });
        }

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var due = PayPalViews.Money(invoice.DueAmount) ?? PayPalViews.Money(invoice.Amount);
        var daysOverdue = PayPalViews.DaysOverdue(invoice, today);
        var number = invoice.Detail?.InvoiceNumber ?? invoiceId;
        var customer = invoice.PrimaryRecipients?.FirstOrDefault()?.BillingInfo?.Name?.DisplayName;

        var action = new PendingAction
        {
            Id = Guid.NewGuid(),
            Type = ActionType.InvoiceReminder,
            TargetId = invoiceId,
            Amount = due?.Amount,
            Currency = due?.Currency,
            Reason = reason,
            Summary = $"Send a payment reminder for invoice #{number}"
                      + (due is null ? "" : $" ({Money.Display(due.Amount, due.Currency)} due")
                      + (due is null ? "" : daysOverdue > 0 ? $", {daysOverdue} days overdue)" : ")")
                      + (customer is null ? "" : $" to {(customer.Length > 60 ? customer[..60] + "…" : customer)}"),
            Payload = JsonSerializer.Serialize(new ReminderPayload(subject, note, number, customer, invoice.Detail?.PaymentTerm?.DueDate, daysOverdue), ActionPayloads.Json),
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
            invoiceId,
            invoiceNumber = number,
            amountDue = due?.Amount,
            currency = due?.Currency,
            reason,
            conversationId = context.ConversationId,
        }, ct);

        return ToolResult.Ok(new
        {
            Status = "awaiting_seller_approval",
            ActionId = action.Id,
            InvoiceId = invoiceId,
            InvoiceNumber = number,
            AmountDue = due,
            Note = "Nothing has been sent yet. The seller must click Approve on the proposal card.",
        });
    }

    private async Task<bool> SentRecentlyAsync(string invoiceId, CancellationToken ct)
    {
        var since = time.GetUtcNow().UtcDateTime.AddHours(-guardrails.Value.ReminderCooldownHours);
        return await db.PendingActions.AsNoTracking()
            .AnyAsync(a => a.Type == ActionType.InvoiceReminder && a.TargetId == invoiceId && a.Status == ActionStatus.Executed && a.DecidedAt >= since, ct);
    }
}
