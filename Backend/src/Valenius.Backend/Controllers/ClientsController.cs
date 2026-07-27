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
[ServiceFilter(typeof(ApiKeyFilter))]
public class ClientsController(ApplicationDbContext db, ClientNotifier notifier, ClientPresenceTracker presence, WgTunnelTracker wgTunnels, ILicenseEnforcement enforcement, IMfaSessionService mfa, IMfaChallengeService challenge, ISidecarService sidecar, ApiKeyStore apiKeyStore) : ControllerBase
{
    /// <summary>LogEvent EventTypes that are client-side repair outcomes routed to ClientAlerts
    /// (Admin/Alerts) instead of ConnectionLogs. Kept as a set so LogEvent's dispatch and dedupe
    /// stay generic as new kinds are added.</summary>
    private static readonly HashSet<string> ClientAlertEventTypes =
        new(StringComparer.OrdinalIgnoreCase) { "ConfigRepairFailed", "OwnershipReclaimed" };

    // Keyless-friendly: a first-run device with no installation key yet may register (it becomes a
    // PENDING client). It receives the installation key only once an admin activates it — see the
    // keyToAdopt logic in BuildStatusResponseAsync. Auth for the keyed fleet is unchanged.
    [ApiKeyExempt]
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientKey))
            return BadRequest("ClientKey is required.");

        var now    = DateTime.UtcNow;
        var client = await db.Clients
            .Include(c => c.Customer).ThenInclude(cu => cu!.TrustedNetworks)
            .FirstOrDefaultAsync(c => c.ClientKey == request.ClientKey);

        // One-time migration: claim the most-recently-seen non-deleted record for this
        // hostname that predates the ClientKey scheme (ClientKey == "").
        if (client is null && !string.IsNullOrEmpty(request.Hostname))
        {
            var legacy = await db.Clients
                .Include(c => c.Customer).ThenInclude(cu => cu!.TrustedNetworks)
                .Where(c => c.ClientKey == "" && !c.IsDeleted && c.Hostname == request.Hostname)
                .OrderByDescending(c => c.LastSeenAt)
                .FirstOrDefaultAsync();
            if (legacy is not null)
            {
                legacy.ClientKey = request.ClientKey;
                client = legacy;
            }
        }

        if (client is null)
        {
            // Same-hostname collision: a non-deleted client with this hostname already exists
            // under a different ClientKey (typically a reinstall with a fresh key). Don't
            // silently create a parallel active client and don't fail with a misleading error —
            // record the new install as a pending duplicate so an admin can reassign the
            // original record to it, or keep both. (Excludes other pending-duplicate rows so
            // repeated heartbeats from this same new install don't chain.)
            var nameCollision = !string.IsNullOrEmpty(request.Hostname)
                && await db.Clients.AnyAsync(c =>
                    !c.IsDeleted
                    && !c.IsDuplicatePending
                    && c.ClientKey != ""
                    && c.ClientKey != request.ClientKey
                    && c.Hostname == request.Hostname);

            client = new Client
            {
                ClientKey          = request.ClientKey,
                Hostname           = request.Hostname,
                HostSid            = request.HostSid,
                Username           = request.Username,
                UserSid            = request.UserSid,
                ClientVersion      = request.Version,
                RegisteredAt       = now,
                LastSeenAt         = now,
                IsActive           = false,
                IsDuplicatePending = nameCollision,
                CustomerId         = await MatchCustomerAsync(request.Hostname),
                KnownProfiles      = SerializeProfiles(request.Profiles),
                Platform           = request.Platform
            };
            db.Clients.Add(client);
        }
        else if (client.IsDeleted)
        {
            // Machine re-registered after being deleted — restore as pending.
            client.IsDeleted   = false;
            client.IsActive    = false;
            client.Hostname    = request.Hostname;
            client.HostSid     = request.HostSid;
            client.CustomerId  = await MatchCustomerAsync(request.Hostname);
            if (!string.IsNullOrEmpty(request.Username)) client.Username = request.Username;
            if (!string.IsNullOrEmpty(request.UserSid))  client.UserSid  = request.UserSid;
            if (!string.IsNullOrEmpty(request.Version))  client.ClientVersion = request.Version;
            if (!string.IsNullOrEmpty(request.Platform)) client.Platform = request.Platform;
            client.KnownProfiles = SerializeProfiles(request.Profiles);
            client.LastSeenAt = now;
        }
        else
        {
            client.Hostname = request.Hostname;
            client.HostSid  = request.HostSid;
            if (!string.IsNullOrEmpty(request.Username)) client.Username = request.Username;
            if (!string.IsNullOrEmpty(request.UserSid))  client.UserSid  = request.UserSid;
            if (!string.IsNullOrEmpty(request.Version))  client.ClientVersion = request.Version;
            if (!string.IsNullOrEmpty(request.Platform)) client.Platform = request.Platform;
            client.KnownProfiles = SerializeProfiles(request.Profiles);
            client.LastSeenAt = now;
        }

        // Drain confirmed deletes from the queue: any queued name the client's own reported
        // profile list no longer contains has landed, so stop re-sending it. Every confirmed
        // entry clears in one pass (not just the oldest) — see ClientPendingDelete.
        if (request.Profiles is not null)
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
            // Two concurrent first-registrations with the same ClientKey raced — the other
            // request already inserted the row. Clear the stale tracked entity, re-read the
            // winner's row, and return its current state.
            db.ChangeTracker.Clear();
            var existing = await db.Clients
                .Include(c => c.Customer).ThenInclude(cu => cu!.TrustedNetworks)
                .FirstOrDefaultAsync(c => c.ClientKey == request.ClientKey);
            if (existing is null)
                return Problem("Registration conflict could not be resolved.", statusCode: 500);
            var conflictResponse = await BuildStatusResponseAsync(existing);
            await db.SaveChangesAsync();
            return Ok(conflictResponse);
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
        return Ok(heartbeatResponse);
    }

    /// <summary>
    /// Builds the status response AND clears one-shot flags (PendingConnect) on the entity
    /// so the caller must SaveChanges after calling this.
    /// </summary>
    private async Task<StatusResponse> BuildStatusResponseAsync(Client client)
    {
        var networks = client.Customer?.TrustedNetworks
            .Select(n => new TrustedNetworkDto(n.Subnet, n.PrimaryDns))
            .ToArray() ?? [];

        // Capture one-shot flags and clear them — each fires exactly once per trigger.
        var pendingConnect    = client.PendingConnect;
        client.PendingConnect = false;

        var pendingDisconnect    = client.PendingDisconnect;
        client.PendingDisconnect = false;

        var forceUpdate    = client.ForceUpdate;
        client.ForceUpdate = false;

        var pendingConfigRepair    = client.PendingConfigRepair;
        client.PendingConfigRepair = false;

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
            : 5;

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
            .Where(p => p.ClientId == client.Id && p.PendingConfigFileName != null)
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
            .Where(a => a.ClientId == client.Id && a.PendingResend)
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
                p.SourceCustomer.SidecarLanCidrs
            })
            .ToListAsync();
        foreach (var fr in foreignGatewayRows)
        {
            if (string.IsNullOrEmpty(fr.SidecarVpnIp) || string.IsNullOrEmpty(fr.ProfileName)) continue;
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
        var logUploadRequested = client.PendingLogRequest;
        if (logUploadRequested) client.PendingLogRequest = false;

        // Hand the client the installation's current API key when it presented a *different* key, in
        // two cases:
        //   1. Rotation roll-forward — it presented a still-valid previous key (grace window), so the
        //      fleet moves to the new key without a hard cutover.
        //   2. First-run keyless bootstrap — it presented no/blank/invalid key BUT the client is
        //      active, i.e. an admin has activated this device (register is [ApiKeyExempt]). A
        //      pending (unactivated) keyless device gets nothing, so an unknown caller can never
        //      pull the key just by guessing a ClientKey.
        // Null in the normal case (already on the current key).
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

        var presentedKey       = Request.Headers.TryGetValue("X-Api-Key", out var pk) ? pk.ToString() : null;
        var presentedIsGraceKey = apiKeyStore.IsAccepted(presentedKey);   // current or valid previous
        var rotatedApiKey = (!string.IsNullOrEmpty(apiKeyStore.Key)
                             && !string.Equals(presentedKey, apiKeyStore.Key, StringComparison.Ordinal)
                             && (presentedIsGraceKey || client.IsActive))
            ? apiKeyStore.Key
            : null;

        return new StatusResponse(
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
            rotatedApiKey,
            remoteLanCidrsByProfile,
            pendingConfigRepair,
            deviceTunnelEnabled);
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
            }
            return Ok();
        }

        // Not a state transition either — the gateway /health probe succeeded for an
        // already-connected tunnel (see WgTunnelTracker.IsDisplayConnected). Not logged to
        // ConnectionLogs; it isn't a connect/disconnect, just a confirmation of one already
        // recorded.
        if (string.Equals(request.EventType, "Verified", StringComparison.OrdinalIgnoreCase))
        {
            wgTunnels.MarkVerified(client.ClientKey);
            return Ok();
        }

        if (string.Equals(request.EventType, "Connect", StringComparison.OrdinalIgnoreCase)
            && client.WgPublicKey is not null)
        {
            // A gateway-verified connect (Pro sidecar, globally-unique transit network) is
            // tracked separately from a plain handshake-only one, so the admin list's "connected"
            // star can require actual verification where that's a meaningful signal, while
            // clients with no gateway target keep today's simpler "peer registered" behavior —
            // a handshake alone isn't reliable enough to gate a display indicator on (WireGuard's
            // own rekey timers mean a genuinely-connected peer can go ~180s without a fresh one).
            var expectsGatewayVerification = false;
            if (client.Customer?.ServerMode == "Valenius" && !string.IsNullOrEmpty(client.Customer.SidecarVpnIp))
            {
                var otherSubnets = await db.Customers
                    .Where(c => c.Id != client.Customer.Id && c.SidecarVpnSubnet != null && c.SidecarVpnSubnet != "")
                    .Select(c => c.SidecarVpnSubnet)
                    .ToListAsync();
                expectsGatewayVerification = WireGuardHelper.IsTransitUnique(client.Customer.SidecarVpnSubnet, otherSubnets);
            }
            wgTunnels.MarkConnected(client.ClientKey, client.WgPublicKey, expectsGatewayVerification);

            // A prior "Kill Tunnel" removed this peer from the sidecar. Now that the client
            // is actively reconnecting, transparently re-add it — a kick, not a ban.
            //
            // NEVER raw-re-add an MFA-gated peer: a gated peer must be absent from the
            // interface until MfaSessionService pushes a CA-signed grant, and a plain
            // AddPeerAsync here would restore full VPN access with no active session / no
            // grant — and MfaReconcilerService (which reconciles the /grants store, not raw
            // peers) would not undo it. For gated peers the grant pipeline is the only re-add
            // path; the client re-authenticates via the MfaAuthorizeUrl deep link on its next
            // heartbeat. WgPeerKilled is left set and self-clears if the policy later ungates.
            if (client.WgPeerKilled
                && client.Customer?.ServerMode == "Valenius"
                && client.VpnIpAddress is not null
                && !mfa.ResolvePolicy(client, client.Customer).Gated)
            {
                var added = await sidecar.AddPeerAsync(
                    client.Customer.SidecarBaseUrl, client.WgPublicKey, $"{client.VpnIpAddress}/32");
                if (added)
                    client.WgPeerKilled = false;
            }
        }
        else if (string.Equals(request.EventType, "Disconnect", StringComparison.OrdinalIgnoreCase))
        {
            wgTunnels.MarkDisconnected(client.ClientKey);

            // PerConnection MFA: an explicit disconnect revokes the active session immediately
            // so the next connect re-authenticates. (No-op in OSS / for ungated peers.)
            if (mfa.ResolvePolicy(client, client.Customer).Policy == MfaPolicy.PerConnection)
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
        await Task.WhenAny(
            notifier.WaitAsync(clientKey),
            Task.Delay(Timeout.Infinite, linked.Token));

        // Re-read fresh state.  Use CancellationToken.None so PendingConnect is always
        // cleared even if the HTTP client disconnected during the wait.
        var client = await db.Clients
            .Include(c => c.Customer).ThenInclude(cu => cu!.TrustedNetworks)
            .FirstOrDefaultAsync(c => c.ClientKey == clientKey && !c.IsDeleted, CancellationToken.None);
        if (client is null) return NotFound();

        var response = await BuildStatusResponseAsync(client);      // clears PendingConnect on entity
        await db.SaveChangesAsync(CancellationToken.None);
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

    // Bootstrap: authenticated by the one-time pairing token (not the API key), so a keyless first-run
    // device can pair. Returns the installation's API key so the device adopts it for all later calls.
    [ApiKeyExempt]
    [HttpPost("pairing/redeem")]
    public async Task<IActionResult> RedeemPairing([FromBody] RedeemPairingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.ClientKey))
            return BadRequest("Token and ClientKey are required.");

        var now = DateTime.UtcNow;
        var token = await db.PairingTokens.FirstOrDefaultAsync(t => t.Token == request.Token);
        if (token is null || token.UsedAt is not null || token.ExpiresAt < now)
            return BadRequest("Invalid or expired pairing code.");

        var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientKey == request.ClientKey);
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

        // Pairing explicitly assigns the customer (mobile has no hostname prefix to match on).
        client.IsActive           = true;
        client.IsDeleted          = false;
        client.IsDuplicatePending = false;
        client.CustomerId         = token.CustomerId;
        if (!string.IsNullOrEmpty(request.Hostname)) client.Hostname = request.Hostname;
        if (!string.IsNullOrEmpty(request.Platform)) client.Platform = request.Platform;
        client.LastSeenAt = now;
        token.UsedAt      = now;

        await db.SaveChangesAsync();
        // Pairing both activates the device and hands it the installation's API key, so a keyless
        // first-run app never needs a hardcoded shared key. The one-time token was the authorization.
        return Ok(new { activated = true, apiKey = apiKeyStore.Key });
    }

    [HttpGet("pending-config")]
    public async Task<IActionResult> GetPendingConfig([FromQuery] string clientKey)
    {
        if (string.IsNullOrWhiteSpace(clientKey))
            return BadRequest("clientKey is required.");

        var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientKey == clientKey && !c.IsDeleted);
        if (client is null)
            return NotFound();

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

