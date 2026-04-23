# Local LLM Tunnel Implementation Plan

**Goal:** Add `stylobot llmtunnel [cloudflare-tunnel-token]` so a public Stylobot site can securely use a local GPU-backed Ollama node through an anonymous quick tunnel or named permanent Cloudflare tunnel, imported via an encoded connection key.

**Spec:** `docs/superpowers/specs/2026-04-23-local-llm-tunnel-design.md`

**Tech Stack:** .NET 10, existing `ILlmProvider`, existing Ollama provider patterns, existing console `cloudflared` tunnel launch pattern, ASP.NET minimal APIs

---

## File Map

### New package: `Mostlylucid.BotDetection.Llm.Tunnel/`

| File | Responsibility |
|------|----------------|
| `Mostlylucid.BotDetection.Llm.Tunnel.csproj` | Package references core LLM abstractions |
| `LocalLlmTunnelOptions.cs` | Site and agent options |
| `LocalLlmTunnelModels.cs` | DTOs for connection-key import, node registry, model inventory, signed requests, optional encrypted envelopes |
| `LocalLlmTunnelCrypto.cs` | request signing, nonce replay cache, optional ECDH/HKDF/AES-GCM helper |
| `LocalLlmProviderProbe.cs` | Ollama model discovery |
| `LocalLlmAgentEndpoints.cs` | `/api/v1/llm-tunnel/*` local agent endpoints |
| `LocalLlmTunnelClientProvider.cs` | Remote-site `ILlmProvider` implementation |
| `Extensions/LocalLlmTunnelServiceExtensions.cs` | DI and endpoint registration |

### Console changes

| File | Change |
|------|--------|
| `Mostlylucid.BotDetection.Console/Program.cs` | Detect `llmtunnel` subcommand and compatibility aliases; help/man text |
| `Mostlylucid.BotDetection.Console/Services/LlmTunnelCommand.cs` | Parse local agent command, start host, launch tunnel, print connection key |
| `Mostlylucid.BotDetection.Console/Services/CloudflaredTunnelLauncher.cs` | Move existing `LaunchCloudflaredTunnel` logic out of `Program.cs` |

### API/site changes

| File | Change |
|------|--------|
| `Mostlylucid.BotDetection.Api/Endpoints/LlmNodeControllerEndpoints.cs` | Connection-key import plus list/test/revoke node endpoints |
| `Mostlylucid.BotDetection.Api/StyloBotApiExtensions.cs` | Map node controller endpoints |
| `Mostlylucid.BotDetection/Services/ILlmNodeRegistry.cs` | Registry abstraction if shared outside API |
| `Mostlylucid.BotDetection/Data/InMemoryLlmNodeRegistry.cs` | First-slice in-memory registry |

### Tests

| File | Responsibility |
|------|----------------|
| `Mostlylucid.BotDetection.Test/LlmTunnel/LocalLlmTunnelCryptoTests.cs` | Signing, bad signature, nonce replay, expiry, optional encryption round-trip |
| `Mostlylucid.BotDetection.Test/LlmTunnel/LocalLlmProviderProbeTests.cs` | Ollama model parsing |
| `Mostlylucid.BotDetection.Test/LlmTunnel/LocalLlmAgentEndpointsTests.cs` | Health/models/complete endpoint behavior |
| `Mostlylucid.BotDetection.Test/LlmTunnel/LocalLlmTunnelClientProviderTests.cs` | Remote provider request encryption and fallback |
| `Mostlylucid.BotDetection.Test/LlmTunnel/LlmNodeControllerEndpointsTests.cs` | Import/list/revoke/test controller API |

---

## Task 1: Create Tunnel Package Skeleton

- [ ] Create `Mostlylucid.BotDetection.Llm.Tunnel/`.
- [ ] Add csproj referencing `Mostlylucid.BotDetection.Llm` and core abstractions.
- [ ] Add project to `mostlylucid.stylobot.sln`.
- [ ] Add `LocalLlmTunnelOptions` with defaults:
  - agent base path `/api/v1/llm-tunnel`,
  - provider `ollama`,
  - local provider URL `http://127.0.0.1:11434`,
  - request timeout 5000 ms,
  - session TTL 24 hours,
  - max concurrent requests 2,
  - max context 8192 tokens.
- [ ] Build the new project.

## Task 2: Add Shared DTOs

- [ ] Add `LocalLlmTunnelModels.cs`.
- [ ] Include:
  - `LlmTunnelConnectionKey`,
  - `LlmNodeImportRequest`,
  - `LlmNodeImportResponse`,
  - `LlmNodeDescriptor`,
  - `LlmNodeModelInventory`,
  - `LlmNodeModelInfo`,
  - `LlmSignedInferenceRequest`,
  - `LlmSignedInferenceResponse`,
  - `LlmTunnelEnvelope`,
  - `LlmTunnelCompletionRequest`,
  - `LlmTunnelCompletionResponse`,
  - `LlmTunnelConnectionPayload`.
- [ ] Keep DTOs serializable with source-gen friendly simple properties.
- [ ] Add unit tests for JSON round trips.

## Task 3: Implement Signing, Replay Protection, and Optional Envelope Encryption

- [ ] Add `LocalLlmTunnelCrypto`.
- [ ] Sign canonical request bodies with the node/controller session key.
- [ ] Enforce:
  - timestamp expiry,
  - node id and key id binding,
  - nonce uniqueness using an in-memory replay cache.
- [ ] Add optional ECDH/HKDF/AES-GCM seal/open helpers for encrypted payload mode.
- [ ] Add tests for successful signing, wrong key, wrong node id, expired request, nonce replay, and optional encryption round-trip.

