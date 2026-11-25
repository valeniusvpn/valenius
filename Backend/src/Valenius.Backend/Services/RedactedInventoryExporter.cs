using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;

namespace Valenius.Backend.Services;

/// <summary>
/// A default-deny plaintext export. Project approved scalars into dedicated DTOs;
/// never serialize entities, navigation graphs, configuration text or arbitrary
/// database strings. Recovery credentials belong only in encrypted snapshots.
/// </summary>
public static class RedactedInventoryExporter
{
    public static async Task<RedactedInventory> ExportAsync(ApplicationDbContext db, bool includeConnectionLogs)
        => new(
            "Valenius.RedactedInventory", 1, DateTime.UtcNow, includeConnectionLogs,
            await db.ServerSettings.AsNoTracking().Select(s => new InventorySettings(
                s.OidcEnabled, s.LocalAuthEnabled, s.SmtpEnabled, s.SmtpPort, s.SmtpUseSsl,
                s.TrafficRetentionDays, s.DefaultHeartbeatIntervalMinutes, s.ApiExposeClientKey)).SingleAsync(),
            await db.Customers.AsNoTracking().OrderBy(c => c.Id).Select(c => new InventoryCustomer(
                c.Id, c.HeartbeatIntervalMinutes,
                c.ServerMode == "Valenius" ? "Valenius" : c.ServerMode == "External" ? "External" : "Other",
                c.AllowExternalConfig, c.MaxEndpoints, c.DefaultMfaPolicy, c.MfaSessionLifetimeHours,
                c.DeviceTunnelDefaultEnabled, c.AllowShareSidecar, c.AllowReceiveForeignProfiles)).ToListAsync(),
            await db.Clients.AsNoTracking().OrderBy(c => c.Id).Select(c => new InventoryClient(
                c.Id, c.CustomerId, c.RegisteredAt, c.LastSeenAt, c.IsActive, c.IsDeleted,
                c.IsDuplicatePending, c.AutoConnectEnabled, c.SkipConnectivityCheck, c.WgPeerKilled,
                c.PeerType, c.MfaPolicy, c.MfaSessionLifetimeHours, c.DeviceTunnelEnabled,
                c.MfaTotpEnabled, c.MfaEnrollmentOpen, c.MfaApproverClientId,
                c.PendingConfigContent != null)).ToListAsync(),
            await db.AppUsers.AsNoTracking().OrderBy(u => u.Id).Select(u => new InventoryUser(
                u.Id, u.CustomerId, u.Role == "Admin" ? "Admin" : u.Role == "User" ? "User" : "Other",
                u.Subject.StartsWith("local:"), u.PasswordHash != null, u.TotpEnabled)).ToListAsync(),
            await db.TrustedNetworks.AsNoTracking().OrderBy(n => n.Id)
                .Select(n => new InventoryTrustedNetwork(n.Id, n.CustomerId)).ToListAsync(),
            includeConnectionLogs
                ? await db.ConnectionLogs.AsNoTracking().OrderBy(l => l.Id).Select(l => new InventoryConnectionLog(
                    l.Id, l.ClientId, l.OccurredAt,
                    l.EventType == "Connect" ? "Connect" : l.EventType == "Disconnect" ? "Disconnect" : "Other")).ToListAsync()
                : []);
}

public sealed record RedactedInventory(
    string ExportKind, int ExportVersion, DateTime CreatedAt, bool IncludesConnectionLogs,
    InventorySettings Settings, List<InventoryCustomer> Customers, List<InventoryClient> Clients,
    List<InventoryUser> Users, List<InventoryTrustedNetwork> TrustedNetworks,
    List<InventoryConnectionLog> ConnectionLogs);

public sealed record InventorySettings(
    bool OidcEnabled, bool LocalAuthEnabled, bool SmtpEnabled, int SmtpPort, bool SmtpUseSsl,
    int TrafficRetentionDays, int DefaultHeartbeatIntervalMinutes, bool ApiExposeClientKey);

public sealed record InventoryCustomer(
    int Id, int? HeartbeatIntervalMinutes, string ServerMode, bool AllowExternalConfig,
    int MaxEndpoints, MfaPolicy DefaultMfaPolicy, int MfaSessionLifetimeHours,
    bool DeviceTunnelDefaultEnabled, bool AllowShareSidecar, bool AllowReceiveForeignProfiles);

public sealed record InventoryClient(
    int Id, int? CustomerId, DateTime RegisteredAt, DateTime LastSeenAt, bool IsActive,
    bool IsDeleted, bool IsDuplicatePending, bool AutoConnectEnabled, bool SkipConnectivityCheck,
    bool WgPeerKilled, PeerType PeerType, MfaPolicy? MfaPolicy, int? MfaSessionLifetimeHours,
    bool? DeviceTunnelEnabled, bool MfaTotpEnabled, bool MfaEnrollmentOpen, int? MfaApproverClientId,
    bool HasPendingConfig);

public sealed record InventoryUser(
    int Id, int? CustomerId, string Role, bool IsLocalUser, bool HasPassword, bool TotpEnabled);

public sealed record InventoryTrustedNetwork(int Id, int CustomerId);

public sealed record InventoryConnectionLog(int Id, int ClientId, DateTime OccurredAt, string EventType);
