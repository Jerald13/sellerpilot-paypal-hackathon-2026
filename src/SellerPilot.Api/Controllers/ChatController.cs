using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SellerPilot.Api.Agent;
using SellerPilot.Api.Auth;
using SellerPilot.Api.Contracts;

namespace SellerPilot.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthPolicies.Seller)]
[Route("api/chat")]
public sealed class ChatController(IAgentService agent) : ControllerBase
{
    /// <summary>Send a message; returns the agent's reply plus any new proposals waiting for approval.</summary>
    [HttpPost]
    [EnableRateLimiting(AuthPolicies.ChatRateLimit)]
    public async Task<ActionResult<ChatResponse>> Post([FromBody] ChatRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Message is required.");
        }

        var turn = await agent.HandleMessageAsync(request.ConversationId, request.Message, ct);
        return ChatResponse.From(turn);
    }
}
