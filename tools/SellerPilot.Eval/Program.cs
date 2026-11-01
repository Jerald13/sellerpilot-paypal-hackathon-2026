using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SellerPilot.Api;
using SellerPilot.Api.Configuration;
using SellerPilot.Api.Llm;
using SellerPilot.Api.PayPal;
using SellerPilot.Eval;

// Usage: dotnet run --project tools/SellerPilot.Eval -- [--case id1,id2] [--category refunds] [--delay-ms 2000]
DotEnv.Load();

var repoRoot = DotEnv.FindRepoRoot(new DirectoryInfo(Directory.GetCurrentDirectory()))?.FullName ?? Directory.GetCurrentDirectory();
string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

var casesPath = Option("--cases") ?? Path.Combine(repoRoot, "eval", "cases.json");
var seedPath = Option("--seed") ?? Path.Combine(repoRoot, "seed-output.json");
var onlyCases = Option("--case")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
var onlyCategory = Option("--category");
var delay = TimeSpan.FromMilliseconds(int.TryParse(Option("--delay-ms"), out var ms) ? ms : 0);
var resultsDirectory = Path.Combine(repoRoot, "eval", "results");
Directory.CreateDirectory(resultsDirectory);

// A throwaway database: proposals made during the eval never reach the real one.
var databasePath = Path.Combine(resultsDirectory, "eval.db");
foreach (var file in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
{
    File.Delete(file);
}

// Same configuration as the API (appsettings.json + user secrets + environment), so the eval measures the real agent.
var configuration = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(repoRoot, "src", "SellerPilot.Api", "appsettings.json"), optional: false)
    .AddUserSecrets("sellerpilot-dev")
    .AddEnvironmentVariables()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:SellerPilot"] = $"Data Source={databasePath}" })
    .Build();

var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
services.AddSellerPilot(configuration);
services.AddSingleton<RecordingWriteClient>();
services.AddSingleton<IPayPalWriteClient>(sp => sp.GetRequiredService<RecordingWriteClient>());
await using var provider = services.BuildServiceProvider();
await provider.MigrateSellerPilotDatabaseAsync();
// The eval database gets the same demo shop books as the API, so reconciliation cases see the same data.
await provider.SeedSellerPilotShopAsync(repoRoot);

var llm = provider.GetRequiredService<ILlmClient>();
if (llm is UnconfiguredLlmClient)
{
    Console.Error.WriteLine($"ERROR: no API key configured for {llm.ProviderName} (see README).");
    return 1;
}

try
{
    await provider.GetRequiredService<IPayPalTokenProvider>().GetAccessTokenAsync(CancellationToken.None);
}
catch (Exception ex) when (ex is PayPalConfigurationException or PayPalApiException)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

JsonNode? seed = null;
if (File.Exists(seedPath))
{
    seed = JsonNode.Parse(await File.ReadAllTextAsync(seedPath));
}
else
{
    Console.WriteLine($"WARNING: {seedPath} not found; cases that use seed data will be skipped.");
}

var allCases = JsonNode.Parse(await File.ReadAllTextAsync(casesPath))!.AsArray();
var selected = allCases
    .Where(c => c is not null)
    .Select(c => c!)
    .Where(c => onlyCases is null || onlyCases.Contains(c["id"]?.GetValue<string>() ?? ""))
    .Where(c => onlyCategory is null || string.Equals(c["category"]?.GetValue<string>(), onlyCategory, StringComparison.OrdinalIgnoreCase))
    .ToList();

var info = new EvalRunInfo(DateTimeOffset.UtcNow, llm.ProviderName, llm.Model, seed?["runTag"]?.GetValue<string>(), selected.Count);
Console.WriteLine($"Running {selected.Count} cases with {info.Provider} {info.Model}...");

var runner = new EvalRunner(provider, provider.GetRequiredService<RecordingWriteClient>());
var results = new List<EvalCaseResult>();
await EvalReport.WriteProgressAsync(resultsDirectory, info, results, CancellationToken.None);
foreach (var rawCase in selected)
{
    var result = await runner.RunAsync(rawCase, seed, CancellationToken.None);
    results.Add(result);
    EvalReport.PrintCase(result, Console.Out);
    await EvalReport.WriteProgressAsync(resultsDirectory, info, results, CancellationToken.None);
    if (delay > TimeSpan.Zero)
    {
        await Task.Delay(delay);
    }
}

EvalReport.PrintSummary(info, results, Console.Out);
await EvalReport.WriteAsync(resultsDirectory, info, results, CancellationToken.None);
Console.WriteLine($"Report: {Path.Combine(resultsDirectory, "latest.md")}");

var (passed, scored, _) = EvalReport.Score(results);
return passed == scored ? 0 : 1;
