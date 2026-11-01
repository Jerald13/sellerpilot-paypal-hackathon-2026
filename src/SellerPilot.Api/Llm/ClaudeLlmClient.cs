using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using SellerPilot.Api.Configuration;

namespace SellerPilot.Api.Llm;

/// <summary>
/// Claude via the official Anthropic SDK (beta Messages endpoint, for server-side refusal fallbacks).
/// No sampling parameters (rejected by current models); thinking is adaptive and controlled by effort.
/// Assistant turns are replayed exactly as received (including thinking signatures) to keep history append-only.
/// </summary>
public sealed class ClaudeLlmClient : ILlmClient
{
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    private readonly AnthropicClient _client;
    private readonly ClaudeOptions _options;
    private readonly ILogger<ClaudeLlmClient> _logger;
    private readonly Lock _toolsLock = new();
    private IReadOnlyList<LlmToolDefinition>? _toolSource;
    private List<BetaToolUnion> _tools = [];

    /// <param name="httpClient">Optional; tests pass one with a fake handler. Its own timeout must not be shorter than <paramref name="timeout"/>.</param>
    public ClaudeLlmClient(ClaudeOptions options, TimeSpan timeout, ILogger<ClaudeLlmClient> logger, HttpClient? httpClient = null)
    {
        _options = options;
        _logger = logger;
        _client = httpClient is null
            ? new AnthropicClient { ApiKey = options.ApiKey, Timeout = timeout, MaxRetries = 2 }
            : new AnthropicClient { ApiKey = options.ApiKey, Timeout = timeout, MaxRetries = 2, HttpClient = httpClient };
    }

    public string ProviderName => "Claude";

    public string Model => _options.Model;

