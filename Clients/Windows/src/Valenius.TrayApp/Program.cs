namespace Valenius.TrayApp;

static class Program
{
    // Local\ (not Global\) so each Terminal Services session gets its own singleton --
    // Fast User Switching leaves the previous user's session alive (merely disconnected),
    // and a Global\ mutex would still be held, silently blocking the next user's tray.
    private const string MutexName = "Local\\Valenius.TrayApp";

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
