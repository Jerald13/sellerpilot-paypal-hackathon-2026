namespace SellerPilot.Api.Configuration;

public enum LlmProvider
{
    Claude,
    Gemini,
}

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public LlmProvider Provider { get; set; } = LlmProvider.Claude;

    /// <summary>Timeout for a single LLM HTTP call.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    public ClaudeOptions Claude { get; set; } = new();
    public GeminiOptions Gemini { get; set; } = new();
}

public sealed class ClaudeOptions
{
    /// <summary>Falls back to the ANTHROPIC_API_KEY environment variable when empty.</summary>
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>low | medium | high | xhigh | max. Controls how much the model thinks (cost and latency).</summary>
    public string Effort { get; set; } = "medium";

    /// <summary>Covers thinking plus the visible reply.</summary>
    public int MaxTokens { get; set; } = 16000;

    /// <summary>Re-run a safety-classifier refusal on Anthropic's recommended fallback model (server-side, beta).</summary>
    public bool UseServerSideFallback { get; set; } = true;
}

public sealed class GeminiOptions
{
    /// <summary>Falls back to the GEMINI_API_KEY environment variable when empty.</summary>
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-flash-latest";
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/";
    public int MaxOutputTokens { get; set; } = 8192;

    /// <summary>Leave empty to use the model default (recommended for Gemini 3 models).</summary>
    public double? Temperature { get; set; }
}
