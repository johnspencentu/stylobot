using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Stylobot.Website.Portal.Data;
using Stylobot.Website.Portal.Licensing;

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
    ///     Adds portal services when enabled. Safe to call in all environments - if
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
        // ASP.NET Core defaults to for back-compat. This must happen BEFORE any handler
        // is constructed. Both the legacy JwtSecurityTokenHandler and the newer
        // JsonWebTokenHandler (.NET 8+) carry independent static remap tables - clear both.
        JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
        Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler.DefaultInboundClaimTypeMap.Clear();

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

                // Authorization-code flow with PKCE - the modern default for server-side RPs.
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
                // new Keycloak sub. Runs per login - cheap idempotent upsert after the
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
            })
            // Programmatic API: Authorization: Bearer sbk_xxx - only accepted for /api/v1/*
            // endpoints that explicitly opt in via [Authorize(AuthenticationSchemes = ...)].
            .AddScheme<ApiTokenOptions, ApiTokenAuthenticationHandler>(
                ApiTokenAuthenticationHandler.SchemeName, _ => { });

        services.AddScoped<PortalProvisioningService>();

        // Licensing: vendor key (singleton, lazy-loaded) + issuer (scoped per request).
        services.Configure<PortalLicenseOptions>(configuration.GetSection("Portal:License"));
        services.AddSingleton<VendorKeyProvider>();
        services.AddScoped<LicenseIssuer>();

        services.AddAuthorization(a =>
        {
            // Baseline: the user successfully completed the Keycloak OIDC flow. Per-org
            // authorization is done in each controller via the Member table - any user
            // who made it into our realm is a legitimate portal visitor; what they can
            // ACT on is gated by the memberships we find for their sub claim.
            a.AddPolicy(PortalAuthorizationPolicy, p => p.RequireAuthenticatedUser());
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