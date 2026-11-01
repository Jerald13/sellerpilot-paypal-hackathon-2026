using System.Text.Json;

namespace SellerPilot.Api.Llm;

public enum LlmRole
{
    User,
    Assistant,
}

public enum LlmStopReason
{
    /// <summary>The model finished its answer.</summary>
    EndTurn,

    /// <summary>The model wants tools to be run.</summary>
    ToolUse,

    /// <summary>The output limit was hit; the answer (and any tool call) may be incomplete.</summary>
    MaxTokens,

    /// <summary>The provider's safety system declined the request.</summary>
    Refusal,

    Other,
}

/// <summary>A tool the model may call. <see cref="InputSchema"/> is a JSON Schema object (type/properties/required).</summary>
public sealed record LlmToolDefinition(string Name, string Description, JsonElement InputSchema);

public sealed record LlmToolCall(string Id, string Name, JsonElement Arguments);

public sealed record LlmToolResult(string ToolCallId, string ToolName, string Content, bool IsError);

/// <summary>
/// One provider-neutral message. A user message has <see cref="Text"/> or <see cref="ToolResults"/>;
/// an assistant message has <see cref="Text"/> and/or <see cref="ToolCalls"/>.
/// </summary>
public sealed record LlmMessage(
    LlmRole Role,
    string? Text,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    IReadOnlyList<LlmToolResult>? ToolResults = null)
{
    /// <summary>
    /// The provider's own representation of an assistant turn (e.g. Claude content blocks with thinking
    /// signatures, Gemini parts with thought signatures). Replayed verbatim so the history stays append-only.
    /// </summary>
    public object? ProviderState { get; init; }

    public static LlmMessage User(string text) => new(LlmRole.User, text);

    public static LlmMessage FromToolResults(IReadOnlyList<LlmToolResult> results) => new(LlmRole.User, null, ToolResults: results);
}

public sealed record LlmRequest(string SystemPrompt, IReadOnlyList<LlmMessage> Messages, IReadOnlyList<LlmToolDefinition> Tools);

/// <summary>Token counts for one or more model calls. <see cref="InputTokens"/> excludes cache reads and cache writes.</summary>
public sealed record LlmUsage(long InputTokens, long OutputTokens, long CacheReadInputTokens, long CacheWriteInputTokens = 0)
{
    public static LlmUsage? Add(LlmUsage? a, LlmUsage? b) =>
        a is null ? b : b is null ? a : new(
            a.InputTokens + b.InputTokens,
            a.OutputTokens + b.OutputTokens,
            a.CacheReadInputTokens + b.CacheReadInputTokens,
            a.CacheWriteInputTokens + b.CacheWriteInputTokens);
}

public sealed record LlmResponse(
    LlmMessage Message,
    LlmStopReason StopReason,
    string Model,
    LlmUsage? Usage = null,
    string? RefusalCategory = null)
{
    public string Text => Message.Text ?? "";

    public IReadOnlyList<LlmToolCall> ToolCalls => Message.ToolCalls ?? [];
}

public interface ILlmClient
{
    string ProviderName { get; }

    string Model { get; }

    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}

/// <summary>The LLM provider failed (network, rate limit, bad key...). Message is safe to show.</summary>
public sealed class LlmException(string message, Exception? innerException = null, bool isTransient = false)
    : Exception(message, innerException)
{
    public bool IsTransient { get; } = isTransient;
}

/// <summary>No API key is configured for the selected provider.</summary>
public sealed class LlmConfigurationException(string message) : Exception(message);

/// <summary>Registered when no API key is set, so the app still starts and /api/health can report it.</summary>
public sealed class UnconfiguredLlmClient(string providerName, string model) : ILlmClient
{
    public string ProviderName => providerName;

    public string Model => model;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct) =>
        throw new LlmConfigurationException(
            $"No API key is configured for {providerName}. Set Llm:{providerName}:ApiKey with dotnet user-secrets or .env (see README).");
}
