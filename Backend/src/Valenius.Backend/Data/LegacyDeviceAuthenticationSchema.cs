using Microsoft.EntityFrameworkCore;

namespace Valenius.Backend.Data;

public static class LegacyDeviceAuthenticationSchema
{
    public static Task ApplyAsync(ApplicationDbContext db) => SchemaChangeRunner.ApplyAsync(db,
        "20261029-03-legacy-device-authentication", """
        ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "LegacyAuthenticationEligible" BOOLEAN NOT NULL DEFAULT FALSE;
        ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "PendingDeviceCredentialHash" VARCHAR(64) NULL;
        ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "PendingDeviceCredentialProtected" VARCHAR(1024) NULL;
        UPDATE "Clients" SET "LegacyAuthenticationEligible" = TRUE
          WHERE "DeviceCredentialHash" IS NULL AND NOT "IsDeleted" AND "ClientKey" <> '';
        """);
}
