using System.IO.Pipes;
using System.Text;

namespace Valenius.Service;

/// <summary>
/// Verifies a tunnel is live by reading WireGuard's per-interface UAPI pipe and checking
/// for a recent handshake — a local, per-interface signal that needs no routed gateway.
///
/// Used for foreign-customer profiles, where the primary VPN's HTTP /health probe is not
/// applicable: foreign configs deliberately do NOT route the sidecar gateway (that route
/// would collide with the primary VPN when both sidecars share a transit subnet), so there
/// is nothing to probe over HTTP. A fresh WireGuard handshake confirms the peer answered.
///
/// WireGuard for Windows exposes each running tunnel's UAPI as a named pipe
/// <c>\\.\pipe\ProtectedPrefix\Administrators\WireGuard\&lt;tunnel&gt;</c>, accessible to
/// SYSTEM/Administrators. Sending "get=1\n\n" returns the cross-platform wg(8) UAPI dump,
/// including <c>last_handshake_time_sec</c> per peer.
/// </summary>
public class HandshakeVerifier
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TotalBudget   = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout= TimeSpan.FromSeconds(2);

    // A handshake counts as "live" if it happened within this window.
    private const long HandshakeMaxAgeSec = 180;

    private readonly ILogger<HandshakeVerifier> _logger;

    public HandshakeVerifier(ILogger<HandshakeVerifier> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Polls the tunnel's UAPI for up to 30 seconds. Returns true once any peer reports a
    /// handshake within the last <see cref="HandshakeMaxAgeSec"/> seconds.
    /// </summary>
    public Task<bool> VerifyAsync(string tunnelName, CancellationToken ct)
        => VerifyAsync(tunnelName, TotalBudget, ct);

    /// <summary>
    /// As <see cref="VerifyAsync(string,CancellationToken)"/> but with a caller-supplied budget.
    /// The network-change re-verifier uses a shorter budget. "Live" means any peer handshook
    /// within <see cref="HandshakeMaxAgeSec"/> — with the universal <c>PersistentKeepalive=25</c>
    /// on provisioned configs, a reachable tunnel re-handshakes well inside that window while a
    /// dead one ages out of it.
    /// </summary>
    public async Task<bool> VerifyAsync(string tunnelName, TimeSpan budget, CancellationToken ct)
    {
        var pipeName = $@"ProtectedPrefix\Administrators\WireGuard\{tunnelName}";
        var deadline = DateTime.UtcNow + budget;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                if (await TryReadHandshakeAsync(pipeName, ct) is { } epoch)
                {
                    var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - epoch;
                    if (age <= HandshakeMaxAgeSec)
                    {
                        _logger.LogInformation("HandshakeVerifier: tunnel '{Tunnel}' handshook {Age}s ago.", tunnelName, age);
                        return true;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug("HandshakeVerifier: '{Tunnel}' probe failed — {Msg} (retrying)", tunnelName, ex.Message);
            }

            await Task.Delay(RetryInterval, ct);
        }

        _logger.LogWarning("HandshakeVerifier: tunnel '{Tunnel}' had no recent handshake within {Budget}s.",
            tunnelName, (int)budget.TotalSeconds);
        return false;
    }

    /// <summary>
    /// Opens the UAPI pipe, issues "get=1", and returns the unix-second timestamp of the most
    /// recent peer handshake, or null if none / the interface has never handshaked.
    /// </summary>
    private static async Task<long?> TryReadHandshakeAsync(string pipeName, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(connectCts.Token);
        }

        var request = "get=1\n\n"u8.ToArray();
        await pipe.WriteAsync(request, ct);
        await pipe.FlushAsync(ct);

        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        long mostRecent = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0) break;   // blank line terminates the UAPI response
            const string key = "last_handshake_time_sec=";
            if (line.StartsWith(key, StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan(key.Length), out var sec) && sec > mostRecent)
                mostRecent = sec;
        }

        if (mostRecent <= 0) return null;
        return mostRecent;
    }
}
