namespace Valenius.Backend.Licensing;

public interface ILicenseEnforcement
{
    LicenseCheckResult CanActivateEndpoint(int currentActiveEndpoints, int customerMaxEndpoints);
    LicenseCheckResult CanAddCustomer(int currentCustomerCount);
    LicenseCheckResult CanAssignEndpointsToCustomer(int requestedEndpoints, int currentTotalAssigned);
}

public record LicenseCheckResult(bool Allowed, string? DenialReason = null)
{
    public static readonly LicenseCheckResult Ok = new(true);
}
