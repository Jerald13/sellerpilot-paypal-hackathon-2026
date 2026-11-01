using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SellerPilot.Api.Configuration;
using SellerPilot.Api.PayPal;
using SellerPilot.Tests.TestSupport;

namespace SellerPilot.Tests.PayPal;

public class PayPalTokenProviderTests
{
    private const string TokenJson = """{"scope":"https://uri.paypal.com/services/invoicing https://uri.paypal.com/services/reporting/search/read","access_token":"A21AAtoken1","token_type":"Bearer","app_id":"APP-80W284485P519543T","expires_in":32400,"nonce":"n"}""";

    private readonly FakeHttpMessageHandler _handler = new();
    private readonly FixedTimeProvider _time = new(PayPalFixtures.Now);

    private PayPalTokenProvider CreateProvider(string clientId = "sandbox-client", string secret = "sandbox-secret")
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(PayPalTokenProvider.HttpClientName)
            .Returns(_ => new HttpClient(_handler, disposeHandler: false) { BaseAddress = new Uri(PayPalOptions.SandboxBaseUrl + "/") });
        var options = Options.Create(new PayPalOptions { ClientId = clientId, ClientSecret = secret });
        return new PayPalTokenProvider(factory, options, _time, NullLogger<PayPalTokenProvider>.Instance);
    }

    [Fact]
    public async Task Requests_token_with_client_credentials_and_basic_auth()
    {
        _handler.Respond(HttpStatusCode.OK, TokenJson);
        var provider = CreateProvider();

        var token = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("A21AAtoken1", token);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api-m.sandbox.paypal.com/v1/oauth2/token", request.Uri.ToString());
        Assert.Equal("grant_type=client_credentials", request.Body);
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("sandbox-client:sandbox-secret"));
        Assert.Equal($"Basic {expected}", request.Header("Authorization"));
    }

    [Fact]
    public async Task Caches_token_until_shortly_before_expiry()
    {
        _handler.Respond(HttpStatusCode.OK, TokenJson)
            .Respond(HttpStatusCode.OK, TokenJson.Replace("A21AAtoken1", "A21AAtoken2"));
        var provider = CreateProvider();

        var first = await provider.GetAccessTokenAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(8)); // still more than 5 minutes left of 9 hours
        var second = await provider.GetAccessTokenAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(56)); // now within the refresh window
        var third = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("A21AAtoken1", first);
        Assert.Equal("A21AAtoken1", second);
        Assert.Equal("A21AAtoken2", third);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_token_request()
    {
        _handler.Respond(HttpStatusCode.OK, TokenJson);
        var provider = CreateProvider();

        var tokens = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => provider.GetAccessTokenAsync(CancellationToken.None)));

        Assert.All(tokens, t => Assert.Equal("A21AAtoken1", t));
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Invalidate_forces_a_new_token()
    {
        _handler.Respond(HttpStatusCode.OK, TokenJson)
            .Respond(HttpStatusCode.OK, TokenJson.Replace("A21AAtoken1", "A21AAtoken2"));
        var provider = CreateProvider();

        var first = await provider.GetAccessTokenAsync(CancellationToken.None);
        provider.Invalidate(first);
        var second = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("A21AAtoken2", second);
    }

    [Fact]
    public async Task Exposes_granted_scopes()
    {
        _handler.Respond(HttpStatusCode.OK, TokenJson);
        var provider = CreateProvider();

        var scopes = await provider.GetGrantedScopesAsync(CancellationToken.None);

        Assert.Contains("https://uri.paypal.com/services/reporting/search/read", scopes);
    }

    [Fact]
    public async Task Missing_credentials_fail_without_calling_PayPal()
    {
        var provider = CreateProvider(clientId: "", secret: "");

        await Assert.ThrowsAsync<PayPalConfigurationException>(() => provider.GetAccessTokenAsync(CancellationToken.None));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Rejected_credentials_give_a_clear_error_without_leaking_the_secret()
    {
        _handler.Respond(HttpStatusCode.Unauthorized, """{"error":"invalid_client","error_description":"Client Authentication failed"}""");
        var provider = CreateProvider(secret: "super-secret-value");

        var ex = await Assert.ThrowsAsync<PayPalApiException>(() => provider.GetAccessTokenAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("invalid_client", ex.Message);
        Assert.DoesNotContain("super-secret-value", ex.Message);
    }
}
