using System.Globalization;
using System.Text.Json;
using SellerPilot.Api.Data;
using SellerPilot.Api.Guardrails;

namespace SellerPilot.Api.Agent.Tools;

public interface IAgentTool
{
    string Name { get; }

    /// <summary>Shown to the LLM; says what the tool does and when to use it.</summary>
    string Description { get; }

    /// <summary>JSON Schema object (type/properties/required) for the arguments.</summary>
    JsonElement InputSchema { get; }

    Task<ToolResult> ExecuteAsync(ToolArguments args, ToolContext context, CancellationToken ct);
}

/// <summary>State shared by the tool calls of one user message.</summary>
public sealed class ToolContext
{
    public required Guid ConversationId { get; init; }

    public List<PendingAction> ProposedActions { get; } = [];

    public SecurityFlagCollector SecurityFlags { get; } = new();
}

/// <summary>What a tool returns to the LLM. <see cref="Data"/> is serialized to JSON.</summary>
public sealed record ToolResult(object Data, bool IsError = false)
{
    public static ToolResult Ok(object data) => new(data);

    public static ToolResult Error(string message, IEnumerable<string>? reasons = null) =>
        new(new ToolError(message, reasons?.ToList()), IsError: true);
}

public sealed record ToolError(string Error, IReadOnlyList<string>? Reasons);

/// <summary>The LLM sent arguments that fail validation. The message goes back to the LLM so it can correct itself.</summary>
public sealed class ToolArgumentException(string message) : Exception(message);

/// <summary>
/// Strict accessors over the LLM's tool arguments. Every value is checked before use (CLAUDE.md rule 4).
/// </summary>
public sealed class ToolArguments
{
    private readonly JsonElement _root;

    public ToolArguments(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ToolArgumentException("Arguments must be a JSON object.");
        }

        _root = root;
    }

    public JsonElement Raw => _root;

    public string RequiredString(string name, int maxLength) =>
        OptionalString(name, maxLength) ?? throw new ToolArgumentException($"'{name}' is required.");

    public string? OptionalString(string name, int maxLength)
    {
        if (!_root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ToolArgumentException($"'{name}' must be a string.");
        }

        var text = value.GetString()!.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length > maxLength)
        {
            throw new ToolArgumentException($"'{name}' must be at most {maxLength} characters.");
        }

        return text;
    }

    public string RequiredResourceId(string name)
    {
        var raw = RequiredString(name, 64);
        return PayPalIds.TryNormalizeResourceId(raw, out var id)
            ? id
            : throw new ToolArgumentException($"'{name}' is not a valid PayPal ID (expected 10-30 letters/digits, e.g. 5O190127TN364715T).");
    }

    public string RequiredInvoiceId(string name)
    {
        var raw = RequiredString(name, 64);
        return PayPalIds.TryNormalizeInvoiceId(raw, out var id)
            ? id
            : throw new ToolArgumentException($"'{name}' is not a valid PayPal invoice ID (expected the form INV2-XXXX-XXXX-XXXX-XXXX).");
    }

    /// <summary>Accepts a JSON number or a numeric string ("45.00"). Never uses floating point.</summary>
    public decimal? OptionalDecimal(string name, decimal min, decimal max)
    {
        if (!_root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        decimal result;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            result = number;
        }
        else if (value.ValueKind == JsonValueKind.String
                 && decimal.TryParse(value.GetString(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
        {
            result = parsed;
        }
        else
        {
            throw new ToolArgumentException($"'{name}' must be a number.");
        }

        if (result < min || result > max)
        {
            throw new ToolArgumentException($"'{name}' must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.");
        }

        return result;
    }

    public int OptionalInt(string name, int defaultValue, int min, int max)
    {
        if (!_root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return defaultValue;
        }

        int result;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            result = number;
        }
        else if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            result = parsed;
        }
        else
        {
            throw new ToolArgumentException($"'{name}' must be a whole number.");
        }

        if (result < min || result > max)
        {
            throw new ToolArgumentException($"'{name}' must be between {min} and {max}.");
        }

        return result;
    }

    public bool OptionalBool(string name, bool defaultValue)
    {
        if (!_root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => throw new ToolArgumentException($"'{name}' must be true or false."),
        };
    }

    public string? OptionalCurrency(string name)
    {
        var raw = OptionalString(name, 3)?.ToUpperInvariant();
        if (raw is null)
        {
            return null;
        }

        return PayPal.Models.Money.IsCurrencyCode(raw)
            ? raw
            : throw new ToolArgumentException($"'{name}' must be a 3-letter currency code such as USD.");
    }
}

public static class ToolSchema
{
    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
