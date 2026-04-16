using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stylobot.Website.Portal.Data;

namespace Stylobot.Website.Portal;

/// <summary>
///     Phase 1a landing page: lists the orgs the signed-in user belongs to and offers
///     a link into each. Licensing UI lands in Phase 1b.
/// </summary>
[Route("portal")]
[Authorize(Policy = PortalServiceCollectionExtensions.PortalAuthorizationPolicy)]
public sealed class PortalController : Controller
{
    private readonly PortalDbContext _db;

    public PortalController(PortalDbContext db)
    {
        _db = db;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var sub = User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(sub)) return Forbid();

        var orgs = await _db.Members
            .Where(m => m.KeycloakSub == sub && m.AcceptedAt != null)
            .Include(m => m.Organization)
            .Select(m => new OrgSummary(
                m.Organization!.Slug,
                m.Organization.Name,
                m.Role.ToString(),
                m.Organization.Licenses.Count(l => l.RevokedAt == null && l.ExpiresAt > DateTime.UtcNow)))
            .ToListAsync(ct);

        return View(new PortalIndexViewModel(
            Email: User.FindFirst("email")?.Value ?? "",
            DisplayName: User.FindFirst("name")?.Value ?? User.FindFirst("preferred_username")?.Value ?? "",
            Orgs: orgs));
    }
}

public sealed record OrgSummary(string Slug, string Name, string Role, int ActiveLicenses);
public sealed record PortalIndexViewModel(string Email, string DisplayName, IReadOnlyList<OrgSummary> Orgs);
