# Portal Architecture — Customer Licensing on stylobot.net

## Two auth domains — remember which you're in

**Domain 1 (this project) — stylobot.net (vendor-operated).**
We run Keycloak. It authenticates prospects, trial users, paying customers, our own staff. It issues ID tokens to the ASP.NET portal. The portal issues **license tokens** (separate Ed25519-signed JWTs) via a dedicated `LicenseIssuer` service whose key lives in a secrets vault.

**Domain 2 (the self-hosted product — NOT this project).**
Customers install the gateway, control plane, and dashboard in their infrastructure. Their dashboard authenticates *their* employees against *their* IdP (their Keycloak, Azure AD, Okta, Google Workspace, etc.) via OIDC. The control plane validates license tokens against the vendor public key baked into the release binary.

These two never share an identity store, a signing key, or a database. They communicate only via one signed artifact crossing the boundary: the license JWT the customer downloads from stylobot.net and pastes into their control plane config.

## Stack on stylobot.net

- **Keycloak** (official Docker image) — IdP. Stores users, passwords, MFA secrets, OAuth provider configs (Google/Microsoft/GitHub federated login). Realm: `stylobot`. Client: `stylobot-portal` (confidential).
- **ASP.NET Core 10 portal** — OIDC relying party via `Microsoft.AspNetCore.Authentication.OpenIdConnect`. Cookie session after login. No user/password storage of our own.
- **PostgreSQL** — `Portal` schema holds `Organizations`, `Members`, `Licenses`, `LicenseAudit`, `ApiTokens`. Does not duplicate Keycloak's user table; we key by Keycloak's `sub` claim.
- **Redis** — `DataProtection` keyring (for cookie encryption across replicas), distributed antiforgery, rate-limit counters for signup / login / trial-request endpoints.
- **Ed25519 vendor signing key** — held in HashiCorp Vault / AWS KMS / Azure Key Vault in production; user-secrets in dev. **Never on disk.** The `LicenseIssuer` service calls the vault to sign each token.
- **EF Core + Npgsql** — migrations for portal tables.

## Why Keycloak (and not ASP.NET Core Identity)

For a security product, the IdP needs enterprise-grade secret storage (Vault/KMS-integrated), native SAML/OIDC, built-in MFA + passkeys, federated login, and proper audit. Keycloak has all of that out of the box and is open source / self-hostable — which matches our "your data in your perimeter" positioning. ASP.NET Core Identity is fine for generic SaaS; for a company whose brand is "we're better at security than you," using the framework default would undersell us.

## Why Keycloak does NOT sign license tokens

See `stylobot-commercial/docs/licensing-tiers.md` for the full reasoning. Summary:
- **Trust domain boundary** — license tokens are verified by keys baked into customer-installed binaries. Those keys must be decoupled from our IdP's session keys so that Keycloak key rotations don't invalidate every deployed license.
- **Lifecycle mismatch** — Keycloak access tokens live ~5 min; license tokens live 30 days to 12 months.
- **Key custody** — license keys belong in a hardened vault (KMS/HSM) with tight rotation & audit. Keycloak's own keys live in its DB.
- **Separation of duties** — if Keycloak is breached, we lose auth (recoverable). If Keycloak also signed licenses, we'd lose every deployed license simultaneously.

So: Keycloak for **who are you**, `LicenseIssuer` for **what have you bought**. Two separate Ed25519 keypairs, two separate key custody stories.

## Data model

```
AspNetUsers (Keycloak, not ours)      Keycloak sub claim →  Member.KeycloakSub
                                                              │
Organization ◄──────────────  Member  ──────────────► [Role: Owner/Admin/Developer/Viewer]
    │
    ├── License (Id, Tier, Features[], Fingerprint?, IssuedAt, ExpiresAt, RevokedAt, TokenJti)
    │       └── LicenseAudit (Action: issue/revoke/rotate, ActorKeycloakSub, At, Reason)
    │
    └── ApiToken (Id, Name, TokenHash, CreatedBy, CreatedAt, LastUsedAt, RevokedAt, Scopes[])
```

`TokenJti` is the JWT `jti` claim so we can revoke in-flight licenses. `Fingerprint` is the optional hardware binding the customer supplies at trial request time.

## Routes

