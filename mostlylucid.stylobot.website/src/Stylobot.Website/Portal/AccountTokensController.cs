using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stylobot.Website.Portal.Data;
using Stylobot.Website.Portal.Licensing;

namespace Stylobot.Website.Portal;

/// <summary>
///     Personal API tokens for programmatic license retrieval (CI/CD). Tokens are scoped
///     to the user, not the org — a single token can fetch licenses for any org the user
///     is an accepted member of. Plaintext is shown exactly once, at creation.
/// </summary>
[Route("account/tokens")]
[Authorize(Policy = PortalServiceCollectionExtensions.PortalAuthorizationPolicy)]
public sealed class AccountTokensController : Controller
{
    private readonly PortalDbContext _db;

    public AccountTokensController(PortalDbContext db) => _db = db;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var sub = User.FindFirst("sub")!.Value;
        var tokens = await _db.ApiTokens
            .Where(t => t.KeycloakSub == sub)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

        return View(new TokensIndexViewModel(
            Email: User.FindFirst("email")?.Value ?? "",
            Tokens: tokens,
            JustCreatedPlaintext: TempData["JustCreatedPlaintext"] as string,
            JustCreatedName: TempData["JustCreatedName"] as string));
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, string[]? scopes, CancellationToken ct)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            TempData["TokenError"] = "Give the token a descriptive name (e.g., \"prod CI\" or \"staging deploy\").";
            return RedirectToAction(nameof(Index));
        }

        var validScopes = (scopes ?? Array.Empty<string>())
            .Where(s => ApiTokenScopes.All.Any(a => a.Key == s))
            .Distinct()
            .ToList();

        if (validScopes.Count == 0)
            validScopes.Add(ApiTokenScopes.LicensesRead);

        var plaintext = ApiTokenAuthenticationHandler.GenerateToken();
        var hash = ApiTokenAuthenticationHandler.HashToken(plaintext);

        _db.ApiTokens.Add(new ApiToken
        {
            KeycloakSub = User.FindFirst("sub")!.Value,
            Name = name,
            TokenHash = hash,
            Scopes = validScopes
        });
        await _db.SaveChangesAsync(ct);

        TempData["JustCreatedPlaintext"] = plaintext;
        TempData["JustCreatedName"] = name;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var sub = User.FindFirst("sub")!.Value;
        var token = await _db.ApiTokens
            .FirstOrDefaultAsync(t => t.Id == id && t.KeycloakSub == sub, ct);
        if (token is null) return NotFound();
        if (token.RevokedAt is null)
        {
            token.RevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return RedirectToAction(nameof(Index));
    }
}

public sealed record TokensIndexViewModel(
    string Email,
    IReadOnlyList<ApiToken> Tokens,
    string? JustCreatedPlaintext,
    string? JustCreatedName);
