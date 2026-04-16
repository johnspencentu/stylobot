using Microsoft.EntityFrameworkCore;

namespace Stylobot.Website.Portal.Data;

/// <summary>
///     Portal database — organizations, memberships, licenses, audit log, API tokens.
///     Deliberately does NOT include a Users table: Keycloak owns user identity and we
///     just key by <c>sub</c> claim. Every row in <see cref="Members"/> and
///     <see cref="ApiTokens"/> references a Keycloak sub string.
/// </summary>
public sealed class PortalDbContext : DbContext
{
    public PortalDbContext(DbContextOptions<PortalDbContext> options) : base(options) { }

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<License> Licenses => Set<License>();
    public DbSet<LicenseAudit> LicenseAudits => Set<LicenseAudit>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Organization>(e =>
        {
            e.ToTable("organizations");
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.BillingEmail).HasMaxLength(256);
        });

        b.Entity<Member>(e =>
        {
            e.ToTable("members");
            e.HasOne(x => x.Organization).WithMany(o => o.Members).HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.KeycloakSub).HasMaxLength(128);
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(256);
            // A given Keycloak sub can only be a member of an org once.
            e.HasIndex(x => new { x.OrganizationId, x.KeycloakSub }).IsUnique()
                .HasFilter("\"KeycloakSub\" IS NOT NULL");
            // Also prevent duplicate invites (pending, pre-login) by email.
            e.HasIndex(x => new { x.OrganizationId, x.Email }).IsUnique();
        });

        b.Entity<License>(e =>
        {
            e.ToTable("licenses");
            e.HasOne(x => x.Organization).WithMany(o => o.Licenses).HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Tier).HasMaxLength(32).IsRequired();
            e.Property(x => x.TokenJti).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.TokenJti).IsUnique();
            e.Property(x => x.HardwareFingerprint).HasMaxLength(64);
            // Store feature flags as a JSON array column (native jsonb in Npgsql).
            e.Property(x => x.Features).HasColumnType("jsonb");
            // Token body can be several KB — text column is fine.
            e.Property(x => x.SignedToken).HasColumnType("text").IsRequired();
            e.HasIndex(x => new { x.OrganizationId, x.RevokedAt });
        });

        b.Entity<LicenseAudit>(e =>
        {
            e.ToTable("license_audits");
            e.HasOne(x => x.Organization).WithMany(o => o.Audits).HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Action).HasMaxLength(64).IsRequired();
            e.Property(x => x.ActorKeycloakSub).HasMaxLength(128).IsRequired();
            e.Property(x => x.ActorEmail).HasMaxLength(256);
            e.Property(x => x.Reason).HasMaxLength(1024);
            e.HasIndex(x => new { x.OrganizationId, x.At });
        });

        b.Entity<ApiToken>(e =>
        {
            e.ToTable("api_tokens");
            e.Property(x => x.KeycloakSub).HasMaxLength(128).IsRequired();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.Scopes).HasColumnType("jsonb");
            e.HasIndex(x => x.KeycloakSub);
        });
    }
}
