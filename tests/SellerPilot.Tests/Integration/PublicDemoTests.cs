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
using SellerPilot.Api.Shop;
using SellerPilot.Tests.TestSupport;

namespace SellerPilot.Tests.Integration;

/// <summary>
/// The hosted public demo: one-click sign-in as the demo accounts and a per-address chat limit.
/// Both are off unless Shop:PublicDemo is set; a local install keeps password sign-in and unlimited chat.
/// </summary>
public sealed class PublicDemoTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"sellerpilot-demo-it-{Guid.NewGuid():N}.db");
    private readonly string _seedPath = Path.Combine(Path.GetTempPath(), $"sellerpilot-demo-seed-{Guid.NewGuid():N}.json");
    private readonly ScriptedLlmClient _llm = new();
    private readonly SandboxPayPal _paypal = new();
    private readonly List<WebApplicationFactory<ApiExceptionHandler>> _factories = [];

    public PublicDemoTests()
    {
        File.WriteAllText(_seedPath, JsonSerializer.Serialize(ShopFixtures.Seed(), SeedOutputFile.Json));
    }

    public void Dispose()
    {
        _factories.ForEach(f => f.Dispose());
        File.Delete(_databasePath);
        File.Delete(_seedPath);
    }

    private HttpClient Client(bool publicDemo, int chatPerMinute = 10, string environment = "Development")
    {
        var factory = new WebApplicationFactory<ApiExceptionHandler>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:SellerPilot", $"Data Source={_databasePath};Pooling=False");
            builder.UseSetting("Shop:SeedFile", _seedPath);
            builder.UseSetting("Shop:PublicDemo", publicDemo ? "true" : "false");
            builder.UseSetting("Shop:PublicDemoChatPerMinute", chatPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(PayPalFixtures.Now));
                services.AddSingleton<ILlmClient>(_llm);
                services.AddSingleton(_paypal.Client);
                services.AddSingleton(Substitute.For<IPayPalWriteClient>());
            });
        });
        _factories.Add(factory);
        return factory.CreateClient();
    }

    [Fact]
    public async Task The_sign_in_page_can_list_the_demo_accounts()
    {
        var http = Client(publicDemo: false);

        using var json = JsonDocument.Parse(await http.GetStringAsync("/api/auth/demo"));

        Assert.False(json.RootElement.GetProperty("publicDemo").GetBoolean());
        var emails = json.RootElement.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("email").GetString()).ToList();
        Assert.Equal("emma@sellerpilot.test", emails[0]);
        Assert.Contains("john.doe@example.com", emails);
        Assert.DoesNotContain("password", json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Passwordless_sign_in_does_not_exist_outside_the_public_demo()
    {
        var http = Client(publicDemo: false);

        var login = await http.PostAsJsonAsync("/api/auth/demo-login", new { email = "emma@sellerpilot.test" });

        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/orders")).StatusCode);
    }

    [Fact]
    public async Task In_the_public_demo_visitors_sign_in_as_the_seller_or_a_customer_with_one_click()
    {
        var emma = Client(publicDemo: true);
        (await emma.PostAsJsonAsync("/api/auth/demo-login", new { email = "emma@sellerpilot.test" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await emma.GetAsync("/api/orders")).StatusCode);

        var john = Client(publicDemo: true);
        var login = await john.PostAsJsonAsync("/api/auth/demo-login", new { email = "John.Doe@example.com" });
        login.EnsureSuccessStatusCode();
        Assert.Equal("customer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, (await john.GetAsync("/api/shop/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await john.GetAsync("/api/orders")).StatusCode);
    }

    [Fact]
    public async Task Unknown_accounts_cannot_use_the_demo_sign_in()
    {
        var http = Client(publicDemo: true);

        var login = await http.PostAsJsonAsync("/api/auth/demo-login", new { email = "someone@else.com" });

        Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/shop/orders")).StatusCode);
    }

    [Fact]
    public async Task The_public_demo_limits_chat_messages_per_visitor()
    {
        for (var i = 0; i < 3; i++)
        {
            _llm.ThenText("Hello! How can I help with your orders?");
        }

        var john = Client(publicDemo: true, chatPerMinute: 2);
        (await john.PostAsJsonAsync("/api/auth/demo-login", new { email = "john.doe@example.com" })).EnsureSuccessStatusCode();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add((await john.PostAsJsonAsync("/api/shop/chat", new { message = "Hi" })).StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task Visitors_to_the_hosted_demo_cannot_start_an_eval_run_and_are_told_why()
    {
        var emma = Client(publicDemo: true, environment: "Production");
        (await emma.PostAsJsonAsync("/api/auth/demo-login", new { email = "emma@sellerpilot.test" })).EnsureSuccessStatusCode();

        var start = await emma.PostAsJsonAsync("/api/eval/runs", new { category = (string?)null });

        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
        var title = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();
        Assert.Contains("protect the API credits", title);
        Assert.Empty(_llm.Requests);
    }
}
