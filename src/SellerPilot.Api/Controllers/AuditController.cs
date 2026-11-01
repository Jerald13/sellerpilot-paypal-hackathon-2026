using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SellerPilot.Api.Auth;
using SellerPilot.Api.Contracts;
using SellerPilot.Api.Data;

namespace SellerPilot.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthPolicies.Seller)]
[Route("api/audit")]
public sealed class AuditController(AppDbContext db) : ControllerBase
{
    /// <summary>Newest first.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<AuditLogDto>> List([FromQuery] int take = 200, CancellationToken ct = default)
    {
        var logs = await db.AuditLogs.AsNoTracking()
            .OrderByDescending(l => l.Id)
            .Take(Math.Clamp(take, 1, 1000))
            .ToListAsync(ct);
        return logs.Select(AuditLogDto.From).ToList();
    }
}
