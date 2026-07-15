using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Models;

namespace Valenius.Backend.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<ConnectionLog> ConnectionLogs => Set<ConnectionLog>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<ErrorLog>   ErrorLogs  => Set<ErrorLog>();
    public DbSet<AuditLog>   AuditLogs  => Set<AuditLog>();
    public DbSet<TrustedNetwork> TrustedNetworks => Set<TrustedNetwork>();
    public DbSet<ServerSettings>    ServerSettings    => Set<ServerSettings>();
    public DbSet<LicenseAuditLog>    LicenseAuditLogs    => Set<LicenseAuditLog>();
    public DbSet<SidecarCertificate> SidecarCertificates => Set<SidecarCertificate>();
    public DbSet<SidecarAlert>      SidecarAlerts     => Set<SidecarAlert>();
    public DbSet<Appliance>         Appliances        => Set<Appliance>();
    public DbSet<ApplianceUpdateLog> ApplianceUpdateLogs => Set<ApplianceUpdateLog>();
    public DbSet<ApplianceRelease>  ApplianceReleases => Set<ApplianceRelease>();
    public DbSet<MfaSession>             MfaSessions           => Set<MfaSession>();
    public DbSet<ClientForeignProfile>   ClientForeignProfiles => Set<ClientForeignProfile>();
    public DbSet<ClientConfigArchive>    ClientConfigArchives  => Set<ClientConfigArchive>();
    public DbSet<ClientLogBundle>        ClientLogBundles      => Set<ClientLogBundle>();
    public DbSet<ClientRelease>          ClientReleases        => Set<ClientRelease>();
    public DbSet<PairingToken>           PairingTokens         => Set<PairingToken>();
    public DbSet<MfaChallenge>           MfaChallenges         => Set<MfaChallenge>();
    public DbSet<ClientTrafficSample>    ClientTrafficSamples  => Set<ClientTrafficSample>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Filtered: allows multiple legacy rows with ClientKey = '' while enforcing uniqueness for real GUIDs.
        modelBuilder.Entity<Client>()
            .HasIndex(c => c.ClientKey)
            .IsUnique()
            .HasFilter("\"ClientKey\" <> ''");
        modelBuilder.Entity<ConnectionLog>()
            .HasOne(l => l.Client)
            .WithMany()
            .HasForeignKey(l => l.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AppUser>().HasIndex(u => u.Subject).IsUnique();

        modelBuilder.Entity<Client>()
            .HasOne(c => c.Customer)
            .WithMany(cu => cu.Clients)
            .HasForeignKey(c => c.CustomerId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<AppUser>()
            .HasOne(u => u.Customer)
            .WithMany(cu => cu.AppUsers)
            .HasForeignKey(u => u.CustomerId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<TrustedNetwork>()
            .HasOne(t => t.Customer)
            .WithMany(c => c.TrustedNetworks)
            .HasForeignKey(t => t.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Appliance>()
            .HasIndex(a => a.DeviceId).IsUnique();
        modelBuilder.Entity<Appliance>()
            .HasOne(a => a.Customer)
            .WithMany()
            .HasForeignKey(a => a.CustomerId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<ApplianceUpdateLog>()
            .HasOne(l => l.Appliance)
            .WithMany(a => a.UpdateLogs)
            .HasForeignKey(l => l.ApplianceId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ApplianceRelease>()
            .HasIndex(r => r.Version).IsUnique();

        // MFA gating: store PeerType as a readable string (matches VARCHAR(10) migration).
        modelBuilder.Entity<Client>()
            .Property(c => c.PeerType)
            .HasConversion<string>()
            .HasMaxLength(10);

        modelBuilder.Entity<MfaSession>()
            .HasOne(s => s.Client)
            .WithMany()
            .HasForeignKey(s => s.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MfaSession>()
            .HasIndex(s => s.SessionId).IsUnique();
        modelBuilder.Entity<MfaSession>()
            .HasIndex(s => new { s.ClientId, s.Status });

        modelBuilder.Entity<ClientForeignProfile>()
            .HasOne(p => p.Client)
            .WithMany(c => c.ForeignProfiles)
            .HasForeignKey(p => p.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ClientForeignProfile>()
            .HasOne(p => p.SourceCustomer)
            .WithMany()
            .HasForeignKey(p => p.SourceCustomerId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ClientForeignProfile>()
            .HasIndex(p => new { p.ClientId, p.SourceCustomerId })
            .IsUnique();

        modelBuilder.Entity<ClientConfigArchive>()
            .HasOne(a => a.Client)
            .WithMany()
            .HasForeignKey(a => a.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ClientConfigArchive>()
            .HasIndex(a => new { a.ClientId, a.ProfileName })
            .IsUnique();

        modelBuilder.Entity<ClientLogBundle>()
            .HasOne(b => b.Client)
            .WithMany()
            .HasForeignKey(b => b.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ClientLogBundle>()
            .HasIndex(b => new { b.ClientId, b.UploadedAt });

        modelBuilder.Entity<ClientRelease>()
            .HasIndex(r => new { r.Os, r.UploadedAt });
    }
}
