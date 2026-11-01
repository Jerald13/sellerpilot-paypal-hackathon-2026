using System.Text.RegularExpressions;

namespace SellerPilot.Api.Guardrails;

public interface IInjectionDetector
{
    /// <summary>Returns the names of the rules the text matches (empty if it looks harmless).</summary>
    IReadOnlyList<string> Detect(string? text);
}

/// <summary>
/// Heuristic detector for instructions hidden in PayPal data (order notes, item names, invoice memos).
/// It is one layer only: limits and the approval step are enforced in code regardless of what it finds.
/// </summary>
public sealed class InjectionDetector : IInjectionDetector
{
    private const int MaxScanLength = 4000;
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly (string Rule, Regex Pattern)[] Rules =
    [
        ("override-instructions", new Regex(
            @"\b(ignore|disregard|forget|override|bypass)\b[^.!?\n]{0,40}\b(rules?|instructions?|guidelines?|polic(y|ies)|guardrails?|limits?|restrictions?|prompts?)\b",
            Options, MatchTimeout)),
        ("role-hijack", new Regex(
            @"\b(you are now|act as|pretend (to be|you are)|from now on,? you|new instructions?|system prompt|developer mode|jailbreak)\b",
            Options, MatchTimeout)),
        ("fake-system-tag", new Regex(
            @"(\[\s*(system|assistant|admin)\s*\]|<\s*/?\s*(system|assistant|instructions?)\s*>|^\s*(system|assistant)\s*:)",
            Options | RegexOptions.Multiline, MatchTimeout)),
        ("money-instruction", new Regex(
            @"\b(refund|transfer|send|pay|wire|credit)\b[^.!?\n]{0,30}(\$|usd|sgd|myr|rm|eur|€|£)\s?\d",
            Options, MatchTimeout)),
        ("approval-bypass", new Regex(
            @"\b(approve|execute|process|issue)\b[^.!?\n]{0,30}\b(automatically|immediately|without (approval|asking|confirmation|review))\b",
            Options, MatchTimeout)),
        ("concealment", new Regex(
            @"\b(do not|don't|never)\s+(tell|inform|notify|show|alert)\b[^.!?\n]{0,30}\b(seller|merchant|owner|user|anyone)\b",
            Options, MatchTimeout)),
    ];

    public IReadOnlyList<string> Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var input = text.Length > MaxScanLength ? text[..MaxScanLength] : text;
        var matches = new List<string>();
        foreach (var (rule, pattern) in Rules)
        {
            try
            {
                if (pattern.IsMatch(input))
                {
                    matches.Add(rule);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Pathological input: treat as suspicious rather than letting it through.
                matches.Add(rule);
            }
        }

        return matches;
    }
}
