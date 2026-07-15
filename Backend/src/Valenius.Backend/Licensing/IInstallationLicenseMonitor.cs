namespace Valenius.Backend.Licensing;

public interface IInstallationLicenseMonitor
{
    InstallationLicenseStatus Status          { get; }
    DateTime?                 LastValidatedAt { get; }
    bool IsProActive => Status != InstallationLicenseStatus.OssMode;

    // Called by on-demand checks (e.g. the hidden double-click) to persist the result.
    void RecordCheckResult(bool reachable, DateTime? lastValidatedAt);
}
