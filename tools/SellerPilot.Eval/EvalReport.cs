using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SellerPilot.Eval;

public sealed record EvalRunInfo(DateTimeOffset StartedAt, string Provider, string Model, string? SeedRunTag, int TotalCases);

public static class EvalReport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static (int Passed, int Scored, int Skipped) Score(IReadOnlyList<EvalCaseResult> results)
    {
        var skipped = results.Count(r => r.Status == EvalCaseResult.Skip);
        var passed = results.Count(r => r.Status == EvalCaseResult.Pass);
        return (passed, results.Count - skipped, skipped);
    }

    public static void PrintCase(EvalCaseResult result, TextWriter output)
    {
        output.WriteLine($"{result.Status,-5} {result.Id,-14} {result.Category,-12} {result.DurationMs / 1000.0,6:0.0}s  {string.Join(" -> ", result.Observation?.ToolCalls.Select(c => c.Name) ?? [])}");
        foreach (var failure in result.Failures)
        {
            output.WriteLine($"        - {failure}");
        }
    }

    public static void PrintSummary(EvalRunInfo info, IReadOnlyList<EvalCaseResult> results, TextWriter output)
    {
        var (passed, scored, skipped) = Score(results);
        output.WriteLine();
        output.WriteLine($"SellerPilot eval: {passed}/{scored} passed{(skipped > 0 ? $" ({skipped} skipped)" : "")} - {info.Provider} {info.Model}");
        foreach (var group in results.GroupBy(r => r.Category))
        {
            var groupScored = group.Count(r => r.Status != EvalCaseResult.Skip);
            output.WriteLine($"  {group.Key,-12} {group.Count(r => r.Status == EvalCaseResult.Pass)}/{groupScored}");
        }
    }

    public static async Task WriteAsync(string directory, EvalRunInfo info, IReadOnlyList<EvalCaseResult> results, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var (passed, scored, skipped) = Score(results);
        var stamp = info.StartedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmm'Z'", CultureInfo.InvariantCulture);

        var json = JsonSerializer.Serialize(new { info, score = new { passed, scored, skipped }, results }, Json);
        await File.WriteAllTextAsync(Path.Combine(directory, "latest.json"), json, ct);

        var markdown = BuildMarkdown(info, results, passed, scored, skipped);
        await File.WriteAllTextAsync(Path.Combine(directory, "latest.md"), markdown, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, $"run-{stamp}.md"), markdown, ct);

        var history = Path.Combine(directory, "history.csv");
        if (!File.Exists(history))
        {
            await File.WriteAllTextAsync(history, "started_at,provider,model,passed,scored,skipped\n", ct);
        }

        await File.AppendAllTextAsync(history, $"{info.StartedAt:O},{info.Provider},{info.Model},{passed},{scored},{skipped}\n", ct);
    }

    private static string BuildMarkdown(EvalRunInfo info, IReadOnlyList<EvalCaseResult> results, int passed, int scored, int skipped)
    {
        var md = new StringBuilder();
        md.AppendLine("# SellerPilot evaluation");
        md.AppendLine();
        md.AppendLine($"**Score: {passed}/{scored} passed**{(skipped > 0 ? $" ({skipped} skipped for missing seed data)" : "")}  ");
        md.AppendLine($"Run: {info.StartedAt:yyyy-MM-dd HH:mm} UTC · Model: {info.Provider} `{info.Model}` · Seed run: `{info.SeedRunTag ?? "none"}`");
        md.AppendLine();
        md.AppendLine("| Category | Passed |");
        md.AppendLine("|---|---|");
        foreach (var group in results.GroupBy(r => r.Category))
        {
            md.AppendLine($"| {group.Key} | {group.Count(r => r.Status == EvalCaseResult.Pass)}/{group.Count(r => r.Status != EvalCaseResult.Skip)} |");
        }

        md.AppendLine();
        md.AppendLine("| Case | Category | Result | Tools called | Notes |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var r in results)
        {
            var tools = string.Join(" → ", r.Observation?.ToolCalls.Select(c => c.Name) ?? []);
            var notes = string.Join("; ", r.Failures).Replace("|", "\\|");
            md.AppendLine($"| {r.Id} | {r.Category} | {r.Status} | {tools} | {notes} |");
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
