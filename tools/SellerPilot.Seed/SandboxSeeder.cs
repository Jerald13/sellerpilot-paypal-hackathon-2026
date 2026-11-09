using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;

namespace SellerPilot.Seed;

public sealed class SeedApiException(int status, string body, string operation)
    : Exception($"{operation} failed: HTTP {status} {Summarize(body)}")
{
    public int Status { get; } = status;

    private static string Summarize(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() : root.TryGetProperty("error", out var e) ? e.GetString() : null;
            var issues = root.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Array
                ? string.Join(", ", d.EnumerateArray().Select(x => x.TryGetProperty("issue", out var i) ? i.GetString() : null).Where(x => x is not null))
                : "";
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : root.TryGetProperty("error_description", out var ed) ? ed.GetString() : "";
            return $"{name} {(issues.Length > 0 ? $"({issues}) " : "")}{message}".Trim();
        }
        catch (JsonException)
        {
            return body.Length > 200 ? body[..200] : body;
        }
    }
}

/// <summary>
/// Creates the demo data in the PayPal sandbox: captured orders (some refunded), and invoices (some overdue, some paid).
/// Uses the same sandbox-only HttpClient and token cache as the API. Never touches anything but the sandbox.
/// </summary>
public sealed class SandboxSeeder(IHttpClientFactory httpClientFactory, IPayPalTokenProvider tokens, IConfiguration configuration, TimeProvider time)
{
    private const string ReturnUrl = "https://example.com/sellerpilot/approved";
    private const string CancelUrl = "https://example.com/sellerpilot/cancelled";

    public async Task<int> CheckAsync(CancellationToken ct)
    {
        var scopes = await tokens.GetGrantedScopesAsync(ct);
        Console.WriteLine("OK: PayPal sandbox credentials work (access token received).");
        Report("Invoicing", scopes.Any(s => s.Contains("/invoicing", StringComparison.Ordinal)));
        Report("Transaction Search", scopes.Any(s => s.Contains("/reporting/search/read", StringComparison.Ordinal)),
            "Enable 'Transaction search' for the sandbox app in the developer dashboard (new tokens can take a while to include it).");
        Report("Payments (refunds)", scopes.Any(s => s.Contains("/payments/", StringComparison.Ordinal) || s.Contains("/payments", StringComparison.Ordinal)));
        Console.WriteLine("Card processing can't be checked from scopes; `run` tries it and falls back to buyer approval.");

        // Scopes can lag behind dashboard changes, so also try the endpoints themselves (read-only).
        Console.WriteLine("Live read-only probes:");
        await ProbeAsync("Invoicing (GET /v2/invoicing/invoices)", "v2/invoicing/invoices?page=1&page_size=1");
        var end = time.GetUtcNow();
        await ProbeAsync("Transaction Search (GET /v1/reporting/transactions)",
            $"v1/reporting/transactions?start_date={Uri.EscapeDataString(end.AddDays(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))}" +
            $"&end_date={Uri.EscapeDataString(end.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))}&page_size=1");

        async Task ProbeAsync(string name, string path)
        {
            try
            {
                await SendAsync(HttpMethod.Get, path, null, null, ct);
                Console.WriteLine($"  [x] {name}: works");
            }
            catch (SeedApiException ex)
            {
                Console.WriteLine($"  [ ] {name}: {ex.Message}");
            }
        }
        if (configuration.GetValue("verbose", false) || Environment.GetCommandLineArgs().Contains("--verbose"))
        {
            Console.WriteLine("Granted scopes:");
            foreach (var scope in scopes.Order())
            {
                Console.WriteLine($"  {scope}");
            }
        }

        return 0;

        static void Report(string feature, bool enabled, string? hint = null) =>
            Console.WriteLine($"  {(enabled ? "[x]" : "[ ]")} {feature}{(enabled || hint is null ? "" : " - " + hint)}");
    }

