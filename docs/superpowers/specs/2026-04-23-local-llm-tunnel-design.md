# Local LLM Tunnel: Remote Stylobot Sites Using Self-Hosted GPUs

**Date:** 2026-04-23
**Status:** Proposed
**Scope:** Small CLI + API feature that lets a public Stylobot instance securely use a local Ollama runtime through Cloudflare anonymous tunnel mode

---

## Problem

Stylobot already supports local LLM providers, and the console already has Cloudflare tunnel support. What is missing is the easy "use my home/workstation GPUs from a hosted Stylobot site" path.

Current friction:

- Local GPU machines are often behind NAT or dynamic IPs.
- Exposing Ollama directly is unsafe.
- The remote site needs model discovery, liveness, request signing, and a revocable trust relationship.
- Anonymous tunnel URLs are convenient but must not become bearer secrets by themselves.

The desired operator workflow is:

```bash
stylobot llmtunnel
stylobot llmtunnel <cloudflare-tunnel-token>
```

The command starts a local agent in front of Ollama, opens an anonymous or named Cloudflare tunnel, and prints an encoded connection key. The user pastes that key into the paid Stylobot site UI, or adds it to FOSS configuration. This works even when the local GPU box can open an outbound tunnel but cannot reach the Stylobot site directly.

---

## Goals

- Make local GPU capacity easy to attach to a hosted/public Stylobot instance.
- Keep Ollama and similar local LLM APIs private; expose only a narrow Stylobot agent protocol.
- Support Cloudflare quick tunnels with no account setup, and named/permanent Cloudflare tunnels when the user passes a tunnel token.
- Let the local agent advertise available models, health, load, and streaming support.
- Authenticate the remote Stylobot controller and every inference request.
- Keep optional end-to-end payload encryption available, while making TLS plus mutual auth and per-request signing sufficient for the first slice.
- Keep the first implementation small enough to fit the console/API architecture.

## Non-Goals

- No multi-tenant GPU marketplace.
- No general-purpose reverse proxy to local services.
- No full browser management UI is required for the first slice beyond importing a connection key and listing active nodes.
- No direct exposure of raw Ollama endpoints.
- No arbitrary HTTP forwarding, shell, filesystem, or general remote execution.
- No guarantee that anonymous tunnel URLs are stable across restarts.

---

## User Experience

### Local GPU Owner: Anonymous Tunnel

```bash
stylobot llmtunnel
```

Output:

```text
Tunnel: active
Node: gpu-office-01
Advertised models:
 - llama3.2:3b
 - qwen2.5:14b

Connection key:
sb_llmtunnel_v1_eyJ0dW5uZWwiOiJodHRwczovL21ldGFsLWdwdS1zdGFjay50cnljbG91ZGZsYXJlLmNvbSIs...

Paste this key into the Stylobot paid UI, or add it to FOSS config:
BotDetection:AiDetection:LocalTunnel:ConnectionKey
```

### Local GPU Owner: Named Cloudflare Tunnel

```bash
stylobot llmtunnel eyJhIjoiNjQ2...
```

This reuses the existing `--tunnel <token>` behavior but starts the LLM agent instead of the bot-defense proxy. A named Cloudflare tunnel is the production path when the operator wants a stable hostname and a node that can survive restarts without changing the Stylobot connection key.

The connection key should mark the tunnel as permanent/named when a Cloudflare token is supplied:

```json
{
  "tunnelKind": "cloudflare-named",
  "tunnelUrl": "https://gpu-office.example.com",
  "requiresStableTunnel": true
}
```

For quick anonymous tunnels, the key should mark the node as ephemeral:

```json
{
  "tunnelKind": "cloudflare-quick",
  "tunnelUrl": "https://metal-gpu-stack.trycloudflare.com",
  "requiresStableTunnel": false
}
```

### Import Into Stylobot

The paid product should expose:

```text
Settings -> LLM -> Add local tunnel node -> paste connection key
```

FOSS should support configuration:

```json
{
  "BotDetection": {
    "AiDetection": {
      "Provider": "LocalTunnel",
      "LocalTunnel": {
        "ConnectionKey": "sb_llmtunnel_v1_..."
      }
    }
  }
}
```

### Provider and Limits

Defaults should work for Ollama:

```bash
stylobot llmtunnel
```

Optional v1 flags:

```bash
stylobot llmtunnel --ollama http://127.0.0.1:11434
stylobot llmtunnel --models llama3.2:3b,qwen2.5:14b
stylobot llmtunnel --max-concurrency 2 --max-context 8192
```

The command should prefer the subcommand form `stylobot llmtunnel`. A compatibility alias for `-llmtunnel` is acceptable while the feature is new.

---

## Architecture

