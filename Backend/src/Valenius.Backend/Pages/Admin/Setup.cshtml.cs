using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class SetupModel(ApplicationDbContext db, ISidecarService sidecar, IAuditService audit) : PageModel
{
    public string  Step              { get; private set; } = "welcome";
    public int?    CreatedCustomerId { get; private set; }
    public string? CarriedName       { get; private set; }
    public string? CarriedPrefix     { get; private set; }
    public string? CarriedNotes      { get; private set; }
    public string? CarriedToken      { get; private set; }
    public string? Error             { get; private set; }
    public string? EnrollmentToken   { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? step = null, int? customerId = null)
    {
        if (step == "done")
        {
            Step = "done";
            CreatedCustomerId = customerId;
            EnrollmentToken   = TempData["SetupEnrollmentToken"] as string;
            return Page();
        }

        if (await db.Customers.AnyAsync())
            return RedirectToPage("/Admin/Overview");

        Step = step ?? "welcome";

        if (step == "wireguard")
        {
            CarriedName   = TempData["SetupName"]   as string;
            CarriedPrefix = TempData["SetupPrefix"] as string;
            CarriedNotes  = TempData["SetupNotes"]  as string;
            CarriedToken  = TempData["SetupToken"]  as string;
            if (string.IsNullOrEmpty(CarriedName))
                return RedirectToPage(); // TempData expired — restart
        }

        return Page();
    }

    public async Task<IActionResult> OnPostCustomerAsync(
        string? name, string? clientPrefix, string? notes, string? serverMode)
    {
        if (await db.Customers.AnyAsync())
            return RedirectToPage("/Admin/Overview");

        if (string.IsNullOrWhiteSpace(name))
        {
            Error = "Customer name is required.";
            Step  = "customer";
            return Page();
        }

        if (serverMode == "External")
        {
            var customer = BuildCustomer(name, clientPrefix, notes, "External");
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            _ = audit.LogAsync(Audit.Customer, "Created via setup wizard", customer.Name);
            return RedirectToPage(new { step = "done", customerId = customer.Id });
        }

        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));

        TempData["SetupName"]   = name.Trim();
        TempData["SetupPrefix"] = clientPrefix;
        TempData["SetupNotes"]  = notes;
        TempData["SetupToken"]  = token;
        return RedirectToPage(new { step = "wireguard" });
    }

    public async Task<IActionResult> OnPostWireGuardAsync(
        string? setupName, string? setupPrefix, string? setupNotes, string? setupToken,
        string? sidecarUrl, int sidecarManagementPort, int sidecarHealthPort,
        string? serverEndpoint, string? vpnPrefix, string? dnsServer, string? allowedIPs)
    {
        if (await db.Customers.AnyAsync())
            return RedirectToPage("/Admin/Overview");

        if (string.IsNullOrWhiteSpace(setupName))
            return RedirectToPage();

        if (!IsValidIpv4(dnsServer))
        {
            Error         = "DNS Server must be a valid IPv4 address.";
            Step          = "wireguard";
            CarriedName   = setupName;
            CarriedPrefix = setupPrefix;
            CarriedNotes  = setupNotes;
            CarriedToken  = setupToken;
            return Page();
        }

        if (!IsValidCidrList(allowedIPs))
        {
            Error         = "Allowed IPs must be comma-separated CIDRs (e.g. 0.0.0.0/0, ::/0).";
            Step          = "wireguard";
            CarriedName   = setupName;
            CarriedPrefix = setupPrefix;
            CarriedNotes  = setupNotes;
            CarriedToken  = setupToken;
            return Page();
        }

        // Re-use the token generated at the customer step; fall back to a new one
        // if TempData expired (e.g. user took too long).
        var enrollmentToken = string.IsNullOrWhiteSpace(setupToken)
            ? Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
            : setupToken;

        var customer = BuildCustomer(setupName, setupPrefix, setupNotes, "Valenius");
        customer.SidecarUrl            = string.IsNullOrWhiteSpace(sidecarUrl)               ? null : StripToHost(sidecarUrl);
        customer.SidecarManagementPort = sidecarManagementPort is > 0 and <= 65535 ? sidecarManagementPort : 8443;
        customer.SidecarHealthPort     = sidecarHealthPort     is > 0 and <= 65535 ? sidecarHealthPort     : 0;
        customer.ServerEndpoint        = string.IsNullOrWhiteSpace(serverEndpoint)            ? null : serverEndpoint.Trim();
        customer.VpnPrefix             = NormalizeVpnPrefix(vpnPrefix);
        customer.DnsServer             = string.IsNullOrWhiteSpace(dnsServer)                 ? null : dnsServer.Trim();
        customer.AllowedIPs            = string.IsNullOrWhiteSpace(allowedIPs)                ? null : allowedIPs.Trim();
        customer.EnrollmentToken       = enrollmentToken;

        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        _ = audit.LogAsync(Audit.Customer, "Created via setup wizard", customer.Name);

        TempData["SetupEnrollmentToken"] = enrollmentToken;
        return RedirectToPage(new { step = "done", customerId = customer.Id });
    }

    public async Task<IActionResult> OnGetTestSidecarAsync(string? host, int port = 8443)
    {
        if (string.IsNullOrWhiteSpace(host))
            return new JsonResult(new { ok = false, message = "No host provided." });
        var url = $"https://{StripToHost(host)}:{(port > 0 ? port : 8443)}";
        var (ok, msg, _) = await sidecar.TestConnectionAsync(url);
        return new JsonResult(new { ok, message = msg });
    }

    private static Customer BuildCustomer(string name, string? prefix, string? notes, string serverMode)
    {
        var p = prefix?.Trim();
        return new Customer
        {
            Name         = name.Trim(),
            ClientPrefix = string.IsNullOrEmpty(p) ? null : p.ToUpperInvariant()[..Math.Min(3, p.Length)],
            Notes        = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            ServerMode   = serverMode
        };
    }

    private static string StripToHost(string input)
    {
        var h = input.Trim();
        if (h.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) h = h[8..];
        if (h.StartsWith("http://",  StringComparison.OrdinalIgnoreCase)) h = h[7..];
        var slash = h.IndexOf('/');
        if (slash >= 0) h = h[..slash];
        var colon = h.LastIndexOf(':');
        if (colon >= 0 && int.TryParse(h[(colon + 1)..], out _)) h = h[..colon];
        return h.Trim();
    }

    private static string? NormalizeVpnPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = new string(value.Where(char.IsLetter).ToArray());
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static bool IsValidIpv4(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return System.Net.IPAddress.TryParse(value.Trim(), out var addr)
               && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    private static bool IsValidCidrList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        foreach (var part in value.Split(','))
            if (!System.Net.IPNetwork.TryParse(part.Trim(), out _)) return false;
        return true;
    }
}
