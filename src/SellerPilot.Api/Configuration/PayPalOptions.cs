namespace SellerPilot.Api.Configuration;

public sealed class PayPalOptions
{
    public const string SectionName = "PayPal";

    /// <summary>The only PayPal host SellerPilot may call. Any other host is rejected at startup.</summary>
    public const string SandboxHost = "api-m.sandbox.paypal.com";
    public const string SandboxBaseUrl = "https://" + SandboxHost;

    public string BaseUrl { get; set; } = SandboxBaseUrl;
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>Timeout for a single HTTP attempt.</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Extra attempts for transient failures (timeouts, 429, 5xx). Writes reuse the same PayPal-Request-Id.</summary>
    public int MaxRetries { get; set; } = 2;

    public int RetryBaseDelayMilliseconds { get; set; } = 300;

    public bool HasCredentials => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public static bool IsSandboxUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, SandboxHost, StringComparison.OrdinalIgnoreCase);
}
