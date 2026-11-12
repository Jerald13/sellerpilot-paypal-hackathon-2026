using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SellerPilot.Api.Configuration;
using SellerPilot.Api.Data;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;

namespace SellerPilot.Api.Shop;

public sealed record ShopSeedResult(bool Seeded, string Message);

/// <summary>
/// Creates the demo shop (customers, products, orders, payment history) once, from seed-output.json.
/// Runs at startup; does nothing if the shop already has customers, except keeping demo passwords in sync.
/// </summary>
public sealed class ShopSeeder(AppDbContext db, IOptions<ShopOptions> options, TimeProvider time, ILogger<ShopSeeder> logger, IPayPalReadClient? paypalRead = null)
{
    private readonly PasswordHasher<Customer> _hasher = new();

    public async Task<ShopSeedResult> SeedFromRepositoryAsync(string? repoRoot, CancellationToken ct)
    {
        var path = Path.Combine(repoRoot ?? Directory.GetCurrentDirectory(), options.Value.SeedFile);
        SeedOutputFile? seed = null;
        if (File.Exists(path))
        {
            try
            {
                seed = JsonSerializer.Deserialize<SeedOutputFile>(await File.ReadAllTextAsync(path, ct), SeedOutputFile.Json);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Could not read {SeedFile}", path);
                if (!await db.Customers.AnyAsync(ct))
                {
                    return new ShopSeedResult(false, $"{options.Value.SeedFile} could not be read.");
                }
            }
        }

        if (seed is not null)
        {
            return await SeedAsync(seed, ct);
        }

        if (await db.Customers.AnyAsync(ct))
        {
            await SyncDemoCatalogAsync(ct);
            await ApplyDemoPasswordsAsync(ct);
            return new ShopSeedResult(false, "The shop data already exists.");
        }

        return File.Exists(path)
            ? new ShopSeedResult(false, "The seed file is empty.")
            : new ShopSeedResult(false, $"{options.Value.SeedFile} not found; run the seed tool first, then restart.");
    }

