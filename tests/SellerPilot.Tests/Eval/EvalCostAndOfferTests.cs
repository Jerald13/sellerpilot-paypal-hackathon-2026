using System.Text.Json;
using SellerPilot.Api.Llm;
using SellerPilot.Eval;

namespace SellerPilot.Tests.Eval;

public class EvalCostAndOfferTests
{
    private static EvalExpect Expect(string json) => JsonSerializer.Deserialize<EvalExpect>(json, EvalRunner.CaseJson)!;

    private static EvalObservation WithOffers(params ObservedOffer[] offers) => new(["ok"], [], [], 0, 0, 0) { Offers = offers };

    [Fact]
    public void Cost_uses_list_prices_including_cache_reads_and_writes()
    {
        // Haiku 5.5: input 0.10, output 0.50, cache read 0.01, cache write 0.125 USD per million tokens.
        var usage = new LlmUsage(InputTokens: 10_000, OutputTokens: 1_000, CacheReadInputTokens: 20_000, CacheWriteInputTokens: 4_000);

        var cost = LlmPricing.EstimateUsd("claude-haiku-5-5", usage);

        Assert.Equal(0.0022m, cost); // 0.001 + 0.0005 + 0.0002 + 0.0005
        Assert.Equal(0.084m, LlmPricing.EstimateUsd("claude-opus-5-5", usage)); // 0.04 + 0.02 + 0.004 + 0.02
    }

    [Fact]
    public void Unknown_models_get_no_cost_rather_than_a_guess()
    {
        Assert.Null(LlmPricing.EstimateUsd("gemini-flash-latest", new LlmUsage(1000, 100, 0)));
        Assert.Null(LlmPricing.EstimateUsd("claude-haiku-5-5", null));
    }

    [Fact]
    public void Totals_add_up_cost_and_tokens_across_cases()
    {
        EvalCaseResult Case(decimal? cost, long input) => new("x", "c", EvalCaseResult.Pass, [], [], null, 1) { CostUsd = cost, Usage = new LlmUsage(input, 10, 5, 1) };

        var totals = EvalReport.Totals([Case(0.001m, 100), Case(0.002m, 200), Case(null, 300)]);

        Assert.Equal(0.003m, totals.CostUsd);
        Assert.Equal((600L, 30L, 15L, 3L), (totals.InputTokens, totals.OutputTokens, totals.CacheReadTokens, totals.CacheWriteTokens));
        Assert.Null(EvalReport.Totals([Case(null, 1)]).CostUsd);
    }

    [Fact]
    public void Refund_offer_checks_count_amount_and_return()
    {
        var expect = Expect("""{ "offers": 1, "offer_amount": "50.00", "offer_return_required": false }""");

        Assert.Empty(EvalChecks.Evaluate(expect, WithOffers(new ObservedOffer("SP-1", 50.00m, "USD", false))));

        var wrong = EvalChecks.Evaluate(expect, WithOffers(new ObservedOffer("SP-1", 60.00m, "USD", true), new ObservedOffer("SP-2", 1m, "USD", true)));
        Assert.Equal(3, wrong.Count);
        Assert.Contains(wrong, f => f.Contains("expected 1 refund offer"));
        Assert.Contains(wrong, f => f.Contains("offer of 50.00"));
        Assert.Contains(wrong, f => f.Contains("without a return"));
    }

    [Fact]
    public void Customer_cases_are_recognised()
    {
        var evalCase = JsonSerializer.Deserialize<EvalCase>("""{ "id": "shop-01", "agent": "customer", "customer": "john.doe@example.com", "prompt": "hi" }""", EvalRunner.CaseJson)!;

        Assert.True(evalCase.IsCustomerCase);
        Assert.False(new EvalCase { Id = "lookup-01" }.IsCustomerCase);
    }
}
