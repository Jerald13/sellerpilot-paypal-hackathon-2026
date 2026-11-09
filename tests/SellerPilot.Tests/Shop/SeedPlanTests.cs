using SellerPilot.Seed;

namespace SellerPilot.Tests.Shop;

/// <summary>The seed tool's `add` command: which orders it creates and which order numbers they get.</summary>
public sealed class SeedPlanTests
{
    private static SeededOrder Seeded(string reference) => new() { Reference = reference, Status = "CAPTURED" };

    [Fact]
    public void Add_creates_only_the_planned_orders_the_seed_file_does_not_have()
    {
        var existing = SeedPlan.Orders.Where(o => o.Key != "video_partial" && o.Key != "spare_partial")
            .ToDictionary(o => o.Key, _ => Seeded("SP-DVAN-1001"));

        var missing = SeedPlan.MissingOrders(existing);

        Assert.Equal(["video_partial", "spare_partial"], missing.Select(o => o.Key));
    }

    [Fact]
    public void Orders_that_failed_before_are_tried_again()
    {
        var existing = SeedPlan.Orders.ToDictionary(o => o.Key, _ => Seeded("SP-DVAN-1001"));
        existing["spare_partial"] = new SeededOrder { Reference = "SP-DVAN-1013", Status = "FAILED" };

        Assert.Equal(["spare_partial"], SeedPlan.MissingOrders(existing).Select(o => o.Key));
    }

    [Fact]
    public void Nothing_is_added_when_every_planned_order_exists()
    {
        var existing = SeedPlan.Orders.ToDictionary(o => o.Key, _ => Seeded("SP-DVAN-1001"));

        Assert.Empty(SeedPlan.MissingOrders(existing));
    }

    [Fact]
    public void The_spare_orders_are_copies_of_the_partly_refunded_cushion_covers()
    {
        var original = SeedPlan.Orders.Single(o => o.Key == "partially_refunded");

        foreach (var key in new[] { "eval_partial", "video_partial", "spare_partial" })
        {
            var copy = SeedPlan.Orders.Single(o => o.Key == key);
            Assert.Equal((original.Currency, original.Amount, original.RefundAfterCapture), (copy.Currency, copy.Amount, copy.RefundAfterCapture));
        }
    }

    [Fact]
    public void New_order_numbers_continue_after_the_highest_one_so_PayPal_never_sees_a_duplicate()
    {
        Assert.Equal(1011, SeedPlan.NextOrderNumber([Seeded("SP-DVAN-1002"), Seeded("SP-DVAN-1010"), Seeded("SP-DVAN-1007")]));
        Assert.Equal(1001, SeedPlan.NextOrderNumber([]));
        Assert.Equal(1001, SeedPlan.NextOrderNumber([Seeded("not-a-number")]));
    }
}
