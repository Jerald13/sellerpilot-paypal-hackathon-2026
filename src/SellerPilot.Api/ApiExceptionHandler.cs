using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SellerPilot.Api.Llm;
using SellerPilot.Api.PayPal;

namespace SellerPilot.Api;

/// <summary>Maps known failures to problem details with messages that are safe to show (no secrets).</summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            ArgumentException => (StatusCodes.Status400BadRequest, exception.Message),
            LlmConfigurationException or PayPalConfigurationException => (StatusCodes.Status503ServiceUnavailable, exception.Message),
            LlmException { IsTransient: true } => (StatusCodes.Status503ServiceUnavailable, exception.Message),
            LlmException => (StatusCodes.Status502BadGateway, exception.Message),
            PayPalApiException => (StatusCodes.Status502BadGateway, exception.Message),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected server error."),
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Request failed: {Title}", title);
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title },
        });
    }
}
