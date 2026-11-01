using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SellerPilot.Api.Data;
using SellerPilot.Api.Guardrails;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;

namespace SellerPilot.Api.Agent.Tools;

/// <param name="db">Optional: when given, a PayPal order that belongs to a shop order also returns the shop's own record.</param>
public sealed class GetOrderTool(IPayPalReadClient paypal, IUntrustedTextSanitizer sanitizer, AppDbContext? db = null) : IAgentTool
{
    public string Name => "GetOrder";

    public string Description =>
        "Look up one PayPal order by its order ID. Returns the status, customer, items, captured payments " +
        "(with the capture_id needed for refunds), refunds already made, and how much can still be refunded. " +
        "Call this before proposing any refund, and to answer whether an order was paid or refunded.";

    public JsonElement InputSchema { get; } = ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "order_id": { "type": "string", "description": "PayPal order ID: 17 letters/digits, e.g. 5O190127TN364715T." }
          },
          "required": ["order_id"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(ToolArguments args, ToolContext context, CancellationToken ct)
    {
        var id = args.RequiredResourceId("order_id");
        try
        {
            var order = await paypal.GetOrderAsync(id, ct);
            return ToolResult.Ok(await WithShopRecordAsync(order, PayPalViews.Order(order, sanitizer, context.SecurityFlags), ct));
        }
        catch (PayPalApiException ex) when (ex.IsNotFound)
        {
            // The seller may have pasted a capture ID; resolve it to its order instead of failing.
            try
            {
                var capture = await paypal.GetCaptureAsync(id, ct);
                if (capture.RelatedOrderId is { } orderId)
                {
                    var order = await paypal.GetOrderAsync(orderId, ct);
                    return ToolResult.Ok(new
                    {
                        Note = $"{id} is a capture ID, not an order ID. This is the order it belongs to.",
                        Order = await WithShopRecordAsync(order, PayPalViews.Order(order, sanitizer, context.SecurityFlags), ct),
                    });
                }
            }
            catch (PayPalApiException inner) when (inner.IsNotFound)
            {
                // Neither an order nor a capture.
            }

            return ToolResult.Error($"No PayPal order or capture exists with ID {id}. Ask the seller to double-check the ID.");
        }
    }

    /// <summary>Adds the shop's order number and its current item names (the shop's wording, not PayPal's) when the order is in the books.</summary>
    private async Task<object> WithShopRecordAsync(PayPalOrder order, object paypalView, CancellationToken ct)
    {
        var shop = db is null ? null : await db.ShopOrders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.PayPalOrderId == order.Id, ct);
        if (shop is null)
        {
            return paypalView;
        }

        return new
        {
            PayPal = paypalView,
            ShopRecord = new
            {
                shop.OrderNumber,
                Items = shop.Lines.Select(l => new { l.Name, l.Quantity }).ToList(),
                Total = new PayPalViews.MoneyView(shop.Total, shop.Currency),
            },
        };
    }
}
