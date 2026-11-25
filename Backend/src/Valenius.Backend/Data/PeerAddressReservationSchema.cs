using Microsoft.EntityFrameworkCore;

namespace Valenius.Backend.Data;

public static class PeerAddressReservationSchema
{
    public static Task ApplyAsync(ApplicationDbContext db) => SchemaChangeRunner.ApplyAsync(db, "20261029-01-peer-address-reservations", """
        CREATE TABLE IF NOT EXISTS "PeerAddressReservations" (
            "Id" SERIAL PRIMARY KEY, "CustomerId" INTEGER NOT NULL, "ClientId" INTEGER NOT NULL,
            "Foreign" BOOLEAN NOT NULL, "Owner" VARCHAR(100) NOT NULL, "Address" VARCHAR(45) NOT NULL,
            "PublicKey" VARCHAR(50) NOT NULL, "PrivateKey" VARCHAR(50) NOT NULL,
            "CreatedAt" TIMESTAMPTZ NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_PeerAddressReservations_CustomerId_Address"
            ON "PeerAddressReservations" ("CustomerId", "Address");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_PeerAddressReservations_CustomerId_Owner"
            ON "PeerAddressReservations" ("CustomerId", "Owner");
        """);
}
