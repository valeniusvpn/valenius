namespace Valenius.Backend.Services;

/// <summary>
/// Singleton in-memory holder for the system-wide default heartbeat interval, loaded from
/// ServerSettings on startup and updated whenever Admin -> Settings saves it. Avoids a DB
/// round-trip on every heartbeat/long-poll response (BuildStatusResponseAsync runs for every
/// client, potentially every ~1 min per client fleet-wide) — same rationale as ApiKeyStore.
/// </summary>
public sealed class HeartbeatSettingsStore
{
    public int DefaultIntervalMinutes { get; set; } = 1;
}
