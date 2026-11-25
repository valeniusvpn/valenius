namespace Valenius.Backend.Filters;

/// <summary>
/// Marks a client-API action as exempt from <see cref="DeviceCredentialFilter"/> so it can be reached by a
/// device that does not yet hold an individual credential (first-run bootstrap). The action is
/// then responsible for its own authentication:
///   • <c>pairing/redeem</c> authenticates via the one-time pairing token.
///   • <c>register</c> issues a credential once for a new pending identity; existing
///     identities must prove possession. It never discloses an installation-wide key.
/// Everything else stays protected by the class-level filter (fail-closed by default — you must opt
/// out explicitly, so a new endpoint is never accidentally left open).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ApiKeyExemptAttribute : Attribute;
