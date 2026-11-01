namespace SellerPilot.Api.Llm;

/// <summary>
/// Rough cost estimates for the eval report and the Eval tab. List prices in USD per million tokens
/// (Claude API, November 2026). Cache writes cost 1.25x input (5-minute cache); cache reads are about 10% of input.
/// Unknown models (including Gemini) return null rather than a guess.
/// </summary>
public static class LlmPricing
{
    public sealed record Rates(decimal InputPerMillion, decimal OutputPerMillion, decimal CacheReadPerMillion, decimal CacheWritePerMillion);

    private static readonly Dictionary<string, Rates> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-haiku-5-5"] = new(0.10m, 0.50m, 0.01m, 0.125m),
        ["claude-sonnet-5-5"] = new(2.00m, 10.00m, 0.20m, 2.50m),
        ["claude-opus-5-5"] = new(4.00m, 20.00m, 0.20m, 5.00m),
    };

    public static Rates? For(string? model) =>
        model is null ? null : Known.FirstOrDefault(k => model.StartsWith(k.Key, StringComparison.OrdinalIgnoreCase)).Value;

    public static decimal? EstimateUsd(string? model, LlmUsage? usage)
    {
        if (usage is null || For(model) is not { } rates)
        {
            return null;
        }

        var cost = usage.InputTokens * rates.InputPerMillion
                   + usage.OutputTokens * rates.OutputPerMillion
                   + usage.CacheReadInputTokens * rates.CacheReadPerMillion
                   + usage.CacheWriteInputTokens * rates.CacheWritePerMillion;
        return decimal.Round(cost / 1_000_000m, 6);
    }
}
