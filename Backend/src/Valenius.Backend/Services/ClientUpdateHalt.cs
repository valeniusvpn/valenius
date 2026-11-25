using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Controllers;
using Valenius.Backend.Data;
using Valenius.Backend.Models;

namespace Valenius.Backend.Services;

/// <summary>Database-backed so a restart or another replica cannot silently resume delivery.
/// No new client protocol is required: old clients receive their previous policy.</summary>
public static class ClientUpdateHalt
{
    public static Task<bool> IsEnabledAsync(ApplicationDbContext db) =>
        db.ServerSettings.AsNoTracking().AnyAsync(s => s.ClientUpdateHaltEnabled);

    public static async Task<bool> AppliesAsync(ApplicationDbContext db, Client client) =>
        ReleaseChannel.Normalize(client.UpdateChannel) != ReleaseChannel.Beta && await IsEnabledAsync(db);

    public static IActionResult Paused(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.RetryAfter = "60";
        return new ObjectResult("Client update delivery is halted; retain local configuration and retry later.")
            { StatusCode = StatusCodes.Status503ServiceUnavailable };
    }

    // Explicit allowlist: future StatusResponse commands/secrets must never enter the snapshot.
    public static void Remember(Client client, StatusResponse response)
    {
        var policy = new StatusResponse(true,
            AutoConnectEnabled: response.AutoConnectEnabled,
            AutoConnectProfileName: response.AutoConnectProfileName,
            TrustedNetworks: response.TrustedNetworks,
            HeartbeatIntervalMinutes: response.HeartbeatIntervalMinutes,
            ServerProfileName: response.ServerProfileName,
            ServerHealthUrl: response.ServerHealthUrl,
            ServerVpnIp: response.ServerVpnIp,
            SkipConnectivityCheck: response.SkipConnectivityCheck,
            GatewayProfiles: response.GatewayProfiles,
            UpdateChannel: response.UpdateChannel,
            RemoteLanCidrsByProfile: response.RemoteLanCidrsByProfile,
            DeviceTunnelEnabled: response.DeviceTunnelEnabled,
            FallbackEndpoints: response.FallbackEndpoints);
        client.LastDeliveredPolicyJson = JsonSerializer.Serialize(new Snapshot(client.ClientKey, client.CustomerId, policy));
    }

    public static StatusResponse? PreviousPolicy(Client client)
    {
        if (client.LastDeliveredPolicyJson is null) return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<Snapshot>(client.LastDeliveredPolicyJson);
            return snapshot?.ClientKey == client.ClientKey && snapshot.CustomerId == client.CustomerId
                ? snapshot.Policy : null;
        }
        catch (JsonException) { return null; } // No invented defaults for a missing/corrupt baseline.
    }

    private sealed record Snapshot(string ClientKey, int? CustomerId, StatusResponse Policy);
}
