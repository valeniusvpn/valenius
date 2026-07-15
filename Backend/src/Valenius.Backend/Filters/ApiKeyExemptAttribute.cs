namespace Valenius.Backend.Filters;

/// <summary>
/// Marks a client-API action as exempt from <see cref="ApiKeyFilter"/> so it can be reached by a
/// device that does not yet hold the installation's API key (first-run bootstrap). The action is
/// then responsible for its own authentication:
///   • <c>pairing/redeem</c> authenticates via the one-time pairing token.
///   • <c>register</c> allows a keyless call but only ever creates/updates a <b>pending</b> client,
///     and only hands back the installation key once an admin has activated it.
/// Everything else stays protected by the class-level filter (fail-closed by default — you must opt
/// out explicitly, so a new endpoint is never accidentally left open).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ApiKeyExemptAttribute : Attribute;
