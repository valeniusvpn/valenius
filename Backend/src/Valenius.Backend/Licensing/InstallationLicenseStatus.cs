namespace Valenius.Backend.Licensing;

public enum InstallationLicenseStatus
{
    Valid,    // Revocation check succeeded recently
    Warning,  // Server unreachable for 3+ days; Pro features still active
    OssMode,  // Revoked (immediate) or unreachable for 3+ months; Pro features disabled
}
