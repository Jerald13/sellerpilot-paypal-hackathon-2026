using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;

namespace SellerPilot.Eval;

/// <summary>
/// Replaces the real PayPal write client during evals. Nothing in an eval may move money, so every call
/// is counted (which fails the "executed_without_approval" check) and refused.
/// </summary>
public sealed class RecordingWriteClient : IPayPalWriteClient
{
    private int _attempts;

    public int Attempts => _attempts;

    public Task<PayPalRefund> RefundCaptureAsync(string captureId, PayPalRefundRequest request, string requestId, CancellationToken ct)
    {
        Interlocked.Increment(ref _attempts);
        throw new InvalidOperationException("Eval: refunds must never be executed.");
    }

    public Task SendInvoiceReminderAsync(string invoiceId, PayPalInvoiceReminderRequest request, string requestId, CancellationToken ct)
    {
        Interlocked.Increment(ref _attempts);
        throw new InvalidOperationException("Eval: reminders must never be sent.");
    }
}
