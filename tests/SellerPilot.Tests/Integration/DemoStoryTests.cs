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
using SellerPilot.Tests.TestSupport;

namespace SellerPilot.Tests.Integration;

/// <summary>
/// The demo story over real HTTP, DI, EF migrations and JSON: chat -> proposal -> approve -> PayPal refund -> audit.
/// Only the LLM and PayPal are faked.
/// </summary>
public sealed class DemoStoryTests : IDisposable
{
    private const string SellerPassword = "it-seller-password";
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"sellerpilot-it-{Guid.NewGuid():N}.db");
    private readonly ScriptedLlmClient _llm = new();
    private readonly IPayPalReadClient _read = Substitute.For<IPayPalReadClient>();
    private readonly IPayPalWriteClient _write = Substitute.For<IPayPalWriteClient>();
    private readonly WebApplicationFactory<ApiExceptionHandler> _factory;

    public DemoStoryTests()
    {
        _factory = new WebApplicationFactory<ApiExceptionHandler>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:SellerPilot", $"Data Source={_databasePath};Pooling=False");
            builder.UseSetting("Shop:DemoPassword", SellerPassword);
            // No shop data in these tests (and no dependency on the developer's local seed-output.json).
            builder.UseSetting("Shop:SeedFile", Path.Combine(Path.GetTempPath(), $"no-seed-{Guid.NewGuid():N}.json"));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ILlmClient>(_llm);
                services.AddSingleton(_read);
                services.AddSingleton(_write);
            });
        });

        _read.GetOrderAsync(PayPalFixtures.OrderId, Arg.Any<CancellationToken>())
            .Returns(PayPalFixtures.Parse<PayPalOrder>(PayPalFixtures.Order()));
        _read.GetCaptureAsync(PayPalFixtures.CaptureId, Arg.Any<CancellationToken>())
            .Returns(PayPalFixtures.Parse<PayPalCapture>(PayPalFixtures.Capture()));
        _write.RefundCaptureAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(PayPalFixtures.Parse<PayPalRefund>(PayPalFixtures.Refund("1JU08902781691411", "45.00")));
    }

    public void Dispose()
    {
        _factory.Dispose();
        File.Delete(_databasePath);
    }

    private async Task<HttpClient> SellerClientAsync()
    {
        var http = _factory.CreateClient();
        var login = await http.PostAsJsonAsync("/api/auth/login", new { email = "emma@sellerpilot.test", password = SellerPassword });
        login.EnsureSuccessStatusCode();
        return http;
    }

    [Fact]
    public async Task Refund_is_proposed_by_chat_and_executed_only_after_approval()
    {
        _llm.ThenToolCalls(("GetOrder", $$"""{"order_id":"{{PayPalFixtures.OrderId}}"}"""))
            .ThenToolCalls(("ProposeRefund", $$"""{"capture_id":"{{PayPalFixtures.CaptureId}}","reason":"The scarf arrived damaged."}"""))
            .ThenText("I've proposed a full refund of USD 45.00. It is waiting for your approval.");
        var http = await SellerClientAsync();

        var chat = await http.PostAsJsonAsync("/api/chat", new { message = $"Customer for order {PayPalFixtures.OrderId} wants a refund. What should I do?" });
        chat.EnsureSuccessStatusCode();
        using var chatJson = JsonDocument.Parse(await chat.Content.ReadAsStringAsync());
        var proposal = chatJson.RootElement.GetProperty("proposedActions")[0];
        Assert.Equal("Refund", proposal.GetProperty("type").GetString());
        Assert.Equal("Pending", proposal.GetProperty("status").GetString());
        Assert.Equal(45.00m, proposal.GetProperty("amount").GetDecimal());
        var actionId = proposal.GetProperty("id").GetGuid();

        // Nothing moved yet.
        await _write.DidNotReceiveWithAnyArgs().RefundCaptureAsync(default!, default!, default!, default);
        var pending = await http.GetFromJsonAsync<JsonElement>("/api/actions?status=Pending");
        Assert.Equal(1, pending.GetArrayLength());

        var approve = await http.PostAsync($"/api/actions/{actionId}/approve", null);
        approve.EnsureSuccessStatusCode();
        using var approved = JsonDocument.Parse(await approve.Content.ReadAsStringAsync());
        Assert.Equal("Executed", approved.RootElement.GetProperty("status").GetString());
        Assert.Equal("1JU08902781691411", approved.RootElement.GetProperty("payPalResultId").GetString());
        await _write.Received(1).RefundCaptureAsync(PayPalFixtures.CaptureId, Arg.Any<PayPalRefundRequest>(), actionId.ToString(), Arg.Any<CancellationToken>());

        var again = await http.PostAsync($"/api/actions/{actionId}/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var audit = await http.GetFromJsonAsync<JsonElement>("/api/audit");
        var events = audit.EnumerateArray().Select(e => e.GetProperty("event").GetString()).ToList();
        Assert.Contains("ToolCalled", events);
        Assert.Contains("ActionProposed", events);
        Assert.Contains("ActionApproved", events);
        Assert.Contains("ActionExecuted", events);
    }

    [Fact]
    public async Task Rejected_proposals_never_reach_PayPal()
    {
        _llm.ThenToolCalls(("ProposeRefund", $$"""{"capture_id":"{{PayPalFixtures.CaptureId}}","amount":10,"reason":"Goodwill"}"""))
            .ThenText("Proposed a USD 10.00 refund for your approval.");
        var http = await SellerClientAsync();

        using var chat = JsonDocument.Parse(await (await http.PostAsJsonAsync("/api/chat", new { message = "Refund USD 10 as goodwill" })).Content.ReadAsStringAsync());
        var actionId = chat.RootElement.GetProperty("proposedActions")[0].GetProperty("id").GetGuid();

        var reject = await http.PostAsync($"/api/actions/{actionId}/reject", null);

        reject.EnsureSuccessStatusCode();
        await _write.DidNotReceiveWithAnyArgs().RefundCaptureAsync(default!, default!, default!, default);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/api/actions/{actionId}/approve", null)).StatusCode);
    }

    [Fact]
    public async Task Invalid_requests_return_problem_details()
    {
        var http = await SellerClientAsync();

        var empty = await http.PostAsJsonAsync("/api/chat", new { message = "" });
        var unknown = await http.PostAsync($"/api/actions/{Guid.NewGuid()}/approve", null);

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("application/problem+json", empty.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task The_back_office_needs_the_seller_to_sign_in()
    {
        var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/chat", new { message = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/actions/{Guid.NewGuid()}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/orders")).StatusCode);

        var wrong = await anonymous.PostAsJsonAsync("/api/auth/login", new { email = "emma@sellerpilot.test", password = "guess" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var seller = await SellerClientAsync();
        var me = await seller.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("seller", me.GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, (await seller.GetAsync("/api/audit")).StatusCode);

        await seller.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await seller.GetAsync("/api/audit")).StatusCode);
    }
}
