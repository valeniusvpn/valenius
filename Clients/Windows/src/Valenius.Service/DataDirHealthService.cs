namespace Valenius.Service;

/// <summary>
/// Runs the data-directory ACL self-heal once at service startup, before any other hosted
/// service (registered first in Program.cs, so its StartAsync completes first). This ensures
/// the SYSTEM service can read its own state (registration.json, configs) even if a bad
/// installer ACL run left the tree unreadable — see <see cref="DataDirSelfHeal"/>.
/// </summary>
internal sealed class DataDirHealthService(ILogger<DataDirHealthService> logger) : IHostedService
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Valenius");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Run OFF the startup path: file enumeration + ACL rewrites are synchronous and can be
        // slow, and blocking here delays (or, past the SCM timeout, fails) the whole service
        // start. Fire-and-forget on a background thread; the heartbeat loop tolerates a data-dir
        // that is briefly still-broken (it retries), and GetOrCreateClientIdAsync refuses to mint
        // a new identity while registration.json is unreadable, so nothing is lost in the gap.
        _ = Task.Run(() =>
        {
            try
            {
                DataDirSelfHeal.EnsureAccessible(DataDir, logger);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Data-directory self-heal threw unexpectedly.");
            }
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
