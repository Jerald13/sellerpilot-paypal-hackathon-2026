using Microsoft.Extensions.Options;
using SellerPilot.Api.Configuration;
using SellerPilot.Api.Guardrails;
using SellerPilot.Api.PayPal.Models;
using SellerPilot.Tests.TestSupport;

namespace SellerPilot.Tests.Guardrails;

public class RefundPolicyTests
{
    private readonly RefundPolicy _policy = new(
        Options.Create(new GuardrailOptions
        {
            MaxRefundAmount = new(StringComparer.OrdinalIgnoreCase) { ["USD"] = 500m, ["SGD"] = 650m },
            RefundWindowDays = 180,
        }),
        new FixedTimeProvider(PayPalFixtures.Now));

    private static CaptureRefundState State(string amount = "45.00", string status = "COMPLETED", string currency = "USD", params string[] refunds)
    {
        var capture = PayPalFixtures.Parse<PayPalCapture>(PayPalFixtures.Capture(amount: amount, status: status, currency: currency));
        var order = PayPalFixtures.Parse<PayPalOrder>(PayPalFixtures.Order(
            capture: PayPalFixtures.Capture(amount: amount, status: status, currency: currency),
            refunds: refunds,
            amount: amount,
            currency: currency));
        return RefundStateLoader.Build(capture, order);
    }

    [Fact]
    public void Allows_a_full_refund_within_limits()
    {
        var decision = _policy.Evaluate(State(), 45.00m, "USD", 0m);

        Assert.True(decision.Allowed);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void Computes_remaining_from_completed_and_pending_refunds_only()
    {
        var state = State("80.00", "PARTIALLY_REFUNDED", "USD",
            PayPalFixtures.Refund("R1AAAAAAAAAAAAAAA", "30.00"),
            PayPalFixtures.Refund("R2AAAAAAAAAAAAAAA", "5.00", status: "PENDING"),
            PayPalFixtures.Refund("R3AAAAAAAAAAAAAAA", "20.00", status: "CANCELLED"));

        Assert.Equal(35.00m, state.RefundedAmount);
        Assert.Equal(45.00m, state.RemainingRefundable);
        Assert.True(_policy.Evaluate(state, 45.00m, null, 0m).Allowed);
    }

    [Fact]
    public void Blocks_more_than_the_remaining_refundable_amount()
    {
        var state = State("80.00", "PARTIALLY_REFUNDED", "USD", PayPalFixtures.Refund("R1AAAAAAAAAAAAAAA", "30.00"));

        var decision = _policy.Evaluate(state, 80.00m, "USD", 0m);

        Assert.False(decision.Allowed);
        Assert.Contains(decision.Reasons, r => r.Contains("remaining refundable amount of USD 50.00"));
    }

    [Fact]
    public void Blocks_an_already_fully_refunded_capture()
    {
        var state = State("25.00", "REFUNDED", "USD", PayPalFixtures.Refund("R1AAAAAAAAAAAAAAA", "25.00", createTime: "2026-10-25T12:00:00Z"));

        var decision = _policy.Evaluate(state, 25.00m, null, 0m);

        Assert.False(decision.Allowed);
        Assert.Contains(decision.Reasons, r => r.Contains("already fully refunded") && r.Contains("2026-10-25"));
    }

    [Fact]
    public void Blocks_amounts_above_the_configured_limit_even_if_refundable()
    {
        var decision = _policy.Evaluate(State("900.00"), 600.00m, "USD", 0m);

        Assert.False(decision.Allowed);
        Assert.Contains(decision.Reasons, r => r.Contains("per-refund limit of USD 500.00"));
    }

    [Fact]
    public void Blocks_the_injection_demo_amount_for_two_reasons()
    {
        var decision = _policy.Evaluate(State("60.00"), 5000m, "USD", 0m);

        Assert.False(decision.Allowed);
        Assert.Equal(2, decision.Reasons.Count);
    }

    [Fact]
    public void Blocks_currency_mismatch()
    {
        var decision = _policy.Evaluate(State(currency: "SGD"), 45.00m, "USD", 0m);

        Assert.False(decision.Allowed);
        Assert.Contains(decision.Reasons, r => r.Contains("Currency mismatch"));
    }

    [Fact]
    public void Blocks_currencies_without_a_configured_limit()
    {
        var decision = _policy.Evaluate(State(currency: "MYR"), 45.00m, null, 0m);

        Assert.False(decision.Allowed);
        Assert.Contains(decision.Reasons, r => r.Contains("No refund limit is configured for MYR"));
    }

    [Fact]
    public void Counts_proposals_already_waiting_for_approval()
    {
        var decision = _policy.Evaluate(State("45.00"), 30.00m, null, awaitingApprovalAmount: 20.00m);

        Assert.False(decision.Allowed);
        Assert.Contains(decision.Reasons, r => r.Contains("at most USD 25.00 more"));
    }

    [Theory]
    [InlineData("PENDING", "still pending")]
    [InlineData("DECLINED", "No money was captured")]
    public void Blocks_captures_that_hold_no_money(string status, string expected)
    {
        var decision = _policy.Evaluate(State(status: status), 45.00m, null, 0m);

        Assert.False(decision.Allowed);
        Assert.Contains(decision.Reasons, r => r.Contains(expected));
    }

    [Fact]
    public void Blocks_captures_older_than_the_refund_window()
    {
        var capture = PayPalFixtures.Parse<PayPalCapture>(PayPalFixtures.Capture(createTime: "2026-03-01T00:00:00Z"));
        var state = RefundStateLoader.Build(capture, order: null);

        var decision = _policy.Evaluate(state, 45.00m, null, 0m);

        Assert.False(decision.Allowed);
        Assert.Contains(decision.Reasons, r => r.Contains("more than 180 days ago"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10.001)]
    public void Blocks_invalid_amounts(double raw)
    {
        var decision = _policy.Evaluate(State(), (decimal)raw, null, 0m);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Unknown_refund_history_fails_closed()
    {
        // Partially refunded, but the order (with its refund list) could not be loaded.
        var capture = PayPalFixtures.Parse<PayPalCapture>(PayPalFixtures.Capture(status: "PARTIALLY_REFUNDED"));
        var state = RefundStateLoader.Build(capture, order: null);

        var decision = _policy.Evaluate(state, 1.00m, null, 0m);

        Assert.False(decision.Allowed);
        Assert.Contains(decision.Reasons, r => r.Contains("could not be loaded"));
    }
}
