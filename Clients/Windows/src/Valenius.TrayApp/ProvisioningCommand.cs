using System.Security.Principal;
using Valenius.Shared;

namespace Valenius.TrayApp;

internal static class ProvisioningCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return Report("Run the provisioning command from an elevated administrator terminal.", false);
        PipeCommand command;
        if (args is ["--provision-backend", var address])
            command = new() { Command = CommandType.SetBackendUrl, BackendDns = address };
        else if (args is ["--provision-standalone"])
            command = new() { Command = CommandType.SetStandaloneMode };
        else
            return Report("Usage: Valenius.TrayApp.exe --provision-backend <server> or --provision-standalone", false);
        try
        {
            var response = await new PipeClient().SendAsync(command).ConfigureAwait(false);
            return Report(response.Error ?? (response.Success ? "Provisioning saved." : "Provisioning refused."), response.Success);
        }
        catch (Exception error) { return Report(error.Message, false); }
    }

    private static int Report(string message, bool success)
    {
        MessageBox.Show(message, "Valenius provisioning", MessageBoxButtons.OK,
            success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        return success ? 0 : 1;
    }
}
