using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Filters;
using Valenius.Backend.Models;
using Valenius.Backend.Licensing;
using Valenius.Backend.Services;

namespace Valenius.Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ServiceFilter(typeof(DeviceCredentialFilter))]
public class ClientsController(ApplicationDbContext db, ClientNotifier notifier, ClientPresenceTracker presence, WgTunnelTracker wgTunnels, ILicenseEnforcement enforcement, IMfaSessionService mfa, IMfaChallengeService challenge, ISidecarService sidecar, HeartbeatSettingsStore heartbeatSettings, IManagementEventLog eventLog, DeviceRequestBudget requestBudget, DeviceAuthentication authentication) : ControllerBase
{
    /// <summary>LogEvent EventTypes that are client-side repair outcomes routed to ClientAlerts
    /// (Admin/Alerts) instead of ConnectionLogs. Kept as a set so LogEvent's dispatch and dedupe
    /// stay generic as new kinds are added.</summary>
    private static readonly HashSet<string> ClientAlertEventTypes =
        new(StringComparer.OrdinalIgnoreCase) { "ConfigRepairFailed", "OwnershipReclaimed" };

    // A new identity receives its own credential exactly once while still PENDING.
    // Existing identities prove possession, even while inactive. Only the explicit
    // legacy window admits snapshotted shared-key devices; hostname never proves ownership.
    [ApiKeyExempt]
    [HttpPost("register")]
    [RequestSizeLimit(65_536)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientKey) || request.ClientKey.Length > 36)
            return BadRequest("ClientKey is required.");

        using var control = requestBudget.TryControl(request.ClientKey);
        if (control is null) return StatusCode(StatusCodes.Status429TooManyRequests);
        var now    = DateTime.UtcNow;
        var client = await db.Clients
            .Include(c => c.Customer).ThenInclude(cu => cu!.TrustedNetworks)
            .FirstOrDefaultAsync(c => c.ClientKey == request.ClientKey);

        if (client is not null && !await authentication.AuthenticateAsync(client,
                Request.Headers["X-Api-Key"].ToString(), HttpContext.RequestAborted))
            return Unauthorized();
        string? issuedCredential = null;

        var isAndroid = string.Equals(request.Platform, "Android", StringComparison.OrdinalIgnoreCase);
        var deviceId = string.IsNullOrWhiteSpace(request.DeviceId) ? null : request.DeviceId.Trim();
        if (deviceId?.Length > 128)
            return BadRequest("DeviceId must not exceed 128 characters.");

        if (client is null)
        {
            var budget = requestBudget;
            if (!budget.TryRegister(HttpContext.Connection.RemoteIpAddress?.ToString(), now))
            {
                Response.Headers.RetryAfter = "60";
                return StatusCode(StatusCodes.Status429TooManyRequests);
            }
            if (await db.Clients.CountAsync(c => !c.IsActive && !c.IsDeleted) >= 10_000)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Pending-device capacity reached; administrator review is required.");
            issuedCredential = DeviceCredential.Mint();
            // Identity collision: a non-deleted client already exists under a different
            // ClientKey (typically a reinstall with a fresh key). Desktop clients retain the
            // hostname rule. Android model names are shared by every device of that model, so
            // Android uses ANDROID_ID and deliberately performs no duplicate check when an older
            // app does not provide one. Don't
            // silently create a parallel active client and don't fail with a misleading error —
            // record the new install as a pending duplicate so an admin can reassign the
            // original record to it, or keep both. (Excludes other pending-duplicate rows so
            // repeated heartbeats from this same new install don't chain.)
            var identityCollision = isAndroid
                ? deviceId is not null && await db.Clients.AnyAsync(c =>
                    !c.IsDeleted
                    && !c.IsDuplicatePending
                    && c.ClientKey != ""
                    && c.ClientKey != request.ClientKey
                    && c.Platform == "Android"
                    && c.DeviceId == deviceId)
                : !string.IsNullOrEmpty(request.Hostname)
                    && await db.Clients.AnyAsync(c =>
                        !c.IsDeleted
                        && !c.IsDuplicatePending
                        && c.ClientKey != request.ClientKey
                        && c.Hostname == request.Hostname);

            client = new Client
            {
                ClientKey          = request.ClientKey,
                DeviceCredentialHash = DeviceCredential.Hash(issuedCredential),
                Hostname           = request.Hostname,
                HostSid            = request.HostSid,
                Username           = request.Username,
                UserSid            = request.UserSid,
                ClientVersion      = request.Version,
                RegisteredAt       = now,
                LastSeenAt         = now,
                IsActive           = false,
                IsDuplicatePending = identityCollision,
                CustomerId         = await MatchCustomerAsync(request.Hostname),
                KnownProfiles      = SerializeProfiles(request.Profiles),
                Platform           = request.Platform,
                DeviceId           = deviceId
            };
            db.Clients.Add(client);
        }
        else
        {
            // Only updated clients advertise persistence-before-use support. Old
            // binaries continue on legacy auth until updated or the window expires.
            issuedCredential = await authentication.OfferAsync(client, Request.Headers["X-Api-Key"].ToString(),
                Request.Headers[DeviceAuthentication.UpgradeHeader].ToString(), HttpContext.RequestAborted);
            client.Hostname = request.Hostname;
            client.HostSid  = request.HostSid;
            if (!string.IsNullOrEmpty(request.Username)) client.Username = request.Username;
            if (!string.IsNullOrEmpty(request.UserSid))  client.UserSid  = request.UserSid;
            if (!string.IsNullOrEmpty(request.Version))  client.ClientVersion = request.Version;
            if (!string.IsNullOrEmpty(request.Platform)) client.Platform = request.Platform;
            if (deviceId is not null) client.DeviceId = deviceId;
            client.KnownProfiles = SerializeProfiles(request.Profiles);
            client.LastSeenAt = now;
        }

        // Repair Android rows that older app versions incorrectly marked as duplicates solely
        // because another phone had the same model name. A genuine reinstall remains a duplicate
        // when its Android ID matches another retained client; without an ID it becomes an ordinary
        // pending registration that an admin can review and activate.
        if (isAndroid && client.IsDuplicatePending)
        {
            client.IsDuplicatePending = deviceId is not null && await db.Clients.AnyAsync(c =>
                c.Id != client.Id
                && !c.IsDeleted
                && !c.IsDuplicatePending
                && c.ClientKey != ""
                && c.DeviceId == deviceId);
        }

        // Drain confirmed deletes from the queue: any queued name the client's own reported
        // profile list no longer contains has landed, so stop re-sending it. Every confirmed
        // entry clears in one pass (not just the oldest) — see ClientPendingDelete.
        if (request.Profiles is not null && !await ClientUpdateHalt.AppliesAsync(db, client))
        {
            var queued = await db.ClientPendingDeletes
                .Where(d => d.ClientId == client.Id)
                .ToListAsync();
            foreach (var d in queued)
            {
                if (!request.Profiles.Contains(d.ProfileName, StringComparer.OrdinalIgnoreCase))
                    db.ClientPendingDeletes.Remove(d);
            }
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyViolation(ex))
        {
            // Never give the losing unauthenticated first-registration the winner's
            // status/credential. Recovery after a lost first response is administrator-owned.
            db.ChangeTracker.Clear();
            return Conflict("Device identity already registered; use its credential or administrator recovery.");
        }

        // Re-load with Customer + TrustedNetworks to build the full response.
        await db.Entry(client).Reference(c => c.Customer).LoadAsync();
        if (client.Customer is not null)
            await db.Entry(client.Customer).Collection(cu => cu.TrustedNetworks).LoadAsync();

        // Mirror the same TrayRunning logic as the long-poll: heartbeat also updates
        // presence so the client goes online within ~5 s of TrayApp opening
        // (poll timer → SyncStatus → TryRegisterAsync → heartbeat).
        if (request.TrayRunning)
            presence.MarkSeen(client.ClientKey);
        else
            presence.MarkOffline(client.ClientKey);

        var heartbeatResponse = await BuildStatusResponseAsync(client);
        await db.SaveChangesAsync();
        if (heartbeatResponse is null) return ClientUpdateHalt.Paused(Response);
        return Ok(heartbeatResponse with { ClientApiKey = issuedCredential });
    }

    /// <summary>
    /// Builds the status response AND clears one-shot flags (PendingConnect) on the entity
    /// so the caller must SaveChanges after calling this.
    /// </summary>
    private async Task<StatusResponse?> BuildStatusResponseAsync(Client client)
    {
        // Denial/status polling must never deliver profiles, enrollment secrets or queued
        // commands, nor drain those commands while the device is pending/deactivated.
        if (!client.IsActive || client.IsDeleted)
            return new StatusResponse(false, DuplicatePending: client.IsDuplicatePending);
        var halted = await ClientUpdateHalt.AppliesAsync(db, client);
        var previousPolicy = halted ? ClientUpdateHalt.PreviousPolicy(client) : null;
        // Never synthesize a successful empty/default policy: old clients would apply it.
        if (halted && previousPolicy is null) return null;
        var networks = client.Customer?.TrustedNetworks
            .Select(n => new TrustedNetworkDto(n.Subnet, n.PrimaryDns))
            .ToArray() ?? [];

        // Capture one-shot flags and clear them — each fires exactly once per trigger.
        var pendingConnect    = !halted && client.PendingConnect;
        if (!halted) client.PendingConnect = false;

        var pendingDisconnect    = client.PendingDisconnect;
        client.PendingDisconnect = false;

        var forceUpdate    = !halted && client.ForceUpdate;
        if (!halted) client.ForceUpdate = false;

        var pendingConfigRepair    = !halted && client.PendingConfigRepair;
        if (!halted) client.PendingConfigRepair = false;

        var pendingServiceRestart    = !halted && client.PendingServiceRestart;
        if (!halted) client.PendingServiceRestart = false;

        // Deliver the oldest queued delete (see ClientPendingDelete) — not one-shot; only the
        // Register POST above drains confirmed entries (it's the only request that carries the
        // client's own Profiles list), so this keeps re-sending on every response — including
        // LongPoll responses — until a later Register call confirms it's gone.
        var pendingDeleteProfileName = await db.ClientPendingDeletes
            .Where(d => d.ClientId == client.Id)
            .OrderBy(d => d.CreatedAt)
            .Select(d => d.ProfileName)
            .FirstOrDefaultAsync();

        var heartbeatInterval = client.Customer?.HeartbeatIntervalMinutes is >= 1 and <= 60
            ? client.Customer.HeartbeatIntervalMinutes.Value
            : heartbeatSettings.DefaultIntervalMinutes;

        var serverProfileName = client.Customer?.ServerMode == "Valenius"
            ? WireGuardHelper.ProfileName(client.Customer.VpnPrefix, client.Customer.Name)
            : null;

        // Device tunnel (Windows): per-client override, else the customer default. Only ever
        // true when there's actually a native profile to connect (serverProfileName != null) —
        // an enabled-but-non-Valenius customer simply never sends a true flag, no separate
        // error path needed on the client.
        var deviceTunnelEnabled =
            (client.DeviceTunnelEnabled ?? client.Customer?.DeviceTunnelDefaultEnabled ?? false)
            && serverProfileName is not null;

        // Build health probe URLs for the Windows client connectivity verifier.
        // ServerHealthUrl  → pre-connect public reachability check (Phase 1, auto-connect only)
        // ServerVpnIp      → post-connect tunnel verification (Phase 2)
        // Both are only populated for Valenius-managed servers with a configured sidecar.
        // ServerVpnIp is ONLY sent over the existing HTTPS backend↔client channel.
        // Sidecar transit subnets — the gateway /health check is only offered when a sidecar's
        // transit network is globally unique (otherwise two sidecars could share a gateway IP).
        var allTransitSubnets = await db.Customers
            .Where(c => c.SidecarVpnSubnet != null && c.SidecarVpnSubnet != "")
            .Select(c => new { c.Id, c.SidecarVpnSubnet })
            .ToListAsync();
        bool TransitUnique(int customerId, string? subnet) =>
            WireGuardHelper.IsTransitUnique(subnet,
                allTransitSubnets.Where(s => s.Id != customerId).Select(s => s.SidecarVpnSubnet));

        var gatewayProfiles = new List<GatewayProfileEntry>();

        // Port-443 fallback (docs/design/port-443-fallback.md). Advertised per profile, and ONLY
        // when the serving sidecar has confirmed on /info that the redirect really is installed
        // (cached in FallbackPortActive). Advertising an unconfirmed fallback would make every
        // client waste its fallback window on a dead port at every connect, turning a failed
        // optional feature into a fleet-wide connect slowdown.
        //
        // This is deliberately limited to profiles the backend provisions — the native sidecar
        // profile and foreign ones. An External customer's profiles are manually-uploaded .conf
        // files whose names the backend never learns (serverProfileName is null for them), so
        // there is no per-profile key to attach a fallback to even if their own server did
        // answer on an alternate port.
        var fallbackEndpoints = new List<FallbackEndpointEntry>();

        static (int Port, int TriggerSeconds)? FallbackPortFor(bool enabled, bool active, int port, int triggerSeconds)
            => enabled && active && port is >= 1 and <= 65535
                ? (port, triggerSeconds is >= 1 and <= 300 ? triggerSeconds : 20)
                : null;

        string? serverHealthUrl = null;
        string? serverVpnIp    = null;
        if (client.Customer?.ServerMode == "Valenius"
            && !string.IsNullOrEmpty(client.Customer.ServerEndpoint))
        {
            var host = client.Customer.ServerEndpoint.Split(':')[0];
            // The Phase-2 probe is certless, so it must target the sidecar's dedicated
            // health server (HEALTH_PORT, server-TLS only) — NEVER the mTLS management
            // port, which would reject the handshake. Fall back to the sidecar's default
            // health port (9004) when the column is unset on legacy customer rows.
            var healthPort = client.Customer.SidecarHealthPort > 0
                ? client.Customer.SidecarHealthPort
                : 9004;
            serverHealthUrl = $"https://{host}:{healthPort}/health";

            // The gateway probe (serverVpnIp + native gateway-profile entry) is offered only
            // when the transit network is unique; otherwise it stays null and the client falls
            // back to the handshake check. serverHealthUrl stays for the Phase-1 public reach test.
            if (TransitUnique(client.Customer.Id, client.Customer.SidecarVpnSubnet)
                && !string.IsNullOrEmpty(client.Customer.SidecarVpnIp))
            {
                serverVpnIp = client.Customer.SidecarVpnIp;
                if (serverProfileName != null)
                    gatewayProfiles.Add(new GatewayProfileEntry(serverProfileName, client.Customer.SidecarVpnIp!, healthPort));
            }
        }

        // Native profile's fallback. Evaluated outside the block above because it is independent
        // of the gateway probe: a customer with a conflicting transit subnet gets no gateway
        // target but can still have a working fallback port.
        if (serverProfileName != null && client.Customer is not null
            && FallbackPortFor(client.Customer.FallbackPortEnabled, client.Customer.FallbackPortActive,
                               client.Customer.FallbackPort, client.Customer.FallbackTriggerSeconds) is var nativeFb
            && nativeFb is not null)
        {
            fallbackEndpoints.Add(new FallbackEndpointEntry(serverProfileName, nativeFb.Value.Port, nativeFb.Value.TriggerSeconds));
        }

        // When the license blocks activation (expired, limit reached), report IsActive=false
        // so the client deactivates its VPN without a DB change (reverts automatically on restore).
        //
        // client.Customer.Clients is NOT eager-loaded here (Register/GetStatus/LongPoll Include
        // only Customer + TrustedNetworks) and lazy-loading is off, so the old navigation Count
        // was always 0 and the cap never fired (audit M1). Count via a real query, EXCLUDING this
        // client — mirroring the admin activate path (Details.OnPostToggleActive), i.e. "may this
        // endpoint be active given the others?" — so a client already within the limit isn't
        // flapped off by counting itself against the cap.
        var customerMax = client.Customer?.MaxEndpoints ?? 0;
        var activeCount = client.CustomerId is int custId
            ? await db.Clients.CountAsync(c => c.CustomerId == custId && c.Id != client.Id && c.IsActive && !c.IsDeleted)
            : 0;
        var canActivate = enforcement.CanActivateEndpoint(activeCount, customerMax);
        var effectiveIsActive = client.IsActive && canActivate.Allowed;

        // MFA session gating (Pro; no-op in OSS). When the peer is gated and has no active
        // session, the tray must drive re-authentication via the deep link.
        bool mfaRequired = false, mfaEnrollmentOpen = client.MfaEnrollmentOpen;
        string? mfaAuthorizeUrl = null, mfaEnrollmentUri = null;
        DateTime? mfaSessionExpiresAt = null;
        var mfaPolicy = mfa.ResolvePolicy(client, client.Customer);
        if (mfaPolicy.Gated)
        {
            var active = await mfa.GetActiveSessionAsync(client.Id);
            mfaSessionExpiresAt = active?.ExpiresAt;
            mfaRequired         = active is null;
            mfaAuthorizeUrl     = $"{Request.Scheme}://{Request.Host}/mfa/authorize?peer={Uri.EscapeDataString(client.ClientKey)}";
        }
        if (mfaEnrollmentOpen)
            mfaEnrollmentUri = mfa.GetEnrollmentUri(client, client.Customer);

        // Collect pending foreign configs and clear them atomically (delivered inline).
        // Archive each on the way out so a future reinstall reassign can re-send it.
        var pendingForeignRows = await db.ClientForeignProfiles
            .Where(p => !halted && p.ClientId == client.Id && p.PendingConfigFileName != null)
            .ToListAsync();
        var inlineConfigs = new List<PendingForeignConfigEntry>();
        foreach (var p in pendingForeignRows)
        {
            inlineConfigs.Add(new PendingForeignConfigEntry(p.PendingConfigFileName!, p.PendingConfigContent ?? ""));
            await ClientConfigArchiveStore.UpsertAsync(db, client.Id, p.PendingConfigFileName, p.PendingConfigContent);
            p.PendingConfigFileName = null;
            p.PendingConfigContent  = null;
        }

        // Reinstall reassign: re-deliver every archived profile flagged for resend (one-shot),
        // so a freshly-installed device receives all the profiles the record already had —
        // including manually-uploaded configs that can't be re-provisioned. Reuses the inline
        // foreign-config channel (all clients already save these to staged).
        var resendRows = await db.ClientConfigArchives
            .Where(a => !halted && a.ClientId == client.Id && a.PendingResend)
            .ToListAsync();
        foreach (var a in resendRows)
        {
            inlineConfigs.Add(new PendingForeignConfigEntry(a.ProfileName, a.Content));
            a.PendingResend = false;
        }

        var pendingForeignConfigs = inlineConfigs.Count > 0 ? inlineConfigs.ToArray() : null;

        // Foreign sidecar profiles: add a gateway-probe entry for each whose source sidecar
        // has a unique transit network. (Distinct from pendingForeignRows above, which is the
        // one-shot config delivery — these persist so the client can verify on every connect.)
        var foreignGatewayRows = await db.ClientForeignProfiles
            .Where(p => p.ClientId == client.Id && p.ProfileName != null && p.SourceCustomer != null)
            .Select(p => new
            {
                p.ProfileName,
                SourceId   = p.SourceCustomer!.Id,
                p.SourceCustomer.SidecarVpnIp,
                p.SourceCustomer.SidecarVpnSubnet,
                p.SourceCustomer.SidecarHealthPort,
                p.SourceCustomer.SidecarLanCidrs,
                p.SourceCustomer.FallbackPortEnabled,
                p.SourceCustomer.FallbackPort,
                p.SourceCustomer.FallbackPortActive,
                p.SourceCustomer.FallbackTriggerSeconds
            })
            .ToListAsync();
        foreach (var fr in foreignGatewayRows)
        {
            if (string.IsNullOrEmpty(fr.ProfileName)) continue;

            // A foreign profile's fallback comes from its OWN source customer's server, not
            // from this client's customer — same per-profile scoping as the gateway entry below.
            if (FallbackPortFor(fr.FallbackPortEnabled, fr.FallbackPortActive, fr.FallbackPort, fr.FallbackTriggerSeconds) is var foreignFb
                && foreignFb is not null)
                fallbackEndpoints.Add(new FallbackEndpointEntry(fr.ProfileName!, foreignFb.Value.Port, foreignFb.Value.TriggerSeconds));

            if (string.IsNullOrEmpty(fr.SidecarVpnIp)) continue;
            if (!TransitUnique(fr.SourceId, fr.SidecarVpnSubnet)) continue;
            var hp = fr.SidecarHealthPort > 0 ? fr.SidecarHealthPort : 9004;
            gatewayProfiles.Add(new GatewayProfileEntry(fr.ProfileName!, fr.SidecarVpnIp!, hp));
        }

        // Cross-device push-to-approve: ensure a challenge for a gated requester that has
        // an approver device, and surface the number it should display; plus any approvals
        // this device must act on (when it is itself an approver phone).
        int? mfaApproveNumber = null;
        if (mfaRequired && client.MfaApproverClientId is not null)
        {
            await challenge.EnsureChallengeAsync(client, HttpContext.Connection.RemoteIpAddress?.ToString());
            mfaApproveNumber = await challenge.GetRequesterMatchNumberAsync(client.Id);
        }
        var pendingApprovals = (await challenge.GetPendingApprovalsAsync(client.Id))
            .Select(a => new MfaApprovalEntry(a.ChallengeId, a.RequesterName, a.RequesterIp, a.Choices, a.ExpiresAt))
            .ToArray();

        // One-shot: an admin asked this client to upload its logs. Clear on delivery (the caller
        // SaveChanges after this) so it fires exactly once.
        var logUploadRequested = !halted && client.PendingLogRequest;
        if (logUploadRequested) client.PendingLogRequest = false;

        // Per-profile remote LAN CIDR(s), for the client's pre-connect LAN-conflict check. MUST
        // stay scoped to the profile whose sidecar actually reports it — the native server
        // profile gets its own customer's SidecarLanCidrs, each foreign profile gets its OWN
        // source customer's, from the SAME foreignGatewayRows query above (already joined to
        // SourceCustomer). Applying one customer's LAN blindly to every profile would misattribute
        // a conflict (e.g. the native customer's LAN wrongly blocking an unrelated foreign
        // profile's connect, or vice versa) — this bit us in testing, keep it profile-scoped.
        var lanCidrEntries = new List<LanCidrProfileEntry>();
        if (serverProfileName != null && client.Customer?.ServerMode == "Valenius" && !string.IsNullOrEmpty(client.Customer.SidecarLanCidrs))
        {
            var cidrs = client.Customer.SidecarLanCidrs
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (cidrs.Length > 0) lanCidrEntries.Add(new LanCidrProfileEntry(serverProfileName, cidrs));
        }
        foreach (var fr in foreignGatewayRows)
        {
            if (string.IsNullOrEmpty(fr.SidecarLanCidrs) || string.IsNullOrEmpty(fr.ProfileName)) continue;
            var cidrs = fr.SidecarLanCidrs
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (cidrs.Length > 0) lanCidrEntries.Add(new LanCidrProfileEntry(fr.ProfileName!, cidrs));
        }
        var remoteLanCidrsByProfile = lanCidrEntries.Count > 0 ? lanCidrEntries.ToArray() : null;

        var response = new StatusResponse(
            effectiveIsActive,
            client.PendingConfigFileName != null,
            client.AutoConnectEnabled,
            client.AutoConnectProfileName,
            networks,
            pendingDeleteProfileName,
            pendingConnect,
            heartbeatInterval,
            serverProfileName,
            serverHealthUrl,
            serverVpnIp,
            forceUpdate,
            client.SkipConnectivityCheck,
            pendingDisconnect,
            mfaRequired,
            mfaAuthorizeUrl,
            mfaSessionExpiresAt,
            mfaEnrollmentOpen,
            mfaEnrollmentUri,
            client.IsDuplicatePending,
            pendingForeignConfigs,
            gatewayProfiles.Count > 0 ? gatewayProfiles.ToArray() : null,
            mfaApproveNumber,
            pendingApprovals.Length > 0 ? pendingApprovals : null,
            logUploadRequested,
            string.IsNullOrEmpty(client.UpdateChannel) ? "stable" : client.UpdateChannel,
            null, // Device credentials are never reissued by a status/heartbeat read.
            remoteLanCidrsByProfile,
            pendingConfigRepair,
            deviceTunnelEnabled,
            fallbackEndpoints.Count > 0 ? fallbackEndpoints.ToArray() : null,
            pendingServiceRestart);

        if (halted)
            return previousPolicy! with
            {
                IsActive = effectiveIsActive,
                PendingDisconnect = pendingDisconnect,
                MfaRequired = mfaRequired,
                MfaAuthorizeUrl = mfaAuthorizeUrl,
                MfaSessionExpiresAt = mfaSessionExpiresAt,
                MfaEnrollmentOpen = mfaEnrollmentOpen,
                MfaEnrollmentUri = mfaEnrollmentUri,
                MfaApproveNumber = mfaApproveNumber,
                PendingApprovals = response.PendingApprovals,
                UpdateChannel = ReleaseChannel.Normalize(client.UpdateChannel)
            };

        ClientUpdateHalt.Remember(client, response);
        return response;
    }

    [HttpPost("event")]
    public async Task<IActionResult> LogEvent([FromBody] LogEventRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientKey))
            return BadRequest("ClientKey is required.");

        var client = await db.Clients
            .Include(c => c.Customer)
            .FirstOrDefaultAsync(c => c.ClientKey == request.ClientKey && !c.IsDeleted);
        if (client is null)
            return NotFound();

        // Not a VPN connection event — a client-side repair outcome worth surfacing on Admin/Alerts
        // rather than the connection history. "ConfigRepairFailed": the client's local self-heal
        // still couldn't fix the problem. "OwnershipReclaimed": it DID fix it, but only after
        // forcibly reclaiming ownership of the data directory — informational, since an object
        // under a folder only SYSTEM should ever write to lost SYSTEM ownership somehow. Deduped:
        // only raise a new alert if there isn't already an unacknowledged one of the same kind for
        // this client, so a machine stuck in the broken state doesn't spam a fresh row on every
        // service restart / repair attempt.
        if (ClientAlertEventTypes.Contains(request.EventType))
        {
            var hasOpenAlert = await db.ClientAlerts.AnyAsync(a =>
                a.ClientId == client.Id && a.Kind == request.EventType && !a.IsAcknowledged);
            if (!hasOpenAlert)
            {
                db.ClientAlerts.Add(new ClientAlert
                {
                    ClientId  = client.Id,
                    Kind      = request.EventType,
                    Detail    = request.Detail,
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
                _ = eventLog.RecordAsync(ManagementEventType.ClientAlert, client.Id, client.CustomerId,
                    $"{request.EventType}: {request.Detail}");
            }
            return Ok();
        }

        // Not a state transition either — the gateway /health probe succeeded for an
        // already-connected tunnel (see WgTunnelTracker.IsDisplayConnected). Not logged to
        // ConnectionLogs; it isn't a connect/disconnect, just a confirmation of one already
        // recorded.
        if (string.Equals(request.EventType, "Verified", StringComparison.OrdinalIgnoreCase))
        {
            wgTunnels.MarkVerified(client.ClientKey, request.TunnelName);
            return Ok();
        }

        // A client's own assigned customer's native profile, if it has one. Every sidecar-backed
        // tunnel — native or foreign — is identified by matching request.TunnelName against this
        // or against a ClientForeignProfiles row, since a client can have several tunnels up at
        // once (see root CLAUDE.md "Simultaneous multi-VPN connections") and only the tunnel this
        // specific event is actually about may be marked connected/disconnected.
        var nativeProfileName = client.Customer?.ServerMode == "Valenius"
            ? WireGuardHelper.ProfileName(client.Customer.VpnPrefix, client.Customer.Name)
            : null;
        var isNativeTunnel = nativeProfileName is not null
            && string.Equals(request.TunnelName, nativeProfileName, StringComparison.OrdinalIgnoreCase);

        async Task<bool> IsTransitUniqueForAsync(int customerId, string? subnet)
        {
            if (string.IsNullOrEmpty(subnet)) return false;
            var others = await db.Customers
                .Where(c => c.Id != customerId && c.SidecarVpnSubnet != null && c.SidecarVpnSubnet != "")
                .Select(c => c.SidecarVpnSubnet)
                .ToListAsync();
            return WireGuardHelper.IsTransitUnique(subnet, others);
        }

        if (string.Equals(request.EventType, "Connect", StringComparison.OrdinalIgnoreCase))
        {
            // A gateway-verified connect (Pro sidecar, globally-unique transit network) is
            // tracked separately from a plain handshake-only one, so the admin list's "connected"
            // star can require actual verification where that's a meaningful signal, while
            // tunnels with no gateway target keep today's simpler "peer registered" behavior —
            // a handshake alone isn't reliable enough to gate a display indicator on (WireGuard's
            // own rekey timers mean a genuinely-connected peer can go ~180s without a fresh one).
            string? wgPublicKey = null;
            var expectsGatewayVerification = false;

            if (isNativeTunnel && client.WgPublicKey is not null && client.Customer is not null)
            {
                wgPublicKey = client.WgPublicKey;
                if (!string.IsNullOrEmpty(client.Customer.SidecarVpnIp))
                    expectsGatewayVerification = await IsTransitUniqueForAsync(client.Customer.Id, client.Customer.SidecarVpnSubnet);
            }
            else if (!isNativeTunnel)
            {
                var foreign = await db.ClientForeignProfiles
                    .Include(p => p.SourceCustomer)
                    .FirstOrDefaultAsync(p => p.ClientId == client.Id
                        && p.ProfileName == request.TunnelName
                        && p.WgPublicKey != null);
                if (foreign?.SourceCustomer is { } source)
                {
                    wgPublicKey = foreign.WgPublicKey;
                    if (!string.IsNullOrEmpty(source.SidecarVpnIp))
                        expectsGatewayVerification = await IsTransitUniqueForAsync(source.Id, source.SidecarVpnSubnet);
                }
            }

            if (wgPublicKey is not null)
            {
                wgTunnels.MarkConnected(client.ClientKey, request.TunnelName, wgPublicKey, expectsGatewayVerification);
                _ = eventLog.RecordAsync(ManagementEventType.ClientConnected, client.Id, client.CustomerId, client.Hostname);
            }

            // A prior "Kill Tunnel" removed the NATIVE peer from the sidecar. Now that the
            // client is actively reconnecting THAT tunnel specifically, transparently re-add
            // it — a kick, not a ban. A foreign-profile connect must never resurrect an
            // unrelated killed native peer.
            //
            // NEVER raw-re-add an MFA-gated peer: a gated peer must be absent from the
            // interface until MfaSessionService pushes a CA-signed grant, and a plain
            // AddPeerAsync here would restore full VPN access with no active session / no
            // grant — and MfaReconcilerService (which reconciles the /grants store, not raw
            // peers) would not undo it. For gated peers the grant pipeline is the only re-add
            // path; the client re-authenticates via the MfaAuthorizeUrl deep link on its next
            // heartbeat. WgPeerKilled is left set and self-clears if the policy later ungates.
            if (isNativeTunnel && client.WgPeerKilled
                && client.Customer?.ServerMode == "Valenius"
                && client.WgPublicKey is not null
                && client.VpnIpAddress is not null
                && !mfa.ResolvePolicy(client, client.Customer).Gated)
            {
                var added = await sidecar.AddPeerAsync(
                    client.Customer.SidecarBaseUrl, client.Customer.Id, client.WgPublicKey, $"{client.VpnIpAddress}/32");
                if (added)
                    client.WgPeerKilled = false;
            }
        }
        else if (string.Equals(request.EventType, "Disconnect", StringComparison.OrdinalIgnoreCase))
        {
            wgTunnels.MarkDisconnected(client.ClientKey, request.TunnelName);
            _ = eventLog.RecordAsync(ManagementEventType.ClientDisconnected, client.Id, client.CustomerId, client.Hostname);

            // PerConnection MFA gates the native tunnel specifically (grants are pushed to the
            // client's own customer's sidecar) — a foreign-profile disconnect must not revoke a
            // session tied to an unrelated, possibly still-connected, native tunnel.
            if (isNativeTunnel && mfa.ResolvePolicy(client, client.Customer).Policy == MfaPolicy.PerConnection)
            {
                var active = await mfa.GetActiveSessionAsync(client.Id);
                if (active is not null)
                    await mfa.RevokeSessionAsync(active.SessionId, "client-disconnect");
            }
        }

        db.ConnectionLogs.Add(new ConnectionLog
        {
            ClientId   = client.Id,
            Username   = request.Username,
            TunnelName = request.TunnelName,
            EventType  = request.EventType,
            LanIp      = request.LanIp,
            WanIp      = request.WanIp,
            Detail     = request.Detail ?? "",
            OccurredAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return Ok();
    }

    /// <summary>
    /// Confirms per-client TOTP enrollment: the tray posts the first valid 6-digit code.
    /// No-op (BadRequest) in OSS where MFA is unavailable.
    /// </summary>
    [HttpPost("mfa/enroll-confirm")]
    public async Task<IActionResult> MfaEnrollConfirm([FromBody] MfaEnrollConfirmRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientKey))
            return BadRequest("ClientKey is required.");

        var client = await db.Clients
            .Include(c => c.Customer)
            .FirstOrDefaultAsync(c => c.ClientKey == request.ClientKey && !c.IsDeleted);
        if (client is null)
            return NotFound();

        var ok = await mfa.ConfirmEnrollmentAsync(client, request.Code ?? "");
        return ok ? Ok(new { confirmed = true }) : BadRequest(new { confirmed = false });
    }

    [DeviceStatus]
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus([FromQuery] string clientKey)
    {
        if (string.IsNullOrWhiteSpace(clientKey))
            return BadRequest("clientKey is required.");

        var client = await db.Clients
            .Include(c => c.Customer).ThenInclude(cu => cu!.TrustedNetworks)
            .FirstOrDefaultAsync(c => c.ClientKey == clientKey && !c.IsDeleted);
        if (client is null)
            return NotFound();

        client.LastSeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var statusResponse = await BuildStatusResponseAsync(client);
        await db.SaveChangesAsync();
        if (statusResponse is null) return ClientUpdateHalt.Paused(Response);
        return Ok(statusResponse);
    }

    // PostgreSQL error code 23505 = unique_violation
    private static bool IsDuplicateKeyViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private static string? SerializeProfiles(string[]? profiles) =>
        profiles is { Length: > 0 }
            ? System.Text.Json.JsonSerializer.Serialize(profiles)
            : null;

    private async Task<int?> MatchCustomerAsync(string hostname)
    {
        if (string.IsNullOrEmpty(hostname)) return null;
        var prefixes = await db.Customers
            .Where(c => c.ClientPrefix != null && c.ClientPrefix != "")
            .Select(c => new { c.Id, c.ClientPrefix })
            .ToListAsync();
        return prefixes
            .FirstOrDefault(p => hostname.StartsWith(p.ClientPrefix!, StringComparison.OrdinalIgnoreCase))
            ?.Id;
    }

    /// <summary>
    /// Long-poll endpoint: holds the connection for up to 55 seconds and returns as soon
    /// as any admin action changes this client's state (or on timeout).  The client
    /// processes the response identically to a heartbeat, then immediately reopens the poll.
    /// This gives sub-second delivery of PendingConnect, config pushes, profile deletes, etc.
    /// </summary>
    [DeviceStatus]
    [DevicePoll]
    [HttpGet("poll")]
    public async Task<IActionResult> LongPoll(
        [FromQuery] string clientKey,
        [FromQuery] bool trayRunning = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientKey))
            return BadRequest("clientKey is required.");

        // Verify client exists before holding the connection open.
        var exists = await db.Clients.AnyAsync(c => c.ClientKey == clientKey && !c.IsDeleted, ct);
        if (!exists) return NotFound();

        // Online = TrayApp is open on the user's machine (not just the background service).
        // Actively mark offline when trayRunning=false so the status updates within one
        // poll cycle (~55 s) rather than waiting for the 7-minute threshold to expire.
        if (trayRunning)
            presence.MarkSeen(clientKey);
        else
            presence.MarkOffline(clientKey);

        // Wait until notified by an admin action, or the 55-second window expires,
        // or the HTTP client disconnects.  Task.WhenAny never throws.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try { await notifier.WaitAsync(clientKey, linked.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        ct.ThrowIfCancellationRequested();

        // A disconnected poll must not consume commands or retain notification state.
        var client = await db.Clients
            .Include(c => c.Customer).ThenInclude(cu => cu!.TrustedNetworks)
            .FirstOrDefaultAsync(c => c.ClientKey == clientKey && !c.IsDeleted, ct);
        if (client is null) return NotFound();
        // The poll may have waited across credential rotation/reassignment. Revalidate
        // fresh DB state before consuming commands or returning any private content.
        if (!authentication.Accepts(client, Request.Headers["X-Api-Key"].ToString()))
            return Unauthorized();

        var response = await BuildStatusResponseAsync(client);      // clears PendingConnect on entity
        await db.SaveChangesAsync(ct);
        if (response is null) return ClientUpdateHalt.Paused(Response);
        return Ok(response);
    }

    /// <summary>
    /// Best-effort offline notification.  Called by the Windows service before it exits
    /// (service stop, machine shutdown) and when the TrayApp closes.
    /// Immediately removes the client from the in-memory presence tracker so the admin
    /// panel shows the client as offline without waiting for the threshold to expire.
    /// </summary>
    [HttpPost("offline")]
    public async Task<IActionResult> GoOffline([FromQuery] string clientKey)
    {
        if (string.IsNullOrWhiteSpace(clientKey))
            return BadRequest("clientKey is required.");

        var exists = await db.Clients.AnyAsync(c => c.ClientKey == clientKey && !c.IsDeleted);
        if (!exists) return NotFound();

        presence.MarkOffline(clientKey);
        return Ok();
    }

    /// <summary>
    /// Receives a diagnostic log bundle from a client (see <see cref="Models.ClientLogBundle"/>).
    /// The client has already collected a fixed, documented allowlist of Valenius-only logs,
    /// redacted secrets, and gzipped them. Stored per client (newest 10 kept) for an admin to
    /// download on the client's Details page. API-key protected (class-level filter).
    /// </summary>
    [HttpPost("logs")]
    [RequestSizeLimit(15_000_000)]
    public async Task<IActionResult> UploadLogs([FromForm] string clientKey, [FromForm] string? trigger, IFormFile? file)
    {
        if (string.IsNullOrWhiteSpace(clientKey))
            return BadRequest("clientKey is required.");
        if (file is null || file.Length == 0)
            return BadRequest("No log bundle provided.");
        if (file.Length > 15_000_000)
            return BadRequest("Log bundle too large (15 MB max).");

        var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientKey == clientKey && !c.IsDeleted);
        if (client is null) return NotFound();

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);

        db.ClientLogBundles.Add(new Models.ClientLogBundle
        {
            ClientId    = client.Id,
            UploadedAt  = DateTime.UtcNow,
            Trigger     = string.Equals(trigger, "User", StringComparison.OrdinalIgnoreCase) ? "User" : "Admin",
            FileName    = $"valenius-logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.tar.gz",
            ContentType = string.IsNullOrEmpty(file.ContentType) ? "application/gzip" : file.ContentType,
            SizeBytes   = (int)file.Length,
            Content     = ms.ToArray(),
        });
        await db.SaveChangesAsync();

        // Retention: keep only the newest 10 bundles per client.
        var stale = await db.ClientLogBundles
            .Where(b => b.ClientId == client.Id)
            .OrderByDescending(b => b.UploadedAt)
            .Skip(10)
            .ToListAsync();
        if (stale.Count > 0)
        {
            db.ClientLogBundles.RemoveRange(stale);
            await db.SaveChangesAsync();
        }

        return Ok(new { received = true });
    }

    [HttpPost("device")]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientKey))
            return BadRequest("ClientKey is required.");

        var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientKey == request.ClientKey);
        if (client is null) return NotFound();

        client.FcmToken = string.IsNullOrWhiteSpace(request.FcmToken) ? null : request.FcmToken;
        await db.SaveChangesAsync();
        return Ok();
    }

    [HttpPost("mfa/approve")]
    public async Task<IActionResult> ApproveMfa([FromBody] MfaApproveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientKey)) return BadRequest("ClientKey is required.");
        var approver = await db.Clients.FirstOrDefaultAsync(c => c.ClientKey == request.ClientKey && !c.IsDeleted);
        if (approver is null) return NotFound();
        var ok = await challenge.ApproveAsync(approver.Id, request.ChallengeId, request.MatchedNumber,
            HttpContext.Connection.RemoteIpAddress?.ToString());
        return ok ? Ok() : BadRequest("Approval failed (expired, mismatched, or not addressed to this device).");
    }

    [HttpPost("mfa/deny")]
    public async Task<IActionResult> DenyMfa([FromBody] MfaDenyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientKey)) return BadRequest("ClientKey is required.");
        var approver = await db.Clients.FirstOrDefaultAsync(c => c.ClientKey == request.ClientKey && !c.IsDeleted);
        if (approver is null) return NotFound();
        await challenge.DenyAsync(approver.Id, request.ChallengeId);
        return Ok();
    }

    // Customer-wide pairing codes enroll NEW identities. Existing identities need either
    // their credential (same customer) or an exact-device recovery code issued by an admin.
    [ApiKeyExempt]
    [HttpPost("pairing/redeem")]
    public async Task<IActionResult> RedeemPairing([FromBody] RedeemPairingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.ClientKey)
            || request.ClientKey.Length > 36)
            return BadRequest("Token and ClientKey are required.");
        if (request.DeviceId?.Trim().Length > 128)
            return BadRequest("DeviceId must not exceed 128 characters.");

        var now = DateTime.UtcNow;
        var token = await db.PairingTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Token == request.Token);
        if (token is null || token.UsedAt is not null || token.ExpiresAt <= now)
            return BadRequest("Invalid or expired pairing code.");

        // Original startup-created tables store UTC in TIMESTAMP without time zone.
        // Their values are read as Unspecified; Npgsql's current EF parameter mapping
        // requires UTC for timestamp-with-time-zone parameters. Preserve the UTC ticks.
        var expiresAtUtc = DateTime.SpecifyKind(token.ExpiresAt, DateTimeKind.Utc);

        // Claim before activating anything. The database arbitrates concurrent requests,
        // including requests handled by different backend processes. A failed activation
        // rolls the claim back, so it cannot burn a code without activating the device.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            now = DateTime.UtcNow;
            var claimed = await db.PairingTokens
                .Where(t => t.Id == token.Id && t.Token == request.Token
                         && t.CustomerId == token.CustomerId
                         && t.ClientKey == token.ClientKey
                         && t.ExpiresAt == expiresAtUtc
                         && t.UsedAt == null && t.ExpiresAt > now)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.UsedAt, now));
            // A competing transaction may have held the row lock until after expiry.
            if (claimed != 1 || token.ExpiresAt <= DateTime.UtcNow)
                return BadRequest("Invalid or expired pairing code.");

            var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientKey == request.ClientKey);
            if (token.ClientKey is not null && (token.ClientKey != request.ClientKey || client is null
                || client.CustomerId != token.CustomerId))
                return BadRequest("Pairing code does not match this device.");
            if (client is not null && token.ClientKey is null
                && (client.IsDeleted || (client.CustomerId is not null && client.CustomerId != token.CustomerId)
                    || !DeviceCredential.Matches(Request.Headers["X-Api-Key"].ToString(), client.DeviceCredentialHash)))
                return Unauthorized();
            if (client is null)
            {
                client = new Client
                {
                    ClientKey    = request.ClientKey,
                    Hostname     = request.Hostname ?? "",
                    HostSid      = "",
                    Username     = "",
                    UserSid      = "",
                    RegisteredAt = now,
                };
                db.Clients.Add(client);
            }

            var deviceCredential = DeviceCredential.Mint();
            client.DeviceCredentialHash = DeviceCredential.Hash(deviceCredential);
            client.LegacyAuthenticationEligible = false;
            client.PendingDeviceCredentialHash = null;
            client.PendingDeviceCredentialProtected = null;
            // Customer assignment comes only from the claimed token.
            client.IsActive           = true;
            client.IsDeleted          = false;
            client.IsDuplicatePending = false;
            client.CustomerId         = token.CustomerId;
            if (!string.IsNullOrEmpty(request.Hostname)) client.Hostname = request.Hostname;
            if (!string.IsNullOrEmpty(request.Platform)) client.Platform = request.Platform;
            if (!string.IsNullOrWhiteSpace(request.DeviceId)) client.DeviceId = request.DeviceId.Trim();
            client.LastSeenAt = now;

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (attempt == 0 && IsDuplicateKeyViolation(ex))
            {
                // A first registration may have inserted this ClientKey after our lookup.
                // Retry the WHOLE transaction, including a fresh conditional token claim.
                // Never update a detached token after clearing EF's tracker.
                await transaction.RollbackAsync();
                db.ChangeTracker.Clear();
                continue;
            }

            await transaction.CommitAsync();
            // Return this device's credential only after activation/consumption commit.
            return Ok(new { activated = true, apiKey = deviceCredential });
        }

        return Problem("Pairing conflict could not be resolved.", statusCode: 500);
    }

    [HttpGet("pending-config")]
    public async Task<IActionResult> GetPendingConfig([FromQuery] string clientKey)
    {
        if (string.IsNullOrWhiteSpace(clientKey))
            return BadRequest("clientKey is required.");

        var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientKey == clientKey && !c.IsDeleted);
        if (client is null)
            return NotFound();

        if (await ClientUpdateHalt.AppliesAsync(db, client))
            return ClientUpdateHalt.Paused(Response);

        if (client.PendingConfigFileName is null)
            return NoContent();

        var result = new PendingConfigResponse(client.PendingConfigFileName, client.PendingConfigContent!);
        await ClientConfigArchiveStore.UpsertAsync(db, client.Id, client.PendingConfigFileName, client.PendingConfigContent);
        client.PendingConfigFileName = null;
        client.PendingConfigContent  = null;
        await db.SaveChangesAsync();

        return Ok(result);
    }

    /// <summary>
    /// Returns every profile this Client (i.e. this Windows machine, not a specific OS user
    /// account) is known to have — the retained <see cref="Models.ClientConfigArchive"/> rows —
    /// so a client can reconcile an individual OS user's local profile folder against the
    /// machine's authoritative set. See root CLAUDE.md "Cross-OS-user profile sync (Windows)".
    /// </summary>
    [HttpGet("profiles")]
    public async Task<IActionResult> GetProfiles([FromQuery] string clientKey)
    {
        if (string.IsNullOrWhiteSpace(clientKey))
            return BadRequest("clientKey is required.");

        var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientKey == clientKey && !c.IsDeleted);
        if (client is null)
            return NotFound();

        if (await ClientUpdateHalt.AppliesAsync(db, client))
            return ClientUpdateHalt.Paused(Response);

        var entries = await db.ClientConfigArchives
            .Where(a => a.ClientId == client.Id)
            .Select(a => new PendingForeignConfigEntry(a.ProfileName, a.Content))
            .ToArrayAsync();

        return Ok(entries);
    }

    /// <summary>
    /// Archives a manually-uploaded config's content so it becomes part of this Client's
    /// authoritative profile set and can be pulled by <see cref="GetProfiles"/> onto every other
    /// OS user account on the same machine — used only when the uploading user explicitly opts
    /// in to "available to all users on this PC" (the default upload stays local-only and never
    /// calls this). See root CLAUDE.md "Cross-OS-user profile sync (Windows)".
    /// </summary>
    [HttpPost("profiles/archive")]
    public async Task<IActionResult> ArchiveProfile([FromBody] ArchiveProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientKey))
            return BadRequest("clientKey is required.");
        if (!Valenius.Shared.ProfileNameHelper.IsValid(request.ProfileName))
            return BadRequest("Invalid profile name.");

        var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientKey == request.ClientKey && !c.IsDeleted);
        if (client is null)
            return NotFound();

        await ClientConfigArchiveStore.UpsertAsync(db, client.Id, request.ProfileName, request.Content);
        await db.SaveChangesAsync();

        return Ok();
    }
}

