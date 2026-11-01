using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;

namespace SellerPilot.Tests.TestSupport;

/// <summary>
/// A fake PayPal read client holding the same orders as the real sandbox (run DVAN), after the USD 45 scarf refund
/// was approved in SellerPilot. Tests can change single orders before use.
/// </summary>
public sealed class SandboxPayPal
{
    public const string ScarfRefundId = "44527045F66134616";
    public const string KeychainCaptureId = "24P59947NC3418222";

    public IPayPalReadClient Client { get; } = Substitute.For<IPayPalReadClient>();

    private readonly Dictionary<string, (string OrderId, string Json)> _captures = new();
    private readonly List<PayPalTransactionDetail> _transactions = [];

    public SandboxPayPal()
    {
        AddOrder("4X8699120B681554H", "6YM65696F5973222C", "45.00", "USD", "REFUNDED", (ScarfRefundId, "45.00"));
        AddOrder("3TC19417WU947222P", "4CL16101SL049693V", "120.00", "USD", "COMPLETED");
        AddOrder("46F59753TW901143S", "5GT08587M7102403F", "68.00", "SGD", "PENDING");
        AddOrder("24S697138A8192733", "4U66538538985522M", "150.00", "MYR", "COMPLETED");
        AddOrder("7R3994090M8505904", "9SJ46960HW343273P", "25.00", "USD", "REFUNDED", ("6X698339418911635", "25.00"));
        AddOrder("9P344288XW6898834", "30379916G6480042M", "80.00", "USD", "PARTIALLY_REFUNDED", ("9YK317787A915471L", "30.00"));
        AddOrder("85690008WY900754C", "4MS80554JR003113B", "60.00", "USD", "COMPLETED");
        AddOrder("1JG9681911260461C", "04D88916GP735262X", "150.00", "USD", "COMPLETED");
        AddOrder("93270599TG532911K", KeychainCaptureId, "15.00", "USD", "COMPLETED");
        AddOrder("38362659L10908516", "8V099063E6792002K", "32.00", "SGD", "PENDING");

        Client.SearchTransactionsAsync(Arg.Any<TransactionSearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => new PayPalTransactionSearchResponse(_transactions.ToList(), null, null, "2026-11-02T08:00:00+0000", 1, _transactions.Count, 1));
    }

    /// <summary>Registers (or replaces) an order with one capture and optional refunds, plus its Transaction Search rows.</summary>
    public void AddOrder(string orderId, string captureId, string amount, string currency, string captureStatus, params (string Id, string Amount)[] refunds)
    {
        var capture = PayPalFixtures.Capture(captureId, orderId, amount, currency, captureStatus, createTime: "2026-10-31T14:05:00Z");
        var refundJson = refunds.Select(r => PayPalFixtures.Refund(r.Id, r.Amount, currency, captureId: captureId, createTime: "2026-10-31T15:00:00Z")).ToList();
        var order = PayPalFixtures.Order(orderId, capture, refundJson, amount: amount, currency: currency);
        Client.GetOrderAsync(orderId, Arg.Any<CancellationToken>()).Returns(_ => PayPalFixtures.Parse<PayPalOrder>(order));
        Client.GetCaptureAsync(captureId, Arg.Any<CancellationToken>()).Returns(_ => PayPalFixtures.Parse<PayPalCapture>(capture));
        _captures[captureId] = (orderId, order);

        _transactions.RemoveAll(t => t.TransactionInfo?.TransactionId == captureId || refunds.Any(r => r.Id == t.TransactionInfo?.TransactionId));
        _transactions.Add(Transaction(captureId, "T0006", amount, currency, captureStatus == "PENDING" ? "P" : "S"));
        foreach (var refund in refunds)
        {
            _transactions.Add(Transaction(refund.Id, "T1107", "-" + refund.Amount, currency, "S"));
        }
    }

    public void OrderIsMissing(string orderId) =>
        Client.GetOrderAsync(orderId, Arg.Any<CancellationToken>()).ThrowsAsync(new PayPalApiException(System.Net.HttpStatusCode.NotFound, "RESOURCE_NOT_FOUND", null, "Order not found"));

    public void PayPalIsDown() =>
        Client.GetOrderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new PayPalApiException(null, "TIMEOUT", null, "PayPal did not respond"));

    public void TransactionSearchIsNotEnabled() =>
        Client.SearchTransactionsAsync(Arg.Any<TransactionSearchQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PayPalApiException(System.Net.HttpStatusCode.Forbidden, "NOT_AUTHORIZED", null, "Not authorized"));

    private static PayPalTransactionDetail Transaction(string id, string code, string amount, string currency, string status) =>
        new(new PayPalTransactionInfo(id, null, code, "2026-10-31T14:05:00+0000", null, new PayPalMoney(currency, amount), null, status, null, null, null, null),
            new PayPalPayerInfo(null, "buyer@example.com", new PayPalPayerName("John", "Doe", null), "US"),
            null);
}
