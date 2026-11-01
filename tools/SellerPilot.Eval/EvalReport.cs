using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SellerPilot.Eval;

public sealed record EvalRunInfo(DateTimeOffset StartedAt, string Provider, string Model, string? SeedRunTag, int TotalCases);

public sealed record EvalTotals(decimal? CostUsd, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens);

public static class EvalReport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static (int Passed, int Scored, int Skipped) Score(IReadOnlyList<EvalCaseResult> results)
    {
        var skipped = results.Count(r => r.Status == EvalCaseResult.Skip);
        var passed = results.Count(r => r.Status == EvalCaseResult.Pass);
        return (passed, results.Count - skipped, skipped);
    }

    public static EvalTotals Totals(IReadOnlyList<EvalCaseResult> results) => new(
        results.Any(r => r.CostUsd is not null) ? results.Sum(r => r.CostUsd ?? 0m) : null,
        results.Sum(r => r.Usage?.InputTokens ?? 0),
        results.Sum(r => r.Usage?.OutputTokens ?? 0),
        results.Sum(r => r.Usage?.CacheReadInputTokens ?? 0),
        results.Sum(r => r.Usage?.CacheWriteInputTokens ?? 0));

    public static string Usd(decimal? value) => value is { } v ? "$" + v.ToString("0.0000", CultureInfo.InvariantCulture) : "n/a";

    public static void PrintCase(EvalCaseResult result, TextWriter output)
    {
        output.WriteLine($"{result.Status,-5} {result.Id,-14} {result.Category,-12} {result.DurationMs / 1000.0,6:0.0}s {Usd(result.CostUsd),9}  {string.Join(" -> ", result.Observation?.ToolCalls.Select(c => c.Name) ?? [])}");
        foreach (var failure in result.Failures)
        {
            output.WriteLine($"        - {failure}");
        }
    }

    public static void PrintSummary(EvalRunInfo info, IReadOnlyList<EvalCaseResult> results, TextWriter output)
    {
        var (passed, scored, skipped) = Score(results);
        var totals = Totals(results);
        output.WriteLine();
        output.WriteLine($"SellerPilot eval: {passed}/{scored} passed{(skipped > 0 ? $" ({skipped} skipped)" : "")} - {info.Provider} {info.Model}");
        foreach (var group in results.GroupBy(r => r.Category))
        {
            var groupScored = group.Count(r => r.Status != EvalCaseResult.Skip);
            output.WriteLine($"  {group.Key,-14} {group.Count(r => r.Status == EvalCaseResult.Pass)}/{groupScored}");
        }

        output.WriteLine($"Estimated cost: {Usd(totals.CostUsd)} ({totals.InputTokens:N0} input + {totals.CacheReadTokens:N0} cached + {totals.CacheWriteTokens:N0} cache-write tokens in, {totals.OutputTokens:N0} out)");
    }

    /// <summary>Written after every case, so the Eval tab can show progress while the run is going.</summary>
    public static Task WriteProgressAsync(string directory, EvalRunInfo info, IReadOnlyList<EvalCaseResult> results, CancellationToken ct) =>
        WriteJsonAsync(directory, "running", info, results, ct);

    public static async Task WriteAsync(string directory, EvalRunInfo info, IReadOnlyList<EvalCaseResult> results, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var (passed, scored, skipped) = Score(results);
        var totals = Totals(results);
        var stamp = info.StartedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmm'Z'", CultureInfo.InvariantCulture);

        await WriteJsonAsync(directory, "completed", info, results, ct);

        var markdown = BuildMarkdown(info, results, passed, scored, skipped, totals);
        await File.WriteAllTextAsync(Path.Combine(directory, "latest.md"), markdown, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, $"run-{stamp}.md"), markdown, ct);

        var history = Path.Combine(directory, "history.csv");
        const string header = "started_at,provider,model,passed,scored,skipped,cost_usd";
        if (!File.Exists(history))
        {
            await File.WriteAllTextAsync(history, header + "\n", ct);
        }
        else
        {
            // Older files had no cost column; keep their rows and upgrade the header.
            var lines = await File.ReadAllLinesAsync(history, ct);
            if (lines.Length > 0 && !lines[0].Contains("cost_usd", StringComparison.Ordinal))
            {
                lines[0] = header;
                await File.WriteAllLinesAsync(history, lines, ct);
            }
        }

        var cost = totals.CostUsd?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "";
        await File.AppendAllTextAsync(history, $"{info.StartedAt:O},{info.Provider},{info.Model},{passed},{scored},{skipped},{cost}\n", ct);
    }

    private static async Task WriteJsonAsync(string directory, string status, EvalRunInfo info, IReadOnlyList<EvalCaseResult> results, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var (passed, scored, skipped) = Score(results);
        var json = JsonSerializer.Serialize(new
        {
            status,
            info,
            completed = results.Count,
            score = new { passed, scored, skipped },
            totals = Totals(results),
            results,
        }, Json);

        // Write then rename, so a reader (the API) never sees half a file.
        var target = Path.Combine(directory, "latest.json");
        var temp = target + ".tmp";
        await File.WriteAllTextAsync(temp, json, ct);
        File.Move(temp, target, overwrite: true);
    }

    private static string BuildMarkdown(EvalRunInfo info, IReadOnlyList<EvalCaseResult> results, int passed, int scored, int skipped, EvalTotals totals)
    {
        var md = new StringBuilder();
        md.AppendLine("# SellerPilot evaluation");
        md.AppendLine();
        md.AppendLine($"**Score: {passed}/{scored} passed**{(skipped > 0 ? $" ({skipped} skipped for missing seed data)" : "")}  ");
        md.AppendLine($"Run: {info.StartedAt:yyyy-MM-dd HH:mm} UTC · Model: {info.Provider} `{info.Model}` · Seed run: `{info.SeedRunTag ?? "none"}`  ");
        md.AppendLine($"Estimated cost: **{Usd(totals.CostUsd)}** ({totals.InputTokens:N0} input, {totals.CacheReadTokens:N0} cached, {totals.CacheWriteTokens:N0} cache-write, {totals.OutputTokens:N0} output tokens)");
        md.AppendLine();
        md.AppendLine("| Category | Passed |");
        md.AppendLine("|---|---|");
        foreach (var group in results.GroupBy(r => r.Category))
        {
            md.AppendLine($"| {group.Key} | {group.Count(r => r.Status == EvalCaseResult.Pass)}/{group.Count(r => r.Status != EvalCaseResult.Skip)} |");
        }

        md.AppendLine();
        md.AppendLine("| Case | Category | Result | Cost | Tools called | Notes |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            var tools = string.Join(" → ", r.Observation?.ToolCalls.Select(c => c.Name) ?? []);
            var notes = string.Join("; ", r.Failures).Replace("|", "\\|");
            md.AppendLine($"| {r.Id} | {r.Category} | {r.Status} | {Usd(r.CostUsd)} | {tools} | {notes} |");
        }

        var failed = results.Where(r => r.Status is EvalCaseResult.Fail or EvalCaseResult.Error).ToList();
        if (failed.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("## Failures");
            foreach (var r in failed)
            {
                md.AppendLine();
                md.AppendLine($"### {r.Id}");
                foreach (var prompt in r.Prompts)
                {
                    md.AppendLine($"> {prompt}");
                }

                md.AppendLine();
                foreach (var failure in r.Failures)
                {
                    md.AppendLine($"- {failure}");
                }

                if (r.Observation?.FinalReply is { Length: > 0 } reply)
                {
                    md.AppendLine();
                    md.AppendLine("Reply:");
                    md.AppendLine();
                    md.AppendLine("```text");
                    md.AppendLine(reply);
                    md.AppendLine("```");
                }
            }
        }

        return md.ToString();
    }
}
