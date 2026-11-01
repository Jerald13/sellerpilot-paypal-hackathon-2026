namespace SellerPilot.Api.PayPal.Models;

// Invoicing v2.

public static class InvoiceStatuses
{
    public const string Draft = "DRAFT";
    public const string Sent = "SENT";
    public const string Scheduled = "SCHEDULED";
    public const string Unpaid = "UNPAID";
    public const string PartiallyPaid = "PARTIALLY_PAID";
    public const string PaymentPending = "PAYMENT_PENDING";
    public const string Paid = "PAID";
    public const string MarkedAsPaid = "MARKED_AS_PAID";
    public const string Cancelled = "CANCELLED";

    /// <summary>Statuses where the customer still owes money and a reminder makes sense.</summary>
    public static readonly IReadOnlySet<string> Unsettled = new HashSet<string> { Sent, Unpaid, PartiallyPaid };
}

public sealed record PayPalInvoiceList(int? TotalItems, int? TotalPages, IReadOnlyList<PayPalInvoice>? Items);

public sealed record PayPalInvoice(
    string Id,
    string? Status,
    PayPalInvoiceDetail? Detail,
    PayPalInvoicer? Invoicer,
    IReadOnlyList<PayPalInvoiceRecipient>? PrimaryRecipients,
    PayPalMoney? Amount,
    PayPalMoney? DueAmount,
    IReadOnlyList<PayPalLink>? Links);

public sealed record PayPalInvoiceDetail(
    string? InvoiceNumber,
    string? Reference,
    string? InvoiceDate,
    string? CurrencyCode,
    string? Note,
    string? Memo,
    PayPalInvoicePaymentTerm? PaymentTerm,
    PayPalInvoiceMetadata? Metadata);

public sealed record PayPalInvoicePaymentTerm(string? TermType, string? DueDate);

public sealed record PayPalInvoiceMetadata(string? CreateTime, string? LastUpdateTime, string? FirstSentTime, string? LastSentTime);

public sealed record PayPalInvoicer(PayPalInvoiceName? Name, string? EmailAddress, string? BusinessName);

public sealed record PayPalInvoiceRecipient(PayPalInvoiceBillingInfo? BillingInfo);

public sealed record PayPalInvoiceBillingInfo(PayPalInvoiceName? Name, string? EmailAddress);

public sealed record PayPalInvoiceName(string? GivenName, string? Surname, string? FullName)
{
    public string? DisplayName =>
        !string.IsNullOrWhiteSpace(FullName)
            ? FullName
            : string.Join(' ', new[] { GivenName, Surname }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } name ? name : null;
}

/// <summary>Body of POST /v2/invoicing/invoices/{id}/remind.</summary>
public sealed record PayPalInvoiceReminderRequest(string? Subject, string Note, bool SendToInvoicer = false, bool SendToRecipient = true);
