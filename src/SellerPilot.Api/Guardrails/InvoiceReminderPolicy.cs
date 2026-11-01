using SellerPilot.Api.PayPal.Models;

namespace SellerPilot.Api.Guardrails;

public static class InvoiceReminderPolicy
{
    /// <summary>Checked when the reminder is proposed and again right before it is sent.</summary>
    public static GuardrailDecision CheckStatus(PayPalInvoice invoice)
    {
        var label = invoice.Detail?.InvoiceNumber is { Length: > 0 } number ? $"Invoice #{number}" : $"Invoice {invoice.Id}";
        return invoice.Status switch
        {
            InvoiceStatuses.Sent or InvoiceStatuses.Unpaid or InvoiceStatuses.PartiallyPaid => GuardrailDecision.Allow,
            InvoiceStatuses.Paid or InvoiceStatuses.MarkedAsPaid => GuardrailDecision.Block($"{label} is already paid."),
            InvoiceStatuses.PaymentPending => GuardrailDecision.Block($"A payment for {label} is already being processed."),
            InvoiceStatuses.Draft => GuardrailDecision.Block($"{label} is still a draft and was never sent to the customer."),
            InvoiceStatuses.Scheduled => GuardrailDecision.Block($"{label} is scheduled and has not been sent yet."),
            InvoiceStatuses.Cancelled => GuardrailDecision.Block($"{label} was cancelled."),
            _ => GuardrailDecision.Block($"{label} has status {invoice.Status ?? "UNKNOWN"}, which does not allow reminders."),
        };
    }
}
