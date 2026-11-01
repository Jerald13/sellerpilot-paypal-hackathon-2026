using Microsoft.EntityFrameworkCore;
using SellerPilot.Api.Audit;
using SellerPilot.Api.Data;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;

namespace SellerPilot.Api.Shop;

public interface IReconciliationService
{
    /// <summary>Compares the shop's books with live PayPal data and stores the run with its findings.</summary>
    /// <param name="trigger">"seller" or "agent", for the audit trail.</param>
    Task<ReconciliationRun> RunAsync(string trigger, CancellationToken ct);
}

/// <summary>
/// "Do the books match PayPal?" Every rule is plain code with exact decimal arithmetic; the assistant only explains the result.
/// Per order: booked payments vs completed captures, booked refunds vs PayPal refunds (by ID), and what the customer paid
/// vs the order total. Then PayPal payments that no order accounts for, via Transaction Search.
/// </summary>
public sealed class ReconciliationService(
    AppDbContext db,
    IPayPalReadClient paypal,
    IAuditService audit,
    TimeProvider time,
    ILogger<ReconciliationService> logger) : IReconciliationService
{
    // Transaction Search covers at most 31 days per request.
    private const int MaxSearchDays = 30;
    private const int MaxSearchPages = 5;

    public async Task<ReconciliationRun> RunAsync(string trigger, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var run = new ReconciliationRun
        {
            StartedAt = now.UtcDateTime,
            PeriodStart = now.AddDays(-MaxSearchDays).UtcDateTime,
            PeriodEnd = now.UtcDateTime,
            Trigger = trigger,
        };

        var orders = await db.ShopOrders.AsNoTracking()
            .Include(o => o.LedgerEntries)
            .Where(o => o.PaymentMethod == PaymentMethod.PayPal && o.PayPalOrderId != null)
            .OrderBy(o => o.OrderNumber)
            .ToListAsync(ct);

        var findings = new List<ReconciliationFinding>();
        try
        {
            foreach (var order in orders)
            {
                var orderFindings = await CheckOrderAsync(order, ct);
                findings.AddRange(orderFindings);
                if (orderFindings.Count == 0)
                {
                    run.OrdersMatched++;
                }
            }

            run.OrdersChecked = orders.Count;
            var (unknown, checkedPayments, note) = await FindUnknownPaymentsAsync(now, ct);
            findings.AddRange(unknown);
            run.PayPalPaymentsChecked = checkedPayments;
            run.Note = note;
            run.Status = ReconciliationStatus.Completed;
        }
        catch (Exception ex) when (ex is PayPalApiException or PayPalConfigurationException)
        {
            logger.LogWarning(ex, "Reconciliation run could not read PayPal");
            run.Status = ReconciliationStatus.Failed;
            run.Error = ex is PayPalApiException { IsNoResponse: true }
                ? "PayPal did not respond, so the books could not be checked. Try again in a moment."
                : $"PayPal returned an error, so the books could not be checked: {ex.Message}";
            findings.Clear();
        }

        run.Findings = findings.OrderBy(f => f.Severity).ThenBy(f => f.OrderNumber).ToList();
        run.IssuesFound = run.Findings.Count;
        run.FinishedAt = time.GetUtcNow().UtcDateTime;
        db.ReconciliationRuns.Add(run);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(trigger == "agent" ? AuditActors.Agent : AuditActors.Seller, AuditEvents.ReconciliationRun, new
        {
            runId = run.Id,
            run.Status,
            run.OrdersChecked,
            run.OrdersMatched,
            run.IssuesFound,
            kinds = run.Findings.Select(f => f.Kind).Distinct(),
        }, ct);
        return run;
    }

    private async Task<List<ReconciliationFinding>> CheckOrderAsync(ShopOrder order, CancellationToken ct)
    {
        var findings = new List<ReconciliationFinding>();
        PayPalOrder paypalOrder;
        try
        {
            paypalOrder = await paypal.GetOrderAsync(order.PayPalOrderId!, ct);
        }
        catch (PayPalApiException ex) when (ex.IsNotFound)
        {
            findings.Add(Finding(order, FindingKind.PayPalOrderMissing, FindingSeverity.High,
                $"The books link {order.OrderNumber} to PayPal order {order.PayPalOrderId}, but PayPal has no such order.",
                "Check the order number and the PayPal order ID; the payment may have been made under another order."));
            return findings;
        }

        var currency = order.Currency;
        var captures = paypalOrder.PurchaseUnits?.SelectMany(u => u.Payments?.Captures ?? []).ToList() ?? [];
        var paypalRefunds = paypalOrder.PurchaseUnits?.SelectMany(u => u.Payments?.Refunds ?? [])
            .Where(r => r.Status is RefundStatuses.Completed or RefundStatuses.Pending)
            .ToList() ?? [];

        var otherCurrency = captures.Select(c => c.Amount?.CurrencyCode).FirstOrDefault(c => c is not null && !string.Equals(c, currency, StringComparison.OrdinalIgnoreCase));
        if (otherCurrency is not null)
        {
            findings.Add(Finding(order, FindingKind.PaymentMismatch, FindingSeverity.High,
                $"{order.OrderNumber} is priced in {currency}, but PayPal captured the payment in {otherCurrency}.",
                "Check which currency the customer was charged in before refunding anything."));
            return findings;
        }

        var completed = captures.Where(c => c.Status is CaptureStatuses.Completed or CaptureStatuses.PartiallyRefunded or CaptureStatuses.Refunded).ToList();
        var captured = completed.Sum(Amount);
        var pending = captures.FirstOrDefault(c => c.Status == CaptureStatuses.Pending);
        var paypalRefunded = paypalRefunds.Sum(r => Amount(r.Amount));

        var bookedPayments = order.LedgerEntries.Where(e => e.Type == LedgerEntryType.Payment).ToList();
        var bookedRefunds = order.LedgerEntries.Where(e => e.Type == LedgerEntryType.Refund).ToList();
        var booksPaid = bookedPayments.Sum(e => e.Amount);

        // 1. Payment: books vs PayPal.
        if (booksPaid > 0 && captured == 0)
        {
            var shipped = order.Status is ShopOrderStatus.Shipped or ShopOrderStatus.Delivered;
            var reason = pending?.StatusDetails?.Reason is { Length: > 0 } r ? $" ({r})" : "";
            findings.Add(Finding(order, FindingKind.PaymentNotCompleted, shipped ? FindingSeverity.High : FindingSeverity.Medium,
                pending is not null
                    ? $"The books record {Money.Display(booksPaid, currency)} received for {order.OrderNumber}, but PayPal still shows the payment as PENDING{reason}."
                      + (shipped ? " The order was already shipped, so the goods left before the money arrived." : "")
                    : $"The books record {Money.Display(booksPaid, currency)} received for {order.OrderNumber}, but PayPal has no completed payment for it.",
                pending is not null
                    ? "Don't ship anything else to this customer until the payment completes. Accept or resolve the pending payment in the PayPal account (often a receiving-currency setting)."
                    : "Find out how the customer actually paid before treating this order as paid.",
                transactionId: pending?.Id, books: booksPaid, paypalAmount: 0m, difference: booksPaid));
        }
        else if (captured > 0 && booksPaid == 0)
        {
            findings.Add(Finding(order, FindingKind.PaymentNotInBooks, FindingSeverity.Medium,
                $"PayPal captured {Money.Display(captured, currency)} for {order.OrderNumber}, but the books have no payment for it.",
                "Record the payment in the books (books correction).",
                transactionId: completed.FirstOrDefault()?.Id, books: 0m, paypalAmount: captured, difference: captured));
        }
        else if (captured > 0 && booksPaid != captured)
        {
            findings.Add(Finding(order, FindingKind.PaymentMismatch, FindingSeverity.High,
                $"The books record {Money.Display(booksPaid, currency)} received for {order.OrderNumber}, but PayPal captured {Money.Display(captured, currency)}.",
                "Compare the PayPal payment with the shop's checkout record and correct the books.",
                transactionId: completed.FirstOrDefault()?.Id, books: booksPaid, paypalAmount: captured, difference: captured - booksPaid));
        }

        // 2. Refunds, matched by PayPal refund ID.
        var bookedRefundIds = bookedRefunds.Where(e => e.PayPalTransactionId is not null).Select(e => e.PayPalTransactionId!).ToHashSet();
        foreach (var refund in paypalRefunds.Where(r => !bookedRefundIds.Contains(r.Id)))
        {
            var amount = Amount(refund.Amount);
            findings.Add(Finding(order, FindingKind.RefundNotInBooks, FindingSeverity.Medium,
                $"PayPal shows refund {refund.Id} of {Money.Display(amount, currency)} for {order.OrderNumber}"
                + (PayPalDates.Parse(refund.CreateTime) is { } at ? $" (made {at:yyyy-MM-dd})" : "")
                + ", but the books don't have it. It was probably made directly in the PayPal dashboard.",
                $"Record refund {refund.Id} in the books (books correction).",
                transactionId: refund.Id, books: 0m, paypalAmount: amount, difference: amount));
        }

        var paypalRefundIds = paypalRefunds.Select(r => r.Id).ToHashSet();
        foreach (var entry in bookedRefunds.Where(e => e.PayPalTransactionId is null || !paypalRefundIds.Contains(e.PayPalTransactionId)))
        {
            findings.Add(Finding(order, FindingKind.RefundNotInPayPal, FindingSeverity.High,
                $"The books record a refund of {Money.Display(entry.Amount, currency)} for {order.OrderNumber}"
                + (entry.PayPalTransactionId is { } id ? $" (PayPal ID {id})" : "")
                + ", but PayPal has no such refund. The customer may never have received this money.",
                "Check the refund in PayPal; if it never happened, refund the customer or correct the books.",
                transactionId: entry.PayPalTransactionId, books: entry.Amount, paypalAmount: 0m, difference: entry.Amount));
        }

        // 3. What the customer paid vs what the order says they should pay.
        if (captured > 0)
        {
            var netPaid = captured - paypalRefunded;
            if (netPaid > order.Total)
            {
                var over = netPaid - order.Total;
                var discount = order.Discount > 0 ? $" ({Money.Display(order.Subtotal, currency)} minus {Money.Display(order.Discount, currency)} {order.DiscountCode ?? "discount"})" : "";
                findings.Add(Finding(order, FindingKind.Overcharged, FindingSeverity.Medium,
                    $"The order total for {order.OrderNumber} is {Money.Display(order.Total, currency)}{discount}, but the customer paid {Money.Display(netPaid, currency)}"
                    + (paypalRefunded > 0 ? " after refunds" : "") + $": overcharged by {Money.Display(over, currency)}."
                    + (order.Discount > 0 ? " The discount was probably not applied at checkout." : ""),
                    $"Refund the difference of {Money.Display(over, currency)} to the customer (needs the seller's approval).",
                    transactionId: completed.FirstOrDefault()?.Id, books: order.Total, paypalAmount: netPaid, difference: over));
            }
            else if (captured < order.Total)
            {
                findings.Add(Finding(order, FindingKind.Undercharged, FindingSeverity.Low,
                    $"PayPal captured {Money.Display(captured, currency)} for {order.OrderNumber}, less than the order total of {Money.Display(order.Total, currency)}.",
                    "Check whether a discount or partial payment explains the difference.",
                    transactionId: completed.FirstOrDefault()?.Id, books: order.Total, paypalAmount: captured, difference: order.Total - captured));
            }
        }

        return findings;
    }

    private async Task<(List<ReconciliationFinding> Findings, int Checked, string? Note)> FindUnknownPaymentsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var known = (await db.ShopOrders.AsNoTracking().Where(o => o.PayPalCaptureId != null).Select(o => o.PayPalCaptureId!).ToListAsync(ct))
            .Concat(await db.LedgerEntries.AsNoTracking().Where(e => e.PayPalTransactionId != null).Select(e => e.PayPalTransactionId!).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var findings = new List<ReconciliationFinding>();
        var payments = 0;
        string? refreshed = null;
        try
        {
            for (var page = 1; page <= MaxSearchPages; page++)
            {
                var response = await paypal.SearchTransactionsAsync(new TransactionSearchQuery(now.AddDays(-MaxSearchDays), now, Page: page), ct);
                refreshed ??= response.LastRefreshedDatetime;
                foreach (var detail in response.TransactionDetails ?? [])
                {
                    var info = detail.TransactionInfo;
                    if (info?.TransactionId is not { } id
                        || info.TransactionEventCode?.StartsWith("T00", StringComparison.Ordinal) != true
                        || info.TransactionStatus is not ("S" or "P")
                        || info.TransactionAmount is null
                        || !info.TransactionAmount.TryGetAmount(out var amount)
                        || amount <= 0)
                    {
                        continue;
                    }

                    payments++;
                    if (known.Contains(id))
                    {
                        continue;
                    }

                    var currency = info.TransactionAmount.CurrencyCode ?? "";
                    var date = PayPalDates.Parse(info.TransactionInitiationDate);
                    findings.Add(new ReconciliationFinding
                    {
                        Kind = FindingKind.UnknownPayment,
                        Severity = FindingSeverity.Medium,
                        PayPalTransactionId = id,
                        Currency = currency,
                        BooksAmount = 0m,
                        PayPalAmount = amount,
                        Difference = amount,
                        // The payer name is customer-controlled text: it stays out of the message the assistant reads.
                        Message = $"PayPal received {Money.Display(amount, currency)}"
                                  + (date is { } d ? $" on {d:yyyy-MM-dd}" : "")
                                  + $" (transaction {id}), but no shop order accounts for it.",
                        SuggestedFix = "Find the order this payment belongs to and add it to the books, or refund the customer if it was paid by mistake.",
                    });
                }

                if (response.TotalPages is null || page >= response.TotalPages)
                {
                    break;
                }
            }
        }
        catch (PayPalApiException ex) when (!ex.IsNoResponse)
        {
            // Orders were checked; only the "unlinked payments" part is missing, so the run still completes.
            logger.LogWarning(ex, "Transaction Search failed during reconciliation");
            return (findings, payments, "Payments without a shop order could not be checked: Transaction Search is unavailable for this sandbox app.");
        }

        var note = "Payments without a shop order are found with PayPal Transaction Search, which can lag a few hours behind."
                   + (PayPalDates.Parse(refreshed) is { } at ? $" PayPal data last refreshed {at.UtcDateTime:yyyy-MM-dd HH:mm} UTC." : "");
        return (findings, payments, note);
    }

    private static decimal Amount(PayPalCapture capture) => Amount(capture.Amount);

    private static decimal Amount(PayPalMoney? money) => money is not null && money.TryGetAmount(out var value) ? value : 0m;

    private static ReconciliationFinding Finding(
        ShopOrder order,
        FindingKind kind,
        FindingSeverity severity,
        string message,
        string? fix,
        string? transactionId = null,
        decimal? books = null,
        decimal? paypalAmount = null,
        decimal? difference = null) => new()
    {
        Kind = kind,
        Severity = severity,
        ShopOrderId = order.Id,
        OrderNumber = order.OrderNumber,
        PayPalOrderId = order.PayPalOrderId,
        PayPalTransactionId = transactionId,
        Currency = order.Currency,
        BooksAmount = books,
        PayPalAmount = paypalAmount,
        Difference = difference,
        Message = message,
        SuggestedFix = fix,
    };
}
