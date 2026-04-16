using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Stylobot.Website.Portal.Data;

namespace Stylobot.Website.Portal;

/// <summary>
///     Handles the "first time this Keycloak user signs in" provisioning:
///     auto-creates a personal org keyed by email and attaches the user as its Owner.
///     Subsequent logins just refresh the cached email/display name on the Member row.
/// </summary>
public sealed class PortalProvisioningService
{
    private readonly PortalDbContext _db;
    private readonly ILogger<PortalProvisioningService> _logger;

    public PortalProvisioningService(PortalDbContext db, ILogger<PortalProvisioningService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task OnUserSignedInAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var sub = principal.FindFirst("sub")?.Value;
        var email = principal.FindFirst("email")?.Value;
        var name = principal.FindFirst("name")?.Value
                   ?? principal.FindFirst("preferred_username")?.Value
                   ?? email;

        if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(email))
        {
            _logger.LogWarning("OIDC sign-in missing sub or email — cannot provision org");
            return;
        }

        // Is this sub already a member of any org? If so, just refresh cached fields.
        var existing = await _db.Members
            .Where(m => m.KeycloakSub == sub)
            .ToListAsync(ct);

        if (existing.Count > 0)
        {
            foreach (var m in existing)
            {
                m.Email = email;
                m.DisplayName = name;
            }
            await _db.SaveChangesAsync(ct);
            return;
        }

        // Is there a pending invite for this email (Member row without a KeycloakSub)?
        var pendingInvite = await _db.Members
            .Where(m => m.Email == email && m.KeycloakSub == null)
            .FirstOrDefaultAsync(ct);

        if (pendingInvite is not null)
        {
            pendingInvite.KeycloakSub = sub;
            pendingInvite.DisplayName = name;
            pendingInvite.AcceptedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Accepted invite for {Email} → org {OrgId}", email, pendingInvite.OrganizationId);
            return;
        }

        // Otherwise: brand new user, create a personal org.
        var slug = SlugifyEmail(email);
        slug = await EnsureUniqueSlugAsync(slug, ct);

        var org = new Organization
        {
            Slug = slug,
            Name = $"{name ?? email}'s workspace",
            BillingEmail = email
        };
        _db.Organizations.Add(org);

        var member = new Member
        {
            OrganizationId = org.Id,
            KeycloakSub = sub,
            Email = email,
            DisplayName = name,
            Role = OrgRole.Owner,
            AcceptedAt = DateTime.UtcNow
        };
        _db.Members.Add(member);

        _db.LicenseAudits.Add(new LicenseAudit
        {
            OrganizationId = org.Id,
            Action = "org-created",
            ActorKeycloakSub = sub,
            ActorEmail = email,
            Reason = "auto-provisioned on first login"
        });

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Provisioned new org {Slug} for {Email}", slug, email);
    }

    private static string SlugifyEmail(string email)
    {
        // Take the local-part, lowercase, strip non-slug chars.
        var local = email.Split('@')[0].ToLowerInvariant();
        var slug = Regex.Replace(local, "[^a-z0-9-]+", "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "user" : slug;
    }

    private async Task<string> EnsureUniqueSlugAsync(string baseSlug, CancellationToken ct)
    {
        var slug = baseSlug;
        var n = 2;
        while (await _db.Organizations.AnyAsync(o => o.Slug == slug, ct))
        {
            slug = $"{baseSlug}-{n}";
            n++;
        }
        return slug;
    }
}
