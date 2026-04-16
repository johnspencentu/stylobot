using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Stylobot.Website.Portal.Data;

namespace Stylobot.Website.Portal;

/// <summary>
///     Wires the customer portal into the site: Postgres DbContext, OIDC relying-party
///     against Keycloak, cookie session, and authorization policies.
/// </summary>
public static class PortalServiceCollectionExtensions
{
    public const string PortalAuthorizationPolicy = "PortalUser";
    public const string CookieAuthScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string OidcAuthScheme = OpenIdConnectDefaults.AuthenticationScheme;

    /// <summary>
    ///     Adds portal services when enabled. Safe to call in all environments — if
    ///     <c>Portal:Enabled</c> is false (default), nothing is wired.
    /// </summary>
    public static IServiceCollection AddStyloBotPortal(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var opts = new PortalOptions();
        configuration.GetSection("Portal").Bind(opts);
        services.Configure<PortalOptions>(configuration.GetSection("Portal"));

        if (!opts.Enabled)
            return services;

        if (string.IsNullOrWhiteSpace(opts.ConnectionString))
            throw new InvalidOperationException(
                "Portal:Enabled=true but Portal:ConnectionString is empty. " +
                "Set Portal__ConnectionString environment variable.");

        // Postgres DbContext for orgs, members, licenses, audit.
        services.AddDbContext<PortalDbContext>(db =>
            db.UseNpgsql(opts.ConnectionString, npg =>
                npg.MigrationsAssembly(typeof(PortalDbContext).Assembly.FullName)));

        // Stop the default claim-type remapper so we see Keycloak's raw claim names
        // (<c>sub</c>, <c>email</c>, <c>preferred_username</c>) rather than the SOAP-era URIs
        // ASP.NET Core defaults to for back-compat.
        JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

        services.AddAuthentication(o =>
            {
                o.DefaultScheme = CookieAuthScheme;
                o.DefaultChallengeScheme = OidcAuthScheme;
                o.DefaultSignOutScheme = OidcAuthScheme;
            })
            .AddCookie(CookieAuthScheme, c =>
            {
                c.Cookie.Name = "stylobot.portal";
                c.Cookie.HttpOnly = true;
                c.Cookie.SameSite = SameSiteMode.Lax;
                c.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                c.ExpireTimeSpan = TimeSpan.FromHours(8);
                c.SlidingExpiration = true;
                c.LoginPath = "/account/login";
                c.LogoutPath = "/account/logout";
                c.AccessDeniedPath = "/account/denied";
            })
            .AddOpenIdConnect(OidcAuthScheme, o =>
            {
                o.Authority = opts.Oidc.Authority;
                o.ClientId = opts.Oidc.ClientId;
                o.ClientSecret = opts.Oidc.ClientSecret;
                o.RequireHttpsMetadata = opts.Oidc.RequireHttpsMetadata;

                // Authorization-code flow with PKCE — the modern default for server-side RPs.
                o.ResponseType = OpenIdConnectResponseType.Code;
                o.UsePkce = true;

                o.Scope.Clear();
                o.Scope.Add("openid");
                o.Scope.Add("profile");
                o.Scope.Add("email");
                foreach (var extra in opts.Oidc.ExtraScopes)
                    o.Scope.Add(extra);

                // Keycloak returns richer claims on the userinfo endpoint; pull them so we
                // can show display name + verify email before first-login org provisioning.
                o.GetClaimsFromUserInfoEndpoint = true;
                o.SaveTokens = true;

                o.CallbackPath = "/account/callback";
                o.SignedOutCallbackPath = "/account/signout-callback";

                o.TokenValidationParameters.NameClaimType = "preferred_username";
                o.TokenValidationParameters.RoleClaimType = "roles";

                // On first successful login, provision a personal org if this is a brand
                // new Keycloak sub. Runs per login — cheap idempotent upsert after the
                // first time.
                o.Events = new OpenIdConnectEvents
                {
                    OnTokenValidated = async ctx =>
                    {
                        var provisioning = ctx.HttpContext.RequestServices
                            .GetRequiredService<PortalProvisioningService>();
                        await provisioning.OnUserSignedInAsync(
                            ctx.Principal ?? new System.Security.Claims.ClaimsPrincipal(),
                            ctx.HttpContext.RequestAborted);
                    }
                };
            });

        services.AddScoped<PortalProvisioningService>();

        services.AddAuthorization(a =>
        {
            // Baseline: signed in and has the portal-user realm role. Keycloak's default
            // role mapper puts realm roles into a "roles" claim array; the OIDC handler
            // above is configured to treat "roles" as the RoleClaimType.
            a.AddPolicy(PortalAuthorizationPolicy, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx =>
                    ctx.User.HasClaim(c => c.Type == "roles" && c.Value == "portal-user") ||
                    ctx.User.IsInRole("portal-user")));
        });

        return services;
    }

    /// <summary>
    ///     Apply migrations on startup if <c>Portal:AutoMigrate</c> is true. Call from
    ///     the request pipeline configuration once <c>app.Build()</c> has completed.
    /// </summary>
    public static async Task ApplyPortalMigrationsAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var opts = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PortalOptions>>().Value;
        if (!opts.Enabled || !opts.AutoMigrate) return;

        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        await db.Database.MigrateAsync();
    }
}
