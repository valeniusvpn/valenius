using System.Security.Cryptography;
using System.Text;

namespace Valenius.Backend.Services;

/// <summary>
/// Singleton in-memory holder for the client API key(s) loaded from ServerSettings on
/// startup. Avoids a DB round-trip on every API request while keeping the key out of
/// appsettings.json.
///
/// Holds a <see cref="Key"/> (current) plus an optional <see cref="PreviousKey"/> that
/// stays valid during a rotation grace window, so deployed clients can roll forward to
/// the new key (delivered in the heartbeat) before the old one is retired.
/// </summary>
public sealed class ApiKeyStore
{
    public string Key { get; set; } = "";
    public string? PreviousKey { get; set; }

    /// <summary>
    /// True if <paramref name="presented"/> matches the current or (still-valid) previous
    /// key. Constant-time comparison; an empty configured key is never accepted (fail closed).
    /// </summary>
    public bool IsAccepted(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
            return false;
        return Matches(presented, Key) || Matches(presented, PreviousKey);
    }

    private static bool Matches(string presented, string? configured)
    {
        if (string.IsNullOrEmpty(configured))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(configured));
    }
}
