using System.Text.Json;
using SellerPilot.Api.Guardrails;

namespace SellerPilot.Api.Agent.Tools;

/// <summary>Shapes of <see cref="Data.PendingAction.Payload"/> per action type.</summary>
public static class ActionPayloads
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed record RefundPayload(
    string? OrderId,
    string? CustomerName,
    decimal CapturedAmount,
    decimal RefundedBefore,
    decimal RemainingBefore,
    IReadOnlyList<SecurityFlag> SecurityFlags,
    bool ReturnRequired = false,
    string? OrderNumber = null);

public sealed record ReminderPayload(
    string? Subject,
    string Note,
    string InvoiceNumber,
    string? CustomerName,
    string? DueDate,
    int DaysOverdue);

public sealed record BooksCorrectionPayload(
    string OrderNumber,
    string EntryType,
    string? PayPalOrderId,
    string? CustomerName);
