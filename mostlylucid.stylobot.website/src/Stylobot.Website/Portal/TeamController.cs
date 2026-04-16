using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stylobot.Website.Portal.Data;

namespace Stylobot.Website.Portal;

/// <summary>
///     Team management: list members, invite by email, change role, remove. Pending invites
///     (<see cref="Member.KeycloakSub"/> null) get auto-accepted in
///     <see cref="PortalProvisioningService.OnUserSignedInAsync"/> when the invitee first
///     logs into Keycloak — the provisioning service detects a pending Member row by email
///     and binds it to the new sub.
/// </summary>
[Route("portal/org/{slug}/team")]
[Authorize(Policy = PortalServiceCollectionExtensions.PortalAuthorizationPolicy)]
public sealed class TeamController : Controller
{
    private readonly PortalDbContext _db;

    public TeamController(PortalDbContext db) => _db = db;

    [HttpGet("")]
    public async Task<IActionResult> Index(string slug, CancellationToken ct)
    {
        var ctx = await LoadAsync(slug, ct);
        if (ctx is null) return NotFound();

        var members = await _db.Members
            .Where(m => m.OrganizationId == ctx.Org.Id)
            .OrderByDescending(m => m.Role)
            .ThenBy(m => m.Email)
            .ToListAsync(ct);

        return View(new TeamIndexViewModel(ctx.Org, ctx.Me, members));
    }

    [HttpPost("invite")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Invite(string slug, string email, string role, CancellationToken ct)
    {
        var ctx = await LoadAsync(slug, ct);
        if (ctx is null) return NotFound();
        if (ctx.Me.Role < OrgRole.Admin) return Forbid();

        email = email?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            TempData["TeamError"] = "A valid email address is required.";
            return RedirectToAction(nameof(Index), new { slug });
        }

        if (!Enum.TryParse<OrgRole>(role, ignoreCase: true, out var parsedRole))
            parsedRole = OrgRole.Developer;

        // Admins can't create Owners; only an existing Owner can grant Owner.
        if (parsedRole == OrgRole.Owner && ctx.Me.Role < OrgRole.Owner)
            parsedRole = OrgRole.Admin;

        var existing = await _db.Members
            .FirstOrDefaultAsync(m => m.OrganizationId == ctx.Org.Id && m.Email == email, ct);

        if (existing is not null)
        {
            TempData["TeamError"] = "That email is already a member or has a pending invite.";
            return RedirectToAction(nameof(Index), new { slug });
        }

        _db.Members.Add(new Member
        {
            OrganizationId = ctx.Org.Id,
            Email = email,
            Role = parsedRole
            // KeycloakSub and AcceptedAt stay null — filled by provisioning on first login.
        });

        _db.LicenseAudits.Add(new LicenseAudit
        {
            OrganizationId = ctx.Org.Id,
            Action = "member-invite",
            ActorKeycloakSub = User.FindFirst("sub")!.Value,
            ActorEmail = User.FindFirst("email")?.Value,
            Reason = $"invited {email} as {parsedRole}"
        });

        await _db.SaveChangesAsync(ct);

        // TODO Phase 1c+: send an email to the invitee. For now, the invite is visible
        // the next time they log into Keycloak with that email.
        TempData["TeamSuccess"] = $"Invited {email} as {parsedRole}.";
        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("{memberId:guid}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(string slug, Guid memberId, CancellationToken ct)
    {
        var ctx = await LoadAsync(slug, ct);
        if (ctx is null) return NotFound();
        if (ctx.Me.Role < OrgRole.Admin) return Forbid();

        var member = await _db.Members
            .FirstOrDefaultAsync(m => m.Id == memberId && m.OrganizationId == ctx.Org.Id, ct);
        if (member is null) return NotFound();

        // Can't remove yourself, can't remove the last Owner.
        if (member.Id == ctx.Me.Id)
        {
            TempData["TeamError"] = "You can't remove yourself. Transfer ownership first, then leave.";
            return RedirectToAction(nameof(Index), new { slug });
        }

        if (member.Role == OrgRole.Owner)
        {
            var otherOwners = await _db.Members.CountAsync(
                m => m.OrganizationId == ctx.Org.Id && m.Role == OrgRole.Owner && m.Id != member.Id, ct);
            if (otherOwners == 0)
            {
                TempData["TeamError"] = "Can't remove the last Owner. Promote someone else first.";
                return RedirectToAction(nameof(Index), new { slug });
            }
        }

        _db.Members.Remove(member);
        _db.LicenseAudits.Add(new LicenseAudit
        {
            OrganizationId = ctx.Org.Id,
            Action = "member-remove",
            ActorKeycloakSub = User.FindFirst("sub")!.Value,
            ActorEmail = User.FindFirst("email")?.Value,
            Reason = $"removed {member.Email}"
        });
        await _db.SaveChangesAsync(ct);

        TempData["TeamSuccess"] = $"Removed {member.Email}.";
        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("{memberId:guid}/role")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeRole(string slug, Guid memberId, string role, CancellationToken ct)
    {
        var ctx = await LoadAsync(slug, ct);
        if (ctx is null) return NotFound();
        if (ctx.Me.Role < OrgRole.Admin) return Forbid();

        if (!Enum.TryParse<OrgRole>(role, ignoreCase: true, out var parsedRole))
            return BadRequest();

        // Only Owners can create Owners.
        if (parsedRole == OrgRole.Owner && ctx.Me.Role < OrgRole.Owner) return Forbid();

        var member = await _db.Members
            .FirstOrDefaultAsync(m => m.Id == memberId && m.OrganizationId == ctx.Org.Id, ct);
        if (member is null) return NotFound();

        // Can't demote the last Owner.
        if (member.Role == OrgRole.Owner && parsedRole != OrgRole.Owner)
        {
            var otherOwners = await _db.Members.CountAsync(
                m => m.OrganizationId == ctx.Org.Id && m.Role == OrgRole.Owner && m.Id != member.Id, ct);
            if (otherOwners == 0)
            {
                TempData["TeamError"] = "Can't demote the last Owner.";
                return RedirectToAction(nameof(Index), new { slug });
            }
        }

        var oldRole = member.Role;
        member.Role = parsedRole;
        _db.LicenseAudits.Add(new LicenseAudit
        {
            OrganizationId = ctx.Org.Id,
            Action = "member-role-change",
            ActorKeycloakSub = User.FindFirst("sub")!.Value,
            ActorEmail = User.FindFirst("email")?.Value,
            Reason = $"{member.Email}: {oldRole} → {parsedRole}"
        });
        await _db.SaveChangesAsync(ct);

        return RedirectToAction(nameof(Index), new { slug });
    }

    private async Task<OrgContext?> LoadAsync(string slug, CancellationToken ct)
    {
        var sub = User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(sub)) return null;

        var member = await _db.Members
            .Include(m => m.Organization)
            .Where(m => m.Organization!.Slug == slug
                        && m.KeycloakSub == sub
                        && m.AcceptedAt != null)
            .FirstOrDefaultAsync(ct);

        return member?.Organization is null ? null : new OrgContext(member.Organization, member);
    }

    private sealed record OrgContext(Organization Org, Member Me);
}

public sealed record TeamIndexViewModel(Organization Org, Member Me, IReadOnlyList<Member> Members);
