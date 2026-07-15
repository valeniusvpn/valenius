namespace Valenius.Backend.Licensing;

/// <summary>
/// Community edition: no limits enforced. Pro replaces this via DI.
/// </summary>
public sealed class CommunityLicenseEnforcement(ILicenseContext license) : ILicenseEnforcement
{
    public LicenseCheckResult CanActivateEndpoint(int currentActiveEndpoints, int customerMaxEndpoints)
    {
        if (!license.IsValid && license.LicenseType != "community")
            return new(false, "License expired — new endpoint activations are blocked.");

        if (customerMaxEndpoints > 0 && currentActiveEndpoints >= customerMaxEndpoints)
            return new(false, $"Customer endpoint limit reached ({customerMaxEndpoints}).");

        if (license.MaxEndpoints > 0 && currentActiveEndpoints >= license.MaxEndpoints)
            return new(false, $"License endpoint pool exhausted ({license.MaxEndpoints} total).");

        return LicenseCheckResult.Ok;
    }

    public LicenseCheckResult CanAddCustomer(int currentCustomerCount)
    {
        if (!license.MspModeEnabled && currentCustomerCount >= 1)
            return new(false, "MSP mode is not enabled — only one customer is allowed.");

        if (license.MaxCustomers > 0 && currentCustomerCount >= license.MaxCustomers)
            return new(false, $"Customer limit reached ({license.MaxCustomers}).");

        return LicenseCheckResult.Ok;
    }

    public LicenseCheckResult CanAssignEndpointsToCustomer(int requestedEndpoints, int currentTotalAssigned)
    {
        if (license.MaxEndpoints < 0)
            return LicenseCheckResult.Ok; // unlimited

        var wouldBeTotal = currentTotalAssigned + requestedEndpoints;
        if (wouldBeTotal > license.MaxEndpoints)
            return new(false,
                $"Assignment would exceed license pool ({wouldBeTotal} > {license.MaxEndpoints}).");

        return LicenseCheckResult.Ok;
    }
}
