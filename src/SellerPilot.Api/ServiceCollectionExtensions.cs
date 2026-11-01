using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SellerPilot.Api.Agent;
using SellerPilot.Api.Agent.Tools;
using SellerPilot.Api.Approvals;
using SellerPilot.Api.Audit;
using SellerPilot.Api.Configuration;
using SellerPilot.Api.Data;
using SellerPilot.Api.Guardrails;
using SellerPilot.Api.Llm;
using SellerPilot.Api.PayPal;

namespace SellerPilot.Api;

public static class ServiceCollectionExtensions
{
    /// <summary>Shared by the API and the eval runner so both use exactly the same agent wiring.</summary>
    public static IServiceCollection AddSellerPilot(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSellerPilotPayPal(configuration);
        services.AddOptions<LlmOptions>()
            .Bind(configuration.GetSection(LlmOptions.SectionName))
            .Validate(o => o.TimeoutSeconds is > 0 and <= 600, "Llm:TimeoutSeconds must be between 1 and 600.")
            .ValidateOnStart();
        services.AddOptions<AgentOptions>()
            .Bind(configuration.GetSection(AgentOptions.SectionName))
            .Validate(o => o.MaxToolIterations is >= 1 and <= 10, "Agent:MaxToolIterations must be between 1 and 10.")
            .ValidateOnStart();
        services.AddOptions<GuardrailOptions>()
            .Bind(configuration.GetSection(GuardrailOptions.SectionName))
            .Validate(o => o.MaxRefundAmount.Values.All(v => v > 0), "Guardrails:MaxRefundAmount values must be positive.")
            .ValidateOnStart();

        // LLM
        services.AddHttpClient(GeminiLlmClient.HttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
            client.BaseAddress = new Uri(options.Gemini.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });
        services.AddSingleton(CreateLlmClient);

        // Database and audit
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(
            configuration.GetConnectionString("SellerPilot") ?? "Data Source=sellerpilot.db"));
        services.AddScoped<IAuditService, AuditService>();

        // Guardrails
        services.AddSingleton<IInjectionDetector, InjectionDetector>();
        services.AddSingleton<IUntrustedTextSanitizer, UntrustedTextSanitizer>();
        services.AddSingleton<IRefundPolicy, RefundPolicy>();
        services.AddSingleton<IRefundStateLoader, RefundStateLoader>();

        // Agent: read tools first, then the two write (propose-only) tools. Order is fixed, so the tool list
        // sent to the LLM is identical on every request.
        services.AddScoped<IAgentTool, GetOrderTool>();
        services.AddScoped<IAgentTool, SearchTransactionsTool>();
        services.AddScoped<IAgentTool, ListUnpaidInvoicesTool>();
        services.AddScoped<IAgentTool, ProposeRefundTool>();
        services.AddScoped<IAgentTool, ProposeInvoiceReminderTool>();
        services.AddSingleton<IConversationStore, InMemoryConversationStore>();
        services.AddScoped<IAgentService, AgentService>();

        services.AddScoped<IApprovalService, ApprovalService>();
        return services;
    }

    /// <summary>PayPal sandbox options (validated), the OAuth token cache and the typed client. Also used by the seed tool.</summary>
    public static IServiceCollection AddSellerPilotPayPal(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<PayPalOptions>()
            .Bind(configuration.GetSection(PayPalOptions.SectionName))
            .Validate(o => PayPalOptions.IsSandboxUrl(o.BaseUrl), $"PayPal:BaseUrl must be {PayPalOptions.SandboxBaseUrl}. SellerPilot only talks to the PayPal sandbox.")
            .Validate(o => o.TimeoutSeconds is > 0 and <= 120, "PayPal:TimeoutSeconds must be between 1 and 120.")
            .Validate(o => o.MaxRetries is >= 0 and <= 5, "PayPal:MaxRetries must be between 0 and 5.")
            .ValidateOnStart();

        services.AddHttpClient(PayPalTokenProvider.HttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<PayPalOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });
        services.AddSingleton<IPayPalTokenProvider, PayPalTokenProvider>();
        services.AddSingleton<PayPalClient>();
        services.AddSingleton<IPayPalReadClient>(sp => sp.GetRequiredService<PayPalClient>());
        services.AddSingleton<IPayPalWriteClient>(sp => sp.GetRequiredService<PayPalClient>());
        return services;
    }

    public static async Task MigrateSellerPilotDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(ct);
    }

    private static ILlmClient CreateLlmClient(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
        var timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        switch (options.Provider)
        {
            case LlmProvider.Gemini:
            {
                var gemini = options.Gemini;
                var key = FirstNonEmpty(gemini.ApiKey, Environment.GetEnvironmentVariable("GEMINI_API_KEY"));
                if (key is null)
                {
                    return new UnconfiguredLlmClient("Gemini", gemini.Model);
                }

                gemini.ApiKey = key;
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(GeminiLlmClient.HttpClientName);
                return new GeminiLlmClient(http, gemini, sp.GetRequiredService<ILogger<GeminiLlmClient>>());
            }

            default:
            {
                var claude = options.Claude;
                var key = FirstNonEmpty(claude.ApiKey, Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
                if (key is null)
                {
                    return new UnconfiguredLlmClient("Claude", claude.Model);
                }

                claude.ApiKey = key;
                return new ClaudeLlmClient(claude, timeout, sp.GetRequiredService<ILogger<ClaudeLlmClient>>());
            }
        }
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