    public async Task<int> RunAsync(string outputPath, bool force, CancellationToken ct)
    {
        if (File.Exists(outputPath) && !force)
        {
            Console.Error.WriteLine($"{outputPath} already exists. Re-run with --force to create a second set of sandbox data.");
            return 1;
        }

        var output = new SeedOutput { GeneratedAt = time.GetUtcNow(), RunTag = RandomTag() };
        var useCard = !configuration.GetValue("Seed:SkipCard", false);
        var cardVerified = false;
        var number = 1000;

        Console.WriteLine($"Creating {SeedPlan.Orders.Count} orders (run tag {output.RunTag})...");
        foreach (var spec in SeedPlan.Orders)
        {
            number++;
            var seeded = new SeededOrder
            {
                Buyer = spec.Buyer,
                Currency = spec.Currency,
                Amount = spec.Amount,
                Description = spec.Description,
                Reference = $"SP-{output.RunTag}-{number}",
                PlannedRefund = spec.RefundAfterCapture,
            };
            output.Orders[spec.Key] = seeded;
            try
            {
                if (useCard)
                {
                    try
                    {
                        await CreateCardOrderAsync(spec, seeded, output, ct);
                        cardVerified |= seeded.Status == "CAPTURED";
                    }
                    catch (SeedApiException ex) when (!cardVerified && ex.Status is >= 400 and < 500 and not 401)
                    {
                        output.Warnings.Add($"Card payments are not available for this sandbox app ({ex.Message}). Switched to buyer approval.");
                        Console.WriteLine("  Card flow not available; switching to buyer-approval orders.");
                        useCard = false;
                    }
                }

                if (!useCard)
                {
                    await CreateApprovalOrderAsync(spec, seeded, ct);
                }

                if (seeded.Status == "CAPTURED" && spec.RefundAfterCapture is { } refund)
                {
                    await RefundAsync(seeded, refund, ct);
                }
            }
            catch (SeedApiException ex)
            {
                seeded.Status = "FAILED";
                seeded.Error = ex.Message;
                output.Warnings.Add($"Order {spec.Key}: {ex.Message}");
            }

            Console.WriteLine($"  {spec.Key,-20} {seeded.Status,-24} {seeded.OrderId}");
        }

        output.OrderFlow = output.Orders.Values.Any(o => o.Status == "AWAITING_BUYER_APPROVAL") ? "buyer-approval" : "card";

        Console.WriteLine($"Creating {SeedPlan.Invoices.Count} invoices...");
        var index = 0;
        foreach (var spec in SeedPlan.Invoices)
        {
            index++;
            var seeded = new SeededInvoice
            {
                Buyer = $"{spec.BuyerGiven} {spec.BuyerSurname}",
                Currency = spec.Currency,
                Amount = spec.UnitPrice * spec.Quantity,
            };
            output.Invoices[spec.Key] = seeded;
            try
            {
                await CreateInvoiceAsync(spec, seeded, $"SP-{output.RunTag}-{index:00}", output, ct);
            }
            catch (SeedApiException ex)
            {
                seeded.Status = "FAILED";
                seeded.Error = ex.Message;
                output.Warnings.Add($"Invoice {spec.Key}: {ex.Message}");
            }

            Console.WriteLine($"  {spec.Key,-20} {seeded.Status,-24} {seeded.InvoiceId}");
        }

        await SaveAsync(outputPath, output, ct);
        PrintNextSteps(output, outputPath);
        return output.Orders.Values.Any(o => o.Status == "FAILED") || output.Invoices.Values.Any(i => i.Status == "FAILED") ? 2 : 0;
    }

