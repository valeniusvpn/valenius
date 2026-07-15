namespace Valenius.Backend.Licensing;

public interface ILicenseContext
{
    // Type and identity
    string  LicenseType { get; }   // "community", "pro-s", "msp-full", etc.
    string  Licensee    { get; }   // customer name or "Community"
    bool    IsCloud     { get; }
    string? LicenseId   { get; }   // null for community edition

    // Limits (-1 = unlimited)
    int MaxEndpoints { get; }
    int MaxCustomers { get; }

    // Feature flags
    bool MspModeEnabled          { get; }
    bool AutoProvisioningEnabled { get; }
    bool AutoConnectEnabled      { get; }
    bool HasFeature(string featureFlag);

    // Validity
    bool      IsValid          { get; }  // signature OK + not expired (incl. grace)
    bool      IsExpired        { get; }  // past ExpiresAt but within grace period
    bool      IsInGracePeriod  { get; }
    DateTime? ExpiresAt        { get; }
    int       DaysUntilExpiry  { get; }  // negative when in grace period
}
