using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

/// <summary>
/// A retained copy of a WireGuard config that was delivered to a client, keyed by
/// (client, profile name). Captured at every config-egress point so the actual
/// <c>.conf</c> content survives the one-shot delivery that otherwise clears it.
///
/// Purpose: when an admin reassigns a reinstalled machine onto an existing record
/// (<c>Details.OnPostReassignAsync</c>), the fresh install has no local configs.
/// Flagging every archived profile with <see cref="PendingResend"/> lets the next
/// heartbeat re-deliver all of them inline so the device gets its profiles back
/// automatically — including manually-uploaded ones that aren't re-provisionable.
/// </summary>
public class ClientConfigArchive
{
    public int Id { get; set; }

    public int ClientId { get; set; }
    public Client Client { get; set; } = null!;

    [MaxLength(50)]
    public string ProfileName { get; set; } = "";

    /// <summary>The full WireGuard <c>.conf</c> text last delivered for this profile.</summary>
    public string Content { get; set; } = "";

    /// <summary>
    /// When true, this profile is queued for one-shot re-delivery to the client on its
    /// next heartbeat (set by a reinstall reassign). Cleared once delivered.
    /// </summary>
    public bool PendingResend { get; set; }

    public DateTime UpdatedAt { get; set; }
}
