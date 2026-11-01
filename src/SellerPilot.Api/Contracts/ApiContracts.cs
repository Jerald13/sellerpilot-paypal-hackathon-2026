using System.Text.Json;
using SellerPilot.Api.Agent;
using SellerPilot.Api.Data;
using SellerPilot.Api.Guardrails;

namespace SellerPilot.Api.Contracts;

public sealed record ChatRequest(Guid? ConversationId, string? Message);

public sealed record ChatResponse(
    Guid ConversationId,
    string Reply,
    IReadOnlyList<PendingActionDto> ProposedActions,
    IReadOnlyList<ToolCallDto> ToolCalls,
    IReadOnlyList<SecurityFlag> SecurityFlags,
    bool StepLimitReached,
    bool Refused)
{
    public static ChatResponse From(AgentTurnResult turn) => new(
        turn.ConversationId,
        turn.Reply,
        turn.ProposedActions.Select(PendingActionDto.From).ToList(),
        turn.ToolCalls.Select(c => new ToolCallDto(c.Name, c.Arguments, c.Succeeded, c.Error, c.DurationMs)).ToList(),
        turn.SecurityFlags,
        turn.StepLimitReached,
        turn.Refused);
}

public sealed record ToolCallDto(string Name, JsonElement Arguments, bool Succeeded, string? Error, long DurationMs);

public sealed record PendingActionDto(
    Guid Id,
    ActionType Type,
    string TargetId,
    decimal? Amount,
    string? Currency,
    string Reason,
    string Summary,
    JsonElement? Details,
    ActionStatus Status,
    string? PayPalResultId,
    string? Error,
    DateTime CreatedAt,
    DateTime? DecidedAt,
    string RequestedBy,
    int? CustomerId,
    int? ShopOrderId)
{
    public static PendingActionDto From(PendingAction a) => new(
        a.Id,
        a.Type,
        a.TargetId,
        a.Amount,
        a.Currency,
        a.Reason,
        a.Summary,
        ParseJson(a.Payload),
        a.Status,
        a.PayPalResultId,
        a.Error,
        a.CreatedAt,
        a.DecidedAt,
        a.RequestedBy,
        a.CustomerId,
        a.ShopOrderId);

    internal static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record AuditLogDto(long Id, DateTime Timestamp, string Actor, string Event, JsonElement? Details)
{
    public static AuditLogDto From(AuditLog log) =>
        new(log.Id, log.Timestamp, log.Actor, log.Event, PendingActionDto.ParseJson(log.Details));
}