    /// <summary>Server-side fallback ("default" mode) is offered for these models; Haiku has none.</summary>
    private bool UseFallback =>
        _options.UseServerSideFallback
        && (_options.Model.StartsWith("claude-opus-5", StringComparison.Ordinal)
            || _options.Model.StartsWith("claude-fable-5", StringComparison.Ordinal)
            || _options.Model == "claude-sonnet-5-5");

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = _options.MaxTokens,
            System = request.SystemPrompt,
            Messages = BuildMessages(request.Messages),
            Tools = GetTools(request.Tools),
            OutputConfig = new BetaOutputConfig { Effort = ParseEffort(_options.Effort) },
            // Automatic prompt caching of the stable prefix (system + tools + earlier turns).
            CacheControl = new BetaCacheControlEphemeral(),
        };

        if (UseFallback)
        {
            parameters = new MessageCreateParams
            {
                Model = parameters.Model,
                MaxTokens = parameters.MaxTokens,
                System = parameters.System,
                Messages = parameters.Messages,
                Tools = parameters.Tools,
                OutputConfig = parameters.OutputConfig,
                CacheControl = parameters.CacheControl,
                Betas = [FallbackBeta],
                Fallbacks = new Default(),
            };
        }

        BetaMessage response;
        try
        {
            response = await _client.Beta.Messages.Create(parameters, ct);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new LlmException("Claude rate limit reached. Wait a moment and try again.", ex, isTransient: true);
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new LlmException("The Claude API key was rejected. Check Llm:Claude:ApiKey.", ex);
        }
        catch (AnthropicBadRequestException ex)
        {
            throw new LlmException($"Claude rejected the request: {ex.Message}", ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw new LlmException("The Claude API is temporarily unavailable.", ex, isTransient: true);
        }
        catch (AnthropicIOException ex)
        {
            throw new LlmException("Could not reach the Claude API.", ex, isTransient: true);
        }
        catch (AnthropicApiException ex)
        {
            throw new LlmException($"Claude API error: {ex.Message}", ex);
        }

        return ToLlmResponse(response);
    }

    private LlmResponse ToLlmResponse(BetaMessage response)
    {
        var blocks = response.Content.ToList();

        // After a server-side fallback, blocks before the last "fallback" marker belong to the declined attempt:
        // only their text is echoed back (see Anthropic docs, "Echoing fallback turns back").
        var boundary = -1;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].TryPickFallback(out _))
            {
                boundary = i;
            }
        }

        var replay = new List<BetaContentBlockParam>();
        var text = new List<string>();
        var calls = new List<LlmToolCall>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.TryPickText(out var textBlock))
            {
                replay.Add(new BetaTextBlockParam { Text = textBlock.Text });
                if (i > boundary)
                {
                    text.Add(textBlock.Text);
                }
            }
            else if (i < boundary)
            {
                continue;
            }
            else if (block.TryPickThinking(out var thinking))
            {
                // The signature must be preserved exactly; the API rejects tampered thinking blocks.
                replay.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
            }
            else if (block.TryPickRedactedThinking(out var redacted))
            {
                replay.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
            }
            else if (block.TryPickToolUse(out var toolUse))
            {
                replay.Add(new BetaToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                calls.Add(new LlmToolCall(toolUse.ID, toolUse.Name, JsonSerializer.SerializeToElement(toolUse.Input)));
            }
        }

        var stopReason = MapStopReason(response.StopReason?.Raw());
        string? refusalCategory = null;
        if (stopReason == LlmStopReason.Refusal)
        {
            refusalCategory = response.StopDetails?.Category?.Raw();
            _logger.LogWarning("Claude declined the request (category {Category})", refusalCategory ?? "none");
        }

        var message = new LlmMessage(LlmRole.Assistant, text.Count > 0 ? string.Join("\n", text) : null, calls.Count > 0 ? calls : null)
        {
            ProviderState = replay,
        };

        var usage = new LlmUsage(
            response.Usage.InputTokens,
            response.Usage.OutputTokens,
            response.Usage.CacheReadInputTokens ?? 0,
            response.Usage.CacheCreationInputTokens ?? 0);

        return new LlmResponse(message, stopReason, response.Model.Raw(), usage, refusalCategory);
    }

    private static List<BetaMessageParam> BuildMessages(IReadOnlyList<LlmMessage> messages)
    {
        var result = new List<BetaMessageParam>(messages.Count);
        foreach (var message in messages)
        {
            if (message.Role == LlmRole.Assistant)
            {
                var content = message.ProviderState as List<BetaContentBlockParam> ?? RebuildAssistantContent(message);
                result.Add(new BetaAssistantMessageParam { Content = content });
            }
            else if (message.ToolResults is { Count: > 0 } toolResults)
            {
                // All results of one round go back in a single user message.
                var content = toolResults
                    .Select(r => (BetaContentBlockParam)new BetaToolResultBlockParam
                    {
                        ToolUseID = r.ToolCallId,
                        Content = r.Content,
                        IsError = r.IsError,
                    })
                    .ToList();
                result.Add(new BetaUserMessageParam { Content = content });
            }
            else
            {
                result.Add(new BetaUserMessageParam { Content = message.Text ?? "" });
            }
        }

        return result;
    }

    /// <summary>Only used if a turn came from another provider; normally ProviderState is replayed.</summary>
    private static List<BetaContentBlockParam> RebuildAssistantContent(LlmMessage message)
    {
        var content = new List<BetaContentBlockParam>();
        if (!string.IsNullOrEmpty(message.Text))
        {
            content.Add(new BetaTextBlockParam { Text = message.Text });
        }

        foreach (var call in message.ToolCalls ?? [])
        {
            var input = call.Arguments.ValueKind == JsonValueKind.Object
                ? call.Arguments.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : [];
            content.Add(new BetaToolUseBlockParam { ID = call.Id, Name = call.Name, Input = input });
        }

        return content;
    }

    private List<BetaToolUnion> GetTools(IReadOnlyList<LlmToolDefinition> definitions)
    {
        lock (_toolsLock)
        {
            if (!ReferenceEquals(_toolSource, definitions))
            {
                _tools = definitions.Select(ToTool).ToList();
                _toolSource = definitions;
            }

            return _tools;
        }
    }

    private static BetaToolUnion ToTool(LlmToolDefinition definition)
    {
        var schema = definition.InputSchema;
        var properties = schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
            ? props.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
            : new Dictionary<string, JsonElement>();
        var required = schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
            ? req.EnumerateArray().Select(r => r.GetString()!).ToList()
            : [];

        return new BetaTool
        {
            Name = definition.Name,
            Description = definition.Description,
            InputSchema = new() { Properties = properties, Required = required },
        };
    }

    private static Effort ParseEffort(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };

    private static LlmStopReason MapStopReason(string? stopReason) => stopReason switch
    {
        "end_turn" or "stop_sequence" => LlmStopReason.EndTurn,
        "tool_use" => LlmStopReason.ToolUse,
        "max_tokens" or "model_context_window_exceeded" => LlmStopReason.MaxTokens,
        "refusal" => LlmStopReason.Refusal,
        _ => LlmStopReason.Other,
    };
}
