namespace SellerPilot.Api.Configuration;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Max rounds of tool execution per user message (CLAUDE.md rule 9).</summary>
    public int MaxToolIterations { get; set; } = 6;

    /// <summary>Max tool calls the model may make in one round; extra calls get an error result.</summary>
    public int MaxToolCallsPerIteration { get; set; } = 10;

    public int MaxUserMessageLength { get; set; } = 2000;

    /// <summary>When the stored history grows past this, it is compacted into a short text recap.</summary>
    public int MaxHistoryMessages { get; set; } = 60;
}
