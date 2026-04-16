using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stylobot.Website.Portal.Data;
using Stylobot.Website.Portal.Licensing;

namespace Stylobot.Website.Portal;

/// <summary>
///     Per-org routes: overview, trial request, license list, download, revoke.
///     All actions authorize against membership in the org - a user must have an
///     accepted <see cref="Member"/> row for the org or they get 404.
/// </summary>
[Route("portal/org/{slug}")]
[Authorize(Policy = PortalServiceCollectionExtensions.PortalAuthorizationPolicy)]
public sealed class OrgController : Controller
{
    private readonly PortalDbContext _db;
    private readonly LicenseIssuer _issuer;

    public OrgController(PortalDbContext db, LicenseIssuer issuer)
    {
        _db = db;
        _issuer = issuer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Overview(string slug, CancellationToken ct)
    {
        var ctx = await LoadAsync(slug, ct);
        if (ctx is null) return NotFound();

        var licenses = await _db.Licenses
            .Where(l => l.OrganizationId == ctx.Org.Id)
            .OrderByDescending(l => l.IssuedAt)
            .ToListAsync(ct);

        return View(new OrgOverviewViewModel(ctx.Org, ctx.Member, licenses));
    }

    [HttpPost("trial")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestTrial(
        string slug, string? hardwareFingerprint, string? primaryDomain, CancellationToken ct)
    {
        var ctx = await LoadAsync(slug, ct);
        if (ctx is null) return NotFound();
        if (ctx.Member.Role < OrgRole.Admin) return Forbid();

        var sub = User.FindFirst("sub")!.Value;
        var email = User.FindFirst("email")?.Value;

        // Primary domain is the license entitlement boundary - see licensing-simplified.md.
        // Parse comma / newline-separated input so operators can enter one or several
        // (e.g., "acme.com, acme.io" for multi-brand). Trial starts with Startup-tier
        // entitlement (1 primary domain); additional domains can be added in the
        // license-management UI later.
        var domains = ParseDomains(primaryDomain);

        var result = await _issuer.IssueTrialAsync(
            ctx.Org,
            actorSub: sub,
            actorEmail: email,
            hardwareFingerprint: string.IsNullOrWhiteSpace(hardwareFingerprint) ? null : hardwareFingerprint.Trim(),
            domains: domains,
            ct);

        if (!result.Succeeded)
        {
            TempData["TrialError"] = result.Reason == "trial_already_consumed"
                ? "This organization has already used its one trial. Contact sales for an extension."
                : result.Reason;
            return RedirectToAction(nameof(Overview), new { slug });
        }

        return RedirectToAction(nameof(LicenseIssued), new { slug, id = result.License!.Id });
    }

    private static IReadOnlyList<string> ParseDomains(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw
            .Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    [HttpGet("licenses/{id:guid}/issued")]
    public async Task<IActionResult> LicenseIssued(string slug, Guid id, CancellationToken ct)
    {
        var ctx = await LoadAsync(slug, ct);
        if (ctx is null) return NotFound();

        var license = await _db.Licenses.FirstOrDefaultAsync(
            l => l.Id == id && l.OrganizationId == ctx.Org.Id, ct);
        if (license is null) return NotFound();

        return View(new LicenseIssuedViewModel(ctx.Org, license));
    }

    [HttpGet("licenses/{id:guid}/download")]
    public async Task<IActionResult> DownloadLicense(string slug, Guid id, CancellationToken ct)
    {
        var ctx = await LoadAsync(slug, ct);
        if (ctx is null) return NotFound();

        var license = await _db.Licenses.FirstOrDefaultAsync(
            l => l.Id == id && l.OrganizationId == ctx.Org.Id, ct);
        if (license is null) return NotFound();

        _db.LicenseAudits.Add(new LicenseAudit
        {
            OrganizationId = ctx.Org.Id,
            LicenseId = license.Id,
            Action = "download",
            ActorKeycloakSub = User.FindFirst("sub")!.Value,
            ActorEmail = User.FindFirst("email")?.Value
        });
        await _db.SaveChangesAsync(ct);

        var fileName = $"stylobot-{ctx.Org.Slug}-{(license.IsTrial ? "trial" : license.Tier)}.license";
        return File(
            System.Text.Encoding.UTF8.GetBytes(license.SignedToken),
            "application/json",
            fileName);
    }

    [HttpPost("licenses/{id:guid}/revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeLicense(string slug, Guid id, string? reason, CancellationToken ct)
    {
        var ctx = await LoadAsync(slug, ct);
        if (ctx is null) return NotFound();
        if (ctx.Member.Role < OrgRole.Admin) return Forbid();

        var license = await _db.Licenses.FirstOrDefaultAsync(
            l => l.Id == id && l.OrganizationId == ctx.Org.Id, ct);
        if (license is null) return NotFound();

        await _issuer.RevokeAsync(
            license,
            actorSub: User.FindFirst("sub")!.Value,
            actorEmail: User.FindFirst("email")?.Value,
            reason: reason,
            ct);

        return RedirectToAction(nameof(Overview), new { slug });
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

    private sealed record OrgContext(Organization Org, Member Member);
}

public sealed record OrgOverviewViewModel(Organization Org, Member Me, IReadOnlyList<License> Licenses);
public sealed record LicenseIssuedViewModel(Organization Org, License License);