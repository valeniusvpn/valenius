namespace Valenius.Backend.Services;

/// <summary>
/// A selectable time frame for the traffic charts. <see cref="Window"/> is the total span shown,
/// <see cref="Bucket"/> the width of each plotted point, and <see cref="AxisFormat"/> the x-axis
/// label format (rendered in Europe/Vienna). The set is fixed and shared by every dashboard page
/// (client / customer / fleet) plus the button group in <c>_TrafficRangeSelector</c>.
/// </summary>
public sealed record TrafficRange(string Key, string Label, TimeSpan Window, TimeSpan Bucket, string AxisFormat)
{
    public static readonly IReadOnlyList<TrafficRange> All =
    [
        new("hour", "1h",  TimeSpan.FromHours(1),  TimeSpan.FromMinutes(5),  "HH:mm"),
        new("4h",   "4h",  TimeSpan.FromHours(4),  TimeSpan.FromMinutes(15), "HH:mm"),
        new("8h",   "8h",  TimeSpan.FromHours(8),  TimeSpan.FromMinutes(30), "HH:mm"),
        new("day",  "24h", TimeSpan.FromHours(24), TimeSpan.FromHours(1),    "HH:mm"),
        new("3d",   "3d",  TimeSpan.FromDays(3),   TimeSpan.FromHours(3),    "MMM d HH:mm"),
        new("week", "7d",  TimeSpan.FromDays(7),   TimeSpan.FromHours(6),    "MMM d"),
    ];

    /// <summary>The default range used on first page load and by clients that send no <c>range</c>.</summary>
    public static readonly TrafficRange Default = All.First(r => r.Key == "day");

    /// <summary>Resolve a range key (from a query string); falls back to <see cref="Default"/>.</summary>
    public static TrafficRange Parse(string? key) =>
        All.FirstOrDefault(r => r.Key == key) ?? Default;
}
