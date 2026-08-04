namespace Valenius.Backend.Services;

/// <summary>The fixed Management API scope catalog (docs/design/management-api.md §5.1).
/// Deliberately coarse — read/write per resource family, with `*:write` never implying
/// `*:read`. Lives in OSS (used by the Pro admin UI to render the create-token form)
/// even though no scoped endpoint exists yet — the catalog itself is part of the Phase 1
/// foundation; individual scopes go from "defined" to "enforced" as their endpoints land
/// in later phases.</summary>
public static class ManagementApiScopeCatalog
{
    public sealed record ScopeInfo(string Scope, string Label, string Description, bool Dangerous = false);

    public sealed record ScopeGroup(string Resource, IReadOnlyList<ScopeInfo> Scopes);

    public static readonly IReadOnlyList<ScopeGroup> Groups =
    [
        new("Clients", [
            new("clients:read", "Read", "List/get clients, profiles, status, connection history, alerts"),
            new("clients:write", "Write", "Edit client settings, activate/deactivate, assign customer, delete"),
            new("clients:actions", "Actions", "Connect, disconnect, kill tunnel, force update, request logs, repair, (de)provision"),
        ]),
        new("Configs", [
            new("configs:read", "Read", "Download a client .conf / QR — contains the WireGuard private key", Dangerous: true),
            new("configs:write", "Write", "Upload an external .conf, resend an archived profile, delete a profile"),
        ]),
        new("Customers", [
            new("customers:read", "Read", "List/get customers, trusted networks, sharing + sidecar settings"),
            new("customers:write", "Write", "Create/edit/delete customers, networks, endpoints, sharing flags"),
        ]),
        new("MFA", [
            new("mfa:read", "Read", "MFA policy + active session state"),
            new("mfa:write", "Write", "Set policy, reset enrollment, revoke sessions"),
        ]),
        new("Traffic", [
            new("traffic:read", "Read", "Traffic totals + time series"),
        ]),
        new("Audit", [
            new("audit:read", "Read", "Audit log, error log, client log bundles"),
        ]),
        new("Pairing", [
            new("pairing:write", "Write", "Mint QR pairing tokens"),
        ]),
        new("Events", [
            new("events:read", "Read", "Poll fleet events: client connect/disconnect/alert/provisioned"),
        ]),
        new("Monitoring", [
            new("monitoring:read", "Read", "Prometheus /api/v1/metrics + /api/v1/monitoring/status — control-plane, license, and fleet-aggregate metrics for an NMS (Zabbix, Grafana, etc.)"),
        ]),
    ];

    public static readonly IReadOnlySet<string> AllScopes =
        Groups.SelectMany(g => g.Scopes).Select(s => s.Scope).ToHashSet(StringComparer.Ordinal);
}
