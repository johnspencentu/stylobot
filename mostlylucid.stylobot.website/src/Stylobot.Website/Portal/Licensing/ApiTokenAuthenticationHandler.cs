using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stylobot.Website.Portal.Data;

namespace Stylobot.Website.Portal.Licensing;

/// <summary>
///     Accepts <c>Authorization: Bearer sbk_&lt;secret&gt;</c> headers. The secret's SHA256
///     hash is looked up in <see cref="PortalDbContext.ApiTokens"/>; on match we mint a
///     ClaimsPrincipal with the token owner's sub + the token's scopes.
///
///     Tokens are stored hashed — the plaintext is only visible to the user at creation
///     time. <see cref="AccountTokensController"/> generates the plaintext, then only the
///     hash is persisted.
/// </summary>
public sealed class ApiTokenAuthenticationHandler : AuthenticationHandler<ApiTokenOptions>
{
    public const string SchemeName = "StylobotApiKey";
    public const string TokenPrefix = "sbk_";

    private readonly PortalDbContext _db;

    public ApiTokenAuthenticationHandler(
        IOptionsMonitor<ApiTokenOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        PortalDbContext db)
        : base(options, logger, encoder)
    {
        _db = db;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var auth) || auth.Count == 0)
            return AuthenticateResult.NoResult();

        var header = auth.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var raw = header["Bearer ".Length..].Trim();
        if (!raw.StartsWith(TokenPrefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult(); // Not our scheme — let others handle it.

        var hash = HashToken(raw);

        var token = await _db.ApiTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null);

        if (token is null)
            return AuthenticateResult.Fail("Invalid or revoked API token");

        // Touch LastUsedAt — cheap, helps UI show "stale" tokens.
        token.LastUsedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var claims = new List<Claim>
        {
            new("sub", token.KeycloakSub),
            new("token_id", token.Id.ToString()),
            new("auth_type", "api-token")
        };
        foreach (var scope in token.Scopes)
            claims.Add(new Claim("scope", scope));

        var identity = new ClaimsIdentity(claims, SchemeName, "sub", "roles");
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    /// <summary>Hash an API token plaintext for storage / lookup. SHA256, hex-encoded.</summary>
    public static string HashToken(string plaintext)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    ///     Generate a fresh API token plaintext. Format: <c>sbk_&lt;43 url-safe chars&gt;</c>
    ///     = 32 bytes of cryptographic randomness, base64url-encoded without padding.
    /// </summary>
    public static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var base64 = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return TokenPrefix + base64;
    }
}

public sealed class ApiTokenOptions : AuthenticationSchemeOptions { }

/// <summary>Canonical scope names. Keep in sync with the AccountTokensController UI.</summary>
public static class ApiTokenScopes
{
    public const string LicensesRead = "licenses:read";
    public const string LicensesRotate = "licenses:rotate";

    public static readonly IReadOnlyList<(string Key, string Label, string Description)> All =
    [
        (LicensesRead, "Read licenses", "View active licenses and download signed tokens for orgs you belong to."),
        (LicensesRotate, "Rotate licenses", "Revoke + re-issue licenses. Required for automated cert-rotation workflows.")
    ];
}
