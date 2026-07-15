using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using Valenius.Backend.Data;
using Valenius.Backend.Models;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages.Admin;

public class ConfigCardModel(ApplicationDbContext db, ISidecarService sidecar, IMfaSessionService mfa) : PageModel
{
    public string Hostname     { get; private set; } = "";
    public string ProfileName  { get; private set; } = "";
    public string VpnIp        { get; private set; } = "";
    public string Config       { get; private set; } = "";
    public string QrBase64     { get; private set; } = "";
    public string CustomerName { get; private set; } = "";
    /// <summary>True when this peer has an active MFA policy — sharing the config bypasses per-device auth.</summary>
    public bool   MfaGated     { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var client = await db.Clients.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == id);
        if (client is null || client.IsDeleted) return NotFound();
        if (!CanAccess(client)) return Forbid();
        if (client.Customer?.ServerMode != "Valenius") return NotFound();

        var config = await BuildConfigAsync(client);
        if (config is null) return NotFound();

        Hostname     = client.EffectiveName;
        CustomerName = client.Customer.Name;
        ProfileName  = WireGuardHelper.ProfileName(client.Customer.VpnPrefix, client.Customer.Name);
        VpnIp        = client.VpnIpAddress ?? "";
        Config       = config;
        MfaGated     = mfa.ResolvePolicy(client, client.Customer).Policy != MfaPolicy.Disabled;

        using var qrGenerator = new QRCodeGenerator();
        var qrData = qrGenerator.CreateQrCode(config, QRCodeGenerator.ECCLevel.M);
        QrBase64 = Convert.ToBase64String(new PngByteQRCode(qrData).GetGraphic(8));

        return Page();
    }

    private async Task<string?> BuildConfigAsync(Client client)
    {
        var customer = client.Customer!;
        if (string.IsNullOrEmpty(client.WgPrivateKey) ||
            string.IsNullOrEmpty(client.VpnIpAddress)  ||
            string.IsNullOrEmpty(customer.SidecarUrl)  ||
            string.IsNullOrEmpty(customer.ServerEndpoint))
            return null;

        var info = await sidecar.GetInfoAsync(customer.SidecarBaseUrl, customer.Id);
        if (info is null) return null;

        return WireGuardHelper.BuildClientConfig(
            client.WgPrivateKey,
            client.VpnIpAddress,
            info.PublicKey,
            $"{customer.ServerEndpoint}:{info.ListenPort}",
            client.DnsServer ?? customer.DnsServer,
            client.AllowedIPs ?? customer.AllowedIPs ?? "0.0.0.0/0, ::/0",
            client.DnsSearchDomains ?? customer.DnsSearchDomains);
    }

    private bool CanAccess(Client client)
    {
        if (User.IsInRole("Admin")) return true;
        var val = User.FindFirstValue("CustomerId");
        return int.TryParse(val, out var cid) && client.CustomerId == cid;
    }
}
