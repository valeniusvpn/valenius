using Microsoft.EntityFrameworkCore;

namespace Valenius.Backend.Data;

public static class TotpAttemptSchema
{
    public static Task ApplyAsync(ApplicationDbContext db) => db.Database.ExecuteSqlRawAsync("""
        ALTER TABLE "AppUsers" ADD COLUMN IF NOT EXISTS "TotpFailureCount" INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE "AppUsers" ADD COLUMN IF NOT EXISTS "TotpFailureWindowEndsAt" TIMESTAMPTZ NULL;
        ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "TotpFailureCount" INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "TotpFailureWindowEndsAt" TIMESTAMPTZ NULL;
        """);
}
