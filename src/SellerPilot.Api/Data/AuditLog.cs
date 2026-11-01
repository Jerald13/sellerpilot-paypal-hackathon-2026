namespace SellerPilot.Api.Data;

public class AuditLog
{
    public long Id { get; set; }

    public DateTime Timestamp { get; set; }

    /// <summary>"agent" | "seller" | "system".</summary>
    public string Actor { get; set; } = "";

    /// <summary>See <see cref="Audit.AuditEvents"/>.</summary>
    public string Event { get; set; } = "";

    /// <summary>JSON.</summary>
    public string Details { get; set; } = "{}";
}
