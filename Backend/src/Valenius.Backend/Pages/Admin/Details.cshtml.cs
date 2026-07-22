using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using Valenius.Backend.Data;
using Valenius.Backend.Models;
using Valenius.Backend.Licensing;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages.Admin;

public class DetailsModel(ApplicationDbContext db, ClientNotifier notifier, ClientPresenceTracker presence, WgTunnelTracker tunnels, IPeerProvisioningService provisioner, ILicenseEnforcement enforcement, ISidecarService sidecar, IConfiguration config, IEmailService email, IAuditService audit, IMfaSessionService mfa, IInstallationLicenseMonitor installLicense, ILicenseContext license) : PageModel
{
    public Client Client { get; private set; } = null!;
    public List<ConnectionLog> Logs { get; private set; } = [];
    /// <summary>Uploaded diagnostic log bundles (metadata only — the blob is loaded on download).</summary>
    public List<ClientLogBundle> LogBundles { get; private set; } = [];
    public List<Customer> AllCustomers { get; private set; } = [];
    public List<ClientForeignProfile> ForeignProfiles { get; private set; } = [];
    public List<Customer> ShareableCustomers { get; private set; } = [];
    public List<string> KnownProfiles { get; private set; } = [];
    public bool IsOnline { get; private set; }
    public bool IsWgConnected { get; private set; }

    // ── MFA (Pro) ────────────────────────────────────────────────────────────
    /// <summary>Whether the MFA management card is shown (Pro installation license active).</summary>
    public bool MfaProActive { get; private set; }
    /// <summary>
    /// Whether MFA policy editing is permitted. Enforcement of existing sessions never stops,
    /// but policy modification locks when the Pro license is expired (design doc §9).
    /// </summary>
    public bool MfaEditable => MfaProActive && license.IsValid;
    /// <summary>Effective resolved policy for this peer.</summary>
    public MfaPolicyResolution MfaResolved { get; private set; }
    /// <summary>Active (non-expired) MFA sessions for this client.</summary>
    public List<MfaSession> ActiveMfaSessions { get; private set; } = [];
    /// <summary>Mobile devices in this customer that can be picked as the push-to-approve approver.</summary>
    public List<Client> ApproverCandidates { get; private set; } = [];
    public string? GeneratedConfig   { get; private set; }
    public string? GeneratedConfigQr { get; private set; }
    public string? LatestVersion     { get; private set; }

    // WireGuard traffic (per-client). Lifetime totals from the Client row; buckets (one per day
    // over the retention window) feed the chart.
    public long TrafficRx { get; private set; }
    public long TrafficTx { get; private set; }
    public IReadOnlyList<Valenius.Backend.Services.TrafficBucket> TrafficBuckets { get; private set; } = [];

    public bool IsUpdateAvailable =>
        IsOnline
        && !string.IsNullOrEmpty(Client?.ClientVersion)
        && !string.IsNullOrEmpty(LatestVersion)
        && Version.TryParse(LatestVersion, out var latest)
        && Version.TryParse(Client.ClientVersion, out var current)
        && latest > current;

