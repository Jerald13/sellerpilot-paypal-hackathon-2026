using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SellerPilot.Api.Agent;
using SellerPilot.Api.Auth;
using SellerPilot.Api.Configuration;
using SellerPilot.Api.Data;
using SellerPilot.Api.Shop;

namespace SellerPilot.Api.Controllers;

public sealed record ShopInfoDto(string ShopName, string SellerName);

public sealed record ProductDto(string Sku, string Name, decimal Price, string Currency, string ReturnPolicy);

public sealed record MyOrderItemDto(string Sku, string Name, int Quantity, decimal UnitPrice);

public sealed record MyOrderDto(
    string OrderNumber,
    DateTime PlacedAt,
    ShopOrderStatus Status,
    DateTime? DeliveredAt,
    string Currency,
    decimal Subtotal,
    decimal Discount,
    string? DiscountCode,
    decimal Total,
    decimal RefundedSoFar,
    string PaidWith,
    string ReturnPolicy,
    IReadOnlyList<MyOrderItemDto> Items);

public sealed record MyRefundRequestDto(Guid Id, string? OrderNumber, decimal? Amount, string? Currency, ActionStatus Status, DateTime CreatedAt, DateTime? DecidedAt);

public sealed record ShopChatRequest(Guid? ConversationId, string? Message);

public sealed record ShopChatResponse(Guid ConversationId, string Reply, IReadOnlyList<RefundOffer> RefundOffers, IReadOnlyList<string> ToolsUsed);

public sealed record CreateRefundRequest(string? OrderNumber, decimal? Amount, string? Reason);

/// <summary>
/// The storefront. A signed-in customer only ever sees their own orders (the customer ID comes from the sign-in cookie,
/// never from the request), and can only ask for refunds; approving them stays with the seller.
/// </summary>
[ApiController]
[Route("api/shop")]
[Authorize(Policy = AuthPolicies.Customer)]
public sealed class ShopController(AppDbContext db, ICustomerAgentService agent, IRefundRequestService refunds, IOptions<ShopOptions> options) : ControllerBase
{
    [HttpGet("info")]
    [AllowAnonymous]
    public ShopInfoDto Info() => new(options.Value.ShopName, options.Value.SellerName);

    [HttpGet("products")]
    [AllowAnonymous]
    public async Task<IReadOnlyList<ProductDto>> Products(CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking().Where(p => p.Active).OrderBy(p => p.Id).ToListAsync(ct);
        return products.Select(p => new ProductDto(p.Sku, p.Name, p.Price, p.Currency, ReturnPolicy(p.ReturnWindowDays))).ToList();
    }

    [HttpGet("orders")]
    public async Task<IReadOnlyList<MyOrderDto>> MyOrders(CancellationToken ct)
    {
        var customerId = CustomerId();
        var orders = await db.ShopOrders.AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.LedgerEntries)
            .Where(o => o.CustomerId == customerId)
            .OrderByDescending(o => o.PlacedAt)
            .ToListAsync(ct);
        return orders.Select(o => new MyOrderDto(
            o.OrderNumber,
            o.PlacedAt,
            o.Status,
            o.DeliveredAt,
            o.Currency,
            o.Subtotal,
            o.Discount,
            o.DiscountCode,
            o.Total,
            o.LedgerEntries.Where(e => e.Type == LedgerEntryType.Refund).Sum(e => e.Amount),
            o.PaymentMethod == PaymentMethod.PayPal ? "PayPal" : "Bank transfer",
            ReturnPolicy(o.Lines.Count > 0 ? o.Lines.Min(l => l.ReturnWindowDays) : 0),
            o.Lines.Select(l => new MyOrderItemDto(l.Sku, l.Name, l.Quantity, l.UnitPrice)).ToList())).ToList();
    }

    [HttpPost("chat")]
    [EnableRateLimiting(AuthPolicies.ChatRateLimit)]
    public async Task<ActionResult<ShopChatResponse>> Chat([FromBody] ShopChatRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Message is required.");
        }

        var turn = await agent.HandleMessageAsync(CustomerId(), request.ConversationId, request.Message, ct);
        return new ShopChatResponse(turn.ConversationId, turn.Reply, turn.RefundOffers ?? [], turn.ToolCalls.Select(c => c.Name).Distinct().ToList());
    }

    /// <summary>The customer's "Request refund" click. Everything is checked again here; the request then waits for the seller.</summary>
    [HttpPost("refund-requests")]
    public async Task<ActionResult<MyRefundRequestDto>> RequestRefund([FromBody] CreateRefundRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.OrderNumber) || request.OrderNumber.Length > 32 || request.Amount is not > 0m)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "An order number and a positive amount are required.");
        }

        var result = await refunds.CreateAsync(CustomerId(), request.OrderNumber, request.Amount.Value, request.Reason, ct);
        if (result.Action is null)
        {
            return Problem(statusCode: result.NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict, title: result.Error);
        }

        var a = result.Action;
        return new MyRefundRequestDto(a.Id, ShopViewsOrderNumber(request.OrderNumber), a.Amount, a.Currency, a.Status, a.CreatedAt, a.DecidedAt);
    }

    [HttpGet("refund-requests")]
    public async Task<IReadOnlyList<MyRefundRequestDto>> MyRefundRequests(CancellationToken ct)
    {
        var customerId = CustomerId();
        var requests = await db.PendingActions.AsNoTracking()
            .Where(a => a.CustomerId == customerId && a.RequestedBy == ActionRequesters.Customer)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);
        var orderIds = requests.Where(a => a.ShopOrderId != null).Select(a => a.ShopOrderId!.Value).Distinct().ToList();
        var numbers = await db.ShopOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.OrderNumber, ct);
        return requests.Select(a => new MyRefundRequestDto(
            a.Id,
            a.ShopOrderId is { } id && numbers.TryGetValue(id, out var number) ? number : null,
            a.Amount,
            a.Currency,
            a.Status,
            a.CreatedAt,
            a.DecidedAt)).ToList();
    }

    private int CustomerId() => User.CustomerId() ?? throw new InvalidOperationException("The Customer policy guarantees a customer ID.");

    private static string ReturnPolicy(int days) => days <= 0 ? "Final sale" : $"{days}-day returns";

    private static string ShopViewsOrderNumber(string value) => Agent.Tools.ShopViews.NormalizeOrderNumber(value);
}
