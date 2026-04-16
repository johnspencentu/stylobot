namespace Stylobot.Website.Portal.Data;

/// <summary>
///     A customer organization / tenant. Licenses attach to orgs, not users — so team
///     members can rotate without invalidating licenses. A user auto-creates a personal
///     org on first login; they can create more orgs and be invited to orgs they don't own.
/// </summary>
public sealed class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>URL slug (e.g., "acme-corp") — unique, used in /portal/org/{slug} routes.</summary>
    public required string Slug { get; set; }

    /// <summary>Display name.</summary>
    public required string Name { get; set; }

    /// <summary>Optional billing email; defaults to owner's Keycloak email if null.</summary>
    public string? BillingEmail { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Member> Members { get; set; } = new List<Member>();
    public ICollection<License> Licenses { get; set; } = new List<License>();
    public ICollection<LicenseAudit> Audits { get; set; } = new List<LicenseAudit>();
}

/// <summary>Role a user has within an organization. Ordered least-to-most privileged.</summary>
public enum OrgRole
{
    Viewer = 0,
    Developer = 10,
    Admin = 20,
    Owner = 30
}

/// <summary>
///     Join-table between a Keycloak-authenticated user and an Organization.
///     We do NOT store Keycloak users ourselves — only their <c>sub</c> claim plus
///     a cached email for convenience (so invite flows can show "Invite alice@example.com"
///     before the invitee has actually logged in).
/// </summary>
public sealed class Member
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>
    ///     Keycloak <c>sub</c> claim (stable per user, opaque). May be null for pending
    ///     invites — the invitee hasn't logged in yet, so we only know their email.
    /// </summary>
    public string? KeycloakSub { get; set; }

    /// <summary>Cached email — populated on first login from Keycloak's <c>email</c> claim.</summary>
    public required string Email { get; set; }

    /// <summary>Cached display name — populated from <c>name</c> claim on login.</summary>
    public string? DisplayName { get; set; }

    public required OrgRole Role { get; set; }

    public DateTime InvitedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AcceptedAt { get; set; }

    public bool IsActive => AcceptedAt.HasValue;
}

/// <summary>
///     A signed license token issued to an organization. Trial licenses are just regular
///     licenses with <c>IsTrial=true</c> and a 30-day expiry.
/// </summary>
public sealed class License
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>StyloBot tier (<c>oss</c> / <c>startup</c> / <c>sme</c> / <c>enterprise</c>).</summary>
    public required string Tier { get; set; }

    /// <summary>Is this a trial license (one-per-org, 30 day default)?</summary>
    public bool IsTrial { get; set; }

    /// <summary>Feature flags granted (stored as JSON array in Postgres).</summary>
    public List<string> Features { get; set; } = new();

    /// <summary>
    ///     Optional hardware fingerprint binding — customer provides it at trial request
    ///     time for installs that want extra binding, otherwise null (portable license).
    /// </summary>
    public string? HardwareFingerprint { get; set; }

    /// <summary>JWT <c>jti</c> — unique per token, used for revocation lookups.</summary>
    public required string TokenJti { get; set; }

    /// <summary>The signed JWT itself — stored so we can re-show "download" later.</summary>
    public required string SignedToken { get; set; }

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public required DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTime.UtcNow;
}

/// <summary>Append-only record of license lifecycle events — SOC 2 evidence.</summary>
public sealed class LicenseAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>License this audit pertains to (null for org-level actions).</summary>
    public Guid? LicenseId { get; set; }

    /// <summary><c>issue</c> / <c>revoke</c> / <c>rotate</c> / <c>download</c> / <c>member-invite</c> / <c>member-remove</c></summary>
    public required string Action { get; set; }

    /// <summary>Keycloak sub of the actor performing the action.</summary>
    public required string ActorKeycloakSub { get; set; }

    public string? ActorEmail { get; set; }

    /// <summary>Free-text reason (e.g., "trial request", "fingerprint update", "team removed member").</summary>
    public string? Reason { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;
}

/// <summary>
///     Personal API token for programmatic license retrieval (e.g., CI/CD downloading the
///     current license JWT at deploy time). Scoped to orgs the user is a member of.
/// </summary>
public sealed class ApiToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string KeycloakSub { get; set; }

    public required string Name { get; set; }

    /// <summary>SHA256(token) — the plaintext is shown once at creation and never stored.</summary>
    public required string TokenHash { get; set; }

    /// <summary>Scopes: <c>licenses:read</c>, <c>licenses:rotate</c>, etc.</summary>
    public List<string> Scopes { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public bool IsActive => RevokedAt is null;
}