## Task 4: Probe Local LLM Providers

- [ ] Add `LocalLlmProviderProbe`.
- [ ] Implement Ollama model discovery via `GET /api/tags`.
- [ ] Return sanitized model metadata only.
- [ ] Apply `--models` allowlist and `--max-context` limit to advertised models.
- [ ] Add explicit "unready" result when the provider is unavailable.
- [ ] Add tests using fake HTTP handlers.

## Task 5: Map Local Agent Endpoints

- [ ] Add `LocalLlmAgentEndpoints.MapLocalLlmAgentEndpoints()`.
- [ ] Map:
  - `GET /api/v1/llm-tunnel/health`,
  - `GET /api/v1/llm-tunnel/models`,
  - `POST /api/v1/llm-tunnel/complete`.
- [ ] Include queue depth, max concurrency, max context, and streaming support in health/models.
- [ ] Refuse raw Ollama-compatible bodies; require valid signed inference request.
- [ ] Reject unadvertised models, expired requests, replayed nonces, and over-limit contexts.
- [ ] Convert verified request payload to existing `LlmRequest`.
- [ ] Return signed response; support optional encrypted response when negotiated.
- [ ] Add endpoint tests with a fake `ILlmProvider`.

## Task 6: Add Remote Tunnel Provider

- [ ] Add `LocalLlmTunnelClientProvider : ILlmProvider`.
- [ ] Load node descriptor/session from `ILlmNodeRegistry`.
- [ ] Sign `LlmRequest`, call agent `/complete`, verify signed response.
- [ ] Support optional encrypted payload mode if negotiated by imported node metadata.
- [ ] Return empty content on timeout/unavailable node, matching existing provider fallback style.
- [ ] Add model-selection hook but keep one-node routing for MVP.
- [ ] Add provider tests with a fake agent server.

## Task 7: Add Connection-Key Import, Node Registry, and Controller Endpoints

- [ ] Add `ILlmNodeRegistry`.
- [ ] Add in-memory registry implementation.
- [ ] Add controller endpoints:
  - `POST /api/v1/llm-nodes/import`,
  - `GET /api/v1/llm-nodes`,
  - `POST /api/v1/llm-nodes/{nodeId}/test`,
  - `DELETE /api/v1/llm-nodes/{nodeId}`.
- [ ] Require admin/controller authorization to import connection keys in paid/API mode.
- [ ] Decode and validate `sb_llmtunnel_v1_...` keys.
- [ ] Store node id, tunnel URL, tunnel kind, request-signing secret, model inventory, and limits.
- [ ] Support FOSS config import from `BotDetection:AiDetection:LocalTunnel:ConnectionKey` and `ConnectionKeys`.
- [ ] Add tests for auth, invalid key, import, replacement, revoke, and FOSS config parsing.

## Task 8: Factor Cloudflared Launcher

- [ ] Move `LaunchCloudflaredTunnel` logic out of `Program.cs`.
- [ ] Preserve current `--tunnel` behavior.
- [ ] Add a result object with process, discovered URL, and startup diagnostics.
- [ ] Add timeout while waiting for tunnel URL discovery.
- [ ] Keep log parsing compatible with quick and named tunnels.

## Task 9: Implement `llmtunnel` Console Command

- [ ] Detect `llmtunnel` in the existing subcommand switch.
- [ ] Keep `-llmtunnel` and `--llm-tunnel` as compatibility aliases before normal proxy argument parsing.
- [ ] Parse:
  - optional Cloudflare tunnel token,
  - optional `--ollama`,
  - optional `--models`,
  - optional `--max-concurrency`,
  - optional `--max-context`,
  - optional `--agent-port`.
- [ ] Start agent host on loopback.
- [ ] Probe local models before opening tunnel.
- [ ] Launch Cloudflare tunnel to the agent port.
- [ ] For anonymous quick tunnel mode, mark the generated key as `cloudflare-quick`.
- [ ] For tokened named tunnel mode, mark the generated key as `cloudflare-named` and preserve stable-tunnel metadata.
- [ ] Generate node id, key id, agent public key, and request-signing secret.
- [ ] Print tunnel status, node id/name, advertised models, and `sb_llmtunnel_v1_...` connection key.
- [ ] Keep process alive until Ctrl+C and cleanly stop `cloudflared`.

## Task 10: Documentation and Help

- [ ] Update console short help.
- [ ] Update man-page output.
- [ ] Update `Mostlylucid.BotDetection.Console/README.md`.
- [ ] Add security notes:
  - do not expose raw Ollama,
  - anonymous tunnel URL is not a credential,
  - connection keys are sensitive import credentials,
  - named Cloudflare tunnels are the permanent production path,
  - quick Cloudflare tunnels are ephemeral and require re-import when URL changes,
  - imported nodes can be revoked/rotated,
  - every request is signed and replay-protected,
  - optional payload encryption can be enabled when needed.

## Task 11: Verification

- [ ] `dotnet build mostlylucid.stylobot.sln --no-restore -v:minimal`
- [ ] `dotnet test Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --no-build`
- [ ] Manual smoke:
  - start Ollama locally,
  - run `stylobot llmtunnel`,
  - paste generated key into paid import endpoint or FOSS config,
  - verify node import,
  - invoke remote `ILlmProvider`,
  - kill agent and verify fallback.

---

## Suggested MVP Commit Slices

1. Add tunnel package skeleton and DTOs.
2. Add signing/replay protection and optional crypto envelope tests.
3. Add local model probing and agent endpoints.
4. Add remote `ILlmProvider`, connection-key import, and node registry.
5. Add console `llmtunnel` command and cloudflared launcher extraction.
6. Add docs, help text, and end-to-end tests.
