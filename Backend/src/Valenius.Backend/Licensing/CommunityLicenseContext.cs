namespace Valenius.Backend.Licensing;

public sealed class CommunityLicenseContext : ILicenseContext
{
    public string  LicenseType => "community";
    public string  Licensee    => "Community";
    public bool    IsCloud     => false;
    public string? LicenseId   => null;

    public int MaxEndpoints => -1; // unlimited
    public int MaxCustomers =>  1;

    public bool MspModeEnabled          => false;
    public bool AutoProvisioningEnabled => false;
    public bool AutoConnectEnabled      => false;
    public bool HasFeature(string featureFlag) => false;

    public bool      IsValid         => true;
    public bool      IsExpired       => false;
    public bool      IsInGracePeriod => false;
    public DateTime? ExpiresAt       => null;
    public int       DaysUntilExpiry => int.MaxValue;
}