    /// <summary>
    /// Creates only the planned orders that seed-output.json doesn't have yet (with their planned refunds) and adds them to the file.
    /// Everything already seeded, orders and invoices, is left alone.
    /// </summary>
    public async Task<int> AddAsync(string outputPath, CancellationToken ct)
    {
        if (!File.Exists(outputPath))
        {
            Console.Error.WriteLine($"{outputPath} not found. Run `dotnet run --project tools/SellerPilot.Seed -- run` first.");
            return 1;
        }

        var output = JsonSerializer.Deserialize<SeedOutput>(await File.ReadAllTextAsync(outputPath, ct), SeedOutput.Json)!;
        var missing = SeedPlan.MissingOrders(output.Orders);
        if (missing.Count == 0)
        {
            Console.WriteLine("Nothing to add: every planned order is already in seed-output.json.");
            return 0;
        }

        // Try the card flow first; like `run`, fall back to buyer approval when the sandbox app can't take cards.
        var useCard = !configuration.GetValue("Seed:SkipCard", false);
        var number = SeedPlan.NextOrderNumber(output.Orders.Values.Where(o => o.Status != "FAILED"));
        Console.WriteLine($"Adding {missing.Count} orders (run tag {output.RunTag}, from number {number})...");
        foreach (var spec in missing)
        {
            var seeded = new SeededOrder
            {
                Buyer = spec.Buyer,
                Currency = spec.Currency,
                Amount = spec.Amount,
                Description = spec.Description,
                Reference = $"SP-{output.RunTag}-{number++}",
                PlannedRefund = spec.RefundAfterCapture,
            };
            output.Orders[spec.Key] = seeded;
            try
            {
                if (useCard)
                {
                    try
                    {
                        await CreateCardOrderAsync(spec, seeded, output, ct);
                    }
                    catch (SeedApiException ex) when (ex.Status is >= 400 and < 500 and not 401)
                    {
                        Console.WriteLine($"  Card flow not available ({ex.Message}); switching to buyer-approval orders.");
                        useCard = false;
                    }
                }

                if (!useCard)
                {
                    await CreateApprovalOrderAsync(spec, seeded, ct);
                }

                if (seeded.Status == "CAPTURED" && spec.RefundAfterCapture is { } refund)
                {
                    await RefundAsync(seeded, refund, ct);
                }
            }
            catch (SeedApiException ex)
            {
                seeded.Status = "FAILED";
                seeded.Error = ex.Message;
                output.Warnings.Add($"Order {spec.Key}: {ex.Message}");
            }

            Console.WriteLine($"  {spec.Key,-20} {seeded.Reference,-14} {seeded.Status,-24} {seeded.OrderId}");
        }

        output.OrderFlow = output.Orders.Values.Any(o => o.Status == "AWAITING_BUYER_APPROVAL") ? "buyer-approval" : "card";
        await SaveAsync(outputPath, output, ct);
        PrintNextSteps(output, outputPath);
        Console.WriteLine("Restart the API so the shop picks up the new orders.");
        return missing.Any(s => output.Orders[s.Key].Status == "FAILED") ? 2 : 0;
    }

    /// <summary>Read-only: shows each seeded capture's live status at PayPal (the dashboard can lag).</summary>
    public async Task<int> StatusAsync(string outputPath, CancellationToken ct)
    {
        if (!File.Exists(outputPath))
        {
            Console.Error.WriteLine($"{outputPath} not found.");
            return 1;
        }

        var output = JsonSerializer.Deserialize<SeedOutput>(await File.ReadAllTextAsync(outputPath, ct), SeedOutput.Json)!;
        foreach (var (key, seeded) in output.Orders.Where(o => o.Value.CaptureId is not null))
        {
            try
            {
                var capture = await SendAsync(HttpMethod.Get, $"v2/payments/captures/{seeded.CaptureId}", null, null, ct);
                var status = capture.GetProperty("status").GetString();
                var reason = capture.TryGetProperty("status_details", out var d) && d.TryGetProperty("reason", out var r) ? $" ({r.GetString()})" : "";
                Console.WriteLine($"  {key,-20} {seeded.Currency} {seeded.Amount,8:0.00}  {status}{reason}");
            }
            catch (SeedApiException ex)
            {
                Console.WriteLine($"  {key,-20} ERROR {ex.Message}");
            }
        }

        return 0;
    }

