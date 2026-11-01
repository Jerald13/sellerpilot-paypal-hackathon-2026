using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SellerPilot.Api.Auth;
using SellerPilot.Api.Contracts;
using SellerPilot.Api.Data;
using SellerPilot.Api.Shop;

namespace SellerPilot.Api.Controllers;

/// <summary>The shop's orders and their payment history (the books), for the seller.</summary>
[ApiController]
[Authorize(Policy = AuthPolicies.Seller)]
[Route("api/orders")]
public sealed class OrdersController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<ShopOrderDto>> List(CancellationToken ct)
    {
        var orders = await db.ShopOrders.AsNoTracking()
            .Include(o => o.Customer)
            .Include(o => o.Lines)
            .Include(o => o.LedgerEntries)
            .OrderByDescending(o => o.PlacedAt)
            .ToListAsync(ct);
        return orders.Select(ShopOrderDto.From).ToList();
    }

    [HttpGet("{orderNumber}")]
    public async Task<ActionResult<ShopOrderDto>> Get(string orderNumber, CancellationToken ct)
    {
        var normalized = orderNumber.Trim().ToUpperInvariant();
        var order = await db.ShopOrders.AsNoTracking()
            .Include(o => o.Customer)
            .Include(o => o.Lines)
            .Include(o => o.LedgerEntries)
            .FirstOrDefaultAsync(o => o.OrderNumber == normalized, ct);
        return order is null ? Problem(statusCode: StatusCodes.Status404NotFound, title: $"Order {normalized} does not exist.") : ShopOrderDto.From(order);
    }
}

/// <summary>Books vs PayPal checks.</summary>
[ApiController]
[Authorize(Policy = AuthPolicies.Seller)]
[Route("api/reconciliation")]
public sealed class ReconciliationController(AppDbContext db, IReconciliationService reconciliation) : ControllerBase
{
    /// <summary>Runs a check now (reads every PayPal order; takes a few seconds).</summary>
    [HttpPost("runs")]
    public async Task<ReconciliationRunDto> Run(CancellationToken ct) =>
        ReconciliationRunDto.From(await reconciliation.RunAsync("seller", ct));

    [HttpGet("runs/latest")]
    public async Task<ActionResult<ReconciliationRunDto>> Latest(CancellationToken ct)
    {
        var run = await db.ReconciliationRuns.AsNoTracking().Include(r => r.Findings).OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        return run is null ? NoContent() : ReconciliationRunDto.From(run);
    }
}
