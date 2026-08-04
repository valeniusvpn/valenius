using System.Net;
using System.Net.Sockets;

namespace Valenius.Backend.Services;

/// <summary>
/// Validation for the per-client network override fields (AllowedIPs, DnsServer,
/// DnsSearchDomains) — shared by the Management API's client PATCH endpoint and, in spirit,
/// mirrors the equivalent private helpers on <c>Admin/Details.cshtml.cs</c> /
/// <c>Admin/Customers/Details.cshtml.cs</c>. Extracted here (rather than duplicated a third
/// time) because these two call sites must agree on what's valid — the existing admin-page
/// copies are left as-is to avoid touching unrelated UI code for this feature.
/// </summary>
public static class ClientFieldValidation
{
    public static bool IsValidCidrList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        foreach (var part in value.Split(','))
            if (!IPNetwork.TryParse(part.Trim(), out _)) return false;
        return true;
    }

    public static bool IsValidIpv4(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return IPAddress.TryParse(value.Trim(), out var addr) && addr.AddressFamily == AddressFamily.InterNetwork;
    }

    /// <summary>Validates a comma-separated DNS search-domain list (letters, digits, dots, hyphens).</summary>
    public static bool IsValidDomainList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length > 253) return false;
            if (!part.All(c => char.IsLetterOrDigit(c) || c is '.' or '-')) return false;
            if (part.StartsWith('.') || part.EndsWith('.') || part.StartsWith('-')) return false;
        }
        return true;
    }

    /// <summary>Trims and re-joins a comma-separated domain list as "a, b, c".</summary>
    public static string NormalizeDomainList(string value) =>
        string.Join(", ", value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
