using SellerPilot.Api.Guardrails;
using SellerPilot.Api.PayPal.Models;

namespace SellerPilot.Api.Agent.Tools;

/// <summary>
/// Turns PayPal payloads into what the LLM sees: only the fields it needs, emails masked, no addresses,
/// and every customer-written text field passed through the injection sanitizer.
/// </summary>
public static class PayPalViews
{
    public sealed record MoneyView(decimal Amount, string Currency);

    public static MoneyView? Money(PayPalMoney? money) =>
        money is not null && money.TryGetAmount(out var amount) && money.CurrencyCode is { } currency
            ? new MoneyView(amount, currency)
            : null;

    public static string? Time(string? value) => PayPalDates.Parse(value)?.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'");

    public static string? CustomerName(PayPalOrder order) =>
        order.Payer?.Name?.FullName
        ?? order.PaymentSource?.Paypal?.Name?.FullName
        ?? order.PaymentSource?.Card?.Name;

    public static object Order(PayPalOrder order, IUntrustedTextSanitizer sanitizer, SecurityFlagCollector flags)
    {
        var source = $"order {order.Id}";
        var units = order.PurchaseUnits ?? [];
        var captureStates = units
            .SelectMany(u => u.Payments?.Captures ?? [])
            .Select(c => RefundStateLoader.Build(c, order))
            .ToList();

        return new
        {
            OrderId = order.Id,
            order.Status,
            Created = Time(order.CreateTime),
            Customer = new
            {
                Name = sanitizer.Sanitize(CustomerName(order), $"{source} customer name", flags),
                Email = Pii.MaskEmail(order.Payer?.EmailAddress ?? order.PaymentSource?.Paypal?.EmailAddress),
                Country = order.Payer?.Address?.CountryCode ?? order.PaymentSource?.Paypal?.Address?.CountryCode,
                PaidWith = order.PaymentSource?.Card is { } card ? $"card {card.Brand} ending {card.LastDigits}" : order.PaymentSource?.Paypal is not null ? "PayPal" : null,
            },
            PurchaseUnits = units.Select(u => new
            {
                Reference = sanitizer.Sanitize(u.InvoiceId ?? u.CustomId, $"{source} reference", flags),
                Description = sanitizer.Sanitize(u.Description, $"{source} description", flags),
                Total = Money(u.Amount),
                Items = u.Items?.Select(i => new
                {
                    Name = sanitizer.Sanitize(i.Name, $"{source} item name", flags),
                    i.Quantity,
                    UnitPrice = Money(i.UnitAmount),
                }).ToList(),
            }).ToList(), // Eager on purpose: the sanitizer must run (and raise flags) now, not during serialization.
            Captures = captureStates.Select(s => new
            {
                s.CaptureId,
                s.CaptureStatus,
                Captured = new MoneyView(s.CapturedAmount, s.Currency),
                CapturedAt = s.CapturedAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'"),
                AlreadyRefunded = new MoneyView(s.RefundedAmount, s.Currency),
                RemainingRefundable = new MoneyView(s.RemainingRefundable, s.Currency),
            }).ToList(),
            Refunds = captureStates.SelectMany(s => s.Refunds.Select(r => new
            {
                r.RefundId,
                ForCapture = s.CaptureId,
                r.Status,
                Refunded = new MoneyView(r.Amount, s.Currency),
                RefundedAt = r.CreatedAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'"),
            })).ToList(),
            FullyRefunded = captureStates.Count > 0 && captureStates.All(s => s.RemainingRefundable == 0 && s.RefundedAmount > 0),
        };
    }

    public static object Invoice(PayPalInvoice invoice, DateOnly today, IUntrustedTextSanitizer sanitizer, SecurityFlagCollector flags)
    {
        var source = $"invoice {invoice.Id}";
        var recipient = invoice.PrimaryRecipients?.FirstOrDefault()?.BillingInfo;
        var dueDate = PayPalDates.ParseDate(invoice.Detail?.PaymentTerm?.DueDate);
        var daysOverdue = dueDate is { } due && due < today ? today.DayNumber - due.DayNumber : 0;

        return new
        {
            InvoiceId = invoice.Id,
            invoice.Detail?.InvoiceNumber,
            invoice.Status,
            Customer = new
            {
                Name = sanitizer.Sanitize(recipient?.Name?.DisplayName, $"{source} customer name", flags),
                Email = Pii.MaskEmail(recipient?.EmailAddress),
            },
            AmountDue = Money(invoice.DueAmount) ?? Money(invoice.Amount),
            Total = Money(invoice.Amount),
            invoice.Detail?.InvoiceDate,
            DueDate = invoice.Detail?.PaymentTerm?.DueDate,
            DaysOverdue = daysOverdue,
            Overdue = daysOverdue > 0,
            LastSent = Time(invoice.Detail?.Metadata?.LastSentTime),
            Note = sanitizer.Sanitize(invoice.Detail?.Note, $"{source} note", flags),
        };
    }

    public static int DaysOverdue(PayPalInvoice invoice, DateOnly today) =>
        PayPalDates.ParseDate(invoice.Detail?.PaymentTerm?.DueDate) is { } due && due < today ? today.DayNumber - due.DayNumber : 0;

    public static object Transaction(PayPalTransactionDetail detail, IUntrustedTextSanitizer sanitizer, SecurityFlagCollector flags)
    {
        var info = detail.TransactionInfo;
        var source = $"transaction {info?.TransactionId}";
        return new
        {
            info?.TransactionId,
            Date = Time(info?.TransactionInitiationDate),
            Type = DescribeEventCode(info?.TransactionEventCode),
            Status = DescribeStatus(info?.TransactionStatus),
            Amount = Money(info?.TransactionAmount),
            Fee = Money(info?.FeeAmount),
            Customer = new
            {
                Name = sanitizer.Sanitize(detail.PayerInfo?.PayerName?.DisplayName, $"{source} customer name", flags),
                Email = Pii.MaskEmail(detail.PayerInfo?.EmailAddress),
                Country = detail.PayerInfo?.CountryCode,
            },
            Reference = sanitizer.Sanitize(info?.InvoiceId ?? info?.CustomField, $"{source} reference", flags),
            Subject = sanitizer.Sanitize(info?.TransactionSubject, $"{source} subject", flags),
            Note = sanitizer.Sanitize(info?.TransactionNote, $"{source} note", flags),
            Items = detail.CartInfo?.ItemDetails?
                .Select(i => sanitizer.Sanitize(i.ItemName, $"{source} item name", flags))
                .Where(n => n is not null)
                .ToList(),
        };
    }

    private static string DescribeEventCode(string? code) => code switch
    {
        null => "unknown",
        "T1107" => "refund",
        "T1106" => "payment reversal",
        "T1201" => "chargeback",
        _ when code.StartsWith("T00", StringComparison.Ordinal) => "payment",
        _ when code.StartsWith("T11", StringComparison.Ordinal) => "reversal",
        _ when code.StartsWith("T04", StringComparison.Ordinal) => "withdrawal",
        _ => $"other ({code})",
    };

    private static string DescribeStatus(string? status) => status switch
    {
        "S" => "completed",
        "P" => "pending",
        "D" => "denied",
        "V" => "reversed",
        null => "unknown",
        _ => status,
    };
}
