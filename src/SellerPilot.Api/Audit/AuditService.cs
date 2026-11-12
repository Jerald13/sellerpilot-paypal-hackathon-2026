using System.Text.Json;
using System.Text.Json.Serialization;
using SellerPilot.Api.Data;

namespace SellerPilot.Api.Audit;

public static class AuditActors
{
    public const string Agent = "agent";
    public const string Seller = "seller";
    public const string System = "system";
    public const string Customer = "customer";
}

public static class AuditEvents
{
    public const string ToolCalled = "ToolCalled";
    public const string ToolFailed = "ToolFailed";
    public const string ActionProposed = "ActionProposed";
    public const string GuardrailBlocked = "GuardrailBlocked";
    public const string InjectionSuspected = "InjectionSuspected";
    public const string ActionApproved = "ActionApproved";
    public const string ActionRejected = "ActionRejected";
    public const string ActionExecuted = "ActionExecuted";
    public const string ActionFailed = "ActionFailed";
    public const string ActionNotSent = "ActionNotSent";
    public const string AgentStepLimitReached = "AgentStepLimitReached";
    public const string LlmRefusal = "LlmRefusal";
    public const string LedgerPosted = "LedgerPosted";
    public const string LedgerPostingFailed = "LedgerPostingFailed";
    public const string ReturnReceived = "ReturnReceived";
    public const string RefundRequested = "RefundRequested";
    public const string ReconciliationRun = "ReconciliationRun";
    public const string SignedIn = "SignedIn";
    public const string SignInFailed = "SignInFailed";
}

public interface IAuditService
{
    /// <summary>Appends an audit entry. <paramref name="details"/> is serialized to JSON; never pass secrets.</summary>
    Task LogAsync(string actor, string eventName, object details, CancellationToken ct = default);
}

public sealed class AuditService(AppDbContext db, TimeProvider time, ILogger<AuditService> logger) : IAuditService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task LogAsync(string actor, string eventName, object details, CancellationToken ct = default)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = time.GetUtcNow().UtcDateTime,
            Actor = actor,
            Event = eventName,
            Details = JsonSerializer.Serialize(details, Json),
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Audit: {Actor} {Event}", actor, eventName);
    }
}
