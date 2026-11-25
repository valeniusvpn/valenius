using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using Valenius.Backend.Data;
using Valenius.Backend.Models;

namespace Valenius.Backend.Pages.Admin;

public class PairDeviceModel(ApplicationDbContext db) : PageModel
{
    public int    CustomerId   { get; private set; }
    public string CustomerName { get; private set; } = "";
    public string Code         { get; private set; } = "";
    public string QrBase64     { get; private set; } = "";
    public string? RecoveryDevice { get; private set; }
    public int    ExpiryMinutes => 15;

    public async Task<IActionResult> OnGetAsync(int customerId, int? clientId = null)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId);
        if (customer is null) return NotFound();
        if (!CanAccess(customerId)) return Forbid();

        Client? device = null;
        if (clientId is int id)
        {
            if (!User.IsInRole("Admin")) return Forbid();
            device = await db.Clients.FirstOrDefaultAsync(c => c.Id == id && c.CustomerId == customerId);
            if (device is null || string.IsNullOrEmpty(device.ClientKey)) return NotFound();
            RecoveryDevice = device.EffectiveName;
        }
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";

        var now = DateTime.UtcNow;
        var token = new PairingToken
        {
            Token      = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            CustomerId = customerId,
            ClientKey  = device?.ClientKey,
            CreatedAt  = now,
            ExpiresAt  = now.AddMinutes(ExpiryMinutes),
        };
        db.PairingTokens.Add(token);
        await db.SaveChangesAsync();

        CustomerId   = customerId;
        CustomerName = customer.Name;
        Code         = token.Token;

        var payload = JsonSerializer.Serialize(new
        {
            v     = 1,
            url   = $"{Request.Scheme}://{Request.Host}",
            token = token.Token,
        });
        using var qr = new QRCodeGenerator();
        var data = qr.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        QrBase64 = Convert.ToBase64String(new PngByteQRCode(data).GetGraphic(8));

        return Page();
    }

    private bool CanAccess(int customerId)
    {
        if (User.IsInRole("Admin")) return true;
        var val = User.FindFirstValue("CustomerId");
        return int.TryParse(val, out var cid) && customerId == cid;
    }
}
