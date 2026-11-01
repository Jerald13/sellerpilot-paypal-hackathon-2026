using System.Text.Json;
using SellerPilot.Api.Guardrails;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;

namespace SellerPilot.Api.Agent.Tools;

public sealed class ListUnpaidInvoicesTool(IPayPalReadClient paypal, IUntrustedTextSanitizer sanitizer, TimeProvider time) : IAgentTool
{
    private const int PageSize = 100;
    private const int MaxPages = 5;

    public string Name => "ListUnpaidInvoices";

    public string Description =>
        "List the shop's PayPal invoices that are sent but not fully paid (SENT, UNPAID, PARTIALLY_PAID), " +
        "with amount due, due date and days overdue, most overdue first. Returns invoice_id values needed for reminders.";

    public JsonElement InputSchema { get; } = ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "overdue_only": { "type": "boolean", "description": "Only return invoices past their due date. Default false." }
          }
        }
        """);

    public async Task<ToolResult> ExecuteAsync(ToolArguments args, ToolContext context, CancellationToken ct)
    {
        var overdueOnly = args.OptionalBool("overdue_only", false);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        var unpaid = new List<PayPalInvoice>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var list = await paypal.ListInvoicesAsync(page, PageSize, ct);
            unpaid.AddRange((list.Items ?? []).Where(i => i.Status is not null && InvoiceStatuses.Unsettled.Contains(i.Status)));
            if (list.TotalPages is null || page >= list.TotalPages)
            {
                break;
            }
        }

        var selected = unpaid
            .Where(i => !overdueOnly || PayPalViews.DaysOverdue(i, today) > 0)
            .OrderByDescending(i => PayPalViews.DaysOverdue(i, today))
            .ToList();

        var totals = selected
            .Select(i => PayPalViews.Money(i.DueAmount) ?? PayPalViews.Money(i.Amount))
            .Where(m => m is not null)
            .GroupBy(m => m!.Currency)
            .Select(g => new PayPalViews.MoneyView(g.Sum(m => m!.Amount), g.Key))
            .ToList();

        return ToolResult.Ok(new
        {
            AsOf = today.ToString("yyyy-MM-dd"),
            Count = selected.Count,
            TotalDue = totals,
            Invoices = selected.Select(i => PayPalViews.Invoice(i, today, sanitizer, context.SecurityFlags)).ToList(),
        });
    }
}
