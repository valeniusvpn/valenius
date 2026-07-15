namespace Valenius.Backend.Models;

/// <summary>
/// A per-interval WireGuard traffic sample for one client, written by TrafficSamplingService
/// (Pro). <see cref="RxBytes"/>/<see cref="TxBytes"/> are the bytes transferred *during this
/// sample interval* (reset-aware deltas — see <see cref="Client.WgRxLastRaw"/>), so charts plot
/// them directly as throughput. The client's running lifetime totals live on
/// <see cref="Client.WgRxLifetime"/>/<see cref="Client.WgTxLifetime"/>. Rows older than
/// <c>ServerSettings.TrafficRetentionDays</c> are pruned daily.
/// </summary>
public class ClientTrafficSample
{
    public long Id { get; set; }

    public int ClientId { get; set; }
    public Client? Client { get; set; }

    public DateTime SampledAt { get; set; }

    /// <summary>Bytes received (from the client's perspective: downloaded) in this interval.</summary>
    public long RxBytes { get; set; }

    /// <summary>Bytes sent (uploaded) in this interval.</summary>
    public long TxBytes { get; set; }
}
