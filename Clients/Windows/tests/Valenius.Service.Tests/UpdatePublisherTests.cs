using System.Diagnostics;
using System.Security.Cryptography;
using Valenius.Service;
using Xunit;

namespace Valenius.Service.Tests;

/// <summary>Release gate: supply two real signed releases with different leaf certificates.
/// Fixtures are external artifacts, not large binaries copied into the repository.</summary>
public sealed class SignedReleaseFactAttribute : FactAttribute
{
    public SignedReleaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VALENIUS_WINDOWS_RELEASE_FIXTURES")))
            Skip = "Set VALENIUS_WINDOWS_RELEASE_FIXTURES to two signed installer paths (semicolon-separated).";
    }
}

public sealed class UpdatePublisherTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Valenius-publisher-test-" + Guid.NewGuid().ToString("N"));

    internal static string[] Fixtures()
    {
        var values = Environment.GetEnvironmentVariable("VALENIUS_WINDOWS_RELEASE_FIXTURES")!.Split(';', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(values.Length >= 2, "Two real releases are required to verify daily signing-certificate renewal.");
        foreach (var path in values) Assert.True(File.Exists(path), "Signed fixture not found: " + path);
        return values;
    }

    internal static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    [SignedReleaseFact]
    public void RealPublishedInstallersAreAcceptedAcrossCertificateRenewal()
    {
        var fingerprints = new HashSet<string>();
        foreach (var path in Fixtures())
        {
            var version = FileVersionInfo.GetVersionInfo(path).ProductVersion!.Trim();
            using var accepted = VerifiedUpdateInstaller.Open(path, Hash(path), version, "0.0.0");
            fingerprints.Add(accepted.SignerThumbprint);
        }
        Assert.True(fingerprints.Count >= 2, "Fixtures must use different leaf certificates.");
    }

    [SignedReleaseFact]
    public void TamperingIsRejectedEvenWhenBackendSuppliesMatchingHash()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "tampered.exe");
        File.Copy(Fixtures()[0], path);
        using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            file.Position = 128; // Signed DOS-stub bytes, outside the PE header/checksum/security-directory exclusions.
            var old = file.ReadByte();
            file.Position--;
            file.WriteByte((byte)(old ^ 1));
        }
        Assert.Throws<CryptographicException>(() => VerifiedUpdateInstaller.Open(path, Hash(path), "9999.0.0", "0.0.0"));
    }

    [SignedReleaseFact]
    public void AdvertisedVersionCannotRelabelOrDowngradeAValidSignedInstaller()
    {
        var path = Fixtures()[0];
        var version = FileVersionInfo.GetVersionInfo(path).ProductVersion!.Trim();
        Assert.Throws<CryptographicException>(() => VerifiedUpdateInstaller.Open(path, Hash(path), "9999.0.0", "0.0.0"));
        Assert.Throws<CryptographicException>(() => VerifiedUpdateInstaller.Open(path, Hash(path), version, version));
        // Preserve revision comparison even though the legacy heartbeat/UI reports three components.
        Assert.Throws<CryptographicException>(() => VerifiedUpdateInstaller.Open(path, Hash(path), version, version + ".1"));
    }

    [SignedReleaseFact]
    public void UnsignedCertificateTablePaddingCannotBeAddedToSignedInstaller()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "padded.exe");
        File.Copy(Fixtures()[0], path);
        using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        using (var reader = new BinaryReader(file, System.Text.Encoding.UTF8, leaveOpen: true))
        using (var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            file.Position = 0x3c;
            var pe = reader.ReadInt32();
            file.Position = pe + 24;
            var magic = reader.ReadUInt16();
            var directory = pe + 24 + (magic == 0x10b ? 96 : 112) + 8 * 4;
            file.Position = directory;
            var offset = reader.ReadInt32();
            var size = reader.ReadInt32();
            file.Position = directory + 4;
            writer.Write(size + 64);
            file.Position = offset;
            writer.Write(size + 64);
            file.Position = file.Length;
            writer.Write(Enumerable.Repeat((byte)0x41, 64).ToArray());
        }
        var version = FileVersionInfo.GetVersionInfo(path).ProductVersion!.Trim();
        var error = Assert.Throws<CryptographicException>(() => VerifiedUpdateInstaller.Open(path, Hash(path), version, "0.0.0"));
        Assert.Contains("padding", error.Message);
    }

    [Fact]
    public void UnsignedFileWithCorrectBackendHashIsRejected()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "unsigned.exe");
        File.WriteAllText(path, "Untrusted update contents");
        Assert.Throws<CryptographicException>(() => VerifiedUpdateInstaller.Open(path, Hash(path), "9999.0.0", "0.0.0"));
    }

    [Fact]
    public void ValidMicrosoftSignedExecutableIsNotTheValeniusPublisher()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        Assert.True(File.Exists(path), "The Windows .NET SDK supplies this wrong-publisher fixture.");
        using var file = File.OpenRead(path);
        var error = Assert.Throws<CryptographicException>(() => UpdatePublisherVerifier.Verify(path, file.SafeFileHandle));
        Assert.Contains("publisher profile", error.Message);
    }

    [SignedReleaseFact]
    public void VerifiedArtifactCannotBeModifiedOrReplacedDuringHandoff()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "verified.exe");
        File.Copy(Fixtures()[0], path);
        var version = FileVersionInfo.GetVersionInfo(path).ProductVersion!.Trim();
        using (var accepted = VerifiedUpdateInstaller.Open(path, Hash(path), version, "0.0.0"))
        {
            Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write).Dispose());
            Assert.Throws<IOException>(() => File.Delete(path));
        }
        File.Delete(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
