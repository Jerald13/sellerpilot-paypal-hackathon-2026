using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SellerPilot.Api.Agent.Tools;
using SellerPilot.Api.Approvals;
using SellerPilot.Api.Audit;
using SellerPilot.Api.Configuration;
using SellerPilot.Api.Data;
using SellerPilot.Api.Guardrails;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.Shop;
using SellerPilot.Tests.TestSupport;

namespace SellerPilot.Tests.Shop;

public sealed class ShopToolTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly FixedTimeProvider _time = new(PayPalFixtures.Now);
    private readonly SandboxPayPal _paypal = new();
    private readonly IPayPalWriteClient _write = Substitute.For<IPayPalWriteClient>();
    private readonly ToolContext _context = new() { ConversationId = Guid.NewGuid() };

    public ShopToolTests() => ShopFixtures.SeedAsync(_testDb, _time).GetAwaiter().GetResult();

    public void Dispose() => _testDb.Dispose();

    private static ToolArguments Args(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    private static JsonElement Json(ToolResult result) => JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

    private ProposeBooksCorrectionTool CorrectionTool(AppDbContext db) =>
        new(db, _paypal.Client, new AuditService(db, _time, NullLogger<AuditService>.Instance), _time);

    private ApprovalService Approvals(AppDbContext db) => new(
        db,
        _paypal.Client,
        _write,
        new RefundStateLoader(_paypal.Client, NullLogger<RefundStateLoader>.Instance),
        new RefundPolicy(Options.Create(new GuardrailOptions { MaxRefundAmount = new() { ["USD"] = 500m } }), _time),
        new AuditService(db, _time, NullLogger<AuditService>.Instance),
        new LedgerService(db, _time),
        _time,
        NullLogger<ApprovalService>.Instance);

    [Fact]
    public async Task FindShopOrders_finds_a_customer_by_name_and_masks_the_email()
    {
        await using var db = _testDb.CreateContext();
        var tool = new FindShopOrdersTool(db, new UntrustedTextSanitizer(new InjectionDetector()));

        var result = Json(await tool.ExecuteAsync(Args("""{"query":"ben miller"}"""), _context, CancellationToken.None));

        Assert.Equal(2, result.GetProperty("found").GetInt32());
        var orders = result.GetProperty("orders").EnumerateArray().ToList();
        Assert.All(orders, o => Assert.Equal("Ben Miller", o.GetProperty("customer").GetProperty("name").GetString()));
        Assert.DoesNotContain("ben.miller@example.com", result.GetRawText());
        Assert.Contains(orders, o => o.GetProperty("pay_pal_order_id").GetString() == "46F59753TW901143S");
    }

    [Fact]
    public async Task FindShopOrders_matches_item_names_and_order_numbers()
    {
        await using var db = _testDb.CreateContext();
        var tool = new FindShopOrdersTool(db, new UntrustedTextSanitizer(new InjectionDetector()));

        var byItem = Json(await tool.ExecuteAsync(Args("""{"query":"wall hanging"}"""), _context, CancellationToken.None));
        var byNumber = Json(await tool.ExecuteAsync(Args("""{"query":"sp-dvan-1005"}"""), _context, CancellationToken.None));

        Assert.Equal("SP-DVAN-1008", byItem.GetProperty("orders")[0].GetProperty("order_number").GetString());
        Assert.Equal("SP-DVAN-1005", byNumber.GetProperty("orders")[0].GetProperty("order_number").GetString());
    }

    [Fact]
    public async Task GetOrder_adds_the_shop_record_with_the_shops_own_item_names()
    {
        await using var db = _testDb.CreateContext();
        var tool = new GetOrderTool(_paypal.Client, new UntrustedTextSanitizer(new InjectionDetector()), db);

        var linked = Json(await tool.ExecuteAsync(Args("""{"order_id":"1JG9681911260461C"}"""), _context, CancellationToken.None));
        var unknown = Json(await tool.ExecuteAsync(Args("""{"order_id":"93270599TG532911K"}"""), _context, CancellationToken.None));

        var record = linked.GetProperty("shop_record");
        Assert.Equal("SP-DVAN-1008", record.GetProperty("order_number").GetString());
        Assert.Equal("Wall hanging, large", record.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal("1JG9681911260461C", linked.GetProperty("pay_pal").GetProperty("order_id").GetString());
        Assert.False(unknown.TryGetProperty("shop_record", out _)); // the keychain payment has no shop order
    }

    [Fact]
    public async Task A_books_correction_copies_the_amount_from_PayPal_and_waits_for_approval()
    {
        await using var db = _testDb.CreateContext();

        var result = await CorrectionTool(db).ExecuteAsync(
            Args("""{"order_number":"sp-dvan-1005","paypal_transaction_id":"6X698339418911635","reason":"Refund made in the PayPal dashboard."}"""), _context, CancellationToken.None);

        Assert.False(result.IsError);
        var action = Assert.Single(_context.ProposedActions);
        Assert.Equal((ActionType.BooksCorrection, ActionStatus.Pending, 25.00m, "USD"), (action.Type, action.Status, action.Amount, action.Currency));
        Assert.Contains("no money moves", action.Summary);
        Assert.Empty(await db.LedgerEntries.Where(e => e.PayPalTransactionId == "6X698339418911635").ToListAsync());
    }

    [Theory]
    [InlineData("SP-DVAN-1006", "9YK317787A915471L", "already recorded")] // already in the books
    [InlineData("SP-DVAN-1005", "44527045F66134616", "is not a refund or payment")] // belongs to another order
    [InlineData("SP-NOPE-0001", "6X698339418911635", "no shop order")]
    [InlineData("SP-HIST-0830", "6X698339418911635", "not paid through PayPal")]
    public async Task Books_corrections_that_dont_match_PayPal_are_blocked(string orderNumber, string transactionId, string expected)
    {
        await using var db = _testDb.CreateContext();

        var result = await CorrectionTool(db).ExecuteAsync(
            Args($$"""{"order_number":"{{orderNumber}}","paypal_transaction_id":"{{transactionId}}","reason":"x"}"""), _context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains(expected, JsonSerializer.Serialize(result.Data));
        Assert.Empty(_context.ProposedActions);
    }

    [Fact]
    public async Task Approving_a_books_correction_writes_the_ledger_and_never_calls_PayPal_write()
    {
        await using (var db = _testDb.CreateContext())
        {
            await CorrectionTool(db).ExecuteAsync(
                Args("""{"order_number":"SP-DVAN-1005","paypal_transaction_id":"6X698339418911635","reason":"Dashboard refund."}"""), _context, CancellationToken.None);
        }

        var actionId = _context.ProposedActions.Single().Id;
        await using (var db = _testDb.CreateContext())
        {
            var result = await Approvals(db).ApproveAsync(actionId, CancellationToken.None);
            Assert.Equal(ActionStatus.Executed, result.Action!.Status);
        }

        await using var verify = _testDb.CreateContext();
        var entry = await verify.LedgerEntries.Include(e => e.ShopOrder).SingleAsync(e => e.PayPalTransactionId == "6X698339418911635");
        Assert.Equal(("SP-DVAN-1005", LedgerEntryType.Refund, 25.00m, LedgerSources.Correction), (entry.ShopOrder!.OrderNumber, entry.Type, entry.Amount, entry.Source));
        Assert.Empty(_write.ReceivedCalls());
    }

    [Fact]
    public async Task A_books_correction_is_blocked_at_approval_if_someone_recorded_it_meanwhile()
    {
        await using (var db = _testDb.CreateContext())
        {
            await CorrectionTool(db).ExecuteAsync(
                Args("""{"order_number":"SP-DVAN-1005","paypal_transaction_id":"6X698339418911635","reason":"Dashboard refund."}"""), _context, CancellationToken.None);
            var order = await db.ShopOrders.SingleAsync(o => o.OrderNumber == "SP-DVAN-1005");
            await new LedgerService(db, _time).RecordAsync(order, LedgerEntryType.Refund, 25m, "USD", "6X698339418911635", LedgerSources.Checkout, null, CancellationToken.None);
        }

        await using var approveDb = _testDb.CreateContext();
        var result = await Approvals(approveDb).ApproveAsync(_context.ProposedActions.Single().Id, CancellationToken.None);

        Assert.Equal(ActionStatus.Failed, result.Action!.Status);
        Assert.Contains("already recorded", result.Action.Error);
        Assert.Single(await approveDb.LedgerEntries.Where(e => e.PayPalTransactionId == "6X698339418911635").ToListAsync());
    }
}
