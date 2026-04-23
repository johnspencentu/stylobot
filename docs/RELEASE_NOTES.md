# StyloBot Release Notes

## v6.0.1-beta.0 — 2026-04-23

### New: Local LLM GPU Tunnel

Route LLM classification work from a cloud/VPS instance (no GPU) to a local machine with a GPU and Ollama, using a Cloudflare tunnel as the transport.

**New NuGet package:** `Mostlylucid.BotDetection.Llm.Tunnel`

**New console command:** `stylobot llmtunnel`

The tunnel agent probes your local Ollama instance, binds a loopback Kestrel server, starts a Cloudflare tunnel (anonymous quick tunnel or stable named tunnel), and prints a single `sb_llmtunnel_v1_<key>` connection string. Paste that key into the remote StyloBot config and the remote site will route all LLM inference through your GPU.

```bash
# On the local GPU machine
stylobot llmtunnel

# On the remote site
stylobot 5080 https://mysite.example.com --llm localtunnel --llm-key "sb_llmtunnel_v1_..."
```

**Security:** HMAC-SHA256 per-request signing, 30-second TTL nonces with 60-second replay protection window. All traffic flows through Cloudflare's encrypted tunnel; the agent only listens on loopback.

**Named vs anonymous tunnels:**
- Anonymous (quick): no Cloudflare account needed, URL changes on restart- must re-import key after each restart
- Named: requires a Cloudflare tunnel token, stable URL across restarts

**Dashboard status strip:** New "GPU Tunnels" widget shows active node count, per-node status badges with model list and queue depth.

**Configuration:**
```json
{
  "BotDetection": {
    "AiDetection": {
      "LocalTunnel": {
        "ConnectionKey": "sb_llmtunnel_v1_..."
      }
    }
  }
}
```

### Bug Fixes

- **HNSW graph deserialization:** Stale HNSW graph files (from older HNSW.Net versions) now deleted automatically on MessagePack deserialization failure- no more `FormatterNotRegisteredException` noise on startup after a library update.

- **AOT JSON serialization:** All JSON serialization in the tunnel package now uses source-generated contexts (`TunnelJsonContext`), removing any reflection-based JSON calls. The `stylobot` console binary publishes correctly as a NativeAOT single-file executable.

---

## v6.0.0-alpha- 2026-04-21

### Architecture

- **Local LLM Tunnel** package skeleton, crypto layer (HMAC-SHA256, nonce replay, optional AES-256-GCM envelope), agent endpoints, Cloudflare launcher, console command wiring
- **StatusStrip** dashboard widget for GPU tunnel node status
- **Content Sequence Detection**- divergence tracking per endpoint using Markov-chain centroid comparison; staleness signals from ETag/content-hash changes (`AssetHashStore`, `AssetHashMiddleware`)
- **Centroid Freshness**- endpoint staleness state in `CentroidSequenceStore`; `EndpointDivergenceTracker` rolling per-path divergence rate

### Public API & Node SDK

- Canonical REST API (`Mostlylucid.BotDetection.Api`) at `/api/v1/*`
- Node SDK: `@stylobot/core` (zero-dep types + client), `@stylobot/node` (Express middleware, Fastify plugin)
- API auth tiers: proxy headers (zero-latency), `X-SB-Api-Key` (detection + read), OIDC bearer (management)

---

## v5.6.0- 2026-04-17

### RTM Release

- 45 detectors across 4 waves, <1ms fast path, Leiden clustering
- Zero-PII design with HMAC-SHA256 signature hashing
- SQLite persistence (FOSS), PostgreSQL upgrade path (commercial)
- Dashboard: session timeline, Markov chain drill-in, behavioral radar charts, world threat map, Threats tab (CVE probes)
- Simulation packs (WordPress FOSS)
- Holodeck honeypot response system with beacon canary tracking
- PoW challenge system (SHA-256 micro-puzzles)
- Anonymous entity resolution (L0–L5 confidence levels)
- Session vectors: 129-dimensional Markov chain compression
- `UseStyloBot()` single-call setup