    public async Task<int> CaptureAsync(string outputPath, CancellationToken ct)
    {
        if (!File.Exists(outputPath))
        {
            Console.Error.WriteLine($"{outputPath} not found. Run `dotnet run --project tools/SellerPilot.Seed -- run` first.");
            return 1;
        }

        var output = JsonSerializer.Deserialize<SeedOutput>(await File.ReadAllTextAsync(outputPath, ct), SeedOutput.Json)!;
        foreach (var (key, seeded) in output.Orders.Where(o => o.Value.Status == "AWAITING_BUYER_APPROVAL" && o.Value.OrderId is not null))
        {
            try
            {
                var order = await SendAsync(HttpMethod.Get, $"v2/checkout/orders/{seeded.OrderId}", null, null, ct);
                var status = order.GetProperty("status").GetString();
                if (status == "APPROVED")
                {
                    var captured = await SendAsync(HttpMethod.Post, $"v2/checkout/orders/{seeded.OrderId}/capture", null, Guid.NewGuid().ToString(), ct);
                    ApplyCapture(captured, seeded, output);
                    if (seeded.Status == "CAPTURED" && seeded.PlannedRefund is { } refund)
                    {
                        await RefundAsync(seeded, refund, ct);
                    }
                }
                else
                {
                    Console.WriteLine($"  {key,-20} still {status}; approve it here: {seeded.ApproveUrl}");
                    continue;
                }
            }
            catch (SeedApiException ex)
            {
                output.Warnings.Add($"Capture {key}: {ex.Message}");
            }

            Console.WriteLine($"  {key,-20} {seeded.Status,-24} {seeded.CaptureId}");
        }

        output.OrderFlow = output.Orders.Values.Any(o => o.Status == "AWAITING_BUYER_APPROVAL") ? "buyer-approval" : "card";
        await SaveAsync(outputPath, output, ct);
        PrintNextSteps(output, outputPath);
        return 0;
    }

    private async Task CreateCardOrderAsync(SeedPlan.OrderSpec spec, SeededOrder seeded, SeedOutput output, CancellationToken ct)
    {
        var card = new JsonObject
        {
            ["name"] = spec.Buyer,
            ["number"] = configuration["Seed:CardNumber"] ?? "4111111111111111",
            ["expiry"] = configuration["Seed:CardExpiry"] ?? time.GetUtcNow().AddYears(3).ToString("yyyy-MM", CultureInfo.InvariantCulture),
            ["security_code"] = "123",
            ["billing_address"] = new JsonObject
            {
                ["address_line_1"] = "2211 N First Street",
                ["admin_area_2"] = "San Jose",
                ["admin_area_1"] = "CA",
                ["postal_code"] = "95131",
                ["country_code"] = "US",
            },
        };

        var order = await SendAsync(HttpMethod.Post, "v2/checkout/orders", OrderBody(spec, seeded.Reference, new JsonObject { ["card"] = card }), Guid.NewGuid().ToString(), ct);
        seeded.OrderId = order.GetProperty("id").GetString();
        switch (order.GetProperty("status").GetString())
        {
            case "COMPLETED":
                ApplyCapture(order, seeded, output);
                break;
            case "APPROVED":
            case "CREATED":
                ApplyCapture(await SendAsync(HttpMethod.Post, $"v2/checkout/orders/{seeded.OrderId}/capture", null, Guid.NewGuid().ToString(), ct), seeded, output);
                break;
            default:
                // e.g. PAYER_ACTION_REQUIRED for 3-D Secure.
                seeded.Status = "AWAITING_BUYER_APPROVAL";
                seeded.ApproveUrl = Link(order, "payer-action") ?? Link(order, "approve");
                break;
        }
    }

    private async Task CreateApprovalOrderAsync(SeedPlan.OrderSpec spec, SeededOrder seeded, CancellationToken ct)
    {
        var paypal = new JsonObject
        {
            ["experience_context"] = new JsonObject
            {
                ["brand_name"] = "Emma's Textile Studio (SellerPilot demo)",
                ["shipping_preference"] = "NO_SHIPPING",
                ["user_action"] = "PAY_NOW",
                ["return_url"] = ReturnUrl,
                ["cancel_url"] = CancelUrl,
            },
        };
        var order = await SendAsync(HttpMethod.Post, "v2/checkout/orders", OrderBody(spec, seeded.Reference, new JsonObject { ["paypal"] = paypal }), Guid.NewGuid().ToString(), ct);
        seeded.OrderId = order.GetProperty("id").GetString();
        seeded.Status = "AWAITING_BUYER_APPROVAL";
        seeded.ApproveUrl = Link(order, "payer-action") ?? Link(order, "approve");
    }

