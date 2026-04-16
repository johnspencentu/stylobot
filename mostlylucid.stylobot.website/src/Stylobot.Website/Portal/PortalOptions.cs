namespace Stylobot.Website.Portal;

/// <summary>
///     Bound from the <c>Portal</c> configuration section. Use user-secrets for the
///     OIDC client secret in development; environment variables in production.
/// </summary>
public sealed class PortalOptions
{
    /// <summary>
    ///     Master switch. When false, the /portal routes, OIDC wiring, and PortalDbContext
    ///     are not registered — useful for environments that only serve marketing content
    ///     (e.g., a CDN edge that never touches Keycloak).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Connection string to the portal's Postgres database.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Auto-run EF migrations on startup. Defaults to true in dev, false in prod.</summary>
    public bool AutoMigrate { get; set; } = true;

    public OidcOptions Oidc { get; set; } = new();

    public sealed class OidcOptions
    {
        /// <summary>Keycloak realm URL, e.g., <c>http://localhost:8081/realms/stylobot</c>.</summary>
        public string Authority { get; set; } = "http://localhost:8081/realms/stylobot";

        /// <summary>OIDC client id registered in Keycloak.</summary>
        public string ClientId { get; set; } = "stylobot-portal";

        /// <summary>
        ///     OIDC client secret. MUST come from user-secrets (dev) or environment variable
        ///     <c>Portal__Oidc__ClientSecret</c> (prod). Never committed to git.
        /// </summary>
        public string ClientSecret { get; set; } = string.Empty;

        /// <summary>
        ///     Require HTTPS on the authority URL. Set to false for local dev against HTTP
        ///     Keycloak. Production MUST be true.
        /// </summary>
        public bool RequireHttpsMetadata { get; set; } = true;

        /// <summary>Additional scopes beyond <c>openid profile email</c>.</summary>
        public List<string> ExtraScopes { get; set; } = new();
    }
}
