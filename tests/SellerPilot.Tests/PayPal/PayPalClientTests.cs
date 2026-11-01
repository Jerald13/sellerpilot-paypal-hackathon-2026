using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SellerPilot.Api.Configuration;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;
using SellerPilot.Tests.TestSupport;

namespace SellerPilot.Tests.PayPal;

public class PayPalClientTests
{
    private const string NotFoundJson = """
        {"name":"RESOURCE_NOT_FOUND","details":[{"field":"order_id","value":"X","location":"path","issue":"INVALID_RESOURCE_ID","description":"Specified resource ID does not exist."}],"message":"The specified resource does not exist.","debug_id":"f1a2b3c4d5e6"}
        """;

    private readonly FakeHttpMessageHandler _handler = new();
    private readonly IPayPalTokenProvider _tokens = Substitute.For<IPayPalTokenProvider>();
    private readonly PayPalClient _client;

    public PayPalClientTests()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(PayPalTokenProvider.HttpClientName)
            .Returns(_ => new HttpClient(_handler, disposeHandler: false) { BaseAddress = new Uri(PayPalOptions.SandboxBaseUrl + "/") });
        _tokens.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns("token-1");
        var options = Options.Create(new PayPalOptions { ClientId = "id", ClientSecret = "secret", MaxRetries = 2, RetryBaseDelayMilliseconds = 0 });
        _client = new PayPalClient(factory, _tokens, options, NullLogger<PayPalClient>.Instance);
    }

    [Fact]
    public async Task GetOrder_sends_bearer_token_and_parses_captures_and_refunds()
    {
        _handler.Respond(HttpStatusCode.OK, PayPalFixtures.Order(refunds: [PayPalFixtures.Refund("1JU08902781691411", "10.00")]));

        var order = await _client.GetOrderAsync(PayPalFixtures.OrderId, CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal($"https://api-m.sandbox.paypal.com/v2/checkout/orders/{PayPalFixtures.OrderId}", request.Uri.ToString());
        Assert.Equal("Bearer token-1", request.Header("Authorization"));
        Assert.Null(request.Header("PayPal-Request-Id"));

        var unit = Assert.Single(order.PurchaseUnits!);
        var capture = Assert.Single(unit.Payments!.Captures!);
        Assert.Equal(PayPalFixtures.CaptureId, capture.Id);
        Assert.True(capture.Amount!.TryGetAmount(out var amount));
        Assert.Equal(45.00m, amount);
        var refund = Assert.Single(unit.Payments.Refunds!);
        Assert.Equal(PayPalFixtures.CaptureId, refund.CaptureId);
        Assert.Equal("Test Buyer Aisha", order.Payer!.Name!.FullName);
    }

    [Fact]
    public async Task Refund_sends_idempotency_key_and_snake_case_body()
    {
        _handler.Respond(HttpStatusCode.Created, PayPalFixtures.Refund("1JU08902781691411", "45.00"));
        var requestId = Guid.NewGuid().ToString();

        var refund = await _client.RefundCaptureAsync(
            PayPalFixtures.CaptureId,
            new PayPalRefundRequest(PayPalMoney.From(45m, "USD"), CustomId: requestId),
            requestId,
            CancellationToken.None);

        Assert.Equal("1JU08902781691411", refund.Id);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith($"/v2/payments/captures/{PayPalFixtures.CaptureId}/refund", request.Uri.AbsolutePath);
        Assert.Equal(requestId, request.Header("PayPal-Request-Id"));
        Assert.Equal("return=representation", request.Header("Prefer"));

        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("USD", body.RootElement.GetProperty("amount").GetProperty("currency_code").GetString());
        Assert.Equal("45.00", body.RootElement.GetProperty("amount").GetProperty("value").GetString());
        Assert.Equal(requestId, body.RootElement.GetProperty("custom_id").GetString());
        Assert.False(body.RootElement.TryGetProperty("note_to_payer", out _));
    }

    [Fact]
    public async Task Retried_refund_reuses_the_same_PayPal_Request_Id()
    {
        _handler.Respond(HttpStatusCode.ServiceUnavailable, """{"name":"SERVICE_UNAVAILABLE"}""")
            .Throw(new TaskCanceledException("timeout"))
            .Respond(HttpStatusCode.Created, PayPalFixtures.Refund("1JU08902781691411", "45.00"));
        var requestId = Guid.NewGuid().ToString();

        await _client.RefundCaptureAsync(PayPalFixtures.CaptureId, new PayPalRefundRequest(PayPalMoney.From(45m, "USD")), requestId, CancellationToken.None);

        Assert.Equal(3, _handler.Requests.Count);
        Assert.All(_handler.Requests, r => Assert.Equal(requestId, r.Header("PayPal-Request-Id")));
    }

    [Fact]
    public async Task Not_found_is_reported_and_not_retried()
    {
        _handler.Respond(HttpStatusCode.NotFound, NotFoundJson);

        var ex = await Assert.ThrowsAsync<PayPalApiException>(() => _client.GetOrderAsync("AAAAAAAAAAAAAAAAA", CancellationToken.None));

        Assert.True(ex.IsNotFound);
        Assert.Equal("RESOURCE_NOT_FOUND", ex.ErrorName);
        Assert.Equal("f1a2b3c4d5e6", ex.DebugId);
        Assert.Contains("INVALID_RESOURCE_ID", ex.Issues);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Business_errors_are_not_retried()
    {
        _handler.Respond(HttpStatusCode.UnprocessableEntity, """{"name":"UNPROCESSABLE_ENTITY","details":[{"issue":"CAPTURE_FULLY_REFUNDED"}],"message":"The requested action could not be performed."}""");

        var ex = await Assert.ThrowsAsync<PayPalApiException>(() =>
            _client.RefundCaptureAsync(PayPalFixtures.CaptureId, new PayPalRefundRequest(PayPalMoney.From(45m, "USD")), "req-1", CancellationToken.None));

        Assert.Contains("CAPTURE_FULLY_REFUNDED", ex.Issues);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Unauthorized_refreshes_the_token_once()
    {
        _tokens.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns("expired-token", "fresh-token");
        _handler.Respond(HttpStatusCode.Unauthorized, """{"error":"invalid_token"}""")
            .Respond(HttpStatusCode.OK, PayPalFixtures.Capture());

        var capture = await _client.GetCaptureAsync(PayPalFixtures.CaptureId, CancellationToken.None);

        Assert.Equal(PayPalFixtures.CaptureId, capture.Id);
        _tokens.Received(1).Invalidate("expired-token");
        Assert.Equal("Bearer fresh-token", _handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task Network_failure_after_all_retries_reports_no_response()
    {
        _handler.Throw(new HttpRequestException("down")).Throw(new HttpRequestException("down")).Throw(new HttpRequestException("down"));

        var ex = await Assert.ThrowsAsync<PayPalApiException>(() => _client.GetInvoiceAsync(PayPalFixtures.InvoiceId, CancellationToken.None));

        Assert.True(ex.IsNoResponse);
        Assert.Equal(3, _handler.Requests.Count);
    }

    [Fact]
    public async Task Invoice_reminder_posts_to_remind_endpoint_and_accepts_204()
    {
        _handler.Respond(HttpStatusCode.NoContent);

        await _client.SendInvoiceReminderAsync(PayPalFixtures.InvoiceId, new PayPalInvoiceReminderRequest("Friendly reminder", "Hi, invoice #0003 is due."), "req-9", CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.EndsWith($"/v2/invoicing/invoices/{PayPalFixtures.InvoiceId}/remind", request.Uri.AbsolutePath);
        Assert.Equal("req-9", request.Header("PayPal-Request-Id"));
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("Hi, invoice #0003 is due.", body.RootElement.GetProperty("note").GetString());
        Assert.True(body.RootElement.GetProperty("send_to_recipient").GetBoolean());
        Assert.False(body.RootElement.GetProperty("send_to_invoicer").GetBoolean());
    }

    [Fact]
    public async Task Transaction_search_uses_utc_window_and_parses_offsets_without_colon()
    {
        _handler.Respond(HttpStatusCode.OK, """
            {"transaction_details":[{"transaction_info":{"transaction_id":"3C679366HH908993F","transaction_event_code":"T0006","transaction_initiation_date":"2026-10-20T10:00:00+0000","transaction_amount":{"currency_code":"USD","value":"45.00"},"transaction_status":"S"},"payer_info":{"email_address":"aisha.buyer@example.com","payer_name":{"given_name":"Test Buyer","surname":"Aisha"}}}],"page":1,"total_items":1,"total_pages":1}
            """);

        var result = await _client.SearchTransactionsAsync(
            new TransactionSearchQuery(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero)),
            CancellationToken.None);

        var query = _handler.Requests[0].Uri.Query;
        Assert.Contains("start_date=2026-10-01T00%3A00%3A00Z", query);
        Assert.Contains("end_date=2026-10-31T00%3A00%3A00Z", query);
        var info = Assert.Single(result.TransactionDetails!).TransactionInfo!;
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero), PayPalDates.Parse(info.TransactionInitiationDate));
    }

    [Theory]
    [InlineData("https://api-m.sandbox.paypal.com", true)]
    [InlineData("https://api-m.sandbox.paypal.com/", true)]
    [InlineData("http://api-m.sandbox.paypal.com", false)]
    [InlineData("https://api.example.com", false)]
    [InlineData("https://api-m.sandbox.paypal.com.evil.example", false)]
    [InlineData("not a url", false)]
    public void Only_the_sandbox_host_is_accepted(string url, bool expected) =>
        Assert.Equal(expected, PayPalOptions.IsSandboxUrl(url));
}
