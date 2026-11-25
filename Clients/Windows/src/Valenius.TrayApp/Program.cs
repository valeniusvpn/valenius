namespace Valenius.TrayApp;

static class Program
{
    // Local\ (not Global\) so each Terminal Services session gets its own singleton --
    // Fast User Switching leaves the previous user's session alive (merely disconnected),
    // and a Global\ mutex would still be held, silently blocking the next user's tray.
    private const string MutexName = "Local\\Valenius.TrayApp";

    [STAThread]
    static int Main(string[] args)
    {
        // Administrator helper must run before the ordinary tray's per-session mutex.
        if (args.Length > 0 && args[0].StartsWith("--provision", StringComparison.Ordinal))
        {
            ApplicationConfiguration.Initialize();
            return ProvisioningCommand.RunAsync(args).GetAwaiter().GetResult();
        }
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
            return 0;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
        return 0;
    }
}
