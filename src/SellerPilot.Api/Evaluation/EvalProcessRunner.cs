using System.Diagnostics;
using SellerPilot.Api.Configuration;

namespace SellerPilot.Api.Evaluation;

/// <summary>
/// Starts the eval tool (tools/SellerPilot.Eval) as a separate process, exactly like running it in a terminal, so the
/// Eval tab and the command line can never disagree. One run at a time. The tool must be built first (dotnet build).
/// The eval process swaps in a PayPal write client that refuses every call and uses its own throwaway database.
/// </summary>
public sealed class EvalProcessRunner(ILogger<EvalProcessRunner> logger) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly LinkedList<string> _output = new();
    private Process? _process;

    public string? RepoRoot { get; } = DotEnv.FindRepoRoot(new DirectoryInfo(Directory.GetCurrentDirectory()))?.FullName;

    public string? ResultsDirectory => RepoRoot is null ? null : Path.Combine(RepoRoot, "eval", "results");

    public string? CasesFile => RepoRoot is null ? null : Path.Combine(RepoRoot, "eval", "cases.json");

    private string? ToolDll => RepoRoot is null ? null : Path.Combine(RepoRoot, "tools", "SellerPilot.Eval", "bin", "Debug", "net10.0", "SellerPilot.Eval.dll");

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public int? LastExitCode { get; private set; }

    /// <summary>The last lines the tool printed (shown in the UI when a run fails).</summary>
    public IReadOnlyList<string> RecentOutput
    {
        get
        {
            lock (_gate)
            {
                return [.. _output];
            }
        }
    }

    /// <param name="category">Optional case category; validated by the caller.</param>
    public string? Start(string? category)
    {
        if (ToolDll is not { } dll || !File.Exists(dll))
        {
            return "The eval tool is not built yet. Stop the API, run `dotnet build` in the repository, then start the API again.";
        }

        lock (_gate)
        {
            if (_process is { HasExited: false })
            {
                return "An eval run is already in progress.";
            }

            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = RepoRoot!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(dll);
            start.ArgumentList.Add("--delay-ms");
            start.ArgumentList.Add("500");
            if (category is not null)
            {
                start.ArgumentList.Add("--category");
                start.ArgumentList.Add(category);
            }

            _output.Clear();
            LastExitCode = null;
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Remember(e.Data);
            process.ErrorDataReceived += (_, e) => Remember(e.Data);
            process.Exited += (_, _) =>
            {
                LastExitCode = process.ExitCode;
                logger.LogInformation("Eval run finished with exit code {ExitCode}", process.ExitCode);
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            logger.LogInformation("Eval run started (category {Category})", category ?? "all");
            return null;
        }
    }

    public bool Cancel()
    {
        lock (_gate)
        {
            if (_process is not { HasExited: false } process)
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            Remember("Run cancelled by the seller.");
            return true;
        }
    }

    private void Remember(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_gate)
        {
            _output.AddLast(line);
            while (_output.Count > 40)
            {
                _output.RemoveFirst();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
            }

            _process?.Dispose();
        }
    }
}
