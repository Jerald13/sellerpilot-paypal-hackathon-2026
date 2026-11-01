using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SellerPilot.Api.Audit;
using SellerPilot.Api.Data;
using SellerPilot.Api.Shop;
using SellerPilot.Tests.TestSupport;

namespace SellerPilot.Tests.Shop;

public sealed class ReconciliationTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly FixedTimeProvider _time = new(PayPalFixtures.Now);
    private readonly SandboxPayPal _paypal = new();

    public void Dispose() => _testDb.Dispose();

    /// <summary>The real situation: the USD 45 scarf refund was approved in SellerPilot before the books existed.</summary>
    private async Task SeedShopAsync()
    {
        await using (var db = _testDb.CreateContext())
        {
            db.PendingActions.Add(new PendingAction
            {
                Id = Guid.NewGuid(),
                Type = ActionType.Refund,
                TargetId = "6YM65696F5973222C",
                Amount = 45.00m,
                Currency = "USD",
                Status = ActionStatus.Executed,
                PayPalResultId = SandboxPayPal.ScarfRefundId,
                CreatedAt = _time.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync();
        }

        await ShopFixtures.SeedAsync(_testDb, _time);
    }

    private async Task<ReconciliationRun> RunAsync()
    {
        await using var db = _testDb.CreateContext();
        var service = new ReconciliationService(db, _paypal.Client, new AuditService(db, _time, NullLogger<AuditService>.Instance), _time, NullLogger<ReconciliationService>.Instance);
        return await service.RunAsync("seller", CancellationToken.None);
    }

    [Fact]
    public async Task Finds_exactly_the_planted_mismatches()
    {
        await SeedShopAsync();

        var run = await RunAsync();

        Assert.Equal(ReconciliationStatus.Completed, run.Status);
        Assert.Equal(9, run.OrdersChecked);
        Assert.Equal(6, run.OrdersMatched);
        Assert.Equal(4, run.IssuesFound);

        var pending = Assert.Single(run.Findings, f => f.Kind == FindingKind.PaymentNotCompleted);
        Assert.Equal(("SP-DVAN-1003", FindingSeverity.High), (pending.OrderNumber, pending.Severity));
        Assert.Contains("PENDING", pending.Message);
        Assert.Contains("already shipped", pending.Message);

        var over = Assert.Single(run.Findings, f => f.Kind == FindingKind.Overcharged);
        Assert.Equal(("SP-DVAN-1002", 12.00m, "4CL16101SL049693V"), (over.OrderNumber, over.Difference, over.PayPalTransactionId));
        Assert.Contains("WELCOME10", over.Message);

        var refund = Assert.Single(run.Findings, f => f.Kind == FindingKind.RefundNotInBooks);
        Assert.Equal(("SP-DVAN-1005", "6X698339418911635", 25.00m), (refund.OrderNumber, refund.PayPalTransactionId, refund.Difference));

        var unknown = Assert.Single(run.Findings, f => f.Kind == FindingKind.UnknownPayment);
        Assert.Equal((SandboxPayPal.KeychainCaptureId, 15.00m), (unknown.PayPalTransactionId, unknown.PayPalAmount));
        Assert.DoesNotContain("Doe", unknown.Message); // payer names are customer text; they stay out of the explanation

        Assert.Equal(FindingSeverity.High, run.Findings[0].Severity); // most severe first
    }

    [Fact]
    public async Task Fixing_the_books_and_refunding_the_overcharge_clears_those_findings()
    {
        await SeedShopAsync();
        _paypal.AddOrder("3TC19417WU947222P", "4CL16101SL049693V", "120.00", "USD", "PARTIALLY_REFUNDED", ("2RF51234AB1234567", "12.00"));
        await using (var db = _testDb.CreateContext())
        {
            var ledger = new LedgerService(db, _time);
            var overcharged = await db.ShopOrders.SingleAsync(o => o.OrderNumber == "SP-DVAN-1002");
            var dashboardRefund = await db.ShopOrders.SingleAsync(o => o.OrderNumber == "SP-DVAN-1005");
            await ledger.RecordAsync(overcharged, LedgerEntryType.Refund, 12.00m, "USD", "2RF51234AB1234567", LedgerSources.SellerPilot, null, CancellationToken.None);
            await ledger.RecordAsync(dashboardRefund, LedgerEntryType.Refund, 25.00m, "USD", "6X698339418911635", LedgerSources.Correction, null, CancellationToken.None);
        }

        var run = await RunAsync();

        Assert.Equal(
            new[] { FindingKind.PaymentNotCompleted, FindingKind.UnknownPayment },
            run.Findings.Select(f => f.Kind).OrderBy(k => k).ToArray());
    }

    [Fact]
    public async Task A_refund_in_the_books_that_PayPal_never_made_is_high_severity()
    {
        await SeedShopAsync();
        await using (var db = _testDb.CreateContext())
        {
            var order = await db.ShopOrders.SingleAsync(o => o.OrderNumber == "SP-DVAN-1007");
            db.LedgerEntries.Add(new LedgerEntry { ShopOrderId = order.Id, Type = LedgerEntryType.Refund, Amount = 20m, Currency = "USD", PayPalTransactionId = "0FAKE0REFUND00001", Source = LedgerSources.Checkout, RecordedAt = _time.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync();
        }

        var run = await RunAsync();

        var finding = Assert.Single(run.Findings, f => f.Kind == FindingKind.RefundNotInPayPal);
        Assert.Equal(("SP-DVAN-1007", FindingSeverity.High, 20m), (finding.OrderNumber, finding.Severity, finding.Difference));
    }

    [Fact]
    public async Task A_missing_PayPal_order_is_reported()
    {
        await SeedShopAsync();
        _paypal.OrderIsMissing("85690008WY900754C");

        var run = await RunAsync();

        var finding = Assert.Single(run.Findings, f => f.Kind == FindingKind.PayPalOrderMissing);
        Assert.Equal("SP-DVAN-1007", finding.OrderNumber);
    }

    [Fact]
    public async Task Without_Transaction_Search_the_orders_are_still_reconciled_and_the_gap_is_noted()
    {
        await SeedShopAsync();
        _paypal.TransactionSearchIsNotEnabled();

        var run = await RunAsync();

        Assert.Equal(ReconciliationStatus.Completed, run.Status);
        Assert.DoesNotContain(run.Findings, f => f.Kind == FindingKind.UnknownPayment);
        Assert.Contains("could not be checked", run.Note);
        Assert.Equal(3, run.IssuesFound);
    }

    [Fact]
    public async Task When_PayPal_is_down_the_run_fails_without_half_a_report()
    {
        await SeedShopAsync();
        _paypal.PayPalIsDown();

        var run = await RunAsync();

        Assert.Equal(ReconciliationStatus.Failed, run.Status);
        Assert.Empty(run.Findings);
        Assert.Contains("did not respond", run.Error);
        await using var db = _testDb.CreateContext();
        Assert.Single(await db.ReconciliationRuns.ToListAsync());
        Assert.Contains(await db.AuditLogs.Select(l => l.Event).ToListAsync(), e => e == AuditEvents.ReconciliationRun);
    }
}
