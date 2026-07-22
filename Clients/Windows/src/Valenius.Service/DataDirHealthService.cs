namespace Valenius.Service;

/// <summary>
/// Runs the data-directory ACL self-heal once at service startup, before any other hosted
/// service (registered first in Program.cs, so its StartAsync completes first). This ensures
/// the SYSTEM service can read its own state (registration.json, configs) even if a bad
/// installer ACL run left the tree unreadable — see <see cref="DataDirSelfHeal"/>. When SYSTEM
/// itself can't repair the tree (e.g. ownership was moved away from SYSTEM), that's reported to
/// the backend so it surfaces on Admin/Alerts — an unattended machine with a broken data dir would
/// otherwise just go quietly offline with no signal pointing at *why*.
/// </summary>
internal sealed class DataDirHealthService(
    ILogger<DataDirHealthService> logger,
    ClientRegistrationService registrationService) : IHostedService
{
    /// <summary>Exposed so other components (e.g. an admin-triggered on-demand repair) can reuse
    /// the same path without recomputing it.</summary>
    internal static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Valenius");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Run OFF the startup path: file enumeration + ACL rewrites are synchronous and can be
        // slow, and blocking here delays (or, past the SCM timeout, fails) the whole service
        // start. Fire-and-forget on a background thread; the heartbeat loop tolerates a data-dir
        // that is briefly still-broken (it retries), and GetOrCreateClientIdAsync refuses to mint
        // a new identity while registration.json is unreadable, so nothing is lost in the gap.
        _ = Task.Run(async () =>
        {
            try
            {
                // allowOwnershipTakeover defaults to false here — the automatic startup path stays
                // non-invasive; forcibly reclaiming ownership only happens via the admin-triggered
                // on-demand "Repair client config" action (ClientRegistrationService).
                if (DataDirSelfHeal.EnsureAccessible(DataDir, logger) == DataDirSelfHeal.Result.Failed)
                    await registrationService.ReportConfigRepairFailedAsync(
                        DataDirSelfHeal.FailureDetail ?? "ACL self-heal failed.", cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Data-directory self-heal threw unexpectedly.");
            }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
