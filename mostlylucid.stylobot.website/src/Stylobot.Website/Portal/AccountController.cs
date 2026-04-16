using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Stylobot.Website.Portal;

/// <summary>
///     Thin controller wrapping the OIDC challenge/signout dance. The actual first-login
///     provisioning (auto-creating a personal org for a new Keycloak sub) happens in
///     <see cref="PortalProvisioningService.OnUserSignedInAsync"/> invoked by the
///     <c>OnTokenValidated</c> event in <c>PortalServiceCollectionExtensions</c>.
/// </summary>
[Route("account")]
public sealed class AccountController : Controller
{
    [HttpGet("login")]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        var redirect = string.IsNullOrEmpty(returnUrl) || !Url.IsLocalUrl(returnUrl)
            ? Url.Action("Index", "Portal") ?? "/portal"
            : returnUrl;

        return Challenge(
            new AuthenticationProperties { RedirectUri = redirect },
            OpenIdConnectDefaults.AuthenticationScheme);
    }

    [HttpGet("logout")]
    [Authorize]
    public IActionResult Logout()
    {
        // Sign out of the local cookie first, then redirect to Keycloak end-session.
        return SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            CookieAuthenticationDefaults.AuthenticationScheme,
            OpenIdConnectDefaults.AuthenticationScheme);
    }

    [HttpGet("denied")]
    [AllowAnonymous]
    public IActionResult Denied() => View();

    /// <summary>
    ///     Lightweight JSON "who am I" endpoint — useful for debugging OIDC claims
    ///     during Phase 1. Removed or guarded before shipping to prod.
    /// </summary>
    [HttpGet("whoami")]
    [Authorize]
    public IActionResult WhoAmI() => Ok(new
    {
        Sub = User.FindFirst("sub")?.Value,
        Email = User.FindFirst("email")?.Value,
        Name = User.FindFirst("name")?.Value ?? User.FindFirst("preferred_username")?.Value,
        Roles = User.FindAll("roles").Select(c => c.Value).ToArray(),
        AllClaims = User.Claims.Select(c => new { c.Type, c.Value }).ToArray()
    });
}
