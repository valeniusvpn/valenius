using Valenius.Backend.Data;
using Valenius.Backend.Models;

namespace Valenius.Backend.Services;

public interface IPeerProvisioningService
{
    Task<string> ProvisionAsync(Client client, Customer customer);
    Task<string> DeprovisionAsync(Client client, Customer customer);

    /// <summary>
    /// Provisions a WireGuard peer for <paramref name="client"/> on
    /// <paramref name="sourceCustomer"/>'s sidecar (cross-customer profile).
    /// Both the source customer (<c>AllowShareSidecar</c>) and the client's own
    /// customer (<c>AllowReceiveForeignProfiles</c>) must permit sharing.
    /// </summary>
    Task<string> ProvisionForeignAsync(Client client, Customer sourceCustomer);

    /// <summary>
    /// Removes the cross-customer peer for <paramref name="client"/> from
    /// <paramref name="sourceCustomer"/>'s sidecar and deletes the
    /// <c>ClientForeignProfile</c> row.
    /// </summary>
    Task<string> DeprovisionForeignAsync(Client client, Customer sourceCustomer);

    /// <summary>
    /// Removes all peers from the sidecar and re-registers every active provisioned
    /// client from the database.  Also re-stages client configs so any server-side
    /// changes (new public key, port) are pushed down on the next heartbeat.
    /// Use after a sidecar reinstall or whenever the server peer list drifts.
    /// </summary>
    Task<string> ResyncAsync(Customer customer, ApplicationDbContext db);

    /// <summary>
    /// Sweeps every profile name ever delivered to <paramref name="client"/>
    /// (<c>ClientConfigArchive</c>) against its current authoritative set — its native
    /// profile, if still provisioned, plus its current foreign profiles — and queues an
    /// on-device delete for anything archived that no longer belongs. Recovers a profile
    /// orphaned by a deletion request that got clobbered by another one racing it (see
    /// <c>ClientPendingDelete</c>). Called from the "Re-provision" actions so a stale
    /// profile gets swept the next time an admin re-syncs this client instead of staying
    /// stuck forever.
    /// </summary>
    Task ReconcileStaleProfilesAsync(Client client);
}
