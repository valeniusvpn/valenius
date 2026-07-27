using Valenius.Backend.Data;
using Valenius.Backend.Models;

namespace Valenius.Backend.Services;

/// <summary>OSS no-op: peer provisioning requires the Pro sidecar. Pro replaces this via DI.</summary>
public sealed class NullPeerProvisioningService : IPeerProvisioningService
{
    public Task<string> ProvisionAsync(Client client, Customer customer)
        => Task.FromResult("Skipped: peer provisioning requires the Pro edition.");

    public Task<string> DeprovisionAsync(Client client, Customer customer)
        => Task.FromResult("Skipped: peer provisioning requires the Pro edition.");

    public Task<string> ProvisionForeignAsync(Client client, Customer sourceCustomer)
        => Task.FromResult("Skipped: peer provisioning requires the Pro edition.");

    public Task<string> DeprovisionForeignAsync(Client client, Customer sourceCustomer)
        => Task.FromResult("Skipped: peer provisioning requires the Pro edition.");

    public Task<string> ResyncAsync(Customer customer, ApplicationDbContext db)
        => Task.FromResult("Skipped: peer provisioning requires the Pro edition.");

    public Task ReconcileStaleProfilesAsync(Client client) => Task.CompletedTask;
}
