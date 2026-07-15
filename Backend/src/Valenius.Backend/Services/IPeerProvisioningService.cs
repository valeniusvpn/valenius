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
}
