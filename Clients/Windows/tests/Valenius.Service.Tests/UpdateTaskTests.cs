using System.Xml.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Valenius.Service;
using Xunit;

namespace Valenius.Service.Tests;

public sealed class UpdateTaskTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Valenius-battery-task-" + Guid.NewGuid().ToString("N"));
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void Created_task_allows_start_and_continuation_on_battery_as_detached_SYSTEM()
    {
        var commands = new List<string>();
        string? scriptPath = null;
        string? definitionPath = null;
        using var updater = new UpdateChecker(null!, NullLogger<UpdateChecker>.Instance, null!, null!, null!, null!,
            _directory, runProcess: (file, arguments) =>
            {
                Assert.Equal("schtasks.exe", file);
                commands.Add(arguments);
                if (arguments.StartsWith("/Create", StringComparison.Ordinal))
                {
                    definitionPath = Directory.GetFiles(_directory, "*.xml").Single();
                    Assert.Contains("/XML \"" + definitionPath + "\"", arguments);
                    var root = XDocument.Load(definitionPath).Root!;
                    var settings = root.Element(Ns + "Settings")!;
                    Assert.Equal("false", settings.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
                    Assert.Equal("false", settings.Element(Ns + "StopIfGoingOnBatteries")!.Value);
                    Assert.Equal("true", settings.Element(Ns + "AllowStartOnDemand")!.Value);
                    Assert.Equal("true", settings.Element(Ns + "Enabled")!.Value);
                    var principal = root.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
                    Assert.Equal("S-1-5-18", principal.Element(Ns + "UserId")!.Value);
                    Assert.Null(principal.Element(Ns + "LogonType")); // SYSTEM service logon is inferred from its SID.
                    Assert.Equal("HighestAvailable", principal.Element(Ns + "RunLevel")!.Value);
                    scriptPath = root.Element(Ns + "Actions")!.Element(Ns + "Exec")!.Element(Ns + "Command")!.Value;
                    var script = File.ReadAllText(scriptPath);
                    Assert.Contains("/SILENT /NORESTART /LOG=", script);
                    Assert.Contains("schtasks /Delete /TN ValeniusAutoUpdate /F", script);
                }
            });
        updater.LaunchInstallerDetached(Path.Combine(_directory, "ValeniusSetup-1.18.3.exe"));
        Assert.Equal(2, commands.Count);
        Assert.Equal("/Run /TN ValeniusAutoUpdate", commands[1]);
        Assert.True(File.Exists(scriptPath));
        Assert.False(File.Exists(definitionPath));
    }

    [Fact]
    public void Windows_Task_Scheduler_validates_the_definition_without_registering_a_task()
    {
        var service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        object? folder = null;
        try
        {
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            // TASK_VALIDATE_ONLY (1) checks the real Windows schema but does not
            // register or start a task, execute a script or touch the live updater.
            ((dynamic)folder).RegisterTask("ValeniusBatteryValidation-" + Guid.NewGuid().ToString("N"),
                UpdateTaskDefinition.Create(Path.Combine(_directory, "run-update.cmd")),
                1, null, null, 5 /* TASK_LOGON_SERVICE_ACCOUNT */, null);
        }
        finally
        {
            if (folder is not null) Marshal.FinalReleaseComObject(folder);
            Marshal.FinalReleaseComObject(service);
        }
    }

    [Fact]
    public void Failed_task_registration_does_not_issue_run_or_claim_success()
    {
        var attempts = 0;
        using var updater = new UpdateChecker(null!, NullLogger<UpdateChecker>.Instance, null!, null!, null!, null!,
            _directory, runProcess: (_, args) =>
            {
                attempts++;
                Assert.StartsWith("/Create", args);
                throw new InvalidOperationException("Synthetic scheduler rejection.");
            });
        Assert.Throws<InvalidOperationException>(() => updater.LaunchInstallerDetached(Path.Combine(_directory, "installer.exe")));
        Assert.Equal(1, attempts);
        Assert.Empty(Directory.GetFiles(_directory, "*.xml"));
    }

    [Fact]
    public void XML_round_trips_script_paths_with_spaces_and_XML_special_characters()
    {
        var script = Path.Combine(_directory, "A & B", "run-update.cmd");
        var definition = XDocument.Parse(UpdateTaskDefinition.Create(script));
        Assert.Equal(Path.GetFullPath(script), definition.Root!.Element(Ns + "Actions")!.Element(Ns + "Exec")!.Element(Ns + "Command")!.Value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); // Unique fixture only.
    }
}
