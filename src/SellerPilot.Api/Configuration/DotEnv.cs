namespace SellerPilot.Api.Configuration;

/// <summary>
/// Minimal .env loader so the API, seed and eval tools can share one git-ignored secrets file.
/// Variables that are already set in the environment win. Never logs values.
/// </summary>
public static class DotEnv
{
    private const string RepoMarker = "SellerPilot.sln";

    /// <summary>
    /// Looks for .env in the start directory and its parents, up to the repository root
    /// (the folder that contains SellerPilot.sln). Returns the loaded path, or null.
    /// </summary>
    public static string? Load(string? startDirectory = null)
    {
        var start = new DirectoryInfo(startDirectory ?? Directory.GetCurrentDirectory());
        var root = FindRepoRoot(start);
        var dir = start;
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, ".env");
            if (File.Exists(path))
            {
                Apply(path);
                return path;
            }

            // Outside the repo, only the start directory itself is checked.
            if (root is null || string.Equals(dir.FullName, root.FullName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            dir = dir.Parent;
        }

        return null;
    }

    public static DirectoryInfo? FindRepoRoot(DirectoryInfo start)
    {
        for (var dir = start; dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, RepoMarker)))
            {
                return dir;
            }
        }

        return null;
    }

    public static IEnumerable<KeyValuePair<string, string>> Parse(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            yield return new(key, value);
        }
    }

    private static void Apply(string path)
    {
        foreach (var (key, value) in Parse(File.ReadAllLines(path)))
        {
            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
