using System.Security.Cryptography;

namespace Valenius.Backend.Services;

/// <summary>
/// AES-256-GCM encryption for full (key-inclusive) backup files.
///
/// File format: magic(4) | version(1) | nonce(12) | ciphertext | tag(16)
/// Key display:  8 groups of 8 lowercase hex chars joined by hyphens (71 chars total).
/// </summary>
public static class BackupEncryption
{
    private static readonly byte[] Magic = "VLNB"u8.ToArray();
    private const byte   FormatVersion = 1;
    private const int    KeyBytes      = 32;
    private const int    NonceBytes    = 12;
    private const int    TagBytes      = 16;

    /// <summary>
    /// Generates a cryptographically random AES-256 key and returns the raw bytes
    /// together with the human-readable display string used as the backup passphrase.
    /// </summary>
    public static (byte[] Key, string DisplayKey) GenerateKey()
    {
        var key  = RandomNumberGenerator.GetBytes(KeyBytes);
        var hex  = Convert.ToHexString(key).ToLowerInvariant();
        var disp = string.Join('-', Enumerable.Range(0, 8).Select(i => hex.Substring(i * 8, 8)));
        return (key, disp);
    }

    /// <summary>Encrypts <paramref name="plaintext"/> with AES-256-GCM.</summary>
    public static byte[] Encrypt(byte[] key, byte[] plaintext)
    {
        var nonce      = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag        = new byte[TagBytes];

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Layout: magic(4) | version(1) | nonce(12) | ciphertext | tag(16)
        var result = new byte[5 + NonceBytes + ciphertext.Length + TagBytes];
        Magic.CopyTo(result, 0);
        result[4] = FormatVersion;
        nonce.CopyTo(result, 5);
        ciphertext.CopyTo(result, 5 + NonceBytes);
        tag.CopyTo(result, 5 + NonceBytes + ciphertext.Length);
        return result;
    }

    /// <summary>Decrypts a file produced by <see cref="Encrypt"/>. Throws on wrong key or corrupt data.</summary>
    public static byte[] Decrypt(byte[] key, byte[] data)
    {
        const int minLen = 5 + NonceBytes + TagBytes;
        if (data.Length < minLen)
            throw new InvalidOperationException("File is too short to be a valid encrypted backup.");

        if (!data.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidOperationException("Not a Valenius encrypted backup file (invalid header).");

        if (data[4] != FormatVersion)
            throw new InvalidOperationException($"Unsupported backup format version {data[4]}.");

        var nonce      = data.AsSpan(5, NonceBytes);
        var tagOffset  = data.Length - TagBytes;
        var ciphertext = data.AsSpan(5 + NonceBytes, tagOffset - (5 + NonceBytes));
        var tag        = data.AsSpan(tagOffset, TagBytes);
        var plaintext  = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    /// <summary>
    /// Parses the display key (e.g. "a1b2c3d4-e5f6a7b8-…") back into 32 raw bytes.
    /// Hyphens and whitespace are stripped before parsing.
    /// </summary>
    public static byte[] ParseDisplayKey(string displayKey)
    {
        var clean = displayKey.Trim().Replace("-", "").Replace(" ", "");
        if (clean.Length != KeyBytes * 2)
            throw new ArgumentException(
                $"Invalid backup key: expected {KeyBytes * 2} hex characters, got {clean.Length}.");
        try
        {
            return Convert.FromHexString(clean);
        }
        catch (FormatException)
        {
            throw new ArgumentException("Invalid backup key: contains non-hexadecimal characters.");
        }
    }
}
