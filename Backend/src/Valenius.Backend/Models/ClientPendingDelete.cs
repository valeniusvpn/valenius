using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

/// <summary>
/// A profile name queued for deletion on a client. A real queue — not a single scalar field —
/// because three independent triggers can request a deletion for the same client (foreign-profile
/// deprovision, native deprovision, and the admin's manual "Delete profile" action): with a single
/// field, a second deletion racing the first before it's been delivered would silently overwrite
/// and permanently lose it (the source row/config backing that first deletion is already gone by
/// the time the loss would be noticed). See <see cref="Services.ClientPendingDeleteQueue"/>.
/// </summary>
public class ClientPendingDelete
{
    public int Id { get; set; }

    public int ClientId { get; set; }
    public Client Client { get; set; } = null!;

    [MaxLength(50)]
    public string ProfileName { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
