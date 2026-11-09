using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SellerPilot.Api.Configuration;
using SellerPilot.Api.Data;
using SellerPilot.Api.Llm;
using SellerPilot.Api.PayPal;

namespace SellerPilot.Api.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController(AppDbContext db, IPayPalTokenProvider tokens, ILlmClient llm, IOptions<ShopOptions> shop) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var databaseOk = await db.Database.CanConnectAsync(ct);

        bool paypalOk;
        string? paypalError = null;
        IReadOnlyCollection<string> scopes = [];
        try
        {
            scopes = await tokens.GetGrantedScopesAsync(ct);
            paypalOk = true;
        }
        catch (Exception ex) when (ex is PayPalApiException or PayPalConfigurationException)
        {
            paypalOk = false;
            paypalError = ex.Message;
        }

        var llmConfigured = llm is not UnconfiguredLlmClient;
        var healthy = databaseOk && paypalOk && llmConfigured;
        var body = new
        {
            Status = healthy ? "ok" : "degraded",
            Database = new { Ok = databaseOk },
            PayPal = new
            {
                Ok = paypalOk,
                Environment = "sandbox",
                Error = paypalError,
                Features = paypalOk
                    ? new
                    {
                        Invoicing = scopes.Any(s => s.Contains("/invoicing", StringComparison.Ordinal)),
                        TransactionSearch = scopes.Any(s => s.Contains("/reporting/search/read", StringComparison.Ordinal)),
                    }
                    : null,
            },
            Llm = new { Provider = llm.ProviderName, llm.Model, Configured = llmConfigured },
            // Sign-in and shop data are reported but don't make the API unhealthy: the agent works without them.
            Auth = new { Configured = shop.Value.HasDemoPassword || shop.Value.PublicDemo, shop.Value.PublicDemo },
            Shop = new { Seeded = databaseOk && await db.Customers.AnyAsync(ct), shop.Value.ShopName },
        };

        return healthy ? Ok(body) : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
    }
}
