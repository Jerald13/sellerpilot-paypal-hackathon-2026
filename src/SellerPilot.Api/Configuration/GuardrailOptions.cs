namespace SellerPilot.Api.Configuration;

public sealed class GuardrailOptions
{
    public const string SectionName = "Guardrails";

    /// <summary>
    /// Hard cap for a single refund, per currency (e.g. "USD": 500).
    /// A currency that is not listed cannot be refunded through SellerPilot at all.
    /// </summary>
    public Dictionary<string, decimal> MaxRefundAmount { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>PayPal only allows refunds within 180 days of the capture.</summary>
    public int RefundWindowDays { get; set; } = 180;

    /// <summary>Minimum time between two executed reminders for the same invoice.</summary>
    public int ReminderCooldownHours { get; set; } = 24;
}
