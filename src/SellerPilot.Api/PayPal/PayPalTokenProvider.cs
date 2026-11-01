using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SellerPilot.Api.Configuration;
using SellerPilot.Api.PayPal.Models;

namespace SellerPilot.Api.PayPal;

public interface IPayPalTokenProvider
{
    /// <summary>Returns a cached OAuth access token, fetching a new one shortly before it expires.</summary>
    Task<string> GetAccessTokenAsync(CancellationToken ct);

    /// <summary>Scopes granted to the current token (used for diagnostics, e.g. is Transaction Search enabled).</summary>
    Task<IReadOnlyCollection<string>> GetGrantedScopesAsync(CancellationToken ct);

    /// <summary>Drops the cached token if it is still the given one (call after a 401).</summary>
    void Invalidate(string token);
}

/// <summary>
/// OAuth 2.0 client-credentials token cache for the PayPal sandbox.
/// Singleton; concurrent callers share one in-flight refresh. The token and secret are never logged.
/// </summary>
public sealed class PayPalTokenProvider : IPayPalTokenProvider, IDisposable
{
    public const string HttpClientName = "PayPal";
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<PayPalOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PayPalTokenProvider> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CachedToken? _cached;

    public PayPalTokenProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<PayPalOptions> options,
        TimeProvider time,
        ILogger<PayPalTokenProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct) => (await GetTokenAsync(ct)).Value;

    public async Task<IReadOnlyCollection<string>> GetGrantedScopesAsync(CancellationToken ct) => (await GetTokenAsync(ct)).Scopes;

    public void Invalidate(string token)
    {
        var current = Volatile.Read(ref _cached);
        if (current is not null && current.Value == token)
        {
            Interlocked.CompareExchange(ref _cached, null, current);
        }
    }

    public void Dispose() => _refreshGate.Dispose();

    private async Task<CachedToken> GetTokenAsync(CancellationToken ct)
    {
        var current = Volatile.Read(ref _cached);
        if (current is not null && _time.GetUtcNow() < current.RefreshAfter)
        {
            return current;
        }

        await _refreshGate.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while we waited.
            current = Volatile.Read(ref _cached);
            if (current is not null && _time.GetUtcNow() < current.RefreshAfter)
            {
                return current;
            }

            var fresh = await RequestTokenAsync(ct);
            Volatile.Write(ref _cached, fresh);
            return fresh;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<CachedToken> RequestTokenAsync(CancellationToken ct)
    {
        var options = _options.Value;
        if (!options.HasCredentials)
        {
            throw new PayPalConfigurationException(
                "PayPal sandbox credentials are not configured. Set PayPal:ClientId and PayPal:ClientSecret with dotnet user-secrets or .env (see README).");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/oauth2/token")
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("grant_type", "client_credentials")]),
        };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.ClientId}:{options.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new PayPalApiException(null, "NETWORK_ERROR", null, "Could not reach the PayPal sandbox to get an access token.", innerException: ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PayPalApiException(null, "TIMEOUT", null, "The PayPal sandbox token request timed out.", innerException: ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var (error, description) = await ReadOAuthErrorAsync(response, ct);
                throw new PayPalApiException(
                    response.StatusCode,
                    error ?? "OAUTH_ERROR",
                    response.Headers.TryGetValues("Paypal-Debug-Id", out var ids) ? ids.FirstOrDefault() : null,
                    $"PayPal rejected the token request ({(int)response.StatusCode} {error}): {description}. Check that the sandbox client ID and secret are correct.");
            }

            var token = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(PayPalJson.Options, ct);
            if (token is null || string.IsNullOrEmpty(token.AccessToken))
            {
                throw new PayPalApiException(response.StatusCode, "OAUTH_ERROR", null, "PayPal returned an empty access token.");
            }

            var lifetime = TimeSpan.FromSeconds(Math.Max(token.ExpiresIn, 1));
            var skew = lifetime > RefreshSkew * 2 ? RefreshSkew : lifetime / 2;
            var scopes = (token.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _logger.LogInformation("Obtained PayPal sandbox access token, expires in {ExpiresIn}s, {ScopeCount} scopes", token.ExpiresIn, scopes.Length);
            return new CachedToken(token.AccessToken, _time.GetUtcNow() + lifetime - skew, scopes);
        }
    }

    private static async Task<(string? Error, string? Description)> ReadOAuthErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return (
                root.TryGetProperty("error", out var e) ? e.GetString() : null,
                root.TryGetProperty("error_description", out var d) ? d.GetString() : null);
        }
        catch (JsonException)
        {
            return (null, response.ReasonPhrase);
        }
    }

    private sealed record CachedToken(string Value, DateTimeOffset RefreshAfter, IReadOnlyCollection<string> Scopes);

    private sealed record OAuthTokenResponse(string? AccessToken, string? TokenType, int ExpiresIn, string? Scope);
}
