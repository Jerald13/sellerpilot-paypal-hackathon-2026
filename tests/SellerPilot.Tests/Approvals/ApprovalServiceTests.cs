using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SellerPilot.Api.Agent.Tools;
using SellerPilot.Api.Approvals;
using SellerPilot.Api.Audit;
using SellerPilot.Api.Configuration;
using SellerPilot.Api.Data;
using SellerPilot.Api.Guardrails;
using SellerPilot.Api.PayPal;
using SellerPilot.Api.PayPal.Models;
using SellerPilot.Tests.TestSupport;

namespace SellerPilot.Tests.Approvals;

public sealed class ApprovalServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IPayPalReadClient _read = Substitute.For<IPayPalReadClient>();
    private readonly IPayPalWriteClient _write = Substitute.For<IPayPalWriteClient>();
    private readonly FixedTimeProvider _time = new(PayPalFixtures.Now);

    public void Dispose() => _testDb.Dispose();

    private ApprovalService CreateService(AppDbContext db) => new(
        db,
        _read,
        _write,
        new RefundStateLoader(_read, NullLogger<RefundStateLoader>.Instance),
        new RefundPolicy(Options.Create(new GuardrailOptions { MaxRefundAmount = new() { ["USD"] = 500m } }), _time),
        new AuditService(db, _time, NullLogger<AuditService>.Instance),
        _time,
        NullLogger<ApprovalService>.Instance);

    private async Task<PendingAction> AddRefundProposalAsync(decimal amount = 45.00m)
    {
        await using var db = _testDb.CreateContext();
        var action = new PendingAction
        {
            Id = Guid.NewGuid(),
            Type = ActionType.Refund,
            TargetId = PayPalFixtures.CaptureId,
            Amount = amount,
            Currency = "USD",
            Reason = "Damaged item",
            Summary = "Full refund",
            Status = ActionStatus.Pending,
            CreatedAt = _time.GetUtcNow().UtcDateTime,
        };
        db.PendingActions.Add(action);
        await db.SaveChangesAsync();
        return action;
    }

    private async Task<PendingAction> AddReminderProposalAsync()
    {
        await using var db = _testDb.CreateContext();
        var action = new PendingAction
        {
            Id = Guid.NewGuid(),
            Type = ActionType.InvoiceReminder,
            TargetId = PayPalFixtures.InvoiceId,
            Amount = 120m,
            Currency = "USD",
            Reason = "Overdue",
            Summary = "Reminder",
            Payload = JsonSerializer.Serialize(new ReminderPayload("Invoice #0003", "Hi, a friendly reminder.", "0003", "Test Buyer Tan", "2026-10-20", 13), ActionPayloads.Json),
            Status = ActionStatus.Pending,
            CreatedAt = _time.GetUtcNow().UtcDateTime,
        };
        db.PendingActions.Add(action);
        await db.SaveChangesAsync();
        return action;
    }

    private void GivenCaptureState(string status = "COMPLETED", params string[] refunds)
    {
        _read.GetCaptureAsync(PayPalFixtures.CaptureId, Arg.Any<CancellationToken>())
            .Returns(PayPalFixtures.Parse<PayPalCapture>(PayPalFixtures.Capture(status: status)));
        _read.GetOrderAsync(PayPalFixtures.OrderId, Arg.Any<CancellationToken>())
            .Returns(PayPalFixtures.Parse<PayPalOrder>(PayPalFixtures.Order(capture: PayPalFixtures.Capture(status: status), refunds: refunds)));
    }

    private void GivenRefundSucceeds() =>
        _write.RefundCaptureAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(PayPalFixtures.Parse<PayPalRefund>(PayPalFixtures.Refund("1JU08902781691411", "45.00")));

    [Fact]
    public async Task Approving_a_refund_executes_it_with_the_action_id_as_idempotency_key()
    {
        var action = await AddRefundProposalAsync();
        GivenCaptureState();
        GivenRefundSucceeds();
        await using var db = _testDb.CreateContext();

        var result = await CreateService(db).ApproveAsync(action.Id, CancellationToken.None);

        Assert.Equal(DecisionOutcome.Done, result.Outcome);
        Assert.Equal(ActionStatus.Executed, result.Action!.Status);
        Assert.Equal("1JU08902781691411", result.Action.PayPalResultId);
        await _write.Received(1).RefundCaptureAsync(
            PayPalFixtures.CaptureId,
            Arg.Is<PayPalRefundRequest>(r => r.Amount.Value == "45.00" && r.Amount.CurrencyCode == "USD" && r.CustomId == action.Id.ToString()),
            action.Id.ToString(),
            Arg.Any<CancellationToken>());

        var events = await db.AuditLogs.OrderBy(l => l.Id).Select(l => l.Event).ToListAsync();
        Assert.Equal(new[] { AuditEvents.ActionApproved, AuditEvents.ActionExecuted }, events);
    }

    [Fact]
    public async Task Approval_is_blocked_if_the_capture_was_refunded_since_the_proposal()
    {
        var action = await AddRefundProposalAsync();
        GivenCaptureState("REFUNDED", PayPalFixtures.Refund("R9AAAAAAAAAAAAAAA", "45.00"));
        await using var db = _testDb.CreateContext();

        var result = await CreateService(db).ApproveAsync(action.Id, CancellationToken.None);

        Assert.Equal(ActionStatus.Failed, result.Action!.Status);
        Assert.StartsWith("Blocked at approval", result.Action.Error);
        await _write.DidNotReceiveWithAnyArgs().RefundCaptureAsync(default!, default!, default!, default);
        Assert.Contains(await db.AuditLogs.ToListAsync(), l => l.Event == AuditEvents.GuardrailBlocked);
    }

    [Fact]
    public async Task A_second_approval_click_is_a_conflict_and_refunds_only_once()
    {
        var action = await AddRefundProposalAsync();
        GivenCaptureState();
        GivenRefundSucceeds();

        await using (var db = _testDb.CreateContext())
        {
            await CreateService(db).ApproveAsync(action.Id, CancellationToken.None);
        }

        await using (var db = _testDb.CreateContext())
        {
            var second = await CreateService(db).ApproveAsync(action.Id, CancellationToken.None);
            Assert.Equal(DecisionOutcome.Conflict, second.Outcome);
            Assert.Contains("already Executed", second.Message);
        }

        await _write.ReceivedWithAnyArgs(1).RefundCaptureAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Concurrent_approvals_execute_exactly_once()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sellerpilot-test-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.PendingActions.Add(new PendingAction
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Type = ActionType.Refund,
                    TargetId = PayPalFixtures.CaptureId,
                    Amount = 45m,
                    Currency = "USD",
                    Status = ActionStatus.Pending,
                    CreatedAt = _time.GetUtcNow().UtcDateTime,
                });
                await setup.SaveChangesAsync();
            }

            GivenCaptureState();
            GivenRefundSucceeds();
            var id = Guid.Parse("11111111-1111-1111-1111-111111111111");

            var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
            {
                await using var db = new AppDbContext(options);
                return (await CreateService(db).ApproveAsync(id, CancellationToken.None)).Outcome;
            }));

            Assert.Equal(1, outcomes.Count(o => o == DecisionOutcome.Done));
            Assert.Equal(4, outcomes.Count(o => o == DecisionOutcome.Conflict));
            await _write.ReceivedWithAnyArgs(1).RefundCaptureAsync(default!, default!, default!, default);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Rejecting_never_calls_PayPal_and_blocks_later_approval()
    {
        var action = await AddRefundProposalAsync();
        await using var db = _testDb.CreateContext();
        var service = CreateService(db);

        var rejected = await service.RejectAsync(action.Id, CancellationToken.None);
        var approved = await service.ApproveAsync(action.Id, CancellationToken.None);

        Assert.Equal(ActionStatus.Rejected, rejected.Action!.Status);
        Assert.Equal(DecisionOutcome.Conflict, approved.Outcome);
        await _write.DidNotReceiveWithAnyArgs().RefundCaptureAsync(default!, default!, default!, default);
        Assert.Contains(await db.AuditLogs.ToListAsync(), l => l.Event == AuditEvents.ActionRejected);
    }

    [Fact]
    public async Task Unknown_action_is_not_found()
    {
        await using var db = _testDb.CreateContext();

        var result = await CreateService(db).ApproveAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(DecisionOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Approving_a_reminder_sends_the_approved_text()
    {
        var action = await AddReminderProposalAsync();
        _read.GetInvoiceAsync(PayPalFixtures.InvoiceId, Arg.Any<CancellationToken>()).Returns(PayPalFixtures.Parse<PayPalInvoice>(PayPalFixtures.Invoice()));
        await using var db = _testDb.CreateContext();

        var result = await CreateService(db).ApproveAsync(action.Id, CancellationToken.None);

        Assert.Equal(ActionStatus.Executed, result.Action!.Status);
        await _write.Received(1).SendInvoiceReminderAsync(
            PayPalFixtures.InvoiceId,
            Arg.Is<PayPalInvoiceReminderRequest>(r => r.Note == "Hi, a friendly reminder." && r.Subject == "Invoice #0003"),
            action.Id.ToString(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reminder_is_not_sent_if_the_invoice_was_paid_in_the_meantime()
    {
        var action = await AddReminderProposalAsync();
        _read.GetInvoiceAsync(PayPalFixtures.InvoiceId, Arg.Any<CancellationToken>()).Returns(PayPalFixtures.Parse<PayPalInvoice>(PayPalFixtures.Invoice(status: "PAID")));
        await using var db = _testDb.CreateContext();

        var result = await CreateService(db).ApproveAsync(action.Id, CancellationToken.None);

        Assert.Equal(ActionStatus.Failed, result.Action!.Status);
        Assert.Contains("already paid", result.Action.Error);
        await _write.DidNotReceiveWithAnyArgs().SendInvoiceReminderAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task PayPal_errors_mark_the_action_failed_with_the_reason()
    {
        var action = await AddRefundProposalAsync();
        GivenCaptureState();
        _write.RefundCaptureAsync(default!, default!, default!, default)
            .ThrowsAsyncForAnyArgs(new PayPalApiException(HttpStatusCode.UnprocessableEntity, "UNPROCESSABLE_ENTITY", "dbg", "PayPal POST refund failed: 422 (CAPTURE_FULLY_REFUNDED)", ["CAPTURE_FULLY_REFUNDED"]));
        await using var db = _testDb.CreateContext();

        var result = await CreateService(db).ApproveAsync(action.Id, CancellationToken.None);

        Assert.Equal(ActionStatus.Failed, result.Action!.Status);
        Assert.Contains("CAPTURE_FULLY_REFUNDED", result.Action.Error);
        Assert.Contains(await db.AuditLogs.ToListAsync(), l => l.Event == AuditEvents.ActionFailed);
    }

    [Fact]
    public async Task A_cancelled_request_after_the_claim_still_finishes_the_approval()
    {
        var action = await AddRefundProposalAsync();
        GivenCaptureState();
        GivenRefundSucceeds();
        using var cts = new CancellationTokenSource();
        _write.When(w => w.RefundCaptureAsync(Arg.Any<string>(), Arg.Any<PayPalRefundRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => cts.Cancel()); // the browser goes away while PayPal is processing
        await using var db = _testDb.CreateContext();

        var result = await CreateService(db).ApproveAsync(action.Id, cts.Token);

        Assert.Equal(ActionStatus.Executed, result.Action!.Status);
        await using var verify = _testDb.CreateContext();
        Assert.Equal(ActionStatus.Executed, (await verify.PendingActions.SingleAsync(a => a.Id == action.Id)).Status);
    }

    [Fact]
    public async Task No_response_from_PayPal_is_reported_as_unknown_outcome()
    {
        var action = await AddRefundProposalAsync();
        GivenCaptureState();
        _write.RefundCaptureAsync(default!, default!, default!, default)
            .ThrowsAsyncForAnyArgs(new PayPalApiException(null, "TIMEOUT", null, "PayPal did not respond"));
        await using var db = _testDb.CreateContext();

        var result = await CreateService(db).ApproveAsync(action.Id, CancellationToken.None);

        Assert.Equal(ActionStatus.Failed, result.Action!.Status);
        Assert.Contains("outcome is unknown", result.Action.Error);
    }
}