| Route | Authorize? | Purpose |
|---|---|---|
| `/` ... `/features`, `/detectors`, `/enterprise`, `/threats`, `/trust`, `/pricing` | public | Marketing (existing + Phase 2) |
| `/account/login` | redirect → Keycloak | OIDC challenge |
| `/account/logout` | signed-in | Sign out of cookie + Keycloak end-session |
| `/account/callback` | OIDC RP | Redirect URI from Keycloak |
| `/portal` | signed-in | Org switcher + dashboard |
| `/portal/org/{slug}` | org member | Org overview (licenses, active gateways, usage) |
| `/portal/org/{slug}/trial` | org owner/admin | Request 30-day SME trial |
| `/portal/org/{slug}/licenses` | org member | List / download / rotate / revoke |
| `/portal/org/{slug}/licenses/{id}/download` | org member | Download signed JWT |
| `/portal/org/{slug}/team` | org owner/admin | Invite members, change roles |
| `/portal/account/tokens` | signed-in | Personal API tokens for CI/CD |
| `/api/v1/licenses/current` | API token | Programmatic license retrieval |

All `/portal/*` and `/api/v1/*` are behind `[Authorize]`. `/account/login` redirects to Keycloak; successful callback upserts a `Member` record keyed by Keycloak `sub` if it's a first login.

## Why Postgres (not SQLite) here

The portal database is small (orgs, licenses, audit) — but it needs multi-replica horizontal scaling to survive a deployment restart without session loss. Sharing a Postgres with proper replication means every replica sees the same licenses and every `DataProtection` keyring entry decrypts the same cookies. SQLite would force single-writer + file-copy replication which doesn't scale to a customer-facing portal.

## License issuance flow

```
User clicks "Request Trial" in /portal/org/acme/trial
    │
    ▼
Portal.LicenseIssuer.IssueTrialAsync(org, fingerprint?, actor)
    │
    ├─ Check: no active trial for this org (1 lifetime per org)
    ├─ Call VaultClient.SignLicense(payload) — Ed25519 sign via KMS API
    ├─ Insert License row (Tier=sme, IsTrial=true, ExpiresAt=now+30d, TokenJti=...)
    ├─ Append LicenseAudit (action=issue, actor=keycloak_sub, reason=trial)
    └─ Return { token, expiresAt, downloadUrl }
    │
    ▼
UI shows the token, offers "Copy" and "Download .license" buttons.
Customer pastes into BotDetection:Commercial:LicenseToken env var.
```

Revocation:
1. Admin clicks "Revoke" on a license in `/portal/org/acme/licenses`.
2. Portal sets `RevokedAt=now`, appends audit.
3. Revoked JWT `jti` is added to a revocation feed. **Phase 2** work — for trials and paid licenses that don't phone home, revocation is only enforced via short TTL + renewal. For enterprise with phone-home enabled, control plane checks JWKS/revocation endpoint on heartbeat.

## Scale plan

| Concern | How we handle it |
|---|---|
| User growth | Postgres on `users.keycloak_sub` index; millions fit |
| Multi-region portal | Postgres streaming replicas per region, Redis DataProtection keyring shared |
| Cookie encryption across replicas | `services.AddDataProtection().PersistKeysToStackExchangeRedis(...)` |
| Antiforgery | cookie-based, cryptographic tokens — no server state |
| Signing key custody | Vault/KMS via client; key material never in app memory beyond the sign call |
| External OAuth providers | Added in Keycloak admin UI — no code change to the portal |
| SAML for enterprise orgs | Keycloak supports SAML identity-broker-style — one Keycloak realm federates multiple IdPs |
| Rate limiting | ASP.NET `AddRateLimiter()` on `/account/*` and `/api/v1/licenses/*` |
| Audit log | `LicenseAudit` table is append-only; export job for SOC 2 evidence |

## Phased delivery

- **1a (this commit):** Keycloak dev compose, OIDC wiring in Program.cs, PortalDbContext + entities, `/portal` landing page behind `[Authorize]`. License issuer stubbed — issues a placeholder token.
- **1b:** `LicenseIssuer` with real Ed25519 signing (user-secrets key in dev, Vault client in prod). Trial request flow. License list + download.
- **1c:** Team invites, API tokens, audit log UI, MFA enforcement via Keycloak required-actions.
- **1d:** Remove `Stylobot.Commercial.ControlPlane`'s self-signing trial endpoint. Replace `TrialBootstrapService` in the gateway plugin with a validator-only flow that reads the token from config.
