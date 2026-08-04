using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Licensing;
using Valenius.Backend.Models;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages.Admin.Customers;

public class DetailsModel(ApplicationDbContext db, ILicenseContext license, ILicenseEnforcement enforcement, ISidecarService sidecar, IPeerProvisioningService provisioner, IAuditService audit, HeartbeatSettingsStore heartbeatSettings, BackendEgressIpProvider egressIp, WireGuardHandshakeProbe handshakeProbe) : PageModel
{
    public Customer     Customer             { get; private set; } = null!;

    /// <summary>System-wide default heartbeat interval (Admin -> Settings), used when this
    /// customer has no override of its own.</summary>
    public int DefaultHeartbeatIntervalMinutes => heartbeatSettings.DefaultIntervalMinutes;
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

    /// <summary>The sidecar's WireGuard static public key from /info — the identity the handshake
    /// probe encrypts to, and therefore what a successful handshake proves it really holds.</summary>
    public string? SidecarPublicKey        { get; private set; }

    /// <summary>Physical LAN CIDR(s) the sidecar detected on its host, freshly fetched from
    /// /info (falls back to the last-cached <see cref="Customer.SidecarLanCidrs"/> value when the
    /// live call fails). This is what's sent to clients as StatusResponse.RemoteLanCidrs for the
    /// pre-connect LAN-conflict check.</summary>
    public List<string> DetectedLanCidrs { get; private set; } = [];
    /// <summary>Whether the sidecar confirms the port-fallback redirect is installed right now
    /// (fresh from /info, falling back to the cached <see cref="Customer.FallbackPortActive"/>
    /// when the sidecar is unreachable). This — not the admin's toggle — is what decides whether
    /// clients are offered the fallback; see docs/design/port-443-fallback.md §4.4.</summary>
    public bool    FallbackActive          { get; private set; }
    public string? FallbackError           { get; private set; }

    /// <summary>A sidecar address is configured but GET /info did not answer.</summary>
    public bool    SidecarUnreachable      { get; private set; }

    /// <summary>This backend's public egress IPv4, resolved only when a sidecar is unreachable so
    /// the firewall guidance can name a single source address instead of telling the operator to
    /// expose the management port to the internet. Null when it could not be determined.</summary>
    public string? BackendPublicIp         { get; private set; }

    /// <summary>
    /// The sidecar enrolled successfully at some point but cannot be reached now. Enrollment is
    /// sidecar → backend (outbound, usually permitted); the management API is backend → sidecar
    /// (inbound, needs an explicit rule). So this specific combination points hard at a blocked
    /// inbound port rather than a mis-set address or a stopped container.
    /// </summary>
    public bool    SidecarEnrolledButUnreachable => SidecarUnreachable && HasActiveSidecarCert;

    public bool    HasActiveSidecarCert    { get; private set; }
    public bool    EnrollmentTokenIsSet    { get; private set; }
    public bool?   UdpPortReachable        { get; private set; }
    public int?    UdpCheckedPort          { get; private set; }
    public string? UdpCheckedHost          { get; private set; }

    /// <summary>Other customers whose sidecar transit subnet overlaps this one's. Non-empty
    /// means the gateway /health verification is disabled (handshake fallback) until resolved.</summary>
    public List<(string Name, string Subnet)> TransitConflicts { get; private set; } = [];
    // Forced to https regardless of Request.Scheme: this backend sits behind a reverse proxy
    // that terminates TLS, and Request.Scheme silently falls back to http whenever the proxy's
    // IP falls outside the trusted KnownNetworks for X-Forwarded-Proto (see OssStartup.cs) -
    // which produced a real bug: a sidecar given http://... as BACKEND_URL gets redirected to
    // https by the proxy, and Go's http.Client downgrades POST to GET on 301/302, turning
    // enrollment into "405 Method Not Allowed" instead of a working POST. A sidecar's backend
    // URL should never legitimately be plain http in production, so this value is intentionally
    // not Request.Scheme-derived here.
    public string  BackendUrl              => $"https://{Request.Host}";

    public ILicenseContext License             => license;
    public bool            IsProEdition        => sidecar is not NullSidecarService;
    public int             ActiveCount         { get; private set; }
    public int             TotalAssigned       { get; private set; } // sum of all Customer.MaxEndpoints
    public int             PoolRemaining       => license.MaxEndpoints < 0
                                                    ? int.MaxValue
                                                    : license.MaxEndpoints - TotalAssigned;

