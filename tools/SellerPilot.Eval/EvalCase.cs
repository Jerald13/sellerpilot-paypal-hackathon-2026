using System.Text.Json;
using System.Text.Json.Serialization;

namespace SellerPilot.Eval;

/// <summary>One test prompt (or a short multi-turn conversation) and what a correct agent must do.</summary>
public sealed class EvalCase
{
    public string Id { get; set; } = "";

    public string Category { get; set; } = "";

    /// <summary>Single-turn prompt. Use <see cref="Turns"/> for a conversation; expectations apply to the whole case.</summary>
    public string? Prompt { get; set; }

    public List<string>? Turns { get; set; }

    public EvalExpect Expect { get; set; } = new();

    public string? Note { get; set; }

    /// <summary>"seller" (default): the back-office assistant. "customer": the storefront assistant, signed in as <see cref="Customer"/>.</summary>
    public string? Agent { get; set; }

    /// <summary>Email of the demo customer for customer-assistant cases.</summary>
    public string? Customer { get; set; }

    [JsonIgnore]
    public bool IsCustomerCase => string.Equals(Agent, "customer", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> AllTurns => Turns is { Count: > 0 } ? Turns : Prompt is null ? [] : [Prompt];
}

public sealed class EvalExpect
{
    /// <summary>A tool name or a list of names; every one must be called at least once.</summary>
    [JsonPropertyName("tool_called")]
    public JsonElement? ToolCalled { get; set; }

    [JsonPropertyName("tool_called_any")]
    public List<string>? ToolCalledAny { get; set; }

    [JsonPropertyName("tool_not_called")]
    public List<string>? ToolNotCalled { get; set; }

    [JsonPropertyName("no_tools")]
    public bool? NoTools { get; set; }

    /// <summary>Subset match against the arguments of at least one successful call of the first <see cref="ToolCalled"/> tool.</summary>
    [JsonPropertyName("args")]
    public Dictionary<string, JsonElement>? Args { get; set; }

    [JsonPropertyName("proposals")]
    public int? Proposals { get; set; }

    [JsonPropertyName("min_proposals")]
    public int? MinProposals { get; set; }

    [JsonPropertyName("max_proposals")]
    public int? MaxProposals { get; set; }

    /// <summary>At least one created proposal must match all given fields.</summary>
    [JsonPropertyName("proposal")]
    public EvalProposalExpect? Proposal { get; set; }

    [JsonPropertyName("max_proposal_amount")]
    public decimal? MaxProposalAmount { get; set; }

    [JsonPropertyName("reply_contains")]
    public List<string>? ReplyContains { get; set; }

    [JsonPropertyName("reply_contains_any")]
    public List<string>? ReplyContainsAny { get; set; }

    [JsonPropertyName("reply_not_contains")]
    public List<string>? ReplyNotContains { get; set; }

    /// <summary>Whether SellerPilot must (true) or must not (false) raise a prompt-injection flag.</summary>
    [JsonPropertyName("flagged")]
    public bool? Flagged { get; set; }

    /// <summary>"executed_without_approval", "proposal_created", "claims_executed".</summary>
    [JsonPropertyName("must_not")]
    public List<string>? MustNot { get; set; }

    /// <summary>Customer assistant: number of "Request refund" offers shown to the customer.</summary>
    [JsonPropertyName("offers")]
    public int? Offers { get; set; }

    /// <summary>Customer assistant: an offer with exactly this amount must exist (string, so templates work).</summary>
    [JsonPropertyName("offer_amount")]
    public string? OfferAmount { get; set; }

    [JsonPropertyName("offer_return_required")]
    public bool? OfferReturnRequired { get; set; }
}

public sealed class EvalProposalExpect
{
    public string? Type { get; set; }

    [JsonPropertyName("target_id")]
    public string? TargetId { get; set; }

    /// <summary>String so it can hold a template like "{{orders.full_refund.amount}}".</summary>
    public string? Amount { get; set; }

    public string? Currency { get; set; }
}

/// <summary>What the agent did during one case, gathered across all its turns.</summary>
public sealed record EvalObservation(
    IReadOnlyList<string> Replies,
    IReadOnlyList<ObservedToolCall> ToolCalls,
    IReadOnlyList<ObservedProposal> Proposals,
    int SecurityFlags,
    int WriteAttempts,
    int ExecutedActions)
{
    public string FinalReply => Replies.Count > 0 ? Replies[^1] : "";

    public IReadOnlyList<ObservedOffer> Offers { get; init; } = [];
}

/// <param name="Result">What the model got back (sanitized JSON), so a reader can see why it answered as it did.</param>
public sealed record ObservedToolCall(string Name, JsonElement Arguments, bool Succeeded, string? Error, string? Result = null);

public sealed record ObservedProposal(string Type, string TargetId, decimal? Amount, string? Currency, string Status, string? Reason = null);

public sealed record ObservedOffer(string OrderNumber, decimal Amount, string Currency, bool ReturnRequired);
