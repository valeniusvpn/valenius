namespace Valenius.Backend.Licensing;

// Community / OSS edition — no licensing enforcement, always "valid".
internal sealed class NullInstallationLicenseMonitor : IInstallationLicenseMonitor
{
    public InstallationLicenseStatus Status          => InstallationLicenseStatus.Valid;
    public DateTime?                 LastValidatedAt => null;
    public bool                      IsProActive     => true;
    public void RecordCheckResult(bool reachable, DateTime? lastValidatedAt) { }
}
