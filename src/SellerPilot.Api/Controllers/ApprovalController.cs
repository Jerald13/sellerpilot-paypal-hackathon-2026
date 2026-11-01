using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SellerPilot.Api.Approvals;
using SellerPilot.Api.Contracts;
using SellerPilot.Api.Data;

namespace SellerPilot.Api.Controllers;

/// <summary>Pending actions and the seller's decisions. Approve is the only endpoint that moves money.</summary>
[ApiController]
[Route("api/actions")]
public sealed class ApprovalController(AppDbContext db, IApprovalService approvals) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<PendingActionDto>> List([FromQuery] ActionStatus? status, CancellationToken ct)
    {
        var query = db.PendingActions.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(a => a.Status == status);
        }

        var actions = await query.OrderByDescending(a => a.CreatedAt).Take(200).ToListAsync(ct);
        return actions.Select(PendingActionDto.From).ToList();
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PendingActionDto>> Get(Guid id, CancellationToken ct)
    {
        var action = await db.PendingActions.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        return action is null ? NotFound() : PendingActionDto.From(action);
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<ActionResult<PendingActionDto>> Approve(Guid id, CancellationToken ct) =>
        ToResponse(await approvals.ApproveAsync(id, ct));

    [HttpPost("{id:guid}/reject")]
    public async Task<ActionResult<PendingActionDto>> Reject(Guid id, CancellationToken ct) =>
        ToResponse(await approvals.RejectAsync(id, ct));

    private ActionResult<PendingActionDto> ToResponse(DecisionResult result) => result.Outcome switch
    {
        DecisionOutcome.Done => PendingActionDto.From(result.Action!),
        DecisionOutcome.NotFound => Problem(statusCode: StatusCodes.Status404NotFound, title: result.Message),
        _ => Problem(statusCode: StatusCodes.Status409Conflict, title: result.Message),
    };
}
