using System.Collections.Concurrent;

namespace Valenius.Backend.Services;

/// <summary>
/// Tracks which clients currently have an active long-poll connection, giving a
/// real-time "online / offline" signal without any database writes.
///
/// <see cref="MarkSeen"/> is called at the start of every <c>GET /api/clients/poll</c>
/// request.  A client is considered online if it polled within the last
/// <see cref="OnlineThreshold"/> (90 s), which comfortably covers one full 55-second poll
/// cycle plus reconnection time.
///
/// State is in-memory only and resets on server restart.  Clients reconnect within ~55 s,
/// so the display recovers on its own.
/// </summary>
public sealed class ClientPresenceTracker
{
    /// <summary>
    /// How long after the last contact (heartbeat OR long-poll) a client is still
    /// considered online.  Must exceed the maximum heartbeat interval (default 5 min,
    /// max 60 min) so a client that is reachable via heartbeat never flickers offline
    /// between cycles.  Set to 7 minutes to give a reasonable buffer above the default.
    /// </summary>
    public static readonly TimeSpan OnlineThreshold = TimeSpan.FromMinutes(7);

    private readonly ConcurrentDictionary<string, DateTime> _lastSeen =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records that <paramref name="clientKey"/> is currently connected.
    /// Call this at the start of each poll request.
    /// </summary>
    public void MarkSeen(string clientKey) =>
        _lastSeen[clientKey] = DateTime.UtcNow;

    /// <returns>True if the client polled within the last <see cref="OnlineThreshold"/>.</returns>
    public bool IsOnline(string clientKey) =>
        _lastSeen.TryGetValue(clientKey, out var ts) &&
        DateTime.UtcNow - ts < OnlineThreshold;

    /// <summary>
    /// Immediately marks <paramref name="clientKey"/> as offline by removing it from the
    /// tracker.  Called when the client notifies the backend it is gracefully shutting down.
    /// </summary>
    public void MarkOffline(string clientKey) =>
        _lastSeen.TryRemove(clientKey, out _);

    /// <summary>Total number of clients currently considered online.</summary>
    public int OnlineCount =>
        _lastSeen.Count(kv => DateTime.UtcNow - kv.Value < OnlineThreshold);
}