    private static JsonObject OrderBody(SeedPlan.OrderSpec spec, string reference, JsonObject paymentSource)
    {
        var value = Money.Format(spec.Amount, spec.Currency);
        return new JsonObject
        {
            ["intent"] = "CAPTURE",
            ["purchase_units"] = new JsonArray(new JsonObject
            {
                ["reference_id"] = spec.Key,
                ["invoice_id"] = reference,
                ["custom_id"] = $"seed:{spec.Key}",
                ["description"] = spec.Description,
                ["amount"] = new JsonObject
                {
                    ["currency_code"] = spec.Currency,
                    ["value"] = value,
                    ["breakdown"] = new JsonObject { ["item_total"] = MoneyJson(spec.Amount, spec.Currency) },
                },
                ["items"] = new JsonArray(new JsonObject
                {
                    ["name"] = spec.Item,
                    ["quantity"] = "1",
                    ["unit_amount"] = MoneyJson(spec.Amount, spec.Currency),
                }),
            }),
            ["payment_source"] = paymentSource,
        };
    }

    private static void ApplyCapture(JsonElement order, SeededOrder seeded, SeedOutput output)
    {
        var capture = order.GetProperty("purchase_units")[0].GetProperty("payments").GetProperty("captures")[0];
        seeded.CaptureId = capture.GetProperty("id").GetString();
        var status = capture.GetProperty("status").GetString();
        if (status == "COMPLETED")
        {
            seeded.Status = "CAPTURED";
            seeded.RemainingRefundable = seeded.Amount;
            return;
        }

        seeded.Status = $"CAPTURE_{status}";
        var reason = capture.TryGetProperty("status_details", out var details) && details.TryGetProperty("reason", out var r) ? r.GetString() : null;
        output.Warnings.Add(
            $"Capture {seeded.CaptureId} is {status}{(reason is null ? "" : $" ({reason})")}. " +
            "If the reason is about receiving preferences, log in to the sandbox business account and accept the payment (or allow that currency).");
    }

