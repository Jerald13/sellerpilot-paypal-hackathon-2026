using System.Net;

namespace SellerPilot.Api.PayPal;

/// <summary>A PayPal call failed. Message is safe to show (no secrets, no tokens).</summary>
public sealed class PayPalApiException : Exception
{
    public PayPalApiException(
        HttpStatusCode? statusCode,
        string? errorName,
        string? debugId,
        string message,
        IReadOnlyList<string>? issues = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ErrorName = errorName;
        DebugId = debugId;
        Issues = issues ?? [];
    }

    /// <summary>Null when PayPal never answered (network error or timeout).</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>PayPal error name, e.g. RESOURCE_NOT_FOUND or UNPROCESSABLE_ENTITY.</summary>
    public string? ErrorName { get; }

    /// <summary>PayPal-Debug-Id, useful when contacting PayPal support.</summary>
    public string? DebugId { get; }

    /// <summary>Issue codes from the error details, e.g. INVALID_RESOURCE_ID or REFUND_AMOUNT_EXCEEDED.</summary>
    public IReadOnlyList<string> Issues { get; }

    public bool IsNotFound =>
        StatusCode == HttpStatusCode.NotFound || Issues.Contains("INVALID_RESOURCE_ID", StringComparer.Ordinal);

    /// <summary>True when PayPal gave no answer, so the outcome of a write is unknown.</summary>
    public bool IsNoResponse => StatusCode is null;
}

/// <summary>PayPal credentials are missing; the app runs but cannot call PayPal.</summary>
public sealed class PayPalConfigurationException(string message) : Exception(message);
