using System.Text.Json;
using SellerPilot.Api.Llm;

namespace SellerPilot.Tests.TestSupport;

/// <summary>A fake LLM that plays back scripted responses and snapshots every request it receives.</summary>
public sealed class ScriptedLlmClient : ILlmClient
{
    private readonly Queue<Func<LlmRequest, LlmResponse>> _script = new();
    private int _callCounter;

    public List<LlmRequest> Requests { get; } = [];

    public string ProviderName => "Scripted";

    public string Model => "scripted-model";

    /// <summary>Used when the script is empty.</summary>
    public Func<LlmRequest, LlmResponse>? Default { get; set; }

    public ScriptedLlmClient Then(Func<LlmRequest, LlmResponse> step)
    {
        _script.Enqueue(step);
        return this;
    }

    public ScriptedLlmClient ThenText(string text) => Then(_ => TextResponse(text));

    public ScriptedLlmClient ThenToolCalls(params (string Name, string Arguments)[] calls) => Then(_ => ToolResponse(calls));

    public ScriptedLlmClient ThenStop(LlmStopReason reason) =>
        Then(_ => new LlmResponse(new LlmMessage(LlmRole.Assistant, null), reason, Model));

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        // The agent keeps appending to the list it passed in, so take a snapshot.
        Requests.Add(request with { Messages = request.Messages.ToList() });
        var step = _script.Count > 0 ? _script.Dequeue() : Default ?? throw new InvalidOperationException("LLM script is empty.");
        return Task.FromResult(step(request));
    }

    public static LlmResponse TextResponse(string text) =>
        new(new LlmMessage(LlmRole.Assistant, text), LlmStopReason.EndTurn, "scripted-model");

    public LlmResponse ToolResponse(params (string Name, string Arguments)[] calls)
    {
        var toolCalls = calls
            .Select(c => new LlmToolCall($"call_{++_callCounter}", c.Name, JsonDocument.Parse(c.Arguments).RootElement.Clone()))
            .ToList();
        return new LlmResponse(new LlmMessage(LlmRole.Assistant, null, toolCalls), LlmStopReason.ToolUse, Model);
    }
}
