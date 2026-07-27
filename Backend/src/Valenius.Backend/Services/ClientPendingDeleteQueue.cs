using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;

namespace Valenius.Backend.Services;

/// <summary>
/// Queues a profile-name deletion for on-device removal. Delivered to the client as the
/// heartbeat's <c>PendingDeleteProfileName</c> field, oldest-queued first — the wire shape stays
/// a single name so no client-side changes are needed; the queue only changes how the backend
/// tracks multiple outstanding deletions. See <see cref="Models.ClientPendingDelete"/> for why a
/// queue (not a scalar field) is required. Does not call <c>SaveChangesAsync</c> — the caller owns
/// the surrounding unit of work.
/// </summary>
public static class ClientPendingDeleteQueue
{
    public static async Task QueueAsync(ApplicationDbContext db, int clientId, string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return;
        var name = profileName.Trim();
        var exists = await db.ClientPendingDeletes
            .AnyAsync(d => d.ClientId == clientId && d.ProfileName == name);
        if (!exists)
            db.ClientPendingDeletes.Add(new ClientPendingDelete { ClientId = clientId, ProfileName = name });

        // Also drop the retained copy (Models.ClientConfigArchive) so a deleted profile can't be
        // handed back out by GET /api/clients/profiles (the cross-OS-user sync's authoritative
        // pull) or resurrected by a future reinstall-reassign resend.
        var archived = await db.ClientConfigArchives
            .FirstOrDefaultAsync(a => a.ClientId == clientId && a.ProfileName == name);
        if (archived is not null)
            db.ClientConfigArchives.Remove(archived);
    }
}
