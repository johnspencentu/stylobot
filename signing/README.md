# Windows Binary Signing

Windows executables (`stylobot.exe`) are Authenticode-signed using **Azure Artifact Signing** (`azure/artifact-signing-action`).
Certificates are ephemeral (3-day TTL, automatically renewed by the service).

Current certificate:
- Thumbprint: `2BFBA5A8A2B3732E2DDA048558F99B6FADABDAC8`
- Serial: `3300008161958040F9B80AF9AB000000008161`

## Verifying a release binary

```powershell
# PowerShell (Windows)
Get-AuthenticodeSignature .\stylobot.exe | Select-Object Status, SignerCertificate
# Status must be: Valid

# Or with signtool (requires Windows SDK)
signtool verify /pa /v stylobot.exe
```

The certificate chain roots to Microsoft's Azure Trusted Signing CA.

## GitHub Actions setup

The workflow (`publish-console-gateway.yml`) signs Windows binaries automatically on release.

### Authentication: GitHub OIDC federation (no client secret)

Authentication uses workload identity federation - GitHub generates a short-lived OIDC token
per workflow run that Azure trusts directly. No long-lived client secret is stored anywhere.

### Required GitHub secrets

| Secret | Description |
|--------|-------------|
| `AZURE_TENANT_ID` | Azure AD tenant ID |
| `AZURE_CLIENT_ID` | App registration (service principal) client ID |

### Signing config (hardcoded in workflow - not sensitive)

| Value | Set in workflow as |
|-------|--------------------|
| Endpoint: `https://weu.codesigning.azure.net/` | Inlined |
| Account: `scottgal` | Inlined |
| Profile: `mostlylucid` | Inlined |

### Service principal permissions

The app registration (`AZURE_CLIENT_ID`) needs:
1. **Code Signing Certificate Profile Signer** role on the certificate profile in Azure Trusted Signing.
2. A **federated credential** pointing to this repo:
   - Issuer: `https://token.actions.githubusercontent.com`
   - Subject: `repo:scottgal/stylobot:ref:refs/tags/*` (or `ref:refs/heads/main` for branch-triggered runs)
   - Audience: `api://AzureADTokenExchange`

### Finding your endpoint

Azure portal → Trusted Signing accounts → your account → Overview → Endpoint URI.
