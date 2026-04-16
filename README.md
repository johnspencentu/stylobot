# StyloBot
by ***mostly*lucid**

> **Active development.** Real traffic is collected on [stylobot.net](https://www.stylobot.net) (zero PII - see the [API docs](Mostlylucid.BotDetection/docs/api-reference.md)) to tune detection. The live site may be temporarily rough as new detectors land - expect rough edges.

**Self-hosted bot defense with audit-grade decision evidence on every request.** 30+ detectors, wave-based orchestration, session-level Markov-chain behavioral compression, intent classification with threat scoring, real-time dashboard with session drill-in, and reverse-proxy integration - all in two lines of code. Runs in your VPC, your Kubernetes cluster, or as an embedded YARP gateway. Your traffic never leaves your perimeter.

<img src="https://raw.githubusercontent.com/scottgal/stylobot/refs/heads/main/mostlylucid.stylobot.website/src/Stylobot.Website/wwwroot/img/stylowall.svg?raw=true" alt="StyloBot" style="max-width:200px; height:auto;" />

## Two products, one codebase boundary

| Tier | Repo | What you get |
|---|---|---|
| **FOSS (this repo)** | `stylobot` - Unlicense | All detectors, session vectors, SQLite persistence, local dashboard, YAML-driven config, ASP.NET Core Identity local accounts, local Ollama LLM. **Fully standalone** - no external dependencies, no phone-home, no license required. |
| **Commercial** | [`stylobot-commercial`](https://github.com/scottgal/stylobot-commercial) | Adds Postgres + Redis persistence, central control plane, live config editor (per-endpoint + per-user + per-API-key overrides), multi-gateway fleet dashboard, pgvector HNSW session similarity, OIDC/SAML SSO, scheduled reports, Kubernetes operator. Tiers: Startup $149/mo, SME $499/mo, Enterprise custom. |

The commercial product plugs in via extension interfaces in FOSS - `IConfigurationOverrideSource` and `IFleetReporter` - that customers never see unless they install the commercial plugin package. **OSS shows all the levers in the dashboard; only commercial allows runtime edits.** FOSS is YAML-file-only by design.

Licenses are Ed25519-signed JWTs issued by the [stylobot.net customer portal](https://stylobot.net/portal). The customer's control plane validates tokens against the vendor public key baked into the release binary - no phone-home required. See [`stylobot-commercial/docs/licensing-tiers.md`](https://github.com/scottgal/stylobot-commercial/blob/main/docs/licensing-tiers.md).

## What's New

**v5.6 (in-flight):**
- **Commercial plugin architecture** - FOSS extension interfaces let commercial packages add per-target config overrides + fleet telemetry reporting without touching detection code
- **Keycloak-based customer portal** on stylobot.net - signup, org management, trial issuance (Ed25519-signed 30-day SME), license download/rotate/revoke, team invites, API tokens, audit log
- **Cluster coordination primitives** - Redis-backed backplane, shared reputation cache, cluster-wide work-unit meter for multi-YARP deployments
- **Pipeline coordination spec** - distributed-blackboard model so chained YARP instances (edge → regional → app-side) avoid redundant detector execution via input-hash-per-detector deduplication, while letting later hops contribute signals earlier hops couldn't see
- **Layered action policies** - each YARP in a chain applies its own response policy (monotone-escalating down the chain: `block` at an inner hop can't be softened by an outer hop's `allow`)

**v5.5:**
- **Session Vector Architecture** - per-request Markov chain transitions compressed into 118-dimensional normalized vectors (100 transitions + 10 stationary + 8 temporal + 8 fingerprint). Sessions are the primary behavioral unit with retrogressive boundary detection, inter-session velocity analysis, and snapshot compaction
- **Unified Fingerprint Dimensions** - TLS/TCP/H2 network fingerprints are vector dimensions in the same space as behavioral features; fingerprint mutation across sessions appears as velocity
- **SQLite Persistence** - sessions, signatures, and aggregated counters stored in SQLite (zero-dependency). PostgreSQL + pgvector is the commercial upgrade path for scale. ~100× compression vs per-request storage
- **Transport-Aware Detection** - 7 detectors now consume transport protocol context (API, SignalR, WebSocket, gRPC) to suppress false positives on non-document traffic
- **Oscillation Prevention** - configurable probability ceiling (0.90), state-aware reputation decay (12h for ConfirmedBad vs 3h), widened hysteresis gap
- **Session Dashboard** - Sessions tab with Markov chain previews, behavioral radar charts (ApexCharts), transition bar visualization, HTMX drill-in

See [`CHANGELOG.md`](CHANGELOG.md) for full details.

## Repo layout

**Detection engine:**
- `Mostlylucid.BotDetection` - core detection library, 30+ detectors, middleware, blackboard orchestration
- `Mostlylucid.BotDetection.Demo` - end-to-end demo app with test pages, API endpoints, live signatures

**UI & persistence:**
- `Mostlylucid.BotDetection.UI` - dashboard, tag helpers, view components, SignalR hub
- `Mostlylucid.BotDetection.UI.PostgreSQL` - PostgreSQL + TimescaleDB persistence

**Deployment:**
- `Stylobot.Gateway` - Docker-first YARP reverse proxy with built-in detection
- `mostlylucid.stylobot.website` - marketing site + customer portal (Keycloak OIDC RP + license issuer) + docs

**Geo (separate)**
- `Mostlylucid.GeoDetection`, `Mostlylucid.GeoDetection.Contributor` - country routing + bot detection signals from geo data

## Requirements

- .NET SDK `10.0`
- No external database required - SQLite is the default
- Docker + Docker Compose (optional, for containerized flows)
- Optional for advanced scenarios:
  - PostgreSQL + pgvector (commercial, for vector similarity at scale)
  - Redis (commercial, for multi-gateway coordination)
  - Keycloak (commercial portal auth - self-hostable)
  - Ollama (FOSS LLM provider) or OpenAI/Anthropic/Azure OpenAI (commercial)

## Quick Start - OSS, one gateway

```bash
dotnet run --project Mostlylucid.BotDetection.Demo
```

Open:
- `http://localhost:5080/bot-test`
- `http://localhost:5080/SignatureDemo`
- `http://localhost:5080/_stylobot` - live dashboard

## Quick Start - gateway container

```bash
docker run --rm -p 8080:8080 -e DEFAULT_UPSTREAM=http://host.docker.internal:3000 scottgal/stylobot-gateway:latest
curl http://localhost:8080/admin/health
```

If `ADMIN_SECRET` is configured, include header `X-Admin-Secret` for `/admin/*` endpoints.

## Quick Start - commercial trial

1. Sign up at [stylobot.net/portal](https://stylobot.net/portal) - one 30-day SME trial per organization, no credit card.
2. Download the signed license JWT, place in `BotDetection:Commercial:LicenseToken` env var.
3. `docker compose up` the commercial stack (see [`stylobot-commercial/docs/kubernetes-deployment.md`](https://github.com/scottgal/stylobot-commercial/blob/main/docs/kubernetes-deployment.md) for K8s).
4. Create a `StyloBotGateway` CR via `kubectl apply -f …` or point the gateway container at your control plane.

## Detection Surface - 30+ Detectors

All detectors run in a wave-based pipeline. Fast-path detectors execute in parallel in <1ms; advanced detectors fire only when triggered by upstream signals.

| Wave | Detectors | Latency |
|------|-----------|---------|
| **Wave 0 - Fast Path** | UserAgent, Header, IP, SecurityTool, TransportProtocol, VersionAge, AiScraper, FastPathReputation, ReputationBias, VerifiedBot | <1ms |
| **Wave 1 - Behavioral** | Behavioral, AdvancedBehavioral, BehavioralWaveform, CacheBehavior, ClientSide, GeoChange, ResponseBehavior, StreamAbuse, **SessionVector** | 1–5ms |
| **Wave 2 - Fingerprinting** | TLS (JA3/JA4), TCP/IP (p0f), HTTP/2 (AKAMAI), HTTP/3 (QUIC), MultiLayerCorrelation | <1ms |
| **Wave 3 - AI + Learning** | Heuristic, HeuristicLate, Similarity, Cluster (Leiden), Intent, TimescaleReputation, LLM (optional) | 1–500ms |
| **Slow Path** | ProjectHoneypot (DNS lookup) | ~100ms |

Active detector list is controlled by `BotDetection:Policies` in each app config. See [`Mostlylucid.BotDetection/docs/detector-weights-audit.md`](Mostlylucid.BotDetection/docs/detector-weights-audit.md) for the current weight/confidence baseline across all detectors.

### Key capabilities

- **Intent classification and threat scoring**: HNSW-backed similarity search classifies request intent (reconnaissance, exploitation, scraping, benign) and assigns a threat score orthogonal to bot probability - a human probing `.env` files gets low bot probability but high threat score
- **Protocol-level fingerprinting**: JA3/JA4 TLS, p0f TCP/IP, AKAMAI HTTP/2, QUIC HTTP/3 - detect bots even when they spoof headers perfectly
- **Stream-aware detection**: WebSocket, SSE, SignalR, and gRPC traffic classified early; downstream detectors suppress false positives; dedicated stream abuse detection catches connection churn, payload flooding, and protocol switching
- **Bot network discovery**: Leiden clustering finds coordinated bot campaigns across thousands of signatures
- **Session behavioral vectors**: Markov chain transitions compressed into 118-dim vectors with unified fingerprint dimensions - enables inter-session anomaly detection, behavioral clustering, and fingerprint mutation tracking
- **Adaptive AI**: Heuristic model extracts ~130 features per request (including transport context) and learns from feedback
- **Geo intelligence**: Country reputation tracking, geographic drift detection, VPN/proxy/Tor/datacenter identification
- **Verified bot authentication**: DNS-verified identification of Googlebot, Bingbot, and 30+ legitimate crawlers
- **AI scraper detection**: GPTBot, ClaudeBot, PerplexityBot, Google-Extended and Cloudflare AI signals
- **Zero PII**: All persistence uses HMAC-SHA256 hashed signatures - no raw IPs or user agents stored

### Training Data API

```bash
# JSONL streaming export with labels
curl http://localhost:5080/bot-detection/training/export > training-data.jsonl
curl http://localhost:5080/bot-detection/training/clusters
curl http://localhost:5080/bot-detection/training/countries
```

Register with `app.MapBotTrainingEndpoints()`. See [Training Data API docs](Mostlylucid.BotDetection/docs/training-data-api.md).

## Real-Time Dashboard

Built into `Mostlylucid.BotDetection.UI`, live monitoring via SignalR:

- **Sessions Tab** - timeline with Markov chain previews, behavioral radar charts, transition bar visualization, HTMX drill-in
- **Overview** - total/bot/human request counts, bot rate, unique signatures, top bots
- **World Map** - countries colored by bot rate with markers sized by traffic volume
- **Countries / Endpoints / User Agents / Clusters / Visitors** - drill-in views on each dimension
- **Threat Scoring** - independent threat bands (Low/Elevated/High/Critical) alongside bot probability

All data updates in real-time via SignalR. JSON API endpoints available for programmatic access (`/api/sessions`, `/api/detections`, `/api/clusters`, etc.).

```csharp
builder.Services.AddBotDetection();
builder.Services.AddStyloBotDashboard();
app.UseStyloBotDashboard();  // Dashboard at /_stylobot/
```

**OSS dashboard is read + static-YAML-config only.** Live runtime config editing - per-endpoint, per-user, per-API-key detector overrides - is a commercial feature gated on `stylobot.config-editor.live` (Startup+).

## Product differentiators

- **Self-hosted at every tier.** Even the $499/mo SME tier runs in the customer's VPC. Your traffic data never leaves your perimeter.
- **<1ms fast path** with explainable evidence per decision - detector contributions, confidence deltas, reason strings, optional response headers
- **Protocol-deep fingerprinting** - TLS/TCP/IP/HTTP/2/HTTP/3 fingerprints catch bots that spoof everything else
- **Temporal behavior resolution** - cross-request, session-vector correlation for stronger bot/human discrimination
- **Adaptive learning** - Heuristic weights evolve based on detection outcomes
- **Multi-YARP pipeline coordination** - signed decisions + input-hash-per-detector dedup across edge/regional/app-side YARPs. Detection runs once per unique-input-set; later hops contribute new signals earlier hops couldn't see. See [`stylobot-commercial/docs/pipeline-coordination-spec.md`](https://github.com/scottgal/stylobot-commercial/blob/main/docs/pipeline-coordination-spec.md).
- **Operator-first control** - composable action policies (block, throttle, challenge, honeypot, logonly), chain-aware monotone-escalating policy cascade
- **Powered by `mostlylucid.ephemeral` + `StyloFlow`** - efficient ephemeral state, signal coordination, licensing/metering without heavy per-request latency

## Common dev commands

```bash
dotnet build mostlylucid.stylobot.sln
dotnet run --project Mostlylucid.BotDetection.Demo
dotnet run --project Stylobot.Gateway
dotnet test mostlylucid.stylobot.sln
```

## Docker Compose stacks

- `docker-compose.demo.yml` - full stack (Caddy + gateway + website + DB)
- `mostlylucid.stylobot.website/docker-compose.local.yml` - production-like stack with Cloudflare tunnel
- `mostlylucid.stylobot.website/docker-compose.dev.yml` - dev stack incl. Keycloak + portal-db for the customer-portal auth flow

```bash
cp .env.example .env
docker compose up -d
```

## Documentation

Start here:
- [`docs/README.md`](docs/README.md) - entry index
- [`QUICKSTART.md`](QUICKSTART.md) - hands-on local runbook
- [`DOCKER_SETUP.md`](DOCKER_SETUP.md) - compose and deployment workflows

Library and component docs:
- [`Mostlylucid.BotDetection/README.md`](Mostlylucid.BotDetection/README.md)
- [`Mostlylucid.BotDetection/docs/`](Mostlylucid.BotDetection/docs/) - 20+ detector-specific docs
- [`Stylobot.Gateway/README.md`](Stylobot.Gateway/README.md)
- [`Mostlylucid.BotDetection.UI/README.md`](Mostlylucid.BotDetection.UI/README.md)
- [`Mostlylucid.BotDetection.UI.PostgreSQL/README.md`](Mostlylucid.BotDetection.UI.PostgreSQL/README.md)

Architecture:
- [`detector-weights-audit.md`](Mostlylucid.BotDetection/docs/detector-weights-audit.md) - baseline weight/confidence snapshot for every detector
- [`stylobot-commercial/docs/cluster-architecture.md`](https://github.com/scottgal/stylobot-commercial/blob/main/docs/cluster-architecture.md) - multi-gateway topology, state taxonomy, failure modes
- [`stylobot-commercial/docs/pipeline-coordination-spec.md`](https://github.com/scottgal/stylobot-commercial/blob/main/docs/pipeline-coordination-spec.md) - multi-YARP distributed-blackboard model
- [`stylobot-commercial/docs/licensing-tiers.md`](https://github.com/scottgal/stylobot-commercial/blob/main/docs/licensing-tiers.md) - tier structure, feature gating, competitive landscape

Release notes:
- [`CHANGELOG.md`](CHANGELOG.md)

## License

[The Unlicense](https://unlicense.org/) - FOSS core. Commercial tiers licensed separately via the customer portal.