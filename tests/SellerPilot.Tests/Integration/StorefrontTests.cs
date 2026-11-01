using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SellerPilot.Api;
using SellerPilot.Api.Llm;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;
using SellerPilot.Api.Shop;
using SellerPilot.Tests.TestSupport;

namespace SellerPilot.Tests.Integration;

/// <summary>
/// Customer and seller over real HTTP with real sign-in cookies: a customer asks for a refund in the storefront chat,
/// requests it with one click, and only the seller's approval moves money. LLM and PayPal are faked.
/// </summary>
public sealed class StorefrontTests : IDisposable
{
    private const string Password = "it-demo-password";
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"sellerpilot-shop-it-{Guid.NewGuid():N}.db");
    private readonly string _seedPath = Path.Combine(Path.GetTempPath(), $"sellerpilot-seed-{Guid.NewGuid():N}.json");
    private readonly ScriptedLlmClient _llm = new();
    private readonly SandboxPayPal _paypal = new();
    private readonly IPayPalWriteClient _write = Substitute.For<IPayPalWriteClient>();
    private readonly WebApplicationFactory<ApiExceptionHandler> _factory;

    public StorefrontTests()
    {
        File.WriteAllText(_seedPath, JsonSerializer.Serialize(ShopFixtures.Seed(), SeedOutputFile.Json));
        _factory = new WebApplicationFactory<ApiExceptionHandler>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:SellerPilot", $"Data Source={_databasePath};Pooling=False");
            builder.UseSetting("Shop:DemoPassword", Password);
            builder.UseSetting("Shop:SeedFile", _seedPath);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(PayPalFixtures.Now));
                services.AddSingleton<ILlmClient>(_llm);
                services.AddSingleton(_paypal.Client);
                services.AddSingleton(_write);
            });
        });
        _write.RefundCaptureAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(PayPalFixtures.Parse<PayPalRefund>(PayPalFixtures.Refund("7RF12345AB0000050", "50.00", captureId: "30379916G6480042M")));
    }

    public void Dispose()
    {
        _factory.Dispose();
        File.Delete(_databasePath);
        File.Delete(_seedPath);
    }

    private async Task<HttpClient> SignInAsync(string email)
    {
        var http = _factory.CreateClient();
        var login = await http.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        return http;
    }

    [Fact]
    public async Task A_customer_refund_request_is_executed_only_after_the_seller_approves_it()
    {
        _llm.ThenToolCalls(("CheckRefundOptions", """{"order_number":"SP-DVAN-1006","amount":60,"reason":"one cover arrived torn"}"""))
            .ThenText("The most we can refund is USD 50.00, because USD 30.00 was already refunded. Press Request refund to send it to the shop.");
        var john = await SignInAsync("john.doe@example.com");

        var chat = await john.PostAsJsonAsync("/api/shop/chat", new { message = "Can I get USD 60 back for SP-DVAN-1006? One cover arrived torn." });
        chat.EnsureSuccessStatusCode();
        using var chatJson = JsonDocument.Parse(await chat.Content.ReadAsStringAsync());
        var offer = chatJson.RootElement.GetProperty("refundOffers")[0];
        Assert.Equal(("SP-DVAN-1006", 50.00m), (offer.GetProperty("orderNumber").GetString(), offer.GetProperty("amount").GetDecimal()));

        var request = await john.PostAsJsonAsync("/api/shop/refund-requests", new { orderNumber = "SP-DVAN-1006", amount = 50.00m, reason = "one cover arrived torn" });
        request.EnsureSuccessStatusCode();
        var requestId = (await request.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // The customer can't approve their own refund, and nothing has moved yet.
        Assert.Equal(HttpStatusCode.Forbidden, (await john.PostAsync($"/api/actions/{requestId}/approve", null)).StatusCode);
        await _write.DidNotReceiveWithAnyArgs().RefundCaptureAsync(default!, default!, default!, default);

        var emma = await SignInAsync("emma@sellerpilot.test");
        var pending = await emma.GetFromJsonAsync<JsonElement>("/api/actions?status=Pending");
        var card = pending.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == requestId);
        Assert.Equal("customer", card.GetProperty("requestedBy").GetString());

        var approve = await emma.PostAsync($"/api/actions/{requestId}/approve", null);
        approve.EnsureSuccessStatusCode();
        Assert.Equal("Executed", (await approve.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        await _write.Received(1).RefundCaptureAsync("30379916G6480042M", Arg.Is<PayPalRefundRequest>(r => r.Amount.Value == "50.00"), requestId.ToString(), Arg.Any<CancellationToken>());

        // The refund is in the books, and the customer sees the outcome.
        var order = await emma.GetFromJsonAsync<JsonElement>("/api/orders/SP-DVAN-1006");
        Assert.Equal(80.00m, order.GetProperty("refundsRecorded").GetDecimal());
        var mine = await john.GetFromJsonAsync<JsonElement>("/api/shop/refund-requests");
        Assert.Equal("Executed", mine[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Customers_see_only_their_own_orders_and_never_the_back_office()
    {
        var alice = await SignInAsync("alice.carter@example.com");

        var orders = await alice.GetFromJsonAsync<JsonElement>("/api/shop/orders");
        Assert.Equal(new[] { "SP-DVAN-1007" }, orders.EnumerateArray().Select(o => o.GetProperty("orderNumber").GetString()).ToArray());

        var someoneElses = await alice.PostAsJsonAsync("/api/shop/refund-requests", new { orderNumber = "SP-DVAN-1006", amount = 50m });
        Assert.Equal(HttpStatusCode.NotFound, someoneElses.StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsJsonAsync("/api/chat", new { message = "refund everything" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/audit")).StatusCode);

        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/shop/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/shop/products")).StatusCode);
    }

    [Fact]
    public async Task An_expensive_item_must_come_back_before_the_refund_can_be_approved()
    {
        var john = await SignInAsync("john.doe@example.com");
        var emma = await SignInAsync("emma@sellerpilot.test");

        var request = await john.PostAsJsonAsync("/api/shop/refund-requests", new { orderNumber = "SP-DVAN-1008", amount = 150.00m, reason = "too big for my wall" });
        request.EnsureSuccessStatusCode();
        var created = await request.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AwaitingReturn", created.GetProperty("status").GetString());
        var id = created.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Conflict, (await emma.PostAsync($"/api/actions/{id}/approve", null)).StatusCode);
        var received = await emma.PostAsync($"/api/actions/{id}/return-received", null);
        received.EnsureSuccessStatusCode();
        Assert.Equal("Pending", (await received.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }
}