    /// <summary>
    /// Loads everything the page renders. Extracted so the handlers that return Page() directly
    /// (rather than redirecting) populate the same state as a plain GET — they previously each
    /// reloaded an ad-hoc subset, which left the hero counters and traffic panel empty after a
    /// "Test connection".
    /// </summary>
    private async Task LoadPageDataAsync(Customer customer)
    {
        var id   = customer.Id;
        Customer = customer;
        Clients  = await db.Clients.Where(c => c.CustomerId == id && !c.IsDeleted)
                                   .OrderBy(c => c.Hostname).ToListAsync();
        Users    = await db.AppUsers.Where(u => u.CustomerId == id)
                                    .OrderBy(u => u.DisplayName).ToListAsync();
        TrustedNetworks = await db.TrustedNetworks.Where(t => t.CustomerId == id)
                                                  .OrderBy(t => t.Subnet).ToListAsync();
        ActiveCount = Clients.Count(c => c.IsActive);
        CustomerRx  = Clients.Sum(c => c.WgRxLifetime);
        CustomerTx  = Clients.Sum(c => c.WgTxLifetime);
        TrafficBuckets = await Valenius.Backend.Services.TrafficQuery.LoadRangeAsync(
            db, Clients.Select(c => c.Id).ToList(), Valenius.Backend.Services.TrafficRange.Default);
        TotalAssigned        = await db.Customers.SumAsync(c => c.MaxEndpoints);
        HasActiveSidecarCert = await db.SidecarCertificates.AnyAsync(c => c.CustomerId == id && !c.IsRevoked);
        EnrollmentTokenIsSet = !string.IsNullOrEmpty(customer.EnrollmentToken);
    }

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
        SidecarPublicKey        = info?.PublicKey;
        // Unreachable sidecar → fall back to the last cached verdict rather than reporting
        // "not active", which would wrongly suggest the admin's setting had been rejected.
        FallbackActive          = info?.FallbackActive ?? customer.FallbackPortActive;
        FallbackError           = info?.FallbackError;
        TransitConflicts        = await ComputeTransitConflictsAsync(customer, info?.InterfaceAddress);
        DetectedLanCidrs         = info?.LanCidrs is { Count: > 0 } fresh
            ? fresh
            : (customer.SidecarLanCidrs?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []).ToList();

        // An address is configured but /info didn't answer. The management API is the only way in,
        // so this is where the firewall guidance below becomes relevant.
        SidecarUnreachable = info is null;
        if (SidecarUnreachable)
        {
            // Only looked up when it's actually needed — see BackendEgressIpProvider. Deployments
            // that reach their sidecars over a site-to-site VPN never hit this path at all.
            BackendPublicIp = await egressIp.GetAsync(HttpContext.RequestAborted);
        }
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

    /// <summary>MFA policy editing is locked when the Pro license is expired (enforcement continues).</summary>
    public bool MfaEditable => IsProEdition && license.IsValid;