    // ── Effective inherited values (for clarity display in the UI) ─────────────
    /// <summary>True when AllowedIPs is set on the client (overriding the customer default).</summary>
    public bool AllowedIPsOverridden => !string.IsNullOrWhiteSpace(Client.AllowedIPs);
    /// <summary>The AllowedIPs actually applied: client override, else customer default, else all traffic.</summary>
    public string EffectiveAllowedIPs =>
        AllowedIPsOverridden ? Client.AllowedIPs! : (Client.Customer?.AllowedIPs ?? "0.0.0.0/0, ::/0");
    /// <summary>True when a DNS server is set on the client (overriding the customer default).</summary>
    public bool DnsOverridden => !string.IsNullOrWhiteSpace(Client.DnsServer);
    /// <summary>The DNS server actually applied: client override, else customer default, else none.</summary>
    public string EffectiveDns =>
        DnsOverridden ? Client.DnsServer! : (Client.Customer?.DnsServer ?? "none");
    /// <summary>True when DNS search domains are set on the client (overriding the customer default).</summary>
    public bool DnsSearchDomainsOverridden => !string.IsNullOrWhiteSpace(Client.DnsSearchDomains);
    /// <summary>The DNS search domains actually applied: client override, else customer default, else none.</summary>
    public string EffectiveDnsSearchDomains =>
        DnsSearchDomainsOverridden ? Client.DnsSearchDomains! : (Client.Customer?.DnsSearchDomains ?? "none");

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var client = await db.Clients.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == id);
        if (client is null || client.IsDeleted) return NotFound();
        if (!CanAccess(client)) return Forbid();
        Client = client;
        Logs = await db.ConnectionLogs
            .Where(l => l.ClientId == id)
            .OrderByDescending(l => l.OccurredAt)
            .Take(500)
            .ToListAsync();
        // Log-bundle metadata only — never pull the blob content into the list.
        LogBundles = await db.ClientLogBundles
            .Where(b => b.ClientId == id)
            .OrderByDescending(b => b.UploadedAt)
            .Select(b => new ClientLogBundle
            {
                Id = b.Id, ClientId = b.ClientId, UploadedAt = b.UploadedAt,
                Trigger = b.Trigger, FileName = b.FileName, ContentType = b.ContentType, SizeBytes = b.SizeBytes,
            })
            .ToListAsync();
        if (User.IsInRole("Admin"))
            AllCustomers = await db.Customers.OrderBy(c => c.Name).ToListAsync();
        if (!string.IsNullOrEmpty(client.KnownProfiles))
        {
            try { KnownProfiles = JsonSerializer.Deserialize<List<string>>(client.KnownProfiles) ?? []; }
            catch { /* leave empty */ }
        }
        IsOnline      = presence.IsOnline(client.ClientKey);
        IsWgConnected = tunnels.IsConnected(client.ClientKey);

        TrafficRx = client.WgRxLifetime;
        TrafficTx = client.WgTxLifetime;
        TrafficBuckets = await Valenius.Backend.Services.TrafficQuery.LoadRangeAsync(
            db, [id], Valenius.Backend.Services.TrafficRange.Default);

        MfaProActive = installLicense.IsProActive;
        if (MfaProActive)
        {
            MfaResolved = mfa.ResolvePolicy(client, client.Customer);
            var now = DateTime.UtcNow;
            ActiveMfaSessions = await db.MfaSessions
                .Where(s => s.ClientId == client.Id
                         && s.Status == MfaSessionStatus.Active
                         && s.ExpiresAt > now)
                .OrderByDescending(s => s.ExpiresAt)
                .ToListAsync();

            ForeignProfiles = await db.ClientForeignProfiles
                .Include(p => p.SourceCustomer)
                .Where(p => p.ClientId == client.Id)
                .ToListAsync();

            // Mobile devices in the same customer that can approve this client's MFA.
            ApproverCandidates = await db.Clients
                .Where(c => c.CustomerId == client.CustomerId
                         && c.Id != client.Id
                         && !c.IsDeleted
                         && c.FcmToken != null
                         && (c.Platform == "Android" || c.Platform == "iOS"))
                .OrderBy(c => c.Hostname)
                .ToListAsync();

            // Build the list of customers whose sidecar this client's customer may receive from.
            if (User.IsInRole("Admin") && client.Customer?.AllowReceiveForeignProfiles == true)
            {
                ShareableCustomers = await db.Customers
                    .Where(c => c.AllowShareSidecar
                             && c.ServerMode == "Valenius"
                             && c.Id != client.CustomerId)
                    .OrderBy(c => c.Name)
                    .ToListAsync();
            }
        }

        GeneratedConfig = await BuildGeneratedConfigAsync(client);
        if (GeneratedConfig is not null)
        {
            using var qrGen = new QRCodeGenerator();
            var qrData = qrGen.CreateQrCode(GeneratedConfig, QRCodeGenerator.ECCLevel.M);
            GeneratedConfigQr = Convert.ToBase64String(new PngByteQRCode(qrData).GetGraphic(8));
        }
        LatestVersion = await ReadLatestVersionAsync(client);
        return Page();
    }

    /// <summary>JSON for the auto-refreshing traffic panel (totals + re-rendered chart SVG).</summary>
    public async Task<IActionResult> OnGetTrafficAsync(int id, string? range)
    {
        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == id && !c.IsDeleted);
        if (client is null) return NotFound();
        if (!CanAccess(client)) return Forbid();

        var tr = TrafficRange.Parse(range);
        var buckets = await TrafficQuery.LoadRangeAsync(db, [id], tr);
        return new JsonResult(new
        {
            total = Valenius.Backend.Helpers.TrafficFormat.Bytes(client.WgRxLifetime + client.WgTxLifetime),
            rx    = Valenius.Backend.Helpers.TrafficFormat.Bytes(client.WgRxLifetime),
            tx    = Valenius.Backend.Helpers.TrafficFormat.Bytes(client.WgTxLifetime),
            chart = TrafficChart.Render(buckets, tr),
        });
    }

    /// <summary>The latest published version for THIS client's OS stream + channel — not the
    /// legacy global versions.json (which is only ever the Windows version, so it used to make
    /// every non-Windows client show a bogus "update available" carrying the Windows number).
    /// Returns null when the OS has no published release (e.g. macOS before its first upload) or
    /// the client is store-updated (Android/iOS), so <see cref="IsUpdateAvailable"/> stays false.</summary>
    private async Task<string?> ReadLatestVersionAsync(Client client)
    {
        var os = ReleaseOsForPlatform(client.Platform);
        if (os is null) return null;
        var channel = Models.ReleaseChannel.Normalize(client.UpdateChannel);

        // Mirrors OssStartup.ResolvePublishedReleaseAsync: a beta request with no published beta
        // falls back to stable, so beta clients never look "behind".
        var rel = await db.ClientReleases
            .Where(r => r.Os == os && r.Channel == channel && r.IsPublished)
            .OrderByDescending(r => r.UploadedAt)
            .FirstOrDefaultAsync();
        if (rel is null && channel == Models.ReleaseChannel.Beta)
            rel = await db.ClientReleases
                .Where(r => r.Os == os && r.Channel == Models.ReleaseChannel.Stable && r.IsPublished)
                .OrderByDescending(r => r.UploadedAt)
                .FirstOrDefaultAsync();
        return rel?.Version;
    }

    /// <summary>Maps a client's self-reported <c>Platform</c> to its release-stream OS key, or null
    /// for store-updated / unknown platforms (which have no server-driven update comparison).
    /// Tolerates the casing variants clients send ("macos" per the concept doc, plus "macOS"/
    /// "Mac"/"Darwin").</summary>
    private static string? ReleaseOsForPlatform(string? platform) => platform switch
    {
        "Windows" => Models.ReleaseOs.Windows,
        "Linux" => Models.ReleaseOs.Linux,
        "macos" or "macOS" or "Mac" or "Darwin" => Models.ReleaseOs.MacOs,
        _ => null, // Android/iOS are store-updated; unknown/legacy → no update comparison
    };

    private async Task<string?> BuildGeneratedConfigAsync(Client client)
    {
        var customer = client.Customer;
        if (customer?.ServerMode != "Valenius") return null;
        if (string.IsNullOrEmpty(client.WgPrivateKey) ||
            string.IsNullOrEmpty(client.VpnIpAddress)  ||
            string.IsNullOrEmpty(customer.SidecarUrl)  ||
            string.IsNullOrEmpty(customer.ServerEndpoint))
            return null;

        var info = await sidecar.GetInfoAsync(customer.SidecarBaseUrl, customer.Id);
        if (info is null) return null;

        var endpoint        = $"{customer.ServerEndpoint}:{info.ListenPort}";
        var gatewayIp       = info.InterfaceAddress?.Split('/')[0].Trim();
        var effectiveAllowedIPs = WireGuardHelper.EnsureGatewayInAllowedIPs(
            client.AllowedIPs ?? customer.AllowedIPs ?? "0.0.0.0/0, ::/0", gatewayIp);
        var effectiveDns    = client.DnsServer ?? customer.DnsServer;
        var effectiveDomains = client.DnsSearchDomains ?? customer.DnsSearchDomains;

        return WireGuardHelper.BuildClientConfig(
            client.WgPrivateKey,
            client.VpnIpAddress,
            info.PublicKey,
            endpoint,
            effectiveDns,
            effectiveAllowedIPs,
            effectiveDomains);
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var client = await db.Clients.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == id);
        if (client is null) return NotFound();
        if (!CanAccess(client)) return Forbid();

        var activating = !client.IsActive;

        if (activating)
        {
            var activeCount = await db.Clients.CountAsync(c =>
                c.IsActive && !c.IsDeleted && c.CustomerId == client.CustomerId);
            var customerMax = client.Customer?.MaxEndpoints ?? 0;
            var check = enforcement.CanActivateEndpoint(activeCount, customerMax);
            if (!check.Allowed)
            {
                TempData["Error"] = check.DenialReason ?? "Endpoint activation blocked by license.";
                return RedirectToPage(new { id });
            }
        }

        client.IsActive = activating;
        _ = audit.LogAsync(Audit.Client, activating ? "Activated" : "Deactivated",
            $"Client: {client.Hostname}", $"Customer: {client.Customer?.Name}");

        if (client.Customer?.ServerMode == "Valenius")
        {
            if (activating)
                await provisioner.ProvisionAsync(client, client.Customer);
            else
                await provisioner.DeprovisionAsync(client, client.Customer);
        }

        await db.SaveChangesAsync();
        notifier.Notify(client.ClientKey);
        return RedirectToPage(new { id });
    }

    // ── Diagnostics: client log collection ─────────────────────────────────────

    /// <summary>Ask the client to collect its (Valenius-only, redacted) logs and upload them
    /// on its next check-in. One-shot flag delivered via the heartbeat/poll.</summary>
    public async Task<IActionResult> OnPostRequestLogsAsync(int id)
    {
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        if (!CanAccess(client)) return Forbid();
        client.PendingLogRequest = true;
        await db.SaveChangesAsync();
        notifier.Notify(client.ClientKey);
        _ = audit.LogAsync(Audit.Client, "Log upload requested", $"Client: {client.Hostname}");
        TempData["Success"] = "Log upload requested — the client uploads its Valenius logs on its next check-in.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnGetDownloadLogAsync(int id, int bundleId)
    {
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        if (!CanAccess(client)) return Forbid();
        var bundle = await db.ClientLogBundles.FirstOrDefaultAsync(b => b.Id == bundleId && b.ClientId == id);
        if (bundle is null) return NotFound();
        _ = audit.LogAsync(Audit.Client, "Log bundle downloaded", $"Client: {client.Hostname}", bundle.FileName);
        return File(bundle.Content, bundle.ContentType, bundle.FileName);
    }

    public async Task<IActionResult> OnPostDeleteLogAsync(int id, int bundleId)
    {
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        if (!CanAccess(client)) return Forbid();
        var bundle = await db.ClientLogBundles.FirstOrDefaultAsync(b => b.Id == bundleId && b.ClientId == id);
        if (bundle is not null)
        {
            db.ClientLogBundles.Remove(bundle);
            await db.SaveChangesAsync();
            _ = audit.LogAsync(Audit.Client, "Log bundle deleted", $"Client: {client.Hostname}", bundle.FileName);
        }
        TempData["Success"] = "Log bundle deleted.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSaveNotesAsync(int id, string? notes)
    {
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        if (!CanAccess(client)) return Forbid();
        client.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        await db.SaveChangesAsync();
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSetMfaApproverAsync(int id, int? approverClientId)
    {
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        if (!CanAccess(client)) return Forbid();

        if (approverClientId is int aid)
        {
            var approver = await db.Clients.FirstOrDefaultAsync(c => c.Id == aid && !c.IsDeleted);
            if (approver is null || approver.CustomerId != client.CustomerId)
                return BadRequest("Approver must be a device in the same customer.");
            client.MfaApproverClientId = aid;
        }
        else
        {
            client.MfaApproverClientId = null;
        }
        await db.SaveChangesAsync();
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSaveDisplayNameAsync(int id, string? displayName)
    {
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        if (!CanAccess(client)) return Forbid();
        client.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        await db.SaveChangesAsync();
        return RedirectToPage(new { id });
    }

    /// <summary>
    /// Confirms that a pending-duplicate registration (a reinstall with a fresh ClientKey) is the
    /// same machine as the existing client of that hostname. The existing record keeps its
    /// activation, customer, VPN config and history; only its ClientKey (and host/user identity)
    /// is moved to the new install. The duplicate row is then removed. The new install picks up
    /// the existing record on its next poll.
    /// </summary>
    public async Task<IActionResult> OnPostReassignAsync(int id)
    {
        var duplicate = await db.Clients.FindAsync(id);
        if (duplicate is null) return NotFound();
        if (!CanAccess(duplicate)) return Forbid();
        if (!duplicate.IsDuplicatePending)
            return RedirectToPage(new { id });

        var original = await db.Clients
            .Include(c => c.Customer)
            .Where(c => !c.IsDeleted && !c.IsDuplicatePending
                     && c.Id != duplicate.Id && c.Hostname == duplicate.Hostname)
            .OrderByDescending(c => c.LastSeenAt)
            .FirstOrDefaultAsync();

        if (original is null)
        {
            // Nothing to reassign to (the original was deleted in the meantime) — just promote
            // this row to a normal client.
            duplicate.IsDuplicatePending = false;
            await db.SaveChangesAsync();
            TempData["Warning"] = "No existing client found to reassign — kept this registration instead.";
            return RedirectToPage(new { id });
        }
        if (!CanAccess(original)) return Forbid();

        // Move identity from the new install onto the existing record. Remove the duplicate first
        // so the unique ClientKey index is free before we reassign it.
        var newKey         = duplicate.ClientKey;
        var newHostSid     = duplicate.HostSid;
        var newUsername    = duplicate.Username;
        var newUserSid     = duplicate.UserSid;
        var newVersion     = duplicate.ClientVersion;

        db.Clients.Remove(duplicate);
        await db.SaveChangesAsync();

        original.ClientKey     = newKey;
        original.HostSid       = newHostSid;
        if (!string.IsNullOrEmpty(newUsername)) original.Username = newUsername;
        if (!string.IsNullOrEmpty(newUserSid))  original.UserSid  = newUserSid;
        if (!string.IsNullOrEmpty(newVersion))  original.ClientVersion = newVersion;
        original.LastSeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // The fresh install starts with an empty local config store and no authenticator, so
        // re-stage every profile the record already had and re-arm MFA if the peer is gated.

        // 1) Re-provision sidecar-backed profiles — regenerates the .conf from the retained keys
        //    and re-adds the peer (the automatic equivalent of a manual "Re-provision"), so it
        //    works even for peers provisioned before config archiving existed. No-op in OSS.
        if (original.Customer?.ServerMode == "Valenius")
            await provisioner.ProvisionAsync(original, original.Customer);

        var foreignProfiles = await db.ClientForeignProfiles
            .Include(p => p.SourceCustomer)
            .Where(p => p.ClientId == original.Id)
            .ToListAsync();
        foreach (var fp in foreignProfiles)
            await provisioner.ProvisionForeignAsync(original, fp.SourceCustomer);

        // 2) Flag archived (e.g. manually-uploaded) configs for one-shot inline re-delivery,
        //    covering any profile that isn't sidecar-provisioned.
        var archived = await db.ClientConfigArchives
            .Where(a => a.ClientId == original.Id)
            .ToListAsync();
        foreach (var a in archived)
            a.PendingResend = true;

        // 3) MFA: the reinstalled device has no authenticator, and any session from the old
        //    install must not carry over. Revoke it and re-open enrollment so the client shows
        //    the setup gate and the user must re-enroll before the peer will connect. No-op in OSS.
        var mfaReEnroll = mfa.ResolvePolicy(original, original.Customer).Gated;
        if (mfaReEnroll)
        {
            if (await mfa.GetActiveSessionAsync(original.Id) is { } session)
                await mfa.RevokeSessionAsync(session.SessionId, "reinstall reassign");
            await mfa.ResetEnrollmentAsync(original, original.Customer);
        }

        await db.SaveChangesAsync();

        notifier.Notify(original.ClientKey);
        _ = audit.LogAsync(Audit.Client, "Reassigned to reinstall",
            $"Client: {original.Hostname}", $"New ClientKey: {newKey}");
        TempData["Success"] = mfaReEnroll
            ? $"Reassigned '{original.Hostname}' to the new installation — re-staging its profiles; the user must re-enroll MFA on the device."
            : $"Reassigned '{original.Hostname}' to the new installation — re-staging its profiles.";
        return RedirectToPage(new { id = original.Id });
    }

    /// <summary>
    /// Keeps a pending-duplicate registration as an independent client (the admin has decided the
    /// same-hostname collision is two different machines). Clears the flag so it can be activated
    /// normally.
    /// </summary>
    public async Task<IActionResult> OnPostKeepBothAsync(int id)
    {
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        if (!CanAccess(client)) return Forbid();
        client.IsDuplicatePending = false;
        await db.SaveChangesAsync();
        _ = audit.LogAsync(Audit.Client, "Duplicate kept as separate client", $"Client: {client.Hostname}");
        TempData["Success"] = "Kept as a separate client. You can now activate it.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUploadConfigAsync(int id, string? profileName, IFormFile? configFile)
    {
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        if (!CanAccess(client)) return Forbid();

        if (string.IsNullOrWhiteSpace(profileName) ||
            !System.Text.RegularExpressions.Regex.IsMatch(profileName, @"^[A-Za-z0-9_\-]{1,50}$"))
            return BadRequest("Profile name may only contain letters, digits, underscores, and hyphens (max 50 chars).");

        if (configFile is null || configFile.Length == 0)
            return BadRequest("No file provided.");

        if (!configFile.FileName.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Only .conf files are supported.");

        if (configFile.Length > 1_048_576)
            return BadRequest("File too large.");

        using var reader = new StreamReader(configFile.OpenReadStream());
        client.PendingConfigFileName = profileName.Trim();
        client.PendingConfigContent  = await reader.ReadToEndAsync();
        await db.SaveChangesAsync();
        notifier.Notify(client.ClientKey);

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAssignCustomerAsync(int id, int? customerId)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var client = await db.Clients.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == id);
        if (client is null) return NotFound();

        if (!string.IsNullOrEmpty(client.WgPublicKey) && client.CustomerId != customerId && client.Customer is not null)
        {
            var result = await provisioner.DeprovisionAsync(client, client.Customer);
            client.WgPublicKey  = null;
            client.WgPrivateKey = null;
            client.VpnIpAddress = null;
            TempData["Warning"] = $"Peer unprovisioned from {client.Customer.Name}: {result}";
        }

        client.CustomerId = customerId;
        await db.SaveChangesAsync();
        notifier.Notify(client.ClientKey);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUnprovisionPeerAsync(int id)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var client = await db.Clients.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == id);
        if (client is null) return NotFound();

        if (!string.IsNullOrEmpty(client.WgPublicKey) && client.Customer is not null)
        {
            var result = await provisioner.DeprovisionAsync(client, client.Customer);
            TempData["Warning"] = result;
        }

        client.WgPublicKey  = null;
        client.WgPrivateKey = null;
        client.VpnIpAddress = null;
        await db.SaveChangesAsync();
        notifier.Notify(client.ClientKey);
        if (!TempData.ContainsKey("Warning"))
            TempData["Success"] = "Peer unprovisioned — WireGuard keys and VPN IP cleared.";
        return RedirectToPage(new { id });
    }

    // ── MFA (Pro) ────────────────────────────────────────────────────────────

    /// <summary>Sets peer type + MFA policy override, then applies the transition rules.</summary>
    public async Task<IActionResult> OnPostSetMfaPolicyAsync(int id, string peerType, string mfaPolicy, int? mfaLifetimeHours)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        if (!installLicense.IsProActive || !license.IsValid)
        {
            TempData["Warning"] = "MFA policy editing is locked — the Pro license is inactive or expired. Existing enforcement continues.";
            return RedirectToPage(new { id });
        }
        var client = await db.Clients.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == id);
        if (client is null) return NotFound();

        client.PeerType = string.Equals(peerType, "Machine", StringComparison.OrdinalIgnoreCase)
            ? PeerType.Machine : PeerType.User;
        client.MfaPolicy = mfaPolicy switch
        {
            "Disabled"      => Models.MfaPolicy.Disabled,
            "PerConnection" => Models.MfaPolicy.PerConnection,
            "Interval"      => Models.MfaPolicy.Interval,
            _               => null,   // "Inherit"
        };
        client.MfaSessionLifetimeHours = mfaLifetimeHours is > 0 ? mfaLifetimeHours : null;
        await db.SaveChangesAsync();

        await mfa.ApplyPolicyChangeAsync(client, client.Customer, tunnels.IsConnected(client.ClientKey));
        notifier.Notify(client.ClientKey);

        _ = audit.LogAsync(Audit.Mfa, "Policy changed", $"Client: {client.Hostname}",
            $"peerType={client.PeerType}, policy={mfaPolicy}");
        TempData["Success"] = "MFA policy updated.";
        return RedirectToPage(new { id });
    }

    /// <summary>Resets the per-client TOTP factor and re-opens enrollment.</summary>
    public async Task<IActionResult> OnPostResetMfaAsync(int id)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        if (!installLicense.IsProActive || !license.IsValid)
        {
            TempData["Warning"] = "MFA editing is locked — the Pro license is inactive or expired.";
            return RedirectToPage(new { id });
        }
        var client = await db.Clients.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == id);
        if (client is null) return NotFound();

        await mfa.ResetEnrollmentAsync(client, client.Customer);
        notifier.Notify(client.ClientKey);
        _ = audit.LogAsync(Audit.Mfa, "Enrollment reset (admin)", $"Client: {client.Hostname}");
        TempData["Success"] = "MFA enrollment reset — the user must re-enroll on their device.";
        return RedirectToPage(new { id });
    }

    /// <summary>Revokes a single active MFA session immediately.</summary>
    public async Task<IActionResult> OnPostRevokeMfaSessionAsync(int id, Guid sessionId)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var actor = User.Identity?.Name ?? "admin";
        if (await mfa.RevokeSessionAsync(sessionId, actor))
            TempData["Success"] = "MFA session revoked.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostTriggerConnectAsync(int id)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        client.PendingConnect = true;
        await db.SaveChangesAsync();
        notifier.Notify(client.ClientKey);
        _ = audit.LogAsync(Audit.Client, "Connect triggered", $"Client: {client.Hostname}");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostForceDisconnectAsync(int id)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        client.PendingDisconnect = true;
        await db.SaveChangesAsync();
        notifier.Notify(client.ClientKey);
        _ = audit.LogAsync(Audit.Client, "Disconnect triggered", $"Client: {client.Hostname}");
        return RedirectToPage(new { id });
    }

    /// <summary>
    /// Forcefully removes this client's peer from the sidecar's WireGuard interface,
    /// severing the tunnel immediately at the network level — unlike ForceDisconnect,
    /// this works even if the client itself is unreachable. The client can reconnect
    /// on its own afterwards: LogEvent transparently re-adds the peer the next time it
    /// reports a Connect event (see WgPeerKilled).
    /// </summary>
    public async Task<IActionResult> OnPostKillTunnelAsync(int id)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var client = await db.Clients.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == id);
        if (client is null) return NotFound();
        if (client.Customer?.ServerMode != "Valenius")
            return BadRequest("Customer is not in Valenius server mode.");
        if (string.IsNullOrEmpty(client.WgPublicKey))
            return BadRequest("Client has no WireGuard peer to remove.");

        var removed = await sidecar.RemovePeerAsync(client.Customer.SidecarBaseUrl, client.WgPublicKey);
        client.WgPeerKilled = true;
        await db.SaveChangesAsync();
        tunnels.MarkDisconnected(client.ClientKey);
        notifier.Notify(client.ClientKey);
        _ = audit.LogAsync(Audit.Client, "Tunnel killed", $"Client: {client.Hostname}",
            removed ? "Peer removed from sidecar." : "Peer removal request failed or sidecar unreachable.");

        TempData[removed ? "Success" : "Warning"] = removed
            ? "Tunnel killed — the peer was removed from the WireGuard server immediately. The client will be reconnected automatically on its next connection attempt."
            : "Kill Tunnel request sent, but the sidecar did not confirm removal — check sidecar connectivity.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostForceUpdateAsync(int id)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        client.ForceUpdate = true;
        await db.SaveChangesAsync();
        notifier.Notify(client.ClientKey);
        _ = audit.LogAsync(Audit.Client, "Force update", $"Client: {client.Hostname}");
        TempData["Success"] = "Update triggered — the client will download and install the latest version.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSetUpdateChannelAsync(int id, string? channel)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        client.UpdateChannel = ReleaseChannel.Normalize(channel);
        await db.SaveChangesAsync();
        notifier.Notify(client.ClientKey);
        _ = audit.LogAsync(Audit.Client, "Update channel changed", $"Client: {client.Hostname}", client.UpdateChannel);
        TempData["Success"] = $"Update channel set to {client.UpdateChannel}.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostToggleConnectivityCheckAsync(int id)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        client.SkipConnectivityCheck = !client.SkipConnectivityCheck;
        await db.SaveChangesAsync();
        _ = audit.LogAsync(Audit.Client,
            client.SkipConnectivityCheck ? "Connectivity check disabled" : "Connectivity check enabled",
            $"Client: {client.Hostname}");
        TempData["Success"] = client.SkipConnectivityCheck
            ? "Connectivity check disabled — the tunnel will be treated as connected without Phase 2 verification."
            : "Connectivity check enabled — Phase 2 verification will run on each connection.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteProfileAsync(int id, string profileName)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(profileName))
            client.PendingDeleteProfileName = profileName.Trim();
        await db.SaveChangesAsync();
        notifier.Notify(client.ClientKey);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostToggleAutoConnectAsync(int id)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        client.AutoConnectEnabled = !client.AutoConnectEnabled;
        await db.SaveChangesAsync();
        notifier.Notify(client.ClientKey);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSaveAutoConnectAsync(int id, bool autoConnectEnabled, string? autoConnectProfileName)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        client.AutoConnectEnabled = autoConnectEnabled;
        var profile = autoConnectProfileName?.Trim();
        client.AutoConnectProfileName = string.IsNullOrEmpty(profile) ? null : profile;
        await db.SaveChangesAsync();
        notifier.Notify(client.ClientKey);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSavePeerSettingsAsync(int id, string? allowedIPs, string? dnsServer, string? dnsSearchDomains, int? maxConfigValidDays)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        if (!IsValidCidrList(allowedIPs))
        {
            TempData["Error"] = "Invalid Allowed IPs — enter comma-separated CIDRs, e.g. 0.0.0.0/0, ::/0 or 10.0.0.0/8.";
            return RedirectToPage(new { id });
        }
        if (!IsValidIpv4(dnsServer))
        {
            TempData["Error"] = "Invalid DNS server — enter a valid IPv4 address, e.g. 10.100.0.1.";
            return RedirectToPage(new { id });
        }
        if (!IsValidDomainList(dnsSearchDomains))
        {
            TempData["Error"] = "Invalid DNS search domains — enter comma-separated domains, e.g. corp.internal, lan.";
            return RedirectToPage(new { id });
        }
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        client.AllowedIPs         = string.IsNullOrWhiteSpace(allowedIPs) ? null : allowedIPs.Trim();
        client.DnsServer          = string.IsNullOrWhiteSpace(dnsServer)  ? null : dnsServer.Trim();
        client.DnsSearchDomains   = string.IsNullOrWhiteSpace(dnsSearchDomains) ? null : NormalizeDomainList(dnsSearchDomains);
        client.MaxConfigValidDays = maxConfigValidDays is > 0 ? maxConfigValidDays : null;
        await db.SaveChangesAsync();
        TempData["Success"] = "Peer settings saved. Re-provision to push the updated config to the client.";
        return RedirectToPage(new { id });
    }

    private static bool IsValidCidrList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        foreach (var part in value.Split(','))
            if (!System.Net.IPNetwork.TryParse(part.Trim(), out _)) return false;
        return true;
    }

    private static bool IsValidIpv4(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return System.Net.IPAddress.TryParse(value.Trim(), out var addr)
               && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    /// <summary>Validates a comma-separated DNS search-domain list (letters, digits, dots, hyphens).</summary>
    private static bool IsValidDomainList(string? value)
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
    private static string NormalizeDomainList(string value) =>
        string.Join(", ", value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public async Task<IActionResult> OnPostProvisionForeignAsync(int id, int sourceCustomerId)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var client = await db.Clients.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == id);
        if (client is null) return NotFound();
        if (!installLicense.IsProActive)
        {
            TempData["Error"] = "Pro license required for cross-customer provisioning.";
            return RedirectToPage(new { id });
        }
        var sourceCustomer = await db.Customers.FindAsync(sourceCustomerId);
        if (sourceCustomer is null) return NotFound();

        var result = await provisioner.ProvisionForeignAsync(client, sourceCustomer);
        await db.SaveChangesAsync();
        notifier.Notify(client.ClientKey);
        _ = audit.LogAsync(Audit.Client, "Foreign profile provisioned", $"Client: {client.Hostname}",
            $"Source customer: {sourceCustomer.Name}");

        if (result.StartsWith("Provisioned"))
            TempData["Success"] = result;
        else if (result.StartsWith("Failed") || result.StartsWith("Blocked"))
            TempData["Error"] = result;
        else
            TempData["Warning"] = result;

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeprovisionForeignAsync(int id, int sourceCustomerId)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var client = await db.Clients.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == id);
        if (client is null) return NotFound();
        var sourceCustomer = await db.Customers.FindAsync(sourceCustomerId);
        if (sourceCustomer is null) return NotFound();

        var result = await provisioner.DeprovisionForeignAsync(client, sourceCustomer);
        await db.SaveChangesAsync();
        notifier.Notify(client.ClientKey);
        _ = audit.LogAsync(Audit.Client, "Foreign profile deprovisioned", $"Client: {client.Hostname}",
            $"Source customer: {sourceCustomer.Name}");

        TempData["Warning"] = result;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostProvisionPeerAsync(int id)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var client = await db.Clients.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == id);
        if (client is null) return NotFound();
        if (client.Customer?.ServerMode != "Valenius")
            return BadRequest("Customer is not in Valenius server mode.");

        var result = await provisioner.ProvisionAsync(client, client.Customer);
        var messages = new List<string> { result };

        // Also re-provision every foreign profile this client holds, so the top-bar
        // "Re-provision" button re-registers the whole peer, not just the native one.
        if (installLicense.IsProActive)
        {
            var foreignProfiles = await db.ClientForeignProfiles
                .Include(p => p.SourceCustomer)
                .Where(p => p.ClientId == client.Id)
                .ToListAsync();
            foreach (var fp in foreignProfiles)
            {
                var foreignResult = await provisioner.ProvisionForeignAsync(client, fp.SourceCustomer);
                messages.Add($"{fp.SourceCustomer.Name}: {foreignResult}");
                _ = audit.LogAsync(Audit.Client, "Foreign profile provisioned", $"Client: {client.Hostname}",
                    $"Source customer: {fp.SourceCustomer.Name}");
            }
        }

        await db.SaveChangesAsync();
        notifier.Notify(client.ClientKey);

        if (messages.Any(m => m.StartsWith("Failed") || m.StartsWith("Blocked")))
            TempData["Error"] = string.Join(" | ", messages);
        else if (messages.All(m => m.StartsWith("Provisioned")))
            TempData["Success"] = string.Join(" | ", messages);
        else
            TempData["Warning"] = string.Join(" | ", messages);

        return RedirectToPage(new { id });
    }

    /// <summary>Remotely triggers the Windows client's local repair checks on its next heartbeat,
    /// without waiting for a service restart. Currently runs the data-directory ACL self-heal
    /// (fixes "access to path ... is denied" errors caused by a corrupted/empty ACL under its
    /// ProgramData tree, e.g. a poisoned temp\*.conf left over from a crashed connect — see
    /// DataDirSelfHeal in the Windows client) — a generic hook other client-side repairs can join
    /// later without a new button/flag per fix.</summary>
    public async Task<IActionResult> OnPostRepairConfigAsync(int id)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();

        client.PendingConfigRepair = true;
        await db.SaveChangesAsync();
        notifier.Notify(client.ClientKey);
        _ = audit.LogAsync(Audit.Client, "Client config repair requested", $"Client: {client.Hostname}");

        TempData["Success"] = "Config repair requested — it will run on the client's next heartbeat.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        client.IsDeleted = true;
        await db.SaveChangesAsync();
        _ = audit.LogAsync(Audit.Client, "Deleted", $"Client: {client.Hostname}");
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnGetDownloadConfAsync(int id)
    {
        var client = await db.Clients.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == id);
        if (client is null || client.IsDeleted) return NotFound();
        if (!CanAccess(client)) return Forbid();
        var config = await BuildGeneratedConfigAsync(client);
        if (config is null) return NotFound();
        var fileName = client.Customer is not null
            ? WireGuardHelper.ProfileName(client.Customer.VpnPrefix, client.Customer.Name) + ".conf"
            : "wireguard.conf";
        return File(Encoding.UTF8.GetBytes(config), "text/plain", fileName);
    }

    public async Task<IActionResult> OnGetQrPngAsync(int id)
    {
        var client = await db.Clients.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == id);
        if (client is null || client.IsDeleted) return NotFound();
        if (!CanAccess(client)) return Forbid();
        var config = await BuildGeneratedConfigAsync(client);
        if (config is null) return NotFound();
        using var qrGenerator = new QRCodeGenerator();
        var qrData = qrGenerator.CreateQrCode(config, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(qrData).GetGraphic(10);
        return File(png, "image/png", $"{client.Hostname}-vpn-qr.png");
    }

    public async Task<IActionResult> OnPostSendConfigEmailAsync(int id, string recipientEmail)
    {
        if (!User.IsInRole("Admin")) return Forbid();

        if (string.IsNullOrWhiteSpace(recipientEmail))
        {
            TempData["Error"] = "Recipient e-mail address is required.";
            return RedirectToPage(new { id });
        }

        var client = await db.Clients.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == id);
        if (client is null || client.IsDeleted) return NotFound();

        var configText = await BuildGeneratedConfigAsync(client);
        if (configText is null)
        {
            TempData["Error"] = "No configuration available to send — the client must be active with a provisioned VPN profile.";
            return RedirectToPage(new { id });
        }

        var customer    = client.Customer;
        var profileName = customer is not null
            ? WireGuardHelper.ProfileName(customer.VpnPrefix, customer.Name)
            : "wireguard";

        // Generate QR code PNG
        using var qrGen  = new QRCodeGenerator();
        var qrData       = qrGen.CreateQrCode(configText, QRCodeGenerator.ECCLevel.M);
        var qrPng        = new PngByteQRCode(qrData).GetGraphic(8);
        var confBytes    = Encoding.UTF8.GetBytes(configText);

        var cid         = "wt-vpn-qr";
        var customerName = customer?.Name ?? "your VPN";

        var htmlBody = $"""
            <div style="font-family:sans-serif;max-width:600px;margin:0 auto;color:#333;">
              <h2 style="color:#0F4E66;margin-bottom:.5rem;">WireGuard VPN Configuration</h2>
              <p>Hello,</p>
              <p>Your WireGuard VPN configuration for <strong>{System.Net.WebUtility.HtmlEncode(customerName)}</strong>
                 is attached to this e-mail as <code>{System.Net.WebUtility.HtmlEncode(profileName)}.conf</code>.</p>

              <h3 style="color:#0F4E66;">Desktop / laptop setup</h3>
              <ol>
                <li>Download and install <a href="https://www.wireguard.com/install/">WireGuard</a>.</li>
                <li>Open WireGuard and click <strong>Import Tunnel(s) from File</strong>.</li>
                <li>Select the attached <code>.conf</code> file and click <strong>Activate</strong>.</li>
              </ol>

              <h3 style="color:#0F4E66;">Mobile setup (iOS / Android)</h3>
              <p>Install the WireGuard app, tap <strong>+</strong>, then <strong>Scan from QR Code</strong> and scan:</p>
              <p style="text-align:center;">
                <img src="cid:{cid}" alt="WireGuard QR code" style="width:200px;height:200px;" />
              </p>

              <hr style="border:none;border-top:1px solid #e0e0e0;margin:1.5rem 0;" />
              <p style="font-size:.8rem;color:#888;">
                &#9888;&#65039; Keep this configuration private — it grants access to the VPN.
                Sent by Valenius.
              </p>
            </div>
            """;

        var attachments = new List<EmailAttachment>
        {
            new($"{profileName}.conf", confBytes, "text/plain"),
            new("vpn-qr.png", qrPng, "image/png", IsInline: true, ContentId: cid),
        };

        try
        {
            await email.SendAsync(recipientEmail.Trim(), $"WireGuard VPN — {customerName}", htmlBody, attachments);
            _ = audit.LogAsync(Audit.Email, "Config email sent",
                $"Client: {client.Hostname}", $"To: {recipientEmail.Trim()}");
            TempData["Success"] = $"Configuration sent to {recipientEmail}.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Could not send e-mail: {ex.Message}";
        }

        return RedirectToPage(new { id });
    }

    private bool CanAccess(Client client)
    {
        if (User.IsInRole("Admin")) return true;
        var val = User.FindFirstValue("CustomerId");
        return int.TryParse(val, out var cid) && client.CustomerId == cid;
    }
}
