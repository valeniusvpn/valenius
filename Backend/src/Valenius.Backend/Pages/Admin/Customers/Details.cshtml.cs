using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Licensing;
using Valenius.Backend.Models;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages.Admin.Customers;

public class DetailsModel(ApplicationDbContext db, ILicenseContext license, ILicenseEnforcement enforcement, ISidecarService sidecar, IPeerProvisioningService provisioner, IAuditService audit) : PageModel
{
    public Customer     Customer             { get; private set; } = null!;
    public List<Client> Clients              { get; private set; } = [];
    public List<AppUser> Users               { get; private set; } = [];

    // Customer WireGuard traffic (aggregate across this customer's clients).
    public long CustomerRx { get; private set; }
    public long CustomerTx { get; private set; }
    public IReadOnlyList<Valenius.Backend.Services.TrafficBucket> TrafficBuckets { get; private set; } = [];
    public List<TrustedNetwork> TrustedNetworks { get; private set; } = [];

    public string? SidecarTestMessage      { get; private set; }
    public bool    SidecarTestSuccess      { get; private set; }
    public int?    SidecarListenPort       { get; private set; }
    public string? SidecarInterfaceAddress { get; private set; }
    public string? SidecarVersion          { get; private set; }
    public bool    HasActiveSidecarCert    { get; private set; }
    public bool    EnrollmentTokenIsSet    { get; private set; }
    public bool?   UdpPortReachable        { get; private set; }
    public int?    UdpCheckedPort          { get; private set; }
    public string? UdpCheckedHost          { get; private set; }

    /// <summary>Other customers whose sidecar transit subnet overlaps this one's. Non-empty
    /// means the gateway /health verification is disabled (handshake fallback) until resolved.</summary>
    public List<(string Name, string Subnet)> TransitConflicts { get; private set; } = [];
    public string  BackendUrl              => $"{Request.Scheme}://{Request.Host}";

