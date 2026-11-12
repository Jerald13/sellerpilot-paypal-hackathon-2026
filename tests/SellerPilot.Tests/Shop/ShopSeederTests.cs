using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;
using SellerPilot.Api.Shop;
using Microsoft.EntityFrameworkCore;
using SellerPilot.Api.Data;
using SellerPilot.Tests.TestSupport;

namespace SellerPilot.Tests.Shop;

public sealed class ShopSeederTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly FixedTimeProvider _time = new(PayPalFixtures.Now);

    public void Dispose() => _testDb.Dispose();

    private async Task<List<ShopOrder>> OrdersAsync()
    {
        await using var db = _testDb.CreateContext();
        return await db.ShopOrders.Include(o => o.Customer).Include(o => o.Lines).Include(o => o.LedgerEntries).OrderBy(o => o.OrderNumber).ToListAsync();
    }

    [Fact]
    public async Task Books_are_built_on_the_sandbox_orders_with_the_planted_mismatches()
    {
        await ShopFixtures.SeedAsync(_testDb, _time);
        var orders = await OrdersAsync();

        // 9 linked sandbox orders + 1 historical bank-transfer order; the USD 15 keychain is deliberately missing.
        Assert.Equal(10, orders.Count);
        Assert.DoesNotContain(orders, o => o.PayPalOrderId == "93270599TG532911K");

        var overcharged = orders.Single(o => o.OrderNumber == "SP-DVAN-1002");
        Assert.Equal(120.00m, overcharged.Subtotal);
        Assert.Equal(12.00m, overcharged.Discount);
        Assert.Equal(108.00m, overcharged.Total);
        Assert.Equal(120.00m, overcharged.LedgerEntries.Single(e => e.Type == LedgerEntryType.Payment).Amount);

        var refundedInDashboard = orders.Single(o => o.OrderNumber == "SP-DVAN-1005");
        Assert.DoesNotContain(refundedInDashboard.LedgerEntries, e => e.Type == LedgerEntryType.Refund);

        var partlyRefunded = orders.Single(o => o.OrderNumber == "SP-DVAN-1006");
        var refund = partlyRefunded.LedgerEntries.Single(e => e.Type == LedgerEntryType.Refund);
        Assert.Equal(("9YK317787A915471L", 30.00m), (refund.PayPalTransactionId, refund.Amount));

        var shippedButPending = orders.Single(o => o.OrderNumber == "SP-DVAN-1003");
        Assert.Equal(ShopOrderStatus.Shipped, shippedButPending.Status);
        Assert.Single(shippedButPending.LedgerEntries, e => e.Type == LedgerEntryType.Payment);

        var awaiting = orders.Single(o => o.OrderNumber == "SP-DVAN-1010");
        Assert.Equal(ShopOrderStatus.AwaitingPayment, awaiting.Status);
        Assert.Empty(awaiting.LedgerEntries);

        var finalSale = orders.Single(o => o.OrderNumber == "SP-DVAN-1004");
        Assert.Equal(0, finalSale.Lines.Single().ReturnWindowDays);
        Assert.Equal("Chloe Bennett", finalSale.Customer!.Name);

        var history = orders.Single(o => o.OrderNumber == "SP-HIST-0830");
        Assert.Equal(PaymentMethod.BankTransfer, history.PaymentMethod);
        Assert.Null(history.PayPalOrderId);
        Assert.Equal(new DateTime(2026, 9, 4, 14, 0, 0, DateTimeKind.Utc), history.DeliveredAt);
    }

    [Fact]
    public async Task Refunds_SellerPilot_already_executed_are_posted_to_the_books()
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
                PayPalResultId = "44527045F66134616",
                CreatedAt = _time.GetUtcNow().UtcDateTime,
                DecidedAt = _time.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync();
        }

        await ShopFixtures.SeedAsync(_testDb, _time);

        var scarf = (await OrdersAsync()).Single(o => o.OrderNumber == "SP-DVAN-1001");
        var refund = scarf.LedgerEntries.Single(e => e.Type == LedgerEntryType.Refund);
        Assert.Equal(("44527045F66134616", 45.00m, LedgerSources.SellerPilot), (refund.PayPalTransactionId, refund.Amount, refund.Source));
    }

    [Fact]
    public async Task Seeding_twice_does_not_duplicate_anything()
    {
        await ShopFixtures.SeedAsync(_testDb, _time);
        await ShopFixtures.SeedAsync(_testDb, _time);

        await using var db = _testDb.CreateContext();
        Assert.Equal(4, await db.Customers.CountAsync());
        Assert.Equal(10, await db.ShopOrders.CountAsync());
    }

    [Fact]
    public async Task A_fresh_shop_books_the_refunds_SellerPilot_made_earlier_but_not_refunds_made_elsewhere()
    {
        var read = Substitute.For<IPayPalReadClient>();
        read.GetOrderAsync(default!, default).ReturnsForAnyArgs(PayPalFixtures.Parse<PayPalOrder>(PayPalFixtures.Order()));
        var actionId = Guid.NewGuid().ToString();
        // SP-DVAN-1006: the seed's own refund (no action ID, already in the books) plus one SellerPilot executed later.
        read.GetOrderAsync("9P344288XW6898834", Arg.Any<CancellationToken>()).Returns(PayPalFixtures.Parse<PayPalOrder>(PayPalFixtures.Order(
            orderId: "9P344288XW6898834",
            capture: PayPalFixtures.Capture(captureId: "30379916G6480042M", orderId: "9P344288XW6898834", amount: "80.00"),
            refunds: [
                PayPalFixtures.Refund("9YK317787A915471L", "30.00", captureId: "30379916G6480042M"),
                PayPalFixtures.Refund("9MX373558K1341746", "50.00", captureId: "30379916G6480042M", createTime: "2026-11-09T09:53:00Z", customId: actionId),
            ])));
        // SP-DVAN-1005: the planted refund made in the PayPal dashboard (no action ID) must stay out of the books.
        read.GetOrderAsync("7R3994090M8505904", Arg.Any<CancellationToken>()).Returns(PayPalFixtures.Parse<PayPalOrder>(PayPalFixtures.Order(
            orderId: "7R3994090M8505904",
            capture: PayPalFixtures.Capture(captureId: "9SJ46960HW343273P", orderId: "7R3994090M8505904", amount: "25.00"),
            refunds: [PayPalFixtures.Refund("6X698339418911635", "25.00", captureId: "9SJ46960HW343273P")])));

        await using (var db = _testDb.CreateContext())
        {
            await new ShopSeeder(db, Options.Create(ShopFixtures.Options()), _time, NullLogger<ShopSeeder>.Instance, read)
                .SeedAsync(ShopFixtures.Seed(), CancellationToken.None);
        }

        var orders = await OrdersAsync();
        var partly = orders.Single(o => o.OrderNumber == "SP-DVAN-1006").LedgerEntries.Where(e => e.Type == LedgerEntryType.Refund).ToList();
        Assert.Equal(["9YK317787A915471L", "9MX373558K1341746"], partly.OrderBy(e => e.RecordedAt).Select(e => e.PayPalTransactionId));
        Assert.Equal((50.00m, LedgerSources.SellerPilot), (partly[^1].Amount, partly[^1].Source));
        Assert.DoesNotContain(orders.Single(o => o.OrderNumber == "SP-DVAN-1005").LedgerEntries, e => e.Type == LedgerEntryType.Refund);
    }

    [Fact]
    public async Task The_shop_is_still_created_when_PayPal_cannot_be_reached()
    {
        var read = Substitute.For<IPayPalReadClient>();
        read.GetOrderAsync(default!, default).ThrowsAsyncForAnyArgs(new HttpRequestException("no network"));

        await using (var db = _testDb.CreateContext())
        {
            await new ShopSeeder(db, Options.Create(ShopFixtures.Options()), _time, NullLogger<ShopSeeder>.Instance, read)
                .SeedAsync(ShopFixtures.Seed(), CancellationToken.None);
        }

        Assert.Equal(10, (await OrdersAsync()).Count);
    }

    [Fact]
    public async Task Orders_added_to_the_seed_later_join_an_existing_shop_once_with_their_payment_and_refund_booked()
    {
        await ShopFixtures.SeedAsync(_testDb, _time);
        var seed = ShopFixtures.Seed();
        seed.Orders["eval_partial"] = new("5EV12345AB1234567", "6EV12345AB1234567", "PARTIALLY_REFUNDED", "USD", 80.00m, "Cushion covers (pair)", "SP-DVAN-1011", "7EV12345AB1234567", 30.00m);
        seed.Orders["video_partial"] = new("5VD12345AB1234567", "6VD12345AB1234567", "PARTIALLY_REFUNDED", "USD", 80.00m, "Cushion covers (pair)", "SP-DVAN-1012", "7VD12345AB1234567", 30.00m);

        for (var run = 0; run < 2; run++)
        {
            await using var db = _testDb.CreateContext();
            await ShopFixtures.Seeder(db, _time).SeedAsync(seed, CancellationToken.None);
        }

        var orders = await OrdersAsync();
        Assert.Equal(12, orders.Count);
        var forEval = orders.Single(o => o.OrderNumber == "SP-DVAN-1011");
        Assert.Equal("Alice Carter", forEval.Customer!.Name);
        Assert.Equal(("6EV12345AB1234567", 80.00m), (forEval.PayPalCaptureId, forEval.LedgerEntries.Single(e => e.Type == LedgerEntryType.Payment).Amount));
        Assert.Equal(("7EV12345AB1234567", 30.00m), forEval.LedgerEntries.Where(e => e.Type == LedgerEntryType.Refund).Select(e => (e.PayPalTransactionId, e.Amount)).Single());
        Assert.Equal("John Doe", orders.Single(o => o.OrderNumber == "SP-DVAN-1012").Customer!.Name);

        // The orders that were already there are untouched.
        Assert.Equal(108.00m, orders.Single(o => o.OrderNumber == "SP-DVAN-1002").Total);
    }

    [Fact]
    public async Task Orders_still_waiting_for_a_capture_are_not_added_to_the_shop()
    {
        await ShopFixtures.SeedAsync(_testDb, _time);
        var seed = ShopFixtures.Seed();
        seed.Orders["video_partial"] = new("5VD12345AB1234567", null, "AWAITING_BUYER_APPROVAL", "USD", 80.00m, "Cushion covers (pair)", "SP-DVAN-1012", null, 30.00m);

        await using (var db = _testDb.CreateContext())
        {
            await ShopFixtures.Seeder(db, _time).SeedAsync(seed, CancellationToken.None);
        }

        Assert.Equal(10, (await OrdersAsync()).Count);
    }

    [Fact]
    public async Task A_database_seeded_with_old_product_names_gets_the_current_English_names()
    {
        await ShopFixtures.SeedAsync(_testDb, _time);
        await using (var db = _testDb.CreateContext())
        {
            // As seeded before the catalogue was renamed.
            var product = await db.Products.SingleAsync(p => p.Sku == "WRAP");
            (product.Sku, product.Name) = ("SARONG", "Hand-stamped batik sarong");
            var line = await db.ShopOrderLines.SingleAsync(l => l.Sku == "WRAP");
            (line.Sku, line.Name) = ("SARONG", "Hand-stamped batik sarong");
            (await db.Products.SingleAsync(p => p.Sku == "SCARF-BLUE")).Name = "Blue batik silk scarf";
            await db.SaveChangesAsync();
        }

        await ShopFixtures.SeedAsync(_testDb, _time);

        await using var verify = _testDb.CreateContext();
        Assert.Equal("Hand-stamped cotton wrap", (await verify.Products.SingleAsync(p => p.Sku == "WRAP")).Name);
        Assert.Equal("Hand-stamped cotton wrap", (await verify.ShopOrderLines.SingleAsync(l => l.Sku == "WRAP")).Name);
        Assert.Equal("Blue silk scarf", (await verify.Products.SingleAsync(p => p.Sku == "SCARF-BLUE")).Name);
        Assert.DoesNotContain(await verify.Products.Select(p => p.Name).ToListAsync(), n => n.Contains("batik", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Customers_get_a_hashed_demo_password_only_when_one_is_configured()
    {
        await using (var db = _testDb.CreateContext())
        {
            await ShopFixtures.Seeder(db, _time, password: null).SeedAsync(ShopFixtures.Seed(), CancellationToken.None);
            Assert.All(await db.Customers.ToListAsync(), c => Assert.Null(c.PasswordHash));
        }

        // Configured later: the next startup hashes it.
        await using (var db = _testDb.CreateContext())
        {
            await ShopFixtures.Seeder(db, _time).SeedAsync(ShopFixtures.Seed(), CancellationToken.None);
            var john = await db.Customers.SingleAsync(c => c.Email == "john.doe@example.com");
            Assert.NotNull(john.PasswordHash);
            Assert.DoesNotContain(ShopFixtures.DemoPassword, john.PasswordHash);
            Assert.NotEqual(PasswordVerificationResult.Failed, new PasswordHasher<Customer>().VerifyHashedPassword(john, john.PasswordHash!, ShopFixtures.DemoPassword));
        }
    }
}