public record RegisterRequest(string ClientKey, string Hostname, string HostSid, string Username, string UserSid, string? Version, bool TrayRunning = false, string[]? Profiles = null, string? Platform = null);
public record LogEventRequest(string ClientKey, string EventType, string Username, string TunnelName, string LanIp = "", string WanIp = "",
    /// <summary>Free-text detail for non-connection events (e.g. "ConfigRepairFailed"). Null/unused for Connect/Disconnect.</summary>
    string? Detail = null);
public record MfaEnrollConfirmRequest(string ClientKey, string Code);
public record RedeemPairingRequest(string Token, string ClientKey, string? Hostname = null, string? Platform = null);
public record RegisterDeviceRequest(string ClientKey, string? FcmToken);
public record MfaApproveRequest(string ClientKey, int ChallengeId, int MatchedNumber);
public record MfaDenyRequest(string ClientKey, int ChallengeId);
public record TrustedNetworkDto(string Subnet, string? PrimaryDns);
public record StatusResponse(
    bool IsActive,
    bool HasPendingConfig = false,
    bool AutoConnectEnabled = false,
    string? AutoConnectProfileName = null,
    TrustedNetworkDto[]? TrustedNetworks = null,
    string? PendingDeleteProfileName = null,
    bool PendingConnect = false,
    int HeartbeatIntervalMinutes = 5,
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
    /// <summary>The current client API key, sent ONLY when the requester authenticated with a
    /// different (previous/rotated) key — the client persists it and uses it for subsequent
    /// requests, so an admin can rotate the fleet's key without touching each client. Null in
    /// the normal case (already on the current key). Delivered over the authenticated TLS
    /// channel.</summary>
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
    bool DeviceTunnelEnabled = false);
public record MfaApprovalEntry(int ChallengeId, string RequesterName, string? RequesterIp, int[] Choices, DateTime ExpiresAt);
public record PendingConfigResponse(string FileName, string Content);
public record PendingForeignConfigEntry(string FileName, string Content);
public record GatewayProfileEntry(string ProfileName, string GatewayIp, int HealthPort);
public record LanCidrProfileEntry(string ProfileName, string[] Cidrs);
public record ArchiveProfileRequest(string ClientKey, string ProfileName, string Content);
