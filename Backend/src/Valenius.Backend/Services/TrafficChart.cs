using System.Globalization;
using System.Text;
using Valenius.Backend.Helpers;

namespace Valenius.Backend.Services;

/// <summary>One time bucket of traffic (bytes downloaded / uploaded by the client in the interval).</summary>
public readonly record struct TrafficBucket(DateTime At, long Rx, long Tx);

/// <summary>
/// Renders a two-series (Download / Upload) traffic area+line chart as a self-contained inline SVG
/// string — no JS, no external asset, CSP-safe. Marks per the dataviz spec: 2px lines, low-opacity
/// area fills, recessive grid, muted axis text, a legend for the two series. The CVD-safe blue+orange
/// pair (#2563eb / #d97706) is validated. Output goes to the page via @Html.Raw.
/// </summary>
public static class TrafficChart
{
    private const string RxColor = "#2563eb"; // Download (client received)
    private const string TxColor = "#d97706"; // Upload   (client sent)
    private const string Grid    = "#e5e7eb";
    private const string Ink     = "#6b7280";

    public static string Render(IReadOnlyList<TrafficBucket> buckets, TrafficRange? range = null)
    {
        var axisFormat = range?.AxisFormat ?? "MMM d";
        const int w = 720, h = 168, left = 60, right = 14, top = 14, bottom = 30;
        double plotL = left, plotR = w - right, plotT = top, plotB = h - bottom;
        double plotW = plotR - plotL, plotH = plotB - plotT;

        long max = 0;
        foreach (var b in buckets) max = Math.Max(max, Math.Max(b.Rx, b.Tx));

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg viewBox=\"0 0 {w} {h}\" width=\"100%\" role=\"img\" aria-label=\"Traffic over time\" style=\"max-width:100%;height:auto;font:11px system-ui,sans-serif\">");

        if (buckets.Count == 0 || max == 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{w / 2}\" y=\"{h / 2}\" text-anchor=\"middle\" fill=\"{Ink}\">No traffic recorded in this period.</text></svg>");
            return sb.ToString();
        }

        // Nice y-max (round up to 1/2/5 x 10^n) and 4 gridlines.
        var niceMax = NiceCeiling(max);
        double X(int i) => buckets.Count == 1 ? plotL + plotW / 2
                                              : plotL + plotW * i / (buckets.Count - 1);
        double Y(long v) => plotB - plotH * v / niceMax;

        for (var g = 0; g <= 4; g++)
        {
            var val = niceMax * g / 4;
            var y = Y(val);
            sb.Append(CultureInfo.InvariantCulture,
                $"<line x1=\"{plotL:0.#}\" y1=\"{y:0.#}\" x2=\"{plotR:0.#}\" y2=\"{y:0.#}\" stroke=\"{Grid}\" stroke-width=\"1\"/>");
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{plotL - 6:0.#}\" y=\"{y + 3:0.#}\" text-anchor=\"end\" fill=\"{Ink}\">{Esc(TrafficFormat.Bytes(val))}</text>");
        }

        // x-axis: up to 5 date labels.
        var step = Math.Max(1, buckets.Count / 5);
        for (var i = 0; i < buckets.Count; i += step)
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{X(i):0.#}\" y=\"{plotB + 16:0.#}\" text-anchor=\"middle\" fill=\"{Ink}\">{Esc(buckets[i].At.ToVienna().ToString(axisFormat, CultureInfo.InvariantCulture))}</text>");

        Series(sb, buckets, X, Y, plotB, b => b.Rx, RxColor);
        Series(sb, buckets, X, Y, plotB, b => b.Tx, TxColor);

        // Legend (top-right).
        LegendItem(sb, plotR - 150, top + 4, RxColor, "Download");
        LegendItem(sb, plotR - 70,  top + 4, TxColor, "Upload");

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void Series(StringBuilder sb, IReadOnlyList<TrafficBucket> buckets,
        Func<int, double> X, Func<long, double> Y, double baseY, Func<TrafficBucket, long> pick, string color)
    {
        var pts = new StringBuilder();
        for (var i = 0; i < buckets.Count; i++)
            pts.Append(CultureInfo.InvariantCulture, $"{X(i):0.#},{Y(pick(buckets[i])):0.#} ");
        var line = pts.ToString().TrimEnd();

        // area fill (low opacity) down to the baseline, then the 2px line on top.
        sb.Append(CultureInfo.InvariantCulture,
            $"<polygon points=\"{X(0):0.#},{baseY:0.#} {line} {X(buckets.Count - 1):0.#},{baseY:0.#}\" fill=\"{color}\" fill-opacity=\"0.12\"/>");
        sb.Append(CultureInfo.InvariantCulture,
            $"<polyline points=\"{line}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
    }

    private static void LegendItem(StringBuilder sb, double x, double y, string color, string label)
    {
        sb.Append(CultureInfo.InvariantCulture,
            $"<rect x=\"{x:0.#}\" y=\"{y:0.#}\" width=\"10\" height=\"10\" rx=\"2\" fill=\"{color}\"/>");
        sb.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{x + 14:0.#}\" y=\"{y + 9:0.#}\" fill=\"{Ink}\">{Esc(label)}</text>");
    }

    private static long NiceCeiling(long max)
    {
        if (max <= 0) return 1;
        var mag = (long)Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var m in new[] { 1, 2, 5, 10 })
            if (max <= mag * m) return mag * m;
        return mag * 10;
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
