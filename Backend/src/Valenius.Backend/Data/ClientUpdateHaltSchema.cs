using Microsoft.EntityFrameworkCore;

namespace Valenius.Backend.Data;

public static class ClientUpdateHaltSchema
{
    public static Task ApplyAsync(ApplicationDbContext db) => SchemaChangeRunner.ApplyAsync(db,
        "20261109-01-client-update-halt", """
        ALTER TABLE "ServerSettings" ADD COLUMN IF NOT EXISTS "ClientUpdateHaltEnabled" BOOLEAN NOT NULL DEFAULT FALSE;
        ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "LastDeliveredPolicyJson" TEXT NULL;
        """);
}