```
Public Stylobot site
  -> LocalLlmTunnelClient provider
  -> HTTPS request to Cloudflare tunnel URL
  -> local stylobot LLM agent endpoint
  -> allowed local backend adapter
  -> Ollama on loopback
```

The local agent is a small Kestrel app started by the console command. It is not the normal reverse proxy. It maps only the local LLM tunnel API, refuses all other paths, and never exposes raw Ollama.

### Components

| Component | Project | Responsibility |
|-----------|---------|----------------|
| `llmtunnel` command parser | `Mostlylucid.BotDetection.Console` | Starts local agent, starts `cloudflared`, prints connection key |
| `LocalLlmAgentServer` | new package or console services | Minimal Kestrel host for tunnel endpoints |
| `LocalLlmProviderProbe` | new LLM tunnel package | Lists Ollama models and provider health |
| `LocalLlmTunnelEndpoints` | new package | `/api/v1/llm-tunnel/*` agent endpoints |
| `LocalLlmTunnelClientProvider` | new package | Remote-site `ILlmProvider` that calls the agent |
| Controller endpoints | `Mostlylucid.BotDetection.Api` or dashboard API | Import/list/revoke/test local tunnel nodes |

The cleanest implementation is a new package:

```text
Mostlylucid.BotDetection.Llm.Tunnel/
  LocalLlmTunnelOptions.cs
  LocalLlmTunnelCrypto.cs
  LocalLlmTunnelModels.cs
  LocalLlmTunnelClientProvider.cs
  LocalLlmProviderProbe.cs
  LocalLlmRelayEndpoints.cs
  Extensions/LocalLlmTunnelServiceExtensions.cs
```

The console can reference this package and host the agent. Public Stylobot sites can reference it to consume remote local nodes as an `ILlmProvider`.

---

## Agent API

All endpoints live under:

```text
/api/v1/llm-tunnel
```

### `GET /health`

Returns agent version, clock, provider readiness, load, and key id.

```json
{
  "status": "ready",
  "nodeId": "llmn_01hw...",
  "provider": "ollama",
  "version": "1",
  "keyId": "k_01hw...",
  "queueDepth": 0,
  "maxConcurrency": 2,
  "startedAt": "2026-04-23T12:00:00Z"
}
```

### `GET /models`

Returns sanitized model inventory. For v1, probe Ollama `GET /api/tags`.

```json
{
  "provider": "ollama",
  "models": [
    {
      "id": "llama3.1:8b",
      "family": "llama",
      "parameterSize": "8b",
      "quantization": "q4_K_M",
      "contextLength": 8192,
      "allowed": true,
      "supportsStreaming": true
    }
  ]
}
```

### `POST /complete`

Accepts a structured, signed inference request. If payload encryption is enabled for the node session, the signed request carries an encrypted payload and the response is encrypted as well.

Request body:

```json
{
  "requestId": "llmreq_01hw...",
  "tenantId": "tenant_01hw...",
  "nodeId": "llmn_01hw...",
  "keyId": "k_01hw...",
  "nonce": "base64url",
  "issuedAt": "2026-04-23T12:00:00Z",
  "expiresAt": "2026-04-23T12:00:30Z",
  "payload": {
    "model": "llama3.2:3b",
    "messages": [{ "role": "user", "content": "classify this request..." }],
    "temperature": 0.1,
    "maxTokens": 512,
    "stream": false,
    "timeoutMs": 5000
  },
  "signature": "base64url"
}
```

Response:

```json
{
  "requestId": "llmreq_01hw...",
  "model": "llama3.2:3b",
  "content": "...",
  "usage": {
    "promptTokens": 180,
    "completionTokens": 64
  },
  "latencyMs": 420,
  "signature": "base64url"
}
```

The agent converts this to the existing `LlmRequest` abstraction. It must reject unadvertised models, over-limit contexts, over-limit concurrency, expired requests, bad signatures, nonce replay, and direct raw Ollama-style request bodies.

### `POST /stream`

Streams inference chunks for interactive use. This endpoint can be postponed if the first implementation only needs classification-style completions, but the protocol should reserve it because local GPU nodes are useful beyond single JSON completions.

---

## Connection Key and Import

The agent setup flow is intentionally site-offline. The GPU box only needs outbound access to Cloudflare. It does not need to reach the Stylobot site.

### Connection Key

The local command starts the agent and Cloudflare tunnel, then prints an encoded connection key:

```text
sb_llmtunnel_v1_<base64url-json>
```

Payload:

