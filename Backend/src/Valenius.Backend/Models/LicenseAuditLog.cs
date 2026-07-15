namespace Valenius.Backend.Models;

public class LicenseAuditLog
{
    public int      Id        { get; set; }
    public DateTime Timestamp { get; set; }
    public string   Event     { get; set; } = ""; // "loaded", "expired", "grace_period", "limit_reached", "revoked"
    public string   Detail    { get; set; } = "";
}
