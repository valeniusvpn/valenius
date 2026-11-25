using Microsoft.EntityFrameworkCore;

namespace Valenius.Backend.Data;

public static class DeviceCredentialSchema
{
    public static async Task ApplyAsync(ApplicationDbContext db)
    {
        if (!db.Database.IsNpgsql()) return;
        // Deliberately no shared-key/hostname backfill: legacy devices require admin recovery.
        await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "DeviceCredentialHash" VARCHAR(64) NULL""");
        await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "PairingTokens" ADD COLUMN IF NOT EXISTS "ClientKey" VARCHAR(36) NULL""");
    }
}