```json
{
  "version": 1,
  "nodeId": "llmn_01hw...",
  "nodeName": "gpu-office-01",
  "tunnelKind": "cloudflare-quick",
  "tunnelUrl": "https://metal-gpu-stack.trycloudflare.com",
  "agentPublicKey": "base64url",
  "controllerSharedSecret": "base64url",
  "keyId": "k_01hw...",
  "provider": "ollama",
  "models": ["llama3.2:3b", "qwen2.5:14b"],
  "supportsStreaming": true,
  "maxConcurrency": 2,
  "maxContext": 8192,
  "createdAt": "2026-04-23T12:00:00Z",
  "expiresAt": null
}
```

The key is sensitive because it lets a Stylobot controller authenticate to this local agent. It should be displayed once by default and treated like an API key. For named Cloudflare tunnels it can be persisted in site config because the tunnel URL is stable. For quick anonymous tunnels it is ephemeral and should be replaced when the local process restarts with a new URL.

### Paid Site Import

The paid product imports the key through the UI:

```http
POST /api/v1/llm-nodes/import
Authorization: Bearer <site-admin-session>
```

Body:

```json
{
  "connectionKey": "sb_llmtunnel_v1_..."
}
```

Response:

```json
{
  "imported": true,
  "nodeId": "llmn_01hw...",
  "name": "gpu-office-01",
  "models": ["llama3.2:3b", "qwen2.5:14b"],
  "tunnelKind": "cloudflare-quick"
}
```

The site stores the node identity, tunnel URL, request-signing secret, model inventory, capabilities, and audit metadata.

### FOSS Config Import

FOSS deployments can import the same key through config:

```json
{
  "BotDetection": {
    "AiDetection": {
      "Provider": "LocalTunnel",
      "LocalTunnel": {
        "ConnectionKey": "sb_llmtunnel_v1_..."
      }
    }
  }
}
```

For multiple nodes:

```json
{
  "BotDetection": {
    "AiDetection": {
      "LocalTunnel": {
        "ConnectionKeys": [
          "sb_llmtunnel_v1_...",
          "sb_llmtunnel_v1_..."
        ]
      }
    }
  }
}
```

---

## Authentication and Encryption

Anonymous Cloudflare tunnel URLs are public. Security must come from the imported connection key and request protocol.

### V1 Security Requirements

- Outbound-only tunnel from the GPU box.
- Mutual authentication between controller and agent.
- Connection key generated by the agent after the tunnel URL is known.
- Persistent node identity encoded in the connection key.
- Per-request signature with short expiry and nonce replay protection.
- Backend allowlist, defaulting to `http://127.0.0.1:11434`.
- Model allowlist from `--models` or controller policy.
- Rate limits and concurrency caps.
- Tenant/site binding.
- Audit record for every inference request.

The agent rejects:
  - unknown node id,
  - unknown key id,
  - bad signature,
  - expired request,
  - reused nonce,
  - disallowed model,
  - disallowed backend,
  - requests exceeding configured context/concurrency limits,
  - direct raw Ollama-style request bodies.

### Optional Payload Encryption

For v1, TLS plus request signing is enough to avoid exposing Ollama and to prevent stolen tunnel URLs from being useful. Payload encryption can be enabled when stronger privacy is needed:

- agent creates an ephemeral ECDH key pair on startup,
- controller has an ECDH key pair,
- connection-key import exchanges public keys,
- both sides derive a session key with ECDH + HKDF-SHA256,
- request and response payloads are wrapped with AES-GCM.

.NET can implement this without third-party dependencies using `ECDiffieHellman`, `HKDF`, and `AesGcm`.

---

## Remote Site Behavior

The public Stylobot site registers `LocalLlmTunnelClientProvider` as an optional `ILlmProvider`.

Selection rules:

1. If no node is imported, keep current provider behavior.
2. If one node is imported and ready, use it for LLM escalation.
3. If multiple nodes are imported, choose by model preference, health, and queue depth.
4. If the node fails or times out, return empty content just like current providers do, so detection can fall back safely.

Configuration sketch:

```json
{
  "BotDetection": {
    "AiDetection": {
      "Provider": "LocalTunnel",
      "LocalTunnel": {
        "ConnectionKey": "sb_llmtunnel_v1_...",
        "DefaultModel": "llama3.2:3b",
        "TimeoutMs": 5000,
        "MaxConcurrentRequests": 2
      }
    }
  }
}
```

Runtime registry entry:

```json
{
  "nodeId": "llmn_01hw...",
  "name": "studio-4090",
  "tunnelUrl": "https://metal-gpu-stack.trycloudflare.com",
  "provider": "ollama",
  "models": ["llama3.2:3b"],
  "enabled": true,
  "lastSeenAt": "2026-04-23T12:15:00Z",
  "queueDepth": 0,
  "failureCount": 0
}
```

---

## Local Agent Behavior

The agent listens on a random loopback port by default:

```text
http://127.0.0.1:<ephemeral>
```

Then `cloudflared tunnel --url http://127.0.0.1:<ephemeral>` creates the public URL. This avoids exposing the operator's normal Stylobot proxy port.

