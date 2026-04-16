# Portal Dev Setup — Running the Auth Stack Locally

This is the "I want to click through the portal" quickstart. Architecture details live in [`portal-architecture.md`](portal-architecture.md).

## One-time: start Keycloak + portal-db

From the repo root:

```bash
docker compose -f docker-compose.dev.yml up -d keycloak-db keycloak portal-db
```

Wait until Keycloak is ready — on first boot it imports `keycloak/realm-stylobot.json`, which takes ~30 seconds.

Check readiness:

```bash
curl -sI http://localhost:8081/realms/stylobot | head -1
# Expect: HTTP/1.1 200
```

## Dev login credentials

The realm import seeds one test user:

| Field | Value |
|---|---|
| Email / username | `devuser@stylobot.local` |
| Password | `devpass` |

Keycloak admin console: http://localhost:8081/admin/ (admin / admin — **dev only**).

## Run the site

```bash
dotnet run --project src/Stylobot.Website/Stylobot.Website.csproj
```

On startup, `ApplyPortalMigrationsAsync` runs the initial migration against `portal-db` (localhost:5433). You'll see the `organizations`, `members`, `licenses`, `license_audits`, and `api_tokens` tables appear.

## Click-through

1. Browse to http://localhost:5062 (HTTP) or https://localhost:7038 (HTTPS).
2. Navigate to http://localhost:5062/account/login.
3. Keycloak prompts for credentials — log in as `devuser@stylobot.local` / `devpass`.
4. Redirect back to `/portal`. On first login, a personal org is auto-provisioned and shown.
5. `/account/whoami` returns the OIDC claims for inspection.
6. `/account/logout` clears the cookie and redirects through Keycloak end-session.

## Resetting state

To wipe everything (Keycloak users, portal data, imports):

```bash
docker compose -f docker-compose.dev.yml down -v
docker compose -f docker-compose.dev.yml up -d keycloak-db keycloak portal-db
```

## Production checklist (when we get there)

- Move `Portal:Oidc:ClientSecret` out of appsettings into env/user-secrets/Vault.
- Set `Portal:Oidc:RequireHttpsMetadata=true`.
- Disable Keycloak `start-dev`, switch to `start` with proper TLS + DB credentials from secrets.
- Configure post-logout redirect URIs for the real domain.
- Enable MFA in realm (`CONFIGURE_TOTP` required-action already seeded; flip to required on realm).
- Point `DataProtection` keyring at Redis (`AddDataProtection().PersistKeysToStackExchangeRedis(...)`) so cookies survive replica rotation.
- Rate-limit `/account/login` and `/account/callback` with `AddRateLimiter()`.
