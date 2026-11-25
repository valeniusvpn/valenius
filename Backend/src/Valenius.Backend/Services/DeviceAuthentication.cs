using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;

namespace Valenius.Backend.Services;

public sealed class DeviceAuthenticationPolicy
{
    // An explicit, bounded deployment exception. Unset means no shared-key authority.
    public DateTimeOffset? LegacyUntilUtc { get; set; }
    public bool LegacyEnabled => LegacyUntilUtc > DateTimeOffset.UtcNow;

    public void Validate()
    {
        if (LegacyUntilUtc is { } until && (until.Offset != TimeSpan.Zero || until > DateTimeOffset.UtcNow.AddDays(90)))
            throw new InvalidOperationException("DeviceAuthentication:LegacyUntilUtc must be UTC and at most 90 days in the future.");
    }
}

/// <summary>One-way, opt-in legacy migration. The capability header only selects the
/// protocol; it is not proof of identity. Unmigrated devices retain the shared-key risk.</summary>
public sealed class DeviceAuthentication(ApplicationDbContext db, ApiKeyStore keys,
    DeviceAuthenticationPolicy policy, IDataProtectionProvider protection, IAuditService audit)
{
    public const string UpgradeHeader = "X-Valenius-Device-Auth";
    private readonly IDataProtector _protector = protection.CreateProtector("Valenius.DeviceCredentialMigration.v1");

    public bool Accepts(Client client, string? provided) => !client.IsDeleted &&
        (DeviceCredential.Matches(provided, client.DeviceCredentialHash) ||
         (client.DeviceCredentialHash is null && client.LegacyAuthenticationEligible &&
          policy.LegacyEnabled && keys.IsAccepted(provided)));

    public async Task<bool> AuthenticateAsync(Client client, string? provided, CancellationToken ct = default)
    {
        if (client.IsDeleted) return false;
        if (Accepts(client, provided)) return true;
        if (client.DeviceCredentialHash is not null || !client.LegacyAuthenticationEligible ||
            !DeviceCredential.Matches(provided, client.PendingDeviceCredentialHash)) return false;

        // Receipt is insufficient: only a later request using the persisted individual
        // credential disables legacy auth. A lost response/restart can retry the same offer.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var hash = client.PendingDeviceCredentialHash;
        var updated = await db.Clients.Where(c => c.Id == client.Id && c.ClientKey == client.ClientKey && !c.IsDeleted &&
                c.DeviceCredentialHash == null && c.LegacyAuthenticationEligible && c.PendingDeviceCredentialHash == hash)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.DeviceCredentialHash, hash)
                .SetProperty(c => c.LegacyAuthenticationEligible, false)
                .SetProperty(c => c.PendingDeviceCredentialHash, (string?)null)
                .SetProperty(c => c.PendingDeviceCredentialProtected, (string?)null), ct);
        if (updated == 1)
            await audit.SaveChangesWithAuditAsync(db, "Client", "Device credential migration confirmed", client.Id.ToString());
        await transaction.CommitAsync(ct);
        await db.Entry(client).ReloadAsync(ct);
        return db.Entry(client).State != EntityState.Detached && !client.IsDeleted && DeviceCredential.Matches(provided, client.DeviceCredentialHash);
    }

    public async Task<string?> OfferAsync(Client client, string? provided, string capability, CancellationToken ct = default)
    {
        if (capability != "1" || !Accepts(client, provided) || client.DeviceCredentialHash is not null) return null;
        if (client.PendingDeviceCredentialProtected is null)
        {
            var secret = DeviceCredential.Mint();
            // Bind ciphertext to this exact row even if the DB value is accidentally copied.
            var ciphertext = _protector.CreateProtector(client.Id.ToString()).Protect(secret);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var updated = await db.Clients.Where(c => c.Id == client.Id && c.ClientKey == client.ClientKey && !c.IsDeleted &&
                    c.DeviceCredentialHash == null && c.LegacyAuthenticationEligible && c.PendingDeviceCredentialProtected == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.PendingDeviceCredentialHash, DeviceCredential.Hash(secret))
                    .SetProperty(c => c.PendingDeviceCredentialProtected, ciphertext), ct);
            if (updated == 1)
                await audit.SaveChangesWithAuditAsync(db, "Client", "Device credential migration offered", client.Id.ToString());
            await transaction.CommitAsync(ct);
            await db.Entry(client).ReloadAsync(ct);
        }
        // Recheck after a concurrent offer, confirmation, admin rotation or deletion.
        if (db.Entry(client).State == EntityState.Detached || !Accepts(client, provided) || client.DeviceCredentialHash is not null || client.PendingDeviceCredentialProtected is null)
            return null;
        var candidate = _protector.CreateProtector(client.Id.ToString()).Unprotect(client.PendingDeviceCredentialProtected);
        if (!DeviceCredential.Matches(candidate, client.PendingDeviceCredentialHash))
            throw new InvalidOperationException("Invalid device credential migration state; administrator recovery is required.");
        return candidate;
    }
}
