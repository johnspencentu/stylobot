using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stylobot.Website.Portal.Data;
using Stylobot.Website.Portal.Licensing;

namespace Stylobot.Website.Portal;

/// <summary>
///     Programmatic license API for CI/CD. Authenticated via personal API token only -
///     cookie auth is not accepted so browser sessions can't accidentally pull tokens.
/// </summary>
[ApiController]
[Route("api/v1/orgs/{slug}/licenses")]
[Authorize(AuthenticationSchemes = ApiTokenAuthenticationHandler.SchemeName)]
public sealed class LicenseApiController : ControllerBase
{
    private readonly PortalDbContext _db;

    public LicenseApiController(PortalDbContext db) => _db = db;

    [HttpGet("current")]
    public async Task<IActionResult> Current(string slug, CancellationToken ct)
    {
        if (!HasScope(ApiTokenScopes.LicensesRead))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "missing_scope", required = ApiTokenScopes.LicensesRead });

        var sub = User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(sub)) return Unauthorized();

        // Verify the token owner is an accepted member of the requested org.
        var orgId = await _db.Members
            .Where(m => m.KeycloakSub == sub
                        && m.AcceptedAt != null
                        && m.Organization!.Slug == slug)
            .Select(m => (Guid?)m.OrganizationId)
            .FirstOrDefaultAsync(ct);

        if (orgId is null) return NotFound(new { error = "org_not_found_or_not_a_member" });

        var license = await _db.Licenses
            .Where(l => l.OrganizationId == orgId.Value
                        && l.RevokedAt == null
                        && l.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(l => l.IssuedAt)
            .FirstOrDefaultAsync(ct);

        if (license is null)
            return NotFound(new { error = "no_active_license" });

        // Mark a download audit - CI/CD pulls are user-initiated even if automated.
        _db.LicenseAudits.Add(new LicenseAudit
        {
            OrganizationId = orgId.Value,
            LicenseId = license.Id,
            Action = "download",
            ActorKeycloakSub = sub,
            ActorEmail = User.FindFirst("email")?.Value,
            Reason = "api-token programmatic retrieval"
        });
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            tier = license.Tier,
            isTrial = license.IsTrial,
            expiresAt = license.ExpiresAt,
            tokenJti = license.TokenJti,
            features = license.Features,
            signedToken = license.SignedToken
        });
    }

    private bool HasScope(string scope)
        => User.Claims.Any(c => c.Type == "scope" && c.Value == scope);
}