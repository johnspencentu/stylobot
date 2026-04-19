# StyloBot

**Self-hosted bot defense. Free forever.** 31 detectors, sub-millisecond inference, 129-dimensional session vectors, intent classification, and reverse-proxy integration. One binary. No cloud dependency. Your traffic never leaves your perimeter.

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.botdetection)](https://www.nuget.org/packages/mostlylucid.botdetection)
[![License: Unlicense](https://img.shields.io/badge/license-Unlicense-blue.svg)](https://unlicense.org/)

## Install

```bash
# Homebrew (macOS + Linux)
brew install scottgal/stylobot/stylobot

# One-line install (Linux/macOS)
curl -fsSL https://raw.githubusercontent.com/scottgal/stylobot/main/scripts/install.sh | bash

# Windows
choco install stylobot

# Docker
docker run --rm -p 8080:8080 -e DEFAULT_UPSTREAM=http://host.docker.internal:3000 scottgal/stylobot-gateway:latest

# NuGet (ASP.NET Core middleware)
dotnet add package mostlylucid.botdetection
```

Then run it:

```bash
stylobot 5080 http://localhost:3000
# Live detection table with color-coded BOT/HUMAN verdicts

stylobot 8000 http://192.168.0.6:2040 --mode production --policy block
# Production mode with blocking enabled

# Run as background daemon
stylobot start 5080 http://localhost:3000 --policy block
stylobot status     # Check health
stylobot logs       # View recent logs
stylobot stop       # Graceful shutdown

# With TLS
stylobot 443 https://api.example.com --cert cert.pfx

# Cloudflare Tunnel (instant public URL, no port forwarding)
stylobot 5080 http://localhost:3000 --tunnel
```

### Cloudflare Tunnel

The `--tunnel` flag creates an instant public URL via Cloudflare. Requires `cloudflared`:

```bash
# macOS
brew install cloudflared

# Linux (Debian/Ubuntu)
curl -L https://pkg.cloudflare.com/cloudflare-main.gpg | sudo tee /usr/share/keyrings/cloudflare-archive-keyring.gpg
echo "deb [signed-by=/usr/share/keyrings/cloudflare-archive-keyring.gpg] https://pkg.cloudflare.com/cloudflared $(lsb_release -cs) main" | sudo tee /etc/apt/sources.list.d/cloudflared.list
sudo apt update && sudo apt install cloudflared

# Windows
winget install Cloudflare.cloudflared
```

Then: `stylobot 5080 http://localhost:3000 --tunnel` for a quick tunnel, or `--tunnel <token>` for a pre-configured named tunnel.

### As systemd service (Linux)

```bash
sudo cp scripts/stylobot.service /etc/systemd/system/
sudo systemctl enable --now stylobot
```

### As Homebrew service (macOS)

```bash
brew services start stylobot
```

Or embed as middleware:

```csharp
builder.Services.AddBotDetection();
app.UseBotDetection();
```

## Pricing

**$100/month per domain. Unlimited requests.** No per-request metering. No CDN lock-in.

| | **FOSS** | **Startup** | **Enterprise** |
|---|---|---|---|
| **Price** | Free forever | **$100/mo** per domain | From $1,000/mo |
| **Detectors** | All 31 | All 31 | All 31 |
| **Dashboard** | Read-only (unlimited logins) | Live config editor | Fleet dashboard |
| **Persistence** | SQLite (zero-dependency) | PostgreSQL + pgvector | PostgreSQL + pgvector |
| **Session similarity** | Local (in-memory) | pgvector HNSW | pgvector HNSW |
| **Protected identities** | - | 5 per-user policy overrides | Unlimited |
| **Endpoint policies** | Unlimited (via YAML) | 5 live overrides | Unlimited |
| **LLM (optional)** | Any (OpenAI, Anthropic, Gemini, Groq, Ollama...) | Any + budget controls | Any + orchestration + live dashboard |
| **SSO** | Local accounts | OIDC | OIDC/SAML |
| **Backplane** | - | - | Redis (multi-node coordination) |
| **Add-ons** | - | Multi-node +$100/mo | Included |

[Start a 30-day trial](https://stylobot.net/portal) - no credit card. After trial, commercial features switch to logging-only mode. Detection never stops.

## What's in v5.6

- **Hardened proof-of-work challenge** - SHA-256 micro-puzzles with Web Worker parallelism, blackboard-driven difficulty scaling, transport-aware (API clients get JSON, not HTML)
- **Fingerprint approval** - behavioral contract with locked dimensions (country, UA, IP CIDR). Stolen credentials from a different environment are useless
- **Per-transition timing** - 3 new vector dimensions detect impossible navigation speed (PageView→ApiCall in 2ms = bot)
- **Deterministic bot naming** - "Rapid Scraper", "Headless Python Bot" instead of "Unknown"
- **Behavioral shape radar** - 129-dim vector projected into 8-axis radar chart with session stepping
- **World threat map + traffic chart** - jsVectorMap + ApexCharts in the dashboard
- **SQLite everywhere** - dashboard events, labels, challenges, approvals all persist to SQLite. Zero PostgreSQL dependency in FOSS
- **14 security fixes** - 3 critical, 4 high, 4 medium from pre-RTM audit

See [`CHANGELOG.md`](CHANGELOG.md) for full details.

## Detection Surface - 31 Detectors

| Wave | Detectors | Latency |
|------|-----------|---------|
| **Wave 0 - Fast Path** | UserAgent, Header, IP, SecurityTool, TransportProtocol, VersionAge, AiScraper, FastPathReputation, ReputationBias, VerifiedBot | <1ms |
| **Wave 1 - Behavioral** | Behavioral, AdvancedBehavioral, BehavioralWaveform, CacheBehavior, ClientSide, GeoChange, ResponseBehavior, StreamAbuse, SessionVector, FingerprintApproval, ChallengeVerification | 1-5ms |
| **Wave 2 - Fingerprinting** | TLS (JA3/JA4), TCP/IP (p0f), HTTP/2 (AKAMAI), HTTP/3 (QUIC), MultiLayerCorrelation | <1ms |
| **Wave 3 - AI + Learning** | Heuristic, HeuristicLate, Similarity, Cluster (Leiden), Intent, LLM (optional) | 1-500ms |

### Key capabilities

- **Real-time inference** - <1ms fast path, inline in the request pipeline, no cloud round-trips
- **129-dim session vectors** - Markov chain transitions + timing + fingerprints in one vector space. Catches bots even when they rotate IPs, UAs, and TLS fingerprints
- **Intent classification** - threat scoring orthogonal to bot probability. A human probing `.env` files gets low bot probability but high threat score
- **Protocol fingerprinting** - JA3/JA4 TLS, p0f TCP/IP, AKAMAI HTTP/2, QUIC HTTP/3
- **LLM-enhanced (optional)** - enriches decisions with bot names and cluster descriptions. Never makes detection decisions. Works fully without any LLM
- **Zero PII** - all persistence uses HMAC-SHA256 signatures. No raw IPs or user agents stored

## LLM Providers

Any provider, any tier. Bring your own API key. Detection works fully without LLM - it only enriches bot names and intent classification.

```bash
stylobot 5080 http://localhost:3000 --llm openai --llm-key sk-...
stylobot 5080 http://localhost:3000 --llm groq --llm-key gsk-...
stylobot 5080 http://localhost:3000 --llm gemini --llm-key AIza...
stylobot 5080 http://localhost:3000 --llm ollama                    # local, free
```

| Provider | Default model | Cost | Notes |
|----------|---------------|------|-------|
| `openai` | gpt-4o-mini | ~$0.15/1M tokens | Best value |
| `anthropic` | claude-haiku-4-5 | ~$0.25/1M tokens | Best reasoning |
| `gemini` | gemini-2.0-flash | Free tier | Google |
| `groq` | llama-3.3-70b | Free tier | Fastest inference |
| `mistral` | mistral-small | ~$0.10/1M tokens | EU-hosted |
| `deepseek` | deepseek-chat | ~$0.07/1M tokens | Cheapest |
| `ollama` | qwen3:0.6b | Free | Local, GPU-accelerated |
| `llamasharp` | qwen2.5:0.5b | Free | In-process CPU, air-gapped |
| `azure` | (deployment) | Azure pricing | Enterprise Azure |

Advanced: fallback chains, budget controls, and per-use-case routing via `appsettings.json`. See [configuration guide](Mostlylucid.BotDetection/docs/configuration.md).

## Dashboard

Real-time monitoring via SignalR. All data persists to SQLite (FOSS) or PostgreSQL (commercial).

- **Overview** - top threats above the fold, traffic chart, world threat map
- **Sessions** - Markov chain timeline with behavioral radar chart and session stepping
- **Visitors / Endpoints / Countries / Clusters / User Agents** - drill-in views
- **Configuration** - Monaco YAML editor (read-only in FOSS, live-edit in commercial)
- **Approval form** - approve fingerprints with locked dimensions directly from dashboard

## Repo layout

```
Mostlylucid.BotDetection/          # Core detection library (NuGet)
Mostlylucid.BotDetection.UI/       # Dashboard + SignalR hub (NuGet)
Mostlylucid.BotDetection.Demo/     # Interactive demo app
Stylobot.Gateway/                   # Docker YARP reverse proxy
Mostlylucid.BotDetection.Console/  # Standalone AOT binary (6 platforms)
Mostlylucid.Common/                # Shared utilities (NuGet)
Mostlylucid.GeoDetection/          # Geographic routing (NuGet)
bot-signatures/                     # BDF test data (81 scenarios)
docs/                               # Architecture, operations, guides
scripts/                            # k6 load tests, install script, compose files
```

Website and customer portal are in the [`stylobot-commercial`](https://github.com/scottgal/stylobot-commercial) repo (private).

## Distribution

| Channel | Status | Command |
|---------|--------|---------|
| **NuGet** | Published (v5.6.x) | `dotnet add package mostlylucid.botdetection` |
| **Docker Hub** | Published | `docker pull scottgal/stylobot-gateway:latest` |
| **GitHub Release** | Published (6 platforms) | [Releases](https://github.com/scottgal/stylobot/releases) |
| **Homebrew** | Available | `brew install scottgal/stylobot/stylobot` |
| **curl installer** | Available | `curl -fsSL .../install.sh \| bash` |
| **Cosign signatures** | Signed | Docker images + release binaries signed via Sigstore |
| **Windows Authenticode** | Planned | Requires code signing certificate (~$120/yr) |
| **macOS notarization** | Planned | Requires Apple Developer Program ($99/yr) |
| **apt/snap** | Planned | Debian/Ubuntu package repository |
| **winget** | Planned | Windows Package Manager |

## Requirements

- No external dependencies for FOSS (SQLite is embedded)
- .NET SDK 10.0 (for building from source)
- Docker (optional, for containerized deployment)
- Commercial: PostgreSQL, optional Redis, optional Keycloak

## Documentation

- [`docs/README.md`](docs/README.md) - entry index
- [`docs/QUICKSTART.md`](docs/QUICKSTART.md) - local runbook
- [`docs/DOCKER_SETUP.md`](docs/DOCKER_SETUP.md) - container deployment
- [`Mostlylucid.BotDetection/docs/`](Mostlylucid.BotDetection/docs/) - 20+ detector guides
- [`CHANGELOG.md`](CHANGELOG.md) - version history

## License

[The Unlicense](https://unlicense.org/) - FOSS core is public domain. Commercial tiers licensed via the [customer portal](https://stylobot.net/portal).
