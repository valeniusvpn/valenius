using System.Diagnostics;
using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Valenius.Service;

/// <summary>Keep the exact verified bytes read-locked through update handoff.</summary>
internal sealed class VerifiedUpdateInstaller : IDisposable
{
    private readonly FileStream _file;
    internal string SignerThumbprint { get; }
    private VerifiedUpdateInstaller(FileStream file, string signerThumbprint)
    {
        _file = file;
        SignerThumbprint = signerThumbprint;
    }

    internal static VerifiedUpdateInstaller Open(string path, string expectedHash, string advertisedVersion, string currentVersion)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var hash = SHA256.HashData(file);
            if (expectedHash.Length != 64 || !Convert.ToHexString(hash).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Installer SHA-256 does not match the advertised hash.");
            ValidateSignatureContainer(file);
            var signer = UpdatePublisherVerifier.Verify(path, file.SafeFileHandle);
            var metadata = FileVersionInfo.GetVersionInfo(path);
            if (!string.Equals(metadata.ProductName?.Trim(), "Valenius", StringComparison.Ordinal)
                || !string.Equals(metadata.CompanyName?.Trim(), UpdatePublisherVerifier.Publisher, StringComparison.Ordinal))
                throw new CryptographicException("Signed installer product identity is not Valenius.");
            var version = ParseVersion(metadata.ProductVersion?.Trim());
            if (version != ParseVersion(advertisedVersion) || version <= ParseVersion(currentVersion))
                throw new CryptographicException("Signed installer version does not match the advertised newer release.");
            return new VerifiedUpdateInstaller(file, signer);
        }
        catch { file.Dispose(); throw; }
    }

    private static void ValidateSignatureContainer(FileStream file)
    {
        try
        {
            file.Position = 0;
            using var pe = new PEReader(file, PEStreamOptions.LeaveOpen);
            var table = pe.PEHeaders.PEHeader?.CertificateTableDirectory
                ?? throw new CryptographicException("Installer Authenticode PE header is missing.");
            // Authenticode excludes the certificate table from its image digest. Bound it
            // and reject unsigned overlay/padding without relying on machine registry policy.
            if (table.RelativeVirtualAddress <= 0 || table.RelativeVirtualAddress % 8 != 0
                || table.Size is < 8 or > 1_048_576
                || (long)table.RelativeVirtualAddress + table.Size != file.Length)
                throw new CryptographicException("Installer Authenticode certificate table is invalid.");
            file.Position = table.RelativeVirtualAddress;
            var bytes = new byte[table.Size];
            file.ReadExactly(bytes);
            for (var offset = 0; offset < bytes.Length;)
            {
                var remaining = bytes.AsSpan(offset);
                if (remaining.Length < 8) throw new CryptographicException("Invalid Authenticode certificate entry.");
                var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(remaining));
                if (length < 8 || length > remaining.Length
                    || BinaryPrimitives.ReadUInt16LittleEndian(remaining[4..]) != 0x200
                    || BinaryPrimitives.ReadUInt16LittleEndian(remaining[6..]) != 2)
                    throw new CryptographicException("Unsupported Authenticode certificate entry.");
                var blob = bytes.AsMemory(offset + 8, length - 8);
                var reader = new AsnReader(blob, AsnEncodingRules.BER);
                if (!reader.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
                    throw new CryptographicException("Invalid Authenticode signature container.");
                var encodedLength = reader.ReadEncodedValue().Length;
                var alignedLength = checked((length + 7) & ~7);
                if (alignedLength > remaining.Length || length - 8 - encodedLength > 7)
                    throw new CryptographicException("Invalid Authenticode signature padding.");
                foreach (var value in remaining[(8 + encodedLength)..alignedLength])
                    if (value != 0) throw new CryptographicException("Nonzero Authenticode signature padding.");
                offset += alignedLength;
            }
        }
        catch (Exception error) when (error is BadImageFormatException or AsnContentException or OverflowException)
        {
            throw new CryptographicException("Installer Authenticode container is invalid.", error);
        }
    }

    private static Version ParseVersion(string? value)
    {
        if (!Version.TryParse(value, out var version)) throw new CryptographicException("Invalid installer version.");
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }

    public void Dispose() => _file.Dispose();
}
