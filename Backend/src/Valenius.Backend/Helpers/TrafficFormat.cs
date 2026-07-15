using System.Globalization;

namespace Valenius.Backend.Helpers;

/// <summary>Human-readable byte formatting for the traffic dashboard (binary units, KiB/MiB/…).</summary>
public static class TrafficFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>e.g. 1536 → "1.5 KB", 0 → "0 B". Binary (1024) steps; labelled KB/MB for familiarity.</summary>
    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        var digits = v >= 100 || u == 0 ? 0 : v >= 10 ? 1 : 2;
        return v.ToString("F" + digits, CultureInfo.InvariantCulture) + " " + Units[u];
    }
}