    public ILicenseContext License             => license;
    public bool            IsProEdition        => sidecar is not NullSidecarService;
    public int             ActiveCount         { get; private set; }
    public int             TotalAssigned       { get; private set; } // sum of all Customer.MaxEndpoints
    public int             PoolRemaining       => license.MaxEndpoints < 0
                                                    ? int.MaxValue
                                                    : license.MaxEndpoints - TotalAssigned;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();
        Customer = customer;
        Clients  = await db.Clients.Where(c => c.CustomerId == id && !c.IsDeleted)
                                   .OrderBy(c => c.Hostname).ToListAsync();
        Users    = await db.AppUsers.Where(u => u.CustomerId == id)
                                    .OrderBy(u => u.DisplayName).ToListAsync();
        TrustedNetworks = await db.TrustedNetworks.Where(t => t.CustomerId == id)
                                                  .OrderBy(t => t.Subnet).ToListAsync();
        ActiveCount          = Clients.Count(c => c.IsActive);
        CustomerRx = Clients.Sum(c => c.WgRxLifetime);
        CustomerTx = Clients.Sum(c => c.WgTxLifetime);
        TrafficBuckets = await Valenius.Backend.Services.TrafficQuery.LoadRangeAsync(
            db, Clients.Select(c => c.Id).ToList(), Valenius.Backend.Services.TrafficRange.Default);
        TotalAssigned        = await db.Customers.SumAsync(c => c.MaxEndpoints);
        HasActiveSidecarCert = await db.SidecarCertificates
            .AnyAsync(c => c.CustomerId == id && !c.IsRevoked);
        EnrollmentTokenIsSet = !string.IsNullOrEmpty(customer.EnrollmentToken);
        await FetchSidecarInfoAsync(customer);
        return Page();
    }

    /// <summary>JSON for the auto-refreshing customer traffic panel (totals + chart).</summary>
    public async Task<IActionResult> OnGetTrafficAsync(int id, string? range)
    {
        var q = db.Clients.Where(c => c.CustomerId == id && !c.IsDeleted);
        var rx = await q.SumAsync(c => c.WgRxLifetime);
        var tx = await q.SumAsync(c => c.WgTxLifetime);
        var clientIds = await q.Select(c => c.Id).ToListAsync();
        var tr = Valenius.Backend.Services.TrafficRange.Parse(range);
        var buckets = await TrafficQuery.LoadRangeAsync(db, clientIds, tr);
        return new JsonResult(new
        {
            total = Valenius.Backend.Helpers.TrafficFormat.Bytes(rx + tx),
            rx    = Valenius.Backend.Helpers.TrafficFormat.Bytes(rx),
            tx    = Valenius.Backend.Helpers.TrafficFormat.Bytes(tx),
            chart = TrafficChart.Render(buckets, tr),
        });
    }

    private async Task FetchSidecarInfoAsync(Customer customer)
    {
        if (customer.ServerMode != "Valenius" || string.IsNullOrEmpty(customer.SidecarUrl))
            return;
        var info = await sidecar.GetInfoAsync(customer.SidecarBaseUrl, customer.Id);
        SidecarListenPort       = info?.ListenPort;
        SidecarInterfaceAddress = info?.InterfaceAddress;
        SidecarVersion          = info?.Version;
        TransitConflicts        = await ComputeTransitConflictsAsync(customer, info?.InterfaceAddress);
    }

    /// <summary>
    /// Finds other customers whose sidecar transit subnet overlaps this customer's (using the
    /// freshly discovered interface address when available, else the cached subnet). Transit
    /// subnets must be globally disjoint, so any hit disables the gateway check for both sides.
    /// </summary>
    private async Task<List<(string, string)>> ComputeTransitConflictsAsync(Customer customer, string? candidateInterfaceAddress)
    {
        var candidate = WireGuardHelper.NetworkCidr(candidateInterfaceAddress) ?? customer.SidecarVpnSubnet;
        if (string.IsNullOrEmpty(candidate)) return [];
        var others = await db.Customers
            .Where(c => c.Id != customer.Id && c.SidecarVpnSubnet != null && c.SidecarVpnSubnet != "")
            .Select(c => new { c.Name, c.SidecarVpnSubnet })
            .ToListAsync();
        return others
            .Where(o => WireGuardHelper.CidrsOverlap(candidate, o.SidecarVpnSubnet))
            .Select(o => (o.Name, o.SidecarVpnSubnet!))
            .ToList();
    }

    public async Task<IActionResult> OnPostSaveAsync(int id, string name, string? clientPrefix, string? notes, int? heartbeatIntervalMinutes)
    {
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(name))
            customer.Name = name.Trim();
        var prefix = clientPrefix?.Trim();
        customer.ClientPrefix = string.IsNullOrEmpty(prefix) ? null : prefix.ToUpperInvariant()[..Math.Min(3, prefix.Length)];
        customer.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        customer.HeartbeatIntervalMinutes = heartbeatIntervalMinutes is >= 1 and <= 60
            ? heartbeatIntervalMinutes
            : null;
        await db.SaveChangesAsync();
        _ = audit.LogAsync(Audit.Customer, "Updated", $"Customer: {customer.Name}");
        return RedirectToPage(new { id });
    }

    /// <summary>Saves the tenant-default MFA policy + session lifetime (Pro).</summary>
    /// <summary>MFA policy editing is locked when the Pro license is expired (enforcement continues).</summary>
    public bool MfaEditable => IsProEdition && license.IsValid;

    public async Task<IActionResult> OnPostSaveMfaDefaultsAsync(int id, string defaultMfaPolicy, int mfaSessionLifetimeHours)
    {
        if (!MfaEditable)
        {
            TempData["Warning"] = "MFA policy editing is locked — the Pro license is inactive or expired.";
            return RedirectToPage(new { id });
        }
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();
        customer.DefaultMfaPolicy = defaultMfaPolicy switch
        {
            "PerConnection" => MfaPolicy.PerConnection,
            "Interval"      => MfaPolicy.Interval,
            _               => MfaPolicy.Disabled,
        };
        customer.MfaSessionLifetimeHours = mfaSessionLifetimeHours is >= 1 and <= 720
            ? mfaSessionLifetimeHours : 12;
        await db.SaveChangesAsync();
        _ = audit.LogAsync(Audit.Mfa, "Customer MFA defaults updated", $"Customer: {customer.Name}",
            $"policy={customer.DefaultMfaPolicy}, lifetime={customer.MfaSessionLifetimeHours}h");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAddNetworkAsync(int id, string subnet, string? primaryDns)
    {
        if (string.IsNullOrWhiteSpace(subnet))
            return RedirectToPage(new { id });

        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();

        subnet = subnet.Trim();
        primaryDns = string.IsNullOrWhiteSpace(primaryDns) ? null : primaryDns.Trim();

        db.TrustedNetworks.Add(new TrustedNetwork
        {
            CustomerId = id,
            Subnet     = subnet,
            PrimaryDns = primaryDns
        });
        await db.SaveChangesAsync();
        TempData["NetTab"] = true;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveNetworkAsync(int id, int networkId)
    {
        var network = await db.TrustedNetworks.FindAsync(networkId);
        if (network is not null && network.CustomerId == id)
        {
            db.TrustedNetworks.Remove(network);
            await db.SaveChangesAsync();
        }
        TempData["NetTab"] = true;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostGenerateEnrollmentTokenAsync(int id)
    {
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();

        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        customer.EnrollmentToken = token;
        await db.SaveChangesAsync();

        TempData["EnrollmentToken"] = token;
        TempData["WgTab"] = true;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRevokeEnrollmentTokenAsync(int id)
    {
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();
        customer.EnrollmentToken = null;
        await db.SaveChangesAsync();
        TempData["WgTab"] = true;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnGetDownloadEnvAsync(int id)
    {
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();

        if (string.IsNullOrEmpty(customer.EnrollmentToken))
        {
            TempData["WgError"] = "No enrollment token is set. Generate a token first.";
            TempData["WgTab"]   = true;
            return RedirectToPage(new { id });
        }

        var backendUrl = $"{Request.Scheme}://{Request.Host}";
        var content = $"""
            # Valenius sidecar enrollment
            # Customer : {customer.Name}
            # Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC
            #
            # 1. Copy this file to the sidecar host as .env next to docker-compose.yml
            # 2. Start (or restart) the sidecar container — it will enroll automatically
            # 3. Remove or clear ENROLLMENT_TOKEN after enrollment succeeds

            WG_INTERFACE=wg0
            # WG_CONF_PATH=/etc/wireguard/wg0.conf   # defaults to /etc/wireguard/WG_INTERFACE.conf

            MANAGEMENT_PORT={customer.SidecarManagementPort}
            BACKEND_URL={backendUrl}
            ENROLLMENT_TOKEN={customer.EnrollmentToken}
            """;

        var bytes    = System.Text.Encoding.UTF8.GetBytes(content);
        var filename = $"sidecar-{SanitizeName(customer.Name)}.env";
        return File(bytes, "text/plain", filename);
    }

    private static string SanitizeName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"[^A-Za-z0-9_\-]", "_")
            .Trim('_').ToLowerInvariant();

    public async Task<IActionResult> OnPostToggleWtServerAsync(int id, bool enabled)
    {
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();
        customer.ServerMode = enabled ? "Valenius" : "External";
        await db.SaveChangesAsync();
        TempData["WgTab"] = true;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostToggleExternalAsync(int id, bool enabled)
    {
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();
        customer.AllowExternalConfig = enabled;
        await db.SaveChangesAsync();
        TempData["NetTab"] = true;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSaveServerAsync(
        int id, string? sidecarUrl,
        int sidecarManagementPort, int sidecarHealthPort, string? serverEndpoint, string? vpnPrefix,
        string? dnsServer, string? dnsSearchDomains, string? allowedIPs)
    {
        if (!IsValidIpv4(dnsServer))
        {
            TempData["WgError"] = "Invalid DNS server — enter a valid IPv4 address, e.g. 10.100.0.1.";
            TempData["WgTab"] = true;
            return RedirectToPage(new { id });
        }
        if (!IsValidDomainList(dnsSearchDomains))
        {
            TempData["WgError"] = "Invalid DNS search domains — enter comma-separated domains, e.g. corp.internal, lan.";
            TempData["WgTab"] = true;
            return RedirectToPage(new { id });
        }
        if (!IsValidCidrList(allowedIPs))
        {
            TempData["WgError"] = "Invalid Allowed IPs — enter comma-separated CIDRs, e.g. 0.0.0.0/0, ::/0 or 10.0.0.0/8.";
            TempData["WgTab"] = true;
            return RedirectToPage(new { id });
        }
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();

        customer.SidecarUrl = string.IsNullOrWhiteSpace(sidecarUrl) ? null : StripToHost(sidecarUrl);
        // Store only the hostname/IP — port is read from the sidecar at provisioning time.
        var endpointHost = serverEndpoint?.Trim();
        if (!string.IsNullOrEmpty(endpointHost))
        {
            var lastColon = endpointHost.LastIndexOf(':');
            if (lastColon > 0 && int.TryParse(endpointHost[(lastColon + 1)..], out _))
                endpointHost = endpointHost[..lastColon];
        }
        customer.ServerEndpoint = string.IsNullOrEmpty(endpointHost) ? null : endpointHost;
        // Only letters allowed in VPN prefix; strip everything else before saving.
        var prefix = string.IsNullOrWhiteSpace(vpnPrefix) ? null
            : new string(vpnPrefix.Where(char.IsLetter).ToArray());
        customer.VpnPrefix         = string.IsNullOrEmpty(prefix) ? null : prefix;
        customer.DnsServer             = string.IsNullOrWhiteSpace(dnsServer)  ? null : dnsServer.Trim();
        customer.DnsSearchDomains      = string.IsNullOrWhiteSpace(dnsSearchDomains) ? null : NormalizeDomainList(dnsSearchDomains);
        customer.AllowedIPs            = string.IsNullOrWhiteSpace(allowedIPs) ? null : allowedIPs.Trim();
        customer.SidecarManagementPort = sidecarManagementPort is > 0 and <= 65535 ? sidecarManagementPort : 9003;
        // 0 = "unset" → the heartbeat builder falls back to the sidecar's default health
        // port (9004). It must never silently reuse the mTLS management port.
        customer.SidecarHealthPort     = sidecarHealthPort is > 0 and <= 65535 ? sidecarHealthPort : 0;

        await db.SaveChangesAsync();
        TempData["WgTab"] = true;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostResyncAsync(int id)
    {
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();

        var result = await provisioner.ResyncAsync(customer, db);
        _ = audit.LogAsync(Audit.Customer, "Peer resync", $"Customer: {customer.Name}", result);

        TempData[result.Contains("failed") ? "WgError" : "WgSuccess"] = result;
        TempData["WgTab"] = true;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostTestSidecarAsync(int id, string? sidecarHost, int? sidecarManagementPort)
    {
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();
        Customer        = customer;
        Clients         = await db.Clients.Where(c => c.CustomerId == id && !c.IsDeleted).OrderBy(c => c.Hostname).ToListAsync();
        Users           = await db.AppUsers.Where(u => u.CustomerId == id).OrderBy(u => u.DisplayName).ToListAsync();
        TrustedNetworks = await db.TrustedNetworks.Where(t => t.CustomerId == id).OrderBy(t => t.Subnet).ToListAsync();
        HasActiveSidecarCert = await db.SidecarCertificates.AnyAsync(c => c.CustomerId == id && !c.IsRevoked);
        EnrollmentTokenIsSet = !string.IsNullOrEmpty(customer.EnrollmentToken);

        var host = string.IsNullOrWhiteSpace(sidecarHost)
            ? customer.SidecarUrl ?? ""
            : StripToHost(sidecarHost);
        var port = sidecarManagementPort is > 0 and <= 65535
            ? sidecarManagementPort.Value
            : customer.SidecarManagementPort;
        var url  = string.IsNullOrEmpty(host) ? "" : $"https://{host}:{port}";

        var (ok, msg, _) = await sidecar.TestConnectionAsync(url, customer.Id);
        SidecarTestSuccess = ok;
        SidecarTestMessage = msg;
        await FetchSidecarInfoAsync(customer);

        if (SidecarListenPort.HasValue)
        {
            var udpHost = !string.IsNullOrEmpty(customer.ServerEndpoint) ? customer.ServerEndpoint : host;
            UdpCheckedHost = udpHost;
            UdpCheckedPort = SidecarListenPort.Value;
            UdpPortReachable = await CheckUdpPortAsync(udpHost, SidecarListenPort.Value);
        }

        return Page();
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

    /// <summary>
    /// Strips any accidentally-pasted protocol prefix (https://, http://) and port
    /// suffix from a server address, leaving just the hostname or IP.
    /// </summary>
    private static string StripToHost(string input)
    {
        var h = input.Trim();
        if (h.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) h = h[8..];
        if (h.StartsWith("http://",  StringComparison.OrdinalIgnoreCase)) h = h[7..];
        // Strip trailing /path
        var slash = h.IndexOf('/');
        if (slash >= 0) h = h[..slash];
        // Strip port (last colon + digits)
        var colon = h.LastIndexOf(':');
        if (colon >= 0 && int.TryParse(h[(colon + 1)..], out _)) h = h[..colon];
        return h.Trim();
    }

    private static async Task<bool?> CheckUdpPortAsync(string host, int port)
    {
        try
        {
            using var udp = new System.Net.Sockets.UdpClient();
            var data = new byte[] { 0 };
            await udp.SendAsync(data, data.Length, host, port);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await udp.ReceiveAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return true; // WireGuard drops unknown packets silently — timeout means port is open
        }
        catch (System.Net.Sockets.SocketException ex) when
            (ex.SocketErrorCode is System.Net.Sockets.SocketError.ConnectionReset
                                 or System.Net.Sockets.SocketError.ConnectionRefused)
        {
            return false; // ICMP Port Unreachable — nothing listening
        }
        catch
        {
            return null; // DNS failure, network error, etc.
        }
    }

    public async Task<IActionResult> OnPostSaveEndpointsAsync(int id, int maxEndpoints)
    {
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();

        // Calculate current total assigned excluding this customer so we can check headroom.
        var otherAssigned = await db.Customers
            .Where(c => c.Id != id)
            .SumAsync(c => c.MaxEndpoints);

        var check = enforcement.CanAssignEndpointsToCustomer(maxEndpoints, otherAssigned);
        if (!check.Allowed)
        {
            TempData["Error"] = check.DenialReason;
            TempData["EndpointsTab"] = true;
            return RedirectToPage(new { id });
        }

        customer.MaxEndpoints = Math.Max(0, maxEndpoints);
        await db.SaveChangesAsync();
        TempData["Success"] = $"Endpoint allocation updated to {customer.MaxEndpoints}.";
        TempData["EndpointsTab"] = true;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSaveSharingAsync(int id, bool allowShareSidecar, bool allowReceiveForeignProfiles)
    {
        if (!IsProEdition) return Forbid();
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();
        customer.AllowShareSidecar             = allowShareSidecar;
        customer.AllowReceiveForeignProfiles   = allowReceiveForeignProfiles;
        await db.SaveChangesAsync();
        _ = audit.LogAsync(Audit.Customer, "Sharing settings updated", $"Customer: {customer.Name}",
            $"AllowShareSidecar={allowShareSidecar}, AllowReceiveForeignProfiles={allowReceiveForeignProfiles}");
        TempData["WgSuccess"] = "Cross-customer sharing settings saved.";
        TempData["WgTab"] = true;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();
        var name = customer.Name;
        db.Customers.Remove(customer);
        await db.SaveChangesAsync();
        _ = audit.LogAsync(Audit.Customer, "Deleted", $"Customer: {name}");
        return RedirectToPage("Index");
    }
}
