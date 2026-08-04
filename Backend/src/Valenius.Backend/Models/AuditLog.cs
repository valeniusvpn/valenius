using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

public class AuditLog
{
    public int      Id        { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [MaxLength(50)]
    public string Category  { get; set; } = "";

    [MaxLength(100)]
    public string Action    { get; set; } = "";

    [MaxLength(200)]
    public string Actor     { get; set; } = "";

    [MaxLength(500)]
    public string Target    { get; set; } = "";

    public string? Details  { get; set; }

    [MaxLength(45)]
    public string? IpAddress { get; set; }
}

/// <summary>Predefined category constants used in audit log entries.</summary>
public static class Audit
{
    public const string Auth     = "Auth";
    public const string Client   = "Client";
    public const string Customer = "Customer";
    public const string User     = "User";
    public const string Settings = "Settings";
    public const string Backup   = "Backup";
    public const string Email    = "Email";
    public const string Release  = "Release";
    public const string System   = "System";
    public const string Mfa      = "Mfa";
    public const string Api      = "Api";
}
