using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;

namespace Valenius.Backend.Services;

/// <summary>
/// Upserts the retained copy of a config delivered (or generated) for a client, keyed by
/// (client, profile name). See <see cref="Models.ClientConfigArchive"/> — originally built for
/// the reinstall-reassign resend, now also the machine-authoritative profile store consumed by
/// <c>GET /api/clients/profiles</c> (cross-OS-user sync and the Windows device tunnel). Does not
/// call <c>SaveChangesAsync</c> — the caller owns the surrounding unit of work.
/// </summary>
public static class ClientConfigArchiveStore
{
    public static async Task UpsertAsync(ApplicationDbContext db, int clientId, string? profileName, string? content)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return;
        var name = profileName.Trim();
        var existing = await db.ClientConfigArchives
            .FirstOrDefaultAsync(a => a.ClientId == clientId && a.ProfileName == name);
        if (existing is null)
        {
            db.ClientConfigArchives.Add(new Models.ClientConfigArchive
            {
                ClientId    = clientId,
                ProfileName = name,
                Content     = content ?? "",
                UpdatedAt   = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Content   = content ?? "";
            existing.UpdatedAt = DateTime.UtcNow;
        }
    }
}
