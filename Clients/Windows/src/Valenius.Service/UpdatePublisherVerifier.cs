using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace Valenius.Service;

/// <summary>Authenticode trust plus a publisher identity independent of the backend.
/// Pins are public values from the existing signed releases, not deployment settings.
/// The Azure profile EKU survives daily certificate/key renewal; replacing the profile
/// or CA requires a deliberately shipped trust transition in the client.</summary>
internal static class UpdatePublisherVerifier
{
    internal const string Publisher = "Stranto Business Solutions GmbH";
    internal const string ProfileEku = "1.3.6.1.4.1.311.97.73269376.94126991.477555400.567931581";
    internal const string RootSpkiSha256 = "DEDB13BCDE2375995A1C37BD68A7C4A82AF5CBB9C4DF821B62D5733009028F07";
    private static readonly Guid AuthenticodeAction = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    internal static string Verify(string path, SafeFileHandle handle)
    {
        var pathMemory = Marshal.StringToCoTaskMemUni(Path.GetFullPath(path));
        var fileMemory = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var action = AuthenticodeAction;
        var data = new WinTrustData
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>(),
            UiChoice = 2, // WTD_UI_NONE
            UnionChoice = 1, // WTD_CHOICE_FILE
            FileInfo = fileMemory,
            StateAction = 1, // WTD_STATEACTION_VERIFY; CLOSE is mandatory even on failure.
            // Check revocation except the root and disallow obsolete digests. No offline
            // fallback or ignore-revocation flag. Normal trusted timestamp rules apply.
            ProviderFlags = 0x80 | 0x2000,
            UiContext = 1 // WTD_UICONTEXT_INSTALL
        };
        try
        {
            Marshal.StructureToPtr(new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                Path = pathMemory,
                Handle = handle.DangerousGetHandle()
            }, fileMemory, false);
            var status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (status != 0)
                throw new CryptographicException($"Installer Authenticode validation failed (0x{unchecked((uint)status):X8}).");

            // Read the signer selected by the successful verification, not an unrelated
            // certificate extracted separately from the file or a timestamp countersigner.
            var provider = WTHelperProvDataFromStateData(data.StateData);
            var signerPointer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signerPointer == IntPtr.Zero) throw new CryptographicException("Verified installer signer is unavailable.");
            var signer = Marshal.PtrToStructure<ProviderSigner>(signerPointer);
            if (signer.Size < Marshal.SizeOf<ProviderSigner>() || signer.Error != 0 || signer.CertificateCount is < 2 or > 64)
                throw new CryptographicException("Verified installer signer chain is invalid.");
            using var leaf = Certificate(signerPointer, 0);
            using var root = Certificate(signerPointer, signer.CertificateCount - 1);
            if (!MatchesPublisher(leaf, root))
                throw new CryptographicException("Installer was not signed by the trusted Valenius publisher profile.");
            return leaf.Thumbprint;
        }
        finally
        {
            data.StateAction = 2; // WTD_STATEACTION_CLOSE
            try { WinVerifyTrust(new IntPtr(-1), ref action, ref data); }
            finally
            {
                Marshal.FreeHGlobal(fileMemory);
                Marshal.FreeCoTaskMem(pathMemory);
                GC.KeepAlive(handle);
            }
        }
    }

    internal static bool MatchesPublisher(X509Certificate2 leaf, X509Certificate2 root)
    {
        var usages = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .SelectMany(extension => extension.EnhancedKeyUsages.Cast<Oid>()).Select(oid => oid.Value).ToHashSet(StringComparer.Ordinal);
        return usages.Contains("1.3.6.1.5.5.7.3.3") && usages.Contains(ProfileEku)
            && string.Equals(leaf.GetNameInfo(X509NameType.SimpleName, false), Publisher, StringComparison.Ordinal)
            && string.Equals(Convert.ToHexString(SHA256.HashData(root.PublicKey.ExportSubjectPublicKeyInfo())),
                RootSpkiSha256, StringComparison.Ordinal);
    }

    private static X509Certificate2 Certificate(IntPtr signer, uint index)
    {
        var pointer = WTHelperGetProvCertFromChain(signer, index);
        if (pointer == IntPtr.Zero) throw new CryptographicException("Verified certificate is unavailable.");
        var certificate = Marshal.PtrToStructure<ProviderCertificate>(pointer);
        if (certificate.Size < Marshal.SizeOf<ProviderCertificate>() || certificate.Context == IntPtr.Zero)
            throw new CryptographicException("Invalid verified certificate context.");
        var context = Marshal.PtrToStructure<CertificateContext>(certificate.Context);
        if (context.Encoded == IntPtr.Zero || context.EncodedLength is 0 or > 1_048_576)
            throw new CryptographicException("Invalid verified certificate encoding.");
        var bytes = new byte[checked((int)context.EncodedLength)];
        Marshal.Copy(context.Encoded, bytes, 0, bytes.Length);
        return X509CertificateLoader.LoadCertificate(bytes);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo { public uint Size; public IntPtr Path, Handle, KnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallback, SipClient;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData, UrlReference;
        public uint ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderSigner
    {
        public uint Size;
        public System.Runtime.InteropServices.ComTypes.FILETIME VerifyAsOf;
        public uint CertificateCount;
        public IntPtr Certificates;
        public uint SignerType;
        public IntPtr SignerInfo;
        public uint Error, CounterSignerCount;
        public IntPtr CounterSigners, ChainContext;
    }

    // Only the prefixes used here are marshalled; cbStruct describes the full native structure.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate { public uint Size; public IntPtr Context; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext { public uint Encoding; public IntPtr Encoded; public uint EncodedLength; }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint index);
}
