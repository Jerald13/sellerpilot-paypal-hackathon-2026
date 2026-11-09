using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SellerPilot.Api.Auth;
using SellerPilot.Api.Evaluation;
using SellerPilot.Api.Llm;

namespace SellerPilot.Api.Controllers;

public sealed record StartEvalRequest(string? Category);

public sealed record EvalEstimateDto(int Cases, IReadOnlyDictionary<string, int> ByCategory, string Model, decimal? EstimatedUsdPerCase, decimal? EstimatedUsd, string Basis);

/// <summary>
/// The Eval tab: run eval/cases.json against the real model and sandbox, and read the results. Development only,
/// because every run costs real LLM tokens.
/// </summary>
[ApiController]
[Authorize(Policy = AuthPolicies.Seller)]
[Route("api/eval")]
public sealed partial class EvalController(EvalProcessRunner runner, ILlmClient llm, IWebHostEnvironment environment) : ControllerBase
{
    // Without a previous run: a typical case is ~3 model calls with ~4k input and ~400 output tokens each.
    private static readonly LlmUsage TypicalCase = new(12_000, 1_200, 0);

    [HttpGet("latest")]
    public async Task<IActionResult> Latest(CancellationToken ct)
    {
        var report = await ReadLatestAsync(ct);
        return Ok(new
        {
            Running = runner.IsRunning,
            runner.LastExitCode,
            Output = runner.IsRunning || runner.LastExitCode is not (null or 0) ? runner.RecentOutput : [],
            Report = report,
        });
    }

    [HttpGet("estimate")]
    public async Task<EvalEstimateDto> Estimate([FromQuery] string? category, CancellationToken ct)
    {
        var byCategory = await CountCasesAsync(ct);
        var cases = category is null ? byCategory.Values.Sum() : byCategory.GetValueOrDefault(category);

        // Prefer the average cost of the last finished run with the same model.
        var report = await ReadLatestAsync(ct);
        if (report is { } r
            && r.TryGetProperty("info", out var info) && info.TryGetProperty("model", out var model) && model.GetString() == llm.Model
            && r.TryGetProperty("totals", out var totals) && totals.TryGetProperty("costUsd", out var cost) && cost.ValueKind == JsonValueKind.Number
            && r.TryGetProperty("completed", out var completed) && completed.GetInt32() > 0)
        {
            var perCase = cost.GetDecimal() / completed.GetInt32();
            return new EvalEstimateDto(cases, byCategory, llm.Model, decimal.Round(perCase, 6), decimal.Round(perCase * cases, 4), "average of the last run");
        }

        var typical = LlmPricing.EstimateUsd(llm.Model, TypicalCase);
        return new EvalEstimateDto(cases, byCategory, llm.Model, typical, typical is { } t ? decimal.Round(t * cases, 4) : null, "typical case size");
    }

    [HttpPost("runs")]
    public async Task<IActionResult> Start([FromBody] StartEvalRequest? request, CancellationToken ct)
    {
        if (!environment.IsDevelopment())
        {
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Running the eval is turned off on this server to protect the API credits (each run calls the real LLM). The latest results are shown here; to run it yourself, start the API locally in Development.");
        }

        var category = string.IsNullOrWhiteSpace(request?.Category) ? null : request.Category.Trim().ToLowerInvariant();
        if (category is not null && (!CategoryPattern().IsMatch(category) || !(await CountCasesAsync(ct)).ContainsKey(category)))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Unknown eval category '{category}'.");
        }

        var error = runner.Start(category);
        return error is null
            ? Accepted(new { Running = true })
            : Problem(statusCode: StatusCodes.Status409Conflict, title: error);
    }

    [HttpPost("runs/cancel")]
    public IActionResult Cancel() => runner.Cancel() ? NoContent() : Problem(statusCode: StatusCodes.Status409Conflict, title: "No eval run is in progress.");

    private async Task<JsonElement?> ReadLatestAsync(CancellationToken ct)
    {
        if (runner.ResultsDirectory is not { } directory || !System.IO.File.Exists(Path.Combine(directory, "latest.json")))
        {
            return null;
        }

        try
        {
            await using var stream = System.IO.File.OpenRead(Path.Combine(directory, "latest.json"));
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null; // being rewritten right now; the UI polls again
        }
    }

    private async Task<Dictionary<string, int>> CountCasesAsync(CancellationToken ct)
    {
        if (runner.CasesFile is not { } file || !System.IO.File.Exists(file))
        {
            return [];
        }

        await using var stream = System.IO.File.OpenRead(file);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.EnumerateArray()
            .Select(c => c.TryGetProperty("category", out var category) ? category.GetString() ?? "" : "")
            .GroupBy(c => c)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    [GeneratedRegex("^[a-z]{1,20}$")]
    private static partial Regex CategoryPattern();
}
