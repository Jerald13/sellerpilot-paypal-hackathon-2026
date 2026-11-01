using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SellerPilot.Api.Configuration;
using SellerPilot.Api.PayPal.Models;

namespace SellerPilot.Api.PayPal;

/// <summary>
/// Typed PayPal REST client (sandbox only). Adds the bearer token, retries transient failures and
/// sends PayPal-Request-Id on writes so a retried refund can never be executed twice.
/// </summary>
public sealed class PayPalClient : IPayPalReadClient, IPayPalWriteClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPayPalTokenProvider _tokens;
    private readonly IOptions<PayPalOptions> _options;
    private readonly ILogger<PayPalClient> _logger;

    public PayPalClient(
        IHttpClientFactory httpClientFactory,
        IPayPalTokenProvider tokens,
        IOptions<PayPalOptions> options,
        ILogger<PayPalClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
        _options = options;
        _logger = logger;
    }

    public Task<PayPalOrder> GetOrderAsync(string orderId, CancellationToken ct) =>
        SendAsync<PayPalOrder>(HttpMethod.Get, $"v2/checkout/orders/{Escape(orderId)}", null, null, ct);

    public Task<PayPalCapture> GetCaptureAsync(string captureId, CancellationToken ct) =>
        SendAsync<PayPalCapture>(HttpMethod.Get, $"v2/payments/captures/{Escape(captureId)}", null, null, ct);

    public Task<PayPalInvoice> GetInvoiceAsync(string invoiceId, CancellationToken ct) =>
        SendAsync<PayPalInvoice>(HttpMethod.Get, $"v2/invoicing/invoices/{Escape(invoiceId)}", null, null, ct);

    public Task<PayPalInvoiceList> ListInvoicesAsync(int page, int pageSize, CancellationToken ct) =>
        SendAsync<PayPalInvoiceList>(
            HttpMethod.Get,
            $"v2/invoicing/invoices?page={page.ToString(CultureInfo.InvariantCulture)}&page_size={pageSize.ToString(CultureInfo.InvariantCulture)}&total_required=true",
            null,
            null,
            ct);

    public Task<PayPalTransactionSearchResponse> SearchTransactionsAsync(TransactionSearchQuery query, CancellationToken ct)
    {
        var path = new StringBuilder("v1/reporting/transactions?fields=transaction_info,payer_info,cart_info")
            .Append("&start_date=").Append(Escape(FormatSearchDate(query.Start)))
            .Append("&end_date=").Append(Escape(FormatSearchDate(query.End)))
            .Append("&page_size=").Append(query.PageSize.ToString(CultureInfo.InvariantCulture))
            .Append("&page=").Append(query.Page.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(query.TransactionId))
        {
            path.Append("&transaction_id=").Append(Escape(query.TransactionId));
        }

        return SendAsync<PayPalTransactionSearchResponse>(HttpMethod.Get, path.ToString(), null, null, ct);
    }

    public Task<PayPalRefund> RefundCaptureAsync(string captureId, PayPalRefundRequest request, string requestId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        return SendAsync<PayPalRefund>(HttpMethod.Post, $"v2/payments/captures/{Escape(captureId)}/refund", request, requestId, ct);
    }

    public async Task SendInvoiceReminderAsync(string invoiceId, PayPalInvoiceReminderRequest request, string requestId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        await SendAsync<JsonElement>(HttpMethod.Post, $"v2/invoicing/invoices/{Escape(invoiceId)}/remind", request, requestId, ct);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, string? requestId, CancellationToken ct)
    {
        var options = _options.Value;
        var client = _httpClientFactory.CreateClient(PayPalTokenProvider.HttpClientName);
        var maxAttempts = 1 + Math.Max(0, options.MaxRetries);

        // POSTs are only retried when they carry an idempotency key; GETs are always safe to retry.
        var canRetry = method == HttpMethod.Get || requestId is not null;
        var tokenRefreshed = false;
        var logPath = path.Split('?')[0];

        for (var attempt = 1; ; attempt++)
        {
            var token = await _tokens.GetAccessTokenAsync(ct);
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (requestId is not null)
            {
                request.Headers.TryAddWithoutValidation("PayPal-Request-Id", requestId);
            }

            if (method == HttpMethod.Post)
            {
                request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
                request.Content = body is null
                    ? new StringContent("{}", Encoding.UTF8, "application/json")
                    : JsonContent.Create(body, body.GetType(), options: PayPalJson.Options);
            }

            var stopwatch = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                var kind = ex is TaskCanceledException ? "TIMEOUT" : "NETWORK_ERROR";
                if (canRetry && attempt < maxAttempts)
                {
                    _logger.LogWarning("PayPal {Method} {Path} attempt {Attempt} failed ({Kind}); retrying", method, logPath, attempt, kind);
                    await DelayAsync(options, attempt, null, ct);
                    continue;
                }

                throw new PayPalApiException(
                    null,
                    kind,
                    null,
                    $"PayPal did not respond to {method} {logPath} after {attempt} attempt(s) ({kind}).",
                    innerException: ex);
            }

            using (response)
            {
                _logger.LogInformation(
                    "PayPal {Method} {Path} -> {Status} in {ElapsedMs}ms (attempt {Attempt})",
                    method, logPath, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, attempt);

                if (response.StatusCode == HttpStatusCode.Unauthorized && !tokenRefreshed)
                {
                    // Token revoked or expired early: refresh once without consuming a retry.
                    _tokens.Invalidate(token);
                    tokenRefreshed = true;
                    attempt--;
                    continue;
                }

                if (response.IsSuccessStatusCode)
                {
                    return await ReadBodyAsync<T>(response, method, logPath, ct);
                }

                var error = await ReadErrorAsync(response, method, logPath, ct);
                if (canRetry && attempt < maxAttempts && IsTransient(response.StatusCode))
                {
                    _logger.LogWarning("PayPal {Method} {Path} returned {Status} (debug id {DebugId}); retrying", method, logPath, (int)response.StatusCode, error.DebugId);
                    await DelayAsync(options, attempt, response.Headers.RetryAfter, ct);
                    continue;
                }

                throw error;
            }
        }
    }

    private static async Task<T> ReadBodyAsync<T>(HttpResponseMessage response, HttpMethod method, string logPath, CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return default!;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, PayPalJson.Options)!;
        }
        catch (JsonException ex)
        {
            throw new PayPalApiException(response.StatusCode, "INVALID_RESPONSE", null, $"PayPal returned an unexpected response for {method} {logPath}.", innerException: ex);
        }
    }

    private static async Task<PayPalApiException> ReadErrorAsync(HttpResponseMessage response, HttpMethod method, string logPath, CancellationToken ct)
    {
        string? name = null;
        string? message = null;
        var debugId = response.Headers.TryGetValues("Paypal-Debug-Id", out var ids) ? ids.FirstOrDefault() : null;
        var issues = new List<string>();
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                name = GetString(root, "name") ?? GetString(root, "error");
                message = GetString(root, "message") ?? GetString(root, "error_description");
                debugId ??= GetString(root, "debug_id");
                if (root.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                {
                    foreach (var detail in details.EnumerateArray())
                    {
                        if (GetString(detail, "issue") is { } issue)
                        {
                            issues.Add(issue);
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body (e.g. an HTML gateway page); the status code is enough.
        }

        var text = new StringBuilder($"PayPal {method} {logPath} failed: {(int)response.StatusCode} {name ?? response.ReasonPhrase}");
        if (issues.Count > 0)
        {
            text.Append(" (").Append(string.Join(", ", issues)).Append(')');
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            text.Append(". ").Append(message);
        }

        return new PayPalApiException(response.StatusCode, name, debugId, text.ToString(), issues);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)status >= 500;

    private static Task DelayAsync(PayPalOptions options, int attempt, RetryConditionHeaderValue? retryAfter, CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(options.RetryBaseDelayMilliseconds * Math.Pow(3, attempt - 1));
        if (retryAfter?.Delta is { } hinted && hinted > delay)
        {
            delay = hinted < TimeSpan.FromSeconds(5) ? hinted : TimeSpan.FromSeconds(5);
        }

        return delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct);
    }

    private static string FormatSearchDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
