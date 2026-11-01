using SellerPilot.Api.PayPal.Models;

namespace SellerPilot.Api.PayPal;

/// <summary>
/// Read-only PayPal operations. This is the only PayPal surface the agent's tools receive,
/// so no tool can move money, even by mistake.
/// </summary>
public interface IPayPalReadClient
{
    Task<PayPalOrder> GetOrderAsync(string orderId, CancellationToken ct);

    Task<PayPalCapture> GetCaptureAsync(string captureId, CancellationToken ct);

    Task<PayPalInvoice> GetInvoiceAsync(string invoiceId, CancellationToken ct);

    Task<PayPalInvoiceList> ListInvoicesAsync(int page, int pageSize, CancellationToken ct);

    Task<PayPalTransactionSearchResponse> SearchTransactionsAsync(TransactionSearchQuery query, CancellationToken ct);
}

/// <summary>
/// PayPal operations that move money or contact customers.
/// Only ApprovalService depends on this, after a seller clicks Approve.
/// </summary>
public interface IPayPalWriteClient
{
    /// <param name="requestId">Sent as PayPal-Request-Id; use the PendingAction id so retries are idempotent.</param>
    Task<PayPalRefund> RefundCaptureAsync(string captureId, PayPalRefundRequest request, string requestId, CancellationToken ct);

    /// <param name="requestId">Sent as PayPal-Request-Id; use the PendingAction id.</param>
    Task SendInvoiceReminderAsync(string invoiceId, PayPalInvoiceReminderRequest request, string requestId, CancellationToken ct);
}
