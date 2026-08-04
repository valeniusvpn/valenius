using System.Security.Cryptography;
using System.Text;

namespace Valenius.Backend.Services;

/// <summary>One freshly-minted Management API token. <see cref="FullToken"/> is shown to the
/// admin exactly once and never persisted; <see cref="TokenId"/> and <see cref="SecretHash"/>
/// are what gets stored on the <see cref="Models.ApiToken"/> row.</summary>
public readonly record struct MintedApiToken(string FullToken, string TokenId, string SecretHash);

/// <summary>The two segments of a presented <c>Authorization: Bearer vlnm_...</c> token,
/// before either is looked up or verified against the database.</summary>
public readonly record struct ParsedApiToken(string TokenId, string Secret);

/// <summary>
/// Mints, parses, and hashes Management API tokens in the fixed wire format
/// <c>vlnm_&lt;id:12 base32&gt;_&lt;secret:52 base32&gt;</c> (Crockford base32, unpadded).
/// The prefix is fixed and public — recognisable in a log/paste, registrable as a
/// secret-scanning pattern. See docs/design/management-api.md §4.1.
/// </summary>
public static class ApiTokenGenerator
{
    private const string Prefix = "vlnm";

    // Crockford base32 — excludes I, L, O, U to avoid visual confusion with 1/1/0/V.
    private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Mints a new token: 8 random bytes (64 bits) for the id, truncated to 12
    /// base32 chars (60 bits — plenty for a non-secret lookup key; a collision just fails
    /// the unique index and the caller retries), and 32 random bytes (256 bits) for the
    /// secret, which base32-encodes to exactly 52 chars.</summary>
    public static MintedApiToken Mint()
    {
        var tokenId = EncodeBase32(RandomNumberGenerator.GetBytes(8))[..12];
        var secret = EncodeBase32(RandomNumberGenerator.GetBytes(32));

        var fullToken = $"{Prefix}_{tokenId}_{secret}";
        var secretHash = HashSecret(secret);

        return new MintedApiToken(fullToken, tokenId, secretHash);
    }

    /// <summary>Parses an <c>Authorization</c> header value of the form
    /// <c>Bearer vlnm_&lt;id&gt;_&lt;secret&gt;</c>. Returns null on anything malformed —
    /// callers treat that as an auth failure, never an exception.</summary>
    public static ParsedApiToken? TryParseAuthorizationHeader(string? headerValue)
    {
        const string bearerPrefix = "Bearer ";
        if (string.IsNullOrWhiteSpace(headerValue)) return null;
        if (!headerValue.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        return TryParseToken(headerValue[bearerPrefix.Length..].Trim());
    }

    /// <summary>Parses a bare <c>vlnm_&lt;id&gt;_&lt;secret&gt;</c> string (no "Bearer " prefix).</summary>
    public static ParsedApiToken? TryParseToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var parts = token.Split('_');
        if (parts.Length != 3) return null;
        if (parts[0] != Prefix) return null;
        if (parts[1].Length == 0 || parts[2].Length == 0) return null;

        return new ParsedApiToken(parts[1], parts[2]);
    }

    /// <summary>Hex SHA-256 of the secret segment — what gets stored, never the secret itself.</summary>
    public static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>Constant-time check that <paramref name="presentedSecret"/> hashes to
    /// <paramref name="storedHash"/>.</summary>
    public static bool SecretMatches(string presentedSecret, string storedHash)
    {
        if (string.IsNullOrEmpty(presentedSecret) || string.IsNullOrEmpty(storedHash))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(HashSecret(presentedSecret)),
            Encoding.UTF8.GetBytes(storedHash));
    }

    private static string EncodeBase32(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bitsInBuffer = 0;

        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bitsInBuffer += 8;
            while (bitsInBuffer >= 5)
            {
                bitsInBuffer -= 5;
                sb.Append(CrockfordAlphabet[(buffer >> bitsInBuffer) & 0x1F]);
            }
        }

        if (bitsInBuffer > 0)
            sb.Append(CrockfordAlphabet[(buffer << (5 - bitsInBuffer)) & 0x1F]);

        return sb.ToString();
    }
}
