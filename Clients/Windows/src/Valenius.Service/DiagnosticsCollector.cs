using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Valenius.Service;

/// <summary>
/// Collects a fixed, documented allowlist of <b>Valenius-only</b> diagnostic logs into a
/// gzipped text bundle for upload to the backend (POST /api/clients/logs). Secrets are
/// redacted before the bytes ever leave the machine. No other system data is gathered.
///
/// Sources: a summary header, the Windows Application Event Log filtered to source "Valenius"
/// (the service's own log — see Program.cs UseWindowsService), the WireGuard tunnel Event Log
/// (best-effort), and the updater's install.log. Each source is best-effort; a failure to read
/// one is noted inline rather than aborting the bundle.
/// </summary>
public static class DiagnosticsCollector
{
    public static byte[] BuildGzippedBundle(string? apiKeyToRedact)
    {
        var sb = new StringBuilder();
        void Section(string title, string body)
        {
            sb.AppendLine($"===== {title} =====");
            sb.AppendLine(body);
            sb.AppendLine();
        }

        Section("Summary", BuildSummary());
        Section("Windows Event Log (source: Valenius)", TryReadEventLog("Application", "Valenius", 500));
        Section("WireGuard Event Log", TryReadEventLog("Application", "WireGuard", 200));
        Section("install.log", TryReadInstallLog());

        var text = Redact(sb.ToString(), apiKeyToRedact);

        using var outMs = new MemoryStream();
        using (var gz = new GZipStream(outMs, CompressionLevel.Optimal, leaveOpen: true))
        using (var w = new StreamWriter(gz, new UTF8Encoding(false)))
        {
            w.Write(text);
        }
        return outMs.ToArray();
    }

    private static string BuildSummary() => string.Join("\n",
        $"Collected:  {DateTime.UtcNow:O} UTC",
        $"Machine:    {Environment.MachineName}",
        $"OS:         {Environment.OSVersion}",
        $"Client ver: {UpdateChecker.GetCurrentVersion()}");

    private static string TryReadEventLog(string logName, string source, int maxMatches)
    {
        try
        {
            using var log = new EventLog(logName);
            var entries = log.Entries;
            var sb = new StringBuilder();
            int matched = 0, scanned = 0;
            // Newest first; bound the scan so a huge Application log can't stall collection.
            for (int i = entries.Count - 1; i >= 0 && matched < maxMatches && scanned < 20_000; i--, scanned++)
            {
                EventLogEntry e;
                try { e = entries[i]; } catch { continue; }
                if (!string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase)) continue;
                sb.AppendLine($"[{e.TimeGenerated:yyyy-MM-dd HH:mm:ss}] {e.EntryType}: {e.Message}");
                matched++;
            }
            return matched == 0 ? "[no matching entries]" : sb.ToString();
        }
        catch (Exception ex)
        {
            return $"[unavailable: {ex.Message}]";
        }
    }

    private static string TryReadInstallLog()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Valenius", "updates", "install.log");
            if (!File.Exists(path)) return "[not present]";
            var bytes = File.ReadAllBytes(path);
            const int maxBytes = 200_000;
            if (bytes.Length > maxBytes) bytes = bytes[^maxBytes..];
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            return $"[unavailable: {ex.Message}]";
        }
    }

    /// <summary>Strips secrets so nothing sensitive leaves the machine.</summary>
    private static string Redact(string text, string? apiKey)
    {
        if (!string.IsNullOrEmpty(apiKey))
            text = text.Replace(apiKey, "[REDACTED-APIKEY]");
        // WireGuard private / preshared keys.
        text = Regex.Replace(text, @"(?im)^(\s*(?:PrivateKey|PresharedKey))\s*=\s*.+$", "$1 = [REDACTED]");
        // Any bare base64 32-byte key (44 chars ending '=').
        text = Regex.Replace(text, @"\b[A-Za-z0-9+/]{43}=", "[REDACTED-KEY]");
        // secret / token / authorization / bearer values.
        text = Regex.Replace(text, @"(?i)\b(secret|token|authorization|bearer|apikey|x-api-key)\b\s*[:=]\s*\S+", "$1=[REDACTED]");
        return text;
    }
}