    public async Task<ShopSeedResult> SeedAsync(SeedOutputFile seed, CancellationToken ct)
    {
        if (await db.Customers.AnyAsync(ct))
        {
            await SyncDemoCatalogAsync(ct);
            var added = await AddMissingOrdersAsync(seed, ct);
            await ApplyDemoPasswordsAsync(ct);
            return new ShopSeedResult(false, added > 0 ? $"The shop data already exists; added {added} new orders." : "The shop data already exists.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        var customers = ShopSeedData.Customers.ToDictionary(
            c => c.Key,
            c => new Customer { Name = c.Name, Email = c.Email.ToLowerInvariant(), Country = c.Country, CreatedAt = now });
        var products = ShopSeedData.Products.ToDictionary(
            p => p.Sku,
            p => new Product { Sku = p.Sku, Name = p.Name, Price = p.Price, Currency = p.Currency, ReturnWindowDays = p.ReturnWindowDays });
        db.Customers.AddRange(customers.Values);
        db.Products.AddRange(products.Values);

        var orders = new List<ShopOrder>();
        var placed = seed.GeneratedAt.UtcDateTime;
        foreach (var (spec, index) in ShopSeedData.Orders.Select((s, i) => (s, i)))
        {
            if (!seed.Orders.TryGetValue(spec.SeedKey, out var paypal) || paypal.OrderId is null)
            {
                logger.LogWarning("Seed order {Key} is missing from seed-output.json; skipped in the shop data", spec.SeedKey);
                continue;
            }

            var order = BuildOrder(spec, paypal, customers[spec.CustomerKey], products[spec.Sku], placed.AddMinutes(index), now);
            orders.Add(order);
        }

        var history = ShopSeedData.HistoricalOrder;
        var historyProduct = products[history.Sku];
        orders.Add(new ShopOrder
        {
            OrderNumber = history.OrderNumber,
            Customer = customers[history.CustomerKey],
            Status = ShopOrderStatus.Delivered,
            PaymentMethod = PaymentMethod.BankTransfer,
            Currency = historyProduct.Currency,
            Subtotal = historyProduct.Price,
            Total = historyProduct.Price,
            PlacedAt = history.PlacedAt,
            ShippedAt = history.PlacedAt.AddDays(1),
            DeliveredAt = history.DeliveredAt,
            Lines = [new ShopOrderLine { Sku = historyProduct.Sku, Name = historyProduct.Name, Quantity = 1, UnitPrice = historyProduct.Price, ReturnWindowDays = historyProduct.ReturnWindowDays }],
            LedgerEntries =
            [
                new LedgerEntry
                {
                    Type = LedgerEntryType.Payment,
                    Amount = historyProduct.Price,
                    Currency = historyProduct.Currency,
                    Source = LedgerSources.Bank,
                    RecordedAt = history.PlacedAt.AddDays(1),
                    Note = "Bank transfer",
                },
            ],
        });

        db.ShopOrders.AddRange(orders);
        await db.SaveChangesAsync(ct);

        var backfilled = await BackfillExecutedRefundsAsync(ct);
        backfilled += await BookSellerPilotRefundsFromPayPalAsync(orders, ct);
        await ApplyDemoPasswordsAsync(ct);
        logger.LogInformation("Shop data created: {Orders} orders, {Backfilled} executed refunds posted to the books", orders.Count, backfilled);
        return new ShopSeedResult(true, $"Created {customers.Count} customers and {orders.Count} orders.");
    }

    /// <summary>
    /// Adds the shop orders for seed entries that appeared after the shop was created (the seed tool's `add` command),
    /// with their payment and refund in the books. Orders already in the shop are never touched.
    /// </summary>
    private async Task<int> AddMissingOrdersAsync(SeedOutputFile seed, CancellationToken ct)
    {
        var known = (await db.ShopOrders.Where(o => o.PayPalOrderId != null).Select(o => o.PayPalOrderId!).ToListAsync(ct)).ToHashSet();
        var customers = await db.Customers.ToListAsync(ct);
        var products = await db.Products.ToListAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var added = 0;
        foreach (var (spec, index) in ShopSeedData.Orders.Select((s, i) => (s, i)))
        {
            if (!seed.Orders.TryGetValue(spec.SeedKey, out var paypal) || paypal.OrderId is null || paypal.CaptureId is null || known.Contains(paypal.OrderId))
            {
                continue;
            }

            var email = ShopSeedData.Customers.First(c => c.Key == spec.CustomerKey).Email.ToLowerInvariant();
            var customer = customers.FirstOrDefault(c => c.Email == email);
            var product = products.FirstOrDefault(p => p.Sku == spec.Sku);
            if (customer is null || product is null)
            {
                logger.LogWarning("Seed order {Key} skipped: its customer or product is missing from the shop", spec.SeedKey);
                continue;
            }

            db.ShopOrders.Add(BuildOrder(spec, paypal, customer, product, seed.GeneratedAt.UtcDateTime.AddMinutes(index), now));
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Added {Count} new seed orders to the shop", added);
        }

        return added;
    }

    private static ShopOrder BuildOrder(ShopSeedData.OrderSeed spec, SeedOutputOrder paypal, Customer customer, Product product, DateTime placedAt, DateTime now)
    {
        var order = new ShopOrder
        {
            OrderNumber = paypal.Reference,
            Customer = customer,
            PaymentMethod = PaymentMethod.PayPal,
            Currency = paypal.Currency,
            Subtotal = product.Price,
            Discount = spec.Discount,
            DiscountCode = spec.DiscountCode,
            Total = product.Price - spec.Discount,
            PlacedAt = placedAt,
            PayPalOrderId = paypal.OrderId,
            PayPalCaptureId = paypal.CaptureId,
            Lines = [new ShopOrderLine { Sku = product.Sku, Name = product.Name, Quantity = 1, UnitPrice = product.Price, ReturnWindowDays = product.ReturnWindowDays }],
        };
        SetFulfilment(order, spec.Status, now);

        if (spec.BookPayment && paypal.CaptureId is not null)
        {
            order.LedgerEntries.Add(new LedgerEntry
            {
                Type = LedgerEntryType.Payment,
                Amount = paypal.Amount,
                Currency = paypal.Currency,
                PayPalTransactionId = paypal.CaptureId,
                Source = LedgerSources.Checkout,
                RecordedAt = placedAt.AddMinutes(1),
                Note = "PayPal checkout",
            });
        }

        if (spec.BookSeededRefund && paypal.RefundId is not null && paypal.PlannedRefund is { } refunded)
        {
            order.LedgerEntries.Add(new LedgerEntry
            {
                Type = LedgerEntryType.Refund,
                Amount = refunded,
                Currency = paypal.Currency,
                PayPalTransactionId = paypal.RefundId,
                Source = LedgerSources.Checkout,
                RecordedAt = placedAt.AddMinutes(5),
                Note = "Refund recorded by the shop",
            });
        }

        return order;
    }

    /// <summary>Refunds SellerPilot executed before the books existed belong in the books too.</summary>
    /// <summary>
    /// A freshly built shop (the hosted demo starts with an empty database on every restart) would not know the refunds
    /// SellerPilot executed in earlier sessions, and reconciliation would report them as missing from the books. Every
    /// refund SellerPilot sends carries its action ID in PayPal's custom_id, so those refunds are recognised in PayPal's
    /// own data and booked. Refunds made elsewhere (no action ID, e.g. the planted dashboard refund) stay out of the books.
    /// If PayPal can't be reached, the shop is still created; reconciliation will then simply show those refunds.
    /// </summary>
    private async Task<int> BookSellerPilotRefundsFromPayPalAsync(IReadOnlyList<ShopOrder> orders, CancellationToken ct)
    {
        if (paypalRead is null)
        {
            return 0;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var booked = 0;
        foreach (var order in orders.Where(o => o.PayPalOrderId is not null && o.PayPalCaptureId is not null))
        {
            try
            {
                var paypal = await paypalRead.GetOrderAsync(order.PayPalOrderId!, timeout.Token);
                var refunds = (paypal?.PurchaseUnits ?? [])
                    .SelectMany(u => u.Payments?.Refunds ?? [])
                    .Where(r => Guid.TryParse(r.CustomId, out _)
                        && r.Status is RefundStatuses.Completed or RefundStatuses.Pending
                        && r.Amount is not null && r.Amount.TryGetAmount(out _));
                foreach (var refund in refunds)
                {
                    if (await db.LedgerEntries.AnyAsync(e => e.PayPalTransactionId == refund.Id, timeout.Token))
                    {
                        continue;
                    }

                    refund.Amount!.TryGetAmount(out var amount);
                    db.LedgerEntries.Add(new LedgerEntry
                    {
                        ShopOrderId = order.Id,
                        Type = LedgerEntryType.Refund,
                        Amount = amount,
                        Currency = refund.Amount.CurrencyCode ?? order.Currency,
                        PayPalTransactionId = refund.Id,
                        Source = LedgerSources.SellerPilot,
                        RecordedAt = DateTime.TryParse(refund.CreateTime, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var at) ? at : time.GetUtcNow().UtcDateTime,
                        Note = $"Approved action {refund.CustomId}",
                    });
                    booked++;
                }
            }
            catch (Exception ex) when (ex is PayPalApiException or PayPalConfigurationException or HttpRequestException or OperationCanceledException)
            {
                logger.LogWarning("Could not read PayPal order {OrderId} to book earlier SellerPilot refunds: {Error}", order.PayPalOrderId, ex.Message);
                if (timeout.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return booked;
    }

    private async Task<int> BackfillExecutedRefundsAsync(CancellationToken ct)
    {
        var executed = await db.PendingActions.AsNoTracking()
            .Where(a => a.Type == ActionType.Refund && a.Status == ActionStatus.Executed && a.PayPalResultId != null)
            .ToListAsync(ct);
        var count = 0;
        foreach (var action in executed)
        {
            var order = await db.ShopOrders.FirstOrDefaultAsync(o => o.PayPalCaptureId == action.TargetId, ct);
            if (order is null || action.Amount is null || await db.LedgerEntries.AnyAsync(e => e.PayPalTransactionId == action.PayPalResultId, ct))
            {
                continue;
            }

            db.LedgerEntries.Add(new LedgerEntry
            {
                ShopOrderId = order.Id,
                Type = LedgerEntryType.Refund,
                Amount = action.Amount.Value,
                Currency = action.Currency ?? order.Currency,
                PayPalTransactionId = action.PayPalResultId,
                Source = LedgerSources.SellerPilot,
                RecordedAt = action.DecidedAt ?? action.CreatedAt,
                Note = $"Approved action {action.Id}",
            });
            count++;
        }

        await db.SaveChangesAsync(ct);
        return count;
    }

    /// <summary>
    /// Demo data only: renames products (and the order lines that show them) to the current catalogue names, so a database
    /// seeded before a rename doesn't keep the old wording. A real shop would never rewrite past order lines.
    /// </summary>
    private async Task SyncDemoCatalogAsync(CancellationToken ct)
    {
        var products = await db.Products.ToListAsync(ct);
        var lines = await db.ShopOrderLines.ToListAsync(ct);
        foreach (var product in products)
        {
            if (ShopSeedData.RenamedSkus.TryGetValue(product.Sku, out var sku) && products.All(p => p.Sku != sku))
            {
                product.Sku = sku;
            }
        }

        foreach (var line in lines)
        {
            if (ShopSeedData.RenamedSkus.TryGetValue(line.Sku, out var sku))
            {
                line.Sku = sku;
            }
        }

        var names = ShopSeedData.Products.ToDictionary(p => p.Sku, p => p.Name);
        foreach (var product in products.Where(p => names.ContainsKey(p.Sku)))
        {
            product.Name = names[product.Sku];
        }

        foreach (var line in lines.Where(l => names.ContainsKey(l.Sku)))
        {
            line.Name = names[line.Sku];
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Demo catalogue names updated");
        }
    }

    /// <summary>Hashes the configured demo password for every customer (again, if it was changed).</summary>
    private async Task ApplyDemoPasswordsAsync(CancellationToken ct)
    {
        var password = options.Value.DemoPassword;
        if (string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var changed = false;
        foreach (var customer in await db.Customers.ToListAsync(ct))
        {
            if (customer.PasswordHash is not null
                && _hasher.VerifyHashedPassword(customer, customer.PasswordHash, password) != PasswordVerificationResult.Failed)
            {
                continue;
            }

            customer.PasswordHash = _hasher.HashPassword(customer, password);
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private static void SetFulfilment(ShopOrder order, ShopOrderStatus target, DateTime now)
    {
        order.Status = target;
        if (target is ShopOrderStatus.Shipped or ShopOrderStatus.Delivered)
        {
            order.ShippedAt = order.PlacedAt.AddHours(2);
        }

        if (target == ShopOrderStatus.Delivered)
        {
            var delivered = order.PlacedAt.AddHours(20);
            if (delivered <= now)
            {
                order.DeliveredAt = delivered;
            }
            else
            {
                // Seeded moments ago: it can't have arrived yet.
                order.Status = ShopOrderStatus.Shipped;
            }
        }
    }
}