    private async Task RefundAsync(SeededOrder seeded, decimal amount, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["amount"] = MoneyJson(amount, seeded.Currency),
            ["note_to_payer"] = "Refund from Emma's Textile Studio (sandbox demo)",
        };
        var refund = await SendAsync(HttpMethod.Post, $"v2/payments/captures/{seeded.CaptureId}/refund", body, Guid.NewGuid().ToString(), ct);
        seeded.RefundId = refund.GetProperty("id").GetString();
        seeded.Status = amount >= seeded.Amount ? "REFUNDED" : "PARTIALLY_REFUNDED";
        seeded.RemainingRefundable = seeded.Amount - amount;
    }

    private async Task CreateInvoiceAsync(SeedPlan.InvoiceSpec spec, SeededInvoice seeded, string invoiceNumber, SeedOutput output, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var email = configuration["Seed:BuyerEmail"] ?? $"{spec.BuyerSurname.ToLowerInvariant()}.testbuyer@example.com";
        JsonObject Body(DateOnly invoiceDate, JsonObject paymentTerm) => new()
        {
            ["detail"] = new JsonObject
            {
                ["invoice_number"] = invoiceNumber,
                ["invoice_date"] = invoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["currency_code"] = spec.Currency,
                ["note"] = "Thank you for shopping with Emma's Textile Studio!",
                ["payment_term"] = paymentTerm,
            },
            ["primary_recipients"] = new JsonArray(new JsonObject
            {
                ["billing_info"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["given_name"] = spec.BuyerGiven, ["surname"] = spec.BuyerSurname },
                    ["email_address"] = email,
                },
            }),
            ["items"] = new JsonArray(new JsonObject
            {
                ["name"] = spec.Item,
                ["quantity"] = spec.Quantity.ToString(CultureInfo.InvariantCulture),
                ["unit_amount"] = MoneyJson(spec.UnitPrice, spec.Currency),
                ["unit_of_measure"] = "QUANTITY",
            }),
        };

        var due = today.AddDays(-spec.DueDaysAgo);
        JsonElement created;
        try
        {
            created = await SendAsync(HttpMethod.Post, "v2/invoicing/invoices",
                Body(today.AddDays(-spec.InvoiceDaysAgo), new JsonObject { ["term_type"] = "DUE_ON_DATE_SPECIFIED", ["due_date"] = due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }),
                Guid.NewGuid().ToString(), ct);
        }
        catch (SeedApiException ex) when (ex.Status is 400 or 422)
        {
            // Some sandbox accounts reject back-dated invoices; fall back to "due today".
            output.Warnings.Add($"Invoice {spec.Key}: back-dated invoice rejected ({ex.Message}); created it due today instead, so it is not overdue.");
            due = today;
            created = await SendAsync(HttpMethod.Post, "v2/invoicing/invoices", Body(today, new JsonObject { ["term_type"] = "DUE_ON_RECEIPT" }), Guid.NewGuid().ToString(), ct);
        }

        var invoiceId = created.TryGetProperty("id", out var id)
            ? id.GetString()
            : created.GetProperty("href").GetString()!.Split('/').Last();
        seeded.InvoiceId = invoiceId;
        seeded.InvoiceNumber = invoiceNumber;
        seeded.DueDate = due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        seeded.Status = "DRAFT";

        await SendAsync(HttpMethod.Post, $"v2/invoicing/invoices/{invoiceId}/send", new JsonObject { ["send_to_invoicer"] = false, ["send_to_recipient"] = true }, Guid.NewGuid().ToString(), ct);
        seeded.Status = "SENT";

        if (spec.MarkPaid)
        {
            await SendAsync(HttpMethod.Post, $"v2/invoicing/invoices/{invoiceId}/payments", new JsonObject
            {
                ["method"] = "BANK_TRANSFER",
                ["payment_date"] = today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["amount"] = MoneyJson(seeded.Amount, spec.Currency),
                ["note"] = "Paid by bank transfer (sandbox seed)",
            }, Guid.NewGuid().ToString(), ct);
            seeded.Status = "MARKED_AS_PAID";
        }
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, JsonObject? body, string? requestId, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(PayPalTokenProvider.HttpClientName);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(ct));
        request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
        if (requestId is not null)
        {
            request.Headers.TryAddWithoutValidation("PayPal-Request-Id", requestId);
        }

        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent(body?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new SeedApiException((int)response.StatusCode, text, $"{method} {path}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static JsonObject MoneyJson(decimal amount, string currency) =>
        new() { ["currency_code"] = currency, ["value"] = Money.Format(amount, currency) };

    private static string? Link(JsonElement resource, string rel) =>
        resource.TryGetProperty("links", out var links)
            ? links.EnumerateArray().FirstOrDefault(l => l.GetProperty("rel").GetString() == rel) is { ValueKind: JsonValueKind.Object } link
                ? link.GetProperty("href").GetString()
                : null
            : null;

    private static string RandomTag() =>
        string.Concat(Enumerable.Range(0, 4).Select(_ => "ABCDEFGHJKMNPQRSTUVWXYZ23456789"[Random.Shared.Next(31)]));

    private static async Task SaveAsync(string path, SeedOutput output, CancellationToken ct)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(output, SeedOutput.Json), ct);
        Console.WriteLine($"Wrote {path}");
    }

    private static void PrintNextSteps(SeedOutput output, string outputPath)
    {
        foreach (var warning in output.Warnings)
        {
            Console.WriteLine($"WARNING: {warning}");
        }

        var waiting = output.Orders.Where(o => o.Value.Status == "AWAITING_BUYER_APPROVAL").ToList();
        if (waiting.Count == 0)
        {
            Console.WriteLine("Done. Transaction Search can take a few hours to show these payments; GetOrder works immediately.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"{waiting.Count} orders need the sandbox BUYER (personal account) to approve them:");
        foreach (var (key, order) in waiting)
        {
            Console.WriteLine($"  {key,-20} {order.ApproveUrl}");
        }

        Console.WriteLine("Open each link, log in with the sandbox personal account, approve (you'll land on example.com - that's fine),");
        Console.WriteLine("then run: dotnet run --project tools/SellerPilot.Seed -- capture");
        Console.WriteLine($"(state is kept in {outputPath})");
    }
}
