using System.Text.Json;
using SellerPilot.Api.PayPal.Models;

namespace SellerPilot.Tests.TestSupport;

/// <summary>Realistic PayPal sandbox payloads (shapes follow the Orders v2, Payments v2 and Invoicing v2 docs).</summary>
public static class PayPalFixtures
{
    public const string OrderId = "5O190127TN364715T";
    public const string CaptureId = "3C679366HH908993F";
    public const string InvoiceId = "INV2-Z56S-5LLA-Q52L-CPZ5";
    public static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    public static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, PayPalJson.Options)!;

    public static string Capture(
        string captureId = CaptureId,
        string orderId = OrderId,
        string amount = "45.00",
        string currency = "USD",
        string status = "COMPLETED",
        string createTime = "2026-10-20T10:00:00Z") => $$"""
        {
          "id": "{{captureId}}",
          "status": "{{status}}",
          "amount": { "currency_code": "{{currency}}", "value": "{{amount}}" },
          "final_capture": true,
          "seller_protection": { "status": "ELIGIBLE" },
          "create_time": "{{createTime}}",
          "update_time": "{{createTime}}",
          "supplementary_data": { "related_ids": { "order_id": "{{orderId}}" } },
          "links": [
            { "href": "https://api-m.sandbox.paypal.com/v2/payments/captures/{{captureId}}", "rel": "self", "method": "GET" },
            { "href": "https://api-m.sandbox.paypal.com/v2/payments/captures/{{captureId}}/refund", "rel": "refund", "method": "POST" },
            { "href": "https://api-m.sandbox.paypal.com/v2/checkout/orders/{{orderId}}", "rel": "up", "method": "GET" }
          ]
        }
        """;

    public static string Refund(
        string refundId,
        string amount,
        string currency = "USD",
        string status = "COMPLETED",
        string captureId = CaptureId,
        string createTime = "2026-10-22T09:30:00Z") => $$"""
        {
          "id": "{{refundId}}",
          "status": "{{status}}",
          "amount": { "currency_code": "{{currency}}", "value": "{{amount}}" },
          "create_time": "{{createTime}}",
          "update_time": "{{createTime}}",
          "links": [
            { "href": "https://api-m.sandbox.paypal.com/v2/payments/refunds/{{refundId}}", "rel": "self", "method": "GET" },
            { "href": "https://api-m.sandbox.paypal.com/v2/payments/captures/{{captureId}}", "rel": "up", "method": "GET" }
          ]
        }
        """;

    public static string Order(
        string orderId = OrderId,
        string? capture = null,
        IEnumerable<string>? refunds = null,
        string description = "Blue silk scarf",
        string amount = "45.00",
        string currency = "USD",
        string email = "amy.buyer@example.com") => $$"""
        {
          "id": "{{orderId}}",
          "status": "COMPLETED",
          "intent": "CAPTURE",
          "payment_source": {
            "paypal": {
              "email_address": "{{email}}",
              "account_id": "QYR5Z8XDVJNXQ",
              "name": { "given_name": "Test Buyer", "surname": "Amy" },
              "address": { "country_code": "US" }
            }
          },
          "purchase_units": [
            {
              "reference_id": "default",
              "invoice_id": "SP-1001",
              "description": {{JsonSerializer.Serialize(description)}},
              "amount": { "currency_code": "{{currency}}", "value": "{{amount}}" },
              "items": [
                { "name": "Silk scarf", "quantity": "1", "unit_amount": { "currency_code": "{{currency}}", "value": "{{amount}}" } }
              ],
              "shipping": { "name": { "full_name": "Test Buyer Amy" }, "address": { "address_line_1": "1 Main St", "country_code": "US" } },
              "payments": {
                "captures": [ {{capture ?? Capture(orderId: orderId, amount: amount, currency: currency)}} ],
                "refunds": [ {{string.Join(",", refunds ?? [])}} ]
              }
            }
          ],
          "payer": {
            "name": { "given_name": "Test Buyer", "surname": "Amy" },
            "email_address": "{{email}}",
            "payer_id": "QYR5Z8XDVJNXQ",
            "address": { "country_code": "US" }
          },
          "create_time": "2026-10-20T09:58:00Z",
          "update_time": "2026-10-20T10:00:00Z"
        }
        """;

    public static string Invoice(
        string invoiceId = InvoiceId,
        string status = "SENT",
        string number = "0003",
        string amount = "120.00",
        string currency = "USD",
        string dueDate = "2026-10-20",
        string note = "Thank you for your order!") => $$"""
        {
          "id": "{{invoiceId}}",
          "status": "{{status}}",
          "detail": {
            "invoice_number": "{{number}}",
            "invoice_date": "2026-10-05",
            "currency_code": "{{currency}}",
            "note": {{JsonSerializer.Serialize(note)}},
            "payment_term": { "term_type": "DUE_ON_DATE_SPECIFIED", "due_date": "{{dueDate}}" },
            "metadata": { "create_time": "2026-10-05T08:00:00Z", "last_sent_time": "2026-10-05T08:01:00Z" }
          },
          "invoicer": { "email_address": "seller@example.com" },
          "primary_recipients": [
            { "billing_info": { "name": { "given_name": "Test Buyer", "surname": "Tan" }, "email_address": "tan.buyer@example.com" } }
          ],
          "amount": { "currency_code": "{{currency}}", "value": "{{amount}}" },
          "due_amount": { "currency_code": "{{currency}}", "value": "{{amount}}" }
        }
        """;
}