public record RegisterRequest(string ClientKey, string Hostname, string HostSid, string Username, string UserSid, string? Version, bool TrayRunning = false, string[]? Profiles = null, string? Platform = null, string? DeviceId = null) : IDeviceRequest;
public record LogEventRequest(string ClientKey, string EventType, string Username, string TunnelName, string LanIp = "", string WanIp = "",
    /// <summary>Free-text detail for non-connection events (e.g. "ConfigRepairFailed"). Null/unused for Connect/Disconnect.</summary>
    string? Detail = null) : IDeviceRequest;
public record MfaEnrollConfirmRequest(string ClientKey, string Code) : IDeviceRequest;
public record RedeemPairingRequest(string Token, string ClientKey, string? Hostname = null, string? Platform = null, string? DeviceId = null);
public record RegisterDeviceRequest(string ClientKey, string? FcmToken) : IDeviceRequest;
public record MfaApproveRequest(string ClientKey, int ChallengeId, int MatchedNumber) : IDeviceRequest;
public record MfaDenyRequest(string ClientKey, int ChallengeId) : IDeviceRequest;
public record TrustedNetworkDto(string Subnet, string? PrimaryDns);
public record StatusResponse(
    bool IsActive,
    bool HasPendingConfig = false,
    bool AutoConnectEnabled = false,
    string? AutoConnectProfileName = null,
    TrustedNetworkDto[]? TrustedNetworks = null,
    string? PendingDeleteProfileName = null,
    bool PendingConnect = false,
    int HeartbeatIntervalMinutes = 1,
    string? ServerProfileName = null,
    /// <summary>Full URL for pre-connect and post-connect health probes.
    /// e.g. https://vpn.company.com:8443/health</summary>
    string? ServerHealthUrl = null,
    /// <summary>Sidecar WireGuard interface IP (e.g. 10.100.0.1) for the
    /// post-connect tunnel probe.  Only sent over the HTTPS backend channel.</summary>
    string? ServerVpnIp = null,
    /// <summary>When true the client immediately runs the update check instead of waiting for its hourly cycle.</summary>
    bool ForceUpdate = false,
    /// <summary>When true the client skips the Phase 2 tunnel connectivity probe and treats the tunnel as connected immediately.</summary>
    bool SkipConnectivityCheck = false,
    /// <summary>When true the client disconnects its active VPN tunnel. Cleared atomically after being sent.</summary>
    bool PendingDisconnect = false,
    // ── MFA session gating (Pro) ──────────────────────────────────────────────
    /// <summary>True when this peer is MFA-gated and has no active session — the tray must prompt for re-auth.</summary>
    bool MfaRequired = false,
    /// <summary>Deep link the tray opens in the system browser to authorize, e.g. https://backend/mfa/authorize?peer=...</summary>
    string? MfaAuthorizeUrl = null,
    /// <summary>Absolute expiry of the active MFA session, for the tray countdown. Null when none.</summary>
    DateTime? MfaSessionExpiresAt = null,
    /// <summary>True when the TOTP enrollment window is open — the tray shows the QR.</summary>
    bool MfaEnrollmentOpen = false,
    /// <summary>otpauth:// URI for the enrollment QR, sent only while the window is open.</summary>
    string? MfaEnrollmentUri = null,
    /// <summary>True when this registration collides with an existing client of the same hostname
    /// and is awaiting an admin reassign/keep-both decision. The client stays inactive and shows a
    /// "already exists — ask your admin to reassign" message instead of "could not reach server".</summary>
    bool DuplicatePending = false,
    /// <summary>
    /// Configs staged from foreign (cross-customer) sidecar provisioning, delivered inline.
    /// Each entry is installed as an additional WireGuard profile alongside the primary.
    /// Null/empty when there are no pending foreign configs.
    /// </summary>
    PendingForeignConfigEntry[]? PendingForeignConfigs = null,
    /// <summary>
    /// Per-profile gateway-probe targets for sidecar VPNs with a globally-unique transit network.
    /// The client runs the HTTP /health gateway check for a profile listed here (falling back to
    /// the handshake check on failure); profiles not listed (plain uploads, OSS) use handshake only.
    /// </summary>
    GatewayProfileEntry[]? GatewayProfiles = null,
    /// <summary>For a gated requester with a push-approver: the number to display
    /// ("approve N on your phone"). Null when there's no active push-approve challenge.</summary>
    int? MfaApproveNumber = null,
    /// <summary>For an approver phone: pending approvals it must act on (number-match).</summary>
    MfaApprovalEntry[]? PendingApprovals = null,
    /// <summary>When true the client should collect its (Valenius-only, redacted) logs and upload
    /// them via POST /api/clients/logs. One-shot — cleared after being sent in the heartbeat.</summary>
    bool LogUploadRequested = false,
    /// <summary>Auto-update channel this client tracks: "stable" (default) or "beta". The updater
    /// polls the matching release stream (…?channel=&lt;this&gt;).</summary>
    string UpdateChannel = "stable",
    /// <summary>Individual credential for a new pending identity, or a stable migration
    /// offer when an eligible legacy device advertises the upgrade protocol. Clients
    /// persist before first use; normal status/poll reads never reissue it.</summary>
    string? ClientApiKey = null,
    /// <summary>
    /// Per-profile physical LAN CIDR(s) reachable through that profile's sidecar (e.g.
    /// "192.168.1.0/24"), self-reported by the sidecar. Entries are scoped to the profile whose
    /// sidecar actually reports them — the native server profile (this client's own customer) and
    /// each foreign profile (its OWN source customer, not the native one) each get their own entry.
    /// Used by the client's pre-connect LAN-conflict check to detect the remote network even when
    /// a profile's AllowedIPs is a full-tunnel default route (0.0.0.0/0), where the routed network
    /// can't otherwise be inferred from the config alone. A profile absent from this list (External
    /// customers, or a sidecar not yet upgraded) simply skips that half of the check for its own
    /// connect — mirrors the per-profile shape of <see cref="GatewayProfiles"/>. Never apply one
    /// entry's CIDRs to a different profile: a client with both a native and a foreign sidecar
    /// profile would misattribute one customer's conflicting LAN to the other's unrelated profile.
    /// </summary>
    LanCidrProfileEntry[]? RemoteLanCidrsByProfile = null,
    /// <summary>When true, the client immediately runs its local repair checks instead of waiting
    /// for its next service restart — currently the data-directory ACL self-heal (fixes "access to
    /// path ... is denied" errors from a corrupted/empty ACL under its ProgramData tree). A generic
    /// hook other client-side repairs can join later. Windows-only; a no-op on other platforms.
    /// One-shot, cleared atomically after being sent.</summary>
    bool PendingConfigRepair = false,
    /// <summary>Windows only: when true, the client connects this customer's own native sidecar
    /// profile automatically at boot, before any user logs on, using the same away-from-trusted-network
    /// policy as the generic auto-connect feature. Effective value already resolved server-side
    /// (per-client override, else the customer default) and already gated on a native profile
    /// actually existing (ServerProfileName != null) — see BuildStatusResponseAsync.</summary>
    bool DeviceTunnelEnabled = false,
    /// <summary>
    /// Per-profile alternate UDP port (typically 443) the client may retry a profile's
    /// <c>Endpoint</c> on when the primary port appears blocked — see
    /// <c>docs/design/port-443-fallback.md</c>. Scoped per profile like
    /// <see cref="GatewayProfiles"/>: the native profile takes its own customer's setting, each
    /// foreign profile its OWN source customer's. A profile absent from this list simply never
    /// falls back. Only listed once the serving side is confirmed (sidecar reports the redirect
    /// installed; External customers are taken at the admin's word), so a client never wastes a
    /// connect attempt on a port nothing answers.
    /// </summary>
    FallbackEndpointEntry[]? FallbackEndpoints = null,
    /// <summary>When true, the client restarts its own background service (Windows: the
    /// <c>Valenius</c> service). Admin-triggered recovery hatch for stale in-memory client state —
    /// e.g. a tunnel removed behind the service's back, leaving it reporting Connected with no
    /// tunnel present, which the user can neither disconnect nor reconnect. Drops any active
    /// tunnel. One-shot, cleared atomically after being sent. Windows-only today; other platforms
    /// ignore the field.</summary>
    bool ServiceRestartRequested = false);
public record MfaApprovalEntry(int ChallengeId, string RequesterName, string? RequesterIp, int[] Choices, DateTime ExpiresAt);
public record PendingConfigResponse(string FileName, string Content);
public record PendingForeignConfigEntry(string FileName, string Content);
public record GatewayProfileEntry(string ProfileName, string GatewayIp, int HealthPort);
public record FallbackEndpointEntry(string ProfileName, int Port, int TriggerSeconds);
public record LanCidrProfileEntry(string ProfileName, string[] Cidrs);
public record ArchiveProfileRequest(string ClientKey, string ProfileName, string Content) : IDeviceRequest;
