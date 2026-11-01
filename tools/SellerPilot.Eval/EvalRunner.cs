using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SellerPilot.Api.Agent;
using SellerPilot.Api.Data;

namespace SellerPilot.Eval;

public sealed record EvalCaseResult(
    string Id,
    string Category,
    string Status,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Prompts,
    EvalObservation? Observation,
    long DurationMs)
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
    public const string Skip = "SKIP";
    public const string Error = "ERROR";
}

/// <summary>Runs each case through the real agent (real LLM, real sandbox reads) with a fresh conversation.</summary>
public sealed class EvalRunner(IServiceProvider services, RecordingWriteClient writes)
{
    public static readonly JsonSerializerOptions CaseJson = new(JsonSerializerDefaults.Web);

    public async Task<EvalCaseResult> RunAsync(JsonNode rawCase, JsonNode? seed, CancellationToken ct)
    {
        var missing = new List<string>();
        var json = EvalChecks.ApplyTemplates(rawCase.ToJsonString(), seed, missing);
        var evalCase = JsonSerializer.Deserialize<EvalCase>(json, CaseJson)!;
        if (missing.Count > 0)
        {
            return new EvalCaseResult(evalCase.Id, evalCase.Category, EvalCaseResult.Skip,
                [$"missing seed data: {string.Join(", ", missing.Distinct())} (run the seed tool, or fix seed-output.json)"],
                evalCase.AllTurns, null, 0);
        }

        var stopwatch = Stopwatch.StartNew();
        var writesBefore = writes.Attempts;
        await using var scope = services.CreateAsyncScope();
        var agent = scope.ServiceProvider.GetRequiredService<IAgentService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Each case starts with an empty proposal queue so cases don't influence each other.
        await db.PendingActions.ExecuteDeleteAsync(ct);

        var replies = new List<string>();
        var toolCalls = new List<ObservedToolCall>();
        var proposals = new List<ObservedProposal>();
        var flags = 0;
        Guid? conversationId = null;
        try
        {
            foreach (var turn in evalCase.AllTurns)
            {
                var result = await agent.HandleMessageAsync(conversationId, turn, ct);
                conversationId = result.ConversationId;
                replies.Add(result.Reply);
                toolCalls.AddRange(result.ToolCalls.Select(c => new ObservedToolCall(c.Name, c.Arguments, c.Succeeded, c.Error)));
                proposals.AddRange(result.ProposedActions.Select(a => new ObservedProposal(a.Type.ToString(), a.TargetId, a.Amount, a.Currency, a.Status.ToString())));
                flags += result.SecurityFlags.Count;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var partial = new EvalObservation(replies, toolCalls, proposals, flags, writes.Attempts - writesBefore, 0);
            return new EvalCaseResult(evalCase.Id, evalCase.Category, EvalCaseResult.Error, [$"{ex.GetType().Name}: {ex.Message}"], evalCase.AllTurns, partial, stopwatch.ElapsedMilliseconds);
        }

        var executed = await db.PendingActions.CountAsync(a => a.Status == ActionStatus.Executed || a.Status == ActionStatus.Approved, ct);
        var observation = new EvalObservation(replies, toolCalls, proposals, flags, writes.Attempts - writesBefore, executed);
        var failures = EvalChecks.Evaluate(evalCase.Expect, observation);
        return new EvalCaseResult(
            evalCase.Id,
            evalCase.Category,
            failures.Count == 0 ? EvalCaseResult.Pass : EvalCaseResult.Fail,
            failures,
            evalCase.AllTurns,
            observation,
            stopwatch.ElapsedMilliseconds);
    }
}
