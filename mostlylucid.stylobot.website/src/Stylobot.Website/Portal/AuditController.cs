using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stylobot.Website.Portal.Data;

namespace Stylobot.Website.Portal;

/// <summary>
///     SOC 2-grade audit log UI. Shows the append-only LicenseAudit stream for an org.
///     Read-only - audits are written by other controllers' actions; nothing here mutates.
/// </summary>
[Route("portal/org/{slug}/audit")]
[Authorize(Policy = PortalServiceCollectionExtensions.PortalAuthorizationPolicy)]
public sealed class AuditController : Controller
{
    private readonly PortalDbContext _db;
    private const int PageSize = 50;

    public AuditController(PortalDbContext db) => _db = db;

    [HttpGet("")]
    public async Task<IActionResult> Index(string slug, int page = 1, CancellationToken ct = default)
    {
        if (page < 1) page = 1;

        var sub = User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(sub)) return NotFound();

        var org = await _db.Members
            .Where(m => m.Organization!.Slug == slug
                        && m.KeycloakSub == sub
                        && m.AcceptedAt != null)
            .Select(m => m.Organization!)
            .FirstOrDefaultAsync(ct);
        if (org is null) return NotFound();

        var totalCount = await _db.LicenseAudits.CountAsync(a => a.OrganizationId == org.Id, ct);

        var audits = await _db.LicenseAudits
            .Where(a => a.OrganizationId == org.Id)
            .OrderByDescending(a => a.At)
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(ct);

        var totalPages = (int)Math.Ceiling(totalCount / (double)PageSize);

        return View(new AuditIndexViewModel(org, audits, page, totalPages, totalCount));
    }
}

public sealed record AuditIndexViewModel(
    Organization Org,
    IReadOnlyList<LicenseAudit> Audits,
    int Page,
    int TotalPages,
    int TotalCount);