The agent process:

1. Detect Ollama from `--ollama` and local defaults.
2. Probe model inventory.
3. Apply `--models`, `--max-concurrency`, and `--max-context` limits.
4. Generate node id, key id, agent public key, and request-signing secret.
5. Start Kestrel on loopback only.
6. Launch `cloudflared` quick or named tunnel.
7. Extract tunnel URL from `cloudflared` stderr using the existing console logic.
8. Print `sb_llmtunnel_v1_...` connection key containing tunnel URL, tunnel kind, node metadata, model inventory, and controller-side secret material.
9. Keep running until Ctrl+C.

The agent should not bind to `0.0.0.0` unless explicitly requested for development.

---

## CLI Parsing

Current console parsing already routes subcommands before normal proxy startup. Add `llmtunnel` there and keep `-llmtunnel` / `--llm-tunnel` as compatibility aliases:

```csharp
if (firstArg == "llmtunnel" ||
    cmdArgs.Any(a => a.Equals("-llmtunnel", OrdinalIgnoreCase) ||
                    a.Equals("--llm-tunnel", OrdinalIgnoreCase)))
{
    return await LlmTunnelCommand.RunAsync(cmdArgs);
}
```

Arguments:

| Argument | Meaning |
|----------|---------|
| `llmtunnel` | Enter local agent mode |
| first non-option after command | optional named Cloudflare tunnel token |
| `--ollama <url>` | local Ollama URL, default `http://127.0.0.1:11434` |
| `--models <csv>` | advertised model allowlist |
| `--max-concurrency <n>` | local concurrency cap |
| `--max-context <tokens>` | maximum context accepted from controller |
| `--agent-port <port>` | optional loopback agent port |

Any non-option positional after `llmtunnel` is treated as a Cloudflare named tunnel token. Anonymous quick tunnel mode uses no positional arguments.

---

## Implementation Shape

### New Package

```text
Mostlylucid.BotDetection.Llm.Tunnel/
```

Responsibilities:

- shared DTOs,
- crypto envelope helper,
- model probing,
- agent endpoint mapping,
- remote `ILlmProvider`.

### Console Changes

`Mostlylucid.BotDetection.Console`:

- add `LlmTunnelCommand`,
- factor existing `LaunchCloudflaredTunnel` into reusable `CloudflaredTunnelLauncher`,
- add help text and man-page entry,
- add shutdown handling for the agent and `cloudflared`.

### API/Site Changes

`Mostlylucid.BotDetection.Api`:

- add `MapLlmNodeControllerEndpoints()`,
- add connection-key import endpoint for paid UI/API,
- add `ILlmNodeRegistry`,
- add in-memory registry for OSS first slice,
- optional persistent registry later via dashboard/postgres packages.

Potential endpoints:

```text
POST   /api/v1/llm-nodes/import
GET    /api/v1/llm-nodes
POST   /api/v1/llm-nodes/{nodeId}/test
DELETE /api/v1/llm-nodes/{nodeId}
```

---

## Failure Modes

| Failure | Behavior |
|---------|----------|
| `cloudflared` missing | Print install instructions, exit non-zero |
| Ollama unavailable | Start fails unless `--allow-unready` is passed |
| No models found | Warn and allow connection-key generation only with explicit `--models` |
| Tunnel URL not discovered | Exit with clear error after timeout |
| Quick tunnel restarted | Print a new connection key; site/config must be updated |
| Named tunnel restarted | Reuse the same stable hostname; existing connection key can continue working if node secret is persisted |
| Import key leaked | Attacker can import/use that node until it is rotated; paid UI should support revoke/rotate |
| Inference timeout | Return empty content to remote provider; site falls back |
| Replay detected | Return 401 and log warning |

---

## Open Questions

- Should node registry live in the core API package or the dashboard storage layer?
- Should multiple nodes be load-balanced, prioritized, or model-routed in the first release?
- Should streaming use SSE, WebSocket, or chunked HTTP?
- Should payload encryption be on by default, or an opt-in mode after signed TLS transport ships?
- Should the agent persist node id/secret by default for named tunnels, and keep quick tunnels memory-only?

---

## MVP Recommendation

Build the smallest useful version:

1. Console `stylobot llmtunnel [cloudflare-token]` for Ollama only.
2. Loopback agent with `/health`, `/models`, `/complete`.
3. Cloudflare quick tunnel by default, named permanent tunnel when token is supplied.
4. Encoded `sb_llmtunnel_v1_...` connection key import into paid UI or FOSS config.
5. Per-request signing with nonce replay protection and strict model/backend allowlists.
6. Remote `ILlmProvider` that calls one imported node.

This gives self-hosters the high-value path without redesigning the LLM subsystem or exposing raw local services.
