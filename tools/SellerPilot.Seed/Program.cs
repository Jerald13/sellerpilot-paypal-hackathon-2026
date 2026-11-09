using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SellerPilot.Api;
using SellerPilot.Api.Configuration;
using SellerPilot.Api.PayPal;
using SellerPilot.Seed;

// Usage: dotnet run --project tools/SellerPilot.Seed -- [run|add|check|capture|status] [--force] [--output path]
DotEnv.Load();

// "run" is the default (as in CLAUDE.md); it refuses to run twice unless --force is given.
var command = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "run";
var force = args.Contains("--force");
var outputIndex = Array.IndexOf(args, "--output");
var repoRoot = DotEnv.FindRepoRoot(new DirectoryInfo(Directory.GetCurrentDirectory()))?.FullName ?? Directory.GetCurrentDirectory();
var outputPath = outputIndex >= 0 && outputIndex + 1 < args.Length ? Path.GetFullPath(args[outputIndex + 1]) : Path.Combine(repoRoot, "seed-output.json");

// Same secrets as the API: dotnet user-secrets (shared UserSecretsId) or environment variables / .env.
var configuration = new ConfigurationBuilder()
    .AddUserSecrets("sellerpilot-dev")
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection()
    .AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning))
    .AddSingleton<IConfiguration>(configuration)
    .AddSellerPilotPayPal(configuration)
    .AddSingleton<SandboxSeeder>()
    .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

var seeder = services.GetRequiredService<SandboxSeeder>();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return command switch
    {
        "check" => await seeder.CheckAsync(cts.Token),
        "run" => await seeder.RunAsync(outputPath, force, cts.Token),
        "add" => await seeder.AddAsync(outputPath, cts.Token),
        "capture" => await seeder.CaptureAsync(outputPath, cts.Token),
        "status" => await seeder.StatusAsync(outputPath, cts.Token),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is PayPalConfigurationException or PayPalApiException or Microsoft.Extensions.Options.OptionsValidationException)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

static int Usage()
{
    Console.WriteLine("""
        SellerPilot sandbox seed
          run       Create demo orders, refunds and invoices; writes seed-output.json (default)  [--force] [--output path]
          add       Create only the planned orders missing from seed-output.json and add them to it
          check     Verify PayPal sandbox credentials and which features the app has
          capture   Capture orders that were approved by the sandbox buyer, then apply planned refunds
          status    Show each seeded payment's live status at PayPal (read-only)
        """);
    return 1;
}