    /// <summary>
    /// Saves the Integrated Server tab's "Policies" card — the tenant-default MFA policy +
    /// session lifetime (Pro) and the Windows device-tunnel default, which share one form.
    /// The MFA fields are absent from the POST when the card renders them read-only (no enrolled
    /// sidecar) or inside a disabled fieldset (expired licence), so they are applied only when
    /// actually submitted; the device-tunnel switch is always present.
    /// See <see cref="Models.Customer.DeviceTunnelDefaultEnabled"/> /
    /// <see cref="Models.Client.DeviceTunnelEnabled"/>.
    /// </summary>
    public async Task<IActionResult> OnPostSavePoliciesAsync(
        int id, string? defaultMfaPolicy, int? mfaSessionLifetimeHours, bool deviceTunnelDefaultEnabled)
    {
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();

        customer.DeviceTunnelDefaultEnabled = deviceTunnelDefaultEnabled;

        if (defaultMfaPolicy is not null)
        {
            if (!MfaEditable)
            {
                TempData["Warning"] = "MFA policy editing is locked — the Pro license is inactive or expired.";
            }
            else
            {
                customer.DefaultMfaPolicy = defaultMfaPolicy switch
                {
                    "PerConnection" => MfaPolicy.PerConnection,
                    "Interval"      => MfaPolicy.Interval,
                    _               => MfaPolicy.Disabled,
                };
                customer.MfaSessionLifetimeHours = mfaSessionLifetimeHours is >= 1 and <= 720
                    ? mfaSessionLifetimeHours.Value : 12;
                _ = audit.LogAsync(Audit.Mfa, "Customer MFA defaults updated", $"Customer: {customer.Name}",
                    $"policy={customer.DefaultMfaPolicy}, lifetime={customer.MfaSessionLifetimeHours}h");
            }
        }

        await db.SaveChangesAsync();
        TempData["WgTab"] = true;
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

        var backendUrl = BackendUrl; // forced https — see the property's comment
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
            HEALTH_PORT={(customer.SidecarHealthPort > 0 ? customer.SidecarHealthPort : 9004)}
            BACKEND_URL={backendUrl}
            ENROLLMENT_TOKEN={customer.EnrollmentToken}
            """;

        var bytes    = System.Text.Encoding.UTF8.GetBytes(content);
        var filename = $"sidecar-{SanitizeName(customer.Name)}.env";
        return File(bytes, "text/plain; charset=utf-8", filename);
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
        string? dnsServer, string? dnsSearchDomains, string? allowedIPs,
        bool fallbackPortEnabled = false, int fallbackPort = 443, int fallbackTriggerSeconds = 20)
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

        // ── Port fallback (docs/design/port-443-fallback.md) ──────────────────────
        // The intent is stored either way, but the sidecar is only asked to change when the
        // intent actually changed — a save of unrelated fields must not re-push (and possibly
        // re-fail) a rule that is already in the state the admin asked for.
        var newFallbackPort = fallbackPort is > 0 and <= 65535 ? fallbackPort : 443;
        var fallbackChanged = customer.FallbackPortEnabled != fallbackPortEnabled
                              || customer.FallbackPort != newFallbackPort;
        customer.FallbackPortEnabled = fallbackPortEnabled;
        customer.FallbackPort        = newFallbackPort;
        // Client-side timing only — never pushed to the sidecar, so it doesn't participate in
        // fallbackChanged / the SetFallbackAsync call below.
        customer.FallbackTriggerSeconds = fallbackTriggerSeconds is >= 1 and <= 300 ? fallbackTriggerSeconds : 20;

        if (fallbackChanged && customer.ServerMode == "Valenius" && !string.IsNullOrEmpty(customer.SidecarUrl))
        {
            var (ok, message, active) = await sidecar.SetFallbackAsync(
                customer.SidecarBaseUrl, fallbackPortEnabled, newFallbackPort);

            // FallbackPortActive is the observed state the heartbeat gates on, so it follows
            // the sidecar's answer — never the admin's intent. A refusal leaves the toggle on
            // (so the admin can see and retry what they asked for) but clients are not offered
            // a port the server does not actually answer on.
            customer.FallbackPortActive = active;
            if (!ok)
                TempData["WgError"] = "Port fallback was not applied: " + message;
            else
                TempData["WgSuccess"] = message;

            _ = audit.LogAsync(Audit.Customer, "Port fallback changed", $"Customer: {customer.Name}",
                $"enabled={fallbackPortEnabled}, port={newFallbackPort}, applied={ok}, active={active}");
        }
        else if (fallbackChanged)
        {
            // No sidecar to ask (no address configured, or not a Valenius customer) — nothing
            // can confirm the redirect, so nothing is advertised to clients.
            customer.FallbackPortActive = false;
            _ = audit.LogAsync(Audit.Customer, "Port fallback changed", $"Customer: {customer.Name}",
                $"enabled={fallbackPortEnabled}, port={newFallbackPort}, no sidecar to apply to");
        }

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
        await LoadPageDataAsync(customer);

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

    // ── Firewall port checks ──────────────────────────────────────────────────
    //
    // What can honestly be determined, per protocol:
    //
    //   TCP  — definitive. A completed handshake proves the port is open; a refusal or
    //          timeout proves the backend cannot use it.
    //   UDP  — one-sided. An ICMP port-unreachable proves the port is CLOSED, but silence
    //          means "open" and "silently dropped by a firewall" alike, because WireGuard
    //          ignores packets it can't decrypt. So a UDP probe can disprove, never confirm,
    //          and must never be rendered as a green pass.
    //
    // All checks run from the BACKEND's network position, which is not a VPN client's. A
    // sidecar allowlisted for this backend can pass every check here and still be unreachable
    // for clients — the UI says so rather than implying the fleet is fine.

    public enum PortState { Ok, Warn, Fail, Unknown }

    public sealed record PortCheck(string Key, PortState State, string Label, string Detail);

    /// <summary>Results of the last "Check ports" run; null when it hasn't been run.</summary>
    public List<PortCheck>? PortChecks { get; private set; }

    public PortCheck? PortCheckFor(string key) => PortChecks?.FirstOrDefault(p => p.Key == key);

    public async Task<IActionResult> OnPostCheckPortsAsync(int id)
    {
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();
        await LoadPageDataAsync(customer);
        await FetchSidecarInfoAsync(customer);

        var mgmtHost   = customer.SidecarUrl ?? "";
        var publicHost = !string.IsNullOrWhiteSpace(customer.ServerEndpoint)
            ? StripToHost(customer.ServerEndpoint)
            : mgmtHost;
        var healthPort = customer.SidecarHealthPort > 0 ? customer.SidecarHealthPort : 9004;

        // A real WireGuard handshake is the ONLY way to confirm a UDP port — the ICMP probe can
        // disprove but never prove, which is why it can only ever produce an amber "no rejection".
        // So when a handshake is possible, use it and get a definitive answer; the ICMP probe is
        // kept solely for the cases where it isn't (OSS, unreachable sidecar, unknown server key).
        var canHandshake = IsProEdition
                           && !SidecarUnreachable
                           && SidecarListenPort.HasValue
                           && !string.IsNullOrEmpty(SidecarPublicKey)
                           && !string.IsNullOrEmpty(publicHost);

        var mgmtTcpTask   = CheckTcpPortAsync(mgmtHost, customer.SidecarManagementPort);
        var healthTcpTask = CheckTcpPortAsync(publicHost, healthPort);

        // Only fall back to the ICMP probes when no handshake is available. They each burn their
        // full timeout when the port is open (silence is the expected result), so they run in
        // parallel with the TCP checks rather than after them.
        var wgUdpTask = !canHandshake && SidecarListenPort.HasValue
            ? CheckUdpPortAsync(publicHost, SidecarListenPort.Value)
            : Task.FromResult<bool?>(null);
        var fbUdpTask = !canHandshake && customer.FallbackPortEnabled
            ? CheckUdpPortAsync(publicHost, customer.FallbackPort)
            : Task.FromResult<bool?>(null);

        await Task.WhenAll(mgmtTcpTask, healthTcpTask, wgUdpTask, fbUdpTask);

        var checks = new List<PortCheck>();

        // Management: the /info call already told us whether the port is *usable*. Probing TCP
        // as well splits the failure case in two, which point at completely different fixes:
        // a blocked port is a firewall problem, an open port with no answer is the container
        // or mTLS.
        checks.Add(!SidecarUnreachable
            ? new PortCheck("mgmt", PortState.Ok, "Open", "Management API answered.")
            : await mgmtTcpTask
                ? new PortCheck("mgmt", PortState.Warn, "Port open, no answer",
                    "The port accepts connections but the management API did not respond — "
                    + "check the container is running and enrolled, not the firewall.")
                : new PortCheck("mgmt", PortState.Fail, "Blocked",
                    "No TCP connection from this backend."));

        checks.Add(await healthTcpTask
            ? new PortCheck("health", PortState.Ok, "Open", $"TCP connect to {publicHost}:{healthPort} succeeded.")
            : new PortCheck("health", PortState.Fail, "Blocked",
                $"No TCP connection to {publicHost}:{healthPort} from this backend."));

        if (canHandshake)
        {
            var serverKey = SidecarPublicKey!;
            var listen    = SidecarListenPort!.Value;

            var wg = await ProbeEndpointAsync(customer, publicHost, listen, serverKey);
            checks.Add(HandshakeVerdict("wg", wg));

            if (customer.FallbackPortEnabled && customer.FallbackPort != listen)
            {
                var fb = await ProbeEndpointAsync(customer, publicHost, customer.FallbackPort, serverKey);
                var detail = fb.Ok || !wg.Ok
                    ? fb.Message
                    : fb.Message + " Since the WireGuard port answered, the peer and server key are "
                                 + "confirmed good — so this is the redirect or the firewall on this port alone.";
                checks.Add(fb.Ok
                    ? HandshakeVerdict("fallback", fb)
                    : new PortCheck("fallback", PortState.Fail, "No handshake", detail));
            }
            UdpConfirmedByHandshake = true;
        }
        else
        {
            checks.Add(UdpVerdict("wg", await wgUdpTask, SidecarListenPort));
            if (customer.FallbackPortEnabled)
                checks.Add(UdpVerdict("fallback", await fbUdpTask, customer.FallbackPort));
        }

        PortChecks         = checks;
        PortChecksHost     = publicHost;
        TempData["WgTab"]  = true;
        return Page();
    }

    /// <summary>True when the UDP rows came from a real handshake rather than the ICMP probe, so
    /// the panel can drop the "UDP can only be disproved" caveat that no longer applies.</summary>
    public bool UdpConfirmedByHandshake { get; private set; }

    private static PortCheck HandshakeVerdict(string key, WireGuardHandshakeProbe.Result r) => r.Ok
        ? new PortCheck(key, PortState.Ok, "Open",
            "Confirmed by a real WireGuard handshake — the port is reachable, the server's key "
            + "matches, and it accepted a freshly provisioned peer. " + r.Message)
        : new PortCheck(key, PortState.Fail, "No handshake", r.Message);

    /// <summary>Host the public-facing probes were aimed at, echoed in the UI so the operator can
    /// see which name was actually tested.</summary>
    public string? PortChecksHost { get; private set; }

    // ── Full configuration test ───────────────────────────────────────────────

    /// <summary>The sidecar's own host diagnosis; null when unreachable or too old.</summary>
    public SidecarSelfTest? SelfTest { get; private set; }

    /// <summary>Handshake results per probed endpoint, in the order they were tried.</summary>
    public List<PortCheck>? HandshakeChecks { get; private set; }

    /// <summary>Set when the temporary test peer could not be cleaned up, so the admin knows to
    /// look for it rather than discovering a stray peer later.</summary>
    public string? TestPeerLeak { get; private set; }

    /// <summary>
    /// End-to-end configuration test. Two halves that deliberately answer different questions:
    ///
    /// <list type="number">
    ///   <item>the sidecar's own <c>/selftest</c> — forwarding, NAT, conf drift: the failures
    ///   that are invisible from outside because the tunnel connects and then carries nothing;</item>
    ///   <item>a real WireGuard handshake from here — reachability, the server's key, and whether
    ///   peer registration actually took effect: the things only observable from outside.</item>
    /// </list>
    ///
    /// The handshake half provisions a throwaway peer, handshakes against the WireGuard port (and
    /// the fallback port when enabled), then removes it. Deliberately no tunnel is established:
    /// that would need NET_ADMIN on the backend, and a provisioned 0.0.0.0/0 AllowedIPs would
    /// hijack the backend's own default route.
    /// </summary>
    public async Task<IActionResult> OnPostFullTestAsync(int id)
    {
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();
        await LoadPageDataAsync(customer);
        await FetchSidecarInfoAsync(customer);
        TempData["WgTab"] = true;

        if (!IsProEdition)
        {
            TempData["WgError"] = "The full configuration test needs the integrated server (Pro).";
            return Page();
        }

        SelfTest = await sidecar.GetSelfTestAsync(customer.SidecarBaseUrl);

        var serverKey = SidecarPublicKey;
        if (SidecarListenPort is not int listenPort || string.IsNullOrEmpty(serverKey))
        {
            TempData["WgError"] = "The sidecar could not be reached, so the handshake test was skipped.";
            return Page();
        }

        var probeHost = !string.IsNullOrWhiteSpace(customer.ServerEndpoint)
            ? StripToHost(customer.ServerEndpoint)
            : customer.SidecarUrl ?? "";
        if (string.IsNullOrEmpty(probeHost))
        {
            TempData["WgError"] = "No server endpoint is configured, so there is nothing to handshake against.";
            return Page();
        }

        var checks = new List<PortCheck>();

        var wg = await ProbeEndpointAsync(customer, probeHost, listenPort, serverKey);
        checks.Add(new PortCheck("hs-wg", wg.Ok ? PortState.Ok : PortState.Fail,
            wg.Ok ? $"Handshake OK ({listenPort}/udp)" : $"No handshake ({listenPort}/udp)", wg.Message));

        if (customer.FallbackPortEnabled && customer.FallbackPort != listenPort)
        {
            var fb = await ProbeEndpointAsync(customer, probeHost, customer.FallbackPort, serverKey);

            // Cross-probe reasoning: a handshake is silent for three different reasons, but if one
            // port answered then the peer registration and the server key are both provably fine,
            // so the other port's silence can only be the port itself.
            var detail = fb.Ok || !wg.Ok
                ? fb.Message
                : fb.Message + " Since the WireGuard port answered, the peer and server key are "
                             + "confirmed good — so this is the redirect or the firewall on this port alone.";
            checks.Add(new PortCheck("hs-fallback", fb.Ok ? PortState.Ok : PortState.Fail,
                fb.Ok ? $"Handshake OK ({customer.FallbackPort}/udp)" : $"No handshake ({customer.FallbackPort}/udp)",
                detail));
        }

        HandshakeChecks = checks;

        // Audit-logged because it briefly mutates the sidecar's peer list — a stray test peer
        // appearing in a later investigation should be traceable to this action.
        _ = audit.LogAsync(Audit.Customer, "Full configuration test", $"Customer: {customer.Name}",
            $"selftest={(SelfTest is null ? "unavailable" : SelfTest.Ok ? "ok" : "failed")}, "
            + string.Join(", ", checks.Select(c => $"{c.Key}={c.State}")));

        return Page();
    }

    /// <summary>
    /// Registers a throwaway peer, handshakes against one endpoint, and removes the peer again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fresh keypair <b>per endpoint</b>, not one shared across all of them. WireGuard keeps
    /// per-peer replay protection: it stores the TAI64N timestamp of the last handshake initiation
    /// it accepted from a peer and silently drops any initiation whose timestamp is not strictly
    /// greater. Two probes issued microseconds apart can land on the same timestamp, so the second
    /// one is discarded — which was observed in testing as the fallback port appearing dead while
    /// the redirect rule's packet counter proved the packet had arrived. A distinct peer has
    /// distinct replay state, so each endpoint is measured independently rather than being
    /// sabotaged by the probe before it.
    /// </para>
    /// <para>
    /// The peer is needed at all because WireGuard only answers handshakes from keys it knows —
    /// which is exactly what makes a reply proof that peer provisioning works.
    /// </para>
    /// </remarks>
    private async Task<WireGuardHandshakeProbe.Result> ProbeEndpointAsync(
        Customer customer, string host, int port, string serverKey)
    {
        var (priv, pub, pubB64) = WireGuardHandshakeProbe.GenerateKeyPair();

        // TEST-NET-1 (RFC 5737) rather than an address from the customer's transit subnet: it can
        // never collide with a real client's allocation, and if cleanup ever fails the stray peer
        // is unmistakably a test artefact.
        if (!await sidecar.AddPeerAsync(customer.SidecarBaseUrl, pubB64, "192.0.2.254/32"))
            return new WireGuardHandshakeProbe.Result(false,
                "The test peer could not be registered on the sidecar, so this endpoint was not tested.");

        try
        {
            return await handshakeProbe.TryHandshakeAsync(
                host, port, serverKey, priv, pub, TimeSpan.FromSeconds(5));
        }
        finally
        {
            // Always remove the test peer, including when the probe threw. A leak is surfaced
            // rather than swallowed: a stray peer is a (small) security artefact, not cosmetic.
            if (!await sidecar.RemovePeerAsync(customer.SidecarBaseUrl, pubB64))
                TestPeerLeak = pubB64;
        }
    }

    /// <summary>
    /// Maps a UDP probe to a verdict. Deliberately never returns <see cref="PortState.Ok"/>:
    /// no ICMP rejection is consistent with both an open port and a DROP rule, and claiming
    /// "open" on that basis would tell an admin their firewall is fine when it isn't.
    /// </summary>
    private static PortCheck UdpVerdict(string key, bool? reachable, int? port) => (reachable, port) switch
    {
        (_, null)     => new PortCheck(key, PortState.Unknown, "Not checked",
                             "The port isn't known yet — connect to the sidecar first."),
        (false, _)    => new PortCheck(key, PortState.Fail, "Closed",
                             "ICMP port unreachable — nothing is listening, or the packet never arrived."),
        (true, _)     => new PortCheck(key, PortState.Warn, "No rejection",
                             "No ICMP rejection came back. That's what an open WireGuard port looks like — "
                             + "but a firewall that silently drops packets looks identical, so this cannot "
                             + "confirm the port is open. A real client connect is the only proof."),
        _             => new PortCheck(key, PortState.Unknown, "Inconclusive",
                             "The probe could not be completed (DNS or network error)."),
    };

    /// <summary>TCP reachability from this backend. Definitive both ways, unlike the UDP probe.</summary>
    private static async Task<bool> CheckTcpPortAsync(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535) return false;
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await tcp.ConnectAsync(host, port, cts.Token);
            return tcp.Connected;
        }
        catch { return false; }
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
            // WireGuard silently ignores packets it can't decrypt, so no answer is what an open
            // port looks like. A firewall that DROPs looks exactly the same, so callers must
            // treat this as "not rejected", NOT as proof the port is open — see UdpVerdict.
            return true;
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
