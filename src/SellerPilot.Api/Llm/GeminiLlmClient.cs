using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SellerPilot.Api.Configuration;

namespace SellerPilot.Api.Llm;

/// <summary>
/// Gemini via the generateContent REST API with function calling.
/// Model turns are replayed exactly as received (including thoughtSignature parts), as Gemini requires.
/// </summary>
public sealed class GeminiLlmClient(HttpClient http, GeminiOptions options, ILogger<GeminiLlmClient> logger) : ILlmClient
{
    public const string HttpClientName = "Gemini";
    private const string SyntheticIdPrefix = "gemini-call-";
    private const int MaxAttempts = 3;

    private static readonly HashSet<string> RefusalFinishReasons =
        ["SAFETY", "RECITATION", "BLOCKLIST", "PROHIBITED_CONTENT", "SPII", "IMAGE_SAFETY"];

    public string ProviderName => "Gemini";

    public string Model => options.Model;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var body = BuildRequestBody(request, options);
        var json = await SendWithRetryAsync(body, ct);
        return ParseResponse(json, options.Model, logger);
    }

    public static JsonObject BuildRequestBody(LlmRequest request, GeminiOptions options)
    {
        var generationConfig = new JsonObject { ["maxOutputTokens"] = options.MaxOutputTokens };
        if (options.Temperature is { } temperature)
        {
            generationConfig["temperature"] = temperature;
        }

        return new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = request.SystemPrompt }) },
            ["contents"] = BuildContents(request.Messages),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["functionDeclarations"] = new JsonArray(request.Tools
                    .Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = JsonNode.Parse(t.InputSchema.GetRawText()),
                    })
                    .ToArray()),
            }),
            ["toolConfig"] = new JsonObject { ["functionCallingConfig"] = new JsonObject { ["mode"] = "AUTO" } },
            ["generationConfig"] = generationConfig,
        };
    }

    private static JsonArray BuildContents(IReadOnlyList<LlmMessage> messages)
    {
        var contents = new JsonArray();
        foreach (var message in messages)
        {
            if (message.Role == LlmRole.Assistant)
            {
                if (message.ProviderState is JsonElement raw)
                {
                    contents.Add(JsonNode.Parse(raw.GetRawText()));
                    continue;
                }

                var parts = new JsonArray();
                if (!string.IsNullOrEmpty(message.Text))
                {
                    parts.Add(new JsonObject { ["text"] = message.Text });
                }

                foreach (var call in message.ToolCalls ?? [])
                {
                    var functionCall = new JsonObject { ["name"] = call.Name, ["args"] = JsonNode.Parse(call.Arguments.GetRawText()) };
                    if (!call.Id.StartsWith(SyntheticIdPrefix, StringComparison.Ordinal))
                    {
                        functionCall["id"] = call.Id;
                    }

                    parts.Add(new JsonObject { ["functionCall"] = functionCall });
                }

                contents.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
            }
            else if (message.ToolResults is { Count: > 0 } results)
            {
                var parts = new JsonArray();
                foreach (var result in results)
                {
                    var functionResponse = new JsonObject
                    {
                        ["name"] = result.ToolName,
                        ["response"] = ToResponseObject(result),
                    };
                    if (!result.ToolCallId.StartsWith(SyntheticIdPrefix, StringComparison.Ordinal))
                    {
                        functionResponse["id"] = result.ToolCallId;
                    }

                    parts.Add(new JsonObject { ["functionResponse"] = functionResponse });
                }

                contents.Add(new JsonObject { ["role"] = "user", ["parts"] = parts });
            }
            else
            {
                contents.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = message.Text ?? "" }),
                });
            }
        }

        return contents;
    }

    /// <summary>functionResponse.response must be a JSON object.</summary>
    private static JsonObject ToResponseObject(LlmToolResult result)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(result.Content);
        }
        catch (JsonException)
        {
            parsed = JsonValue.Create(result.Content);
        }

        if (parsed is JsonObject obj)
        {
            return obj;
        }

        return new JsonObject { [result.IsError ? "error" : "result"] = parsed };
    }

    public static LlmResponse ParseResponse(string json, string model, ILogger? logger = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("promptFeedback", out var feedback)
            && feedback.TryGetProperty("blockReason", out var blockReason))
        {
            logger?.LogWarning("Gemini blocked the prompt ({BlockReason})", blockReason.GetString());
            return Refusal(model, blockReason.GetString());
        }

        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            return Refusal(model, "NO_CANDIDATES");
        }

        var candidate = candidates[0];
        var finishReason = candidate.TryGetProperty("finishReason", out var fr) ? fr.GetString() : null;
        if (finishReason is not null && RefusalFinishReasons.Contains(finishReason))
        {
            logger?.LogWarning("Gemini stopped with finish reason {FinishReason}", finishReason);
            return Refusal(model, finishReason);
        }

        var text = new StringBuilder();
        var calls = new List<LlmToolCall>();
        JsonElement? rawContent = null;
        if (candidate.TryGetProperty("content", out var content))
        {
            rawContent = content.Clone();
            if (content.TryGetProperty("parts", out var parts))
            {
                foreach (var part in parts.EnumerateArray())
                {
                    var isThought = part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True;
                    if (part.TryGetProperty("text", out var t) && !isThought)
                    {
                        text.Append(t.GetString());
                    }
                    else if (part.TryGetProperty("functionCall", out var fc))
                    {
                        var id = fc.TryGetProperty("id", out var idElement) && idElement.GetString() is { Length: > 0 } providedId
                            ? providedId
                            : SyntheticIdPrefix + calls.Count;
                        var args = fc.TryGetProperty("args", out var a) ? a.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                        calls.Add(new LlmToolCall(id, fc.GetProperty("name").GetString() ?? "", args));
                    }
                }
            }
        }

        var stopReason = finishReason switch
        {
            "MAX_TOKENS" => LlmStopReason.MaxTokens,
            "STOP" or null when calls.Count > 0 => LlmStopReason.ToolUse,
            "STOP" => LlmStopReason.EndTurn,
            _ => calls.Count > 0 ? LlmStopReason.ToolUse : LlmStopReason.Other,
        };

        LlmUsage? usage = null;
        if (root.TryGetProperty("usageMetadata", out var meta))
        {
            // Gemini's prompt count includes cached tokens; LlmUsage keeps them separate (as Claude reports them).
            var prompt = meta.TryGetProperty("promptTokenCount", out var p) ? p.GetInt64() : 0;
            var cachedTokens = meta.TryGetProperty("cachedContentTokenCount", out var cached) ? cached.GetInt64() : 0;
            usage = new LlmUsage(
                prompt - cachedTokens,
                meta.TryGetProperty("candidatesTokenCount", out var c) ? c.GetInt64() : 0,
                cachedTokens);
        }

        var message = new LlmMessage(LlmRole.Assistant, text.Length > 0 ? text.ToString() : null, calls.Count > 0 ? calls : null)
        {
            ProviderState = rawContent,
        };
        var modelVersion = root.TryGetProperty("modelVersion", out var mv) ? mv.GetString() ?? model : model;
        return new LlmResponse(message, stopReason, modelVersion, usage);
    }

    private static LlmResponse Refusal(string model, string? category) =>
        new(new LlmMessage(LlmRole.Assistant, null), LlmStopReason.Refusal, model, RefusalCategory: category);

    private async Task<string> SendWithRetryAsync(JsonObject body, CancellationToken ct)
    {
        var payload = body.ToJsonString();
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{Uri.EscapeDataString(options.Model)}:generateContent")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            // Header instead of ?key= so the key never appears in logged URLs.
            request.Headers.TryAddWithoutValidation("x-goog-api-key", options.ApiKey);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
                    continue;
                }

                throw new LlmException("Could not reach the Gemini API.", ex, isTransient: true);
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode)
                {
                    return text;
                }

                var transient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
                    or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
                if (transient && attempt < MaxAttempts)
                {
                    logger.LogWarning("Gemini returned {Status}; retrying (attempt {Attempt})", (int)response.StatusCode, attempt);
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                    continue;
                }

                throw new LlmException(
                    response.StatusCode switch
                    {
                        HttpStatusCode.TooManyRequests => "Gemini rate limit reached. Wait a moment and try again.",
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The Gemini API key was rejected. Check Llm:Gemini:ApiKey.",
                        HttpStatusCode.NotFound => $"Gemini model '{options.Model}' was not found. Check Llm:Gemini:Model.",
                        _ => $"Gemini API error {(int)response.StatusCode}: {ExtractErrorMessage(text)}",
                    },
                    isTransient: transient);
            }
        }
    }

    private static string ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message)
                ? message.GetString() ?? "unknown error"
                : "unknown error";
        }
        catch (JsonException)
        {
            return "unknown error";
        }
    }
}
