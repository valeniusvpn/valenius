using System.Security.Cryptography;
using System.Text;

namespace Valenius.Backend.Services;

/// <summary>Independent 256-bit device bearer secrets. Only hashes belong in the DB.
/// ClientKey is a public identifier and must never authorize enrollment or recovery.</summary>
public static class DeviceCredential
{
    public static string Mint() => "vlnd_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool Matches(string? provided, string? hash) =>
        provided is { Length: 69 } && provided.StartsWith("vlnd_", StringComparison.Ordinal)
        && hash is { Length: 64 }
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(provided)), Encoding.ASCII.GetBytes(hash));
}

public interface IDeviceRequest
{
    string ClientKey { get; }
}
