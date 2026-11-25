using Microsoft.EntityFrameworkCore;

namespace Valenius.Backend.Data;

/// <summary>Shared schema only; Pro owns token issuance and redemption.</summary>
public static class SidecarEnrollmentSchema
{
    public static Task ApplyAsync(ApplicationDbContext db) => db.Database.ExecuteSqlRawAsync("""
        ALTER TABLE "Customers" ADD COLUMN IF NOT EXISTS "EnrollmentToken" VARCHAR(128) NULL;
        ALTER TABLE "Customers" ADD COLUMN IF NOT EXISTS "EnrollmentTokenHash" VARCHAR(64) NULL;
        ALTER TABLE "Customers" ADD COLUMN IF NOT EXISTS "EnrollmentTokenCreatedAt" TIMESTAMPTZ NULL;
        ALTER TABLE "Customers" ADD COLUMN IF NOT EXISTS "EnrollmentTokenExpiresAt" TIMESTAMPTZ NULL;
        UPDATE "Customers" SET "EnrollmentToken" = NULL WHERE "EnrollmentToken" IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Customers_EnrollmentTokenHash" ON "Customers" ("EnrollmentTokenHash");
        """);
}
